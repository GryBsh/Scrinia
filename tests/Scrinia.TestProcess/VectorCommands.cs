using Scrinia.Core.Embeddings;

namespace Scrinia.TestProcess;

internal static class VectorCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "interleave") return 1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var store = new VectorStore(args[1], "test");
        await store.ReplaceMemoryVectorsAsync("local", "alpha", [(1, new float[] { 1, 0, 0 })], timeout.Token).ConfigureAwait(false);
        await TestProcessProtocol.WriteEventAsync("ready").ConfigureAwait(false);
        await TestProcessProtocol.WaitForContinueAsync(timeout.Token).ConfigureAwait(false);
        await TestProcessProtocol.WriteEventAsync("entered").ConfigureAwait(false);
        await store.ReplaceMemoryVectorsAsync("local", "alpha", [(1, new float[] { 9, 0, 0 })], timeout.Token).ConfigureAwait(false);
        await TestProcessProtocol.WriteEventAsync("completed").ConfigureAwait(false);
        return 0;
    }
}
