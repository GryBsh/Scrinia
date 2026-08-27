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
            new MemoryEntities("local", "m1", D1, [EntityRef.Of("person", "jolene"), EntityRef.Of("place", "france")]),
        };
        string token = EntityFactSidecar.ComputeToken([("local|m1", D1, "sig")]);

        string json = EntityFactSidecar.Serialize(token, mems);
        var loaded = EntityFactSidecar.TryLoad(json);

        loaded.Should().NotBeNull();
        loaded!.Value.Token.Should().Be(token);
        loaded.Value.Memories.Should().BeEquivalentTo(mems);       // Scope+Subject survive the round-trip
        loaded.Value.Memories[0].ScoringKey.Should().Be("local|m1");
    }

    [Fact]
    public void ComputeToken_IsOrderIndependent_AndChangesWithContent()
    {
        var a = EntityFactSidecar.ComputeToken([("m1", D1, "sig1"), ("m2", D1, "sig2")]);
        var b = EntityFactSidecar.ComputeToken([("m2", D1, "sig2"), ("m1", D1, "sig1")]);
        a.Should().Be(b);                                     // order-independent
        EntityFactSidecar.ComputeToken([("m1", D1, "sig1")]).Should().NotBe(a); // content-sensitive
    }

    [Fact]
    public void ComputeToken_ChangesWhenContentSignatureChanges_EvenWithIdenticalIdAndDate()
    {
        // The whole point of the content-signature component: an attribute-only mutation (no
        // id change, no timestamp change) must still move the token, so a stale on-disk
        // facts.json sidecar isn't blindly trusted after a content-only rewrite.
        var before = EntityFactSidecar.ComputeToken([("m1", D1, "mention=[seraphim]|kw=[]")]);
        var after = EntityFactSidecar.ComputeToken([("m1", D1, "thing=[seraphim]|kw=[]")]);

        before.Should().NotBe(after);
    }

    [Fact]
    public void ComputeToken_IsStable_WhenNothingChanges()
    {
        var a = EntityFactSidecar.ComputeToken([("m1", D1, "mention=[seraphim]|kw=[]")]);
        var b = EntityFactSidecar.ComputeToken([("m1", D1, "mention=[seraphim]|kw=[]")]);

        a.Should().Be(b);
    }

    [Fact]
    public void TryLoad_RejectsWrongVersion()
    {
        string bad = """{"version":999,"token":"x","memories":[]}""";
        EntityFactSidecar.TryLoad(bad).Should().BeNull();
    }

    [Fact]
    public void TryLoad_RejectsV1Sidecar()
    {
        // A v1 document (old "id"-keyed schema) must be rejected so it rebuilds as v2.
        string v1 = """{"version":1,"token":"x","memories":[{"id":"m1","date":"2023-01-01T00:00:00+00:00","entities":[]}]}""";
        EntityFactSidecar.TryLoad(v1).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"version\":3,\"token\":\"x\",\"memories\":null}")]
    [InlineData("{\"version\":3,\"token\":\"x\"}")]
    [InlineData("{\"version\":3,\"token\":\"x\",\"memories\":[null]}")]
    [InlineData("{\"version\":3,\"token\":\"x\",\"memories\":[{\"s\":\"local\",\"j\":\"m1\",\"date\":\"2023-01-01T00:00:00Z\"}]}")]
    [InlineData("{\"version\":3,\"token\":\"x\",\"memories\":[{\"s\":\"local\",\"j\":\"m1\",\"date\":\"2023-01-01T00:00:00Z\",\"entities\":null}]}")]
    [InlineData("{\"version\":3,\"token\":\"x\",\"memories\":[{\"s\":\"local\",\"j\":\"m1\",\"date\":\"2023-01-01T00:00:00Z\",\"entities\":[null]}]}")]
    [InlineData("{\"version\":3,\"token\":\"x\",\"memories\":[{\"s\":\"local\",\"j\":\"m1\",\"date\":\"2023-01-01T00:00:00Z\",\"entities\":[{}]}]}")]
    public void TryLoad_RejectsStructurallyInvalidJsonAsCacheMiss(string json)
    {
        EntityFactSidecar.TryLoad(json).Should().BeNull();
    }
}
