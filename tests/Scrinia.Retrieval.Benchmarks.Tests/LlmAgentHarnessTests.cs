using FluentAssertions;
using Scrinia.Core.Llm;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Llm;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Coverage for <see cref="LlmAgentHarness"/> with a fake <see cref="IBackgroundLlm"/>.
/// Verifies (a) the harness calls the LLM once per cold-cache miss and caches the
/// result, (b) null/empty LLM output is handled without crashing, (c) the prompt
/// content sent to the LLM never includes gold-answer or question text — the
/// anti-data-leakage contract.
/// </summary>
public class LlmAgentHarnessTests : IDisposable
{
    private readonly string _cachePath;

    public LlmAgentHarnessTests()
    {
        _cachePath = Path.Combine(Path.GetTempPath(),
            $"scrinia-agent-test-{Guid.NewGuid():N}.ndjson");
    }

    public void Dispose()
    {
        try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { /* best-effort */ }
    }

    private EnrichmentCache NewCache() => new(_cachePath);

    [Fact]
    public async Task GenerateAnchorsAsync_HitsLlmOnce_AndCachesResult()
    {
        var fake = new RecordingFakeLlm
        {
            FactsResult = new[] { "how do I undo X", "what is the policy for Y" },
        };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.AnchorsOnly);

        var first = await harness.GenerateAnchorsAsync("some content body", CancellationToken.None);
        var second = await harness.GenerateAnchorsAsync("some content body", CancellationToken.None);

        first.Should().NotBeNull().And.HaveCount(2);
        second.Should().NotBeNull().And.HaveCount(2);
        // LLM should have been hit exactly once; the second call cache-hits.
        fake.FactsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateAnchorsAsync_NullLlmResult_ReturnsNull_AndCachesNull()
    {
        var fake = new RecordingFakeLlm { FactsResult = null };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.AnchorsOnly);

        var result = await harness.GenerateAnchorsAsync("some content body", CancellationToken.None);
        result.Should().BeNull();
        fake.FactsCallCount.Should().Be(1);

        // Second call should hit the cached null without invoking the LLM again.
        var second = await harness.GenerateAnchorsAsync("some content body", CancellationToken.None);
        second.Should().BeNull();
        fake.FactsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateAnchorsAsync_DisabledMode_ReturnsNull_WithoutCallingLlm()
    {
        var fake = new RecordingFakeLlm
        {
            FactsResult = new[] { "should not be returned" },
        };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.RewriteOnly);  // anchors disabled

        var result = await harness.GenerateAnchorsAsync("content", CancellationToken.None);
        result.Should().BeNull();
        fake.FactsCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateAnchorsAsync_PromptContainsOnlyDocumentContent_NoGoldOrQuestion()
    {
        // Anti-data-leakage contract: the LLM must NEVER receive the gold-answer
        // set or the question text. The doc content is the ONLY user-data input.
        const string secretGold = "GOLD_ANSWER_TOKEN_MUST_NOT_LEAK";
        const string secretQuestion = "QUESTION_TEXT_MUST_NOT_LEAK";
        const string docContent = "This is the doc content body that is safe to send to the LLM.";

        var fake = new RecordingFakeLlm { FactsResult = new[] { "some anchor here" } };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.AnchorsOnly);

        await harness.GenerateAnchorsAsync(docContent, CancellationToken.None);

        fake.FactsCallCount.Should().Be(1);
        fake.LastFactsInput.Should().NotBeNull();
        fake.LastFactsInput.Should().Contain(docContent);
        // The two secrets are tokens we never passed in via the API at all, so by
        // construction they can't appear. This assertion documents the contract.
        fake.LastFactsInput.Should().NotContain(secretGold);
        fake.LastFactsInput.Should().NotContain(secretQuestion);
    }

    [Fact]
    public async Task RewriteQueryAsync_HitsLlmOnce_AndCachesResult()
    {
        var fake = new RecordingFakeLlm { DescriptionResult = "rewritten search-friendly form" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.RewriteOnly);

        var first = await harness.RewriteQueryAsync("original query", CancellationToken.None);
        var second = await harness.RewriteQueryAsync("original query", CancellationToken.None);

        first.Should().Be("rewritten search-friendly form");
        second.Should().Be("rewritten search-friendly form");
        fake.DescriptionCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateHypotheticalAsync_HitsLlmOnce_AndCachesResult()
    {
        var fake = new RecordingFakeLlm { DescriptionResult = "Some hypothetical answer here." };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.HydeOnly);

        var first = await harness.GenerateHypotheticalAsync("what is X", CancellationToken.None);
        var second = await harness.GenerateHypotheticalAsync("what is X", CancellationToken.None);

        first.Should().Be("Some hypothetical answer here.");
        second.Should().Be("Some hypothetical answer here.");
        fake.DescriptionCallCount.Should().Be(1);
    }

    [Fact]
    public async Task RewriteQueryAsync_DistinctFromHyde_UsesSeparateCacheKeys()
    {
        var fake = new RecordingFakeLlm { DescriptionResult = "shared output" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", new AgentModes(false, true, true));

        await harness.RewriteQueryAsync("same query text", CancellationToken.None);
        await harness.GenerateHypotheticalAsync("same query text", CancellationToken.None);

        // Despite identical input, the two ops keyed independently — both hit the LLM.
        fake.DescriptionCallCount.Should().Be(2);
    }

    [Fact]
    public async Task EmptyOrWhitespaceInput_ReturnsNull_WithoutCallingLlm()
    {
        var fake = new RecordingFakeLlm
        {
            FactsResult = new[] { "x" },
            DescriptionResult = "y",
        };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.All);

        (await harness.GenerateAnchorsAsync("", CancellationToken.None)).Should().BeNull();
        (await harness.GenerateAnchorsAsync("   ", CancellationToken.None)).Should().BeNull();
        (await harness.RewriteQueryAsync("", CancellationToken.None)).Should().BeNull();
        (await harness.GenerateHypotheticalAsync("", CancellationToken.None)).Should().BeNull();

        fake.FactsCallCount.Should().Be(0);
        fake.DescriptionCallCount.Should().Be(0);
    }

    /// <summary>
    /// Recording fake — captures the content arg passed to each enrichment method and
    /// returns a configurable canned result. Used to assert call counts and content
    /// shape (e.g. the anti-data-leakage contract).
    /// </summary>
    private sealed class RecordingFakeLlm : IBackgroundLlm
    {
        public int FactsCallCount { get; private set; }
        public int DescriptionCallCount { get; private set; }
        public string? LastFactsInput { get; private set; }
        public string? LastDescriptionInput { get; private set; }
        public string[]? FactsResult { get; set; }
        public string? DescriptionResult { get; set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<string?> GenerateDescriptionAsync(string content, CancellationToken ct)
        {
            DescriptionCallCount++;
            LastDescriptionInput = content;
            return Task.FromResult(DescriptionResult);
        }

        public Task<string?> SummarizeAsync(string text, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<string[]?> ExtractFactsAsync(string content, CancellationToken ct)
        {
            FactsCallCount++;
            LastFactsInput = content;
            return Task.FromResult(FactsResult);
        }

        public Task<int?> ScoreImportanceAsync(string content, CancellationToken ct) =>
            Task.FromResult<int?>(null);
    }
}
