using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// What a compaction does to the memory's keywords, end to end through the MCP tools.
///
/// <para>Compaction is a whole-memory rewrite, so it re-derives — and re-derivation is where a
/// curated keyword can quietly stop being curated. Two failures are possible and both were real at
/// some point in this branch's history:</para>
/// <list type="number">
/// <item>Handing the entry's whole persisted keyword list back in as input. That list is the MERGE
/// of the agent's with the auto-extracted terms, so re-entering it promotes every auto term to
/// agent weight (+5 instead of +2) on every compaction, compounding — D-01, the mistake
/// <c>MemoryWrite</c>'s shape exists to make hard.</item>
/// <item>Handing back nothing. Safe for (1), but it drops a curated keyword outright when the text
/// that carried it is in a chunk the compaction discards, and demotes the rest to auto weight.</item>
/// </list>
/// <para>The answer is D-29's first option, not its last: hand back the subset that is PROVABLY the
/// agent's, recovered by subtracting what the pre-compact content itself yields
/// (<see cref="MemoryDerivation.CuratedKeywords"/>). These tests pin both halves of what that
/// buys — membership and weight — and the third names the limit, so nobody reads the first two as
/// promising more than they do.</para>
/// </summary>
public sealed class CompactCuratedKeywordTests
{
    private static ScriniaMcpTools Tools() => new();

    private static ArtifactEntry Entry(string name) =>
        MemoryStoreContext.Current!.LoadIndex().Single(e => e.Name == name);

    /// <summary>
    /// The membership half. A curated keyword whose text appears nowhere in the memory survives a
    /// TRUNCATING compaction and keeps its agent weight — with no keywords handed back in, it
    /// vanished from the entry entirely, taking its +5 with it.
    /// </summary>
    [Fact]
    public async Task Compact_KeepsACuratedKeywordThatTheSurvivingTextDoesNotContain()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(
            ["the first chunk is about widgets", "the second chunk is about gadgets"],
            "compact-curated",
            keywords: ["portcullis"]);

        var before = Entry("compact-curated");
        before.Keywords.Should().Contain("portcullis");
        before.TermFrequencies!["portcullis"].Should().Be(5,
            because: "precondition: no occurrences in the text, so this is the agent boost alone");

        // keepRecent: 1 discards the first chunk — a genuinely truncating compaction.
        await Tools().Compact("compact-curated", keepRecent: 1);

        var after = Entry("compact-curated");
        after.Keywords.Should().Contain("portcullis",
            because: "a curated keyword is the agent's data, not something re-derivable from the " +
                     "text that happens to be left");
        after.TermFrequencies!["portcullis"].Should().Be(5,
            because: "it is still agent-supplied after the rewrite — surviving as an auto term at " +
                     "+2 would be the same loss in a quieter form");
    }

    /// <summary>
    /// The weight half. A curated keyword the surviving text DOES contain keeps agent weight rather
    /// than being demoted to auto — provided it is one the content does not itself yield as a
    /// keyword, which is the condition under which provenance is recoverable at all.
    /// <para>The filler makes that condition real: <c>AnalyzeText</c> takes the top 25 terms by
    /// frequency, so a single-occurrence term ranks below twenty-five twice-occurring ones and is
    /// not an auto keyword — while still being present in the text, which is the case this test
    /// exists to distinguish from the one above.</para>
    /// </summary>
    [Fact]
    public async Task Compact_KeepsAgentWeightForACuratedKeywordThatSurvivesInTheText()
    {
        using var scope = new TestHelpers.StoreScope();

        string filler = string.Join(" ",
            Enumerable.Range(0, 25).Select(i => $"fillerword{i} fillerword{i}"));

        // The leading space is load-bearing: chunks are CONCATENATED for text analysis, with no
        // separator, so "…fillerword24" + "harbour…" would tokenize as one glued term and the
        // keyword would have zero occurrences — the wrong precondition for this test.
        await Tools().Remember(
            [filler, " harbour " + filler],
            "compact-weight",
            keywords: ["harbour"]);

        var before = Entry("compact-weight");
        before.TermFrequencies!["harbour"].Should().Be(6,
            because: "precondition: one occurrence plus the +5 agent boost — it is curated AND " +
                     "present in the text");

        await Tools().Compact("compact-weight");

        var after = Entry("compact-weight");
        after.Keywords.Should().Contain("harbour");
        after.TermFrequencies!["harbour"].Should().Be(6,
            because: "the compaction kept every word of this memory, so demoting the keyword to " +
                     "auto weight (+2, i.e. 3) would be a ranking change made by a no-op rewrite");
    }

    /// <summary>
    /// The recovery subtracts against the memory's content BEFORE the rewrite, not against what
    /// survives it — and the difference is soundness, not tidiness.
    ///
    /// <para>A term that only ever appeared in a chunk the compaction discards is absent from the
    /// surviving text, so subtracting against THAT text would "recover" it as curated and promote
    /// an auto-extracted term from +2 to +5 — the exact D-01 mistake the recovery exists to avoid,
    /// reintroduced by the recovery itself. Nothing else in the suite notices the difference,
    /// because every other case here uses a keyword the agent really did supply.</para>
    /// </summary>
    [Fact]
    public async Task Compact_DoesNotPromoteATermItOnlyLostBecauseAChunkWasDropped()
    {
        using var scope = new TestHelpers.StoreScope();

        // No agent keywords at all: every keyword on this entry is auto-extracted, so anything
        // that comes back curated came back wrongly.
        await Tools().Remember(
            ["a chunk mentioning zeppelins", " a chunk mentioning bicycles"],
            "compact-provenance");

        Entry("compact-provenance").Keywords.Should().Contain("zeppelins",
            because: "precondition: it was auto-extracted from the chunk about to be dropped");

        await Tools().Compact("compact-provenance", keepRecent: 1);

        var after = Entry("compact-provenance");
        after.Keywords.Should().NotContain("zeppelins",
            because: "the text that produced it is gone, and it was never the agent's to keep");
        after.TermFrequencies.Should().NotContainKey("zeppelins",
            because: "re-admitting it as a curated term would seed it at +5 — a rewrite inventing " +
                     "a ranking signal out of content it just deleted");
    }

    /// <summary>
    /// The limit, pinned so it is visible rather than discovered. A curated keyword that is ALSO
    /// one of the content's top-ranked terms is indistinguishable from an auto one once the merged
    /// list is persisted, so a compaction demotes it from +5 to +2. It keeps its place in the list
    /// — the content still yields it — but not its rank.
    /// <para>This is a property of what was stored, not of the recovery: the provenance was
    /// destroyed at write time. Closing it needs an agent-set flag on the entry, which is a
    /// storage-model change. Recorded here rather than in a planning document because
    /// <c>.superpowers/</c> is gitignored.</para>
    /// </summary>
    [Fact]
    public async Task Compact_DemotesACuratedKeywordTheContentAlsoYields_TheKnownLimit()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(
            ["a harbour full of boats", "more about the harbour"],
            "compact-limit",
            keywords: ["harbour"]);

        Entry("compact-limit").TermFrequencies!["harbour"].Should().Be(7,
            because: "precondition: two occurrences plus the +5 agent boost");

        await Tools().Compact("compact-limit");

        var after = Entry("compact-limit");
        after.Keywords.Should().Contain("harbour",
            because: "the term is still extractable from the compacted text, so it stays — this " +
                     "is a demotion, not a loss");
        after.TermFrequencies!["harbour"].Should().Be(4,
            because: "two occurrences plus the AUTO boost of +2: after the merge there is nothing " +
                     "left to prove this term was ever the agent's");
    }
}
