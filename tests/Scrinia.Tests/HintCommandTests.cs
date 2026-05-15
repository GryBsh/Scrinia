using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the <see cref="HintCommand"/> pre-send relevance hint. Covers threshold
/// filtering, hint-suppression on short prompts, and the JSON-envelope stdin parser
/// (each agent CLI delivers hook input slightly differently).
/// </summary>
public sealed class HintCommandTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public HintCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_hint_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".scrinia", "store", "local"));
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void AddMemory(string name, string content)
    {
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow, "desc");
        _store.Upsert(entry, "local");
        string path = _store.ArtifactPath(name, "local");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public void EmptyPromptReturns_NotEmitted()
    {
        var hint = new HintCommand(_store);
        var result = hint.Compute("", HintCommand.DefaultMinScore, HintCommand.DefaultMinPromptChars);

        result.Emitted.Should().BeFalse();
        result.Matches.Should().BeEmpty();
    }

    [Fact]
    public void ShortPromptBelowThreshold_Returns_NotEmitted()
    {
        AddMemory("oauth-flow", "OAuth implementation notes");
        var hint = new HintCommand(_store);

        var result = hint.Compute("hi", HintCommand.DefaultMinScore, minPromptChars: 8);

        result.Emitted.Should().BeFalse();
    }

    [Fact]
    public void MatchingPrompt_EmitsHintWithTopK()
    {
        // Need entries whose names match the query terms to clear the BM25 floor.
        AddMemory("oauth-flow", "OAuth token rotation");
        AddMemory("jwt-validation", "JWT signature validation");
        AddMemory("random-unrelated", "Notes on bicycles");

        var hint = new HintCommand(_store);
        var result = hint.Compute("oauth jwt", minScore: 0.1, minPromptChars: 4);

        result.Emitted.Should().BeTrue();
        result.Matches.Should().NotBeEmpty();
        result.Matches.Select(m => m.Name).Should().Contain(n => n == "oauth-flow" || n == "jwt-validation");
    }

    [Fact]
    public void BelowScoreThreshold_Returns_NotEmitted()
    {
        AddMemory("oauth-flow", "content here");
        var hint = new HintCommand(_store);

        // Very high min-score floor so even an exact name match doesn't clear it.
        var result = hint.Compute("oauth", minScore: 10_000.0, minPromptChars: 4);

        result.Emitted.Should().BeFalse();
    }

    [Fact]
    public void FormatPlain_IncludesAllMatchNames_AndPointsAtFirst()
    {
        var result = new HintResult(true,
        [
            new HintMatch("local", "oauth-flow", 50.0),
            new HintMatch("local", "jwt-validation", 30.0),
        ]);

        string formatted = HintCommand.FormatPlain(result);

        formatted.Should().Contain("[scrinia]");
        formatted.Should().Contain("2 memories match");
        formatted.Should().Contain("oauth-flow");
        formatted.Should().Contain("jwt-validation");
        formatted.Should().Contain("memory('search', 'oauth-flow')");
    }

    [Fact]
    public void FormatPlain_SingularWording_ForOneMatch()
    {
        var result = new HintResult(true, [new HintMatch("local", "only-one", 50.0)]);

        HintCommand.FormatPlain(result).Should().Contain("1 memory match");
    }

    [Fact]
    public void FormatPlain_NotEmitted_ReturnsEmpty()
    {
        HintCommand.FormatPlain(HintResult.Empty).Should().BeEmpty();
    }

    [Fact]
    public void ExtractPromptFromStdin_PlainText_PassesThrough()
    {
        ScriniaCommands.ExtractPromptFromStdin("just a plain prompt").Should().Be("just a plain prompt");
    }

    [Fact]
    public void ExtractPromptFromStdin_JsonEnvelope_ExtractsPromptKey()
    {
        string raw = """{"prompt": "user wants auth help", "session_id": "abc"}""";
        ScriniaCommands.ExtractPromptFromStdin(raw).Should().Be("user wants auth help");
    }

    [Fact]
    public void ExtractPromptFromStdin_JsonWithoutPromptKey_FallsBackToRaw()
    {
        string raw = """{"other_key": "value"}""";
        ScriniaCommands.ExtractPromptFromStdin(raw).Should().Be(raw);
    }

    [Fact]
    public void ExtractPromptFromStdin_BrokenJson_FallsBackToRaw()
    {
        string raw = "{ not actually json";
        ScriniaCommands.ExtractPromptFromStdin(raw).Should().Be(raw);
    }

    [Fact]
    public void ExtractPromptFromStdin_Empty_ReturnsEmpty()
    {
        ScriniaCommands.ExtractPromptFromStdin("").Should().Be("");
        ScriniaCommands.ExtractPromptFromStdin("   ").Should().Be("   ");
    }
}
