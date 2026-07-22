using System.Text.RegularExpressions;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Task 18 regression coverage: when a supplemental (embeddings) score contributor is active,
/// <c>excludeTopics</c> must filter candidates OUT of the pool BEFORE the <c>limit</c> cut is
/// applied — not after. The bug: MemoryTools.Core.cs's Search() called store.SearchAll WITHOUT
/// excludeTopics on the supplemental-active branch, then post-filtered the already-limited
/// result set, so excluded-topic hits could consume every result slot and the caller silently
/// got fewer than `limit` results even though enough non-excluded matches existed. The
/// non-supplemental branch always passed excludeTopics straight into SearchAll and did not have
/// this bug. See MemoryTools.Core.cs (Search method, embeddings-active branch).
/// </summary>
public sealed class SearchExcludeTopicsTests
{
    /// <summary>
    /// Minimal supplemental contributor stub — assigns a uniform positive score to whatever
    /// candidates it's handed. Its only job is to make `supplemental is {{ Count: > 0 }}` true so
    /// MemoryTools.Core.cs::Search takes the embeddings-active branch under test. Mirrors how a
    /// real embeddings plugin only ever sees (and scores) the excludeTopics-filtered candidate
    /// list handed to it by Search().
    /// </summary>
    private sealed class FakeScoreContributor : ISearchScoreContributor
    {
        public Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(
            string query, IReadOnlyList<ScopedArtifact> candidates, IMemoryStore store, CancellationToken ct)
        {
            var dict = candidates.ToDictionary(c => $"{c.Scope}|{c.Entry.Name}", _ => 1.0);
            return Task.FromResult<IReadOnlyDictionary<string, double>?>((IReadOnlyDictionary<string, double>)dict);
        }
    }

    [Fact]
    public async Task Search_SupplementalActive_ExcludeTopicsFiltersBeforeLimit()
    {
        using var storeScope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        // Excluded-topic entries ("plan"): exact "starts with" match on the query gives field
        // score 30 — unambiguously higher than the non-excluded entries below, so under the bug
        // these are the ones that win the unfiltered top-K cut before post-filtering removes them.
        for (int i = 0; i < 3; i++)
        {
            store.Upsert(new ArtifactEntry(
                Name: $"sentinel-plan-{i}",
                Uri: $"stub://plan{i}",
                OriginalBytes: 10,
                ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: "planning note"),
                // Matches MemoryNaming.BuildScopedTopicScope("plan") — the scope form that
                // excludeTopics: "plan" actually excludes (see IMemoryStore.BuildExcludedScopeSet).
                scope: "local:plan");
        }

        // Non-excluded entries ("kb"): only a content-preview "contains" match (field score 5) —
        // weaker than the plan entries above, so under the bug they never survive the unfiltered
        // top-K cut. After the fix (exclusion applied before the cut), these are the only
        // candidates left and must fill the result set up to `limit`.
        for (int i = 0; i < 2; i++)
        {
            store.Upsert(new ArtifactEntry(
                Name: $"kb-{i}",
                Uri: $"stub://kb{i}",
                OriginalBytes: 10,
                ChunkCount: 1,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: "background text containing sentinel reference"),
                scope: "local:kb");
        }

        SearchContributorContext.Current = new FakeScoreContributor();
        try
        {
            string result = await new ScriniaMcpTools().MemorySearch(
                query: "sentinel", limit: 2, excludeTopics: "plan");

            var parsed = ResponseParser.Parse(result);
            string content = parsed.Content ?? "";

            var match = Regex.Match(content, @"results:\s*(\d+)");
            match.Success.Should().BeTrue("the search response must include a 'results: N' header");
            int resultCount = int.Parse(match.Groups[1].Value);

            resultCount.Should().Be(2,
                "2 non-excluded 'kb' entries exist and limit=2 — excluded 'plan'-topic hits " +
                "must never consume result slots, even though they rank higher before " +
                "exclusion is applied");
            content.Should().NotContain("sentinel-plan",
                "excluded-topic entries must never appear in results");
            content.Should().Contain("kb-0");
            content.Should().Contain("kb-1");
        }
        finally
        {
            SearchContributorContext.Current = null;
        }
    }
}
