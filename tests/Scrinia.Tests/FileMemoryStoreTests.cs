using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class FileMemoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public FileMemoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-fms-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ParseQualifiedName_local_scope()
    {
        var (scope, subject) = _store.ParseQualifiedName("my-notes");
        scope.Should().Be("local");
        subject.Should().Be("my-notes");
    }

    [Fact]
    public void ParseQualifiedName_topic_scope()
    {
        var (scope, subject) = _store.ParseQualifiedName("api:auth-flow");
        scope.Should().Be("local-topic:memory/api");
        subject.Should().Be("auth-flow");
    }

    [Fact]
    public void FormatQualifiedName_roundtrips()
    {
        _store.FormatQualifiedName("local", "notes").Should().Be("notes");
        _store.FormatQualifiedName("local-topic:memory/api", "auth").Should().Be("/api/auth");
    }

    [Fact]
    public void Upsert_and_LoadIndex()
    {
        var entry = new ArtifactEntry("test", "file://test", 100, 1, DateTimeOffset.UtcNow, "desc");
        _store.Upsert(entry);

        var loaded = _store.LoadIndex();
        loaded.Should().HaveCount(1);
        loaded[0].Name.Should().Be("test");
    }

    [Fact]
    public void Upsert_updates_existing_entry()
    {
        var entry1 = new ArtifactEntry("test", "file://test", 100, 1, DateTimeOffset.UtcNow, "v1");
        _store.Upsert(entry1);

        var entry2 = entry1 with { Description = "v2" };
        _store.Upsert(entry2);

        var loaded = _store.LoadIndex();
        loaded.Should().HaveCount(1);
        loaded[0].Description.Should().Be("v2");
    }

    [Fact]
    public void Remove_deletes_entry()
    {
        _store.Upsert(new ArtifactEntry("del-me", "", 50, 1, DateTimeOffset.UtcNow, "temp"));
        _store.Remove("del-me").Should().BeTrue();
        _store.LoadIndex().Should().BeEmpty();
    }

    [Fact]
    public async Task WriteArtifact_and_ReadArtifact_roundtrip()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Hello, store!");
        await _store.WriteArtifactAsync("greet", "local", artifact);

        string read = await _store.ReadArtifactAsync("greet", "local");
        read.Should().Be(artifact);
    }

    [Fact]
    public async Task DeleteArtifact_removes_file()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Delete me");
        await _store.WriteArtifactAsync("del", "local", artifact);
        _store.DeleteArtifact("del", "local").Should().BeTrue();
        _store.DeleteArtifact("del", "local").Should().BeFalse(); // already gone
    }

    [Fact]
    public void Ephemeral_store_and_retrieve()
    {
        var entry = new EphemeralEntry("scratch", "NMP...", 10, 1, DateTimeOffset.UtcNow, "temp");
        _store.RememberEphemeral("scratch", entry);

        _store.GetEphemeral("scratch").Should().NotBeNull();
        _store.GetEphemeral("scratch")!.Name.Should().Be("scratch");
    }

    [Fact]
    public void Ephemeral_forget()
    {
        _store.RememberEphemeral("bye", new EphemeralEntry("bye", "", 0, 1, DateTimeOffset.UtcNow, ""));
        _store.ForgetEphemeral("bye").Should().BeTrue();
        _store.GetEphemeral("bye").Should().BeNull();
    }

    [Fact]
    public async Task ResolveArtifact_finds_persistent_memory()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Resolve me");
        await _store.WriteArtifactAsync("resolve-test", "local", artifact);
        _store.Upsert(new ArtifactEntry("resolve-test", "", 100, 1, DateTimeOffset.UtcNow, "test"));

        string resolved = await _store.ResolveArtifactAsync("resolve-test");
        resolved.Should().Be(artifact);
    }

    [Fact]
    public async Task ResolveArtifact_finds_ephemeral()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Ephemeral data");
        _store.RememberEphemeral("eph", new EphemeralEntry("eph", artifact, 100, 1, DateTimeOffset.UtcNow, ""));

        string resolved = await _store.ResolveArtifactAsync("~eph");
        resolved.Should().Be(artifact);
    }

    [Fact]
    public void ListScoped_includes_local_entries()
    {
        _store.Upsert(new ArtifactEntry("listed", "", 50, 1, DateTimeOffset.UtcNow, "test"));
        var list = _store.ListScoped();
        list.Should().Contain(s => s.Entry.Name == "listed");
    }

    [Fact]
    public async Task CopyMemory_persistent_to_persistent()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Copy content");
        await _store.WriteArtifactAsync("src", "local", artifact);
        _store.Upsert(new ArtifactEntry("src", "", 100, 1, DateTimeOffset.UtcNow, "original"));

        bool ok = _store.CopyMemory("src", "dst", false, out string msg);
        ok.Should().BeTrue();

        var dstEntries = _store.LoadIndex();
        dstEntries.Should().Contain(e => e.Name == "dst");
    }

    [Fact]
    public void DiscoverTopics_finds_topic_dirs()
    {
        string topicDir = Path.Combine(_tempDir, ".scrinia", "topics", "api");
        Directory.CreateDirectory(topicDir);

        var topics = _store.DiscoverTopics();
        topics.Should().Contain("local-topic:api");
    }

    [Fact]
    public void SearchAll_returns_results()
    {
        var entry = new ArtifactEntry("search-me", "", 100, 1, DateTimeOffset.UtcNow, "kubernetes deployment",
            Keywords: ["kubernetes", "deploy"]);
        _store.Upsert(entry);

        var results = _store.SearchAll("kubernetes");
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void ImportTopicEntries_creates_topic()
    {
        var entries = new List<ArtifactEntry>
        {
            new("imported", "", 50, 1, DateTimeOffset.UtcNow, "imported entry")
        };
        var contents = new Dictionary<string, string>
        {
            ["imported"] = Nmp2ChunkedEncoder.Encode("Imported content")
        };

        _store.ImportTopicEntries("local-topic:test", entries, contents, overwrite: false);

        var loaded = _store.LoadIndex("local-topic:test");
        loaded.Should().HaveCount(1);
        loaded[0].Name.Should().Be("imported");
    }

    [Fact]
    public void GenerateContentPreview_truncates()
    {
        string long_content = new string('x', 1000);
        string preview = _store.GenerateContentPreview(long_content);
        preview.Length.Should().BeLessOrEqualTo(500);
    }

    // ── PR 1: Index cache tests ─────────────────────────────────────────────

    [Fact]
    public void Upsert_CachePopulated_SubsequentLoadSkipsDisk()
    {
        var entry = new ArtifactEntry("cached", "file://cached", 100, 1, DateTimeOffset.UtcNow, "cached entry");
        _store.Upsert(entry);

        // Delete the index.json from disk — cache should still serve the data
        string indexPath = Path.Combine(_store.GetStoreDirForScope("local"), "index.json");
        File.Delete(indexPath);

        var loaded = _store.LoadIndex();
        loaded.Should().ContainSingle(e => e.Name == "cached");
    }

    [Fact]
    public void SaveIndex_InvalidatesAndUpdatesCache()
    {
        var entry1 = new ArtifactEntry("v1", "", 50, 1, DateTimeOffset.UtcNow, "first");
        _store.Upsert(entry1);

        // Overwrite index via SaveIndex
        var entry2 = new ArtifactEntry("v2", "", 75, 1, DateTimeOffset.UtcNow, "second");
        _store.SaveIndex([entry2]);

        var loaded = _store.LoadIndex();
        loaded.Should().ContainSingle(e => e.Name == "v2");
        loaded.Should().NotContain(e => e.Name == "v1");
    }

    [Fact]
    public async Task LoadIndex_ConcurrentReaders_DoNotBlock()
    {
        // Seed some data
        for (int i = 0; i < 10; i++)
            _store.Upsert(new ArtifactEntry($"entry-{i}", "", 10, 1, DateTimeOffset.UtcNow, $"desc {i}"));

        // 10 concurrent reads should all complete without blocking each other
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => _store.LoadIndex()))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
            result.Should().HaveCount(10);
    }

    [Fact]
    public void DiscoverTopics_CachedAfterFirstCall()
    {
        string topicDir = Path.Combine(_tempDir, ".scrinia", "topics", "cached-topic");
        Directory.CreateDirectory(topicDir);

        var first = _store.DiscoverTopics();
        first.Should().Contain("local-topic:cached-topic");

        // Delete the directory — cache should still serve the result
        Directory.Delete(topicDir, recursive: true);

        var second = _store.DiscoverTopics();
        second.Should().Contain("local-topic:cached-topic", "cached result should persist within TTL");
    }

    [Fact]
    public void Upsert_ExistingEntry_UsesNameDictionary()
    {
        // Insert 100 entries, then update the last one — should be O(1) via name dictionary
        for (int i = 0; i < 100; i++)
            _store.Upsert(new ArtifactEntry($"entry-{i}", "", 10, 1, DateTimeOffset.UtcNow, $"desc {i}"));

        var updated = new ArtifactEntry("entry-99", "", 10, 1, DateTimeOffset.UtcNow, "updated");
        _store.Upsert(updated);

        var loaded = _store.LoadIndex();
        loaded.Should().HaveCount(100);
        loaded.Should().ContainSingle(e => e.Name == "entry-99" && e.Description == "updated");
    }

    [Fact]
    public void Remove_UsesNameDictionary()
    {
        for (int i = 0; i < 50; i++)
            _store.Upsert(new ArtifactEntry($"rm-{i}", "", 10, 1, DateTimeOffset.UtcNow, $"desc {i}"));

        _store.Remove("rm-25").Should().BeTrue();

        var loaded = _store.LoadIndex();
        loaded.Should().HaveCount(49);
        loaded.Should().NotContain(e => e.Name == "rm-25");
    }

    // ── PR 3: Artifact LRU cache tests ──────────────────────────────────────

    [Fact]
    public async Task ReadArtifact_CacheHit_OnRepeatRead()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Cache test content");
        await _store.WriteArtifactAsync("cache-hit", "local", artifact);

        // First read — cache miss, reads from disk
        string read1 = await _store.ReadArtifactAsync("cache-hit", "local");
        read1.Should().Be(artifact);

        // Second read — should be cache hit (same content)
        string read2 = await _store.ReadArtifactAsync("cache-hit", "local");
        read2.Should().Be(artifact);
    }

    [Fact]
    public async Task WriteArtifact_InvalidatesCache()
    {
        string v1 = Nmp2ChunkedEncoder.Encode("Version 1");
        await _store.WriteArtifactAsync("invalidate", "local", v1);

        // Read to populate cache
        string read1 = await _store.ReadArtifactAsync("invalidate", "local");
        read1.Should().Be(v1);

        // Overwrite
        string v2 = Nmp2ChunkedEncoder.Encode("Version 2");
        await _store.WriteArtifactAsync("invalidate", "local", v2);

        // Should read v2, not cached v1
        string read2 = await _store.ReadArtifactAsync("invalidate", "local");
        read2.Should().Be(v2);
    }

    [Fact]
    public async Task DeleteArtifact_InvalidatesCache()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Delete cache test");
        await _store.WriteArtifactAsync("del-cache", "local", artifact);

        // Populate cache
        await _store.ReadArtifactAsync("del-cache", "local");

        // Delete
        _store.DeleteArtifact("del-cache", "local").Should().BeTrue();

        // Read should now throw
        var act = () => _store.ReadArtifactAsync("del-cache", "local");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ── Namespace separation ─────────────────────────────────────────────────

    [Fact]
    public void ParseQualifiedName_EntityTopic_RoutesToEntityNamespace()
    {
        var (scope, subject) = _store.ParseQualifiedName("task:my-task");
        scope.Should().Be("local-topic:entity/task");
        subject.Should().Be("my-task");
    }

    [Fact]
    public void ParseQualifiedName_AgentTopic_RoutesToAgentScope()
    {
        var (scope, subject) = _store.ParseQualifiedName("agent:profile");
        scope.Should().Be("local-topic:agent");
        subject.Should().Be("profile");
    }

    [Fact]
    public void ParseQualifiedName_UserTopic_RoutesToMemoryNamespace()
    {
        var (scope, subject) = _store.ParseQualifiedName("dotnet:di-patterns");
        scope.Should().Be("local-topic:memory/dotnet");
        subject.Should().Be("di-patterns");
    }

    [Fact]
    public void FormatQualifiedName_EntityNamespaced_StripsPrefix()
    {
        _store.FormatQualifiedName("local-topic:entity/task", "my-task").Should().Be("/task/my-task");
    }

    [Fact]
    public void FormatQualifiedName_MemoryNamespaced_StripsPrefix()
    {
        _store.FormatQualifiedName("local-topic:memory/api", "auth").Should().Be("/api/auth");
    }

    [Fact]
    public void FormatQualifiedName_AgentScope_StripsPrefix()
    {
        _store.FormatQualifiedName("local-topic:agent", "profile").Should().Be("/agent/profile");
    }

    [Fact]
    public void DiscoverTopics_NamespacedDirs_FoundWithNamespace()
    {
        // Create namespaced directories: entity/task, memory/api
        string entityTaskDir = Path.Combine(_tempDir, ".scrinia", "topics", "entity", "task");
        string memoryApiDir = Path.Combine(_tempDir, ".scrinia", "topics", "memory", "api");
        Directory.CreateDirectory(entityTaskDir);
        Directory.CreateDirectory(memoryApiDir);

        var topics = _store.DiscoverTopics();
        topics.Should().Contain("local-topic:entity/task");
        topics.Should().Contain("local-topic:memory/api");
    }

    [Fact]
    public void DiscoverTopics_AgentDir_ReturnsAgentScope()
    {
        string agentDir = Path.Combine(_tempDir, ".scrinia", "topics", "agent");
        Directory.CreateDirectory(agentDir);

        var topics = _store.DiscoverTopics();
        topics.Should().Contain("local-topic:agent",
            because: "agent is a single scope without child directories");
    }

    [Fact]
    public void DiscoverTopics_LegacyFlatDir_NotShadowed_StillDiscovered()
    {
        // A legacy flat dir that has no namespaced counterpart should still appear
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "custom-notes");
        Directory.CreateDirectory(legacyDir);

        var topics = _store.DiscoverTopics();
        topics.Should().Contain("local-topic:custom-notes",
            because: "legacy flat dirs not shadowed by namespace entries should still be discovered");
    }

    [Fact]
    public void DiscoverTopics_LegacyFlatDir_ShadowedByNamespaced_Excluded()
    {
        // Create both a namespaced and a legacy flat dir for "api"
        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "memory", "api");
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "api");
        Directory.CreateDirectory(namespacedDir);
        Directory.CreateDirectory(legacyDir);

        var topics = _store.DiscoverTopics();
        topics.Should().Contain("local-topic:memory/api",
            because: "namespaced entry should be discovered");
        topics.Should().NotContain("local-topic:api",
            because: "legacy flat dir shadowed by namespaced entry should be excluded");
    }

    // ── Legacy fallback: reads from legacy flat paths ────────────────────────

    [Fact]
    public async Task ReadArtifact_FallsBackToLegacyPath()
    {
        // Write an artifact to the legacy flat path: topics/task/my-task.nmp2
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "task");
        Directory.CreateDirectory(legacyDir);
        string artifact = Nmp2ChunkedEncoder.Encode("Legacy task content");
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "my-task.nmp2"), artifact);

        // Read via the namespaced scope — should fall back to legacy path
        string read = await _store.ReadArtifactAsync("my-task", "local-topic:entity/task");
        read.Should().Be(artifact,
            because: "ReadArtifactAsync should fall back to legacy flat path when the namespaced path does not exist");
    }

    [Fact]
    public void LoadIndex_MergesEntriesFromLegacyFlatDir()
    {
        // Create a legacy flat dir with a sidecar meta file
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "task");
        Directory.CreateDirectory(legacyDir);

        // Write a sidecar meta.json in the legacy dir
        string metaJson = """
        {
          "name": "legacy-entry",
          "uri": "file:///tmp/legacy-entry.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "A legacy entry"
        }
        """;
        File.WriteAllText(Path.Combine(legacyDir, "legacy-entry.meta.json"), metaJson);

        // Also ensure the namespaced dir exists (but is empty)
        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "entity", "task");
        Directory.CreateDirectory(namespacedDir);

        var entries = _store.LoadIndex("local-topic:entity/task");
        entries.Should().Contain(e => e.Name == "legacy-entry",
            because: "entries from the legacy flat dir should be merged into the namespaced scope");
    }

    [Fact]
    public void LoadIndex_NamespacedEntries_TakePrecedenceOverLegacy()
    {
        // Create the legacy flat dir with a sidecar
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "task");
        Directory.CreateDirectory(legacyDir);
        string legacyMeta = """
        {
          "name": "shared-entry",
          "uri": "file:///tmp/shared-entry.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "Legacy version"
        }
        """;
        File.WriteAllText(Path.Combine(legacyDir, "shared-entry.meta.json"), legacyMeta);

        // Create the namespaced dir with the same entry name
        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "entity", "task");
        Directory.CreateDirectory(namespacedDir);
        string namespacedMeta = """
        {
          "name": "shared-entry",
          "uri": "file:///tmp/shared-entry.nmp2",
          "originalBytes": 200,
          "chunkCount": 2,
          "createdAt": "2026-02-01T00:00:00+00:00",
          "description": "Namespaced version"
        }
        """;
        File.WriteAllText(Path.Combine(namespacedDir, "shared-entry.meta.json"), namespacedMeta);

        var entries = _store.LoadIndex("local-topic:entity/task");
        entries.Should().ContainSingle(e => e.Name == "shared-entry",
            because: "same-name entries should not be duplicated");
        entries.First(e => e.Name == "shared-entry").Description.Should().Be("Namespaced version",
            because: "namespaced entries take precedence over legacy entries");
    }

    // ── Legacy entry preservation in index cache ────────────────────────────

    [Fact]
    public void Upsert_NamespacedScope_PreservesLegacyEntriesInCache()
    {
        // Create a legacy entry by writing a .meta.json directly to the flat path
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "concern");
        Directory.CreateDirectory(legacyDir);
        string legacyMeta = """
        {
          "name": "legacy-item",
          "uri": "file:///tmp/legacy-item.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "A legacy concern"
        }
        """;
        File.WriteAllText(Path.Combine(legacyDir, "legacy-item.meta.json"), legacyMeta);

        // Also create the namespaced directory so the scope resolves
        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "entity", "concern");
        Directory.CreateDirectory(namespacedDir);

        // Verify LoadIndex initially returns the legacy entry
        var before = _store.LoadIndex("local-topic:entity/concern");
        before.Should().Contain(e => e.Name == "legacy-item",
            because: "legacy entry should be discovered before any namespaced upsert");

        // Now upsert a NEW entry into the namespaced scope
        _store.Upsert(
            new ArtifactEntry("new-namespaced", "", 200, 1, DateTimeOffset.UtcNow, "New entry"),
            "local-topic:entity/concern");

        // Verify LoadIndex STILL returns the legacy entry alongside the new one
        var after = _store.LoadIndex("local-topic:entity/concern");
        after.Should().Contain(e => e.Name == "legacy-item",
            because: "upserting a new namespaced entry must not evict legacy entries from the cache");
        after.Should().Contain(e => e.Name == "new-namespaced",
            because: "the newly upserted entry must also be present");
    }

    [Fact]
    public void Remove_NamespacedScope_PreservesLegacyEntriesInCache()
    {
        // Create a legacy entry in flat path
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "concern");
        Directory.CreateDirectory(legacyDir);
        string legacyMeta = """
        {
          "name": "legacy-survivor",
          "uri": "file:///tmp/legacy-survivor.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "Legacy entry that must survive removal"
        }
        """;
        File.WriteAllText(Path.Combine(legacyDir, "legacy-survivor.meta.json"), legacyMeta);

        // Create a namespaced entry via Upsert
        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "entity", "concern");
        Directory.CreateDirectory(namespacedDir);
        _store.Upsert(
            new ArtifactEntry("removable-entry", "", 200, 1, DateTimeOffset.UtcNow, "Will be removed"),
            "local-topic:entity/concern");

        // Verify LoadIndex returns both
        var before = _store.LoadIndex("local-topic:entity/concern");
        before.Should().Contain(e => e.Name == "legacy-survivor");
        before.Should().Contain(e => e.Name == "removable-entry");

        // Remove the namespaced entry
        _store.Remove("removable-entry", "local-topic:entity/concern").Should().BeTrue();

        // Verify LoadIndex STILL returns the legacy entry
        var after = _store.LoadIndex("local-topic:entity/concern");
        after.Should().Contain(e => e.Name == "legacy-survivor",
            because: "removing a namespaced entry must not evict legacy entries from the cache");
        after.Should().NotContain(e => e.Name == "removable-entry",
            because: "the removed entry must be gone");
    }

    [Fact]
    public void LoadIndex_MergesLegacyAndNamespacedEntries_WithPrecedence()
    {
        // Create an entry in the legacy flat path
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "concern");
        Directory.CreateDirectory(legacyDir);
        string legacyOnlyMeta = """
        {
          "name": "legacy-only",
          "uri": "file:///tmp/legacy-only.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "Only in legacy"
        }
        """;
        File.WriteAllText(Path.Combine(legacyDir, "legacy-only.meta.json"), legacyOnlyMeta);

        // Also write a collision entry in legacy
        string legacyCollisionMeta = """
        {
          "name": "collision",
          "uri": "file:///tmp/collision.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "Legacy version of collision"
        }
        """;
        File.WriteAllText(Path.Combine(legacyDir, "collision.meta.json"), legacyCollisionMeta);

        // Create a different entry in the namespaced path
        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "entity", "concern");
        Directory.CreateDirectory(namespacedDir);
        string namespacedOnlyMeta = """
        {
          "name": "namespaced-only",
          "uri": "file:///tmp/namespaced-only.nmp2",
          "originalBytes": 200,
          "chunkCount": 1,
          "createdAt": "2026-02-01T00:00:00+00:00",
          "description": "Only in namespaced"
        }
        """;
        File.WriteAllText(Path.Combine(namespacedDir, "namespaced-only.meta.json"), namespacedOnlyMeta);

        // Write a collision entry in namespaced (same name as legacy)
        string namespacedCollisionMeta = """
        {
          "name": "collision",
          "uri": "file:///tmp/collision.nmp2",
          "originalBytes": 300,
          "chunkCount": 2,
          "createdAt": "2026-03-01T00:00:00+00:00",
          "description": "Namespaced version of collision"
        }
        """;
        File.WriteAllText(Path.Combine(namespacedDir, "collision.meta.json"), namespacedCollisionMeta);

        // Call LoadIndex — verify both unique entries returned
        var entries = _store.LoadIndex("local-topic:entity/concern");
        entries.Should().Contain(e => e.Name == "legacy-only",
            because: "legacy-only entries must be merged into the result");
        entries.Should().Contain(e => e.Name == "namespaced-only",
            because: "namespaced-only entries must be present");

        // Verify namespaced entry takes precedence when names collide
        entries.Should().ContainSingle(e => e.Name == "collision",
            because: "same-name entries should not be duplicated");
        entries.First(e => e.Name == "collision").Description.Should().Be("Namespaced version of collision",
            because: "namespaced entries take precedence over legacy entries");
    }
}
