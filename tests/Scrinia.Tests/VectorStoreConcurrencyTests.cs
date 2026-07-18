using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests;

public class VectorStoreConcurrencyTests
{
    [Fact]
    public async Task GetVectors_WhileUpserting_DoesNotThrowOrTear()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        var store = new VectorStore(dir);
        const string scope = "ephemeral"; // ephemeral avoids disk persistence noise
        for (int i = 0; i < 50; i++)
            await store.UpsertAsync(scope, $"m{i}", null, new float[] { i, i, i });

        var writer = Task.Run(async () =>
        {
            for (int i = 50; i < 500; i++)
                await store.UpsertAsync(scope, $"m{i}", null, new float[] { i, i, i });
        });

        var ex = await Record.ExceptionAsync(async () =>
        {
            for (int r = 0; r < 500; r++)
            {
                foreach (var v in store.GetVectors(scope)) { _ = v.Vector.Length; }
                await Task.Yield();
            }
        });
        await writer;

        Assert.Null(ex); // BUG: throws InvalidOperationException (Collection was modified)
    }

    [Fact]
    public async Task GetVectors_SnapshotIsStable_AfterSubsequentUpsert()
    {
        // Proves copy-on-write semantics: a previously-captured GetVectors() result must
        // remain exactly as it was, even after a later UpsertAsync mutates the scope. If
        // VectorStore ever went back to in-place mutation, this snapshot would grow/change
        // out from under the caller.
        string dir = Directory.CreateTempSubdirectory().FullName;
        var store = new VectorStore(dir);
        const string scope = "ephemeral";

        await store.UpsertAsync(scope, "a", null, new float[] { 1, 1, 1 });
        await store.UpsertAsync(scope, "b", null, new float[] { 2, 2, 2 });

        var snapshot = store.GetVectors(scope);
        Assert.Equal(2, snapshot.Count);

        await store.UpsertAsync(scope, "c", null, new float[] { 3, 3, 3 });

        // Old snapshot is untouched — still exactly the 2 entries it had when captured.
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, v => v.Name == "a");
        Assert.Contains(snapshot, v => v.Name == "b");
        Assert.DoesNotContain(snapshot, v => v.Name == "c");

        // A fresh call reflects the new entry.
        var fresh = store.GetVectors(scope);
        Assert.Equal(3, fresh.Count);
        Assert.Contains(fresh, v => v.Name == "c");
    }
}
