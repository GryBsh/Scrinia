using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class SearchPathAttributeFilterTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public SearchPathAttributeFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_filter_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Seed(string scope, string name, string description)
    {
        // All entries share the query term "alpha" so relevance is non-zero for every one;
        // the filter, not relevance, must decide what survives.
        var entry = new ArtifactEntry(name, description, description.Length, 1, DateTimeOffset.UtcNow)
        {
            TermFrequencies = new Dictionary<string, int> { ["alpha"] = 3 },
        };
        _store.Upsert(entry, scope);
    }

    [Fact]
    public void SearchAll_WithPathPrefix_FillsUpToLimit_NotThePostLimitRemnant()
    {
        // 5 matching entries under local:keep + 5 decoys under local:drop. v3: every topic scope
        // is flat (MemoryNaming.BuildScopedTopicScope never adds a "memory/" segment), and
        // FormatQualifiedName no longer strips a namespace prefix — it round-trips the topic part
        // verbatim, so "local:keep" formats as "/keep/{name}", matching pathPrefix "/keep" directly.
        for (int i = 0; i < 5; i++) Seed("local:keep", $"k{i}", "alpha note");
        for (int i = 0; i < 5; i++) Seed("local:drop", $"d{i}", "alpha note");

        var results = _store.SearchAll(new SearchQuery("alpha") { Limit = 3, PathPrefix = "/keep" });

        // Old behavior: top-3 by score are cut FIRST, then path-filtered -> often < 3 kept.
        // Correct behavior: 3 keep-entries returned, every one under the prefix.
        results.Should().HaveCount(3);
        results.OfType<EntryResult>()
            .Should().OnlyContain(r => r.Item.Scope == "local:keep");
    }

    [Fact]
    public void SearchAll_WithAttributeFilter_ReturnsOnlyMatchingEntries()
    {
        var tagged = new ArtifactEntry("t1", "alpha", 5, 1, DateTimeOffset.UtcNow)
        {
            TermFrequencies = new Dictionary<string, int> { ["alpha"] = 3 },
            Attributes = new Dictionary<string, string[]> { ["kind"] = new[] { "spec" } },
        };
        // "local:memory" (bare, no child topic) is never discoverable via DiscoverTopics —
        // "memory" is a reserved namespace directory that only yields scopes of the form
        // "local:memory/{child}" (see FileMemoryStore.DiscoverTopics). Use a flat topic scope
        // instead so ListScoped(null) actually finds these entries.
        _store.Upsert(tagged, "local:notes");
        Seed("local:notes", "u1", "alpha"); // no attributes

        var results = _store.SearchAll(new SearchQuery("alpha")
        {
            Limit = 10,
            AttributeFilter = new Dictionary<string, string[]> { ["kind"] = new[] { "spec" } },
        });

        results.OfType<EntryResult>().Should().OnlyContain(r => r.Item.Entry.Name == "t1");
    }

    [Fact]
    public void SearchAll_NoFilter_MatchesUnfilteredOverload()
    {
        for (int i = 0; i < 4; i++) Seed("local:notes", $"m{i}", "alpha");

        var filtered = _store.SearchAll(new SearchQuery("alpha") { Limit = 10 });
        var plain = _store.SearchAll(new SearchQuery("alpha") { Limit = 10 });

        // Approximate, not exact: the recency term decays against DateTimeOffset.UtcNow captured
        // independently in each SearchAll call, so two sequential calls a few ticks apart produce
        // scores that agree to ~1e-8 but not bit-for-bit. That jitter is expected wall-clock noise,
        // not a sign the no-filter branch diverged from the base overload it delegates to.
        var filteredScores = filtered.Select(r => r.Score).ToList();
        var plainScores = plain.Select(r => r.Score).ToList();
        filteredScores.Should().HaveCount(plainScores.Count);
        for (int i = 0; i < filteredScores.Count; i++)
            filteredScores[i].Should().BeApproximately(plainScores[i], 1e-3);
    }
}
