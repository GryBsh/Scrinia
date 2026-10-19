using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AgenticScriniaRetrieverTests : IDisposable
{
    private static readonly string[] ExpectedD11D12 = new[] { "D1:1", "D1:2" };

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

    /// <summary>Overload that also wires a real (or stub) embedder, exercising the
    /// production embedding pipeline (VectorStore + HybridReranker) instead of BM25-only.</summary>
    private (AgenticScriniaRetriever retriever, ScriptedToolClient client, EpisodeCache cache) MakeRetriever(
        Scrinia.Core.Embeddings.IEmbeddingProvider? embedder, params ToolTurnResult?[] turns)
    {
        var client = new ScriptedToolClient(turns);
        var harness = new AgenticIngestHarness(client, maxTurns: 8);
        var cache = new EpisodeCache(_cacheFile);
        var retriever = new AgenticScriniaRetriever(
            _rootDir, harness, cache, modelSlug: "test-model", embedder: embedder);
        return (retriever, client, cache);
    }

    /// <summary>Builds an entity-graph-enabled agentic retriever (BM25-only, no embedder).</summary>
    private (AgenticScriniaRetriever retriever, ScriptedToolClient client, EpisodeCache cache) MakeRetrieverWithEntityGraph(
        params ToolTurnResult?[] turns)
    {
        var client = new ScriptedToolClient(turns);
        var harness = new AgenticIngestHarness(client, maxTurns: 8);
        var cache = new EpisodeCache(_cacheFile);
        var retriever = new AgenticScriniaRetriever(
            _rootDir, harness, cache, modelSlug: "test-model", entityGraphEnabled: true);
        return (retriever, client, cache);
    }

    [Fact]
    public async Task EntityGraph_LiftsAssociatedMemory_FromAgentWrittenAttributes()
    {
        // Attributes-only entity source: the episode tags one memory {person:alice, topic:zeta}
        // and another {topic:zeta, person:bob}; neither content mentions "alice". Query "alice"
        // seeds the alice node → the alice memory, and PPR diffuses alice→zeta→the bob memory,
        // lifting D1:2 (which the bob memory cites) though it never mentions alice.
        var rememberAlice = new ToolCallRequest("c1", "remember",
            """{"path":"/f/a","description":"Stored facts for this test.","content":["kickoff recap.\nref: D1:1"],"attributes":{"person":["alice"],"topic":["zeta"]}}""");
        var rememberBob = new ToolCallRequest("c2", "remember",
            """{"path":"/f/b","description":"Stored facts for this test.","content":["kickoff recap.\nref: D1:2"],"attributes":{"topic":["zeta"],"person":["bob"]}}""");
        var (retriever, _, _) = MakeRetrieverWithEntityGraph(
            new ToolTurnResult(null, [rememberAlice, rememberBob], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Alice: kickoff.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("D1:2", "Bob: kickoff.", null, "s1"));

        var hits = await retriever.RetrieveAsync("alice", topK: 5);

        hits.Select(h => h.Id).Should().Contain("D1:2",
            because: "the entity graph diffuses alice→zeta→the bob memory, lifting its cited doc though it never mentions alice");
    }

    [Fact]
    public async Task WithoutEntityGraph_AssociatedMemory_StaysBuried()
    {
        // Identical episode on the default (entity-graph-off) retriever: "alice" lexically hits
        // only the alice memory, so D1:2 (cited by the zeta/bob memory) is not surfaced. Pins the
        // sibling test's lift to the entity graph, not to ordinary field/BM25 scoring.
        var rememberAlice = new ToolCallRequest("c1", "remember",
            """{"path":"/f/a","description":"Stored facts for this test.","content":["kickoff recap.\nref: D1:1"],"attributes":{"person":["alice"],"topic":["zeta"]}}""");
        var rememberBob = new ToolCallRequest("c2", "remember",
            """{"path":"/f/b","description":"Stored facts for this test.","content":["kickoff recap.\nref: D1:2"],"attributes":{"topic":["zeta"],"person":["bob"]}}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [rememberAlice, rememberBob], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Alice: kickoff.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("D1:2", "Bob: kickoff.", null, "s1"));

        var hits = await retriever.RetrieveAsync("alice", topK: 5);

        hits.Select(h => h.Id).Should().NotContain("D1:2",
            because: "without the entity graph, the zeta/bob memory shares no query term with 'alice'");
    }

    [Fact]
    public async Task ProvenanceExpansion_ReturnsCitedDocs_InOrder()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: D1:1\nref: D1:2"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("D1:2", "Maria: Flight is on Tuesday.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        hits.Select(h => h.Id).Should().Equal("D1:1", "D1:2");
        hits.Should().OnlyContain(h => h.Score > 0);
    }

    [Fact]
    public async Task DedupAcrossMemories_DocAppearsOnce()
    {
        var remember1 = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/a","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: D1:1"]}""");
        var remember2 = new ToolCallRequest("c2", "remember",
            """{"path":"/findings/b","description":"Stored facts for this test.","content":["Lisbon trip details confirmed.\nref: D1:1\nref: D1:2"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember1, remember2], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("D1:2", "Maria: Flight is on Tuesday.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        hits.Count(h => h.Id == "D1:1").Should().Be(1);
    }

    [Fact]
    public async Task CacheReplay_RoundTrips_WithoutSecondLlmCall()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: D1:1"]}""");
        var client = new ScriptedToolClient(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));
        var harness = new AgenticIngestHarness(client, maxTurns: 8);
        var cache = new EpisodeCache(_cacheFile);
        var retriever = new AgenticScriniaRetriever(_rootDir, harness, cache, modelSlug: "test-model");

        var doc = new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1");
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
    public async Task NoToolCallEpisode_RetriesOnce_AndRecoversWhenRetryStores()
    {
        // Attempt 1: the model replies with pure text and makes no tool call at all — an
        // empty episode. That triggers one fresh re-roll; the retry stores a memory, so the
        // session recovers and is counted as an agentic episode (not failed).
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: D1:1"]}""");
        var (retriever, client, _) = MakeRetriever(
            new ToolTurnResult("I have nothing to store.", [], "stop"),          // attempt 1: no tool calls
            new ToolTurnResult(null, [remember], "tool_calls"),                   // attempt 2 (retry): stores
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        hits.Select(h => h.Id).Should().Equal("D1:1");
        retriever.Stats.AgenticEpisodes.Should().Be(1);
        retriever.Stats.FailedEpisodes.Should().Be(0);
        client.CallCount.Should().Be(3, "attempt 1 (1 empty turn) + retry (remember turn + done turn)");
    }

    [Fact]
    public async Task NoToolCallEpisode_OnBothAttempts_CountsFailed_StoresNothing()
    {
        // The model makes no tool call on the first attempt OR the retry. An episode that
        // stored nothing is a failed session — rolled back, counted, and NOT cached (so a
        // future run re-rolls it rather than replaying an empty episode forever).
        var (retriever, client, _) = MakeRetriever(
            new ToolTurnResult("Nothing to store.", [], "stop"),
            new ToolTurnResult("Still nothing.", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked a unique Lisbon getaway.", null, "s1"));

        var hits = await retriever.RetrieveAsync("unique Lisbon getaway", topK: 5);

        hits.Should().BeEmpty("an episode that made no tool calls stores nothing");
        retriever.Stats.FailedEpisodes.Should().Be(1);
        retriever.Stats.AgenticEpisodes.Should().Be(0);
        client.CallCount.Should().Be(2, "one turn per attempt, two attempts, then given up");
    }

    [Fact]
    public async Task NoRefMemory_IncrementsMemoriesWithoutRefs_AndMapsToNoDoc()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Booked the Lisbon trip with no citation."]}""");
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
        var bad = new ToolCallRequest("c1", "remember", "{ not json");
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
        // ArgumentException out of remember. That throw must be caught and counted as a
        // tool-call error — NOT allowed to escape and abort the whole run (previously it bubbled
        // to Program.Main's top-level catch: "error: ArgumentException: Path must not contain
        // colons" and the entire benchmark exited 2).
        var badPath = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip:lisbon","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: d1"]}""");
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
        // Attempt 1: writes a memory citing D1:1, then fails (null turn).
        var rememberD1 = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/a","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: D1:1"]}""");
        // Attempt 2 (retry): writes a memory citing D1:2, then stops cleanly.
        var rememberD2 = new ToolCallRequest("c2", "remember",
            """{"path":"/findings/b","description":"Stored facts for this test.","content":["Lisbon trip confirmed.\nref: D1:2"]}""");

        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [rememberD1], "tool_calls"),
            null,
            new ToolTurnResult(null, [rememberD2], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("D1:2", "Maria: Flight is on Tuesday.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        hits.Select(h => h.Id).Should().Contain("D1:2");
        hits.Select(h => h.Id).Should().NotContain("D1:1");
        retriever.Stats.FailedEpisodes.Should().Be(0);
        retriever.Stats.AgenticEpisodes.Should().Be(1);
    }

    [Fact]
    public async Task RetrievedHit_Text_IsMemoryContent_NotDocText()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/f/x","description":"Stored facts for this test.","content":["MEMORY-SUMMARY-TOKEN about Lisbon.\nref: D1:1"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "RAW-DOC-TOKEN Maria booked Lisbon.", null, "s1"));
        var hits = await retriever.RetrieveAsync("Lisbon", topK: 5);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("D1:1", "R@k still maps to the source doc id");
        hits[0].Text.Should().Contain("MEMORY-SUMMARY-TOKEN", "the judge must see the stored memory content");
        hits[0].Text.Should().NotContain("RAW-DOC-TOKEN", "not the original doc text");
    }

    [Fact]
    public async Task Provenance_ParsesCommaListRefs_MapsAllCitedDialogs()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Trip planning consolidated.\nref: D1:1, D1:2"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("D1:2", "Maria: Flight Tuesday.", null, "s1"));

        var hits = await retriever.RetrieveAsync("trip planning", topK: 5);

        hits.Select(h => h.Id).Should().BeEquivalentTo(ExpectedD11D12);
        retriever.Stats.UnresolvableRefs.Should().Be(0);
    }

    [Fact]
    public async Task Provenance_UnknownCitedId_CountsUnresolvable()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Cites a missing dialog.\nref: D9:99"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));

        var hits = await retriever.RetrieveAsync("missing dialog", topK: 5);

        hits.Should().BeEmpty("D9:99 is not a buffered doc");
        retriever.Stats.UnresolvableRefs.Should().Be(1);
    }

    [Fact]
    public async Task FullContent_UsesFullMemory_NotTruncatedPreview()
    {
        string longContent = "Lisbon trip planning. " + new string('x', 520) + " TAILTOKEN-AFTER-500\\nref: D1:1";
        var remember = new ToolCallRequest("c1", "remember",
            "{\"path\":\"summer-plans-summary\",\"description\":\"Stored facts for this test.\",\"content\":[\"" + longContent + "\"]}");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "some doc text", null, "s1"));
        var hits = await retriever.RetrieveAsync("Lisbon", topK: 5);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("D1:1");
        hits[0].Text.Should().Contain("TAILTOKEN-AFTER-500",
            "full content (not the 500-char preview) must be surfaced");
    }

    [Fact]
    public async Task GetSurfacedContents_ReturnsMemoryContent_EvenWithNoResolvableRefs()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["JUDGE-VISIBLE consolidated Lisbon plan with no citation."]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));

        // R@k drops it (no resolvable ref) ...
        (await retriever.RetrieveAsync("Lisbon plan", topK: 5)).Should().BeEmpty();
        // ... but the judge still sees the memory content.
        var surfaced = await retriever.GetSurfacedContentsAsync("Lisbon plan", k: 10);
        surfaced.Should().ContainSingle().Which.Should().Contain("JUDGE-VISIBLE");
    }

    [Fact]
    public async Task Reindex_PopulatesVectorStore_AfterEpisode()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: D1:1"]}""");
        var (retriever, _, _) = MakeRetriever(
            new StubEmbeddingProvider(),
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));

        await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        retriever.EmbeddedVectorCount.Should().BeGreaterThan(0,
            "an embedder was supplied, so the post-episode reindex must populate the shared VectorStore");
    }

    [Fact]
    public async Task NoEmbedder_LeavesVectorPipelineNull_BehaviorUnchanged()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: D1:1"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        retriever.EmbeddedVectorCount.Should().Be(0, "no embedder was supplied, so the pipeline stays null");
        hits.Select(h => h.Id).Should().Equal("D1:1");
    }

    [Fact]
    public async Task Dispose_ReleasesVectorStore_NoThrow()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: D1:1"]}""");
        var (retriever, _, _) = MakeRetriever(
            new StubEmbeddingProvider(),
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        await retriever.DisposeAsync();
        await retriever.DisposeAsync();
    }

    [Fact]
    public async Task Hybrid_SurfacesSemanticMatch_ThatBm25Misses()
    {
        // Two memories, neither sharing a single query token: the travel memory's tokens are
        // {booked, lisbon, getaway}, the distractor's are {fixed, kitchen, sink, plumbing}, and
        // the query's are {vacation, holiday, plans} — zero lexical overlap with either. Under
        // BM25-only, both score 0 and neither surfaces. ConceptEmbeddingProvider makes the query
        // and the travel memory cosine-identical (both contain travel-concept words), so the
        // hybrid contributor's supplemental score is the ONLY thing that can surface D1:1.
        var rememberTravel = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Booked the Lisbon getaway.\nref: D1:1"]}""");
        var rememberDistractor = new ToolCallRequest("c2", "remember",
            """{"path":"/findings/plumbing","description":"Stored facts for this test.","content":["Fixed the kitchen sink plumbing.\nref: D2:1"]}""");

        var (hybridRetriever, _, _) = MakeRetriever(
            new ConceptEmbeddingProvider(),
            new ToolTurnResult(null, [rememberTravel, rememberDistractor], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));
        await hybridRetriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));
        await hybridRetriever.IngestAsync(new HaystackDoc("D2:1", "Maria: Sink is fixed.", null, "s2"));

        var hybridHits = await hybridRetriever.RetrieveAsync("vacation holiday plans", topK: 5);

        // Empirically verified against the real WeightedAttributeScorer (default Sum fusion):
        // TryInsert's relevance gate is `fieldScore + normalizedBm25*5 + supplemental`, and it
        // only requires relevance > 0 — a zero-BM25, zero-field candidate DOES surface on
        // supplemental score alone. So this asserts hybrid CONTAINS D1:1 while BM25-only misses
        // it entirely, rather than falling back to the reorder variant.
        hybridHits.Select(h => h.Id).Should().Contain("D1:1",
            "the semantic contributor surfaces the travel memory purely on cosine similarity");
        hybridHits.Select(h => h.Id).Should().NotContain("D2:1",
            "the distractor shares no query tokens and is orthogonal in concept-space");

        var (bm25Retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [rememberTravel, rememberDistractor], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));
        await bm25Retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));
        await bm25Retriever.IngestAsync(new HaystackDoc("D2:1", "Maria: Sink is fixed.", null, "s2"));

        var bm25Hits = await bm25Retriever.RetrieveAsync("vacation holiday plans", topK: 5);

        bm25Hits.Should().BeEmpty(
            "BM25-only has zero lexical overlap with either memory, so nothing surfaces");
    }

    [Fact]
    public async Task NoContributor_QueryPath_UnchangedFromBm25()
    {
        // Built WITHOUT an embedder — _searchContributor is null, so the query path must stay
        // byte-for-byte BM25-only. Reuses the lexically-matching-query assertion shape from
        // ProvenanceExpansion_ReturnsCitedDocs_InOrder to pin the no-embedder path unchanged.
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","description":"Stored facts for this test.","content":["Booked the Lisbon trip.\nref: D1:1\nref: D1:2"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("D1:2", "Maria: Flight is on Tuesday.", null, "s1"));

        var hits = await retriever.RetrieveAsync("Lisbon trip", topK: 5);

        hits.Select(h => h.Id).Should().Equal("D1:1", "D1:2");
        hits.Should().OnlyContain(h => h.Score > 0);
    }

    [Fact]
    public async Task GetSurfacedContents_prefixes_memory_with_authoring_session_date()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/support","description":"Stored facts for this test.","content":["Talked about joining a support group.\nref: D1:3"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc(
            "D1:3", "Maria: I joined a support group.", new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero), "s1"));

        var contents = await retriever.GetSurfacedContentsAsync("support group", k: 5);
        Assert.NotEmpty(contents);
        Assert.All(contents, c => Assert.StartsWith("created: 2023-05-04\n", c));
    }

    [Fact]
    public async Task Retrieve_hits_are_not_date_prefixed_so_recall_is_unaffected()
    {
        var remember = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/support","description":"Stored facts for this test.","content":["Talked about joining a support group.\nref: D1:3"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc(
            "D1:3", "Maria: I joined a support group.", new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero), "s1"));

        var hits = await retriever.RetrieveAsync("support group", topK: 10);
        Assert.All(hits, h => Assert.DoesNotContain("created: ", h.Text));
    }
}
