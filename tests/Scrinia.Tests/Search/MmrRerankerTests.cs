using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Search;

/// <summary>
/// Tests for <see cref="MmrReranker"/>. Verifies the diversity rerank correctly handles
/// the documented failure pattern (one session flooding top-K), respects the λ knob
/// at both extremes, and degrades gracefully on edge cases (legacy entries with null
/// TF, fewer candidates than k, all-zero scores).
/// </summary>
public sealed class MmrRerankerTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static EntryResult MakeResult(string name, double score, Dictionary<string, int>? tf = null)
    {
        var entry = new ArtifactEntry(
            Name: name,
            Uri: "",
            OriginalBytes: 0,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: $"desc for {name}",
            TermFrequencies: tf);
        return new EntryResult(new ScopedArtifact("local", entry), score);
    }

    private static Dictionary<string, int> Tf(params (string term, int count)[] pairs)
        => pairs.ToDictionary(p => p.term, p => p.count);

    // ── Basics ───────────────────────────────────────────────────────────────

    [Fact]
    public void Rerank_EmptyInput_ReturnsEmpty()
    {
        MmrReranker.Rerank([], k: 3, lambda: 0.6).Should().BeEmpty();
    }

    [Fact]
    public void Rerank_KZero_ReturnsEmpty()
    {
        var pool = new List<SearchResult> { MakeResult("a", 10) };
        MmrReranker.Rerank(pool, k: 0, lambda: 0.6).Should().BeEmpty();
    }

    [Fact]
    public void Rerank_PoolSmallerThanK_ReturnsAllSortedByRelevance()
    {
        var pool = new List<SearchResult>
        {
            MakeResult("low", 10),
            MakeResult("mid", 50, Tf(("foo", 5))),
            MakeResult("high", 100, Tf(("bar", 5))),
        };

        var result = MmrReranker.Rerank(pool, k: 10, lambda: 0.6);

        result.Should().HaveCount(3);
        result.Select(r => (r as EntryResult)!.Item.Entry.Name).Should()
            .Equal(["high", "mid", "low"]);
    }

    [Fact]
    public void Rerank_AllZeroScores_ReturnsFirstK()
    {
        var pool = new List<SearchResult>
        {
            MakeResult("a", 0), MakeResult("b", 0), MakeResult("c", 0),
        };
        var result = MmrReranker.Rerank(pool, k: 2, lambda: 0.6);
        result.Should().HaveCount(2);
    }

    // ── Lambda extremes ──────────────────────────────────────────────────────

    [Fact]
    public void Rerank_LambdaOne_ProducesPureRelevanceOrdering()
    {
        // λ = 1 means similarity is ignored — top-K should be the highest-scoring K
        // regardless of how similar they are to each other. This is the pre-MMR
        // behaviour and the escape hatch for users who want relevance-only.
        var pool = new List<SearchResult>
        {
            MakeResult("session-1", 100, Tf(("oauth", 5), ("flow", 3))),
            MakeResult("session-2", 95,  Tf(("oauth", 4), ("flow", 3))), // near-clone of session-1
            MakeResult("findings-different", 50, Tf(("foo", 5))),
        };

        var result = MmrReranker.Rerank(pool, k: 2, lambda: 1.0);
        var names = result.Select(r => (r as EntryResult)!.Item.Entry.Name).ToList();

        names.Should().Equal(["session-1", "session-2"],
            because: "λ=1 should pick top two by relevance even though they're near-duplicates");
    }

    [Fact]
    public void Rerank_LambdaZero_ProducesPureDiversityAfterFirstPick()
    {
        // λ = 0 means relevance is ignored after the first pick — the second pick is
        // the candidate least similar to the first (regardless of its score).
        var pool = new List<SearchResult>
        {
            MakeResult("anchor", 100, Tf(("oauth", 5), ("flow", 3))),
            MakeResult("similar", 90, Tf(("oauth", 4), ("flow", 3))), // near-clone of anchor
            MakeResult("diverse-low", 10, Tf(("bicycle", 5))), // unrelated, low relevance
        };

        var result = MmrReranker.Rerank(pool, k: 2, lambda: 0.0);
        var names = result.Select(r => (r as EntryResult)!.Item.Entry.Name).ToList();

        // First pick is "anchor" (max relevance breaks the empty-selected-set tie).
        // Second pick under λ=0 is whichever candidate has the lowest similarity to "anchor"
        // — "diverse-low" wins despite its much lower relevance.
        names[0].Should().Be("anchor");
        names[1].Should().Be("diverse-low",
            "λ=0 picks the most-diverse second candidate even though it has lower relevance");
    }

    // ── The flood-breaking property ──────────────────────────────────────────

    [Fact]
    public void Rerank_BreaksSingleSourceFlood_AtModerateLambda()
    {
        // The exact failure pattern the research synthesis called out: one chatty
        // session produces multiple top hits because its memories share vocabulary.
        // Without MMR (or with λ=1) top-3 is all from that session. With λ=0.6 at
        // least one slot must be a diverse memory.
        var pool = new List<SearchResult>
        {
            MakeResult("session/oauth-day1", 100, Tf(("oauth", 8), ("token", 6), ("flow", 4))),
            MakeResult("session/oauth-day2", 95,  Tf(("oauth", 7), ("token", 5), ("flow", 5))),
            MakeResult("session/oauth-day3", 90,  Tf(("oauth", 6), ("token", 7), ("flow", 4))),
            MakeResult("session/oauth-day4", 85,  Tf(("oauth", 6), ("token", 6), ("flow", 3))),
            MakeResult("findings/oauth-final-design", 70, Tf(("design", 5), ("oauth", 3))),
            MakeResult("skill/oauth-token-rotation",  60, Tf(("rotation", 6), ("oauth", 2))),
        };

        var result = MmrReranker.Rerank(pool, k: 3, lambda: 0.6);
        var names = result.Select(r => (r as EntryResult)!.Item.Entry.Name).ToList();

        names.Should().HaveCount(3);
        int sessionPicks = names.Count(n => n.StartsWith("session/", StringComparison.Ordinal));
        sessionPicks.Should().BeLessThan(3,
            "MMR at λ=0.6 should break the flood — top-3 shouldn't be all session memories");
        // The highest-relevance pick is always first regardless of similarity.
        names[0].Should().Be("session/oauth-day1");
    }

    // ── Robustness ───────────────────────────────────────────────────────────

    [Fact]
    public void Rerank_NullTfEntries_TreatedAsMaximallyDiverse()
    {
        // Legacy v2 entries have null TermFrequencies. Cosine similarity falls back to 0
        // (max diversity) so MMR neither promotes nor demotes them specially — they should
        // win or lose purely on relevance.
        var pool = new List<SearchResult>
        {
            MakeResult("modern-1", 100, Tf(("foo", 5))),
            MakeResult("modern-2", 90,  Tf(("foo", 4))),
            MakeResult("legacy-v2", 50, tf: null),
        };

        var result = MmrReranker.Rerank(pool, k: 2, lambda: 0.6);
        var names = result.Select(r => (r as EntryResult)!.Item.Entry.Name).ToList();

        // First pick is the highest-relevance entry. With λ=0.6 and a near-duplicate
        // (modern-2 has sim ≈ 1 to modern-1) vs the legacy entry (sim=0), the legacy
        // entry's MMR score: 0.6 · 0.5 − 0.4 · 0 = 0.30. modern-2's MMR score:
        // 0.6 · 0.9 − 0.4 · 1.0 = 0.14. Legacy wins.
        names[0].Should().Be("modern-1");
        names[1].Should().Be("legacy-v2",
            "null TF makes a candidate look diverse to everything; MMR favours it here");
    }

    [Fact]
    public void Rerank_TfCosine_IdenticalEntriesGetMaxSimilarity()
    {
        // Two memories with identical TF profiles are maximally similar (cos = 1).
        // MMR with strong-diversity λ should pick at most one of them in top-2.
        var sharedTf = Tf(("alpha", 5), ("beta", 3));
        var pool = new List<SearchResult>
        {
            MakeResult("twin-a", 100, sharedTf),
            MakeResult("twin-b", 99,  new Dictionary<string, int>(sharedTf)), // duplicate dict
            MakeResult("solo",   50,  Tf(("gamma", 4))),
        };

        var result = MmrReranker.Rerank(pool, k: 2, lambda: 0.3);
        var names = result.Select(r => (r as EntryResult)!.Item.Entry.Name).ToList();

        names.Should().Contain("twin-a", "highest-relevance pick is always first");
        names.Should().NotContain("twin-b",
            "with λ=0.3 (diversity-favoring), the duplicate of twin-a should not appear in top-2");
        names.Should().Contain("solo");
    }

    [Fact]
    public void Rerank_LambdaOutOfRange_Clamped()
    {
        // Out-of-range λ is a config foot-gun (user typos). Don't crash; clamp.
        var pool = new List<SearchResult>
        {
            MakeResult("a", 10, Tf(("x", 5))),
            MakeResult("b", 5,  Tf(("y", 5))),
        };

        Action overOne = () => MmrReranker.Rerank(pool, 2, lambda: 5.0);
        Action negative = () => MmrReranker.Rerank(pool, 2, lambda: -3.0);

        overOne.Should().NotThrow();
        negative.Should().NotThrow();
    }
}
