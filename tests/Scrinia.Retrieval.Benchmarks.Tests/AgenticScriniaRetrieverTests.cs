using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AgenticScriniaRetrieverTests : IDisposable
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), $"scrinia_agentic_retr_{Guid.NewGuid():N}");
    private readonly string _cacheFile;

    public AgenticScriniaRetrieverTests()
    {
        _cacheFile = Path.Combine(_rootDir, "cache", "episodes.ndjson");
    }

    public void Dispose() { try { Directory.Delete(_rootDir, true); } catch { } }

    /// <summary>Scripted client: a fixed queue of turns, dequeued on each call. Counts calls for the cache-hit spy.</summary>
    private sealed class ScriptedToolClient : IToolCallingClient
    {
        private readonly Queue<ToolTurnResult?> _turns;
        public int CallCount { get; private set; }
        public ScriptedToolClient(params ToolTurnResult?[] turns) => _turns = new(turns);
        public Task<ToolTurnResult?> ChatWithToolsAsync(
            IReadOnlyList<ToolChatMessage> m, IReadOnlyList<ToolSpec> t, int mt, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : new ToolTurnResult(null, [], "stop"));
        }
    }

    private (AgenticScriniaRetriever retriever, ScriptedToolClient client, EpisodeCache cache) MakeRetriever(
        params ToolTurnResult?[] turns) => MakeRetriever(surfaceFullContent: false, turns);

    private (AgenticScriniaRetriever retriever, ScriptedToolClient client, EpisodeCache cache) MakeRetriever(
        bool surfaceFullContent, params ToolTurnResult?[] turns)
    {
        var client = new ScriptedToolClient(turns);
        var harness = new AgenticIngestHarness(client, maxTurns: 8);
        var cache = new EpisodeCache(_cacheFile);
        var retriever = new AgenticScriniaRetriever(_rootDir, harness, cache, modelSlug: "test-model",
            rankerOptions: null, surfaceFullContent: surfaceFullContent);
        return (retriever, client, cache);
    }

    [Fact]
    public async Task ProvenanceExpansion_ReturnsCitedDocs_InOrder()
    {
        var remember = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/findings/trip","content":["Booked the Lisbon trip.\nref: d1\nref: d2"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("d1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("d2", "Maria: Flight is on Tuesday.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        hits.Select(h => h.Id).Should().Equal("d1", "d2");
        hits.Should().OnlyContain(h => h.Score > 0);
    }

    [Fact]
    public async Task DedupAcrossMemories_DocAppearsOnce()
    {
        var remember1 = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/findings/a","content":["Booked the Lisbon trip.\nref: d1"]}""");
        var remember2 = new ToolCallRequest("c2", "memory_remember",
            """{"path":"/findings/b","content":["Lisbon trip details confirmed.\nref: d1\nref: d2"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember1, remember2], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("d1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("d2", "Maria: Flight is on Tuesday.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        hits.Count(h => h.Id == "d1").Should().Be(1);
    }

    [Fact]
    public async Task CacheReplay_RoundTrips_WithoutSecondLlmCall()
    {
        var remember = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/findings/trip","content":["Booked the Lisbon trip.\nref: d1"]}""");
        var client = new ScriptedToolClient(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));
        var harness = new AgenticIngestHarness(client, maxTurns: 8);
        var cache = new EpisodeCache(_cacheFile);
        var retriever = new AgenticScriniaRetriever(_rootDir, harness, cache, modelSlug: "test-model");

        var doc = new HaystackDoc("d1", "Maria: Booked Lisbon.", null, "s1");
        await retriever.IngestAsync(doc);
        var firstHits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);
        int callCountAfterFirst = client.CallCount;
        callCountAfterFirst.Should().BeGreaterThan(0);

        await retriever.ResetAsync();
        await retriever.IngestAsync(doc);
        var secondHits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        client.CallCount.Should().Be(callCountAfterFirst, "cache hit must replay without calling the LLM again");
        secondHits.Select(h => h.Id).Should().Equal(firstHits.Select(h => h.Id));
    }

    [Fact]
    public async Task FailedEpisode_OnDoubleNull_StoresNothing_NoMechanicalFallback()
    {
        // Both the attempt and its retry fail (null turns). There is deliberately NO
        // mechanical-ingestion fallback — the session stores nothing, so the doc is NOT
        // retrievable, and the failure is counted honestly rather than papered over with
        // verbatim ingest that would inflate the arm's recall.
        var (retriever, _, _) = MakeRetriever((ToolTurnResult?)null, (ToolTurnResult?)null);

        await retriever.IngestAsync(new HaystackDoc("d1", "Maria: Booked a unique Lisbon getaway.", null, "s1"));

        var hits = await retriever.RetrieveAsync("unique Lisbon getaway", topK: 5);

        hits.Should().BeEmpty("a failed episode must not fall back to mechanical ingest");
        retriever.Stats.FailedEpisodes.Should().Be(1);
        retriever.Stats.AgenticEpisodes.Should().Be(0);
    }

    [Fact]
    public async Task NoRefMemory_IncrementsMemoriesWithoutRefs_AndMapsToNoDoc()
    {
        var remember = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/findings/trip","content":["Booked the Lisbon trip with no citation."]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("d1", "Maria: Booked Lisbon.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        retriever.Stats.MemoriesWithoutRefs.Should().Be(1);
        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedToolCall_SurfacesInStats_ToolCallErrors()
    {
        var bad = new ToolCallRequest("c1", "memory_remember", "{ not json");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [bad], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("d1", "Maria: Booked Lisbon.", null, "s1"));

        await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        retriever.Stats.ToolCallErrors.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ToolExecutionThrows_InvalidPath_SurfacesInStats_DoesNotAbortRun()
    {
        // A well-formed tool call whose PATH VALUE the store rejects: '/findings/trip:lisbon'
        // is a v2 path (leading '/') with a colon in a segment, so PathParser.Parse throws
        // ArgumentException out of memory_remember. That throw must be caught and counted as a
        // tool-call error — NOT allowed to escape and abort the whole run (previously it bubbled
        // to Program.Main's top-level catch: "error: ArgumentException: Path must not contain
        // colons" and the entire benchmark exited 2).
        var badPath = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/findings/trip:lisbon","content":["Booked the Lisbon trip.\nref: d1"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [badPath], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("d1", "Maria: Booked Lisbon.", null, "s1"));

        // The whole point: this line must not throw.
        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        retriever.Stats.ToolCallErrors.Should().BeGreaterThanOrEqualTo(1);
        hits.Should().BeEmpty("the rejected memory was never stored, so nothing maps back to a doc");
    }

    [Fact]
    public async Task FailedAttempt_PartialWrite_IsRolledBackBeforeRetry()
    {
        // Attempt 1: writes a memory citing d1, then fails (null turn).
        var rememberD1 = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/findings/a","content":["Booked the Lisbon trip.\nref: d1"]}""");
        // Attempt 2 (retry): writes a memory citing d2, then stops cleanly.
        var rememberD2 = new ToolCallRequest("c2", "memory_remember",
            """{"path":"/findings/b","content":["Lisbon trip confirmed.\nref: d2"]}""");

        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [rememberD1], "tool_calls"),
            null,
            new ToolTurnResult(null, [rememberD2], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("d1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("d2", "Maria: Flight is on Tuesday.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        hits.Select(h => h.Id).Should().Contain("d2");
        hits.Select(h => h.Id).Should().NotContain("d1");
        retriever.Stats.FailedEpisodes.Should().Be(0);
        retriever.Stats.AgenticEpisodes.Should().Be(1);
    }

    [Fact]
    public async Task RetrievedHit_Text_IsMemoryContent_NotDocText()
    {
        var remember = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/f/x","content":["MEMORY-SUMMARY-TOKEN about Lisbon.\nref: d1"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("d1", "RAW-DOC-TOKEN Maria booked Lisbon.", null, "s1"));
        var hits = await retriever.RetrieveAsync("Lisbon", topK: 5);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("d1", "R@k still maps to the source doc id");
        hits[0].Text.Should().Contain("MEMORY-SUMMARY-TOKEN", "the judge must see the stored memory content");
        hits[0].Text.Should().NotContain("RAW-DOC-TOKEN", "not the original doc text");
    }

    [Fact]
    public async Task SurfaceFullContent_JudgePath_UsesFullMemory_NotTruncatedPreview()
    {
        string longContent = "Lisbon trip planning. " + new string('x', 520) + " TAILTOKEN-AFTER-500\\nref: d1";
        // Bare path (no folder) — the scope whose name->artifact round-trip was lossy and
        // made the old FormatQualifiedName-based resolve fail 100% at run time. Resolving
        // via the entry Uri must handle it.
        var remember = new ToolCallRequest("c1", "memory_remember",
            "{\"path\":\"summer-plans-summary\",\"content\":[\"" + longContent + "\"]}");
        var (retriever, _, _) = MakeRetriever(surfaceFullContent: true,
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("d1", "some doc text", null, "s1"));
        var hits = await retriever.RetrieveAsync("Lisbon", topK: 5);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("d1");
        hits[0].Text.Should().Contain("TAILTOKEN-AFTER-500",
            "surfaceFullContent must feed the judge the full memory, not the 500-char ContentPreview");
    }
}
