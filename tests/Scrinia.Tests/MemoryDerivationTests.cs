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
    private static readonly string[] ExpectedWhenAuthChanges = new[] { "when auth changes" };
    private static readonly string[] ExpectedAlphaBeta = new[] { "alpha", "beta" };
    private static readonly string[] ExpectedFirstPhraseSecondPhrase = new[] { "first phrase", "second phrase" };
    private static readonly string[] ExpectedShipped = new[] { "shipped" };
    private static readonly string[] Expected20270101T00000000000000000 = new[] { "2027-01-01T00:00:00.0000000+00:00" };

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

        // The canonical form is byte-exact and MACHINE-INDEPENDENT. A bare date is parsed under
        // the invariant culture and assumed UTC, so a store in UTC and a client in UTC+10 persist
        // the same string. Parsing under CurrentCulture/local zone — what MCP does — embeds the
        // writer's offset here and reopens the very divergence D-06 exists to close.
        review[0].Should().Be("2026-01-01T00:00:00.0000000+00:00");

        DateTimeOffset.TryParseExact(review[0], "o", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _)
            .Should().BeTrue("AttributeReaders.TryReadReview only recognizes round-trip \"o\" form");

        // D-08. "0000000" is the ISO form's fractional-seconds token: it exists ONLY in the
        // canonicalized value the carrier fold produces, so it can only be in the index if seeding
        // ran AFTER that fold. Under MCP's order — seed, then fold the carrier — the seeded value
        // is the raw "2026-01-01" and this key is absent. tf["2026"] does NOT discriminate: the
        // raw value contains it too.
        derived.TermFrequencies.Should().ContainKey("0000000",
            "D-08: the carrier's canonicalized review value is seeded because seeding runs last");
        derived.TermFrequencies["2026"].Should().Be(3);
    }

    [Fact]
    public void ForStore_SeedsTheAutoDerivedReviewConditionBecauseSeedingRunsLast()
    {
        var derived = MemoryDerivation.ForStore(["this workspace has 835 tests today"]);

        // D-08, on the output of D-05. The auto-derived condition is produced by the carrier fold
        // and exists nowhere in the content, so its tokens reach the index only if seeding is the
        // last step of attribute handling. Under MCP's order the attribute map is empty at seed
        // time and none of these tokens appear. This is the pin that a Task 4/5 port restoring
        // MCP's ordering — the natural thing to do when moving Remember — has to trip over.
        derived.TermFrequencies.Should().ContainKey("counts");
        derived.TermFrequencies["counts"].Should().Be(3);
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

        derived.Attributes!["review"].Should().BeEquivalentTo(ExpectedWhenAuthChanges);
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

        // The invariant the carrier's file re-fold exists to hold: every hashed path is also a
        // searchable `file` value, so the private baseline and the public key cannot drift apart.
        // Honest note: on the STORE path this assertion is not mutation-killable — HashFileRefs
        // sources its paths from attributes["file"], so the keys are a subset of what is already
        // there and removing the re-fold changes nothing. It pins the invariant, not the line.
        // The append path is where that fold does real work, and it is killed there (D-21/D-08).
        derived.Attributes!["file"].Should().Contain(derived.FileHashes.Keys);
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
        derived.Attributes!["tag"].Should().BeEquivalentTo(ExpectedAlphaBeta);
        derived.Attributes["anchor"].Should().BeEquivalentTo(ExpectedFirstPhraseSecondPhrase);
    }

    [Fact]
    public void ForAppend_UnionsATagThatCameFromTheAppendedContent()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["original body\n", "tag: beta"],
            existing: new ExistingMetadata(Attributes: new Dictionary<string, string[]>
            {
                ["tag"] = ["alpha"],
            }));

        // D-17 through the other channel. MCP unioned only the tags it received as a PARAMETER and
        // let that union replace whatever a `tag:` line in the content had produced, so a tag
        // written in prose could wipe the curated set. Under one flat map there is no parameter
        // channel left, so the union rule applies to every source of the key — which is stricter
        // than MCP was, and is the reading of D-17 that never loses a tag.
        derived.Attributes!["tag"].Should().BeEquivalentTo(ExpectedAlphaBeta);
    }

    [Fact]
    public void ForAppend_CarriesTheDriftBaselineForward()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["original body\n", "appended body"],
            existing: new ExistingMetadata(
                // A path whose stem appears nowhere else, so the term-frequency assertion below
                // can only be satisfied by the carrier fold.
                FileHashes: new Dictionary<string, string> { ["src/Zzyzx.cs"] = "sha-zzyzx" }));

        // D-21, the second data-loss fix: the server's append built its entry with no FileHashes
        // at all, so a REST append destroyed the memory's drift baseline. The hashes carry
        // forward, and their path keys are re-folded into the searchable `file` attribute so the
        // two cannot fall out of step.
        derived.FileHashes.Should().ContainKey("src/Zzyzx.cs");
        derived.Attributes!["file"].Should().Contain("src/Zzyzx.cs");

        // D-08 on the append path. The `file` key here is produced by the carrier fold and by
        // nothing else — the appended content mentions no path — so its tokens are in the index
        // only because seeding runs after that fold. MCP's append seeded BEFORE folding its
        // review/file carrier, which is exactly the ordering this fails against.
        derived.TermFrequencies.Should().ContainKey("zzyzx",
            "D-08: the file keys re-folded from FileHashes seed because seeding runs last");
    }

    [Fact]
    public void ForAppend_HashesAFileTheAppendedChunkReferencesForTheFirstTime()
    {
        // D-21 carried the existing baseline forward but never added to it, so a file first cited
        // by an appended chunk landed in the searchable `file` attribute with no hash behind it —
        // and list(mode:"drift") then reported it clean forever, having nothing to compare.
        var derived = MemoryDerivation.ForAppend(
            ["first chunk", "second chunk, see src/new.cs"],
            existing: new ExistingMetadata(
                Attributes: new() { ["file"] = ["src/old.cs"] },
                FileHashes: new() { ["src/old.cs"] = "aaa" }),
            // The non-"bbb" branch is UNREACHABLE, and that is the point: HashFileRefs' `skip`
            // parameter excludes every already-baselined path before the hasher runs on it (see
            // MemoryDerivation.ForAppend's "ONLY NEW PATHS ARE HASHED — literally, not just kept"),
            // so src/old.cs never reaches this delegate. It is spelled out rather than left as a
            // throwing sentinel so a reader can see that this test asserts the NEW path's hash
            // without also depending on how the old one is protected — the sibling test below is
            // what pins that.
            fileHasher: path => path == "src/new.cs" ? "bbb" : "unreachable-src-old-is-skipped");

        derived.FileHashes.Should().ContainKey("src/new.cs").WhoseValue.Should().Be("bbb",
            because: "a file first referenced by the appended chunk must gain a drift baseline");
    }

    [Fact]
    public void ForAppend_LeavesAnAlreadyBaselinedFileAtItsOriginalHash()
    {
        // THE LOAD-BEARING ASSERTION. Re-hashing on every append would re-baseline the file to its
        // CURRENT content each time, so it could never be reported as drifted — the drift signal
        // would be destroyed by the very operation meant to maintain it. The hasher here returns a
        // different value on purpose: if the rule re-hashes, this fails.
        var derived = MemoryDerivation.ForAppend(
            ["first chunk", "second chunk mentioning src/old.cs again"],
            existing: new ExistingMetadata(
                Attributes: new() { ["file"] = ["src/old.cs"] },
                FileHashes: new() { ["src/old.cs"] = "original-hash" }),
            fileHasher: _ => "file-has-since-changed");

        derived.FileHashes.Should().ContainKey("src/old.cs").WhoseValue.Should().Be("original-hash",
            because: "the baseline is what drift compares AGAINST — re-hashing it on append " +
                     "silently destroys drift detection");
    }

    [Fact]
    public void ForAppend_WithoutAHasherKeepsTheCarriedBaselineUnchanged()
    {
        var derived = MemoryDerivation.ForAppend(
            ["first chunk", "second chunk, see src/new.cs"],
            existing: new ExistingMetadata(FileHashes: new() { ["src/old.cs"] = "aaa" }));

        derived.FileHashes.Should().BeEquivalentTo(new Dictionary<string, string> { ["src/old.cs"] = "aaa" },
            because: "no resolver means no baseline can be computed — D-04 — and the carried one survives");
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
            attributes: new Dictionary<string, string[]>
            {
                ["status"] = ["shipped"],
                ["review"] = ["2027-01-01"],
            },
            keywords: null,
            existing: new ExistingMetadata(Attributes: new Dictionary<string, string[]>
            {
                ["status"] = ["draft"],
                ["review"] = ["2026-06-01"],
            }));

        // D-18: AppendRequest carried no attributes at all server-side. Scalar keys are
        // newer-wins, so an explicit caller value replaces the entry's.
        derived.Attributes!["status"].Should().BeEquivalentTo(ExpectedShipped);

        // D-18 × D-06, and the one place this pipeline chose against all four originals. MCP's
        // append re-emitted the EXISTING entry's review through its carrier AFTER the caller's
        // attributes had merged, so a caller-supplied review on an append was silently clobbered
        // by the old one. That is an accident of the two-channel carrier mechanism, which does not
        // exist under one flat map; keeping it would make D-18 conditional on the key and leave
        // the bare-date divergence open on append. The canonicalization therefore runs over the
        // MERGED review. Revert it to `existing?.Attributes` and this returns the 2026 date.
        derived.Attributes["review"].Should().BeEquivalentTo(
            Expected20270101T00000000000000000,
            "the caller's review must win on append (D-18) and be canonicalized (D-06)");
    }

    [Fact]
    public void ForStore_DropsAMentionShadowedByATypedKey()
    {
        var derived = MemoryDerivation.ForStore(
            content: ["Jolene shipped it"],
            description: null,
            attributes: null,
            keywords: ["person:jolene"]);

        // D-09. The typed key is authoritative: a routed "person:jolene" collides with the
        // mention:jolene span the extractor finds in the same content, and the untyped duplicate
        // would double-count the entity in the graph and the top-attribute facets. The inventory
        // recorded D-09 as "no difference — recorded so Task 2 does not fix it", which left the
        // drop itself unguarded: it reads as a no-op, and a no-op is what gets deleted.
        derived.Attributes!["person"].Should().Contain("jolene");
        derived.Attributes.Should().NotContainKey("mention",
            "the shadowed mention was the only one, so the key goes with it");
    }

    [Fact]
    public void ForStore_AgentAttributesWinScalarCollisionsWithExtractedOnes()
    {
        var derived = MemoryDerivation.ForStore(
            content: ["Status: draft"],
            description: null,
            attributes: new Dictionary<string, string[]> { ["status"] = ["shipped"] },
            keywords: null);

        // Explicit agent attributes fold in LAST, so they win scalar collisions against anything
        // mechanically extracted from the content — here a literal `Status:` line saying the
        // opposite. Swap the two arguments of that merge and the content wins instead, which is
        // how a caller's correction would silently lose to the text it was correcting.
        derived.Attributes!["status"].Should().BeEquivalentTo(ExpectedShipped);
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

    // ── ForMetadataUpdate: the metadata-only write, with no content to derive from ──────────

    private static ArtifactEntry Entry(
        Dictionary<string, string[]>? attributes = null,
        string[]? keywords = null,
        Dictionary<string, int>? termFrequencies = null) =>
        new("probe", "", 10, 1, DateTimeOffset.UnixEpoch,
            Keywords: keywords,
            TermFrequencies: termFrequencies,
            Attributes: attributes);

    /// <summary>The Critical this exists to close: an attribute added by a metadata-only mutation
    /// must reach the BM25 index, or the entry becomes unfindable by the very attribute the caller
    /// just added — silently, since the write itself succeeds.</summary>
    [Fact]
    public void ForMetadataUpdate_SeedsAValueAddedByTheMutation()
    {
        var current = Entry(termFrequencies: new() { ["body"] = 4 });
        var updated = Entry(
            attributes: new() { ["person"] = ["zanzibar"] },
            termFrequencies: new() { ["body"] = 4 });

        var result = MemoryDerivation.ForMetadataUpdate(current, updated);

        result.TermFrequencies.Should().ContainKey("zanzibar");
        result.TermFrequencies!["body"].Should().Be(4,
            because: "seeding the change must not disturb what the content derived");
    }

    /// <summary>At the SAME weight every write path uses (D-08's +3 via
    /// <c>AttributeExtractor.SeedTermFrequencies</c>), not a second number. <c>UpdateMeta</c> used
    /// +5 here, so the identical attribute ranked differently depending on whether it arrived
    /// through <c>UpdateMeta</c> (no caller outside <c>link()</c> and the tests) or through
    /// <c>remember()</c>.</summary>
    [Fact]
    public void ForMetadataUpdate_SeedsAtTheSameWeightTheWritePathsUse()
    {
        var current = Entry();
        var updated = Entry(attributes: new() { ["person"] = ["zanzibar"] });

        var result = MemoryDerivation.ForMetadataUpdate(current, updated);

        // The key seeds too, exactly as SeedTermFrequencies does on a store write.
        result.TermFrequencies!["zanzibar"].Should().Be(3);
        result.TermFrequencies["person"].Should().Be(3);
    }

    /// <summary>
    /// The invariant the whole PATCH contract rests on: an unedited complete-entry round-trip is a
    /// no-op. Term frequencies are cumulative, so re-seeding the whole attribute map on every
    /// update would inflate every token without bound — a caller who GETs an entry and PATCHes it
    /// back unchanged, which the wire shape exists to make safe, would silently re-rank it.
    /// </summary>
    [Fact]
    public void ForMetadataUpdate_IsANoOp_WhenNothingChanged()
    {
        var attributes = new Dictionary<string, string[]> { ["person"] = ["zanzibar"] };
        var tf = new Dictionary<string, int> { ["zanzibar"] = 3 };
        var current = Entry(attributes: attributes, termFrequencies: tf);
        var updated = Entry(attributes: attributes, termFrequencies: tf);

        var result = MemoryDerivation.ForMetadataUpdate(current, updated);

        result.TermFrequencies.Should().BeSameAs(tf,
            because: "an unchanged round-trip must not even rebuild the map, let alone re-seed it");
    }

    /// <summary>A mutation that changes something else entirely — the orphan marker's removal, a
    /// preview edit — must not touch the index either.</summary>
    [Fact]
    public void ForMetadataUpdate_IgnoresAChangeThatRemovesAValue()
    {
        var tf = new Dictionary<string, int> { ["zanzibar"] = 3 };
        var current = Entry(attributes: new() { ["person"] = ["zanzibar"] }, termFrequencies: tf);
        var updated = Entry(termFrequencies: tf);

        var result = MemoryDerivation.ForMetadataUpdate(current, updated);

        // Documented, not accidental: undoing a seed needs the content-derived baseline this call
        // does not have. A re-store rebuilds term frequencies from scratch and clears it.
        result.TermFrequencies.Should().BeSameAs(tf);
    }

    /// <summary>A keyword added by a caller is agent-supplied by definition (+5, D-13), and seeds
    /// its INDEX TOKENS rather than the raw string (D-14) — <c>UpdateMeta</c> seeded the raw
    /// string, which no tokenized query term can equal for a prefixed keyword.</summary>
    [Fact]
    public void ForMetadataUpdate_BoostsAnAddedKeywordsIndexTokens()
    {
        var current = Entry(keywords: ["alpha"]);
        var updated = Entry(keywords: ["alpha", "bravo"]);

        var result = MemoryDerivation.ForMetadataUpdate(current, updated);

        result.TermFrequencies!["bravo"].Should().Be(5);
        result.TermFrequencies.Should().NotContainKey("alpha",
            because: "only the DELTA seeds — a keyword the entry already carried was already " +
                     "counted when it arrived");
    }

    // ── Curated-keyword recovery (D-29's first option) ───────────────────────

    /// <summary>
    /// A persisted keyword list is the merge of the agent's with the auto-extracted terms, and the
    /// provenance is not stored. Subtracting what the content itself yields recovers the half that
    /// is certainly the agent's — which is what lets a content rewrite hand keywords back in
    /// without committing the D-01 mistake of re-entering derived terms as input.
    /// </summary>
    [Fact]
    public void CuratedKeywords_RecoversWhatTheContentCannotHaveProduced()
    {
        string[] content = ["the quick brown fox jumps over the lazy dog"];
        var derived = MemoryDerivation.ForStore(content, keywords: ["vulpine"]);

        // Precondition, not decoration: the assertion below is only meaningful if the merged list
        // really does carry auto terms alongside the agent's one.
        derived.Keywords.Should().Contain("vulpine").And.Contain("quick");

        MemoryDerivation.CuratedKeywords(derived.Keywords, content)
            .Should().Equal(["vulpine"],
                because: "\"quick\" is extractable from this content and \"vulpine\" is not — that " +
                         "is the whole of what can be proved after the merge");
    }

    /// <summary>The converse case, and the limit worth naming: a curated keyword that the content
    /// ALSO yields is indistinguishable from an auto term once merged, so it is not recovered.
    /// Recovering it would need a stored agent-set flag, not a cleverer subtraction.</summary>
    [Fact]
    public void CuratedKeywords_CannotRecoverATermTheContentAlsoYields()
    {
        string[] content = ["the quick brown fox jumps over the lazy dog"];
        var derived = MemoryDerivation.ForStore(content, keywords: ["quick"]);

        derived.TermFrequencies["quick"].Should().Be(6,
            because: "precondition: it really was treated as agent-supplied when it was written " +
                     "(one occurrence of the token plus the +5 agent boost)");

        MemoryDerivation.CuratedKeywords(derived.Keywords, content)
            .Should().BeEmpty();
    }

    [Fact]
    public void CuratedKeywords_EmptyForNoKeywords()
    {
        MemoryDerivation.CuratedKeywords(null, ["some content"]).Should().BeEmpty();
        MemoryDerivation.CuratedKeywords([], ["some content"]).Should().BeEmpty();
    }

    /// <summary>The review date parses the same way for every writer — the store's two derivation
    /// entry points and the metadata-only update path, which reaches neither. A bare date is UTC,
    /// under invariant culture, on every machine (D-06 / the Task 2 ruling): a local-zone parse
    /// makes the persisted string depend on where the client is sitting.</summary>
    [Theory]
    [InlineData("2026-08-01")]
    [InlineData("2026-08-01T00:00:00Z")]
    public void TryParseReviewDate_ReadsABareDateAsUtc(string value)
    {
        MemoryDerivation.TryParseReviewDate(value, out var parsed).Should().BeTrue();
        parsed.Should().Be(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        parsed.Offset.Should().Be(TimeSpan.Zero,
            because: "the persisted ISO-8601 round-trip form embeds this offset, so a local one " +
                     "makes two machines disagree about the same input");
    }
}
