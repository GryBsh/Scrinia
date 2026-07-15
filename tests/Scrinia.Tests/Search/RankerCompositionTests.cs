using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Search;

/// <summary>
/// Tests for the additive ranker composition introduced alongside <see cref="RankerOptions"/>.
/// The shape is <c>α_relevance·relevance + α_recency·exp(-Δt/τ)·scale</c>, replacing the
/// previous multiplicative "relevance × (1 + small linear recency boost)" formula.
/// </summary>
public sealed class RankerCompositionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ScopedArtifact MakeEntry(
        string name,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        string[]? keywords = null,
        Dictionary<string, int>? tf = null)
    {
        return new ScopedArtifact("local", new ArtifactEntry(
            Name: name,
            Uri: "",
            OriginalBytes: 100,
            ChunkCount: 1,
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow,
            ContentPreview: null,
            Keywords: keywords ?? [name],
            TermFrequencies: tf ?? new Dictionary<string, int> { [name] = 5 },
            UpdatedAt: updatedAt));
    }

    // ── Recency term shape ───────────────────────────────────────────────────

    [Fact]
    public void RecencyTerm_AtZeroDays_IsOne()
    {
        double term = WeightedFieldScorer.ComputeRecencyTerm(DateTimeOffset.UtcNow, tauDays: 14);
        term.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void RecencyTerm_AtTauDays_IsApproxOneOverE()
    {
        // exp(-1) ≈ 0.3679 — the characteristic value at one time-constant.
        double term = WeightedFieldScorer.ComputeRecencyTerm(
            DateTimeOffset.UtcNow.AddDays(-14), tauDays: 14);
        term.Should().BeApproximately(Math.Exp(-1), 0.01);
    }

    [Fact]
    public void RecencyTerm_FarPast_AsymptoticallyZero()
    {
        // 100 days at τ=14 → exp(-7.14) ≈ 0.0008 — effectively zero for ranking purposes.
        double term = WeightedFieldScorer.ComputeRecencyTerm(
            DateTimeOffset.UtcNow.AddDays(-100), tauDays: 14);
        term.Should().BeLessThan(0.01);
    }

    [Fact]
    public void RecencyTerm_FutureDated_ClampsToToday()
    {
        // Clock skew across cloud-sync setups can produce future-dated entries; they should
        // not artificially boost above "today".
        double future = WeightedFieldScorer.ComputeRecencyTerm(
            DateTimeOffset.UtcNow.AddDays(7), tauDays: 14);
        future.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void RecencyTerm_ZeroTau_DegradesToZero()
    {
        // Pathological config — guards against accidental NaN/Inf if a user sets tau=0.
        double term = WeightedFieldScorer.ComputeRecencyTerm(
            DateTimeOffset.UtcNow.AddDays(-1), tauDays: 0);
        term.Should().Be(0);
    }

    // ── Composition: idempotence ─────────────────────────────────────────────

    [Fact]
    public void RankerIsRelevanceOnly_WhenAlphaRecencyZeroed()
    {
        // α_recency = 0 → only relevance contributes. Useful for the BM25-only baselines
        // we want to keep comparing against.
        var opts = new RankerOptions(AlphaRecency: 0);
        var scorer = new WeightedFieldScorer(opts);

        var oldEntry = MakeEntry("oauth-flow", createdAt: DateTimeOffset.UtcNow.AddDays(-365));
        var newEntry = MakeEntry("oauth-flow-new", createdAt: DateTimeOffset.UtcNow);

        var results = scorer.SearchAll("oauth", [oldEntry, newEntry], [], 10);

        // Both should match "oauth" via keyword/name. With α_recency zeroed, score
        // differences should come only from name/BM25 signal.
        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.Score.Should().BeGreaterThan(0));
    }

    // ── Composition: recency contribution ────────────────────────────────────

    [Fact]
    public void Recency_BreaksTiesInFavorOfNewerEntry()
    {
        // Two entries with identical name + TF profile; the only differentiator is age.
        // Pre-change ranker would put them effectively tied (linear +10% boost is noise);
        // exp-decay with τ=14 days should put the fresh one well ahead.
        var scorer = new WeightedFieldScorer(); // defaults: α_recency=1.0, τ=14
        var freshEntry = MakeEntry("alpha", createdAt: DateTimeOffset.UtcNow);
        var staleEntry = MakeEntry("beta", createdAt: DateTimeOffset.UtcNow.AddDays(-90));

        // Use identical content so relevance is the same — only recency differs.
        freshEntry = freshEntry with { Entry = freshEntry.Entry with
        {
            Keywords = ["shared", "term"],
            TermFrequencies = new Dictionary<string, int> { ["shared"] = 5, ["term"] = 3 },
        }};
        staleEntry = staleEntry with { Entry = staleEntry.Entry with
        {
            Keywords = ["shared", "term"],
            TermFrequencies = new Dictionary<string, int> { ["shared"] = 5, ["term"] = 3 },
        }};

        var results = scorer.SearchAll("shared", [freshEntry, staleEntry], [], 10);

        results.Should().HaveCount(2);
        (results[0] as EntryResult)!.Item.Entry.Name.Should().Be("alpha",
            "today's entry should outrank a 90-day-old entry with identical relevance");
    }

    [Fact]
    public void Recency_DoesNotPromoteZeroRelevance()
    {
        // The relevance > 0 gate must hold — a fresh memory with no text match should
        // not surface just because it's recent. This preserves "search must match" semantics.
        var scorer = new WeightedFieldScorer();
        var matchingButOld = MakeEntry("oauth-flow", createdAt: DateTimeOffset.UtcNow.AddDays(-200),
            keywords: ["oauth"],
            tf: new Dictionary<string, int> { ["oauth"] = 5 });
        var freshButUnrelated = MakeEntry("bicycle-notes", createdAt: DateTimeOffset.UtcNow,
            keywords: ["bicycle"],
            tf: new Dictionary<string, int> { ["bicycle"] = 5 });

        var results = scorer.SearchAll("oauth", [matchingButOld, freshButUnrelated], [], 10);

        results.Should().HaveCount(1, "only the entry that matches the query should appear");
        (results[0] as EntryResult)!.Item.Entry.Name.Should().Be("oauth-flow");
    }

    // ── Custom RankerOptions threading ───────────────────────────────────────

    [Fact]
    public void CustomTau_NarrowerHalfLife_PenalizesWeekOldEntriesMore()
    {
        var sharpDecay = new WeightedFieldScorer(new RankerOptions(TauDays: 2));
        var slowDecay = new WeightedFieldScorer(new RankerOptions(TauDays: 30));

        var entries = new[]
        {
            MakeEntry("today", createdAt: DateTimeOffset.UtcNow,
                keywords: ["shared"], tf: new Dictionary<string, int> { ["shared"] = 5 }),
            MakeEntry("week-old", createdAt: DateTimeOffset.UtcNow.AddDays(-7),
                keywords: ["shared"], tf: new Dictionary<string, int> { ["shared"] = 5 }),
        };

        var sharpResults = sharpDecay.SearchAll("shared", entries, [], 10);
        var slowResults = slowDecay.SearchAll("shared", entries, [], 10);

        double sharpGap = sharpResults[0].Score - sharpResults[1].Score;
        double slowGap = slowResults[0].Score - slowResults[1].Score;

        sharpGap.Should().BeGreaterThan(slowGap,
            "shorter τ produces a sharper penalty on older entries");
    }

    // ── Fusion mode: RRF ─────────────────────────────────────────────────────

    [Fact]
    public void RrfFusion_LiftsEmbeddingWinnerOverBm25Magnitude()
    {
        // Core RRF promise: when embeddings rank a doc highest but BM25 magnitude
        // dwarfs the cosine signal, sum-fusion buries the embedding winner; RRF
        // surfaces it by scoring on rank, not raw magnitude.
        //
        // Setup:
        //   doc-A — strong BM25 hit ("shared" appears many times), zero embedding signal.
        //   doc-B — moderate BM25 hit ("shared" once), but strong embedding signal.
        // Under Sum: A wins (BM25 magnitude × 5 swamps the cosine × 50).
        // Under Rrf: B wins (B is rank-1 in embeddings AND rank-2 in BM25; A is rank-1
        // in BM25 only, with no embedding contribution).
        var docA = MakeEntry("doc-a",
            keywords: ["shared"],
            tf: new Dictionary<string, int> { ["shared"] = 20 });
        var docB = MakeEntry("doc-b",
            keywords: ["shared"],
            tf: new Dictionary<string, int> { ["shared"] = 1 });

        // Supplemental keyed as "{scope}|{name}" — see WeightedFieldScorer.GetSupplemental.
        // Magnitudes mirror what the bench passes (cosine × 50.0).
        var supplemental = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["local|doc-a"] = 0.0,
            ["local|doc-b"] = 45.0, // strong semantic match
        };

        var sumScorer = new WeightedFieldScorer(
            new RankerOptions(AlphaRecency: 0, Fusion: FusionMode.Sum));
        var rrfScorer = new WeightedFieldScorer(
            new RankerOptions(AlphaRecency: 0, Fusion: FusionMode.Rrf));

        var sumResults = sumScorer.SearchAll("shared", [docA, docB], [], 10, supplemental);
        var rrfResults = rrfScorer.SearchAll("shared", [docA, docB], [], 10, supplemental);

        sumResults.Should().HaveCount(2);
        rrfResults.Should().HaveCount(2);

        (sumResults[0] as EntryResult)!.Item.Entry.Name.Should().Be("doc-a",
            "sum fusion: BM25 magnitude dominates the cosine signal");
        (rrfResults[0] as EntryResult)!.Item.Entry.Name.Should().Be("doc-b",
            "RRF: doc-b is rank-1 in embeddings and rank-2 in BM25 → higher fused score");
    }

    [Fact]
    public void RrfFusion_DefaultsToSum_WhenNotSpecified()
    {
        // Default RankerOptions() must continue to use Sum fusion — RRF is opt-in.
        // Guards against accidental regression of production search behavior.
        var opts = new RankerOptions();
        opts.Fusion.Should().Be(FusionMode.Sum);
    }

    [Fact]
    public void RrfFusion_ZeroSignalCells_AreExcluded()
    {
        // Cells with zero score across ALL three sources must not surface, even though
        // they technically have a rank in each source's sort. The "if score > 0 →
        // contribute, else 0" gate inside RrfScore enforces this.
        var matching = MakeEntry("oauth-flow",
            keywords: ["oauth"],
            tf: new Dictionary<string, int> { ["oauth"] = 5 });
        var unrelated = MakeEntry("bicycle-notes",
            keywords: ["bicycle"],
            tf: new Dictionary<string, int> { ["bicycle"] = 5 });

        var rrfScorer = new WeightedFieldScorer(
            new RankerOptions(AlphaRecency: 0, Fusion: FusionMode.Rrf));

        var results = rrfScorer.SearchAll("oauth", [matching, unrelated], [], 10);

        results.Should().HaveCount(1,
            "the unrelated entry has zero score in all three sources and must not surface");
        (results[0] as EntryResult)!.Item.Entry.Name.Should().Be("oauth-flow");
    }

    [Fact]
    public void RrfFusion_OnlyOneSourceMatches_StillScores()
    {
        // A doc with signal in only one source (e.g., only embeddings match) must still
        // surface — RRF should not require all three sources to fire. Tests the
        // partial-contribution path through RrfScore.
        var bm25Only = MakeEntry("bm25-only",
            keywords: ["shared"],
            tf: new Dictionary<string, int> { ["shared"] = 5 });
        var embedOnly = MakeEntry("embed-only",
            keywords: ["unrelated"],
            tf: new Dictionary<string, int> { ["unrelated"] = 5 });

        var supplemental = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["local|bm25-only"] = 0.0,
            ["local|embed-only"] = 40.0,
        };

        var rrfScorer = new WeightedFieldScorer(
            new RankerOptions(AlphaRecency: 0, Fusion: FusionMode.Rrf));

        var results = rrfScorer.SearchAll("shared", [bm25Only, embedOnly], [], 10, supplemental);

        results.Should().HaveCount(2,
            "embed-only matches via supplemental signal and must surface alongside bm25-only");
        results.Select(r => (r as EntryResult)!.Item.Entry.Name)
            .Should().Contain(["bm25-only", "embed-only"]);
    }
}
