using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class SharedBasePrivacyTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "scrinia-privacy-tests", Guid.NewGuid().ToString("N"));
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "scrinia-privacy-workspace", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DisabledEntriesArePrivateUnlessExplicitlyListingAll()
    {
        string root = Path.Combine(_home, "private-repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var registry = new SharedBaseRegistry(_home, _workspace);

        registry.Add("private", root).Succeeded.Should().BeTrue();

        registry.Lookup("private").Should().BeNull();
        registry.List(includeDisabled: false).Should().BeEmpty();
        registry.List(includeDisabled: true).Should().ContainSingle(x => x.Name == "private" && x.Root == Path.GetFullPath(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }
}
