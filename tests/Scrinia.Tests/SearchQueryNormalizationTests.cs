using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Task 9 fix round 1 (F-1/F-2/F-3): <see cref="SearchQuery.Scopes"/> and
/// <see cref="SearchQuery.ExcludeTopics"/> normalize through <c>MemoryNaming.NormalizeFilterList</c>
/// in their own <c>init</c> accessors, so no construction path can hand a downstream consumer a
/// null element or a blank entry. This is load-bearing independently of any store-side guard: a
/// store built directly over a <see cref="SearchQuery"/> — <c>HttpMemoryStore.SearchAll</c> in
/// particular, which sends <c>query.Scopes</c> straight onto the wire with no re-normalization of
/// its own — sees only what this record already cleaned up.
/// </summary>
public class SearchQueryNormalizationTests
{
    [Fact]
    public void Scopes_ANullElement_IsDroppedAtConstruction()
    {
        var query = new SearchQuery("q") { Scopes = new string[] { null! } };

        query.Scopes.Should().BeNull(
            "a scopes array containing only a null element normalizes away to \"no filter\"");
    }

    [Fact]
    public void Scopes_ABlankAndANonBlankEntry_KeepsOnlyTheTrimmedNonBlankEntry()
    {
        var query = new SearchQuery("q") { Scopes = ["", "  api  ", null!] };

        query.Scopes.Should().BeEquivalentTo(["api"],
            "blank and null entries must be dropped, and the survivor trimmed, matching the old " +
            "comma-split's RemoveEmptyEntries | TrimEntries semantics");
    }

    [Fact]
    public void Scopes_AnEmptyArray_NormalizesToNull()
    {
        var query = new SearchQuery("q") { Scopes = [] };

        query.Scopes.Should().BeNull("Task 9, D-4: an empty list means \"no filter\", identically to null");
    }

    [Fact]
    public void ExcludeTopics_ANullElement_IsDroppedAtConstruction()
    {
        var query = new SearchQuery("q") { ExcludeTopics = new string[] { "plan", null! } };

        query.ExcludeTopics.Should().BeEquivalentTo(["plan"]);
    }
}
