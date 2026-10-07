using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Commands;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

[Collection(BackgroundWorkCollection.Name)]
public class EntityTypingWiringTests
{
    // Mirrors EntityTypingEventHandlerTests' TempWorkspace (temp-dir + FileMemoryStore
    // construction/disposal).
    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _tempDir;

        public TempWorkspace()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-typingwiring-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
            Store = new FileMemoryStore(_tempDir);
        }

        public FileMemoryStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CompositeSink_Containing_TypingHandler_PromotesEndToEnd()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(new ArtifactEntry("known", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["thing"] = ["susie"] }), "local:findings");
        store.Upsert(new ArtifactEntry("note", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["mention"] = ["seraphim"] }), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f], ["susie"] = [0f, 0f, 1f] };
        CompositeEventSink sink = new CompositeEventSink([
            new MaintenanceEventSink(),
            new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3)),
        ]);

        await sink.OnStoredAsync(MemoryAddress.Parse("/findings/note"), ["We saw Seraphim."], store, default);

        store.LoadIndex("local:findings").Single(e => e.Name == "note")
            .Attributes!["thing"].Should().Contain("seraphim");
    }

    [Theory]
    [InlineData("HTTP embeddings")]
    [InlineData("built-in embeddings")]
    public async Task WorkspaceSetupEmbeddingInstallations_PreserveOrderAndFailureIsolation(
        string installationSite)
    {
        var prior = MemoryEventSinkContext.Default;
        try
        {
            using var tmp = new TempWorkspace();
            var order = new List<string>();
            var downstream = new RecordingSink("downstream", order);

EmbeddingsHost.InstallEmbeddingEventSinks(
                downstream,
                new RecordingSink("entity-typing", order));

            MemoryEventSinkContext.Default.Should().BeOfType<CompositeEventSink>(because: installationSite);
            await MemoryEventSinkContext.Default!.OnStoredAsync(
                MemoryAddress.Parse("/findings/wiring"), ["content"], tmp.Store, CancellationToken.None);

            order.Should().Equal(["entity-typing", "downstream"],
                because: $"the {installationSite} pipeline must keep maintenance before typing and "
                         + "continue to the downstream handler");

            order.Clear();
            EmbeddingsHost.InstallEmbeddingEventSinks(downstream, new ThrowingSink());
            await MemoryEventSinkContext.Default!.OnStoredAsync(
                MemoryAddress.Parse("/findings/wiring"), ["content"], tmp.Store, CancellationToken.None);

            order.Should().Equal(["downstream"],
                because: $"a failing entity-typing sink in the {installationSite} pipeline must "
                         + "not isolate unrelated downstream handlers");
        }
        finally
        {
            MemoryEventSinkContext.Default = prior;
        }
    }

    private sealed class RecordingSink(string name, List<string> order) : IMemoryEventSink
    {
        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
        {
            order.Add(name);
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class ThrowingSink : IMemoryEventSink
    {
        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
            => throw new InvalidOperationException("entity typing failed");

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
            => throw new InvalidOperationException("entity typing failed");

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
            => throw new InvalidOperationException("entity typing failed");
    }
}
