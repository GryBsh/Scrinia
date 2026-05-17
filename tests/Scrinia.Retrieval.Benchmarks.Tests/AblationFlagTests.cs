using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Verifies that ablation knobs flow end-to-end: a retriever constructed with the
/// MMR knob and a small inner-limit actually reorders results based on diversity,
/// not pure relevance. Without these tests the ablation commit could ship with
/// the flags wired but the knob having no effect.
/// </summary>
public class AblationFlagTests : IAsyncLifetime
{
    private string _root = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "scrinia-retbench-ablation-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Mmr_with_low_lambda_diversifies_away_from_dominant_topic()
    {
        // 4 docs about the same topic (kubernetes) + 1 unrelated. Query matches the
        // dominant topic; pure relevance returns the 4 kubernetes docs at the top.
        // MMR with λ low should push the unrelated doc into the top-2 because it
        // diversifies away from the already-selected dominant cluster.
        await using var r = new ScriniaRetriever(
            _root,
            diversityLambda: 0.0,         // pure diversity
            diversityInnerLimit: 10);

        await r.IngestAsync(new HaystackDoc("k1", "kubernetes deployment manifest yaml", null));
        await r.IngestAsync(new HaystackDoc("k2", "kubernetes pod kubernetes service kubernetes", null));
        await r.IngestAsync(new HaystackDoc("k3", "kubernetes operator pattern crd", null));
        await r.IngestAsync(new HaystackDoc("k4", "kubernetes cluster autoscaler kubernetes", null));
        await r.IngestAsync(new HaystackDoc("u1", "kubernetes is mentioned but mostly pizza recipes", null));

        var hits = await r.RetrieveAsync("kubernetes deployment", topK: 2);

        hits.Should().HaveCount(2);
        hits.Select(h => h.Id).Should().Contain("u1",
            because: "with λ=0 MMR maximises diversity from already-selected; the pizza doc " +
                     "looks lexically least like the other kubernetes-heavy docs");
    }

    [Fact]
    public async Task Mmr_lambda_1_returns_relevance_order()
    {
        // With λ=1.0, MMR degenerates to pure relevance ranking. Same input as above
        // but the output should be the relevance-sorted top-2, which would be two
        // kubernetes docs (no diversity bonus).
        await using var r = new ScriniaRetriever(
            _root,
            diversityLambda: 1.0,
            diversityInnerLimit: 10);

        await r.IngestAsync(new HaystackDoc("k1", "kubernetes deployment manifest yaml", null));
        await r.IngestAsync(new HaystackDoc("k2", "kubernetes pod kubernetes service kubernetes", null));
        await r.IngestAsync(new HaystackDoc("k3", "kubernetes operator pattern crd", null));
        await r.IngestAsync(new HaystackDoc("u1", "kubernetes is mentioned but mostly pizza recipes", null));

        var hits = await r.RetrieveAsync("kubernetes deployment", topK: 2);

        hits.Should().HaveCount(2);
        hits.All(h => h.Id.StartsWith('k')).Should().BeTrue(
            because: "λ=1.0 is pure relevance; the kubernetes-heavy docs outrank the pizza doc");
    }

    [Fact]
    public async Task Recency_alpha_pushes_recent_doc_above_stale_equal_match()
    {
        // Two docs with identical relevance — the one with a more recent timestamp
        // should rank higher when α_recency is on.
        var ranker = new Scrinia.Core.Search.RankerOptions(
            AlphaRelevance: 1.0,
            AlphaRecency: 1.0,
            AlphaImportance: 0.0,
            TauDays: 30.0);

        await using var r = new ScriniaRetriever(_root, rankerOptions: ranker);

        var now = DateTimeOffset.UtcNow;
        await r.IngestAsync(new HaystackDoc("old", "kubernetes pod deployment", now.AddDays(-365)));
        await r.IngestAsync(new HaystackDoc("new", "kubernetes pod deployment", now));

        var hits = await r.RetrieveAsync("kubernetes pod deployment", topK: 2);

        hits.Should().HaveCount(2);
        hits[0].Id.Should().Be("new",
            because: "with α_recency=1, τ=30d, the year-old doc is heavily decayed (~exp(-12.2)) vs today's doc");
    }

    [Fact]
    public async Task Recency_alpha_zero_ties_resolve_by_creation_date_tiebreaker()
    {
        // With α_recency=0 the additive recency term contributes nothing; the
        // ranker's stable tie-breaker (creation-date desc) still pushes the
        // newer-created doc to the top on score ties.
        var ranker = new Scrinia.Core.Search.RankerOptions(
            AlphaRelevance: 1.0,
            AlphaRecency: 0.0,
            AlphaImportance: 0.0);

        await using var r = new ScriniaRetriever(_root, rankerOptions: ranker);

        var now = DateTimeOffset.UtcNow;
        await r.IngestAsync(new HaystackDoc("old", "kubernetes pod deployment", now.AddDays(-365)));
        await r.IngestAsync(new HaystackDoc("new", "kubernetes pod deployment", now));

        var hits = await r.RetrieveAsync("kubernetes pod deployment", topK: 2);

        hits.Should().HaveCount(2);
        // Both have identical relevance and α_recency=0, but the stable secondary
        // sort key (CreatedAt desc) still places `new` first.
        hits[0].Id.Should().Be("new");
    }
}
