using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class HomeConfigSerializationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scrinia-home-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_RoundTripsTypedRegistry()
    {
        var config = new HomeConfig
        {
            SchemaVersion = 1,
            SharedBases = new SharedBasesMap
            {
                ["team"] = new SharedBaseEntry { Root = Path.GetFullPath("team-root") },
            },
        };

        var store = new HomeConfigStore();
        store.Save(_directory, config);
        HomeConfig loaded = store.Load(_directory);

        loaded.Should().BeEquivalentTo(config);
        File.ReadAllBytes(Path.Combine(_directory, "config.json"))
            .Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
