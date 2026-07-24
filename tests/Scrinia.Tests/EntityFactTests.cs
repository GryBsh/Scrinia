using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactTests
{
    [Fact]
    public void EntityRef_Of_LowercasesKeyAndValue()
    {
        var r = EntityRef.Of("Person", "Maria");
        r.Key.Should().Be("person");
        r.Value.Should().Be("maria");
    }

    [Fact]
    public void EntityRef_ValueEquality_IsOrdinalOnNormalizedFields()
    {
        EntityRef.Of("thing", "Seraphim").Should().Be(EntityRef.Of("THING", "seraphim"));
        EntityRef.Of("thing", "seraphim").Should().NotBe(EntityRef.Of("thing", "susie"));
    }
}
