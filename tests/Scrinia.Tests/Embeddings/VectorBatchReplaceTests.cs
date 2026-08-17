using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// ReplaceMemoryVectorsAsync must be observationally identical to the RemoveAsync-then-Upsert-loop
/// it replaces, while costing one locked file operation instead of one per vector (finding H5).
/// </summary>
public sealed class VectorBatchReplaceTests : IDisposable
{
    private readonly string _dir;

    public VectorBatchReplaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"scrinia_vec_batch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static (int? ChunkIndex, float[] Vector)[] Plan(int count) =>
        Enumerable.Range(1, count).Select(i => ((int?)i, new[] { (float)i, 0f, 0f })).ToArray();

    [Fact]
    public async Task BatchReplace_MatchesTheRemoveThenLoop_ForTheSameInput()
    {
        string loopDir = Path.Combine(_dir, "loop");
        string batchDir = Path.Combine(_dir, "batch");
        Directory.CreateDirectory(loopDir);
        Directory.CreateDirectory(batchDir);

        var plan = Plan(5);

        using (var loop = new VectorStore(loopDir, expectedSignature: null))
        {
            await loop.RemoveAsync("local", "note");
            foreach (var (ci, v) in plan)
                await loop.UpsertAsync("local", "note", ci, v);
        }

        using (var batch = new VectorStore(batchDir, expectedSignature: null))
        {
            await batch.ReplaceMemoryVectorsAsync("local", "note", plan);
        }

        using var loopRead = new VectorStore(loopDir, expectedSignature: null);
        using var batchRead = new VectorStore(batchDir, expectedSignature: null);

        var loopVectors = loopRead.GetVectors("local")
            .OrderBy(v => v.ChunkIndex).Select(v => (v.Name, v.ChunkIndex)).ToArray();
        var batchVectors = batchRead.GetVectors("local")
            .OrderBy(v => v.ChunkIndex).Select(v => (v.Name, v.ChunkIndex)).ToArray();

        batchVectors.Should().BeEquivalentTo(loopVectors);
    }

    [Fact]
    public async Task BatchReplace_ShrinkingAMemory_DropsStaleHigherChunkIndices()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(6));
        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(2));

        store.GetVectors("local").Select(v => v.ChunkIndex)
            .Should().BeEquivalentTo([1, 2],
                because: "replacing a memory's vectors must drop the ones it no longer has — "
                       + "a shrunk memory keeping chunk 3..6 is the bug the atomic replace prevents");
    }

    [Fact]
    public async Task BatchReplace_LeavesOtherMemoriesAlone()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("local", "keep", Plan(2));
        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(3));
        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(1));

        store.GetVectors("local").Where(v => v.Name == "keep")
            .Select(v => v.ChunkIndex).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task BatchReplace_SurvivesAReload()
    {
        using (var store = new VectorStore(_dir, expectedSignature: null))
            await store.ReplaceMemoryVectorsAsync("local", "note", Plan(4));

        using var reloaded = new VectorStore(_dir, expectedSignature: null);
        reloaded.GetVectors("local").Select(v => v.ChunkIndex)
            .Should().BeEquivalentTo([1, 2, 3, 4]);
    }

    [Fact]
    public async Task BatchReplace_ShrinkingAMemory_DropsStaleHigherChunkIndices_OnDiskAfterReload()
    {
        // The in-memory shrink test above only reads the live cache, which would pass even if
        // the delete ops for superseded entries were never written to disk. This closes that
        // gap by forcing a fresh load from the SVF3 op-log.
        using (var store = new VectorStore(_dir, expectedSignature: null))
        {
            await store.ReplaceMemoryVectorsAsync("local", "note", Plan(6));
            await store.ReplaceMemoryVectorsAsync("local", "note", Plan(2));
        }

        using var reloaded = new VectorStore(_dir, expectedSignature: null);
        reloaded.GetVectors("local").Select(v => v.ChunkIndex)
            .Should().BeEquivalentTo([1, 2],
                because: "a reload replays the on-disk op-log, so stale higher chunk indices "
                       + "would resurface here if the batch write only fixed the in-memory copy");
    }

    [Fact]
    public async Task BatchReplace_WithAnEmptyPlan_RemovesEveryVectorForThatMemory()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(3));
        await store.ReplaceMemoryVectorsAsync("local", "note", []);

        store.GetVectors("local").Where(v => v.Name == "note").Should().BeEmpty();
    }
}
