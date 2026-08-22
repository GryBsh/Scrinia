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

        // A non-empty prior state so the compared write goes through the supersede path
        // (P > 0 delete ops), not just an insert into an empty store — an empty starting
        // point can never distinguish "wrote the right number of delete ops" from "wrote
        // none," which is the entire reason this method exists (finding I-1).
        var priorPlan = Plan(7);
        var plan = Plan(5);

        using (var loop = new VectorStore(loopDir, expectedSignature: null))
        {
            foreach (var (ci, v) in priorPlan)
                await loop.UpsertAsync("local", "note", ci, v);

            await loop.RemoveAsync("local", "note");
            foreach (var (ci, v) in plan)
                await loop.UpsertAsync("local", "note", ci, v);
        }

        using (var batch = new VectorStore(batchDir, expectedSignature: null))
        {
            foreach (var (ci, v) in priorPlan)
                await batch.UpsertAsync("local", "note", ci, v);

            await batch.ReplaceMemoryVectorsAsync("local", "note", plan);
        }

        using var loopRead = new VectorStore(loopDir, expectedSignature: null);
        using var batchRead = new VectorStore(batchDir, expectedSignature: null);

        // Compare the full entry — Name, ChunkIndex AND Vector — not just the shape. A
        // (Name, ChunkIndex)-only comparison passes even if the payload is wrong (e.g. a
        // mis-paired chunkIndex/vector zip in the rebuild), as long as the index set matches.
        var loopVectors = loopRead.GetVectors("local")
            .OrderBy(v => v.ChunkIndex).Select(v => (v.Name, v.ChunkIndex, v.Vector)).ToArray();
        var batchVectors = batchRead.GetVectors("local")
            .OrderBy(v => v.ChunkIndex).Select(v => (v.Name, v.ChunkIndex, v.Vector)).ToArray();

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
    public async Task BatchReplace_WhenAtomicRewriteFails_PreservesPreviousDurableSnapshot()
    {
        using (var store = new VectorStore(_dir, expectedSignature: null))
            await store.ReplaceMemoryVectorsAsync("local", "note", Plan(2));

        VectorStore.RewriteFailureInjector = _ => new IOException("injected rewrite failure");
        try
        {
            using var store = new VectorStore(_dir, expectedSignature: null);
            var act = () => store.ReplaceMemoryVectorsAsync("local", "note", Plan(1));

            await act.Should().ThrowAsync<IOException>();
        }
        finally
        {
            VectorStore.RewriteFailureInjector = null;
        }

        using var reloaded = new VectorStore(_dir, expectedSignature: null);
        reloaded.GetVectors("local").Where(v => v.Name == "note")
            .Select(v => v.ChunkIndex).Should().BeEquivalentTo([1, 2]);
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
    public async Task BatchReplace_WithARepeatedChunkIndex_KeepsOnlyTheLastValue_InMemoryAndOnDisk()
    {
        // finding I-3: the on-disk op-log is last-write-wins by (name, chunkIndex) key, matching
        // what a sequential UpsertAsync(1, vA) then UpsertAsync(1, vB) loop converges to. Without
        // de-duping the incoming batch the same way, a batch containing a repeated ChunkIndex
        // would show 2 entries in memory but only 1 after reload — memory and disk disagreeing,
        // which breaks the "observationally identical to the loop" guarantee this method promises.
        var vA = new[] { 1f, 0f, 0f };
        var vB = new[] { 2f, 0f, 0f };
        var batch = new (int? ChunkIndex, float[] Vector)[] { (1, vA), (1, vB) };

        using (var store = new VectorStore(_dir, expectedSignature: null))
        {
            await store.ReplaceMemoryVectorsAsync("local", "note", batch);

            var live = store.GetVectors("local").Where(v => v.Name == "note").ToArray();
            live.Should().ContainSingle();
            live[0].Vector.Should().BeEquivalentTo(vB);
        }

        using var reloaded = new VectorStore(_dir, expectedSignature: null);
        var persisted = reloaded.GetVectors("local").Where(v => v.Name == "note").ToArray();
        persisted.Should().ContainSingle();
        persisted[0].Vector.Should().BeEquivalentTo(vB);
    }

    [Fact]
    public async Task BatchReplace_WithAnEmptyPlan_RemovesEveryVectorForThatMemory()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(3));
        await store.ReplaceMemoryVectorsAsync("local", "note", []);

        store.GetVectors("local").Where(v => v.Name == "note").Should().BeEmpty();
    }

    /// <summary>
    /// Nothing to remove and nothing to add touches the file at all — the RemoveAsync-then-loop
    /// this replaces never did either (RemoveAsync early-returns on nothing-to-remove and zero
    /// Upserts follow), so probing here would newly risk a VectorStoreUnavailableException on a
    /// call that used to be free.
    /// </summary>
    [Fact]
    public async Task BatchReplace_WithNothingToRemoveAndNothingToAdd_TouchesNoFile()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("local", "ghost", []);

        store.GetVectors("local").Should().BeEmpty();
        Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Should().BeEmpty(
            because: "a no-op replace must not create a vector file for a scope that has none");
    }

    /// <summary>
    /// The ephemeral scope is in-memory only: the replace still swaps the scope's vector list, but
    /// writes nothing, exactly as <c>UpsertAsync</c>/<c>RemoveAsync</c> do for that scope.
    /// </summary>
    [Fact]
    public async Task BatchReplace_OnTheEphemeralScope_UpdatesMemoryAndWritesNothing()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("ephemeral", "note", Plan(3));

        store.GetVectors("ephemeral").Should().HaveCount(3);
        Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Should().BeEmpty(
            because: "the ephemeral scope never persists — its vectors die with the instance");
    }

    /// <summary>
    /// Scope-local, not name-local: a memory of the same name in another scope is a different
    /// memory, and the replace's remove-every-matching-Name sweep must not reach across scopes.
    /// <see cref="BatchReplace_LeavesOtherMemoriesAlone"/> covers the same-scope half.
    /// </summary>
    [Fact]
    public async Task BatchReplace_LeavesTheSameNameInAnotherScopeAlone()
    {
        using var store = new VectorStore(_dir, expectedSignature: null);

        await store.ReplaceMemoryVectorsAsync("local", "note", Plan(3));
        await store.ReplaceMemoryVectorsAsync("local:alpha", "note", Plan(4));

        await store.ReplaceMemoryVectorsAsync("local", "note", []);

        store.GetVectors("local").Should().BeEmpty();
        store.GetVectors("local:alpha").Where(v => v.Name == "note").Should().HaveCount(4);

        using var reload = new VectorStore(_dir, expectedSignature: null);
        reload.GetVectors("local:alpha").Where(v => v.Name == "note").Should().HaveCount(4,
            because: "the other scope's file must be untouched on disk too, not merely in memory");
    }
}
