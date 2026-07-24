using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class EntityFactSidecarTests
{
    private static readonly DateTimeOffset D1 = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

    [Fact]
    public void SerializeThenTryLoad_RoundTrips()
    {
        var mems = new[]
        {
            new MemoryEntities("m1", D1, [EntityRef.Of("person", "jolene"), EntityRef.Of("place", "france")]),
        };
        string token = EntityFactSidecar.ComputeToken([("m1", D1)]);

        string json = EntityFactSidecar.Serialize(token, mems);
        var loaded = EntityFactSidecar.TryLoad(json);

        loaded.Should().NotBeNull();
        loaded!.Value.Token.Should().Be(token);
        loaded.Value.Memories.Should().BeEquivalentTo(mems);
    }

    [Fact]
    public void ComputeToken_IsOrderIndependent_AndChangesWithContent()
    {
        var a = EntityFactSidecar.ComputeToken([("m1", D1), ("m2", D1)]);
        var b = EntityFactSidecar.ComputeToken([("m2", D1), ("m1", D1)]);
        a.Should().Be(b);                                     // order-independent
        EntityFactSidecar.ComputeToken([("m1", D1)]).Should().NotBe(a); // content-sensitive
    }

    [Fact]
    public void TryLoad_RejectsWrongVersion()
    {
        string bad = """{"version":999,"token":"x","memories":[]}""";
        EntityFactSidecar.TryLoad(bad).Should().BeNull();
    }
}
