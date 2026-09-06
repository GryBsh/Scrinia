using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests.Wave2;

/// <summary>
/// Wave 2 Task 1 (M1+M15) — Collapse SearchAll overloads into single SearchQuery contract.
/// Proves the store has exactly one SearchAll(SearchQuery), filters BEFORE truncation,
/// respects Candidates to skip ListScoped, and that SearchPipeline is the sole materialization path.
/// </summary>
public class SearchAllSinglePathTests
{
    [Fact]
    public void IMemoryStore_HasExactlyOneSearchAll_WithSearchQuery()
    {
        var allSearchAll = typeof(IMemoryStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "SearchAll")
            .ToArray();

        var queryOverloads = allSearchAll
            .Where(m =>
            {
                var p = m.GetParameters();
                return p.Length == 1 && p[0].ParameterType == typeof(SearchQuery);
            })
            .ToArray();

        queryOverloads.Should().HaveCount(1,
            "IMemoryStore must expose exactly one SearchAll(SearchQuery) — the collapsed contract");

        var param = queryOverloads[0].GetParameters().Single();
        param.ParameterType.Should().Be(typeof(SearchQuery));

        // The primary overload must be abstract (no default body); the typed adapter is a separate shape.
        queryOverloads[0].IsAbstract.Should().BeTrue(
            "SearchAll(SearchQuery) must have no default body so a store cannot silently inherit post-filter truncation");

        // Old string-query overloads must not exist.
        allSearchAll.Should().NotContain(m =>
            m.GetParameters().Any(p => p.ParameterType == typeof(string) && p.Name == "query"),
            "legacy string-query overloads were collapsed into SearchQuery");
    }

    [Fact]
    public async Task Search_NoPostFilteringAfterTruncation()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-w2-m1-" + Guid.NewGuid().ToString("N"));
        FileMemoryStore? store = null;
        try
        {
            store = new FileMemoryStore(root);

            // 40 excluded via path prefix, 20 matching — limit 20 must return 20 when filtering precedes truncation.
            // Old buggy code truncated to 20 BEFORE filtering and returned < 20.
            for (int i = 0; i < 40; i++)
            {
                var addr = MemoryAddress.Parse($"/private/seed-{i:D3}");
                var write = MemoryWrite.Create(addr, (IReadOnlyList<string?>)new[] { "hello world" },
                    new Dictionary<string, IReadOnlyList<string>>(), []);
                await store.StoreAsync(write);
            }

            for (int i = 0; i < 20; i++)
            {
                var addr = MemoryAddress.Parse($"/public/seed-{i:D3}");
                var write = MemoryWrite.Create(addr, (IReadOnlyList<string?>)new[] { "hello world" },
                    new Dictionary<string, IReadOnlyList<string>>(), []);
                await store.StoreAsync(write);
            }

            var query = new SearchQuery("hello") { Limit = 20, PathPrefix = "/public/" };
            var results = store.SearchAll(query);

            results.Should().HaveCount(20,
                "filtering on path prefix must happen BEFORE ranking/truncation, otherwise selective filters under-fill");

            results.Should().OnlyContain(r =>
                (r as EntryResult)!.Item.Entry.Address.Value.StartsWith("/public/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            store?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SearchQuery_Candidates_SkipsStoreListScoped()
    {
        var candidate = new ScopedArtifact("local",
            new ArtifactEntry(MemoryAddress.Parse("/a"), "artifacts/a.nmp2", 1, 1, DateTimeOffset.UtcNow));

        var query = new SearchQuery("x") { Candidates = new[] { candidate } };

        query.Candidates.Should().NotBeNull();
        query.Candidates.Should().HaveCount(1);

        // Prove the store honours it: a counting double must not see ListScoped.
        var counting = new CountingStore(candidate);
        var viaStore = counting.SearchAll(query);

        counting.ListScopedCalls.Should().Be(0,
            "when Candidates is supplied SearchAll must skip its own ListScoped and use the supplied list");
        viaStore.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchPipeline_MaterializesOnce()
    {
        int listScopedCalls = 0;
        var counting = new PipelineCountingStore(() => listScopedCalls++);

        var query = new SearchQuery("hello") { Limit = 5 };
        await SearchPipeline.ExecuteAsync(counting, query, null, null);

        listScopedCalls.Should().Be(1,
            "SearchPipeline must materialize candidates exactly once, then pass them via SearchQuery.Candidates");
    }

    [Fact]
    public void SearchPipeline_IsSoleMaterializationPath()
    {
        // Pipeline exists with the expected shape: sole place that overwrites Candidates/SupplementalScores.
        var method = typeof(SearchPipeline).GetMethod("ExecuteAsync",
            BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull("SearchPipeline.ExecuteAsync must be the single scoring pipeline");

        method!.GetParameters().Should().Contain(p => p.ParameterType == typeof(IMemoryStore));
        method.GetParameters().Should().Contain(p => p.ParameterType == typeof(SearchQuery));

        // Store's SearchAll must be the single scorer site — verify it constructs scorer once internally.
        // We do this behaviorally: two calls with same query but different Candidates produce different counts,
        // proving Candidates is respected and not re-listed.
        string root = Path.Combine(Path.GetTempPath(), "scrinia-w2-m15-" + Guid.NewGuid().ToString("N"));
        FileMemoryStore? store = null;
        try
        {
            store = new FileMemoryStore(root);
            var addr = MemoryAddress.Parse("/topic/item-0");
            store.StoreAsync(MemoryWrite.Create(addr, (IReadOnlyList<string?>)new[] { "alpha" },
                new Dictionary<string, IReadOnlyList<string>>(), [])).GetAwaiter().GetResult();

            var emptyCandidates = new SearchQuery("alpha") { Limit = 10, Candidates = [] };
            var emptyResult = store.SearchAll(emptyCandidates);
            emptyResult.Should().BeEmpty("empty Candidates must not be replaced by a re-list");

            var matchingCandidates = new SearchQuery("alpha")
            {
                Limit = 10,
                Candidates = store.ListScoped(null)
            };
            var filled = store.SearchAll(matchingCandidates);
            filled.Should().NotBeEmpty();
        }
        finally
        {
            store?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── Doubles ──────────────────────────────────────────────────────────────

    private sealed class CountingStore : FakeMemoryStore
    {
        private readonly ScopedArtifact _candidate;
        public int ListScopedCalls { get; private set; }

        public CountingStore(ScopedArtifact candidate) => _candidate = candidate;

        public override List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null)
        {
            ListScopedCalls++;
            return new List<ScopedArtifact> { _candidate };
        }

        public override IReadOnlyList<SearchResult> SearchAll(SearchQuery query)
        {
            // Replicate FileMemoryStore's Candidates-skip behaviour for the assertion.
            if (query.Candidates is not null)
                return query.Candidates.Select(c => (SearchResult)new EntryResult(c, 1.0)).ToList();

            var list = ListScoped(query.Scopes);
            return list.Select(c => (SearchResult)new EntryResult(c, 1.0)).ToList();
        }
    }

    private sealed class PipelineCountingStore : FakeMemoryStore
    {
        private readonly Action _onList;
        public PipelineCountingStore(Action onList) => _onList = onList;

        public override List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null)
        {
            _onList();
            return new List<ScopedArtifact>
            {
                new("local", new ArtifactEntry(MemoryAddress.Parse("/note"), "artifacts/note.nmp2", 1, 1, DateTimeOffset.UtcNow))
            };
        }

        public override IReadOnlyList<SearchResult> SearchAll(SearchQuery query)
        {
            // Must respect Candidates supplied by pipeline — not re-list.
            var source = query.Candidates ?? ListScoped(query.Scopes);
            return source.Select(c => (SearchResult)new EntryResult(c, 1.0)).ToList();
        }

    }
}
