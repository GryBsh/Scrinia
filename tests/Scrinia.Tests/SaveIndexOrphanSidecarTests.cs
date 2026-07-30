using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class SaveIndexOrphanSidecarTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public SaveIndexOrphanSidecarTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "orphan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SaveIndex_Shrink_DeletesOrphanSidecar_NoResurrection()
    {
        var entryA = new ArtifactEntry("a", "", 10, 1, DateTimeOffset.UtcNow);
        var entryB = new ArtifactEntry("b", "", 10, 1, DateTimeOffset.UtcNow);

        // Save both entries via the SaveIndex entry point.
        _store.SaveIndex([entryA, entryB]);

        string storeDir = _store.GetStoreDirForScope("local");
        string bMetaPath = Path.Combine(storeDir, _store.SanitizeName("b") + ".meta.json");
        File.Exists(bMetaPath).Should().BeTrue("sidecar for 'b' should exist after the initial save");

        // Shrink: save only [a] via the same entry point.
        _store.SaveIndex([entryA]);

        // The orphaned sidecar for 'b' must be deleted from disk.
        File.Exists(bMetaPath).Should().BeFalse("orphaned sidecar for 'b' must be deleted when the index shrinks");

        // A cold reload (new store instance, no cache) must not resurrect 'b'.
        using var coldStore = new FileMemoryStore(_tempDir);
        var loaded = coldStore.LoadIndex();
        loaded.Select(e => e.Name).Should().NotContain("b");
        loaded.Select(e => e.Name).Should().Contain("a");
    }
}
