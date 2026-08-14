using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Anchors the specific worked examples quoted inside the <c>remember</c>/<c>append</c>
/// <c>content</c> parameter descriptions (<c>Scrinia.Mcp.MemoryTools</c>, lines ~111-127 and
/// ~268-289 as of Task 7) to the real extractors those descriptions describe. Task 7's own
/// falsehoods survived an entire branch that changed the behaviour they described because no
/// test touched the description strings — every behavioural test stayed green while the prose
/// drifted. This file exists to close exactly that gap.
///
/// <para>Wording in the descriptions is free to change; enshrining an exact sentence would only
/// get mechanically rewritten by the next editor without re-verification (see the Task 7 report's
/// Step 5). What is pinned here instead is that the SPECIFIC examples the description text uses
/// to illustrate each rule still behave the way the description claims. A change to routing
/// rules, the fence delimiter, or the recommended-key set fails here — a change to the prose
/// around them does not.</para>
/// </summary>
public sealed class McpContentDescriptionExamplesTests
{
    [Fact]
    public void InlineKeyValue_FileExample_RoutesToFileAttribute()
    {
        // "an inline 'key:value' with no space (e.g. 'file:src/x.cs') ... becomes a 'file' attribute"
        AttributeExtractor.Extract("file:src/x.cs")!["file"].Should().BeEquivalentTo(["src/x.cs"]);
    }

    [Fact]
    public void LineStartKey_SharpSuffixedKey_DoesNotRoute()
    {
        // "mints an attribute under any key that starts with a letter (so 'C#:' and '2024:' don't route)"
        AttributeExtractor.Extract("C#: foo").Should().BeNull();
    }

    [Fact]
    public void LineStartKey_DigitLeadingKey_DoesNotRoute()
    {
        // same clause, the '2024:' half
        AttributeExtractor.Extract("2024: foo").Should().BeNull();
    }

    [Fact]
    public void BareFilePath_NonQualifyingExtensions_DoNotExtract()
    {
        // "a name with no matching extension, like 'Dockerfile' or 'app.py', does not qualify"
        ReferenceExtractor.ExtractFileRefs("Dockerfile").Should().BeEmpty();
        ReferenceExtractor.ExtractFileRefs("app.py").Should().BeEmpty();
    }

    [Fact]
    public void WikiLinkAndEphemeral_BecomeRefAttribute()
    {
        // "a '[[wiki-link]]' or '~ephemeral' reference becomes a 'ref' attribute"
        AttributeExtractor.ExtractRefAttributes("[[/api/auth]]")!["ref"]
            .Should().Contain("/api/auth");
        AttributeExtractor.ExtractRefAttributes("~ephemeral")!["ref"]
            .Should().Contain("~ephemeral");
    }

    [Fact]
    public void ClosedFence_ExcludesFilePath_FromDriftBaseline()
    {
        // "a pasted build log or diff still contributes every path in it as a 'file' attribute
        // unless you wrap it in a properly closed fenced code block"
        AttributeExtractor.ExtractRefAttributes("```\nsrc/x.cs\n```").Should().BeNull();
    }

    [Fact]
    public void UnterminatedFence_IsNotAFence_FilePathStillExtracts()
    {
        // "An opener ``` that never closes isn't a fence at all — the opener line and everything
        // after it read as prose too."
        AttributeExtractor.ExtractRefAttributes("```\nsrc/x.cs\n")!["file"]
            .Should().Contain("src/x.cs");
    }

    [Fact]
    public void AppendMerge_MultiValuedKeyUnions_ScalarKeyReplaces()
    {
        // append's content description: "merges what it finds with what's already stored
        // (multi-valued keys union, others replace)"
        var existing = new Dictionary<string, string[]> { ["file"] = ["a.cs"], ["review"] = ["2026-01-01"] };
        var incoming = new Dictionary<string, string[]> { ["file"] = ["b.cs"], ["review"] = ["2026-02-01"] };

        var merged = KnownAttributes.Merge(existing, incoming);

        merged!["file"].Should().BeEquivalentTo(["a.cs", "b.cs"], because: "'file' is a reference key: union");
        merged["review"].Should().BeEquivalentTo(["2026-02-01"], because: "'review' is scalar: newer wins");
    }
}
