using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public class LazyStoreCreationTests : IDisposable
{
    private readonly string _tempDir;

    public LazyStoreCreationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_lazy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string ScriniaDir => Path.Combine(_tempDir, ".scrinia");

    [Fact]
    public void Constructing_TheStore_TouchesNoDisk()
    {
        _ = new FileMemoryStore(_tempDir);

        Assert.False(Directory.Exists(ScriniaDir));
    }

    [Fact]
    public void LoadIndex_OnAbsentStore_ReturnsEmptyAndCreatesNothing()
    {
        var store = new FileMemoryStore(_tempDir);

        var entries = store.LoadIndex();

        Assert.Empty(entries);
        Assert.False(Directory.Exists(ScriniaDir));
    }

    [Fact]
    public void LoadIndex_OnAbsentTopicScope_ReturnsEmptyAndCreatesNothing()
    {
        var store = new FileMemoryStore(_tempDir);

        var entries = store.LoadIndex("local:findings");

        Assert.Empty(entries);
        Assert.False(Directory.Exists(ScriniaDir));
    }

    [Fact]
    public void FirstWrite_MaterializesTheStoreAndTheGitIgnore()
    {
        var store = new FileMemoryStore(_tempDir);

        store.Upsert(new ArtifactEntry("first", "file://first", 0, 0, DateTimeOffset.UtcNow));

        Assert.True(Directory.Exists(ScriniaDir));
        Assert.True(File.Exists(Path.Combine(ScriniaDir, ".gitignore")));
        Assert.True(Directory.Exists(Path.Combine(ScriniaDir, "memories")));
    }

    [Fact]
    public void WrittenEntry_IsReadBack()
    {
        var store = new FileMemoryStore(_tempDir);
        store.Upsert(new ArtifactEntry("first", "file://first", 0, 0, DateTimeOffset.UtcNow));

        var entries = store.LoadIndex();

        Assert.Contains(entries, e => e.Name == "first");
    }

    [Fact]
    public void GeneratedGitIgnore_CoversSuffixedLocksAndVectorIndexes()
    {
        var store = new FileMemoryStore(_tempDir);
        store.Upsert(new ArtifactEntry("first", "file://first", 0, 0, DateTimeOffset.UtcNow));

        string[] lines = File.ReadAllLines(Path.Combine(ScriniaDir, ".gitignore"));

        Assert.Contains("**/*.lock", lines);
        Assert.Contains("embeddings/", lines);
        Assert.DoesNotContain("**/.lock", lines);
    }
}
