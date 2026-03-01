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
        scope.Should().Be("local-topic:api");
        subject.Should().Be("auth-flow");
    }

    [Fact]
    public void FormatQualifiedName_roundtrips()
    {
        _store.FormatQualifiedName("local", "notes").Should().Be("notes");
        _store.FormatQualifiedName("local-topic:api", "auth").Should().Be("api:auth");
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
}
