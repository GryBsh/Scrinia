using System.IO.Compression;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// SaveIndexUnsafe is the only index-mutation path that does not invalidate the cached
/// EntityFactStore, and bundle import reaches it. A store whose cache was warmed before an
/// import would serve facts that omit every imported memory for the rest of the process.
/// </summary>
public sealed class EntityFactCacheInvalidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceRoot;
    private readonly string _targetRoot;
    private readonly FileMemoryStore _source;
    private readonly FileMemoryStore _target;

    public EntityFactCacheInvalidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-factcache-" + Guid.NewGuid().ToString("N")[..8]);
        _sourceRoot = Path.Combine(_tempDir, "source");
        _targetRoot = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_targetRoot);
        _source = new FileMemoryStore(_sourceRoot);
        _target = new FileMemoryStore(_targetRoot);
    }

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ImportingABundle_InvalidatesTheCachedFactStore()
    {
        const string topicScope = "local:api";

        await _source.WriteArtifactAsync("auth-flow", topicScope, "auth flow notes");
        _source.Upsert(new ArtifactEntry(
            Name: "auth-flow",
            Uri: "",
            OriginalBytes: 15,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: "auth flow notes",
            Attributes: new Dictionary<string, string[]> { ["topic"] = ["authentication"] }),
            topicScope);

        string bundlePath = Path.Combine(_tempDir, "api.scrinia-bundle");
        using (var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            BundleFormatService.ExportTopicsToZip(zip, _source, ["api"]);

        // Warm the target's cache BEFORE the import — this is what makes the staleness visible.
        _target.GetEntityFactStore().Entities("topic").Should().BeEmpty();

        using (var readStream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read))
        using (var zipRead = new ZipArchive(readStream, ZipArchiveMode.Read))
            BundleFormatService.ImportTopicsFromZip(zipRead, _target, filterTopics: null, overwrite: true);

        _target.GetEntityFactStore().Entities("topic")
            .Should().Contain(EntityRef.Of("topic", "authentication"),
                because: "an imported memory's attributes must reach the fact store");
    }
}
