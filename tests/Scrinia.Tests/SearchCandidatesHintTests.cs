using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// SearchQuery.Candidates is a cost hint: supplying it may save the store a ListScoped, but it
/// must never change which memories come back. Before this, a caller who materialized a wider
/// slice than the query asked for got results from scopes the query excluded — silently, with
/// no error, and only if you knew to look.
/// </summary>
public class SearchCandidatesHintTests
{
    private static ArtifactEntry Entry(string name) =>
        new(name, $"file:///{name}.nmp2", 64, 1, DateTimeOffset.UtcNow)
        {
            ContentPreview = "shared needle text",
        };

    /// <summary>
    /// SearchResult carries only Score; the name lives on each derived type. Chunk results are
    /// folded to their PARENT's name deliberately — a leaked out-of-scope memory must fail these
    /// assertions whether it surfaced as a whole entry or as one of its chunks.
    /// </summary>
    private static IEnumerable<string> Names(IReadOnlyList<SearchResult> results) =>
        results.Select(r => r switch
        {
            EntryResult e => e.Item.Entry.Name,
            ChunkEntryResult c => c.ParentItem.Entry.Name,
            TopicResult t => t.TopicName,
            _ => throw new InvalidOperationException($"unhandled result type {r.GetType().Name}"),
        });

    [Fact]
    public void SearchAll_WithCandidatesFromAWiderSlice_IgnoresTheOutOfScopeOnes()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        store.Upsert(Entry("alpha-note"), "local:alpha");
        store.Upsert(Entry("beta-note"), "local:beta");

        // A caller that materialized EVERY scope, then searches asking for alpha only.
        var everything = store.ListScoped(null, null);
        everything.Select(c => c.Scope).Should().Contain(["local:alpha", "local:beta"]);

        var results = store.SearchAll(new SearchQuery("needle")
        {
            Scopes = ["local:alpha"],
            Candidates = everything,
        });

        Names(results).Should().NotContain("beta-note",
            because: "Candidates is a hint about cost, never about the answer");
        Names(results).Should().Contain("alpha-note",
            because: "the in-scope memory must still come back — an empty result set would pass the assertion above vacuously");
    }

    [Fact]
    public void SearchAll_WithCandidatesIgnoringExcludeTopics_StillExcludesThem()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        store.Upsert(Entry("alpha-note"), "local:alpha");
        store.Upsert(Entry("beta-note"), "local:beta");

        var results = store.SearchAll(new SearchQuery("needle")
        {
            ExcludeTopics = ["beta"],
            Candidates = store.ListScoped(null, null),
        });

        Names(results).Should().NotContain("beta-note",
            because: "an excludeTopics filter the caller ignored is still this query's filter");
        Names(results).Should().Contain("alpha-note",
            because: "the in-scope memory must still come back — an empty result set would pass the assertion above vacuously");
    }

    [Fact]
    public void SearchAll_WithCorrectlyScopedCandidates_ReturnsTheSameAsNoCandidates()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        store.Upsert(Entry("alpha-note"), "local:alpha");
        store.Upsert(Entry("beta-note"), "local:beta");

        var withHint = store.SearchAll(new SearchQuery("needle")
        {
            Scopes = ["local:alpha"],
            Candidates = store.ListScoped(["local:alpha"], null),
        });
        var withoutHint = store.SearchAll(new SearchQuery("needle") { Scopes = ["local:alpha"] });

        Names(withHint).Should().Contain("alpha-note",
            because: "both runs are expected to return the in-scope memory — equivalence between two empty sets would prove nothing");
        Names(withHint).Should().BeEquivalentTo(Names(withoutHint),
            because: "the hint must be invisible in the results when the caller honoured it");
    }

    /// <summary>Task 9, D-4: SearchQuery.Scopes=[] must behave identically to Scopes=null — an
    /// empty array is not a narrower filter, it is the same "every scope" answer null gives.</summary>
    [Fact]
    public void SearchAll_EmptyScopesAndExcludeTopics_BehaveIdenticallyToNull()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        store.Upsert(Entry("alpha-note"), "local:alpha");
        store.Upsert(Entry("beta-note"), "local:beta");

        var withEmpty = store.SearchAll(new SearchQuery("needle") { Scopes = [], ExcludeTopics = [] });
        var withNull = store.SearchAll(new SearchQuery("needle") { Scopes = null, ExcludeTopics = null });

        Names(withEmpty).Should().Contain("alpha-note")
            .And.Contain("beta-note", "an empty scopes/excludeTopics list must not narrow the result set");
        Names(withEmpty).Should().BeEquivalentTo(Names(withNull));
    }
}
