using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Search;

/// <summary>
/// Tests for the additive ranker composition introduced alongside <see cref="RankerOptions"/>.
/// The new shape is <c>α_relevance·relevance + α_recency·exp(-Δt/τ)·scale +
/// α_importance·(imp/10)·scale</c>, replacing the previous multiplicative
/// "relevance × (1 + small linear recency boost)" formula.
/// </summary>
public sealed class RankerCompositionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ScopedArtifact MakeEntry(
        string name,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        int? importance = null,
        string[]? keywords = null,
        Dictionary<string, int>? tf = null)
    {
        return new ScopedArtifact("local", new ArtifactEntry(
            Name: name,
            Uri: "",
            OriginalBytes: 100,
            ChunkCount: 1,
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow,
            Description: $"desc for {name}",
            Tags: null,
            ContentPreview: null,
            Keywords: keywords ?? [name],
            TermFrequencies: tf ?? new Dictionary<string, int> { [name] = 5 },
            UpdatedAt: updatedAt,
            Importance: importance));
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

    // ── Importance term shape ────────────────────────────────────────────────

    [Fact]
    public void ImportanceTerm_NullImportance_UsesNeutralMidpoint()
    {
        // Memories without an LLM-scored value should rank as "average importance",
        // not as zero — this preserves graceful degradation when no Tier 2 LLM is
        // configured (the common case for fresh installs).
        double term = WeightedFieldScorer.ComputeImportanceTerm(importance: null, neutralImportance: 5);
        term.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void ImportanceTerm_MaxImportance_IsOne()
    {
        WeightedFieldScorer.ComputeImportanceTerm(10, 5).Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void ImportanceTerm_MinImportance_IsOneTenth()
    {
        WeightedFieldScorer.ComputeImportanceTerm(1, 5).Should().BeApproximately(0.1, 0.01);
    }

    [Fact]
    public void ImportanceTerm_OutOfRange_Clamps()
    {
        // The LLM should respond 1-10 but might emit 0 / negative / >10 on bad parses;
        // clamping protects the ranker from amplifying garbage.
        WeightedFieldScorer.ComputeImportanceTerm(-3, 5).Should().BeApproximately(0.1, 0.01);
        WeightedFieldScorer.ComputeImportanceTerm(0, 5).Should().BeApproximately(0.1, 0.01);
        WeightedFieldScorer.ComputeImportanceTerm(99, 5).Should().BeApproximately(1.0, 0.01);
    }

    // ── Composition: idempotence ─────────────────────────────────────────────

    [Fact]
    public void RankerIsRelevanceOnly_WhenAlphasZeroed()
    {
        // α_recency = α_importance = 0 → only relevance contributes. Useful for the
        // BM25-only baselines we want to keep comparing against.
        var opts = new RankerOptions(AlphaRecency: 0, AlphaImportance: 0);
        var scorer = new WeightedFieldScorer(opts);

        var oldEntry = MakeEntry("oauth-flow", createdAt: DateTimeOffset.UtcNow.AddDays(-365), importance: 10);
        var newEntry = MakeEntry("oauth-flow-new", createdAt: DateTimeOffset.UtcNow, importance: 1);

        var results = scorer.SearchAll("oauth", [oldEntry, newEntry], [], 10);

        // Both should match "oauth" via keyword/name. With alphas zeroed, the ancient
        // entry's importance=10 must not outscore the new entry's relevance, and vice
        // versa — score differences should come only from name/BM25 signal.
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

    // ── Composition: importance contribution ─────────────────────────────────

    [Fact]
    public void Importance_HighScoreOutranksLowScore_OnEqualRelevance()
    {
        var scorer = new WeightedFieldScorer();
        var importantEntry = MakeEntry("alpha", importance: 10);
        var trivialEntry = MakeEntry("beta", importance: 1);

        importantEntry = importantEntry with { Entry = importantEntry.Entry with
        {
            Keywords = ["shared"],
            TermFrequencies = new Dictionary<string, int> { ["shared"] = 5 },
        }};
        trivialEntry = trivialEntry with { Entry = trivialEntry.Entry with
        {
            Keywords = ["shared"],
            TermFrequencies = new Dictionary<string, int> { ["shared"] = 5 },
        }};

        var results = scorer.SearchAll("shared", [importantEntry, trivialEntry], [], 10);

        results.Should().HaveCount(2);
        (results[0] as EntryResult)!.Item.Entry.Name.Should().Be("alpha",
            "importance=10 should outrank importance=1 when relevance and recency are equal");
    }

    [Fact]
    public void Importance_NullFallsBackToNeutral_NotZero()
    {
        // A memory without an importance score (no LLM configured, or scoring hasn't run
        // yet) should rank between importance=1 and importance=10 — not at the bottom.
        var scorer = new WeightedFieldScorer();
        var lowImp = MakeEntry("low", importance: 1);
        var unscored = MakeEntry("unscored", importance: null);
        var highImp = MakeEntry("high", importance: 10);

        lowImp = lowImp with { Entry = lowImp.Entry with
        {
            Keywords = ["shared"],
            TermFrequencies = new Dictionary<string, int> { ["shared"] = 5 },
        }};
        unscored = unscored with { Entry = unscored.Entry with
        {
            Keywords = ["shared"],
            TermFrequencies = new Dictionary<string, int> { ["shared"] = 5 },
        }};
        highImp = highImp with { Entry = highImp.Entry with
        {
            Keywords = ["shared"],
            TermFrequencies = new Dictionary<string, int> { ["shared"] = 5 },
        }};

        var results = scorer.SearchAll("shared", [lowImp, unscored, highImp], [], 10);

        results.Should().HaveCount(3);
        var order = results.OfType<EntryResult>().Select(r => r.Item.Entry.Name).ToList();
        order.IndexOf("high").Should().BeLessThan(order.IndexOf("unscored"));
        order.IndexOf("unscored").Should().BeLessThan(order.IndexOf("low"));
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

    [Fact]
    public void CustomImportanceScale_Zero_RemovesImportanceContribution()
    {
        var noImportance = new WeightedFieldScorer(
            new RankerOptions(AlphaImportance: 0));

        var highImp = MakeEntry("alpha", importance: 10,
            keywords: ["shared"], tf: new Dictionary<string, int> { ["shared"] = 5 });
        var lowImp = MakeEntry("beta", importance: 1,
            keywords: ["shared"], tf: new Dictionary<string, int> { ["shared"] = 5 });

        var results = noImportance.SearchAll("shared", [highImp, lowImp], [], 10);

        // Scores should be effectively equal — ordering falls to tie-break (newest first).
        // Both entries have UtcNow CreatedAt, so the order is implementation-defined but
        // their score gap must be ~0.
        results.Should().HaveCount(2);
        Math.Abs(results[0].Score - results[1].Score).Should().BeLessThan(0.001,
            "with α_importance=0 the high/low importance entries should score identically");
    }
}
