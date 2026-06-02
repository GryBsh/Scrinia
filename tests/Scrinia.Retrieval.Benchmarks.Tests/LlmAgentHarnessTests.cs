using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Llm;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Coverage for <see cref="LlmAgentHarness"/> with a fake <see cref="IBenchLlmClient"/>.
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

    private LlmResponseCache NewCache() => new(_cachePath);

    [Fact]
    public async Task GenerateAnchorsAsync_HitsLlmOnce_AndCachesResult()
    {
        var fake = new RecordingFakeLlm
        {
            // The LLM returns line-mode anchors text; AgentPrompts.ParseAnchors splits it.
            CompleteResult = "how do I undo X\nwhat is the policy for Y",
        };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.AnchorsOnly);

        var first = await harness.GenerateAnchorsAsync("some content body", CancellationToken.None);
        var second = await harness.GenerateAnchorsAsync("some content body", CancellationToken.None);

        first.Should().NotBeNull().And.HaveCount(2);
        second.Should().NotBeNull().And.HaveCount(2);
        // LLM should have been hit exactly once; the second call cache-hits.
        fake.CompleteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateAnchorsAsync_NullLlmResult_ReturnsNull_AndCachesNull()
    {
        var fake = new RecordingFakeLlm { CompleteResult = null };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.AnchorsOnly);

        var result = await harness.GenerateAnchorsAsync("some content body", CancellationToken.None);
        result.Should().BeNull();
        fake.CompleteCallCount.Should().Be(1);

        // Second call should hit the cached null without invoking the LLM again.
        var second = await harness.GenerateAnchorsAsync("some content body", CancellationToken.None);
        second.Should().BeNull();
        fake.CompleteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateAnchorsAsync_DisabledMode_ReturnsNull_WithoutCallingLlm()
    {
        var fake = new RecordingFakeLlm
        {
            CompleteResult = "should not be returned",
        };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.RewriteOnly);  // anchors disabled

        var result = await harness.GenerateAnchorsAsync("content", CancellationToken.None);
        result.Should().BeNull();
        fake.CompleteCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateAnchorsAsync_PromptContainsOnlyDocumentContent_NoGoldOrQuestion()
    {
        // Anti-data-leakage contract: the LLM must NEVER receive the gold-answer
        // set or the question text. The doc content is the ONLY user-data input.
        const string secretGold = "GOLD_ANSWER_TOKEN_MUST_NOT_LEAK";
        const string secretQuestion = "QUESTION_TEXT_MUST_NOT_LEAK";
        const string docContent = "This is the doc content body that is safe to send to the LLM.";

        var fake = new RecordingFakeLlm { CompleteResult = "some anchor here" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.AnchorsOnly);

        await harness.GenerateAnchorsAsync(docContent, CancellationToken.None);

        fake.CompleteCallCount.Should().Be(1);
        // The user prompt is what carries the doc content; the system prompt is the
        // (fixed) anchors instruction template. Doc content must appear in the user
        // prompt, never the system prompt.
        fake.LastUserPrompt.Should().NotBeNull();
        fake.LastUserPrompt.Should().Contain(docContent);
        fake.LastSystemPrompt.Should().NotBeNull();
        fake.LastSystemPrompt.Should().NotContain(docContent);
        // The two secrets are tokens we never passed in via the API at all, so by
        // construction they can't appear. This assertion documents the contract.
        fake.LastUserPrompt.Should().NotContain(secretGold);
        fake.LastUserPrompt.Should().NotContain(secretQuestion);
    }

    [Fact]
    public async Task RewriteQueryAsync_HitsLlmOnce_AndCachesResult()
    {
        var fake = new RecordingFakeLlm { CompleteResult = "rewritten search-friendly form" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.RewriteOnly);

        var first = await harness.RewriteQueryAsync("original query", CancellationToken.None);
        var second = await harness.RewriteQueryAsync("original query", CancellationToken.None);

        first.Should().Be("rewritten search-friendly form");
        second.Should().Be("rewritten search-friendly form");
        fake.CompleteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateHypotheticalAsync_HitsLlmOnce_AndCachesResult()
    {
        var fake = new RecordingFakeLlm { CompleteResult = "Some hypothetical answer here." };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.HydeOnly);

        var first = await harness.GenerateHypotheticalAsync("what is X", CancellationToken.None);
        var second = await harness.GenerateHypotheticalAsync("what is X", CancellationToken.None);

        first.Should().Be("Some hypothetical answer here.");
        second.Should().Be("Some hypothetical answer here.");
        fake.CompleteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task RewriteQueryAsync_DistinctFromHyde_UsesSeparateCacheKeys()
    {
        var fake = new RecordingFakeLlm { CompleteResult = "shared output" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", new AgentModes(false, true, true, false));

        await harness.RewriteQueryAsync("same query text", CancellationToken.None);
        await harness.GenerateHypotheticalAsync("same query text", CancellationToken.None);

        // Despite identical input, the two ops keyed independently — both hit the LLM.
        fake.CompleteCallCount.Should().Be(2);
    }

    [Fact]
    public async Task EmptyOrWhitespaceInput_ReturnsNull_WithoutCallingLlm()
    {
        var fake = new RecordingFakeLlm { CompleteResult = "y" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.All);

        (await harness.GenerateAnchorsAsync("", CancellationToken.None)).Should().BeNull();
        (await harness.GenerateAnchorsAsync("   ", CancellationToken.None)).Should().BeNull();
        (await harness.RewriteQueryAsync("", CancellationToken.None)).Should().BeNull();
        (await harness.GenerateHypotheticalAsync("", CancellationToken.None)).Should().BeNull();

        fake.CompleteCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateTypedKeywordsAsync_HitsLlmOnce_AndCachesResult()
    {
        var fake = new RecordingFakeLlm { CompleteResult = "entity:maria\ntopic:home renovation" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.TypedKeywordsOnly);

        var first = await harness.GenerateTypedKeywordsAsync("content body", CancellationToken.None);
        var second = await harness.GenerateTypedKeywordsAsync("content body", CancellationToken.None);

        first.Should().NotBeNull().And.Equal("entity:maria", "topic:home renovation");
        second.Should().NotBeNull().And.Equal("entity:maria", "topic:home renovation");
        fake.CompleteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateTypedKeywordsAsync_DisabledMode_ReturnsNull_WithoutCallingLlm()
    {
        var fake = new RecordingFakeLlm { CompleteResult = "entity:x" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.AnchorsOnly);  // typed disabled

        var result = await harness.GenerateTypedKeywordsAsync("content", CancellationToken.None);
        result.Should().BeNull();
        fake.CompleteCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateTypedKeywordsAsync_NullLlmResult_ReturnsNull_AndCachesNull()
    {
        var fake = new RecordingFakeLlm { CompleteResult = null };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.TypedKeywordsOnly);

        (await harness.GenerateTypedKeywordsAsync("content", CancellationToken.None)).Should().BeNull();
        (await harness.GenerateTypedKeywordsAsync("content", CancellationToken.None)).Should().BeNull();
        fake.CompleteCallCount.Should().Be(1);  // null cached, no retry
    }

    [Fact]
    public async Task GenerateTypedKeywordsAsync_PromptCarriesOnlyDocContent()
    {
        const string docContent = "Maria spent the weekend renovating her kitchen.";
        var fake = new RecordingFakeLlm { CompleteResult = "entity:maria" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.TypedKeywordsOnly);

        await harness.GenerateTypedKeywordsAsync(docContent, CancellationToken.None);

        fake.LastUserPrompt.Should().Contain(docContent);
        fake.LastSystemPrompt.Should().NotContain(docContent);
    }

    /// <summary>
    /// Recording fake — captures the system/user prompts and returns a configurable
    /// canned result. Used to assert call counts and content shape (e.g. the
    /// anti-data-leakage contract).
    /// </summary>
    private sealed class RecordingFakeLlm : IBenchLlmClient
    {
        public int CompleteCallCount { get; private set; }
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserPrompt { get; private set; }
        public bool LastJsonMode { get; private set; }
        public string? CompleteResult { get; set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, bool jsonMode, CancellationToken ct)
        {
            CompleteCallCount++;
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            LastJsonMode = jsonMode;
            return Task.FromResult(CompleteResult);
        }
    }
}
