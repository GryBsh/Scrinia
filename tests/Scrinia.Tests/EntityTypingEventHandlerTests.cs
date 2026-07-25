using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public class EntityTypingEventHandlerTests
{
    // Mirrors AttributeMigrationTests' TempWorkspace (temp-dir + FileMemoryStore construction/disposal),
    // exposing a ready-made Store property since these tests always need one.
    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _tempDir;

        public TempWorkspace()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-entitytyping-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static ArtifactEntry Mentioning(string name, params string[] mentions) =>
        new(name, "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["mention"] = mentions });

    [Fact]
    public async Task Promotes_MentionToTypedKey_ViaNearestEntity()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        // an existing typed entity to link against
        store.Upsert(new ArtifactEntry("known", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["thing"] = ["susie"] }), "local:findings");
        store.Upsert(Mentioning("note", "seraphim"), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f], ["susie"] = [0f, 0f, 1f] };
        var sink = new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3));

        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        var reloaded = store.LoadIndex("local:findings").Single(e => e.Name == "note");
        reloaded.Attributes!.TryGetValue("mention", out var m);
        (m ?? []).Should().NotContain("seraphim");
        reloaded.Attributes!["thing"].Should().Contain("seraphim");
    }

    [Fact]
    public async Task EmbedderAbsent_NoOp_MentionRemains()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(Mentioning("note", "seraphim"), "local:findings");

        var sink = new EntityTypingEventHandler(new UnavailableEmbeddingProvider());
        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        store.LoadIndex("local:findings").Single(e => e.Name == "note")
            .Attributes!["mention"].Should().Contain("seraphim");
    }

    [Fact]
    public async Task ConcurrentEdit_RemovedMention_NotClobbered()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(Mentioning("note", "seraphim"), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f] };
        foreach (var s in EntityTypeClassifier.SeedVocab["thing"]) map[s] = [0f, 0f, 1f];
        var sink = new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3));

        // Simulate a user edit between the sink's read and its write: replace the whole entry so
        // "seraphim" is no longer under mention (content changed to something without it).
        store.Upsert(new ArtifactEntry("note", "uri", 2, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["mention"] = ["different"] }), "local:findings");

        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        // The newer write is intact; no stale "seraphim" resurrected.
        var reloaded = store.LoadIndex("local:findings").Single(e => e.Name == "note");
        reloaded.Attributes!["mention"].Should().Contain("different");
        reloaded.Attributes!.Should().NotContainKey("thing");
    }
}

internal sealed class UnavailableEmbeddingProvider : Scrinia.Core.Embeddings.IEmbeddingProvider
{
    public bool IsAvailable => false;
    public int Dimensions => 3;
    public string Signature => "stub:unavailable";
    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
    public void Dispose() { }
}
