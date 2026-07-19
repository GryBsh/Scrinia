using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// End-to-end smoke test for the agentic-ingest arm: drives
/// <see cref="BenchmarkRunner.RunGroupedAsync"/> against an
/// <see cref="AgenticScriniaRetriever"/> backed by a scripted (no-network)
/// <see cref="IToolCallingClient"/>, then scores through the real
/// <see cref="BenchmarkScorer"/>. Proves runner → per-session episodes →
/// provenance (ref: expansion) → scorer wiring works end-to-end, not just
/// each piece in isolation (see <see cref="AgenticScriniaRetrieverTests"/> and
/// <see cref="AgenticIngestHarnessTests"/> for the unit-level coverage).
/// </summary>
public sealed class AgenticIngestEndToEndTests : IDisposable
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), $"scrinia_agentic_e2e_{Guid.NewGuid():N}");
    private readonly string _cacheFile;

    public AgenticIngestEndToEndTests()
    {
        _cacheFile = Path.Combine(_rootDir, "cache", "episodes.ndjson");
    }

    public void Dispose() { try { Directory.Delete(_rootDir, true); } catch { } }

    /// <summary>Scripted client: a fixed queue of turns, dequeued on each call regardless of
    /// input. The retriever runs one episode per session (first-seen order), so this queue
    /// must contain enough turns for every episode the run will trigger.</summary>
    private sealed class ScriptedToolClient : IToolCallingClient
    {
        private readonly Queue<ToolTurnResult?> _turns;
        public ScriptedToolClient(params ToolTurnResult?[] turns) => _turns = new(turns);
        public Task<ToolTurnResult?> ChatWithToolsAsync(
            IReadOnlyList<ToolChatMessage> m, IReadOnlyList<ToolSpec> t, int mt, CancellationToken ct)
            => Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : new ToolTurnResult(null, [], "stop"));
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RunGroupedAsync_AgenticIngest_ScoresNonZeroRecall_ThroughRunnerAndScorer()
    {
        // Session s1 episode stores a memory citing gold doc D1:1; session s2 episode stores
        // a memory citing gold doc D2:1. Each episode is a tool_calls turn followed by a
        // no-tool-calls turn so the episode terminates (mirrors AgenticIngestHarnessTests).
        var rememberLisbon = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","content":["Booked the Lisbon trip.\nref: D1:1"]}""");
        var rememberBerlin = new ToolCallRequest("c2", "remember",
            """{"path":"/findings/job","content":["Started a new job in Berlin.\nref: D2:1"]}""");

        var client = new ScriptedToolClient(
            new ToolTurnResult(null, [rememberLisbon], "tool_calls"),
            new ToolTurnResult("done", [], "stop"),
            new ToolTurnResult(null, [rememberBerlin], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));
        var harness = new AgenticIngestHarness(client, maxTurns: 8);
        var cache = new EpisodeCache(_cacheFile);
        await using var retriever = new AgenticScriniaRetriever(_rootDir, harness, cache, modelSlug: "test-model");

        // 4-doc haystack across 2 sessions.
        var d1 = new HaystackDoc("D1:1", "Maria: Booked the Lisbon trip.", null, "s1");
        var d2 = new HaystackDoc("D1:2", "Maria: The flight leaves Tuesday.", null, "s1");
        var d3 = new HaystackDoc("D2:1", "Diego: Started a new job in Berlin.", null, "s2");
        var d4 = new HaystackDoc("D2:2", "Diego: The office is downtown.", null, "s2");
        IReadOnlyList<HaystackDoc> haystack = [d1, d2, d3, d4];

        var q1 = new BenchmarkQuestion("q1", "What trip did Maria book?", haystack, ["D1:1"], [], "single-hop");
        var q2 = new BenchmarkQuestion("q2", "Where did Diego start a new job?", haystack, ["D2:1"], [], "single-hop");
        var group = new BenchmarkQuestionGroup("g1", haystack, [q1, q2]);

        var result = await new BenchmarkRunner().RunGroupedAsync(
            "locomo-test", "agentic-ingest-test",
            ToAsync(new[] { group }), retriever, topK: 10,
            BenchmarkScorer.ScoreFractionalById, ct: CancellationToken.None);

        result.Aggregate.RecallAt10.Should().BeGreaterThan(0);
        result.Questions.Should().HaveCount(2);
        result.Questions.Should().Contain(q => q.QuestionId == "q1" && q.RecallAt10 > 0);
        result.Questions.Should().Contain(q => q.QuestionId == "q2" && q.RecallAt10 > 0);
        retriever.Stats.AgenticEpisodes.Should().Be(2);
        retriever.Stats.FailedEpisodes.Should().Be(0);
    }

    [Fact]
    public async Task RunGroupedAsync_AgenticIngest_HybridPath_SurfacesLexicallyMismatchedGoldDoc()
    {
        // The stored memory and the query share NO tokens; only cosine similarity via
        // ConceptEmbeddingProvider (travel-concept words -> [1,0], else -> [0,1]) can
        // connect them. BM25 alone would score this memory zero against the query, so a
        // non-zero recall here can ONLY come from the semantic (hybrid) contributor.
        var rememberLisbon = new ToolCallRequest("c1", "remember",
            """{"path":"/findings/trip","content":["Booked the Lisbon getaway.\nref: D1:1"]}""");

        var client = new ScriptedToolClient(
            new ToolTurnResult(null, [rememberLisbon], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));
        var harness = new AgenticIngestHarness(client, maxTurns: 8);
        var cache = new EpisodeCache(_cacheFile);
        await using var retriever = new AgenticScriniaRetriever(
            _rootDir, harness, cache, modelSlug: "test-model", embedder: new ConceptEmbeddingProvider());

        // Single-session haystack: gold doc (matches the stored memory's citation) plus an
        // off-topic distractor doc, neither sharing tokens with the query.
        var gold = new HaystackDoc("D1:1", "Maria: Booked the Lisbon getaway.", null, "s1");
        var distractor = new HaystackDoc("D1:2", "Diego: Started a new job in Berlin.", null, "s1");
        IReadOnlyList<HaystackDoc> haystack = [gold, distractor];

        var question = new BenchmarkQuestion("q1", "vacation holiday plans", haystack, ["D1:1"], [], "single-hop");
        var group = new BenchmarkQuestionGroup("g1", haystack, [question]);

        var result = await new BenchmarkRunner().RunGroupedAsync(
            "locomo-test", "agentic-ingest-hybrid-test",
            ToAsync(new[] { group }), retriever, topK: 10,
            BenchmarkScorer.ScoreFractionalById, ct: CancellationToken.None);

        result.Aggregate.RecallAt10.Should().BeGreaterThan(0);
        result.Questions.Should().Contain(q => q.QuestionId == "q1" && q.RecallAt10 > 0);
        retriever.Stats.AgenticEpisodes.Should().Be(1);
        retriever.Stats.FailedEpisodes.Should().Be(0);
    }
}
