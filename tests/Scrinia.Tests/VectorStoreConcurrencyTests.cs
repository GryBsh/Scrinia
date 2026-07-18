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
}
