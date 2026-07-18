using FluentAssertions;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Guards against a crash/power-loss mid-append leaving a torn trailing record in
/// vectors.bin. The loader must tolerate the torn tail and return the intact prefix
/// instead of discarding the whole scope via the whole-file catch in LoadFromDisk.
/// </summary>
public class VectorStoreTornAppendTests : IDisposable
{
    private readonly string _tempDir;

    public VectorStoreTornAppendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_tornappend_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task TornTrailingRecord_SurvivesWithIntactPrefix()
    {
        using (var store = new VectorStore(_tempDir))
        {
            await store.UpsertAsync("local", "first", null, [0.1f, 0.2f, 0.3f]);
            await store.UpsertAsync("local", "second", null, [0.4f, 0.5f, 0.6f]);
            await store.UpsertAsync("local", "third", null, [0.7f, 0.8f, 0.9f]);
        }

        string path = Path.Combine(_tempDir, "local", "vectors.bin");
        File.Exists(path).Should().BeTrue();

        // Simulate a crash/power-loss mid-append: truncate a few bytes off the tail,
        // landing inside the last record's vector payload (well short of a full record).
        long originalLength = new FileInfo(path).Length;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            fs.SetLength(originalLength - 3);

        using var reloaded = new VectorStore(_tempDir);
        var vectors = reloaded.GetVectors("local");

        // Must NOT be empty — the torn tail should be skipped, not the whole scope.
        vectors.Should().NotBeEmpty();
        vectors.Should().HaveCount(2);
        vectors.Should().Contain(v => v.Name == "first");
        vectors.Should().Contain(v => v.Name == "second");
        vectors.Should().NotContain(v => v.Name == "third");
    }

    [Fact]
    public async Task HealthyFile_AllRecordsStillLoad()
    {
        using (var store = new VectorStore(_tempDir))
        {
            await store.UpsertAsync("local", "first", null, [0.1f, 0.2f, 0.3f]);
            await store.UpsertAsync("local", "second", null, [0.4f, 0.5f, 0.6f]);
            await store.UpsertAsync("local", "third", null, [0.7f, 0.8f, 0.9f]);
        }

        using var reloaded = new VectorStore(_tempDir);
        var vectors = reloaded.GetVectors("local");

        vectors.Should().HaveCount(3);
    }
}
