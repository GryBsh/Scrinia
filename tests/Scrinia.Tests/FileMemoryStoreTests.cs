using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

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
        // v3: FormatQualifiedName no longer strips a "memory/"/"entity/" namespace segment —
        // it round-trips the topic part verbatim (this literal is what remains of the pre-v3
        // "topic:subject" scope shape; BuildScopedTopicScope itself never produces it anymore).
        _store.FormatQualifiedName("local:memory/api", "auth").Should().Be("/memory/api/auth");
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

        string read = await _store.ReadArtifactAsync(MemoryAddress.Parse("/greet"));
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
    public async Task ReadArtifact_finds_persistent_memory()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Resolve me");
        await _store.WriteArtifactAsync("resolve-test", "local", artifact);
        _store.Upsert(new ArtifactEntry("resolve-test", "", 100, 1, DateTimeOffset.UtcNow));

        string resolved = await _store.ReadArtifactAsync(MemoryAddress.Parse("/resolve-test"));
        resolved.Should().Be(artifact);
    }

    [Fact]
    public async Task ReadArtifact_finds_ephemeral()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Ephemeral data");
        _store.RememberEphemeral("eph", new EphemeralEntry("eph", artifact, 100, 1, DateTimeOffset.UtcNow));

        string resolved = await _store.ReadArtifactAsync(MemoryAddress.Parse("~eph"));
        resolved.Should().Be(artifact);
    }

    [Fact]
    public void ListScoped_includes_local_entries()
    {
        _store.Upsert(new ArtifactEntry("listed", "", 50, 1, DateTimeOffset.UtcNow));
        var list = _store.ListScoped();
        list.Should().Contain(s => s.Entry.Name == "listed");
    }

    [Fact]
    public void DiscoverTopics_finds_topic_dirs()
    {
        // v3: DiscoverTopics scans .scrinia/memories/ directly (the old .scrinia/topics/ v1 scan
        // was deleted in Task 8) — a topic is discovered by real artifact content at its v3 dir.
        string topicDir = Path.Combine(_tempDir, ".scrinia", "memories", "api");
        Directory.CreateDirectory(topicDir);
        File.WriteAllText(Path.Combine(topicDir, "note.meta.json"), "{}");

        var topics = _store.DiscoverTopics();
        topics.Should().Contain("local:api");
    }

    [Fact]
    public void SearchAll_returns_results()
    {
        var entry = new ArtifactEntry("search-me", "", 100, 1, DateTimeOffset.UtcNow,
            Keywords: ["kubernetes", "deploy"]);
        _store.Upsert(entry);

        var results = _store.SearchAll(new SearchQuery("kubernetes"));
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void ImportTopicEntries_creates_topic()
    {
        var entries = new List<ArtifactEntry>
        {
            new(MemoryAddress.Parse("/imported"), "artifacts/imported.nmp2", 50, 1, DateTimeOffset.UtcNow)
        };
        var contents = new Dictionary<string, string>
        {
            ["imported"] = Nmp2ChunkedEncoder.Encode("Imported content")
        };

        _store.ImportTopicEntries("local:test", entries,
            contents.ToDictionary(pair => pair.Key, pair => System.Text.Encoding.UTF8.GetBytes(pair.Value)),
            overwrite: false);

        var loaded = _store.LoadIndex("local:test");
        loaded.Should().HaveCount(1);
        loaded[0].Name.Should().Be("imported");
        loaded[0].Address.Should().Be(MemoryAddress.Parse("/test/imported"));
        loaded[0].ArtifactKey.Should().Be("artifacts/test/imported.nmp2");
    }

    [Fact]
    public void GenerateContentPreview_truncates()
    {
        string long_content = new string('x', 1000);
        string preview = ContentPreview.Generate(long_content);
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
    public void DiscoverTopicsReflectsExternalDeletionImmediately()
    {
        string topicDir = Path.Combine(_tempDir, ".scrinia", "memories", "cached-topic");
        Directory.CreateDirectory(topicDir);
        File.WriteAllText(Path.Combine(topicDir, "note.meta.json"), "{}");

        var first = _store.DiscoverTopics();
        first.Should().Contain("local:cached-topic");

        // Delete the directory — cache should still serve the result
        Directory.Delete(topicDir, recursive: true);

        var second = _store.DiscoverTopics();
        second.Should().NotContain("local:cached-topic", "discovery must reflect external deletion immediately");
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
        string read1 = await _store.ReadArtifactAsync(MemoryAddress.Parse("/cache-hit"));
        read1.Should().Be(artifact);

        // Second read — should be cache hit (same content)
        string read2 = await _store.ReadArtifactAsync(MemoryAddress.Parse("/cache-hit"));
        read2.Should().Be(artifact);
    }

    [Fact]
    public async Task WriteArtifact_InvalidatesCache()
    {
        string v1 = Nmp2ChunkedEncoder.Encode("Version 1");
        await _store.WriteArtifactAsync("invalidate", "local", v1);

        // Read to populate cache
        string read1 = await _store.ReadArtifactAsync(MemoryAddress.Parse("/invalidate"));
        read1.Should().Be(v1);

        // Overwrite
        string v2 = Nmp2ChunkedEncoder.Encode("Version 2");
        await _store.WriteArtifactAsync("invalidate", "local", v2);

        // Should read v2, not cached v1
        string read2 = await _store.ReadArtifactAsync(MemoryAddress.Parse("/invalidate"));
        read2.Should().Be(v2);
    }

    [Fact]
    public async Task DeleteArtifact_InvalidatesCache()
    {
        string artifact = Nmp2ChunkedEncoder.Encode("Delete cache test");
        await _store.WriteArtifactAsync("del-cache", "local", artifact);

        // Populate cache
        await _store.ReadArtifactAsync(MemoryAddress.Parse("/del-cache"));

        // Delete
        _store.DeleteArtifact("del-cache", "local").Should().BeTrue();

        // Read should now throw
        var act = () => _store.ReadArtifactAsync(MemoryAddress.Parse("/del-cache"));
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
    public void FormatQualifiedName_EntityNamespaced_NoLongerStripsPrefix()
    {
        // v3: FormatQualifiedName no longer special-cases the old "entity/" v1 namespace segment
        // — it round-trips the topic part verbatim (FormatQualifiedName is now identical to the
        // removed FormatRoundTripQualifiedName, which is why that method was deleted).
        _store.FormatQualifiedName("local:entity/task", "my-task").Should().Be("/entity/task/my-task");
    }

    [Fact]
    public void FormatQualifiedName_MemoryNamespaced_NoLongerStripsPrefix()
    {
        _store.FormatQualifiedName("local:memory/api", "auth").Should().Be("/memory/api/auth");
    }

    [Fact]
    public void FormatQualifiedName_AgentScope_StripsPrefix()
    {
        _store.FormatQualifiedName("local:agent", "profile").Should().Be("/agent/profile");
    }

    [Fact]
    public void DiscoverTopics_MigratesNamespacedDirs_FlattenedNotNamespaced()
    {
        // Pre-v3 namespaced directories: topics/entity/task, topics/memory/api. Under v3 these
        // flatten during migration (MapTopicFileToV2 strips the entity/memory prefix), so the
        // scopes DiscoverTopics reports afterward are the FLAT forms — "local:entity/task" and
        // "local:memory/api" no longer exist as discoverable scopes at all.
        string entityTaskDir = Path.Combine(_tempDir, ".scrinia", "topics", "entity", "task");
        string memoryApiDir = Path.Combine(_tempDir, ".scrinia", "topics", "memory", "api");
        Directory.CreateDirectory(entityTaskDir);
        Directory.CreateDirectory(memoryApiDir);
        File.WriteAllText(Path.Combine(entityTaskDir, "note.meta.json"), "{}");
        File.WriteAllText(Path.Combine(memoryApiDir, "note.meta.json"), "{}");

        var topics = _store.DiscoverTopics();

        topics.Should().Contain("local:task", because: "topics/entity/task flattens to memories/task");
        topics.Should().Contain("local:api", because: "topics/memory/api flattens to memories/api");
        topics.Should().NotContain("local:entity/task");
        topics.Should().NotContain("local:memory/api");
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "task", "note.meta.json")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "api", "note.meta.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(_tempDir, ".scrinia", "topics")).Should().BeFalse();
    }

    [Fact]
    public void DiscoverTopics_MigratesAgentDir_ReturnsAgentScope()
    {
        // topics/agent/ is FileMemoryStore's own NMP2 agent layout (distinct from the top-level
        // .scrinia/agent/*.md files MemoryTools.Core.cs reads) — MapTopicFileToV2 keeps "agent/"
        // as-is, so it migrates straight to memories/agent/.
        string agentDir = Path.Combine(_tempDir, ".scrinia", "topics", "agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "note.nmp2"), "NMP/2 v1 1\ndata");

        var topics = _store.DiscoverTopics();

        topics.Should().Contain("local:agent",
            because: "agent is a single scope without child directories");
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "agent", "note.nmp2")).Should().BeTrue();
    }

    [Fact]
    public void DiscoverTopics_MigratesLegacyFlatDir_StillDiscovered()
    {
        // A legacy flat dir that has no namespaced counterpart migrates to the same flat name.
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "custom-notes");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "note.meta.json"), "{}");

        var topics = _store.DiscoverTopics();

        topics.Should().Contain("local:custom-notes",
            because: "migration moves the legacy flat dir's content into memories/ before DiscoverTopics scans it");
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "custom-notes", "note.meta.json")).Should().BeTrue();
        Directory.Exists(legacyDir).Should().BeFalse();
    }

    [Fact]
    public void DiscoverTopics_MigratesShadowedNamespacedAndFlatDirs_MergedIntoOneScope()
    {
        // Pre-v3, "topics/api" (flat) and "topics/memory/api" (namespaced) were two distinct
        // v1 layouts for the same conceptual topic, and DiscoverTopics's old v1 scan had to
        // shadow one in favor of the other to avoid reporting both. Migration now moves both
        // into the SAME v3 target (memories/api/), so there's exactly one scope afterward —
        // holding both files, not a shadowed/excluded pair.
        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "memory", "api");
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "api");
        Directory.CreateDirectory(namespacedDir);
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(namespacedDir, "namespaced-note.meta.json"), "{}");
        File.WriteAllText(Path.Combine(legacyDir, "flat-note.meta.json"), "{}");

        var topics = _store.DiscoverTopics();

        topics.Should().ContainSingle(t => t == "local:api",
            because: "both the flat and memory/-namespaced legacy dirs migrate into the SAME v3 scope — one topic, not a shadowed pair");
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "api", "namespaced-note.meta.json")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "api", "flat-note.meta.json")).Should().BeTrue();
    }

    // ── Legacy read fallback (deleted in Task 8): converted to migration tests ──

    [Fact]
    public async Task ReadArtifact_MigratesFromLegacyPath()
    {
        // Write an artifact directly to the pre-v3 flat legacy path.
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "task");
        Directory.CreateDirectory(legacyDir);
        string artifact = Nmp2ChunkedEncoder.Encode("Legacy task content");
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "my-task.nmp2"), artifact);

        // First store access triggers the automatic v3 migration, moving the file into
        // memories/ before the read below — there is no more legacy-path fallback to exercise.
        string read = await _store.ReadArtifactAsync(MemoryAddress.Parse("/task/my-task"));
        read.Should().Be(artifact,
            because: "migration moves legacy artifacts into the v3 layout before any read observes them");

        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "task", "my-task.nmp2")).Should().BeTrue();
        File.Exists(Path.Combine(legacyDir, "my-task.nmp2")).Should().BeFalse();
    }

    [Fact]
    public void LoadIndex_MigratesEntriesFromLegacyFlatDir()
    {
        // Create a legacy flat dir with a sidecar meta file, before any store access.
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "task");
        Directory.CreateDirectory(legacyDir);

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

        var entries = _store.LoadIndex("local:task");

        entries.Should().Contain(e => e.Name == "legacy-entry",
            because: "the automatic v3 migration should have moved the legacy sidecar into memories/ before this read");
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "task", "legacy-entry.meta.json"))
            .Should().BeTrue("migration moves the file rather than merely reading through it");
        Directory.Exists(Path.Combine(_tempDir, ".scrinia", "topics"))
            .Should().BeFalse("the legacy topics/ root is pruned once empty");
    }

    [Fact]
    public void LoadIndex_MigratesEntriesFromLegacyFlatDir_WhenNamespacedDirDoesNotExist()
    {
        // Regression carried over from the pre-Task-8 fallback test: the read path must not
        // require the v3 target directory to already exist — migration creates it as part of
        // the move, and LoadIndex must see the result on the very first cold read.
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "notes-only");
        Directory.CreateDirectory(legacyDir);

        string metaJson = """
        {
          "name": "legacy-only-entry",
          "uri": "file:///tmp/legacy-only-entry.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "A legacy entry with no pre-existing v3 target directory"
        }
        """;
        File.WriteAllText(Path.Combine(legacyDir, "legacy-only-entry.meta.json"), metaJson);

        string targetDir = Path.Combine(_tempDir, ".scrinia", "memories", "notes-only");
        Directory.Exists(targetDir).Should().BeFalse("test setup must not pre-create the v3 target dir");
        Directory.Exists(Path.Combine(_tempDir, ".scrinia", "memories")).Should().BeFalse(
            "test setup must not pre-create the memories/ root either");

        var entries = _store.LoadIndex("local:notes-only");

        entries.Should().Contain(e => e.Name == "legacy-only-entry",
            because: "migration must create the v3 target directory as part of the move, not require it pre-exist");
        File.Exists(Path.Combine(targetDir, "legacy-only-entry.meta.json")).Should().BeTrue();
        Directory.Exists(legacyDir).Should().BeFalse();
    }

    [Fact]
    public void LoadIndex_NamespacedEntries_TakePrecedenceOverLegacy()
    {
        // "Namespaced entries take precedence over legacy" no longer means a read-time merge —
        // it means migration's deterministic collision order. Both legacy locations below
        // migrate to the SAME v3 target (memories/api/): the flat dir (rank 0) wins, the
        // memory/-namespaced dir (rank 2) is quarantined, not silently dropped or merged.
        string flatDir = Path.Combine(_tempDir, ".scrinia", "topics", "api");
        Directory.CreateDirectory(flatDir);
        File.WriteAllText(Path.Combine(flatDir, "shared-entry.meta.json"), """
        {
          "name": "shared-entry",
          "uri": "file:///tmp/shared-entry.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "Flat version"
        }
        """);

        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "memory", "api");
        Directory.CreateDirectory(namespacedDir);
        File.WriteAllText(Path.Combine(namespacedDir, "shared-entry.meta.json"), """
        {
          "name": "shared-entry",
          "uri": "file:///tmp/shared-entry.nmp2",
          "originalBytes": 200,
          "chunkCount": 2,
          "createdAt": "2026-02-01T00:00:00+00:00",
          "description": "Namespaced version",
          "contentPreview": "Namespaced version"
        }
        """);

        var entries = _store.LoadIndex("local:api");

        entries.Should().ContainSingle(e => e.Name == "shared-entry",
            because: "the migration collision is resolved to a single winner, not merged duplicates");
        entries.First(e => e.Name == "shared-entry").ContentPreview.Should().BeNullOrEmpty(
            because: "the flat (non-namespaced) legacy location wins migration's deterministic collision order");
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "api", "shared-entry.meta.json"))
            .Should().BeTrue();
        Directory.EnumerateDirectories(_tempDir, ".scrinia-conflicts-*", SearchOption.TopDirectoryOnly)
            .Should().NotBeEmpty(because: "the losing (namespaced) copy must be quarantined, not silently dropped");
        Directory.Exists(Path.Combine(_tempDir, ".scrinia", "topics")).Should().BeFalse();
    }

    // ── Legacy entries survive a mutation on the same migrated scope ────────

    [Fact]
    public void Upsert_MigratedScope_PreservesMigratedEntriesInCache()
    {
        // Seed a legacy sidecar and let the automatic v3 migration move it into place.
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "concern");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "legacy-item.meta.json"), """
        {
          "name": "legacy-item",
          "uri": "file:///tmp/legacy-item.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "A legacy concern"
        }
        """);

        var before = _store.LoadIndex("local:concern");
        before.Should().Contain(e => e.Name == "legacy-item",
            because: "the legacy sidecar should have migrated into memories/concern/ on first store access");
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "concern", "legacy-item.meta.json"))
            .Should().BeTrue();

        // Now upsert a NEW entry into the same (now-migrated) scope.
        _store.Upsert(new ArtifactEntry("new-entry", "", 200, 1, DateTimeOffset.UtcNow), "local:concern");

        var after = _store.LoadIndex("local:concern");
        after.Should().Contain(e => e.Name == "legacy-item",
            because: "upserting a new entry must not evict the migrated sidecar from the cache");
        after.Should().Contain(e => e.Name == "new-entry");
    }

    [Fact]
    public void Remove_MigratedScope_PreservesOtherMigratedEntriesInCache()
    {
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "concern");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "legacy-survivor.meta.json"), """
        {
          "name": "legacy-survivor",
          "uri": "file:///tmp/legacy-survivor.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "Legacy entry that must survive removal"
        }
        """);

        // First access migrates the legacy sidecar into memories/concern/.
        _store.LoadIndex("local:concern").Should().Contain(e => e.Name == "legacy-survivor");
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "concern", "legacy-survivor.meta.json"))
            .Should().BeTrue();

        _store.Upsert(new ArtifactEntry("removable-entry", "", 200, 1, DateTimeOffset.UtcNow), "local:concern");

        var before = _store.LoadIndex("local:concern");
        before.Should().Contain(e => e.Name == "legacy-survivor");
        before.Should().Contain(e => e.Name == "removable-entry");

        _store.Remove("removable-entry", "local:concern").Should().BeTrue();

        var after = _store.LoadIndex("local:concern");
        after.Should().Contain(e => e.Name == "legacy-survivor",
            because: "removing another entry must not evict the migrated sidecar from the cache");
        after.Should().NotContain(e => e.Name == "removable-entry");
    }

    [Fact]
    public void LoadIndex_MergesLegacyAndNamespacedEntries_WithPrecedence()
    {
        // Two different legacy locations, migrating to the SAME v3 scope ("local:concern"):
        // topics/concern/ (flat, rank 0) and topics/memory/concern/ (rank 2, strips the
        // "memory/" prefix onto the same target dir). Each holds a unique entry plus a
        // colliding one — proves migration both MERGES the unique entries into one scope
        // and resolves the collision deterministically (flat wins), rather than silently
        // dropping either side.
        string legacyDir = Path.Combine(_tempDir, ".scrinia", "topics", "concern");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "legacy-only.meta.json"), """
        {
          "name": "legacy-only",
          "uri": "file:///tmp/legacy-only.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "Only in legacy"
        }
        """);
        File.WriteAllText(Path.Combine(legacyDir, "collision.meta.json"), """
        {
          "name": "collision",
          "uri": "file:///tmp/collision.nmp2",
          "originalBytes": 100,
          "chunkCount": 1,
          "createdAt": "2026-01-01T00:00:00+00:00",
          "description": "Flat version of collision"
        }
        """);

        string namespacedDir = Path.Combine(_tempDir, ".scrinia", "topics", "memory", "concern");
        Directory.CreateDirectory(namespacedDir);
        File.WriteAllText(Path.Combine(namespacedDir, "namespaced-only.meta.json"), """
        {
          "name": "namespaced-only",
          "uri": "file:///tmp/namespaced-only.nmp2",
          "originalBytes": 200,
          "chunkCount": 1,
          "createdAt": "2026-02-01T00:00:00+00:00",
          "description": "Only in namespaced"
        }
        """);
        File.WriteAllText(Path.Combine(namespacedDir, "collision.meta.json"), """
        {
          "name": "collision",
          "uri": "file:///tmp/collision.nmp2",
          "originalBytes": 300,
          "chunkCount": 2,
          "createdAt": "2026-03-01T00:00:00+00:00",
          "description": "Namespaced version of collision",
          "contentPreview": "Namespaced version of collision"
        }
        """);

        var entries = _store.LoadIndex("local:concern");

        entries.Should().Contain(e => e.Name == "legacy-only",
            because: "the flat legacy entry must migrate and be visible");
        entries.Should().Contain(e => e.Name == "namespaced-only",
            because: "the memory/-namespaced entry must migrate (prefix stripped) to the same v3 scope");
        entries.Should().ContainSingle(e => e.Name == "collision",
            because: "a migration collision resolves to one winner, not duplicate entries");
        entries.First(e => e.Name == "collision").ContentPreview.Should().BeNullOrEmpty(
            because: "the flat (non-namespaced) copy wins the deterministic migration collision order");

        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "concern", "legacy-only.meta.json")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, ".scrinia", "memories", "concern", "namespaced-only.meta.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(_tempDir, ".scrinia", "topics")).Should().BeFalse();
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

        entry.Attributes.Should().NotBeNull(because: "read-time forward-fill should populate Attributes without a prior scri migrate");
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
            because: "the first load's write-back should have persisted Attributes to the sidecar on disk");
        entry.Attributes!["person"].Should().Contain("maria");
    }

    [Fact]
    public void LayoutOnlyPathLookupDoesNotAcquireAnUnrelatedScopeLock()
    {
        string legacyDirectory = Path.Combine(_tempDir, ".scrinia", "store");
        Directory.CreateDirectory(legacyDirectory);
        byte[] raw = System.Text.Encoding.UTF8.GetBytes("""
            {"name":"layout-only","uri":"file:///layout-only.nmp2","originalBytes":1,"chunkCount":1,"createdAt":"2026-01-01T00:00:00+00:00","keywords":["person:maria"]}
            """);
        File.WriteAllBytes(Path.Combine(legacyDirectory, "layout-only.meta.json"), raw);
        string destination = Path.Combine(_tempDir, ".scrinia", "memories");
        using var held = FileLock.AcquireExclusive(Path.Combine(destination, ".lock"));
        StoreLayoutMigration.NeedsMigration(_tempDir).Should().BeTrue(
            "the destination lock must not make the legacy-layout preparation vacuous");

        string? resolved = null;
        Exception? failure = Record.Exception(() => resolved = _store.GetStoreDirForScope("local"));

        StoreLayoutMigration.NeedsMigration(_tempDir).Should().BeFalse();
        File.Exists(Path.Combine(legacyDirectory, "layout-only.meta.json")).Should().BeFalse();
        File.ReadAllBytes(Path.Combine(destination, "layout-only.meta.json")).Should().Equal(raw,
            "layout preparation must relocate raw metadata without index forward-fill");
        failure.Should().BeNull("a completed layout-only path lookup needs no exclusive scope ownership");
        resolved.Should().Be(destination);
    }

    [Fact]
    public async Task LayoutOnlyAsyncArtifactReadKeepsSharedScopeOwnership()
    {
        string legacyDirectory = Path.Combine(_tempDir, ".scrinia", "store");
        Directory.CreateDirectory(legacyDirectory);
        string artifact = Nmp2ChunkedEncoder.Encode("async layout body");
        byte[] raw = System.Text.Encoding.UTF8.GetBytes("""
            {"name":"layout-async","uri":"file:///layout-async.nmp2","originalBytes":17,"chunkCount":1,"createdAt":"2026-01-01T00:00:00+00:00","keywords":[],"attributes":{}}
            """);
        File.WriteAllText(Path.Combine(legacyDirectory, "layout-async.nmp2"), artifact);
        File.WriteAllBytes(Path.Combine(legacyDirectory, "layout-async.meta.json"), raw);
        string destination = Path.Combine(_tempDir, ".scrinia", "memories");
        Directory.CreateDirectory(destination);
        using var held = FileLock.AcquireShared(Path.Combine(destination, ".lock"));
        StoreLayoutMigration.NeedsMigration(_tempDir).Should().BeTrue(
            "the shared destination lock must not bypass legacy-layout preparation");

        string? actual = null;
        Exception? failure = await Record.ExceptionAsync(async () =>
            actual = await _store.ReadArtifactAsync(MemoryAddress.Parse("/layout-async")));

        StoreLayoutMigration.NeedsMigration(_tempDir).Should().BeFalse();
        File.Exists(Path.Combine(legacyDirectory, "layout-async.nmp2")).Should().BeFalse();
        File.ReadAllBytes(Path.Combine(destination, "layout-async.meta.json")).Should().Equal(raw);
        File.ReadAllText(Path.Combine(destination, "layout-async.nmp2")).Should().Be(artifact);
        failure.Should().BeNull("the actual async read is compatible with existing shared scope ownership");
        actual.Should().Be(artifact);
    }

    [Fact]
    public void LoadIndex_ConcurrentColdLoads_ForwardFillWriteBackDoesNotRace()
    {
        // All64 concurrent readers must observe the legacy entry and projected attributes.
        // Layout preparation preserves raw metadata; required forward-fill runs under exclusive
        // scope ownership before readers resume. Neither lock contention nor publication windows
        // may make a successful cold read lose the entry. Keep the original concurrency oracle.
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

    // Archive names were stamped to the SECOND and copied with overwrite:true, so two
    // archives of the same memory inside one second silently replaced each other --
    // losing the older body from the only directory backing "a write never destroys
    // what was already there". Two overwrites in a row is not a contrived case: it is
    // an agent correcting itself mid-turn.
    [Fact]
    public void ArchiveVersion_TwiceWithinOneSecond_KeepsBothBodies()
    {
        string artifactPath = _store.ArtifactPath("rapid");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        File.WriteAllText(artifactPath, "NMP/2 first body");
        _store.ArchiveVersion("rapid");
        File.WriteAllText(artifactPath, "NMP/2 second body");
        _store.ArchiveVersion("rapid");

        string versionsDir = Path.Combine(_tempDir, ".scrinia", "memories", "versions");
        string[] archives = Directory.GetFiles(versionsDir, "rapid_*.nmp2");

        archives.Should().HaveCount(2, "each archive is a distinct prior body, not a slot to overwrite");
        archives.Select(File.ReadAllText).Should()
            .Contain("NMP/2 first body")
            .And.Contain("NMP/2 second body");
    }

    // The archive held the artifact only. docs/server-admin.md documents versions/ as the
    // human recovery path, but copying a body back left the sidecar describing the NEWER
    // content -- and FileMemoryStore's own comment records where that leads: a later
    // append reads the stale ChunkEntries and yields non-contiguous chunk indices. A
    // recovery path that corrupts the store is not a recovery path.
    [Fact]
    public void ArchiveVersion_ArchivesTheSidecarBesideTheArtifact()
    {
        string artifactPath = _store.ArtifactPath("with-meta");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, "NMP/2 body");
        File.WriteAllText(_store.MetaPath("with-meta"), """{"name":"with-meta","chunkCount":1}""");

        _store.ArchiveVersion("with-meta");

        string versionsDir = Path.Combine(_tempDir, ".scrinia", "memories", "versions");
        string artifact = Directory.GetFiles(versionsDir, "with-meta_*.nmp2").Should().ContainSingle().Subject;

        string sidecar = Path.ChangeExtension(artifact, null) + ".meta.json";
        File.Exists(sidecar).Should().BeTrue(
            because: "a body without its metadata cannot be restored without corrupting the entry");
        File.ReadAllText(sidecar).Should().Contain("with-meta");
    }

    // ── ResolveReadScopes / NormalizeScopeFilters ────────────────────────────

    [Fact]
    public void ResolveReadScopes_Default_StartsWithLocal()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".scrinia", "memories"));
        var scopes = _store.ResolveReadScopes();
        scopes.Should().StartWith("local",
            because: "local scope must always be first in default read order");
    }

    [Fact]
    public void ResolveReadScopes_ExplicitFilter_ParsesCorrectly()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".scrinia", "memories"));
        var scopes = _store.ResolveReadScopes(["local", "dotnet"]);
        scopes.Should().Equal(["local", "local:dotnet"],
            because: "explicit scope filter must normalize bare topic names to the flat local-topic scope");
    }

    /// <summary>Task 9, D-4: an empty <c>scopes</c> list must behave identically to null — the same
    /// trap Task 6's fix round hit with an explicit null/empty <c>mode</c>. A caller that sends
    /// <c>[]</c> over JSON-RPC (as opposed to omitting the field) must not get a narrower answer
    /// than one that sent nothing at all.</summary>
    [Fact]
    public void ResolveReadScopes_EmptyList_BehavesIdenticallyToNull()
    {
        _store.Upsert(new ArtifactEntry("a", "file:///a.nmp2", 10, 1, DateTimeOffset.UtcNow), "local:findings");

        _store.ResolveReadScopes([]).Should().BeEquivalentTo(_store.ResolveReadScopes(null),
            because: "an empty scopes list is not a narrower filter than null — both mean every scope");
    }

    /// <summary>Task 9, D-4, the <c>ListScoped</c> sibling of the test above.</summary>
    [Fact]
    public void ListScoped_EmptyScopesList_BehavesIdenticallyToNull()
    {
        _store.Upsert(new ArtifactEntry("a", "file:///a.nmp2", 10, 1, DateTimeOffset.UtcNow), "local:findings");

        var withEmpty = _store.ListScoped([]).Select(sa => (sa.Scope, sa.Entry.Name)).ToList();
        var withNull = _store.ListScoped(null).Select(sa => (sa.Scope, sa.Entry.Name)).ToList();
        withEmpty.Should().BeEquivalentTo(withNull);
    }

    /// <summary>Task 9, D-4: an empty <c>excludeTopics</c> list must exclude nothing, identically
    /// to null.</summary>
    [Fact]
    public void ListScoped_EmptyExcludeTopicsList_BehavesIdenticallyToNull()
    {
        _store.Upsert(new ArtifactEntry("a", "file:///a.nmp2", 10, 1, DateTimeOffset.UtcNow), "local:findings");

        var withEmpty = _store.ListScoped(null, []).Select(sa => (sa.Scope, sa.Entry.Name)).ToList();
        var withNull = _store.ListScoped(null, null).Select(sa => (sa.Scope, sa.Entry.Name)).ToList();
        withEmpty.Should().BeEquivalentTo(withNull);
    }

    [Fact]
    public void DefaultScopes_IncludeEveryDiscoveredTopic()
    {
        // IsEntityScope excluded "skill" (and any local:entity/* scope) from unfiltered
        // searches. Both branches are dead under v3: the migration leaves no entity/
        // directories, and skills are file entities rather than store entries. A
        // memories/skill/ topic is only reachable from an unreleased dev build's
        // migration, so including it costs nothing and removes a half-dead predicate.
        _store.Upsert(new ArtifactEntry("a", "file:///a.nmp2", 10, 1, DateTimeOffset.UtcNow), "local:skill");
        _store.Upsert(new ArtifactEntry("b", "file:///b.nmp2", 10, 1, DateTimeOffset.UtcNow), "local:findings");

        IReadOnlyList<string> scopes = _store.ResolveReadScopes(null);

        scopes.Should().Contain("local:skill");
        scopes.Should().Contain("local:findings");
    }

    [Fact]
    public void NormalizeScopeFilters_BareTopicName_ReturnsLocalOnly()
    {
        var result = _store.NormalizeScopeFilters("api");

        result.Should().Equal(["local:api"],
            because: "bare topic names must resolve to the flat local-topic scope");
    }
}
