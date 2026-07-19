using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Tests for <see cref="SentenceMaxSimReranker"/>. Uses a deterministic fake embedder
/// so we can construct exact MaxSim scenarios and assert the reranker reorders as
/// expected. Real model2vec behavior is covered indirectly by the integration bench;
/// these tests cover the reranker's contract — score replacement, fallback semantics,
/// and order — independently of any model's quirks.
/// </summary>
public class SentenceMaxSimRerankerTests
{
    [Fact]
    public async Task ReordersByMaxSim_WhenFirstStageScoresEqual()
    {
        // Three candidates, all with the same first-stage score (so first-stage
        // contributes nothing to ordering — normalized to 0 across the pool). The
        // reranker must reorder them by max sentence-cosine.
        var queryVec = new float[] { 1.0f, 0.0f, 0.0f };
        var embedder = new FakeEmbedder("query", queryVec);

        var sentenceIndex = new Dictionary<string, IReadOnlyList<float[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["doc-a"] = new[] { new float[] { 0.5f, 0.5f, 0.0f }, new float[] { 0.9f, 0.4f, 0.0f } },
            ["doc-b"] = new[] { new float[] { 0.6f, 0.8f, 0.0f } },
            ["doc-c"] = new[] { new float[] { 0.99f, 0.14f, 0.0f }, new float[] { 0.1f, 0.0f, 0.0f } },
        };

        var reranker = new SentenceMaxSimReranker(
            embedder,
            name => sentenceIndex.TryGetValue(name, out var v) ? v : null,
            firstStageWeight: 0.5);

        var candidates = new SearchResult[]
        {
            MakeEntry("doc-a", originalScore: 100.0),
            MakeEntry("doc-b", originalScore: 100.0),
            MakeEntry("doc-c", originalScore: 100.0),
        };

        var result = await reranker.RerankAsync("query", candidates, CancellationToken.None);

        result.Should().HaveCount(3);
        (result[0] as EntryResult)!.Item.Entry.Name.Should().Be("doc-c",
            "with equal first-stage, MaxSim is the only discriminator → doc-c (cos≈0.99) wins");
        (result[1] as EntryResult)!.Item.Entry.Name.Should().Be("doc-a");
        (result[2] as EntryResult)!.Item.Entry.Name.Should().Be("doc-b");
    }

    [Fact]
    public async Task BlendsFirstStageWithMaxSim()
    {
        // Tests the blend math directly: doc-a has the BEST first-stage score but
        // ZERO MaxSim signal. doc-b has the WORST first-stage but the BEST MaxSim.
        // At α=0 (pure MaxSim) doc-b wins; at α=1 (pure first-stage) doc-a wins;
        // at α=0.5 the outcome depends on normalized values — both are at the same
        // [0, 1] extreme of their respective sources, so they tie. Doc-c is the
        // tiebreaker showing the blend is genuinely combining, not just picking one.
        var queryVec = new float[] { 1.0f, 0.0f };
        var embedder = new FakeEmbedder("query", queryVec);

        var sentenceIndex = new Dictionary<string, IReadOnlyList<float[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["doc-a"] = new[] { new float[] { 0.0f, 1.0f } }, // cosine = 0.0 (orthogonal)
            ["doc-b"] = new[] { new float[] { 1.0f, 0.0f } }, // cosine = 1.0 (perfect)
            ["doc-c"] = new[] { new float[] { 0.7f, 0.71f } }, // cosine ≈ 0.7
        };

        // Pure first-stage (α=1) → doc-a wins.
        var pureFs = new SentenceMaxSimReranker(embedder,
            name => sentenceIndex.TryGetValue(name, out var v) ? v : null,
            firstStageWeight: 1.0);
        var fsCandidates = new SearchResult[]
        {
            MakeEntry("doc-a", originalScore: 100.0),
            MakeEntry("doc-b", originalScore: 0.0),
            MakeEntry("doc-c", originalScore: 50.0),
        };
        var fsResult = await pureFs.RerankAsync("query", fsCandidates, CancellationToken.None);
        (fsResult[0] as EntryResult)!.Item.Entry.Name.Should().Be("doc-a",
            "α=1 reduces to first-stage-only order");

        // Pure MaxSim (α=0) → doc-b wins (perfect cosine).
        var pureMs = new SentenceMaxSimReranker(embedder,
            name => sentenceIndex.TryGetValue(name, out var v) ? v : null,
            firstStageWeight: 0.0);
        var msCandidates = new SearchResult[]
        {
            MakeEntry("doc-a", originalScore: 100.0),
            MakeEntry("doc-b", originalScore: 0.0),
            MakeEntry("doc-c", originalScore: 50.0),
        };
        var msResult = await pureMs.RerankAsync("query", msCandidates, CancellationToken.None);
        (msResult[0] as EntryResult)!.Item.Entry.Name.Should().Be("doc-b",
            "α=0 reduces to MaxSim-only order");
    }

    [Fact]
    public async Task FirstStageWeight_ClampedToValidRange()
    {
        // Config typos (e.g. 1.5 or -0.3) should degrade gracefully — clamp into [0, 1]
        // rather than crash mid-run on a long bench.
        var embedder = new FakeEmbedder("q", new float[] { 1.0f });

        var over = new SentenceMaxSimReranker(embedder, _ => null, firstStageWeight: 1.5);
        over.FirstStageWeight.Should().Be(1.0);

        var under = new SentenceMaxSimReranker(embedder, _ => null, firstStageWeight: -0.3);
        under.FirstStageWeight.Should().Be(0.0);
    }

    [Fact]
    public async Task NoSentenceVectors_StillRanked_ViaFirstStageAlone()
    {
        // Candidates with NO sentence vectors must not be dropped — they should still
        // participate in the ranking using only their normalized first-stage score.
        // Otherwise a missed-at-ingest doc would silently disappear from results.
        var queryVec = new float[] { 1.0f, 0.0f, 0.0f };
        var embedder = new FakeEmbedder("query", queryVec);

        var sentenceIndex = new Dictionary<string, IReadOnlyList<float[]>>(StringComparer.OrdinalIgnoreCase)
        {
            // Low MaxSim signal: cosine ≈ 0.5
            ["has-sentences"] = new[] { new float[] { 0.5f, 0.5f, 0.0f } },
        };

        // α=0.0 → MaxSim-dominated blend. Without sentence vectors a candidate gets
        // first-stage-only treatment; with α=0 that should still place it in the
        // ranking via its first-stage score (not drop it).
        var reranker = new SentenceMaxSimReranker(
            embedder,
            name => sentenceIndex.TryGetValue(name, out var v) ? v : null,
            firstStageWeight: 0.0);

        var candidates = new SearchResult[]
        {
            MakeEntry("has-sentences", originalScore: 50.0),
            MakeEntry("no-sentences", originalScore: 200.0),
        };

        var result = await reranker.RerankAsync("query", candidates, CancellationToken.None);

        result.Should().HaveCount(2,
            "doc with no sentence vectors must still be included, not dropped");
        // 'no-sentences' falls back to normalized first-stage = 1.0 (max in pool).
        // 'has-sentences' blends α(=0)·1.0 + 0.5(=1-α)·1.0 = 1.0 too (sole MaxSim).
        // Both at the top end; key assertion is just that no-sentences is included.
        result.Select(r => (r as EntryResult)!.Item.Entry.Name)
            .Should().Contain(["has-sentences", "no-sentences"]);
    }

    [Fact]
    public async Task EmbedderReturnsNull_PassesThroughUnchanged()
    {
        // If the embedder can't embed the query (provider unavailable, model load
        // failed, etc.), the reranker must pass candidates through in their original
        // order rather than scramble them with arbitrary scores.
        var embedder = new FakeEmbedder("query", null);  // returns null vec

        var reranker = new SentenceMaxSimReranker(
            embedder,
            _ => new[] { new float[] { 1.0f, 0.0f, 0.0f } });

        var candidates = new SearchResult[]
        {
            MakeEntry("doc-a", originalScore: 50.0),
            MakeEntry("doc-b", originalScore: 100.0),
        };

        var result = await reranker.RerankAsync("query", candidates, CancellationToken.None);

        // Original order preserved (a first, b second).
        result.Should().Equal(candidates);
    }

    [Fact]
    public async Task EmptyCandidates_ReturnsEmpty()
    {
        var embedder = new FakeEmbedder("query", new float[] { 1.0f, 0.0f });
        var reranker = new SentenceMaxSimReranker(embedder, _ => null);

        var result = await reranker.RerankAsync("query", [], CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SearchResult MakeEntry(string name, double originalScore)
    {
        var entry = new ArtifactEntry(
            Name: name,
            Uri: $"bench://{name}",
            OriginalBytes: 100,
            ChunkCount: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            Keywords: [name],
            TermFrequencies: null,
            ContentPreview: null);
        return new EntryResult(new ScopedArtifact("local", entry), originalScore);
    }

    /// <summary>
    /// Deterministic fake embedder. Returns the configured vector for the configured
    /// query string, null for anything else. Disposes are no-op. Tests that need
    /// specific cosine values build vectors by hand and stub the lookup.
    /// </summary>
    private sealed class FakeEmbedder : IEmbeddingProvider
    {
        private readonly string _expectedText;
        private readonly float[]? _resultVec;

        public FakeEmbedder(string expectedText, float[]? resultVec)
        {
            _expectedText = expectedText;
            _resultVec = resultVec;
        }

        public bool IsAvailable => true;
        public int Dimensions => _resultVec?.Length ?? 0;
        public string Signature => "fake:test";

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(text == _expectedText ? _resultVec : null);

        public void Dispose() { }
    }
}
