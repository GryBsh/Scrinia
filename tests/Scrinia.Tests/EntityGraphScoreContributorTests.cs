using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="EntityGraphScoreContributor"/> — builds the entity graph from the
/// candidates it's handed and returns PPR scores keyed "{scope}|{name}", graceful (null) when
/// inert. The fusion wiring is covered separately via FileMemoryStore in
/// <see cref="EntityGraphSourceSearchTests"/>.
/// </summary>
public sealed class EntityGraphScoreContributorTests
{
    private static ScopedArtifact Doc(string name, params string[] entities) =>
        new("local", new ArtifactEntry(name, $"test://{name}", 0, 0, DateTimeOffset.UtcNow, Keywords: entities));

    [Fact]
    public async Task ComputeScores_LiftsAssociatedMemory_KeyedByScopeAndName()
    {
        var candidates = new List<ScopedArtifact>
        {
            Doc("a", "person:alice", "org:acme"),
            Doc("b", "org:acme", "person:bob"),   // associated via acme; no "alice" overlap
            Doc("c", "person:carol"),             // island
        };

        var scores = await new EntityGraphScoreContributor()
            .ComputeScoresAsync("what did alice do", candidates, null!, default);

        scores.Should().NotBeNull();
        var s = scores!;
        s.Should().ContainKey("local|a");
        s.Should().ContainKey("local|b");
        s.Should().NotContainKey("local|c");
        s["local|a"].Should().BeGreaterThan(s["local|b"]);
    }

    [Fact]
    public async Task ComputeScores_TopScore_EqualsWeight_FromMaxNormalization()
    {
        var candidates = new List<ScopedArtifact> { Doc("a", "person:alice"), Doc("b", "person:bob") };

        var scores = await new EntityGraphScoreContributor(weight: 42.0)
            .ComputeScoresAsync("alice", candidates, null!, default);

        scores!.Values.Max().Should().BeApproximately(42.0, 1e-9);
    }

    [Fact]
    public async Task ComputeScores_ConflictingTypes_DoNotBridge()
    {
        // "paris" claimed as both person and place → split; a query about the person must not
        // lift the place memory through the shared name.
        var candidates = new List<ScopedArtifact>
        {
            Doc("person", "person:alice", "person:paris"),
            Doc("place", "place:louvre", "place:paris"),
        };

        var scores = await new EntityGraphScoreContributor()
            .ComputeScoresAsync("what did alice say", candidates, null!, default);

        scores!.Should().ContainKey("local|person");
        scores.Should().NotContainKey("local|place");
    }

    [Fact]
    public async Task ComputeScores_NoTypedKeywords_ReturnsNull()
    {
        var candidates = new List<ScopedArtifact> { Doc("a", "plainkw", "anotherbareterm") };

        var scores = await new EntityGraphScoreContributor()
            .ComputeScoresAsync("alice", candidates, null!, default);

        scores.Should().BeNull();
    }

    [Fact]
    public async Task ComputeScores_NoSeedMatch_ReturnsNull()
    {
        var candidates = new List<ScopedArtifact> { Doc("a", "person:alice") };

        var scores = await new EntityGraphScoreContributor()
            .ComputeScoresAsync("nothing in the query matches", candidates, null!, default);

        scores.Should().BeNull();
    }
}
