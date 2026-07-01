using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="FieldExtractor"/> — mechanical key:value extraction (frontmatter,
/// line-start pairs, bold labels) and agent-keyword routing into the unified Fields store.
/// </summary>
public sealed class FieldExtractorTests
{
    [Fact]
    public void Extract_FrontmatterAndLineStartAndBoldLabel()
    {
        string content = "---\nStatus: Active\n---\nverdict: no-go\n**Why:** taxes retrieval\nProse: is skipped when value is prose? No — this extracts too.";
        var fields = FieldExtractor.Extract(content);

        fields.Should().NotBeNull();
        fields!["status"].Should().BeEquivalentTo(["Active"], because: "keys lowercase, values verbatim");
        fields["verdict"].Should().BeEquivalentTo(["no-go"]);
        fields["why"].Should().BeEquivalentTo(["taxes retrieval"]);
    }

    [Fact]
    public void Extract_BoldLabel_ColonOutsideVariant()
    {
        FieldExtractor.Extract("**Status**: active")!["status"].Should().BeEquivalentTo(["active"]);
    }

    [Fact]
    public void Extract_MultiValuedKey_AccumulatesWithinText()
    {
        var fields = FieldExtractor.Extract("person: Alice\nperson: Bob");
        fields!["person"].Should().BeEquivalentTo(["Alice", "Bob"],
            because: "entity keys accumulate within one text");
    }

    [Fact]
    public void Extract_ScalarKey_FirstWinsWithinText()
    {
        var fields = FieldExtractor.Extract("status: first\nstatus: second");
        fields!["status"].Should().BeEquivalentTo(["first"],
            because: "scalar keys are first-wins within one extraction pass");
    }

    [Fact]
    public void Extract_Guards_FenceUrlCharsetLengthEmpty()
    {
        string longVal = new string('x', 121);
        var fields = FieldExtractor.Extract(
            "```\nfenced: skipped\n```\n" +
            "http://example.com\n" +          // key "http", value "//..." → URL guard
            "1key: starts with digit\n" +
            "has.dot: bad charset\n" +
            "toolongkeytoolongkeytoolongkeyxx: over 30\n" +
            $"longval: {longVal}\n" +
            "emptyval:\n" +
            "ok: fine");
        fields.Should().NotBeNull();
        fields.Should().HaveCount(1);
        fields!["ok"].Should().BeEquivalentTo(["fine"]);
    }

    [Fact]
    public void Extract_CapsAtMaxFields_AndNullWhenNone()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"key{i}: value{i}");
        FieldExtractor.Extract(string.Join("\n", lines))!.Should().HaveCount(KnownFields.MaxFields);
        FieldExtractor.Extract("Just prose.").Should().BeNull();
        FieldExtractor.Extract(null).Should().BeNull();
    }

    [Fact]
    public void RouteKeywords_SplitsFieldShapedFromPlain()
    {
        var (fields, keywords) = FieldExtractor.RouteKeywords(
            ["person:Nick", "status:active", "plainword", "http://x.com", "person:Maria"]);

        fields.Should().NotBeNull();
        fields!["person"].Should().BeEquivalentTo(["Nick", "Maria"],
            because: "multi-valued keys accumulate; values verbatim");
        fields["status"].Should().BeEquivalentTo(["active"]);
        keywords.Should().BeEquivalentTo(["plainword", "http://x.com"],
            because: "plain and URL-guarded keywords stay keywords");
    }

    [Fact]
    public void RouteKeywords_NullAndAllPlain()
    {
        FieldExtractor.RouteKeywords(null).Should().Be((null, null));
        var (f, k) = FieldExtractor.RouteKeywords(["alpha", "beta"]);
        f.Should().BeNull();
        k.Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public void SeedTermFrequencies_Plus3PerTokenFromKeysAndAllValues()
    {
        var fields = new Dictionary<string, string[]> { ["verdict"] = ["rejected", "quiescent decision"] };
        var tf = new Dictionary<string, int> { ["decision"] = 1 };

        FieldExtractor.SeedTermFrequencies(fields, tf);

        tf["verdict"].Should().Be(3);
        tf["rejected"].Should().Be(3);
        tf["quiescent"].Should().Be(3);
        tf["decision"].Should().Be(4, because: "+3 stacks on the existing count");
    }

    [Fact]
    public void SeedTermFrequencies_NullOrEmpty_NoOp()
    {
        var tf = new Dictionary<string, int>();
        FieldExtractor.SeedTermFrequencies(null, tf);
        FieldExtractor.SeedTermFrequencies(new(), tf);
        tf.Should().BeEmpty();
    }
}
