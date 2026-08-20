using System.Text.Json;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// `scri hint --json` built its output by string interpolation, escaping the memory name with a
/// two-character escaper and interpolating the scope with no escaping at all — from the one
/// command whose whole purpose is to be consumed by another program.
///
/// <para><b>What is actually broken.</b> JsonEscape escapes `\` then `"`, which is the correct
/// order, so those two round-trip fine today — a test using only them passes before AND after
/// this change and proves nothing. The reachable hole is a CONTROL CHARACTER: JsonEscape passes
/// it through raw, and RFC 8259 forbids it inside a JSON string. (m.Scope is interpolated with no
/// escaping at all, but a scope is a path segment and cannot carry one.)</para>
///
/// <para><b>Reachability.</b> A path segment is sanitized (MemoryNaming.TrySanitizeSegment), but
/// the ENTRY NAME held in the index is not the path: a store write keeps the name verbatim and
/// derives a separate sanitized filename. `hint`'s matches read the index, so a newline in a name
/// reaches this emitter intact. Bundle import writes entry names straight from an external
/// bundle, which makes the route an untrusted one rather than only a theoretical one.</para>
///
/// <para>The hook envelope was never affected: Hint.BuildHookEnvelope uses Utf8JsonWriter.
/// Only --json hand-rolled.</para>
///
/// <para>In GlobalWorkspaceConfigCollection because this drives WorkspaceSetup.Configure (a
/// process-wide root) and redirects Console.Out. Without it another class's interleaved stdout
/// can break JsonDocument.Parse here — a false red that looks exactly like the defect.</para>
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class HintJsonOutputTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;
    private readonly TextWriter _originalOut;

    public HintJsonOutputTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_hint_json_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".scrinia", "store", "local"));
        _store = new FileMemoryStore(_root);
        _originalOut = Console.Out;
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Same seeding shape as HintCommandTests.AddMemory — one way to populate a store
    /// in this project, not two. The entry name is stored verbatim; ArtifactPath sanitizes the
    /// filename separately, which is exactly why an awkward name survives into the index.</summary>
    private void AddMemory(string name, string content)
    {
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow);
        _store.Upsert(entry, "local");
        string path = _store.ArtifactPath(name, "local");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public async Task HintJson_WithAControlCharacterInTheName_IsParseableJson()
    {
        // The newline is what the old escaper misses; the quote and backslash ride along so the
        // round-trip assertion still covers the two characters it did handle.
        const string awkwardName = "oauth\"quoted\\flow\nsecond-line";

        // BM25's IDF term collapses toward zero in a one-document corpus, so a single seeded
        // memory cannot clear the default floor no matter how well the prompt overlaps. Seed a
        // small corpus AND pin the floor through the product's own config API, so this test is
        // about the JSON emitter and not about the ranker's calibration.
        WorkspaceConfig.SetValue(_root, "Scrinia:Hint:MinScore", "0.1");
        AddMemory(awkwardName, "OAuth implementation notes for the quoted flow");
        AddMemory("session-store", "session storage and cookie lifetimes");
        AddMemory("db-indexes", "database index maintenance notes");

        var writer = new StringWriter();
        Console.SetOut(writer);
        int exit;
        try
        {
            var cmd = new ScriniaCommands();
            exit = await cmd.Hint(
                prompt: "OAuth implementation notes for the quoted flow",
                workspaceRoot: _root,
                json: true);
        }
        finally
        {
            Console.SetOut(_originalOut);
        }

        exit.Should().Be(0);
        string output = writer.ToString().Trim();
        output.Should().NotBeEmpty(because: "the seeded memory matches the prompt, so a hint is emitted");

        Action parse = () => JsonDocument.Parse(output);
        parse.Should().NotThrow(because: "a --json surface must emit JSON a consumer can parse");

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("matches")[0].GetProperty("name").GetString()
            .Should().Be(awkwardName, because: "the name must survive the round trip verbatim");
    }
}
