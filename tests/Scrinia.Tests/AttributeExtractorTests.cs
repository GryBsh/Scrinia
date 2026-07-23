using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="AttributeExtractor"/> — mechanical key:value extraction (frontmatter,
/// line-start pairs, bold labels) and agent-keyword routing into the attribute map.
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

        var results = store.SearchAll("quiescent", scopes: null, limit: 10);
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

        var results = store.SearchAll("verdict:quiescent", scopes: null, limit: 10);
        results.OfType<Scrinia.Core.Search.EntryResult>().First().Item.Entry.Name
            .Should().Be("right-key", because: "pair exact (50) outranks value-only exact (40)");
    }

    [Fact]
    public void SearchAll_LegacyTypedKeyword_ScoresLikeAnAttribute()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        // Pre-migration entry: person:maria lives in Keywords, Attributes is null.
        store.Upsert(MakeEntry("legacy", keywords: ["person:maria"]), "local");
        store.Upsert(MakeEntry("bystander"), "local");

        var results = store.SearchAll("person:maria", scopes: null, limit: 10);
        results.OfType<Scrinia.Core.Search.EntryResult>().First().Item.Entry.Name
            .Should().Be("legacy",
            because: "read-time forward-fill (AttributeMigration) promotes key:value-shaped legacy " +
                "keywords into Attributes before scoring runs, so the Attributes block scores it identically " +
                "without the scorer parsing Keywords itself (P6.5: that direct dual-read was retired)");
    }
}
