using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

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
        IMemoryEventSink sink = new CompositeEventSink([
            new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3)),
            new MaintenanceEventSink(),
        ]);

        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        store.LoadIndex("local:findings").Single(e => e.Name == "note")
            .Attributes!["thing"].Should().Contain("seraphim");
    }
}
