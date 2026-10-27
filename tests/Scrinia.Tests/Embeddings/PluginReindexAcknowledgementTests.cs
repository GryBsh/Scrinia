using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests.Embeddings;

public sealed class PluginReindexAcknowledgementTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scrinia-reindex-ack-").FullName;

    [Theory]
    [InlineData("{\"ok\":false,\"error\":\"write failed\"}")]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task Reindex_FailedOrMissingAcknowledgements_AreNeverEmbedded(string? response)
    {
        var store = SeedMemories(6);
        var caller = new Caller(_ => Task.FromResult(response));
        await using var host = new McpPluginHost(caller, ["index"]);

        var result = await EmbeddingReindexer.ReindexViaCommandAsync(
            store, host, NullLogger.Instance, null, default);

        Assert.Equal(new EmbeddingReindexer.Result(6, 0, 0, 6), result);
        Assert.Equal(4, caller.Calls);
    }

    [Fact]
    public async Task Reindex_ExplicitAcknowledgements_AreEmbedded()
    {
        var store = SeedMemories(2);
        var caller = new Caller(_ => Task.FromResult<string?>("{\"ok\":true,\"error\":null}"));
        await using var host = new McpPluginHost(caller, ["index"]);

        var result = await EmbeddingReindexer.ReindexViaCommandAsync(
            store, host, NullLogger.Instance, null, default);

        Assert.Equal(new EmbeddingReindexer.Result(2, 2, 0, 0), result);
        Assert.Equal(2, caller.Calls);
    }

    [Fact]
    public async Task Reindex_WithoutIndexCapability_IsFailedWithoutDispatch()
    {
        var store = SeedMemories(2);
        var caller = new Caller(_ => throw new InvalidOperationException("must not dispatch"));
        await using var host = new McpPluginHost(caller, ["remove"]);

        var result = await EmbeddingReindexer.ReindexViaCommandAsync(
            store, host, NullLogger.Instance, null, default);

        Assert.Equal(new EmbeddingReindexer.Result(2, 0, 0, 2), result);
        Assert.Equal(0, caller.Calls);
    }

    [Fact]
    public async Task Reindex_CancellationDuringIndex_Propagates()
    {
        var store = SeedMemories(1);
        using var cancellation = new CancellationTokenSource();
        var caller = new Caller(ct =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<string?>(ct);
        });
        await using var host = new McpPluginHost(caller, ["index"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EmbeddingReindexer.ReindexViaCommandAsync(
                store, host, NullLogger.Instance, null, cancellation.Token));
    }

    [Fact]
    public async Task ForcedReindex_CancellationDuringEmbedding_PropagatesThroughComposition()
    {
        using var isolation = new TestHelpers.StoreScope();
        WorkspaceSetup.Configure(_root);
        MemoryStoreContext.Current = SeedMemories(1);
        using var cancellation = new CancellationTokenSource();
        using var provider = new CancelingProvider(cancellation);
        using var providerOverride = EmbeddingsHost.OverrideEmbeddingProviderForTest(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EmbeddingsHost.ForceReindexAsync(_root, cancellation.Token));
    }

    private FileMemoryStore SeedMemories(int count)
    {
        var store = new FileMemoryStore(_root);
        for (int index = 0; index < count; index++)
        {
            string name = $"memory{index}";
            store.Upsert(new ArtifactEntry(MemoryAddress.Parse($"/{name}"), $"artifacts/local/{name}.nmp2", 10, 1,
                DateTimeOffset.UtcNow), "local");
            string path = store.ArtifactPath(name, "local");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Nmp2ChunkedEncoder.Encode("memory content"));
        }
        return store;
    }

    private sealed class Caller(Func<CancellationToken, Task<string?>> invoke) : McpPluginHost.IPluginToolCaller
    {
        public int Calls { get; private set; }

        public Task<string?> CallToolAsync(string tool, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken ct)
        {
            Calls++;
            return invoke(ct);
        }
    }

    private sealed class CancelingProvider(CancellationTokenSource cancellation) : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:canceling";

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        {
            cancellation.Cancel();
            return Task.FromCanceled<float[]?>(ct);
        }

        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            cancellation.Cancel();
            return Task.FromCanceled<float[][]?>(ct);
        }

        public void Dispose() { }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
