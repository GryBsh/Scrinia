using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// What a compaction does to the memory's keywords, end to end through the MCP tools.
///
/// <para>Compaction is a whole-memory rewrite, so it re-derives — and re-derivation is where a
/// curated keyword can quietly stop being curated. Agent keywords seed BM25 at +5 and
/// auto-extracted ones at +2 (D-13), so every rewrite has to know which is which. Three answers
/// were tried on this branch and the first two are both wrong:</para>
/// <list type="number">
/// <item><b>Hand the persisted list back as input.</b> It is the MERGE of the agent's with the
/// auto-extracted terms, so this promotes every auto term to +5, on every compaction, compounding
/// (D-01).</item>
/// <item><b>Hand back nothing.</b> Drops a curated keyword outright when the text that carried it
/// is in a discarded chunk, and demotes the rest to auto weight.</item>
/// <item><b>Guess, by subtracting the terms the content itself yields.</b> Sound-looking and not
/// sound: it recovers nothing at all for a memory of 25 or fewer distinct tokens, and it
/// over-recovers for a memory that was appended to, because the old append re-boosted the whole
/// list to agent weight.</item>
/// </list>
/// <para>The answer is that provenance is RECORDED at write time
/// (<see cref="ArtifactEntry.AgentKeywords"/>) — it was never derivable afterwards, which is why
/// every attempt to derive it failed differently. The guess survives only as a fallback for an
/// entry written before the field existed, and the two tests at the bottom cover that path
/// separately, including what it cannot do.</para>
/// </summary>
public sealed class CompactCuratedKeywordTests
{
    private static ScriniaMcpTools Tools() => new();

    private static ArtifactEntry Entry(string name) =>
        MemoryStoreContext.Current!.LoadIndex().Single(e => e.Name == name);

    /// <summary>Rewrites an entry to look like one written before <see cref="ArtifactEntry
    /// .AgentKeywords"/> existed: the keyword list is there, the record of who supplied it is not.
    /// <c>ForMetadataUpdate</c> leaves a null provenance null, so this survives the write.</summary>
    private static void ForgetProvenance(string name)
    {
        var store = MemoryStoreContext.Current!;
        store.TryUpdateEntry(name, "local", e => e with { AgentKeywords = null })
            .Should().BeTrue(because: "precondition: the entry has to exist to be aged");
        Entry(name).AgentKeywords.Should().BeNull(
            because: "precondition: this test is about the pre-provenance path");
    }

    // ── Recorded provenance (every entry written since the field landed) ─────

    /// <summary>
    /// The membership half. A curated keyword whose text appears nowhere in the memory survives a
    /// TRUNCATING compaction and keeps its agent weight.
    /// </summary>
    [Fact]
    public async Task Compact_KeepsACuratedKeywordThatTheSurvivingTextDoesNotContain()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(
            ["the first chunk is about widgets", " the second chunk is about gadgets"],
            "compact-curated",
            keywords: ["portcullis"]);

        var before = Entry("compact-curated");
        before.AgentKeywords.Should().Equal(["portcullis"],
            because: "precondition: the write recorded who supplied what");
        before.TermFrequencies!["portcullis"].Should().Be(5);

        // keepRecent: 1 discards the first chunk — a genuinely truncating compaction.
        await Tools().Compact("compact-curated", keepRecent: 1);

        var after = Entry("compact-curated");
        after.Keywords.Should().Contain("portcullis",
            because: "a curated keyword is the agent's data, not something re-derivable from the " +
                     "text that happens to be left");
        after.TermFrequencies!["portcullis"].Should().Be(5);
        after.AgentKeywords.Should().Equal(["portcullis"],
            because: "and the rewrite records it again, so the next compaction does not have to " +
                     "work it out either");
    }

    /// <summary>
    /// The weight half, and the case no subtraction could ever have handled: a curated keyword that
    /// is ALSO one of the content's own top-ranked terms. Indistinguishable from an auto keyword
    /// once merged into the list, so the guess demoted it to +2 on every compaction; the recorded
    /// provenance simply says which it was.
    /// </summary>
    [Fact]
    public async Task Compact_KeepsAgentWeightForACuratedKeywordTheContentAlsoYields()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(
            ["a harbour full of boats", " more about the harbour"],
            "compact-weight",
            keywords: ["harbour"]);

        Entry("compact-weight").TermFrequencies!["harbour"].Should().Be(7,
            because: "precondition: two occurrences plus the +5 agent boost");

        await Tools().Compact("compact-weight");

        var after = Entry("compact-weight");
        after.Keywords.Should().Contain("harbour");
        after.TermFrequencies!["harbour"].Should().Be(7,
            because: "the compaction kept every word of this memory, so demoting the keyword to " +
                     "auto weight (2 occurrences + 2 = 4) would be a ranking change made by a " +
                     "rewrite that changed no text");
    }

    /// <summary>
    /// A short memory — 25 or fewer distinct tokens — where EVERY term is one of the content's own
    /// auto keywords. The subtraction recovers literally nothing here, which is the common case
    /// rather than a corner: most memories are short.
    /// </summary>
    [Fact]
    public async Task Compact_KeepsAgentWeightInAMemoryWhoseEveryTermIsAnAutoKeyword()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(["boats and trains", " planes and boats"], "compact-short",
            keywords: ["boats"]);

        MemoryDerivation.CuratedKeywords(Entry("compact-short").Keywords,
            ["boats and trains", " planes and boats"])
            .Should().BeEmpty(because: "precondition: the fallback heuristic recovers nothing here");

        await Tools().Compact("compact-short");

        Entry("compact-short").TermFrequencies!["boats"].Should().Be(7,
            because: "two occurrences plus the +5 agent boost — the recorded provenance answers " +
                     "what the subtraction could not");
    }

    /// <summary>
    /// The Critical the review found, end to end: append, then compact.
    ///
    /// <para>The old append treated the memory's whole persisted keyword list as agent-supplied
    /// (the crude form of D-12's carry-forward), so after one append every keyword on the entry
    /// read as curated. A later compaction that trusted that reading pinned the ORIGINAL chunk's
    /// auto-extracted terms at +5 with their content deleted, crowding the surviving text's own
    /// terms out of a list capped at 30. Provenance closes it at the source: the append records
    /// what the caller actually supplied and preserves what was already recorded, so there is no
    /// promotion for the compaction to inherit.</para>
    /// </summary>
    [Fact]
    public async Task AppendThenCompact_DoesNotPinTheDroppedChunksTermsAtAgentWeight()
    {
        using var scope = new TestHelpers.StoreScope();

        string firstChunk = string.Join(" ", Enumerable.Range(0, 25).Select(i => $"aaa{i}"));
        // Each term of the appended chunk occurs three times, so the joined text's top 25 is all
        // its own — the first chunk's terms fall out of the auto set entirely.
        string appended = string.Join(" ",
            Enumerable.Range(0, 10).Select(i => $"bbb{i} bbb{i} bbb{i}"));

        await Tools().Remember([firstChunk], "compact-appended");
        await Tools().Append(" " + appended, "compact-appended");

        var afterAppend = Entry("compact-appended");
        afterAppend.AgentKeywords.Should().BeEmpty(
            because: "nobody supplied a keyword to either call — the append must not promote the " +
                     "memory's auto-extracted terms just because it carries them forward");
        afterAppend.TermFrequencies!["aaa0"].Should().Be(3,
            because: "one occurrence plus the AUTO boost of +2, not the agent +5");

        await Tools().Compact("compact-appended", keepRecent: 1);

        var after = Entry("compact-appended");
        after.Keywords.Should().NotContain("aaa0",
            because: "its content was deleted by the compaction and it was never curated");
        after.TermFrequencies.Should().NotContainKey("aaa0",
            because: "pinning a term at agent weight for content the rewrite just deleted is the " +
                     "defect; it also costs the surviving text its own keyword slots");
        after.Keywords.Should().Contain("bbb0",
            because: "the surviving chunk's own terms are what this memory should now be found by");
    }

    /// <summary>
    /// Provenance is recorded per keyword, not per write: an append's own keywords are curated and
    /// the memory's auto-extracted terms stay auto, in the same list.
    /// </summary>
    [Fact]
    public async Task Append_RecordsItsOwnKeywordsAsCuratedAndLeavesTheRestAuto()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(["a note about boats"], "append-provenance", keywords: ["portcullis"]);
        await Tools().Append(" a note about trains", "append-provenance", keywords: ["harbour"]);

        var after = Entry("append-provenance");
        after.AgentKeywords.Should().BeEquivalentTo(["portcullis", "harbour"],
            because: "the memory's recorded curated keywords plus this append's own");
        after.Keywords.Should().Contain("boats",
            because: "D-12: an append destroys none of the memory's keywords");
        after.TermFrequencies!["boats"].Should().Be(3,
            because: "one occurrence plus the AUTO boost — an append carrying a keyword forward is " +
                     "not the same fact as the agent having supplied it");
    }

    /// <summary>
    /// A keyword added through <c>UpdateMeta</c> (no caller outside <c>link()</c> and the tests) is
    /// curated by definition, and is recorded as such —
    /// otherwise the next compaction would have to guess at it, and for a term the content also
    /// yields the guess is wrong.
    /// </summary>
    [Fact]
    public async Task UpdateMeta_RecordsAnAddedKeywordAsCurated()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(["a note about boats", " and trains"], "update-provenance");
        Entry("update-provenance").AgentKeywords.Should().BeEmpty(
            because: "precondition: nobody supplied a keyword to the store call");

        await Tools().UpdateMeta("update-provenance", keywords: ["portcullis"]);

        Entry("update-provenance").AgentKeywords.Should().Contain("portcullis");

        await Tools().Compact("update-provenance");

        Entry("update-provenance").TermFrequencies!["portcullis"].Should().Be(5,
            because: "the keyword the caller added through UpdateMeta is as curated as one supplied " +
                     "to remember(), and survives the next content rewrite at agent weight");
    }

    // ── The legacy fallback: entries written before provenance was recorded ──

    /// <summary>
    /// The migration promise: an entry whose provenance is UNKNOWN behaves on append exactly as it
    /// did before the field existed — its whole keyword list is carried forward at agent weight.
    /// Treating unknown as "nothing was curated" would silently demote every keyword on every
    /// pre-existing memory the first time it was appended to.
    /// </summary>
    [Fact]
    public async Task Append_LegacyEntryWithUnknownProvenance_KeepsItsKeywordsAtAgentWeight()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(["a harbour full of boats"], "append-legacy", keywords: ["harbour"]);
        ForgetProvenance("append-legacy");

        await Tools().Append(" a note about trains", "append-legacy");

        var after = Entry("append-legacy");
        after.Keywords.Should().Contain("harbour", because: "D-12: an append destroys no keyword");
        after.TermFrequencies!["harbour"].Should().Be(6,
            because: "one occurrence plus the agent boost — the entry never said which of its " +
                     "keywords were curated, so the append keeps the old blanket assumption " +
                     "rather than demoting them all");
        after.AgentKeywords.Should().NotBeNull(
            because: "and the append writes the field, so the guessing stops here");
    }

    /// <summary>
    /// An entry with null (UNKNOWN) provenance falls back to the subtraction heuristic, which
    /// recovers a keyword the content cannot have produced. Without the fallback such an entry
    /// would lose its curated keywords on its first compaction, purely because nobody had written
    /// down which they were.
    /// </summary>
    [Fact]
    public async Task Compact_LegacyEntryWithNoRecordedProvenance_RecoversWhatTheContentCannotYield()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(
            ["the first chunk is about widgets", " the second chunk is about gadgets"],
            "compact-legacy",
            keywords: ["portcullis"]);
        ForgetProvenance("compact-legacy");

        await Tools().Compact("compact-legacy", keepRecent: 1);

        var after = Entry("compact-legacy");
        after.Keywords.Should().Contain("portcullis");
        after.TermFrequencies!["portcullis"].Should().Be(5);
        after.AgentKeywords.Should().Equal(["portcullis"],
            because: "the compaction wrote the field, so the entry is no longer unknown and the " +
                     "next rewrite does not have to guess at all");
    }

    /// <summary>
    /// What the fallback cannot do, pinned so it is visible rather than discovered: for an UNKNOWN
    /// entry, a curated keyword the content also yields is indistinguishable from an auto one and
    /// is demoted. This is the case the recorded provenance exists to answer — compare
    /// <see cref="Compact_KeepsAgentWeightForACuratedKeywordTheContentAlsoYields"/>, which is the
    /// same memory with its provenance intact.
    /// </summary>
    [Fact]
    public async Task Compact_LegacyEntry_DemotesACuratedKeywordTheContentAlsoYields()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Remember(
            ["a harbour full of boats", " more about the harbour"],
            "compact-legacy-limit",
            keywords: ["harbour"]);
        ForgetProvenance("compact-legacy-limit");

        await Tools().Compact("compact-legacy-limit");

        var after = Entry("compact-legacy-limit");
        after.Keywords.Should().Contain("harbour",
            because: "the term is still extractable from the compacted text, so it stays — this " +
                     "is a demotion, not a loss");
        after.TermFrequencies!["harbour"].Should().Be(4,
            because: "two occurrences plus the AUTO boost of +2: for an entry that never recorded " +
                     "its provenance there is nothing left to prove this term was the agent's");
    }

    /// <summary>
    /// The fallback subtracts over each chunk SEPARATELY as well as over the joined text. Without
    /// the per-chunk half, an appended chunk whose terms dominate the joined top-25 pushes the
    /// earlier chunks' auto terms out of the subtracted set, and they come back looking curated —
    /// the Critical, arriving through the fallback instead of through the append.
    /// </summary>
    [Fact]
    public async Task Compact_LegacyEntry_DoesNotRecoverAnEarlierChunksAutoTerms()
    {
        using var scope = new TestHelpers.StoreScope();

        string firstChunk = string.Join(" ", Enumerable.Range(0, 25).Select(i => $"aaa{i}"));
        string appended = string.Join(" ",
            Enumerable.Range(0, 10).Select(i => $"bbb{i} bbb{i} bbb{i}"));

        await Tools().Remember([firstChunk], "compact-legacy-append");
        await Tools().Append(" " + appended, "compact-legacy-append");
        ForgetProvenance("compact-legacy-append");

        await Tools().Compact("compact-legacy-append", keepRecent: 1);

        // Asserted over the whole family rather than one probe term: which of the 25 fall out of
        // the JOINED top-25 depends on the frequency tie-break, and the guarantee is that none of
        // them comes back curated — the per-chunk pass is what sees that they are the first
        // chunk's own auto terms.
        var after = Entry("compact-legacy-append");
        after.Keywords.Should().NotContain(k => k.StartsWith("aaa", StringComparison.Ordinal),
            because: "every aaa term was auto-extracted from the chunk this compaction discarded");
        after.TermFrequencies!.Keys.Should().NotContain(k => k.StartsWith("aaa", StringComparison.Ordinal),
            because: "recovering one as 'curated' would pin it at agent weight with its content " +
                     "deleted, which is the Critical arriving through the fallback instead of " +
                     "through the append");
    }

    /// <summary>
    /// A legacy entry can carry a <c>key:value</c>-shaped keyword (<c>person:maria</c>) that
    /// predates the routing of those into attributes. The fallback recovers it — no content yields
    /// a colon-joined token — it re-enters the write as a keyword, and <c>RouteKeywords</c> sends it
    /// to <c>Attributes["person"]</c>, where a freshly-stored memory would have put it.
    /// <para>So a compaction MIGRATES it rather than keeping it a keyword. Pinned because it is a
    /// silent shape change: the keyword list loses an entry and the attribute map gains one. It
    /// matches what <c>AttributeMigration.Forward</c> does with the same input, so the memory ends
    /// where the rest of the system already expects it.</para>
    /// </summary>
    [Fact]
    public async Task Compact_LegacyEntry_MigratesAPrefixedKeywordIntoItsAttribute()
    {
        using var scope = new TestHelpers.StoreScope();

        // Two chunks, because compact() answers "already a single chunk" before writing anything.
        await Tools().Remember(
            ["a note about boats", " and another note about trains"], "compact-legacy-prefixed");
        MemoryStoreContext.Current!.TryUpdateEntry("compact-legacy-prefixed", "local",
            e => e with { Keywords = [.. e.Keywords ?? [], "person:maria"], AgentKeywords = null })
            .Should().BeTrue(because: "precondition: the legacy keyword has to reach the entry");

        await Tools().Compact("compact-legacy-prefixed", keepRecent: 1);

        var after = Entry("compact-legacy-prefixed");
        after.Attributes.Should().ContainKey("person");
        after.Attributes!["person"].Should().Contain("maria");
        after.Keywords.Should().NotContain("person:maria",
            because: "a key:value keyword becomes an attribute on any write — the compaction is " +
                     "just the first write this entry has had since that rule arrived");
    }
}
