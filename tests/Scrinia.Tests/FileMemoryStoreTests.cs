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

    /// <summary>
    /// Touches <see cref="_store"/> once while nothing exists on disk yet, consuming the
    /// one-time v3 layout-migration gate (<c>FileMemoryStore.MemoriesRoot</c>) against an empty
    /// state — nothing to migrate, so it's a no-op. Call this BEFORE seeding legacy
    /// <c>.scrinia/topics/</c> fixtures directly on the filesystem in the tests below: the gate
    /// only ever runs once per store instance, so content that appears afterward is exactly the
    /// "straggler" case <c>FileMemoryStore.ResolveLegacyFallbackDir</c> (the still-present legacy
    /// read-fallback; Task 8 removes it) exists to keep readable — e.g. a second process writing
    /// under <c>topics/</c> after this process's first store access already ran its migration
    /// check. Without this pre-touch, seeding legacy content BEFORE the first store call would
    /// instead get swept into <c>memories/</c> by that same one-time check before the fallback
    /// mechanism these tests exercise ever got a chance to look.
    /// </summary>
    private void TouchStoreToSkipAutoMigration() => _store.LoadIndex("local");

    [Fact]
    public void ParseQualifiedName_local_scope()
    {
        var (scope, subject) = _store.ParseQualifiedName("my-notes");
        scope.Should().Be("local");
        subject.Should().Be("my-notes");
    }

    [Fact]
    public void ParseQualifiedName_topic_path()
    {
        var (scope, subject) = _store.ParseQualifiedName("/api/auth-flow");
        scope.Should().Be("local:api");
        subject.Should().Be("auth-flow");
    }

    [Fact]
    public void ParseQualifiedName_colon_is_rejected()
    {
        var act = () => _store.ParseQualifiedName("api:auth-flow");
        act.Should().Throw<ArgumentException>().WithMessage("*/api/auth-flow*");
    }

    [Fact]
    public void FormatQualifiedName_roundtrips()
    {
        _store.FormatQualifiedName("local", "notes").Should().Be("notes");
        _store.FormatQualifiedName("local:memory/api", "auth").Should().Be("/api/auth");
    }

    [Fact]
    public void Upsert_and_LoadIndex()
    {
        var entry = new ArtifactEntry("test", "file://test", 100, 1, DateTimeOffset.UtcNow);
        _store.Upsert(entry);

        var loaded = _store.LoadIndex();
        loaded.Should().HaveCount(1);
        loaded[0].Name.Should().Be("test");
    }

    [Fact]
    public void Upsert_updates_existing_entry()
    {
        var entry1 = new ArtifactEntry("test", "file://test", 100, 1, DateTimeOffset.UtcNow, ContentPreview: "v1");
        _store.Upsert(entry1);

        var entry2 = entry1 with { ContentPreview = "v2" };
        _store.Upsert(entry2);

        var loaded = _store.LoadIndex();
        loaded.Should().HaveCount(1);
        loaded[0].ContentPreview.Should().Be("v2");
    }

    [Fact]
    public void Remove_deletes_entry()
    {
        _store.Upsert(new ArtifactEntry("del-me", "", 50, 1, DateTimeOffset.UtcNow));
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
        var entry = new EphemeralEntry("scratch", "NMP...", 10, 1, DateTimeOffset.UtcNow);
        _store.RememberEphemeral("scratch", entry);

        _store.GetEphemeral("scratch").Should().NotBeNull();
        _store.GetEphemeral("scratch")!.Name.Should().Be("scratch");
    }

    [Fact]
    public void Ephemeral_forget()
    {
        _store.RememberEphemeral("bye", new EphemeralEntry("bye", "", 0, 1, DateTimeOffset.UtcNow));
        _store.ForgetEphemeral("bye").Should().BeTrue();
        _store.GetEphemeral("bye").Should().BeNull();
    }

    [Fact]
    public async Task ResolveArtifact_finds_persistent_memory()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Resolve me");
        await _store.WriteArtifactAsync("resolve-test", "local", artifact);
        _store.Upsert(new ArtifactEntry("resolve-test", "", 100, 1, DateTimeOffset.UtcNow));

        string resolved = await _store.ResolveArtifactAsync("resolve-test");
        resolved.Should().Be(artifact);
    }

    [Fact]
    public async Task ResolveArtifact_finds_ephemeral()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Ephemeral data");
        _store.RememberEphemeral("eph", new EphemeralEntry("eph", artifact, 100, 1, DateTimeOffset.UtcNow));

        string resolved = await _store.ResolveArtifactAsync("~eph");
        resolved.Should().Be(artifact);
    }

    [Fact]
    public async Task ResolveArtifact_file_uri_rejects_sibling_prefix_escape()
    {
        // ".scrinia-backup" shares the ".scrinia" prefix but is a sibling directory,
        // not a nested path — a naive StartsWith(scriniDir) check would incorrectly
        // allow this to escape the sandbox.
        string backupDir = Path.Combine(_tempDir, ".scrinia-backup");
        Directory.CreateDirectory(backupDir);
        string secretPath = Path.Combine(backupDir, "secret");
        await File.WriteAllTextAsync(secretPath, "top secret");

        Func<Task> act = async () => await _store.ResolveArtifactAsync($"file://{secretPath}");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ResolveArtifact_file_uri_allows_path_under_scrinia_dir()
    {
        string scriniaDir = Path.Combine(_tempDir, ".scrinia");
        Directory.CreateDirectory(scriniaDir);
        string okPath = Path.Combine(scriniaDir, "ok");
        await File.WriteAllTextAsync(okPath, "legit content");

        string resolved = await _store.ResolveArtifactAsync($"file://{okPath}");

        resolved.Should().Be("legit content");
    }

    [Fact]
    public void ListScoped_includes_local_entries()
    {
        _store.Upsert(new ArtifactEntry("listed", "", 50, 1, DateTimeOffset.UtcNow));
        var list = _store.ListScoped();
        list.Should().Contain(s => s.Entry.Name == "listed");
    }

    [Fact]
    public async Task CopyMemory_persistent_to_persistent()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Copy content");
        await _store.WriteArtifactAsync("src", "local", artifact);
        _store.Upsert(new ArtifactEntry("src", "", 100, 1, DateTimeOffset.UtcNow));

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
        topics.Should().Contain("local:api");
    }

    [Fact]
    public void SearchAll_returns_results()
    {
        var entry = new ArtifactEntry("search-me", "", 100, 1, DateTimeOffset.UtcNow,
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
            new("imported", "", 50, 1, DateTimeOffset.UtcNow)
        };
        var contents = new Dictionary<string, string>
        {
            ["imported"] = Nmp2ChunkedEncoder.Encode("Imported content")
        };

        _store.ImportTopicEntries("local:test", entries, contents, overwrite: false);

        var loaded = _store.LoadIndex("local:test");
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
        var entry = new ArtifactEntry("cached", "file://cached", 100, 1, DateTimeOffset.UtcNow);
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
        var entry1 = new ArtifactEntry("v1", "", 50, 1, DateTimeOffset.UtcNow);
        _store.Upsert(entry1);

        // Overwrite index via SaveIndex
        var entry2 = new ArtifactEntry("v2", "", 75, 1, DateTimeOffset.UtcNow);
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
            _store.Upsert(new ArtifactEntry($"entry-{i}", "", 10, 1, DateTimeOffset.UtcNow));

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
        first.Should().Contain("local:cached-topic");

        // Delete the directory — cache should still serve the result
        Directory.Delete(topicDir, recursive: true);

        var second = _store.DiscoverTopics();
        second.Should().Contain("local:cached-topic", "cached result should persist within TTL");
    }

    [Fact]
    public void Upsert_ExistingEntry_UsesNameDictionary()
    {
        // Insert 100 entries, then update the last one — should be O(1) via name dictionary
        for (int i = 0; i < 100; i++)
            _store.Upsert(new ArtifactEntry($"entry-{i}", "", 10, 1, DateTimeOffset.UtcNow));

        var updated = new ArtifactEntry("entry-99", "", 10, 1, DateTimeOffset.UtcNow, ContentPreview: "updated");
        _store.Upsert(updated);

        var loaded = _store.LoadIndex();
        loaded.Should().HaveCount(100);
        loaded.Should().ContainSingle(e => e.Name == "entry-99" && e.ContentPreview == "updated");
    }

    [Fact]
    public void Remove_UsesNameDictionary()
    {
        for (int i = 0; i < 50; i++)
            _store.Upsert(new ArtifactEntry($"rm-{i}", "", 10, 1, DateTimeOffset.UtcNow));

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
    public void BuildScopedTopicScope_EntityTopic_RoutesToEntityNamespace()
    {
        // v3: flattened — skill no longer gets an "entity/" namespace segment.
        MemoryNaming.BuildScopedTopicScope("skill").Should().Be("local:skill");
    }

    [Fact]
    public void BuildScopedTopicScope_AgentTopic_RoutesToAgentScope()
    {
        MemoryNaming.BuildScopedTopicScope("agent").Should().Be("local:agent");
    }

    [Fact]
    public void BuildScopedTopicScope_UserTopic_RoutesToMemoryNamespace()
    {
        MemoryNaming.BuildScopedTopicScope("dotnet").Should().Be("local:dotnet");
    }

    [Fact]
    public void FormatQualifiedName_EntityNamespaced_StripsPrefix()
    {
        _store.FormatQualifiedName("local:entity/task", "my-task").Should().Be("/task/my-task");
    }

    [Fact]
    public void FormatQualifiedName_MemoryNamespaced_StripsPrefix()
    {
        _store.FormatQualifiedName("local:memory/api", "auth").Should().Be("/api/auth");
    }

    [Fact]
    public void FormatQualifiedName_AgentScope_StripsPrefix()
    {
        _store.FormatQualifiedName("local:agent", "profile").Should().Be("/agent/profile");
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
        topics.Should().Contain("local:entity/task");
        topics.Should().Contain("local:memory/api");
    }

    [Fact]
    public void DiscoverTopics_AgentDir_ReturnsAgentScope()
    {
        string agentDir = Path.Combine(_tempDir, ".scrinia", "topics", "agent");
        Directory.CreateDirectory(agentDir);

        var topics = _store.DiscoverTopics();
        topics.Should().Contain("local:agent",
            because: "agent is a single scope without child directories");
    }

    [Fact]
    public void DiscoverTopics_LegacyFlatDir_NotShadowed_StillDiscovered()
    {
        // A legacy flat dir that has no namespaced counterpart should still appear
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "custom-notes");
        Directory.CreateDirectory(legacyDir);

        var topics = _store.DiscoverTopics();
        topics.Should().Contain("local:custom-notes",
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
        topics.Should().Contain("local:memory/api",
            because: "namespaced entry should be discovered");
        topics.Should().NotContain("local:api",
            because: "legacy flat dir shadowed by namespaced entry should be excluded");
    }

    // ── Legacy fallback: reads from legacy flat paths ────────────────────────

    [Fact]
    public async Task ReadArtifact_FallsBackToLegacyPath()
    {
        TouchStoreToSkipAutoMigration();

        // Write an artifact to the legacy flat path: topics/task/my-task.nmp2
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "task");
        Directory.CreateDirectory(legacyDir);
        string artifact = Nmp2ChunkedEncoder.Encode("Legacy task content");
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "my-task.nmp2"), artifact);

        // Read via the namespaced scope — should fall back to legacy path
        string read = await _store.ReadArtifactAsync("my-task", "local:entity/task");
        read.Should().Be(artifact,
            because: "ReadArtifactAsync should fall back to legacy flat path when the namespaced path does not exist");
    }

    [Fact]
    public void LoadIndex_MergesEntriesFromLegacyFlatDir()
    {
        TouchStoreToSkipAutoMigration();

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

        var entries = _store.LoadIndex("local:entity/task");
        entries.Should().Contain(e => e.Name == "legacy-entry",
            because: "entries from the legacy flat dir should be merged into the namespaced scope");
    }

    [Fact]
    public void LoadIndex_MergesEntriesFromLegacyFlatDir_WhenNamespacedDirDoesNotExist()
    {
        TouchStoreToSkipAutoMigration();

        // Regression: the namespaced (primary) directory for this scope is never created —
        // only the legacy flat dir holds data. A prior version of LoadIndex returned an empty
        // list as soon as the primary directory was absent, before the legacy merge ran at all,
        // hiding memories that were actually on disk.
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "task");
        Directory.CreateDirectory(legacyDir);

        string metaJson = """
        {
          "name": "legacy-only-entry",
          "uri": "file:///tmp/legacy-only-entry.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "A legacy entry with no namespaced counterpart directory"
        }
        """;
        File.WriteAllText(Path.Combine(legacyDir, "legacy-only-entry.meta.json"), metaJson);

        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "entity", "task");
        Assert.False(Directory.Exists(namespacedDir), "test setup must not pre-create the primary dir");

        var entries = _store.LoadIndex("local:entity/task");

        entries.Should().Contain(e => e.Name == "legacy-only-entry",
            because: "the legacy merge must run even when the primary directory was never created");
        Assert.False(Directory.Exists(namespacedDir),
            "a read must not materialize the primary directory as a side effect");
    }

    [Fact]
    public void LoadIndex_NamespacedEntries_TakePrecedenceOverLegacy()
    {
        TouchStoreToSkipAutoMigration();

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

        // Create the namespaced dir with the same entry name. This is the PRIMARY location for
        // scope "local:entity/task" — under v3, PathRouter.ToStoreDir routes every "local:X" scope
        // literally to memories/X (here memories/entity/task), not through StoreLayoutMigration's
        // flattening (which only rewrites content actually migrated FROM topics/entity/*, and
        // targets the flattened memories/task instead). Pre-existing content placed directly at
        // this scope's own real primary path is a different, still-valid scenario.
        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "memories", "entity", "task");
        Directory.CreateDirectory(namespacedDir);
        string namespacedMeta = """
        {
          "name": "shared-entry",
          "uri": "file:///tmp/shared-entry.nmp2",
          "originalBytes": 200,
          "chunkCount": 2,
          "createdAt": "2026-02-01T00:00:00+00:00",
          "description": "Namespaced version",
          "contentPreview": "Namespaced version"
        }
        """;
        File.WriteAllText(Path.Combine(namespacedDir, "shared-entry.meta.json"), namespacedMeta);

        var entries = _store.LoadIndex("local:entity/task");
        entries.Should().ContainSingle(e => e.Name == "shared-entry",
            because: "same-name entries should not be duplicated");
        entries.First(e => e.Name == "shared-entry").ContentPreview.Should().Be("Namespaced version",
            because: "namespaced entries take precedence over legacy entries");
    }

    // ── Legacy entry preservation in index cache ────────────────────────────

    [Fact]
    public void Upsert_NamespacedScope_PreservesLegacyEntriesInCache()
    {
        TouchStoreToSkipAutoMigration();

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
        var before = _store.LoadIndex("local:entity/concern");
        before.Should().Contain(e => e.Name == "legacy-item",
            because: "legacy entry should be discovered before any namespaced upsert");

        // Now upsert a NEW entry into the namespaced scope
        _store.Upsert(
            new ArtifactEntry("new-namespaced", "", 200, 1, DateTimeOffset.UtcNow),
            "local:entity/concern");

        // Verify LoadIndex STILL returns the legacy entry alongside the new one
        var after = _store.LoadIndex("local:entity/concern");
        after.Should().Contain(e => e.Name == "legacy-item",
            because: "upserting a new namespaced entry must not evict legacy entries from the cache");
        after.Should().Contain(e => e.Name == "new-namespaced",
            because: "the newly upserted entry must also be present");
    }

    [Fact]
    public void Remove_NamespacedScope_PreservesLegacyEntriesInCache()
    {
        TouchStoreToSkipAutoMigration();

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
            new ArtifactEntry("removable-entry", "", 200, 1, DateTimeOffset.UtcNow),
            "local:entity/concern");

        // Verify LoadIndex returns both
        var before = _store.LoadIndex("local:entity/concern");
        before.Should().Contain(e => e.Name == "legacy-survivor");
        before.Should().Contain(e => e.Name == "removable-entry");

        // Remove the namespaced entry
        _store.Remove("removable-entry", "local:entity/concern").Should().BeTrue();

        // Verify LoadIndex STILL returns the legacy entry
        var after = _store.LoadIndex("local:entity/concern");
        after.Should().Contain(e => e.Name == "legacy-survivor",
            because: "removing a namespaced entry must not evict legacy entries from the cache");
        after.Should().NotContain(e => e.Name == "removable-entry",
            because: "the removed entry must be gone");
    }

    [Fact]
    public void LoadIndex_MergesLegacyAndNamespacedEntries_WithPrecedence()
    {
        TouchStoreToSkipAutoMigration();

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

        // Create a different entry in the namespaced path — the PRIMARY location for scope
        // "local:entity/concern" under v3 (see the comment in
        // LoadIndex_NamespacedEntries_TakePrecedenceOverLegacy for why this differs from where
        // StoreLayoutMigration would flatten pre-existing topics/entity/concern content to).
        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "memories", "entity", "concern");
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
          "description": "Namespaced version of collision",
          "contentPreview": "Namespaced version of collision"
        }
        """;
        File.WriteAllText(Path.Combine(namespacedDir, "collision.meta.json"), namespacedCollisionMeta);

        // Call LoadIndex — verify both unique entries returned
        var entries = _store.LoadIndex("local:entity/concern");
        entries.Should().Contain(e => e.Name == "legacy-only",
            because: "legacy-only entries must be merged into the result");
        entries.Should().Contain(e => e.Name == "namespaced-only",
            because: "namespaced-only entries must be present");

        // Verify namespaced entry takes precedence when names collide
        entries.Should().ContainSingle(e => e.Name == "collision",
            because: "same-name entries should not be duplicated");
        entries.First(e => e.Name == "collision").ContentPreview.Should().Be("Namespaced version of collision",
            because: "namespaced entries take precedence over legacy entries");
    }

    // ── Read-time auto forward-fill (attributes migration) ──────────────────

    [Fact]
    public void LoadIndex_ForwardFillsLegacySidecar_InMemory()
    {
        // Seed a legacy-shaped PRIMARY sidecar directly on disk (tags + a recognized-key
        // keyword, NO fields) so the read-time forward-fill has something to do.
        string storeDir = Path.Combine(_tempDir, ".scrinia", "store");
        Directory.CreateDirectory(storeDir);
        string legacyMeta = """
        {
          "name": "legacy-security-note",
          "uri": "file:///tmp/legacy-security-note.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "A legacy entry",
          "tags": ["security"],
          "keywords": ["person:maria"]
        }
        """;
        File.WriteAllText(Path.Combine(storeDir, "legacy-security-note.meta.json"), legacyMeta);

        var entries = _store.LoadIndex("local");
        var entry = entries.Single(e => e.Name == "legacy-security-note");

        entry.Attributes.Should().NotBeNull(because: "read-time forward-fill should populate Fields without a prior scri migrate");
        entry.Attributes!["person"].Should().Contain("maria");
        // "tags" lost its member source in P6.6b Task 4 — Forward no longer promotes it into Attributes.
        entry.Attributes.Should().NotContainKey("tag");
    }

    [Fact]
    public void LoadIndex_PersistsForwardFill_ToPrimarySidecar()
    {
        // Seed the same legacy-shaped sidecar in the primary "local" store dir.
        string storeDir = Path.Combine(_tempDir, ".scrinia", "store");
        Directory.CreateDirectory(storeDir);
        string legacyMeta = """
        {
          "name": "legacy-security-note",
          "uri": "file:///tmp/legacy-security-note.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "A legacy entry",
          "tags": ["security"],
          "keywords": ["person:maria"]
        }
        """;
        string sidecarPath = Path.Combine(storeDir, "legacy-security-note.meta.json");
        File.WriteAllText(sidecarPath, legacyMeta);

        // First load: upgrades in memory AND writes the upgraded shape back to disk.
        _store.LoadIndex("local");

        // Open a SECOND store instance over the SAME temp root — fresh instance means a
        // cold cache, forcing a real disk read that proves the upgrade was persisted.
        using var store2 = new FileMemoryStore(_tempDir);
        var entries = store2.LoadIndex("local");
        var entry = entries.Single(e => e.Name == "legacy-security-note");

        entry.Attributes.Should().NotBeNull(
            because: "the first load's write-back should have persisted Fields to the sidecar on disk");
        entry.Attributes!["person"].Should().Contain("maria");
    }

    [Fact]
    public void LoadIndex_ConcurrentColdLoads_ForwardFillWriteBackDoesNotRace()
    {
        // Seed a single legacy-shaped sidecar so all racing cold loads try to upgrade + write
        // the SAME sidecar concurrently under LoadIndex's shared lock. Exercises both races:
        // the writer-side temp/File.Move collision (unique per-call temp names + best-effort
        // write-back) and the reader-side File.Move replace window (FileRetry around the
        // sidecar ReadAllText, so a transient lock retries instead of being dropped as "corrupt").
        string storeDir = Path.Combine(_tempDir, ".scrinia", "store");
        Directory.CreateDirectory(storeDir);
        string legacyMeta = """
        {
          "name": "legacy-security-note",
          "uri": "file:///tmp/legacy-security-note.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "A legacy entry",
          "tags": ["security"],
          "keywords": ["person:maria"]
        }
        """;
        File.WriteAllText(Path.Combine(storeDir, "legacy-security-note.meta.json"), legacyMeta);

        var act = () => Parallel.For(0, 64, _ =>
        {
            var entries = _store.LoadIndex("local");
            var entry = entries.Single(e => e.Name == "legacy-security-note");
            entry.Attributes.Should().NotBeNull();
            entry.Attributes!["person"].Should().Contain("maria");
        });

        act.Should().NotThrow(
            because: "concurrent cold-load write-backs of the same sidecar must neither throw " +
                     "(writer-side temp/Move race) nor transiently drop the entry (reader-side replace window)");
    }

    // ── Ported from ScriniaArtifactStoreTests (Task 4: store unification) ──────
    // FileMemoryStoreTests had no equivalent coverage for these behaviors before the
    // duplicate ScriniaArtifactStore was deleted.

    // ── LoadIndex edge cases ─────────────────────────────────────────────────

    [Fact]
    public void LoadIndex_MissingFile_ReturnsEmptyList()
    {
        var entries = _store.LoadIndex();

        entries.Should().BeEmpty(
            because: "a missing index.json must be treated as an empty store");
    }

    [Fact]
    public void LoadIndex_OnAbsentStoreDirectory_ReturnsEmptyAndCreatesNothing()
    {
        string storeDir = Path.Combine(_tempDir, ".scrinia", "store");

        var entries = _store.LoadIndex();

        Assert.Empty(entries);
        Assert.False(Directory.Exists(storeDir));
    }

    [Fact]
    public void LoadIndex_EmptyIndexFile_ReturnsEmptyList()
    {
        // Write an index with zero entries
        _store.SaveIndex([]);

        var entries = _store.LoadIndex();

        entries.Should().BeEmpty(
            because: "an index with no entries must load as empty");
    }

    [Fact]
    public void LoadIndex_CorruptFile_ReturnsEmptyList()
    {
        string storeDir = Path.Combine(_tempDir, ".scrinia", "store");
        Directory.CreateDirectory(storeDir);
        string indexPath = Path.Combine(storeDir, "index.json");
        File.WriteAllText(indexPath, "not valid json {{{{");

        var entries = _store.LoadIndex();

        entries.Should().BeEmpty(
            because: "a corrupt index.json must be treated as empty, not throw");
    }

    // ── Write materializes the workspace .gitignore ─────────────────────────

    [Fact]
    public void EveryWriteMaterializesTheGitIgnore()
    {
        string gitIgnore = Path.Combine(_tempDir, ".scrinia", ".gitignore");

        _store.Upsert(new ArtifactEntry("write-materializes", "file:///tmp/write-materializes.nmp2", 1024, 1, DateTimeOffset.UtcNow));

        Assert.True(File.Exists(gitIgnore));
    }

    // ── SanitizeName ──────────────────────────────────────────────────────────

    [Fact]
    public void SanitizeName_ValidName_Unchanged()
    {
        string result = _store.SanitizeName("session-notes");

        result.Should().Be("session-notes",
            because: "a name with only valid filename chars must be returned unchanged");
    }

    [Fact]
    public void SanitizeName_InvalidChars_ReplacedWithUnderscore()
    {
        string result = _store.SanitizeName("foo/bar:baz*qux");

        result.Should().NotContain("/").And.NotContain(":").And.NotContain("*",
            because: "all invalid filename characters must be replaced");
        result.Should().Contain("_",
            because: "invalid chars must be replaced with '_'");
    }

    [Fact]
    public void SanitizeName_SpaceAndDot_Unchanged()
    {
        // Spaces and dots are valid in file names on all major platforms
        string result = _store.SanitizeName("my notes.v2");

        result.Should().Be("my notes.v2",
            because: "spaces and dots are valid filename characters and must not be replaced");
    }

    // ── NameFromUri ───────────────────────────────────────────────────────────

    [Fact]
    public void NameFromUri_StoreUri_ReturnsBaseName()
    {
        string storeDir = _store.GetStoreDirForScope("local");
        // Normalise slashes for the test assertion (Windows vs Unix)
        string uri = $"file://{storeDir}/session-notes.nmp2".Replace('\\', '/');

        string name = FileMemoryStore.NameFromUri(uri);

        name.Should().Be("session-notes",
            because: "NameFromUri must strip the .nmp2 extension and return the file base name");
    }

    [Fact]
    public void NameFromUri_TempUri_ReturnsGuidSegment()
    {
        string guid = Guid.NewGuid().ToString("N");
        string uri = $"file://{Path.GetTempPath()}scrinia_{guid}.nmp2";

        string name = FileMemoryStore.NameFromUri(uri);

        name.Should().Be($"scrinia_{guid}",
            because: "NameFromUri must handle temp-dir URIs gracefully, returning the file base name");
    }

    // ── SaveIndex (atomic write) ──────────────────────────────────────────────

    [Fact]
    public void SaveIndex_AtomicWrite_IndexRoundTrips()
    {
        var entry = new ArtifactEntry("roundtrip", "file:///tmp/roundtrip.nmp2", 1024, 1, DateTimeOffset.UtcNow, ContentPreview: "Hello, world!");

        _store.SaveIndex([entry]);
        var loaded = _store.LoadIndex();

        loaded.Should().ContainSingle(e =>
            e.Name == "roundtrip" && e.ContentPreview == "Hello, world!",
            because: "SaveIndex → LoadIndex must preserve all entry fields");
    }

    [Fact]
    public void SaveIndex_NoTempFileLeft_AfterWrite()
    {
        _store.SaveIndex([new ArtifactEntry("x", "file:///tmp/x.nmp2", 1024, 1, DateTimeOffset.UtcNow)]);

        // FileMemoryStore writes one sidecar per entry (not a combined index.json), each through
        // a uniquely-named "{meta}.{pid}.{guid}.tmp" temp file — so the leftover-temp-file check
        // scans the store dir for any ".tmp" file rather than a specific "index.json.tmp" name.
        // v3 layout: "local" resolves to .scrinia/memories/, not .scrinia/store/.
        string storeDir = Path.Combine(_tempDir, ".scrinia", "memories");
        Directory.GetFiles(storeDir, "*.tmp").Should().BeEmpty(
            because: "the temp file used for atomic sidecar writes must be renamed/removed after success");
    }

    // ── ArchiveVersion ──────────────────────────────────────────────────────

    [Fact]
    public void ArchiveVersion_ExistingFile_CreatesArchive()
    {
        string artifactPath = _store.ArtifactPath("archive-test");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, "NMP/2 test content");

        _store.ArchiveVersion("archive-test");

        // v3 layout: "local" resolves to .scrinia/memories/, not .scrinia/store/.
        string versionsDir = Path.Combine(_tempDir, ".scrinia", "memories", "versions");
        Directory.Exists(versionsDir).Should().BeTrue();
        Directory.GetFiles(versionsDir, "archive-test_*.nmp2").Should().HaveCount(1);
    }

    [Fact]
    public void ArchiveVersion_NoFile_NoOp()
    {
        // Should not throw
        _store.ArchiveVersion("nonexistent");

        string versionsDir = Path.Combine(_tempDir, ".scrinia", "store", "versions");
        Directory.Exists(versionsDir).Should().BeFalse();
    }

    // ── ResolveReadScopes / NormalizeScopeFilters ────────────────────────────

    [Fact]
    public void ResolveReadScopes_Default_StartsWithLocal()
    {
        var scopes = _store.ResolveReadScopes();
        scopes.Should().StartWith("local",
            because: "local scope must always be first in default read order");
    }

    [Fact]
    public void ResolveReadScopes_ExplicitFilter_ParsesCorrectly()
    {
        var scopes = _store.ResolveReadScopes("local,dotnet");
        scopes.Should().Equal(["local", "local:dotnet"],
            because: "explicit scope filter must normalize bare topic names to the flat local-topic scope");
    }

    [Fact]
    public void NormalizeScopeFilters_BareTopicName_ReturnsLocalOnly()
    {
        var result = _store.NormalizeScopeFilters("api");

        result.Should().Equal(["local:api"],
            because: "bare topic names must resolve to the flat local-topic scope");
    }
}
