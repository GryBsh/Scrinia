using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// <see cref="IMemoryStore.ListScoped(IReadOnlyList{string}?)"/> promises a COMPLETE listing: an
/// implementation backed by a paged source must page to exhaustion before returning, because
/// returning one page is not a degraded answer — it is a wrong one, and a silent one. The caller
/// sees a short list, not an error.
///
/// <para><b>Why this file exists.</b> That promise had a conformance suite,
/// <c>PagedListingConformanceTests</c>, which drove it through the remote store and was deleted
/// along with it. Nothing tested the contract afterwards. The one surviving implementation reads a
/// directory and cannot page, so there is no live defect — but four default interface methods rest
/// on the promise, and the next paged source inherits the trap with no test standing in front of
/// it.</para>
///
/// <para><b>What is actually asserted.</b> Not "the defaults are safe" — they are not, and cannot
/// be. The contract is that they are <b>exactly as complete as <c>ListScoped</c> is, no more and no
/// less</b>. So each test below runs one of the four through a double that pages correctly and
/// through an identical double that stops after the first page, and pins that the second one
/// silently under-reports. That is the blast radius, made executable: it is the argument for why
/// <c>ListScoped</c> must page to exhaustion, rather than a claim that something downstream will
/// catch it if it does not.</para>
///
/// <para>The last test drives the real <see cref="FileMemoryStore"/>, which satisfies the contract
/// trivially. It is here so the suite has a production implementation in it, and so a store that
/// ever does page has somewhere obvious to be added.</para>
/// </summary>
[Collection(BackgroundWorkCollection.Name)]
public sealed class ListScopedCompletenessTests
{
    private const int PageSize = 2;

    /// <summary>
    /// Seven entries across three scopes, arranged so the interesting facts are on the LAST page:
    /// the only <c>person:zoe</c> mention and the only entry in <c>local:archive</c> both sit at
    /// the end. A truncating listing keeps the first page and loses exactly those.
    /// </summary>
    private static List<ScopedArtifact> Corpus() =>
    [
        Scoped("local", "alpha", "person:ana"),
        Scoped("local", "beta", "person:ana"),
        Scoped("local:notes", "gamma", "person:ben"),
        Scoped("local:notes", "delta", "person:ben"),
        Scoped("local:notes", "epsilon", "person:ben"),
        Scoped("local:archive", "zeta", "person:zoe"),
        Scoped("local:archive", "eta", "person:zoe"),
    ];

    private static ScopedArtifact Scoped(string scope, string name, string entityKeyword) =>
        new(scope, new ArtifactEntry(name, $"uri://{name}", 100, 1, DateTimeOffset.UtcNow,
            Keywords: [entityKeyword]));

    [Fact]
    public void PagingToExhaustion_ReturnsEveryEntry_WhileStoppingAtOnePageDoesNot()
    {
        IMemoryStore complete = new PagedFakeStore(Corpus(), PageSize, truncate: false);
        IMemoryStore truncated = new PagedFakeStore(Corpus(), PageSize, truncate: true);

        complete.ListScoped(null).Should().HaveCount(7,
            because: "a listing that pages to exhaustion returns the whole corpus");
        truncated.ListScoped(null).Should().HaveCount(PageSize,
            because: "this is the defect the contract exists to forbid — and note it returns a " +
                     "perfectly well-formed short list rather than failing");
    }

    /// <summary>
    /// <see cref="IMemoryStore.AnyScoped"/>'s default is <c>ListScoped(scopes).Any(predicate)</c>.
    /// A truncated listing turns "does anything match?" into "does anything on the first page
    /// match?", and the answer comes back <c>false</c> — indistinguishable from a genuine absence.
    /// </summary>
    [Fact]
    public void AnyScoped_IsExactlyAsCompleteAsTheListing()
    {
        bool MatchesZoe(ScopedArtifact a) =>
            a.Entry.Keywords?.Contains("person:zoe", StringComparer.OrdinalIgnoreCase) == true;

        IMemoryStore complete = new PagedFakeStore(Corpus(), PageSize, truncate: false);
        IMemoryStore truncated = new PagedFakeStore(Corpus(), PageSize, truncate: true);

        complete.AnyScoped(null, MatchesZoe).Should().BeTrue();
        truncated.AnyScoped(null, MatchesZoe).Should().BeFalse(
            because: "the only zoe mention is past the first page, so the default reports a clean " +
                     "'no such entry' for something the store holds");
    }

    /// <summary>
    /// <see cref="IMemoryStore.GetEntityFactStore"/>'s default rebuilds from
    /// <c>ListScoped(null)</c>. Facts derived over a truncated listing are wrong, not partial —
    /// nothing downstream can tell that an entity is missing rather than absent.
    /// </summary>
    [Fact]
    public void GetEntityFactStore_IsExactlyAsCompleteAsTheListing()
    {
        var zoe = EntityRef.Of("person", "zoe");

        IMemoryStore complete = new PagedFakeStore(Corpus(), PageSize, truncate: false);
        IMemoryStore truncated = new PagedFakeStore(Corpus(), PageSize, truncate: true);

        complete.GetEntityFactStore().MentionCount(zoe).Should().Be(2);
        truncated.GetEntityFactStore().MentionCount(zoe).Should().Be(0,
            because: "an entity whose every mention is past the first page simply does not exist " +
                     "as far as the fact store is concerned");
    }

    /// <summary>
    /// The worst of the four, and the one the original defect actually produced: a truncated
    /// listing does not merely lose entries, it loses WHOLE SCOPES. Every entry in
    /// <c>local:archive</c> sits past the first page, so the scope stops being discoverable and a
    /// caller filtering to it gets an empty answer for a topic that exists.
    /// </summary>
    [Fact]
    public void ResolveReadScopes_LosesAWholeScope_WhenTheListingTruncates()
    {
        IMemoryStore complete = new PagedFakeStore(Corpus(), PageSize, truncate: false);
        IMemoryStore truncated = new PagedFakeStore(Corpus(), PageSize, truncate: true);

        complete.ResolveReadScopes(null).Should()
            .BeEquivalentTo(["local", "local:notes", "local:archive"]);
        truncated.ResolveReadScopes(null).Should().NotContain("local:archive",
            because: "scope discovery reads through the same listing, so a scope with no entries " +
                     "on the first page disappears entirely");
    }

    /// <summary>
    /// <see cref="IMemoryStore.ListScoped(IReadOnlyList{string}?, IReadOnlyList{string}?)"/>'s
    /// default post-filters the listing, which is correct ONLY because the listing is complete.
    /// Post-filtering a short list is a short list with fewer things in it.
    /// </summary>
    [Fact]
    public void ExcludeTopicsOverload_IsExactlyAsCompleteAsTheListing()
    {
        IMemoryStore complete = new PagedFakeStore(Corpus(), PageSize, truncate: false);
        IMemoryStore truncated = new PagedFakeStore(Corpus(), PageSize, truncate: true);

        complete.ListScoped(null, ["notes"]).Should().HaveCount(4,
            because: "seven entries less the three in local:notes");
        truncated.ListScoped(null, ["notes"]).Should().HaveCount(PageSize,
            because: "the exclusion had nothing to do — both first-page entries are in local — so " +
                     "the shortfall is the listing's, invisibly");
    }

    /// <summary>
    /// The production implementation, in the suite so the suite has one. A directory read cannot
    /// page, so this passes trivially — which is the point: it is where a store that DOES page
    /// gets added, next to the doubles above that show what happens if it pages wrongly.
    /// </summary>
    [Fact]
    public async Task FileMemoryStore_ListsEveryEntryAcrossEveryScope()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("alpha", "local", ["first"]));
        await store.StoreAsync(new MemoryWrite("beta", "local", ["second"]));
        await store.StoreAsync(new MemoryWrite("gamma", "local:notes", ["third"]));
        await store.StoreAsync(new MemoryWrite("delta", "local:archive", ["fourth"]));

        var listed = store.ListScoped(null);

        listed.Should().HaveCount(4);
        listed.Select(a => a.Scope).Distinct().Should()
            .BeEquivalentTo(["local", "local:notes", "local:archive"]);
    }

    /// <summary>
    /// A store whose listing arrives in pages, with a switch for the one mistake that matters.
    /// <paramref name="truncate"/> serves the first page and stops — precisely what the deleted
    /// <c>HttpMemoryStore</c> did by sending no <c>limit</c> against a server that defaulted to 200
    /// rows.
    /// <para><see cref="ResolveReadScopes"/> derives from the same listing on purpose: that is how
    /// a store without a filesystem to scan has to answer the question, and it is why scope
    /// discovery inherits the truncation rather than being independent of it.</para>
    /// </summary>
    private sealed class PagedFakeStore(List<ScopedArtifact> all, int pageSize, bool truncate)
        : FakeMemoryStore
    {
        public override List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null)
        {
            var source = scopes is { Count: > 0 }
                ? all.Where(a => scopes.Contains(a.Scope, StringComparer.OrdinalIgnoreCase)).ToList()
                : all;

            var result = new List<ScopedArtifact>();
            for (int offset = 0; offset < source.Count; offset += pageSize)
            {
                result.AddRange(source.Skip(offset).Take(pageSize));
                if (truncate) break;
            }
            return result;
        }

        public override IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null) =>
            scopes is { Count: > 0 }
                ? scopes
                : [.. ListScoped(null).Select(a => a.Scope).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
