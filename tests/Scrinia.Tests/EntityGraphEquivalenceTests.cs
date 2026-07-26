using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityGraphEquivalenceTests
{
    private static ScopedArtifact Doc(string scope, string name, string[] keywords) =>
        new(scope, new ArtifactEntry(
            Name: name, Uri: $"file://{name}", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: default, Keywords: keywords, UpdatedAt: null, Attributes: null));

    [Theory]
    [InlineData("what did alice do")]
    [InlineData("bob paris")]
    [InlineData("nothing matches here")]
    public void StorePath_EqualsGraphPath_WithinTolerance(string query)
    {
        var docs = new[]
        {
            Doc("local", "d1", ["person:alice"]),
            Doc("local", "d2", ["person:alice", "person:bob"]),
            Doc("local", "d3", ["person:bob", "place:paris"]),
            Doc("local", "d4", ["place:paris"]),
        };

        // Old production path: build the graph from the candidates, score it.
        var graphDocs = docs.Select(d => ($"{d.Scope}|{d.Entry.Name}",
            (IReadOnlyList<string>?)d.Entry.Keywords,
            (IReadOnlyDictionary<string, string[]>?)null));
        var graphScores = EntityGraph.Build(graphDocs).Score(query, 0.15, 20);

        // New path: PPR over the store built from the same candidates.
        var storeScores = new EntityGraphScoreContributor()
            .Score(EntityFactDerivation.BuildStore(docs), query);

        // graphScores is the raw PPR mass; the contributor's Score is max-normalized × weight.
        // Compare the *ranking* and the normalized shape: both empty, or same keys with proportional values.
        if (graphScores.Count == 0) { (storeScores is null || storeScores.Count == 0).Should().BeTrue(); return; }

        storeScores.Should().NotBeNull();
        storeScores!.Keys.Should().BeEquivalentTo(graphScores.Keys);

        double gMax = graphScores.Values.Max();
        foreach (var (k, raw) in graphScores)
            storeScores[k].Should().BeApproximately(raw / gMax * EntityGraphScoreContributor.DefaultWeight, 1e-9);
    }
}
