using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>Process-boundary coverage for the local CLI and stdio lifecycle drain.</summary>
[Collection(BackgroundWorkCollection.Name)]
public sealed class CliLifecycleFactFlushTests
{
    [Fact]
    public async Task OneShotProcessDrain_FlushesDirtyFactSidecarBeforeExit()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("one-shot", "cli"), "local");
        _ = store.GetEntityFactStore();

        string factsPath = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        File.Exists(factsPath).Should().BeFalse();

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        File.Exists(factsPath).Should().BeTrue();
        store.HasDirtyFactCache.Should().BeFalse();
    }

    [Fact]
    public async Task TimedOutProcessDrain_PreservesQueuedFactWorkForRestart()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("restart", "timeout"), "local");
        string factsPath = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        Directory.CreateDirectory(factsPath);
        _ = store.GetEntityFactStore();

        (await BackgroundWork.DrainAsync(TimeSpan.FromMilliseconds(100))).Should().BeFalse();
        store.HasDirtyFactCache.Should().BeTrue();

        Directory.Delete(factsPath);
        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        File.Exists(factsPath).Should().BeTrue();
        store.HasDirtyFactCache.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessDrain_DoesNotFlushAStoreAfterItsLifecycleIsDisposed()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("closed", "disposed"), "local");
        _ = store.GetEntityFactStore();
        string factsPath = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");

        store.Dispose();
        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        File.Exists(factsPath).Should().BeFalse();
    }

    private static ArtifactEntry Entry(string name, string topic) =>
        new(Name: name, Uri: $"file://{name}", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Attributes: new Dictionary<string, string[]> { ["topic"] = [topic] });
}
