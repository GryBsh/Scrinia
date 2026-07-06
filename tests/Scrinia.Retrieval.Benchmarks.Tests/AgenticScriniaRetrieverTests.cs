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
        params ToolTurnResult?[] turns)
    {
        var client = new ScriptedToolClient(turns);
        var harness = new AgenticIngestHarness(client, maxTurns: 8);
        var cache = new EpisodeCache(_cacheFile);
        var retriever = new AgenticScriniaRetriever(_rootDir, harness, cache, modelSlug: "test-model");
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
    public async Task Fallback_OnDoubleNull_IngestsRawDoc_AndIncrementsStat()
    {
        var (retriever, _, _) = MakeRetriever((ToolTurnResult?)null, (ToolTurnResult?)null);

        await retriever.IngestAsync(new HaystackDoc("d1", "Maria: Booked a unique Lisbon getaway.", null, "s1"));

        var hits = await retriever.RetrieveAsync("unique Lisbon getaway", topK: 5);

        hits.Select(h => h.Id).Should().Contain("d1");
        retriever.Stats.FallbackEpisodes.Should().Be(1);
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
}
