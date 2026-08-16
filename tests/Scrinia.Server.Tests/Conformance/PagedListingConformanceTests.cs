using FluentAssertions;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// The test that would have caught C2: the same corpus, both store implementations, identical
/// listings — over a corpus large enough for the server's default page size to matter.
/// </summary>
[Collection(nameof(PagedListingConformanceCollection))]
public sealed class PagedListingConformanceTests(PagedListingConformanceFixture fx)
{
    [Fact]
    public void ListScoped_Unfiltered_BothStores_ReturnEveryEntry()
    {
        var local = fx.Local.ListScoped(null);
        var remote = fx.Remote.ListScoped(null);

        local.Should().HaveCount(PagedListingConformanceFixture.TotalEntries,
            because: "the fixture seeded exactly that many through the real REST write path");

        remote.Should().HaveCount(PagedListingConformanceFixture.TotalEntries,
            because: "a remote listing that stops at the server's 200-row default answers "
                   + "confidently and wrongly, with no error and no truncation marker");

        PagedListingConformanceFixture.QualifiedNames(fx.Remote, remote).Should()
            .BeEquivalentTo(PagedListingConformanceFixture.QualifiedNames(fx.Local, local),
                because: "the same corpus read through either store is the same set of memories");
    }
}
