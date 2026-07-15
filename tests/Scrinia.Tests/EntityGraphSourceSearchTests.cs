using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Exercises the entity-graph score channel end-to-end through the public
/// <see cref="FileMemoryStore.SearchAll(string, string?, int, IReadOnlyDictionary{string, double}?, IReadOnlyDictionary{string, double}?)"/>
/// (WeightedFieldScorer is internal). Confirms the entity source is a distinct fusion source
/// under both Sum and RRF, and that an empty/null entity dict is inert (no-regression guard).
/// </summary>
public sealed class EntityGraphSourceSearchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public EntityGraphSourceSearchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-eg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private void SeedUnrelated()
    {
        _store.Upsert(new ArtifactEntry("lonely", "test://lonely", 0, 0, DateTimeOffset.UtcNow,
            ContentPreview: "completely unrelated content"));
        _store.Upsert(new ArtifactEntry("decoy", "test://decoy", 0, 0, DateTimeOffset.UtcNow,
            ContentPreview: "also unrelated content"));
    }

    [Fact]
    public void EntitySource_LiftsZeroTextMatchCandidate_SumFusion()
    {
        SeedUnrelated();
        var entity = new Dictionary<string, double> { ["local|lonely"] = 50.0 };

        // Query matches NO content — only the entity source can surface anything.
        var results = _store.SearchAll("zzqqxx", scopes: null, limit: 10,
            supplementalScores: null, entityGraphScores: entity);

        var names = results.Select(GetName).ToList();
        names.Should().Contain("lonely");
        names.Should().NotContain("decoy");
    }

    [Fact]
    public void EntitySource_LiftsZeroTextMatchCandidate_RrfFusion()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-eg-rrf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        // AlphaRecency=0 so the tiny RRF magnitudes aren't swamped by the recency bump.
        using var store = new FileMemoryStore(dir)
        {
            RankerOptions = RankerOptions.Default with { Fusion = FusionMode.Rrf, AlphaRecency = 0 },
        };
        try
        {
            store.Upsert(new ArtifactEntry("lonely", "test://lonely", 0, 0, DateTimeOffset.UtcNow,
                ContentPreview: "completely unrelated content"));
            store.Upsert(new ArtifactEntry("decoy", "test://decoy", 0, 0, DateTimeOffset.UtcNow,
                ContentPreview: "also unrelated content"));
            var entity = new Dictionary<string, double> { ["local|lonely"] = 50.0 };

            var results = store.SearchAll("zzqqxx", scopes: null, limit: 10,
                supplementalScores: null, entityGraphScores: entity);

            results.Select(GetName).Should().Contain("lonely");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void EmptyOrNullEntityDict_IsInert()
    {
        _store.Upsert(new ArtifactEntry("alpha", "test://alpha", 0, 0, DateTimeOffset.UtcNow,
            ContentPreview: "alpha content about widgets"));
        _store.Upsert(new ArtifactEntry("beta", "test://beta", 0, 0, DateTimeOffset.UtcNow,
            ContentPreview: "beta content about widgets"));

        var withNull = _store.SearchAll("widgets", null, 10, supplementalScores: null, entityGraphScores: null)
            .Select(GetName).ToList();
        var withEmpty = _store.SearchAll("widgets", null, 10, supplementalScores: null,
            entityGraphScores: new Dictionary<string, double>())
            .Select(GetName).ToList();

        withEmpty.Should().Equal(withNull);
        withNull.Should().Contain("alpha").And.Contain("beta");
    }

    private static string GetName(SearchResult r) => r switch
    {
        EntryResult er => er.Item.Entry.Name,
        ChunkEntryResult cr => cr.ParentItem.Entry.Name,
        _ => string.Empty,
    };
}
