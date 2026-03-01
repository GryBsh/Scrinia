using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Tests that FileMemoryStore.SearchAll correctly incorporates supplemental scores (from embeddings).
/// </summary>
public class HybridScorerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public HybridScorerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_hybrid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, ".scrinia", "store"));
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void AddEntry(string name, string description)
    {
        var entry = new ArtifactEntry(name, "", 100, 1, DateTimeOffset.UtcNow, description);
        _store.Upsert(entry, "local");
        // Write a dummy artifact file
        string artifactPath = _store.ArtifactPath(name, "local");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, Nmp2ChunkedEncoder.Encode("test content"));
    }

    [Fact]
    public void SearchAll_WithSupplementalScores_BoostsRanking()
    {
        AddEntry("cats", "A document about cats");
        AddEntry("semantically-similar", "unrelated description xyz");

        // Without supplemental: "cats" matches, "semantically-similar" doesn't
        var resultsWithout = _store.SearchAll("cats", "local", 10);
        resultsWithout.Should().HaveCountGreaterOrEqualTo(1);
        (resultsWithout[0] as EntryResult)!.Item.Entry.Name.Should().Be("cats");

        // With supplemental: boost "semantically-similar" to the top
        var supplemental = new Dictionary<string, double>
        {
            ["local|semantically-similar"] = 200.0,
        };

        var resultsWith = _store.SearchAll("cats", "local", 10, supplemental);
        resultsWith.Should().HaveCount(2);
        (resultsWith[0] as EntryResult)!.Item.Entry.Name.Should().Be("semantically-similar");
    }

    [Fact]
    public void SearchAll_WithNullSupplemental_BehavesLikeLegacy()
    {
        AddEntry("test-memory", "test description about testing");

        var legacy = _store.SearchAll("test", "local", 10);
        var withNull = _store.SearchAll("test", "local", 10, supplementalScores: null);

        legacy.Should().HaveCount(withNull.Count);
        legacy[0].Score.Should().Be(withNull[0].Score);
    }

    [Fact]
    public void SearchAll_SupplementalOnly_StillReturnsResults()
    {
        // Entry with no lexical match
        AddEntry("xyz-unrelated", "nothing matches here abcdef");

        // Pure supplemental score should still surface the result
        var supplemental = new Dictionary<string, double>
        {
            ["local|xyz-unrelated"] = 100.0,
        };

        var results = _store.SearchAll("completely-different-query", "local", 10, supplemental);
        results.Should().HaveCount(1);
        (results[0] as EntryResult)!.Item.Entry.Name.Should().Be("xyz-unrelated");
    }
}
