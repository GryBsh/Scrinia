using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityQuerySeederTests
{
    private static readonly string[] Vocab =
        ["person:jolene", "place:france", "thing:new york", "topic:auth"];

    [Fact]
    public void SubstringOfSurface_Seeds()
    {
        EntityQuerySeeder.Seed("what do I know about jolene?", Vocab)
            .Should().Contain("person:jolene");
    }

    [Fact]
    public void AllSurfaceTokensPresent_OutOfOrder_Seeds()
    {
        // "new york" surface tokens both present though not contiguous.
        EntityQuerySeeder.Seed("york trip, new plans", Vocab)
            .Should().Contain("thing:new york");
    }

    [Fact]
    public void UnnamedEntity_NotSeeded()
    {
        EntityQuerySeeder.Seed("jolene in france", Vocab)
            .Should().NotContain("topic:auth");
    }

    [Fact]
    public void MultipleNamed_AllSeeded()
    {
        EntityQuerySeeder.Seed("jolene in france", Vocab)
            .Should().Contain("person:jolene").And.Contain("place:france");
    }

    [Fact]
    public void ColonlessKey_SeedsOnWholeString()
    {
        EntityQuerySeeder.Seed("about widgets", ["widgets"])
            .Should().Contain("widgets");
    }

    [Fact]
    public void EmptyQuery_SeedsNothing()
    {
        EntityQuerySeeder.Seed("   ", Vocab).Should().BeEmpty();
    }
}
