using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Unit tests for <see cref="ScriniaArtifactStore"/>.
///
/// Each test that writes to the index uses a temporary directory overlay via the
/// <see cref="StoreScope"/> helper so that tests are isolated from the real user store
/// and from each other.
/// </summary>
public sealed class ScriniaArtifactStoreTests
{
    // ── StoreDir ──────────────────────────────────────────────────────────────

    [Fact]
    public void StoreDir_DefaultValue_ContainsScrinia()
    {
        ScriniaArtifactStore.StoreDir.Should().Contain("scrinia",
            because: "the store directory must be under the scrinia application folder");
        ScriniaArtifactStore.StoreDir.Should().Contain("store",
            because: "the store directory name must be 'store'");
    }

    [Fact]
    public void ResolveReadScopes_Default_StartsWithLocal()
    {
        using var scope = new TestHelpers.StoreScope();
        var scopes = ScriniaArtifactStore.ResolveReadScopes();
        scopes.Should().StartWith("local",
            because: "local scope must always be first in default read order");
    }

    [Fact]
    public void ResolveReadScopes_ExplicitFilter_ParsesCorrectly()
    {
        var scopes = ScriniaArtifactStore.ResolveReadScopes("local,dotnet");
        scopes.Should().Equal(["local", "local-topic:dotnet"],
            because: "explicit scope filter must normalize bare topic names to local-topic");
    }

    // ── LoadIndex ─────────────────────────────────────────────────────────────

    [Fact]
    public void LoadIndex_MissingFile_ReturnsEmptyList()
    {
        using var scope = new TestHelpers.StoreScope();

        var entries = ScriniaArtifactStore.LoadIndex();

        entries.Should().BeEmpty(
            because: "a missing index.json must be treated as an empty store");
    }

    [Fact]
    public void LoadIndex_EmptyIndexFile_ReturnsEmptyList()
    {
        using var scope = new TestHelpers.StoreScope();
        // Write an index with zero entries
        ScriniaArtifactStore.SaveIndex([]);

        var entries = ScriniaArtifactStore.LoadIndex();

        entries.Should().BeEmpty(
            because: "an index with no entries must load as empty");
    }

    [Fact]
    public void LoadIndex_CorruptFile_ReturnsEmptyList()
    {
        using var scope = new TestHelpers.StoreScope();
        Directory.CreateDirectory(scope.TempDir);
        string indexPath = Path.Combine(scope.TempDir, "index.json");
        File.WriteAllText(indexPath, "not valid json {{{{");

        var entries = ScriniaArtifactStore.LoadIndex();

        entries.Should().BeEmpty(
            because: "a corrupt index.json must be treated as empty, not throw");
    }

    // ── Upsert ────────────────────────────────────────────────────────────────

    [Fact]
    public void Upsert_NewEntry_AppearsInIndex()
    {
        using var scope = new TestHelpers.StoreScope();
        var entry = MakeEntry("notes");

        ScriniaArtifactStore.Upsert(entry);
        var loaded = ScriniaArtifactStore.LoadIndex();

        loaded.Should().ContainSingle(e => e.Name == "notes",
            because: "the upserted entry must appear in the index");
    }

    [Fact]
    public void Upsert_ExistingName_Overwrites()
    {
        using var scope = new TestHelpers.StoreScope();
        ScriniaArtifactStore.Upsert(MakeEntry("notes", desc: "original"));
        ScriniaArtifactStore.Upsert(MakeEntry("notes", desc: "updated"));

        var loaded = ScriniaArtifactStore.LoadIndex();

        loaded.Should().ContainSingle(e => e.Name == "notes",
            because: "upserting the same name must not duplicate — it must replace");
        loaded[0].Description.Should().Be("updated",
            because: "the description must reflect the most recent upsert");
    }

    [Fact]
    public void Upsert_TwoDistinctNames_BothInIndex()
    {
        using var scope = new TestHelpers.StoreScope();
        ScriniaArtifactStore.Upsert(MakeEntry("alpha"));
        ScriniaArtifactStore.Upsert(MakeEntry("beta"));

        var loaded = ScriniaArtifactStore.LoadIndex();

        loaded.Should().HaveCount(2,
            because: "two distinct names must produce two separate entries");
        loaded.Should().Contain(e => e.Name == "alpha");
        loaded.Should().Contain(e => e.Name == "beta");
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_ExistingEntry_ReturnsTrueAndRemoves()
    {
        using var scope = new TestHelpers.StoreScope();
        ScriniaArtifactStore.Upsert(MakeEntry("target"));

        bool removed = ScriniaArtifactStore.Remove("target");
        var loaded = ScriniaArtifactStore.LoadIndex();

        removed.Should().BeTrue(because: "Remove must return true when the entry existed");
        loaded.Should().BeEmpty(because: "the entry must be removed from the index");
    }

    [Fact]
    public void Remove_MissingEntry_ReturnsFalse()
    {
        using var scope = new TestHelpers.StoreScope();

        bool removed = ScriniaArtifactStore.Remove("nonexistent");

        removed.Should().BeFalse(
            because: "Remove must return false when no matching entry exists");
    }

    [Fact]
    public void Remove_OnlyRemovesMatchingEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        ScriniaArtifactStore.Upsert(MakeEntry("keep"));
        ScriniaArtifactStore.Upsert(MakeEntry("delete-me"));

        ScriniaArtifactStore.Remove("delete-me");
        var loaded = ScriniaArtifactStore.LoadIndex();

        loaded.Should().ContainSingle(e => e.Name == "keep",
            because: "only the named entry must be removed");
    }

    // ── SanitizeName ──────────────────────────────────────────────────────────

    [Fact]
    public void SanitizeName_ValidName_Unchanged()
    {
        string result = ScriniaArtifactStore.SanitizeName("session-notes");

        result.Should().Be("session-notes",
            because: "a name with only valid filename chars must be returned unchanged");
    }

    [Fact]
    public void SanitizeName_InvalidChars_ReplacedWithUnderscore()
    {
        string result = ScriniaArtifactStore.SanitizeName("foo/bar:baz*qux");

        result.Should().NotContain("/").And.NotContain(":").And.NotContain("*",
            because: "all invalid filename characters must be replaced");
        result.Should().Contain("_",
            because: "invalid chars must be replaced with '_'");
    }

    [Fact]
    public void SanitizeName_SpaceAndDot_Unchanged()
    {
        // Spaces and dots are valid in file names on all major platforms
        string result = ScriniaArtifactStore.SanitizeName("my notes.v2");

        result.Should().Be("my notes.v2",
            because: "spaces and dots are valid filename characters and must not be replaced");
    }

    // ── NameFromUri ───────────────────────────────────────────────────────────

    [Fact]
    public void NameFromUri_StoreUri_ReturnsBaseName()
    {
        string uri = $"file://{ScriniaArtifactStore.StoreDir}/session-notes.nmp2";
        // Normalise slashes for the test assertion (Windows vs Unix)
        uri = uri.Replace('\\', '/');

        string name = ScriniaArtifactStore.NameFromUri(uri);

        name.Should().Be("session-notes",
            because: "NameFromUri must strip the .nmp2 extension and return the file base name");
    }

    [Fact]
    public void NameFromUri_TempUri_ReturnsGuidSegment()
    {
        string guid = Guid.NewGuid().ToString("N");
        string uri = $"file://{Path.GetTempPath()}scrinia_{guid}.nmp2";

        string name = ScriniaArtifactStore.NameFromUri(uri);

        name.Should().Be($"scrinia_{guid}",
            because: "NameFromUri must handle temp-dir URIs gracefully, returning the file base name");
    }

    // ── SaveIndex (atomic write) ──────────────────────────────────────────────

    [Fact]
    public void SaveIndex_AtomicWrite_IndexRoundTrips()
    {
        using var scope = new TestHelpers.StoreScope();
        var entry = MakeEntry("roundtrip", desc: "Hello, world!");

        ScriniaArtifactStore.SaveIndex([entry]);
        var loaded = ScriniaArtifactStore.LoadIndex();

        loaded.Should().ContainSingle(e =>
            e.Name == "roundtrip" && e.Description == "Hello, world!",
            because: "SaveIndex → LoadIndex must preserve all entry fields");
    }

    [Fact]
    public void SaveIndex_NoTempFileLeft_AfterWrite()
    {
        using var scope = new TestHelpers.StoreScope();
        ScriniaArtifactStore.SaveIndex([MakeEntry("x")]);

        string tmp = Path.Combine(scope.TempDir, "index.json.tmp");
        File.Exists(tmp).Should().BeFalse(
            because: "the temp file used for atomic write must be renamed/removed after success");
    }

    // ── Ephemeral store ─────────────────────────────────────────────────────

    [Fact]
    public void IsEphemeral_TildePrefix_ReturnsTrue()
    {
        ScriniaArtifactStore.IsEphemeral("~scratch").Should().BeTrue();
    }

    [Fact]
    public void IsEphemeral_NoTilde_ReturnsFalse()
    {
        ScriniaArtifactStore.IsEphemeral("regular-name").Should().BeFalse();
    }

    [Fact]
    public void StripEphemeralPrefix_TildePrefix_StripsIt()
    {
        ScriniaArtifactStore.StripEphemeralPrefix("~scratch").Should().Be("scratch");
    }

    [Fact]
    public void EphemeralStore_RememberAndGet_RoundTrips()
    {
        using var scope = new TestHelpers.StoreScope();
        var entry = new EphemeralEntry(
            Name: "test", Artifact: "NMP/2 fake", OriginalBytes: 10,
            ChunkCount: 1, CreatedAt: DateTimeOffset.UtcNow, Description: "test");

        ScriniaArtifactStore.RememberEphemeral("test", entry);
        var retrieved = ScriniaArtifactStore.GetEphemeral("test");

        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("test");
    }

    [Fact]
    public void EphemeralStore_Forget_RemovesEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var entry = new EphemeralEntry(
            Name: "forget", Artifact: "NMP/2 fake", OriginalBytes: 10,
            ChunkCount: 1, CreatedAt: DateTimeOffset.UtcNow, Description: "test");

        ScriniaArtifactStore.RememberEphemeral("forget", entry);
        ScriniaArtifactStore.ForgetEphemeral("forget").Should().BeTrue();
        ScriniaArtifactStore.GetEphemeral("forget").Should().BeNull();
    }

    [Fact]
    public void ListEphemeral_ReturnsEntriesWithBareName()
    {
        using var scope = new TestHelpers.StoreScope();
        var entry = new EphemeralEntry(
            Name: "listed", Artifact: "NMP/2 fake", OriginalBytes: 10,
            ChunkCount: 1, CreatedAt: DateTimeOffset.UtcNow, Description: "test");

        ScriniaArtifactStore.RememberEphemeral("listed", entry);
        var list = ScriniaArtifactStore.ListEphemeral();

        list.Should().ContainSingle();
        list[0].Scope.Should().Be("ephemeral");
        list[0].Entry.Name.Should().Be("listed",
            because: "ephemeral entries use bare name; display layer adds ~ prefix");
    }

    [Fact]
    public async Task ResolveArtifactAsync_Ephemeral_ResolvesFromMemoryStore()
    {
        using var scope = new TestHelpers.StoreScope();
        string artifact = Nmp2ChunkedEncoder.Encode("hello ephemeral");
        var entry = new EphemeralEntry(
            Name: "resolve-eph", Artifact: artifact, OriginalBytes: 15,
            ChunkCount: 1, CreatedAt: DateTimeOffset.UtcNow, Description: "test");

        ScriniaArtifactStore.RememberEphemeral("resolve-eph", entry);
        string resolved = await ScriniaArtifactStore.ResolveArtifactAsync("~resolve-eph");

        resolved.Should().Be(artifact,
            because: "ResolveArtifactAsync must resolve ~name from the ephemeral store");
    }

    [Fact]
    public async Task ResolveArtifactAsync_EphemeralNotFound_Throws()
    {
        using var scope = new TestHelpers.StoreScope();
        Func<Task<string>> act = () => ScriniaArtifactStore.ResolveArtifactAsync("~nonexistent");

        await act.Should().ThrowAsync<FileNotFoundException>(
            because: "a non-existent ephemeral name must throw");
    }

    // ── Multi-term search scoring ────────────────────────────────────────────

    [Fact]
    public void Search_MultiTerm_SumsPerTermScores()
    {
        using var scope = new TestHelpers.StoreScope();
        ScriniaArtifactStore.Upsert(MakeEntry("di-patterns", desc: "dependency injection patterns"));
        ScriniaArtifactStore.Upsert(MakeEntry("audio-editing", desc: "sound editor"));

        var results = ScriniaArtifactStore.Search("DI patterns");

        results.Should().HaveCountGreaterThanOrEqualTo(1);
        // "di-patterns" should score higher because it matches BOTH terms
        var diResult = results.FirstOrDefault(r => r.Item.Entry.Name == "di-patterns");
        var audioResult = results.FirstOrDefault(r => r.Item.Entry.Name == "audio-editing");

        diResult.Should().NotBeNull(because: "di-patterns must match query 'DI patterns'");

        if (audioResult is not null)
        {
            diResult!.Score.Should().BeGreaterThan(audioResult.Score,
                because: "entry matching both terms must score higher than entry matching only one");
        }
    }

    // ── DiscoverTopics ────────────────────────────────────────────────────────

    [Fact]
    public void DiscoverTopics_LocalTopics_Found()
    {
        using var scope = new TestHelpers.StoreScope();
        // Create a local topic directory
        string topicDir = Path.Combine(scope.WorkspaceDir, ".scrinia", "topics", "api");
        Directory.CreateDirectory(topicDir);

        var topics = ScriniaArtifactStore.DiscoverTopics();

        topics.Should().Contain("local-topic:api",
            because: "local topic directories must be discovered");
    }

    // ── NormalizeScopeFilters ────────────────────────────────────────────────

    [Fact]
    public void NormalizeScopeFilters_BareTopicName_ReturnsLocalOnly()
    {
        var result = ScriniaArtifactStore.NormalizeScopeFilters("api");

        result.Should().Equal(["local-topic:api"],
            because: "bare topic names must resolve to local topic only");
    }

    // ── v3 fields ────────────────────────────────────────────────────────────

    [Fact]
    public void ArtifactEntry_V3Fields_DefaultToNull()
    {
        var entry = MakeEntry("test-v3");

        entry.Keywords.Should().BeNull();
        entry.TermFrequencies.Should().BeNull();
        entry.UpdatedAt.Should().BeNull();
        entry.ReviewAfter.Should().BeNull();
        entry.ReviewWhen.Should().BeNull();
    }

    [Fact]
    public void ArtifactEntry_V3Fields_RoundTripThroughIndex()
    {
        using var scope = new TestHelpers.StoreScope();
        var tf = new Dictionary<string, int> { ["auth"] = 5, ["token"] = 3 };
        var entry = new ArtifactEntry(
            Name: "v3-test",
            Uri: "file:///tmp/v3-test.nmp2",
            OriginalBytes: 1024,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: "v3 fields test",
            Keywords: ["auth", "jwt"],
            TermFrequencies: tf,
            UpdatedAt: DateTimeOffset.UtcNow,
            ReviewAfter: DateTimeOffset.UtcNow.AddMonths(3),
            ReviewWhen: "when auth system changes");

        ScriniaArtifactStore.Upsert(entry);
        var loaded = ScriniaArtifactStore.LoadIndex();

        loaded.Should().ContainSingle();
        var e = loaded[0];
        e.Keywords.Should().Equal("auth", "jwt");
        e.TermFrequencies.Should().ContainKey("auth").WhoseValue.Should().Be(5);
        e.TermFrequencies.Should().ContainKey("token").WhoseValue.Should().Be(3);
        e.UpdatedAt.Should().NotBeNull();
        e.ReviewAfter.Should().NotBeNull();
        e.ReviewWhen.Should().Be("when auth system changes");
    }

    [Fact]
    public void LoadIndex_V2Format_NullsForNewFields()
    {
        using var scope = new TestHelpers.StoreScope();
        // Simulate v2 index: no keywords, TF, updatedAt, reviewAfter, reviewWhen
        string v2Json = """
        {
          "v": 2,
          "entries": [
            {
              "name": "old-entry",
              "uri": "file:///tmp/old.nmp2",
              "originalBytes": 512,
              "chunkCount": 1,
              "createdAt": "2026-01-01T00:00:00+00:00",
              "description": "legacy entry"
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(scope.TempDir, "index.json"), v2Json);

        var loaded = ScriniaArtifactStore.LoadIndex();

        loaded.Should().ContainSingle();
        var e = loaded[0];
        e.Name.Should().Be("old-entry");
        e.Keywords.Should().BeNull();
        e.TermFrequencies.Should().BeNull();
        e.UpdatedAt.Should().BeNull();
        e.ReviewAfter.Should().BeNull();
        e.ReviewWhen.Should().BeNull();
    }

    // ── ArchiveVersion ──────────────────────────────────────────────────────

    [Fact]
    public void ArchiveVersion_ExistingFile_CreatesArchive()
    {
        using var scope = new TestHelpers.StoreScope();
        string artifactPath = ScriniaArtifactStore.ArtifactPath("archive-test");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, "NMP/2 test content");

        ScriniaArtifactStore.ArchiveVersion("archive-test");

        string versionsDir = Path.Combine(scope.TempDir, "versions");
        Directory.Exists(versionsDir).Should().BeTrue();
        Directory.GetFiles(versionsDir, "archive-test_*.nmp2").Should().HaveCount(1);
    }

    [Fact]
    public void ArchiveVersion_NoFile_NoOp()
    {
        using var scope = new TestHelpers.StoreScope();

        // Should not throw
        ScriniaArtifactStore.ArchiveVersion("nonexistent");

        string versionsDir = Path.Combine(scope.TempDir, "versions");
        Directory.Exists(versionsDir).Should().BeFalse();
    }

    // ── EphemeralEntry v3 fields ─────────────────────────────────────────────

    [Fact]
    public void EphemeralEntry_V3Fields_PropagateToListEphemeral()
    {
        using var scope = new TestHelpers.StoreScope();
        var tf = new Dictionary<string, int> { ["auth"] = 3 };
        var entry = new EphemeralEntry(
            Name: "eph-v3", Artifact: "NMP/2 fake", OriginalBytes: 10,
            ChunkCount: 1, CreatedAt: DateTimeOffset.UtcNow, Description: "test",
            Keywords: ["auth"], TermFrequencies: tf, UpdatedAt: DateTimeOffset.UtcNow);

        ScriniaArtifactStore.RememberEphemeral("eph-v3", entry);
        var list = ScriniaArtifactStore.ListEphemeral();

        list.Should().ContainSingle();
        list[0].Entry.Keywords.Should().Equal("auth");
        list[0].Entry.TermFrequencies.Should().ContainKey("auth");
        list[0].Entry.UpdatedAt.Should().NotBeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArtifactEntry MakeEntry(
        string name,
        string desc = "test description",
        string[]? tags = null,
        string? contentPreview = null) =>
        new(
            Name: name,
            Uri: $"file:///tmp/{name}.nmp2",
            OriginalBytes: 1024,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: desc,
            Tags: tags,
            ContentPreview: contentPreview);
}
