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
/// anti-data-leakage contract, (d) one call yields both attributes and keywords.
/// </summary>
public class LlmAgentHarnessTests : IDisposable
{
    private const string AttributesKeywordsJson =
        "{\"attributes\":{\"person\":[\"maria\"],\"topic\":[\"home renovation\"]},\"keywords\":[\"kitchen remodel\",\"renovation\"]}";

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

    // Default mirrors production: DefaultMaxAttempts (1) — retries off unless a test opts in.
    private LlmAgentHarness NewHarness(RecordingFakeLlm fake, int maxAttempts = LlmAgentHarness.DefaultMaxAttempts) =>
        new(fake, NewCache(), "fake", "test-model", maxAttempts);

    [Fact]
    public async Task GenerateIngestAsync_HitsLlmOnce_AndCachesResult()
    {
        var fake = new RecordingFakeLlm { CompleteResult = AttributesKeywordsJson };
        var harness = NewHarness(fake);

        var first = await harness.GenerateIngestAsync("some content body", CancellationToken.None);
        var second = await harness.GenerateIngestAsync("some content body", CancellationToken.None);

        first.Should().NotBeNull();
        first!.Attributes.Should().ContainKey("person");
        first.Attributes["person"].Should().Equal("maria");
        first.Keywords.Should().Contain("kitchen remodel");
        second.Should().NotBeNull();
        // LLM hit exactly once; the second call cache-hits.
        fake.CompleteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateIngestAsync_ReturnsBothAttributesAndKeywords_InOneCall()
    {
        var fake = new RecordingFakeLlm { CompleteResult = AttributesKeywordsJson };
        var harness = NewHarness(fake);

        var result = await harness.GenerateIngestAsync("content", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Attributes.Keys.Should().BeEquivalentTo(new[] { "person", "topic" });
        result.Keywords.Should().Equal("kitchen remodel", "renovation");
        // One shot: the single call produced both attributes and keywords — the faithful mirror of `remember`.
        fake.CompleteCallCount.Should().Be(1);
        fake.LastJsonMode.Should().BeTrue("the combined attributes+keywords are requested in JSON mode");
    }

    [Fact]
    public async Task DefaultMaxAttempts_IsOne_SoRetriesAreOffByDefault()
    {
        LlmAgentHarness.DefaultMaxAttempts.Should().Be(1);

        // With the default, an empty reply is NOT retried — exactly one call, then give up.
        var fake = new RecordingFakeLlm { CompleteResult = null };
        var harness = NewHarness(fake);  // default attempts

        (await harness.GenerateIngestAsync("content", CancellationToken.None)).Should().BeNull();
        fake.CompleteCallCount.Should().Be(1, "retries are opt-in; the default makes a single attempt");
    }

    [Fact]
    public async Task GenerateIngestAsync_RetriesEmptyReply_ThenSucceeds()
    {
        // First attempt comes back empty; the second yields usable attributes/keywords. The harness
        // re-rolls and returns the good parse without the caller seeing the empty.
        var fake = new RecordingFakeLlm { ResultSequence = new Queue<string?>(new[] { null, AttributesKeywordsJson }) };
        var harness = NewHarness(fake, maxAttempts: 3);

        var result = await harness.GenerateIngestAsync("some content body", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Keywords.Should().Contain("renovation");
        fake.CompleteCallCount.Should().Be(2, "one empty attempt then one success");

        // The success is cached: a second call replays it without hitting the LLM again.
        (await harness.GenerateIngestAsync("some content body", CancellationToken.None)).Should().NotBeNull();
        fake.CompleteCallCount.Should().Be(2);
    }

    [Fact]
    public async Task GenerateIngestAsync_AllAttemptsEmpty_ReturnsNull_ExhaustsAttempts_AndDoesNotCache()
    {
        var fake = new RecordingFakeLlm { CompleteResult = null };
        var harness = NewHarness(fake, maxAttempts: 2);

        (await harness.GenerateIngestAsync("some content body", CancellationToken.None)).Should().BeNull();
        fake.CompleteCallCount.Should().Be(2, "every attempt is used before giving up");

        // A failed doc is NOT cached — a rerun gets fresh attempts (2 more calls).
        (await harness.GenerateIngestAsync("some content body", CancellationToken.None)).Should().BeNull();
        fake.CompleteCallCount.Should().Be(4);
    }

    [Fact]
    public async Task GenerateIngestAsync_UnparseableJson_RetriesThenGivesUp_WithoutCaching()
    {
        var fake = new RecordingFakeLlm { CompleteResult = "not json at all" };
        var harness = NewHarness(fake, maxAttempts: 2);

        (await harness.GenerateIngestAsync("content", CancellationToken.None)).Should().BeNull();
        (await harness.GenerateIngestAsync("content", CancellationToken.None)).Should().BeNull();
        fake.CompleteCallCount.Should().Be(4, "2 attempts per call, uncached across calls");
    }

    [Fact]
    public async Task EmptyOrWhitespaceInput_ReturnsNull_WithoutCallingLlm()
    {
        var fake = new RecordingFakeLlm { CompleteResult = AttributesKeywordsJson };
        var harness = NewHarness(fake);

        (await harness.GenerateIngestAsync("", CancellationToken.None)).Should().BeNull();
        (await harness.GenerateIngestAsync("   ", CancellationToken.None)).Should().BeNull();

        fake.CompleteCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateIngestAsync_PromptContainsOnlyDocumentContent_NoGoldOrQuestion()
    {
        // Anti-data-leakage contract: the LLM must NEVER receive the gold-answer set or
        // the question text. The doc content is the ONLY user-data input.
        const string secretGold = "GOLD_ANSWER_TOKEN_MUST_NOT_LEAK";
        const string secretQuestion = "QUESTION_TEXT_MUST_NOT_LEAK";
        const string docContent = "Maria spent the weekend renovating her kitchen.";

        var fake = new RecordingFakeLlm { CompleteResult = AttributesKeywordsJson };
        var harness = NewHarness(fake);

        await harness.GenerateIngestAsync(docContent, CancellationToken.None);

        fake.CompleteCallCount.Should().Be(1);
        // Doc content rides the user prompt; the system prompt is the fixed instruction template.
        fake.LastUserPrompt.Should().NotBeNull();
        fake.LastUserPrompt.Should().Contain(docContent);
        fake.LastSystemPrompt.Should().NotBeNull();
        fake.LastSystemPrompt.Should().NotContain(docContent);
        fake.LastUserPrompt.Should().NotContain(secretGold);
        fake.LastUserPrompt.Should().NotContain(secretQuestion);
    }

    [Fact]
    public async Task Stats_TallyReconciles_AcrossEmpty_CacheHit_Success_AndFailure()
    {
        // Script four docs through one harness (maxAttempts=1): a success, the SAME content
        // again (cache hit), a blank-content skip, and a doc whose reply never parses.
        var fake = new RecordingFakeLlm
        {
            ResultSequence = new Queue<string?>(new[] { AttributesKeywordsJson, /* doc "fail" */ null }),
        };
        var harness = NewHarness(fake, maxAttempts: 1);

        (await harness.GenerateIngestAsync("doc-a", CancellationToken.None)).Should().NotBeNull(); // fresh success
        (await harness.GenerateIngestAsync("doc-a", CancellationToken.None)).Should().NotBeNull(); // cache hit
        (await harness.GenerateIngestAsync("   ",   CancellationToken.None)).Should().BeNull();    // empty content
        (await harness.GenerateIngestAsync("fail",  CancellationToken.None)).Should().BeNull();    // reply won't parse

        var s = harness.Stats;
        s.Seen.Should().Be(4);
        s.EmptyContent.Should().Be(1);
        s.CacheHits.Should().Be(1);
        s.LlmDocs.Should().Be(2, "doc-a's first call and 'fail' both went to the model");
        s.Produced.Should().Be(2, "the fresh success + the cache hit");
        s.Failed.Should().Be(1, "'fail' reached the model but never parsed");

        // Reconciliation invariants the printed block asserts.
        (s.EmptyContent + s.CacheHits + s.LlmDocs).Should().Be(s.Seen);
        s.Produced.Should().Be(s.CacheHits + (s.LlmDocs - s.Failed));
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

        /// <summary>Optional per-call script: each call dequeues the next result. Falls back to
        /// <see cref="CompleteResult"/> once drained. Lets a test model an empty-then-good re-roll.</summary>
        public Queue<string?>? ResultSequence { get; set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, bool jsonMode, CancellationToken ct)
        {
            CompleteCallCount++;
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            LastJsonMode = jsonMode;
            string? result = ResultSequence is { Count: > 0 } ? ResultSequence.Dequeue() : CompleteResult;
            return Task.FromResult(result);
        }
    }
}
