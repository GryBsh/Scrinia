using Scrinia.Server.Auth;
using FluentAssertions;

namespace Scrinia.Server.Tests;

public sealed class RequestContextTests
{
    [Fact] public void WildcardStore_IsGlobalAdmin_AccessesAny() {
        var ctx = new RequestContext { Stores = ["*"] };
        ctx.IsGlobalAdmin.Should().BeTrue();
        ctx.CanAccessStore("anything").Should().BeTrue();
    }
    [Fact] public void NamedStores_MatchOnlyThemselves() {
        var ctx = new RequestContext { Stores = ["alpha"] };
        ctx.IsGlobalAdmin.Should().BeFalse();
        ctx.CanAccessStore("alpha").Should().BeTrue();
        ctx.CanAccessStore("beta").Should().BeFalse();
    }
}
