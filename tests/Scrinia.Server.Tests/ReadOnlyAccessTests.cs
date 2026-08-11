using FluentAssertions;
using Scrinia.Server.Auth;
using Scrinia.Server.Endpoints;

namespace Scrinia.Server.Tests;

public sealed class ReadOnlyAccessTests
{
    [Fact]
    public void ReadOnlyStore_WriteGuard_Denies()
    {
        var ctx = new RequestContext
        {
            Stores = ["test-store"],
            Permissions = ["store"],
            StoreAccessLevels = ["test-store:read-only"],
        };
        MemoryEndpoints.IsWritable(ctx, "test-store").Should().BeFalse();
    }

    [Fact]
    public void ApiKeyDefault_IsWritable()
    {
        var ctx = new RequestContext { Stores = ["test-store"], Permissions = ["store"] };
        MemoryEndpoints.IsWritable(ctx, "test-store").Should().BeTrue(); // default read-write
    }
}
