using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// P6.4 drift rename backward-compat: <c>ArtifactEntry.CodeRefs</c> was renamed to
/// <c>FileHashes</c>, but the on-disk JSON key stays <c>"codeRefs"</c> (<c>JsonPropertyName</c>)
/// so every sidecar written before this rename keeps loading its drift baseline with zero
/// migration. These tests prove: (1) a hand-written legacy sidecar using the old
/// <c>"codeRefs"</c> JSON key deserializes into <c>FileHashes</c>; (2) a fresh write still
/// serializes under the legacy <c>"codeRefs"</c> key and never leaks a new <c>"fileHashes"</c>
/// key to disk; (3) the read-time forward-fill <c>FileHashes.Keys → Fields["file"]</c> still
/// fires for legacy sidecars.
/// </summary>
public sealed class DriftRenameTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public DriftRenameTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-driftrename-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void LegacyCodeRefsSidecar_Deserializes_IntoFileHashes_AndForwardFillsFileField()
    {
        // A sidecar written BEFORE the rename used the "codeRefs" JSON key. It must still load
        // into FileHashes with no migration step — proving [JsonPropertyName("codeRefs")] kept
        // the disk key stable across the C# member rename.
        string storeDir = _store.GetStoreDirForScope("local");
        Directory.CreateDirectory(storeDir);
        const string legacyJson = """
            {
              "name": "legacy-drift-note",
              "uri": "file:///tmp/legacy-drift-note.nmp2",
              "originalBytes": 100,
              "chunkCount": 1,
              "createdAt": "2026-01-01T00:00:00+00:00",
              "description": "Legacy sidecar with codeRefs",
              "codeRefs": { "src/a.cs": "HASH_A", "src/b.cs": "HASH_B" }
            }
            """;
        File.WriteAllText(Path.Combine(storeDir, "legacy-drift-note.meta.json"), legacyJson);

        var entries = _store.LoadIndex("local");
        var entry = entries.Single(e => e.Name == "legacy-drift-note");

        entry.FileHashes.Should().NotBeNull("the legacy \"codeRefs\" JSON key must deserialize into FileHashes");
        entry.FileHashes!["src/a.cs"].Should().Be("HASH_A");
        entry.FileHashes!["src/b.cs"].Should().Be("HASH_B");

        // Read-time forward-fill still routes FileHashes.Keys -> Fields["file"] (paths only).
        entry.Fields.Should().NotBeNull();
        entry.Fields!["file"].Should().Contain(["src/a.cs", "src/b.cs"]);
    }

    [Fact]
    public void FreshWrite_SerializesFileHashes_UnderLegacyCodeRefsKey_AndRoundTrips()
    {
        var entry = new ArtifactEntry(
            Name: "fresh-drift-note",
            Uri: "file:///tmp/fresh-drift-note.nmp2",
            OriginalBytes: 10,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch,
            Description: "fresh",
            FileHashes: new() { ["src/a.cs"] = "HASH_A" });

        _store.Upsert(entry, "local");

        string sidecarPath = Path.Combine(_store.GetStoreDirForScope("local"), "fresh-drift-note.meta.json");
        string raw = File.ReadAllText(sidecarPath);
        raw.Should().Contain("\"codeRefs\"", "the disk key must stay stable for backward compat");
        raw.Should().NotContain("fileHashes", "no new JSON key should leak to disk");

        // Round-trip through a second store instance (independent of _store's in-memory cache)
        // proves both serialize and deserialize directions of the JsonPropertyName mapping.
        using var store2 = new FileMemoryStore(_tempDir);
        var reloaded = store2.LoadIndex("local").Single(e => e.Name == "fresh-drift-note");
        reloaded.FileHashes.Should().NotBeNull();
        reloaded.FileHashes!["src/a.cs"].Should().Be("HASH_A");
    }
}
