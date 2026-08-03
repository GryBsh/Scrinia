using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Covers the legacy <c>"fields"</c>-key sidecar coalesce (Task 2 of the Fields→Attributes
/// rename): a sidecar written before the rename stores its dict under JSON key "fields"
/// instead of "attributes". These tests prove that (1) <see cref="FileMemoryStore"/> loads such
/// a sidecar (whether constructed directly or reached via <see cref="MemoryStoreContext.Current"/>)
/// with <see cref="ArtifactEntry.Attributes"/> populated, (2) <c>scri migrate</c> rewrites the
/// on-disk key from "fields" to "attributes" and is idempotent, and (3) its WriteSidecar
/// chokepoint never re-emits a "fields" key once an entry passes through it.
///
/// Uses the cold-cache contract: seed the sidecar directly on disk, then load/migrate through
/// a FRESH store instance so the read-time forward-fill hook actually fires (a warm in-process
/// cache would mask the behavior under test).
/// </summary>
public sealed class AttributesKeyMigrationTests
{
    // Mirrors AttributeMigrationTests' TempWorkspace (temp-dir + FileMemoryStore(root) construction/disposal).
    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _tempDir;
        private FileMemoryStore? _store;

        public TempWorkspace()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-attrkeymig-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
        }

        public string Root => _tempDir;

        public FileMemoryStore NewStore() => _store = new FileMemoryStore(_tempDir);

        public void Dispose()
        {
            _store?.Dispose();
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private static readonly JsonSerializerOptions SeedOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds the JSON text of a legacy sidecar: an entry serialized normally (dict under
    /// "attributes"), then string-replaced to the pre-rename key "fields". Produces valid,
    /// single-key JSON because null members (including the never-set LegacyFields carrier)
    /// are omitted via WhenWritingNull.
    /// </summary>
    private static string LegacyFieldsSidecarJson(string name, string key, string value)
    {
        var entry = new ArtifactEntry(
            Name: name, Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, ContentPreview: "preview",
            Attributes: new Dictionary<string, string[]> { [key] = [value] });
        string json = JsonSerializer.Serialize(entry, SeedOptions);
        json.Should().Contain("\"attributes\"", "the seed helper must serialize under the current key first");
        json.Should().NotContain("\"fields\"", "no legacy key should be present before the string-replace");
        return json.Replace("\"attributes\"", "\"fields\"");
    }

    private static ArtifactEntry MakeEntry(
        string name = "n", Dictionary<string, string[]>? attributes = null) =>
        new(Name: name, Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, ContentPreview: "preview",
            Attributes: attributes);

    [Fact]
    public void FileStore_LegacyFieldsSidecar_LoadsWithAttributesPopulated()
    {
        using var tmp = new TempWorkspace();
        string storeDir = Path.Combine(tmp.Root, ".scrinia", "store");
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "a.meta.json"), LegacyFieldsSidecarJson("a", "tag", "security"));

        var cold = new FileMemoryStore(tmp.Root);
        var entries = cold.LoadIndex("local");

        var entry = entries.Single(e => e.Name == "a");
        entry.Attributes.Should().NotBeNull("the legacy 'fields' value must be coalesced into Attributes on load");
        entry.Attributes!["tag"].Should().BeEquivalentTo("security");
        cold.Dispose();
    }

    [Fact]
    public void FileStore_Migrate_RewritesLegacyKeyToAttributes()
    {
        using var tmp = new TempWorkspace();
        string storeDir = Path.Combine(tmp.Root, ".scrinia", "store");
        Directory.CreateDirectory(storeDir);
        string metaPath = Path.Combine(storeDir, "a.meta.json");
        File.WriteAllText(metaPath, LegacyFieldsSidecarJson("a", "tag", "security"));

        var cold = new FileMemoryStore(tmp.Root);
        var summary = AttributeMigration.MigrateStore(cold);

        summary.Migrated.Should().BeGreaterThanOrEqualTo(1, "the legacy-key sidecar must be counted as migrated");
        string raw = File.ReadAllText(metaPath);
        raw.Should().Contain("\"attributes\"", "migrate must rewrite the sidecar under the current key");
        raw.Should().NotContain("\"fields\"", "migrate must not leave the legacy key on disk");
        cold.Dispose();
    }

    [Fact]
    public void MigratesTo_True_For_LegacyKeyEntry()
    {
        string json = LegacyFieldsSidecarJson("a", "tag", "security");
        var entry = JsonSerializer.Deserialize<ArtifactEntry>(json)!;
        entry.LegacyFields.Should().NotBeNull("deserializing a 'fields'-only sidecar must populate the legacy carrier");
        entry.Attributes.Should().BeNull();

        AttributeMigration.MigratesTo(entry, out _).Should().BeTrue(
            "an entry carrying only a legacy 'fields' value must be reported as needing migration");

        var fresh = MakeEntry("b", attributes: new Dictionary<string, string[]> { ["tag"] = ["security"] });
        AttributeMigration.MigratesTo(fresh, out _).Should().BeFalse(
            "an entry that already has Attributes set and no legacy value must not be reported as migratable");
    }

    [Fact]
    public void Migrate_Idempotent_After_KeyRewrite()
    {
        using var tmp = new TempWorkspace();
        string storeDir = Path.Combine(tmp.Root, ".scrinia", "store");
        Directory.CreateDirectory(storeDir);
        string metaPath = Path.Combine(storeDir, "a.meta.json");
        File.WriteAllText(metaPath, LegacyFieldsSidecarJson("a", "tag", "security"));

        var first = AttributeMigration.MigrateStore(new FileMemoryStore(tmp.Root));
        first.Migrated.Should().BeGreaterThanOrEqualTo(1);
        string afterFirst = File.ReadAllText(metaPath);

        var second = AttributeMigration.MigrateStore(new FileMemoryStore(tmp.Root));
        second.Migrated.Should().Be(0, "the sidecar is already rewritten under 'attributes' after run 1");
        string afterSecond = File.ReadAllText(metaPath);
        afterSecond.Should().Be(afterFirst, "the second migrate run must leave the sidecar byte-identical");
    }

    [Fact]
    public void StoreScope_LegacyFieldsSidecar_LoadsWithAttributesPopulated()
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(Path.Combine(scope.TempDir, "a.meta.json"), LegacyFieldsSidecarJson("a", "tag", "security"));

        var entries = MemoryStoreContext.Current!.LoadIndex();

        var entry = entries.Single(e => e.Name == "a");
        entry.Attributes.Should().NotBeNull(
            "the store's read path must coalesce a legacy 'fields' sidecar into Attributes at load time");
        entry.Attributes!["tag"].Should().BeEquivalentTo("security");
    }

    /// <summary>
    /// Write-first cache-rebuild contract: <see cref="FileMemoryStore.Upsert"/> rebuilds
    /// <c>_indexCache[scope]</c> from the raw sidecars on disk (to pick up the just-written
    /// entry plus any siblings). Before the fix, that rebuild called
    /// <c>LoadEntriesFromSidecars</c> raw — bypassing the cold-path forward-fill loop in
    /// <c>LoadIndexUnsafe</c> — so a pre-existing legacy "fields" sidecar landed in the warm
    /// cache with <c>Attributes == null</c> whenever an Upsert (not a cold LoadIndex) was the
    /// first touch of that scope in the process. Seeds entry A as a legacy sidecar, then upserts
    /// an unrelated entry B first so the cache is built via the write path, then asserts A's
    /// Attributes survived the rebuild.
    /// </summary>
    [Fact]
    public void WriteFirst_Upsert_DoesNotHideLegacyAttributes()
    {
        using var tmp = new TempWorkspace();
        string storeDir = Path.Combine(tmp.Root, ".scrinia", "store");
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "a.meta.json"), LegacyFieldsSidecarJson("a", "tag", "security"));

        var store = tmp.NewStore();

        // Write-first: an Upsert of a different entry must be the FIRST touch of this scope,
        // so the cache is rebuilt via Upsert's raw sidecar reload rather than a cold LoadIndex.
        store.Upsert(MakeEntry("b"), "local");

        var entries = store.LoadIndex("local");
        var entryA = entries.Single(e => e.Name == "a");
        entryA.Attributes.Should().NotBeNull(
            "Upsert's cache rebuild must coalesce legacy 'fields' entries too, not just the entry being written");
        entryA.Attributes!["tag"].Should().BeEquivalentTo("security");
    }

    /// <summary>Mirrors <see cref="WriteFirst_Upsert_DoesNotHideLegacyAttributes"/> for
    /// <see cref="FileMemoryStore.Remove"/>, which rebuilds the cache the same way.</summary>
    [Fact]
    public void WriteFirst_Remove_DoesNotHideLegacyAttributes()
    {
        using var tmp = new TempWorkspace();
        string storeDir = Path.Combine(tmp.Root, ".scrinia", "store");
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "a.meta.json"), LegacyFieldsSidecarJson("a", "tag", "security"));
        File.WriteAllText(Path.Combine(storeDir, "b.meta.json"), LegacyFieldsSidecarJson("b", "tag", "other"));

        var store = tmp.NewStore();

        // Write-first: removing "b" is the FIRST touch of this scope, so the cache is rebuilt
        // via Remove's raw sidecar reload rather than a cold LoadIndex.
        store.Remove("b", "local").Should().BeTrue();

        var entries = store.LoadIndex("local");
        var entryA = entries.Single(e => e.Name == "a");
        entryA.Attributes.Should().NotBeNull(
            "Remove's cache rebuild must coalesce legacy 'fields' entries too, not just the entry being removed");
        entryA.Attributes!["tag"].Should().BeEquivalentTo("security");
    }

    [Fact]
    public void WriteSidecar_NeverEmitsFieldsKey()
    {
        using var tmp = new TempWorkspace();
        string json = LegacyFieldsSidecarJson("a", "tag", "security");
        var legacyEntry = JsonSerializer.Deserialize<ArtifactEntry>(json)!;
        legacyEntry.LegacyFields.Should().NotBeNull();

        var store = tmp.NewStore();
        store.Upsert(legacyEntry, "local");

        string metaPath = Path.Combine(tmp.Root, ".scrinia", "store", "a.meta.json");
        string raw = File.ReadAllText(metaPath);
        raw.Should().Contain("\"attributes\"", "the write chokepoint must normalize before persisting");
        raw.Should().NotContain("\"fields\"", "the write chokepoint must never re-emit the legacy key");
    }
}
