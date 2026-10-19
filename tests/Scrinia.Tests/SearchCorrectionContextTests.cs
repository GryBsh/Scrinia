using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class SearchCorrectionContextTests
{
    [Theory]
    [InlineData(FusionMode.Sum)]
    [InlineData(FusionMode.Rrf)]
    public async Task Search_ExactOlderMatch_DoesNotHideMatchingCorrection(FusionMode fusion)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.Remember([
            "Conference travel reimbursement allowance is 700 dollars.",
            "Vegetable garden: plant carrots in spring.",
            "Correction dated 2026-06-12: the reimbursement allowance is 950 dollars; 700 was incorrect."
        ], MemoryAddress.Parse("/people/alex/travel"));
        var store = MemoryStoreContext.Current!;
        // Read through the public store's scope inventory instead of depending on path routing.
        var matches = store.SearchAll(new SearchQuery("conference travel reimbursement allowance"));
        var parent = matches.Select(r => r switch {
            EntryResult e => e.Item, ChunkEntryResult c => c.ParentItem, _ => null
        }).First(p => p is not null)!;
        matches = new WeightedAttributeScorer(new RankerOptions(Fusion: fusion))
            .SearchAll("conference travel reimbursement allowance", [parent], [], 1);
        var rendered = SearchResultRenderer.FormatRichSearchResults(store, matches, []);
        matches.Should().ContainSingle();
        rendered.Should().Contain("950 dollars").And.Contain("700 dollars");
        rendered.Should().NotContain("[chunk 2/3]");
    }

    [Fact]
    public async Task Search_AppendToLegacySingleChunk_SurfacesCorrectionAlongsideParent()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.Remember(["Conference travel reimbursement allowance is 700 dollars."],
            MemoryAddress.Parse("/travel"));
        await tools.Append("Correction: reimbursement allowance is 950 dollars.", MemoryAddress.Parse("/travel"));
        var result = await tools.Search("conference travel reimbursement allowance", limit: 1);
        result.ToYaml().Should().Contain("950 dollars").And.Contain("700 dollars");
    }

    [Fact]
    public async Task Search_ContextIsBounded_PreservesWinnerAndLatestMatches()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember([
            "Conference travel reimbursement allowance is 700 dollars.",
            "Reimbursement note two. " + new string('x', 900),
            "Reimbursement note three. " + new string('x', 900),
            "Reimbursement note four. " + new string('x', 900),
            "Reimbursement correction: allowance is 950 dollars. " + new string('x', 900),
            "Reimbursement confirmation: 950 dollars. " + new string('x', 900),
            "Vegetable garden: plant carrots."
        ], MemoryAddress.Parse("/travel"));
        var matches = MemoryStoreContext.Current!.SearchAll(
            new SearchQuery("conference travel reimbursement allowance") { Limit = 1 });
        var match = matches.Should().ContainSingle().Subject;
        match.ContextPreview.Should().NotBeNull();
        match.ContextPreview!.Length.Should().BeLessThanOrEqualTo(2000);
        match.ContextPreview.Should().Contain("700 dollars").And.Contain("950 dollars")
            .And.Contain("additional matching chunks")
            .And.NotContain("[chunk 2/7]").And.NotContain("[chunk 7/7]");
        Scrinia.Commands.ScriniaCommands.BuildCliSearchResult(match).Description
            .Should().Contain("950 dollars");
    }

    [Fact]
    public async Task Search_ParentWins_StillIncludesMatchingCorrection()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember([
            "Conference travel reimbursement allowance is 700 dollars.",
            "Correction: reimbursement allowance is 950 dollars."
        ], MemoryAddress.Parse("/travel"));
        var store = MemoryStoreContext.Current!;
        var existing = store.SearchAll(new SearchQuery("reimbursement"))[0];
        var parent = existing is EntryResult e ? e.Item : ((ChunkEntryResult)existing).ParentItem;
        parent = parent with { Entry = parent.Entry with {
            ContentPreview = "Curated travel policy. " + new string('x', 600) + " Exception: contractors receive 1100 dollars."
        }};
        var matches = new WeightedAttributeScorer().SearchAll("reimbursement", [parent], [], 1,
            new Dictionary<string, double> { [$"{parent.Scope}|{parent.Entry.Name}"] = 100000,
                [$"{parent.Scope}|{parent.Entry.Name}|1"] = 0, [$"{parent.Scope}|{parent.Entry.Name}|2"] = 0 });
        matches.Should().ContainSingle().Which.Should().BeOfType<EntryResult>();
        SearchResultRenderer.FormatRichSearchResults(store, matches, [])
            .Should().Contain("950 dollars").And.Contain("700 dollars").And.Contain("contractors receive 1100 dollars");
    }

    [Fact]
    public async Task SharedSearch_ExposesCorrectionInSameMemoryHit()
    {
        using var fixture = new Shared.SharedOperationFixture();
        await fixture.Tools.Remember("team", "/travel", [
            "Conference travel reimbursement allowance is 700 dollars.",
            "Vegetable garden: plant carrots.",
            "Correction: reimbursement allowance is 950 dollars."
        ], description: "Conference travel reimbursement allowance is 700 dollars.");
        var response = await fixture.Tools.Search("conference travel reimbursement allowance", limit: 1);
        response.Should().Contain("950 dollars").And.Contain("700 dollars")
            .And.NotContain("[chunk 2/3]");
    }

    [Fact]
    public async Task Search_PreviewOnlyIndex_PreservesCorrectionNearEndOfPreview()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember([
            "Conference travel reimbursement allowance is 700 dollars.",
            "Reimbursement update: " + new string('x', 360) + " Correction: allowance is 950 dollars."
        ], MemoryAddress.Parse("/travel"));
        var store = MemoryStoreContext.Current!;
        var hit = store.SearchAll(new SearchQuery("reimbursement"))[0];
        var parent = hit is EntryResult e ? e.Item : ((ChunkEntryResult)hit).ParentItem;
        parent = parent with { Entry = parent.Entry with {
            ChunkEntries = parent.Entry.ChunkEntries!.Select(c => c with { TermFrequencies = null, Keywords = null }).ToArray()
        }};
        var matches = new WeightedAttributeScorer().SearchAll("conference travel reimbursement allowance", [parent], [], 1);
        SearchResultRenderer.FormatRichSearchResults(store, matches, []).Should().Contain("950 dollars");
    }

    [Fact]
    public async Task Search_MetadataOnlyMatch_DoesNotAddUnrelatedSiblingContext()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["Carrots in spring.", "Tomatoes in summer."],
            MemoryAddress.Parse("/travel"), keywords: ["reimbursement"]);
        var hit = MemoryStoreContext.Current!.SearchAll(new SearchQuery("reimbursement") { Limit = 1 })[0];
        hit.ContextPreview.Should().BeNull();
    }
}
