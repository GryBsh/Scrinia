using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityFactDerivationTests
{
    private static ScopedArtifact Entry(
        string name, string scope, DateTimeOffset created, DateTimeOffset? updated = null,
        string[]? keywords = null, Dictionary<string, string[]>? attributes = null) =>
        new(scope, new ArtifactEntry(
            Name: name, Uri: $"file://{name}", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: created, Keywords: keywords, UpdatedAt: updated, Attributes: attributes));

    [Fact]
    public void Derive_UnionsKeywordAndAttributeEntities_DatesByUpdatedThenCreated()
    {
        var created = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        var updated = DateTimeOffset.Parse("2023-06-01T00:00:00Z");
        var item = Entry("m1", "local", created, updated,
            keywords: ["person:jolene", "plain-term"],
            attributes: new() { ["place"] = ["france"], ["thing"] = ["seraphim"] });

        var me = EntityFactDerivation.Derive(item);

        me.Should().NotBeNull();
        me!.Date.Should().Be(updated);
        me.MemoryId.Should().Be("m1");        // FormatRoundTrip("local", "m1")
        me.ScoringKey.Should().Be("local|m1");
        me.Entities.Should().BeEquivalentTo(new[]
        {
            EntityRef.Of("person", "jolene"),
            EntityRef.Of("place", "france"),
            EntityRef.Of("thing", "seraphim"),
        });
    }

    [Fact]
    public void Derive_NoEntities_ReturnsNull()
    {
        var item = Entry("m2", "local", DateTimeOffset.UtcNow, keywords: ["just", "plain", "words"]);
        EntityFactDerivation.Derive(item).Should().BeNull();
    }
}
