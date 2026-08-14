using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="AttributeExtractor"/> — mechanical key:value extraction (frontmatter,
/// line-start pairs, bold labels, and inline prose bounded to
/// <see cref="KnownAttributes.RecommendedKeys"/>) and agent-keyword routing into the attribute map.
/// </summary>
public sealed class AttributeExtractorTests
{
    [Fact]
    public void Extract_FrontmatterAndLineStartAndBoldLabel()
    {
        string content = "---\nStatus: Active\n---\nverdict: no-go\n**Why:** taxes retrieval\nProse: is skipped when value is prose? No — this extracts too.";
        var attributes = AttributeExtractor.Extract(content);

        attributes.Should().NotBeNull();
        attributes!["status"].Should().BeEquivalentTo(["Active"], because: "keys lowercase, values verbatim");
        attributes["verdict"].Should().BeEquivalentTo(["no-go"]);
        attributes["why"].Should().BeEquivalentTo(["taxes retrieval"]);
    }

    [Fact]
    public void Extract_BoldLabel_ColonOutsideVariant()
    {
        AttributeExtractor.Extract("**Status**: active")!["status"].Should().BeEquivalentTo(["active"]);
    }

    [Fact]
    public void Extract_MultiValuedKey_AccumulatesWithinText()
    {
        var attributes = AttributeExtractor.Extract("person: Alice\nperson: Bob");
        attributes!["person"].Should().BeEquivalentTo(["Alice", "Bob"],
            because: "entity keys accumulate within one text");
    }

    [Fact]
    public void Extract_ScalarKey_FirstWinsWithinText()
    {
        var attributes = AttributeExtractor.Extract("status: first\nstatus: second");
        attributes!["status"].Should().BeEquivalentTo(["first"],
            because: "scalar keys are first-wins within one extraction pass");
    }

    [Fact]
    public void Extract_Guards_FenceUrlCharsetLengthEmpty()
    {
        string longVal = new string('x', 121);
        var attributes = AttributeExtractor.Extract(
            "---\n" +                          // no `---` guard exists; pins that it mints nothing
            "```\nfenced: skipped\n```\n" +
            "http://example.com\n" +          // key "http", value "//..." → URL guard
            "1key: starts with digit\n" +
            "has.dot: bad charset\n" +
            "toolongkeytoolongkeytoolongkeyxx: over 30\n" +
            $"longval: {longVal}\n" +
            "emptyval:\n" +
            "ok: fine");
        attributes.Should().NotBeNull();
        attributes.Should().HaveCount(1);
        attributes!["ok"].Should().BeEquivalentTo(["fine"]);
    }

    [Fact]
    public void Extract_CapsAtMaxAttributes_AndNullWhenNone()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"key{i}: value{i}");
        AttributeExtractor.Extract(string.Join("\n", lines))!.Should().HaveCount(KnownAttributes.MaxAttributes);
        AttributeExtractor.Extract("Just prose.").Should().BeNull();
        AttributeExtractor.Extract(null).Should().BeNull();
    }

    [Fact]
    public void InlineProseKeyValue_RoutesForARecommendedKey()
    {
        AttributeExtractor.Extract("see file:src/new.cs for the detail")!["file"]
            .Should().Contain("src/new.cs");
    }

    [Fact]
    public void InlineProseKeyValue_IsIgnoredForAnUnrecommendedKey()
    {
        // Prose is uncontrolled text — pasted logs, quoted code. Only the eight keys the guide
        // teaches route from prose; the keywords ARGUMENT still routes any key, because every
        // element there was typed on purpose.
        AttributeExtractor.Extract("TODO:cleanup the retry path")
            .Should().BeNull();
    }

    [Fact]
    public void Extract_BoldLabel_RecommendedKey_ProducesOneCleanValue()
    {
        // The inline-prose pattern is unanchored, so it also fires INSIDE "**File:** src/thing.cs"
        // (the char right before 'F' is '*', which the lookbehind must treat the same as a word
        // char) — without excluding '*' from the lookbehind, this captured the closing "**" as
        // a second value, polluting a multi-valued key. "why" (the only pre-existing bold-label
        // test) never caught this because "why" isn't in RecommendedKeys.
        var attributes = AttributeExtractor.Extract("**File:** src/thing.cs");
        attributes!["file"].Should().BeEquivalentTo(["src/thing.cs"],
            because: "a bold label must still produce exactly one clean value for a recommended key");
    }

    [Fact]
    public void Extract_ItalicLabel_RecommendedKey_DoesNotMintAsteriskValue()
    {
        // Excluding '*' from the inline-prose lookbehind (the bold-label fix, above) also blocks
        // the single-asterisk italic form from routing at all: no attribute is safer than the
        // pre-fix "file: [*]" junk value.
        AttributeExtractor.Extract("*File:* src/thing.cs").Should().BeNull();
    }

    [Fact]
    public void InlineProseKeyValue_TrimsSentenceFinalPunctuation()
    {
        // (\S+) has no sense of where prose ends, so "file:src/thing.cs." captured the trailing
        // period as part of the path — the corrupt value then fails a file-existence check exactly
        // like D-a's original bug, just reached through D-b's new code path instead.
        AttributeExtractor.Extract("see file:src/thing.cs.")!["file"]
            .Should().BeEquivalentTo(["src/thing.cs"]);
    }

    [Fact]
    public void InlineProseKeyValue_TrimsTrailingParenthesis()
    {
        AttributeExtractor.Extract("(file:src/x.cs)")!["file"]
            .Should().BeEquivalentTo(["src/x.cs"]);
    }

    [Theory]
    [InlineData("thing:Foo(bar) is the shape", "thing", "Foo(bar)")]
    [InlineData("topic:retry(exponential) backoff", "topic", "retry(exponential)")]
    public void InlineProseKeyValue_LeavesBalancedDelimitersIntact(string input, string key, string expected)
    {
        // The trailing-punctuation trim must not be balance-blind: a closing delimiter that matches
        // an opener the caller deliberately typed is not swallowed prose, it's part of the value.
        AttributeExtractor.Extract(input)![key].Should().BeEquivalentTo([expected]);
    }

    [Theory]
    [InlineData("file:-")]
    [InlineData("(ref:**)")]
    [InlineData("file:...")]
    [InlineData("ref:--")]
    public void InlineProseKeyValue_RefusesAValueWithNoAlphanumericCharacter(string input)
    {
        // A length check is not a "must be something" check: "-" is length 1 and "**" is length 2,
        // so both routed. `file` and `ref` are union-merged ReferenceKeys and
        // EntityGraph.ParseEntityAttributes yields every key, so `file=["-"]` becomes a graph node
        // SHARED by every memory whose prose contains the shape — the orphan:true hub pathology
        // KnownAttributes.StateMarkerKeys documents, reached from the other direction. Uncontrolled
        // prose (pasted logs, diffs, ASCII tables) is full of these.
        AttributeExtractor.Extract(input).Should().BeNull();
    }

    [Fact]
    public void InlineProseKeyValue_StillRoutesAnOrdinaryValue()
    {
        // The guard above must bar punctuation runs WITHOUT barring real values — a rule that
        // rejected everything would pass every case in the theory and break the feature.
        AttributeExtractor.Extract("see file:src/a.cs here")!["file"]
            .Should().BeEquivalentTo(["src/a.cs"]);
    }

    [Theory]
    [InlineData("ref:[[/api/auth]] has details", "ref", "/api/auth")]
    [InlineData("file:\"src/a.cs\" is quoted", "file", "src/a.cs")]
    [InlineData("file:'src/a.cs' is quoted", "file", "src/a.cs")]
    public void InlineProseKeyValue_StoresTheSameSpellingTheBareExtractorsProduce(
        string input, string key, string expected)
    {
        // TrimTrailingUnbalancedPunctuation leaves a BALANCED wrapper alone — correct for
        // "Foo(bar)", wrong for a reference: "[[/api/auth]]" stored a second spelling of the value
        // ExtractRefAttributes stores as "/api/auth", and "\"src/a.cs\"" fails HashWorkspaceFile's
        // containment check, so that reference silently got no drift baseline at all.
        AttributeExtractor.Extract(input)![key].Should().BeEquivalentTo([expected]);
    }

    [Fact]
    public void InlineProseKeyValue_WikiLinkSpelling_MatchesTheBareWikiLinkExtractor()
    {
        // Derived rather than hardcoded: this is the assertion that stays true if the wiki-link
        // capture ever changes, and it is the whole point of the strip — one reference, one value.
        string bareSpelling = AttributeExtractor.ExtractRefAttributes("[[/api/auth]]")!["ref"].Single();
        AttributeExtractor.Extract("ref:[[/api/auth]] has details")!["ref"]
            .Should().BeEquivalentTo([bareSpelling]);
    }

    [Fact]
    public void InlineProseKeyValue_NormalizesLeadingPathPrefix_ToMatchBarePathSpelling()
    {
        // Without a matching TrimStart, "file:../src/a.cs" stored "../src/a.cs" while a bare mention
        // of the same file (ExtractFileRefs) stores "src/a.cs" — two spellings of one path under the
        // multi-valued "file" key, and HashWorkspaceFile's containment check rejects the
        // un-normalized spelling, so that half silently gets no drift baseline.
        string barePathSpelling = ReferenceExtractor.ExtractFileRefs("../src/a.cs").Single();
        AttributeExtractor.Extract("see file:../src/a.cs for context")!["file"]
            .Should().BeEquivalentTo([barePathSpelling]);
    }

    [Fact]
    public void RouteKeywords_SplitsAttributeShapedFromPlain()
    {
        var (attributes, keywords) = AttributeExtractor.RouteKeywords(
            ["person:Nick", "status:active", "plainword", "http://x.com", "person:Maria"]);

        attributes.Should().NotBeNull();
        attributes!["person"].Should().BeEquivalentTo(["Nick", "Maria"],
            because: "multi-valued keys accumulate; values verbatim");
        attributes["status"].Should().BeEquivalentTo(["active"]);
        keywords.Should().BeEquivalentTo(["plainword", "http://x.com"],
            because: "plain and URL-guarded keywords stay keywords");
    }

    [Fact]
    public void RouteKeywords_NullAndAllPlain()
    {
        AttributeExtractor.RouteKeywords(null).Should().Be((null, null));
        var (f, k) = AttributeExtractor.RouteKeywords(["alpha", "beta"]);
        f.Should().BeNull();
        k.Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public void SeedTermFrequencies_Plus3PerTokenFromKeysAndAllValues()
    {
        var attributes = new Dictionary<string, string[]> { ["verdict"] = ["rejected", "quiescent decision"] };
        var tf = new Dictionary<string, int> { ["decision"] = 1 };

        AttributeExtractor.SeedTermFrequencies(attributes, tf);

        tf["verdict"].Should().Be(3);
        tf["rejected"].Should().Be(3);
        tf["quiescent"].Should().Be(3);
        tf["decision"].Should().Be(4, because: "+3 stacks on the existing count");
    }

    [Fact]
    public void SeedTermFrequencies_NullOrEmpty_NoOp()
    {
        var tf = new Dictionary<string, int>();
        AttributeExtractor.SeedTermFrequencies(null, tf);
        AttributeExtractor.SeedTermFrequencies(new(), tf);
        tf.Should().BeEmpty();
    }

    [Fact]
    public void ArtifactEntry_Attributes_RoundTripsThroughIndexSaveLoad()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);

        var entry = new Scrinia.Core.Models.ArtifactEntry(
            Name: "attributes-roundtrip", Uri: "file:///dev/null", OriginalBytes: 10, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Attributes: new Dictionary<string, string[]> { ["person"] = ["Alice", "Bob"], ["status"] = ["no-go"] });

        store.Upsert(entry, "local");
        var reloaded = store.LoadIndex("local").Single(e => e.Name == "attributes-roundtrip");

        reloaded.Attributes!["person"].Should().BeEquivalentTo(["Alice", "Bob"]);
        reloaded.Attributes["status"].Should().BeEquivalentTo(["no-go"], because: "values verbatim through JSON");
    }

    private static Scrinia.Core.Models.ArtifactEntry MakeEntry(
        string name, Dictionary<string, string[]>? attributes = null, string[]? keywords = null) =>
        new(
            Name: name, Uri: "file:///dev/null", OriginalBytes: 10, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Keywords: keywords, Attributes: attributes);

    [Fact]
    public void SearchAll_AttributeValueExact_OutranksAttributelessEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        store.Upsert(MakeEntry("with-field",
            new Dictionary<string, string[]> { ["verdict"] = ["quiescent"] }), "local");
        store.Upsert(MakeEntry("without-field"), "local");

        var results = store.SearchAll(new SearchQuery("quiescent") { Limit = 10 });
        results.OfType<Scrinia.Core.Search.EntryResult>().First().Item.Entry.Name
            .Should().Be("with-field");
    }

    [Fact]
    public void SearchAll_CompoundKeyValueTerm_OutranksValueOnlyMatch()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        store.Upsert(MakeEntry("right-key",
            new Dictionary<string, string[]> { ["verdict"] = ["quiescent"] }), "local");
        store.Upsert(MakeEntry("wrong-key",
            new Dictionary<string, string[]> { ["mood"] = ["quiescent"] }), "local");

        var results = store.SearchAll(new SearchQuery("verdict:quiescent") { Limit = 10 });
        results.OfType<Scrinia.Core.Search.EntryResult>().First().Item.Entry.Name
            .Should().Be("right-key", because: "pair exact (50) outranks value-only exact (40)");
    }

    [Fact]
    public void SearchAll_LegacyKeyValueKeyword_ScoresLikeAnAttribute()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        // Pre-migration entry: person:maria lives in Keywords, Attributes is null.
        store.Upsert(MakeEntry("legacy", keywords: ["person:maria"]), "local");
        store.Upsert(MakeEntry("bystander"), "local");

        var results = store.SearchAll(new SearchQuery("person:maria") { Limit = 10 });
        results.OfType<Scrinia.Core.Search.EntryResult>().First().Item.Entry.Name
            .Should().Be("legacy",
            because: "read-time forward-fill (AttributeMigration) promotes key:value-shaped legacy " +
                "keywords into Attributes before scoring runs, so the Attributes block scores it identically " +
                "without the scorer parsing Keywords itself (P6.5: that direct dual-read was retired)");
    }
}
