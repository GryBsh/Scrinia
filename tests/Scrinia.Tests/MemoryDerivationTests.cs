using System.Globalization;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// The specification of the collapsed derivation contract.
///
/// <para>Derivation used to be written out four times — MCP store, server store, MCP append,
/// server append — and those four had drifted apart. <see cref="MemoryDerivation"/> replaces all
/// of them, and every behavioural question was settled in
/// <c>docs/superpowers/analysis/2026-08-11-derivation-inventory.md</c> (ids D-01 … D-29). Each
/// test below names the ruling it pins and is written to FAIL if a later refactor silently
/// reverts to one of the four old behaviours — which is the only guard the subsequent tasks
/// have while they repoint callers and delete the originals.</para>
/// </summary>
public sealed class MemoryDerivationTests
{
    // ── Store path ───────────────────────────────────────────────────────────

    [Fact]
    public void ForStore_BoostsAgentKeywordsAboveAutoKeywords()
    {
        var derived = MemoryDerivation.ForStore(
            content: ["the quick brown fox jumps over the lazy dog"],
            description: null,
            attributes: null,
            keywords: ["vulpine"]);

        // D-13: agent-supplied terms +5, auto-extracted +2. "fox" occurs once in the content and
        // is auto-extracted, so it lands at 1 + 2; "vulpine" is nowhere in the content, so its 5
        // is the agent boost alone.
        derived.TermFrequencies["vulpine"].Should().Be(5);
        derived.TermFrequencies["fox"].Should().Be(3);
        derived.Keywords.Should().Contain("vulpine");
    }

    [Fact]
    public void ForStore_TreatsUnsuppliedKeywordsAsAuto_NeverAsAgent()
    {
        var derived = MemoryDerivation.ForStore(["the quick brown fox jumps over the lazy dog"]);

        // D-01: the surviving behaviour after the server's outcome was dropped. The wire defect
        // was that HttpMemoryStore sent MCP's already-MERGED keyword list as MemoryWrite.Keywords,
        // so every auto-extracted term arrived reclassified as agent-supplied and seeded +5. With
        // raw input — here, none — auto terms stay auto.
        derived.TermFrequencies["fox"].Should().Be(3);
        derived.Keywords.Should().Contain("fox");
    }

    [Fact]
    public void ForStore_ExposesNoChannelForPreviouslyDerivedKeywords()
    {
        var parameters = typeof(MemoryDerivation)
            .GetMethod(nameof(MemoryDerivation.ForStore))!
            .GetParameters()
            .Select(p => p.Name!)
            .ToArray();

        // D-01 / D-29, structurally. The store path takes raw input and returns derived output;
        // it has no "existing keywords" parameter, so neither the wire nor compact can hand a
        // derived list back in and have every auto term re-boosted to +5. If a later task adds
        // one, that mistake becomes expressible again and this fails.
        parameters.Should().NotContain(
            p => p.Contains("existing", StringComparison.OrdinalIgnoreCase),
            "the store pipeline must not accept previously derived metadata as input (D-01/D-29)");
    }

    [Fact]
    public void ForStore_SeedsTheWholeAttributeMapOnce_CarrierKeysIncluded()
    {
        var derived = MemoryDerivation.ForStore(
            content: ["a body with no interesting words in it"],
            description: null,
            attributes: new Dictionary<string, string[]>
            {
                ["tag"] = ["alpha"],
                ["anchor"] = ["betaphrase"],
            },
            keywords: null);

        // D-08 — the ruling that inverts the "MCP wins" default, and the one deliberate ordering
        // choice in the pipeline. MCP seeded term frequencies BEFORE folding its tag/anchor/
        // review/file carrier, so those values seeded 0 (anchors seeded +2 from a separate pass).
        // The complete map is now assembled first and seeded exactly once, last, which is the
        // server's order. A revert to MCP's split drops both of these to 0 / 2.
        derived.TermFrequencies["alpha"].Should().Be(3);
        derived.TermFrequencies["betaphrase"].Should().Be(3,
            "D-03: an anchor is an ordinary attribute key now and takes the attribute weight (+3), "
            + "not MCP's separate +2 anchor pass");
        derived.TermFrequencies["tag"].Should().Be(3, "SeedTermFrequencies seeds the key as well as the values");
    }

    [Fact]
    public void ForStore_CanonicalizesABareReviewDate()
    {
        var derived = MemoryDerivation.ForStore(
            content: ["a body"],
            description: null,
            attributes: new Dictionary<string, string[]> { ["review"] = ["2026-01-01"] },
            keywords: null);

        // D-06: MCP back-parsed `review` and re-emitted the date in ISO-8601 round-trip form; the
        // server persisted "2026-01-01" verbatim. That is the spec-header divergence "MCP and REST
        // disagree on a bare date in a review attribute", closed by adopting MCP's form. `review`
        // is scalar, so the canonical value REPLACES the raw one.
        string[] review = derived.Attributes!["review"];
        review.Should().HaveCount(1);
        DateTimeOffset.TryParseExact(review[0], "o", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed)
            .Should().BeTrue("the persisted review date must be in round-trip \"o\" form");
        parsed.Date.Should().Be(new DateTime(2026, 1, 1));

        // D-06 × D-08: because seeding now runs after the canonicalization, the ISO form's junk
        // tokens seed locally too. Accepted as ranking noise (AttributeExtractor.cs:111-115) and
        // identical on both paths — which is the point.
        derived.TermFrequencies["2026"].Should().Be(3);
    }

    [Fact]
    public void ForStore_AutoDerivesTheCountReviewCondition()
    {
        var derived = MemoryDerivation.ForStore(["this workspace has 835 tests today"]);

        // D-05: kept from MCP; the server never implemented it. Content whose claims are counts
        // goes stale silently, so it reviews itself when the counts change.
        derived.Attributes!["review"].Should().Contain(MemoryDerivation.CountReviewCondition);
    }

    [Fact]
    public void ForStore_DoesNotAutoDeriveTheCountConditionForAnEphemeral()
    {
        var derived = MemoryDerivation.ForStore(
            content: ["this workspace has 835 tests today"], isEphemeral: true);

        // D-05's other half: MCP's guard was `!store.IsEphemeral(name)`.
        (derived.Attributes?.ContainsKey("review") ?? false).Should().BeFalse();
    }

    [Fact]
    public void ForStore_ExplicitReviewConditionSurvivesTheCountPattern()
    {
        var derived = MemoryDerivation.ForStore(
            content: ["this workspace has 835 tests today"],
            description: null,
            attributes: new Dictionary<string, string[]> { ["review"] = ["when auth changes"] },
            keywords: null);

        derived.Attributes!["review"].Should().BeEquivalentTo(new[] { "when auth changes" });
    }

    [Fact]
    public void ForStore_ExtractsRefsFromTheChunksJoinedByNewline()
    {
        // D-02. Concat glues the last token of chunk N to the first of chunk N+1: it DESTROYS the
        // match below (".md" would run into "more") and, in the second case, FABRICATES one that
        // no chunk contains. The newline separator can do neither. Ref extraction is the only call
        // site that changes — AnalyzeText, Extract and ExtractMentionSpans keep the concatenated
        // form, because changing them would re-tokenize every memory already stored.
        var preserved = MemoryDerivation.ForStore(["see docs/guide.md", "more text follows"]);
        preserved.Attributes!["file"].Should().Contain("docs/guide.md",
            "string.Concat would have produced \"docs/guide.mdmore\" and lost the reference");

        var fabricated = MemoryDerivation.ForStore(["a note about src/Foo", ".cs and nothing else"]);
        (fabricated.Attributes?.ContainsKey("file") ?? false).Should().BeFalse(
            "string.Concat would have invented \"src/Foo.cs\", which neither chunk contains");
    }

    [Fact]
    public void ForStore_ComputesTheDriftBaselineWhateverTheStoreMode()
    {
        var derived = MemoryDerivation.ForStore(
            content: ["a body"],
            description: null,
            attributes: new Dictionary<string, string[]> { ["file"] = ["src/Foo.cs", "src/Gone.cs"] },
            keywords: null,
            isEphemeral: false,
            fileHasher: path => path == "src/Foo.cs" ? "sha-foo" : null);

        // D-04: MCP resolved file hashes against a local workspace root and skipped the whole step
        // whenever the store was remote, so a remote write had NO drift baseline. Derivation runs
        // inside the store now, so whoever owns the workspace supplies the hasher and both store
        // modes get a baseline. Paths the hasher cannot resolve are simply absent.
        derived.FileHashes.Should().ContainKey("src/Foo.cs");
        derived.FileHashes!["src/Foo.cs"].Should().Be("sha-foo");
        derived.FileHashes.Should().NotContainKey("src/Gone.cs");
    }

    [Fact]
    public void ForStore_ComputesChunkEntriesOnlyForMultiChunkContent()
    {
        MemoryDerivation.ForStore(["one chunk"])
            .ChunkEntries.Should().BeNull(
                because: "D-11: single-chunk content carries no per-chunk metadata");

        var multi = MemoryDerivation.ForStore(["first chunk", "second chunk"]);
        multi.ChunkEntries.Should().HaveCount(2);
        multi.ChunkEntries![0].ChunkIndex.Should().Be(1, "chunk numbering is 1-based");
    }

    [Fact]
    public void ForStore_LeavesAnEmptyChunkPreviewNullRatherThanBlank()
    {
        var derived = MemoryDerivation.ForStore(["real content here", "   "]);

        // D-23: the store path guarded this and the two append paths did not, so an empty chunk
        // produced "" on one path and null on the other — distinguishable in a sidecar round-trip.
        derived.ChunkEntries![1].ContentPreview.Should().BeNull();
    }

    [Fact]
    public void ForStore_CuratedDescriptionWinsOverTheAutoSnippet()
    {
        MemoryDerivation.ForStore(["body text here"], "curated preview")
            .ContentPreview.Should().Be("curated preview");

        MemoryDerivation.ForStore(["body text here"])
            .ContentPreview.Should().Be("body text here");
    }

    // ── Append path ──────────────────────────────────────────────────────────

    [Fact]
    public void ForAppend_KeepsExistingCuratedKeywordsAsAgentTerms()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["original body\n", "appended body"],
            existing: new ExistingMetadata(Keywords: ["curated"]));

        // D-12 is a DATA-LOSS fix, not a ranking tweak: the server's append passed null as its
        // agent keyword set and wrote the result back over the entry's keywords, so a REST append
        // destroyed every curated keyword the memory had. The term must survive the append...
        derived.Keywords.Should().Contain("curated");
        // ...and keep the agent weight (D-13; the server's append boosted everything +2).
        derived.TermFrequencies["curated"].Should().Be(5);
        derived.TermFrequencies["appended"].Should().Be(3, "auto-extracted terms stay at +2 over their count");
    }

    [Fact]
    public void ForAppend_BoostsTheIndexTokensOfACarriedColonKeyword_NotTheRawString()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["first chunk\n", "second chunk"],
            existing: new ExistingMetadata(Keywords: ["person:jolene"]));

        // D-14, and it is only observable because D-12 landed with it: before that, every append
        // keyword was AnalyzeText output — already tokenized, colon-free — so boosting the raw
        // string and boosting its index tokens were the same function, and a test asserting D-14
        // alone would have passed against the broken code. A carried-forward "person:jolene" is
        // where they differ: seeding the monolithic string is dead weight, because no tokenized
        // query term can ever equal it.
        derived.TermFrequencies["jolene"].Should().Be(5);
        derived.TermFrequencies.Should().NotContainKey("person:jolene");
        derived.Keywords.Should().Contain("person:jolene", "the keyword itself is structural metadata and stays");
    }

    [Fact]
    public void ForAppend_UnionsTagsAndAnchorsRatherThanReplacingThem()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["original body\n", "appended body"],
            description: null,
            attributes: new Dictionary<string, string[]>
            {
                ["tag"] = ["beta"],
                ["anchor"] = ["second phrase"],
            },
            keywords: null,
            existing: new ExistingMetadata(Attributes: new Dictionary<string, string[]>
            {
                ["tag"] = ["alpha"],
                ["anchor"] = ["first phrase"],
            }));

        // D-16 / D-17: `tag` and `anchor` are SCALAR in KnownAttributes, so the plain merge would
        // let one caller-supplied tag replace the memory's whole tag set. Store replaces
        // deliberately (tags must stay clearable on a re-store); append unions. The asymmetry is
        // encoded here as an append-path rule rather than by adding these keys to IsMultiValued,
        // which would take the store's clear away.
        derived.Attributes!["tag"].Should().BeEquivalentTo(new[] { "alpha", "beta" });
        derived.Attributes["anchor"].Should().BeEquivalentTo(new[] { "first phrase", "second phrase" });
    }

    [Fact]
    public void ForAppend_CarriesTheDriftBaselineForward()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["original body\n", "appended body"],
            existing: new ExistingMetadata(
                FileHashes: new Dictionary<string, string> { ["src/Foo.cs"] = "sha-foo" }));

        // D-21, the second data-loss fix: the server's append built its entry with no FileHashes
        // at all, so a REST append destroyed the memory's drift baseline. The hashes carry
        // forward, and their path keys are re-folded into the searchable `file` attribute so the
        // two cannot fall out of step.
        derived.FileHashes.Should().ContainKey("src/Foo.cs");
        derived.Attributes!["file"].Should().Contain("src/Foo.cs");
    }

    [Fact]
    public void ForAppend_RoutesCallerKeywordsIntoAttributes()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["original body\n", "appended body"],
            description: null,
            attributes: null,
            keywords: ["person:maria", "plainterm"]);

        // D-19: the server's append never routed caller keywords, so a `person:maria` keyword on
        // a REST append never became an attribute.
        derived.Attributes!["person"].Should().Contain("maria");
        derived.Keywords.Should().Contain("plainterm");
        derived.TermFrequencies["plainterm"].Should().Be(5);
    }

    [Fact]
    public void ForAppend_CallerAttributesWinOverTheEntrys()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["original body\n", "appended body"],
            description: null,
            attributes: new Dictionary<string, string[]> { ["status"] = ["shipped"] },
            keywords: null,
            existing: new ExistingMetadata(Attributes: new Dictionary<string, string[]>
            {
                ["status"] = ["draft"],
            }));

        // D-18: AppendRequest carried no attributes at all server-side. Scalar keys are
        // newer-wins, so an explicit caller value replaces the entry's.
        derived.Attributes!["status"].Should().BeEquivalentTo(new[] { "shipped" });
    }

    [Fact]
    public void ForAppend_ExtractsAttributesFromTheAppendedChunkOnly()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["Priority: high\n", "Status: shipped"]);

        // D-22: the earlier chunks' attributes are already on the entry; only the appended chunk
        // is re-scanned. Re-scanning everything would resurrect values a later write cleared.
        derived.Attributes!.Should().ContainKey("status");
        derived.Attributes.Should().NotContainKey("priority");
    }

    [Fact]
    public void ForAppend_ReSeedsCarriedAttributesBecauseTermFrequenciesAreRebuilt()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["original body\n", "appended body"],
            existing: new ExistingMetadata(Attributes: new Dictionary<string, string[]>
            {
                ["tag"] = ["alpha"],
                ["anchor"] = ["betaphrase"],
            }));

        // D-08 on the append path: term frequencies are rebuilt from scratch on every append, so
        // everything the merged map carries — including values written long ago — has to re-seed,
        // once, after the map is complete. MCP folded its review/file carrier AFTER seeding, so
        // those keys never seeded on an append at all.
        derived.TermFrequencies["alpha"].Should().Be(3);
        derived.TermFrequencies["betaphrase"].Should().Be(3);
    }

    [Fact]
    public void ForAppend_DescriptionRulesAreThreeWay()
    {
        // D-20: a new description wins; else the existing preview is PRESERVED (regenerating from
        // content would silently drop a curated description on every append); else generate. The
        // server's append had no description input and only implemented the last two.
        MemoryDerivation.ForAppend(
                fullContent: ["original\n", "appended"],
                description: "fresh description",
                attributes: null, keywords: null,
                existing: new ExistingMetadata(ContentPreview: "curated preview"))
            .ContentPreview.Should().Be("fresh description");

        MemoryDerivation.ForAppend(
                fullContent: ["original\n", "appended"],
                existing: new ExistingMetadata(ContentPreview: "curated preview"))
            .ContentPreview.Should().Be("curated preview");

        MemoryDerivation.ForAppend(fullContent: ["original\n", "appended"])
            .ContentPreview.Should().Be("original appended");
    }

    [Fact]
    public void ForAppend_AppendsOneChunkEntryNumberedContiguously()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["first\n", "second\n", "third"],
            description: null, attributes: null, keywords: null,
            existing: new ExistingMetadata(ChunkEntries:
            [
                new ChunkEntry(1, "first"),
                new ChunkEntry(2, "second"),
            ]));

        // D-25: the new entry's index is the post-append chunk count, contiguous with
        // ComputeChunkEntries' 1-based numbering over the earlier chunks. Recorded so nobody
        // "fixes" it into an off-by-one.
        derived.ChunkEntries.Should().HaveCount(3);
        derived.ChunkEntries![2].ChunkIndex.Should().Be(3);
        derived.ChunkEntries[2].ContentPreview.Should().Be("third");
        derived.ChunkEntries[0].ContentPreview.Should().Be("first", "existing chunk entries carry forward untouched");
    }

    [Fact]
    public void ForAppend_LeavesAnEmptyAppendedChunkPreviewNull()
    {
        var derived = MemoryDerivation.ForAppend(fullContent: ["real content here\n", "   "]);

        // D-23: one function produces chunk entries on both paths, so the store path's guard now
        // applies to the appended chunk too — null, not "".
        derived.ChunkEntries![^1].ContentPreview.Should().BeNull();
    }
}
