using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

[Collection(BackgroundWorkCollection.Name)]
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

    /// <summary>
    /// Guards the switch from a bespoke <c>store.ListScoped(null)</c> scan to
    /// <c>store.GetEntityFactStore().Entities(key)</c> for gathering the existing typed vocabulary
    /// (review follow-up: the old scan re-walked the whole store on every write event; the fact
    /// store is cached and generation-invalidated). The typed anchor lives in a DIFFERENT scope
    /// (<c>local:other</c>) than the memory being typed (<c>local:findings</c>) -- linking still
    /// succeeds only if the vocab genuinely comes from the fact store's merged cross-scope view.
    /// </summary>
    [Fact]
    public async Task Promotes_MentionToTypedKey_ViaFactStore_AcrossScopes()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(new ArtifactEntry("known", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["thing"] = ["susie"] }), "local:other");
        store.Upsert(Mentioning("note", "seraphim"), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f], ["susie"] = [0f, 0f, 1f] };
        var sink = new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3));

        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        var reloaded = store.LoadIndex("local:findings").Single(e => e.Name == "note");
        reloaded.Attributes!.TryGetValue("mention", out var m);
        (m ?? []).Should().NotContain("seraphim");
        reloaded.Attributes!["thing"].Should().Contain("seraphim",
            "the existing typed vocabulary must come from the fact store's cross-scope merged " +
            "view (GetEntityFactStore), not a same-scope-only read");
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

    /// <summary>
    /// NOTE ON NAMING: this is the "nothing to do" path, not a race. The concurrent write lands
    /// BEFORE <c>OnStoredAsync</c> is even called, so the sink's Phase-1 read already sees
    /// mention=["different"] (no "seraphim") and "different" embeds to a zero vector -> the
    /// classifier returns null -> <c>moves</c> is empty -> <c>PromoteAsync</c> short-circuits at
    /// <c>if (moves.Count == 0) return;</c> before <c>TryUpdateEntry</c> is ever called. It's a
    /// legitimate no-op case worth covering, but it does NOT exercise the under-lock re-check —
    /// see <see cref="ConcurrentEdit_DuringClassification_UnderLockRecheckPreventsClobber"/> for
    /// that seam.
    /// </summary>
    [Fact]
    public async Task ConcurrentEditBeforeSink_MentionAlreadyGone_NoOpNoWrite()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(Mentioning("note", "seraphim"), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f] };
        foreach (var s in EntityTypeClassifier.SeedVocab["thing"]) map[s] = [0f, 0f, 1f];
        var sink = new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3));

        // Edit lands entirely before the sink runs: replace the whole entry so "seraphim" is no
        // longer under mention (content changed to something without it).
        store.Upsert(new ArtifactEntry("note", "uri", 2, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["mention"] = ["different"] }), "local:findings");

        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        // The newer write is intact; no stale "seraphim" resurrected.
        var reloaded = store.LoadIndex("local:findings").Single(e => e.Name == "note");
        reloaded.Attributes!["mention"].Should().Contain("different");
        reloaded.Attributes!.Should().NotContainKey("thing");
    }

    /// <summary>
    /// This is the actual Piece-2 concurrency seam: a write that lands AFTER the sink's Phase-1
    /// read but BEFORE its Phase-3 <c>TryUpdateEntry</c> apply — while Phase-2 classification is
    /// in flight. <see cref="InterleavingEmbeddingProvider"/> uses the embedder itself as the
    /// interleave hook: the FIRST time it is asked to embed "jolene" (the mention value being
    /// classified), it performs the concurrent write as a side effect, then returns the vector
    /// that makes "jolene" classify to "person" via nearest-existing-entity. So <c>moves</c> ends
    /// up non-empty (jolene -> person) by the time Phase-3 runs, but the concurrent write already
    /// emptied "note"'s mention list. Only the under-lock re-check in the mutate closure (which
    /// reads <c>current.Attributes</c> fresh, not the Phase-1 snapshot) can stop the clobber.
    /// </summary>
    [Fact]
    public async Task ConcurrentEdit_DuringClassification_UnderLockRecheckPreventsClobber()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        // An existing typed entity so "jolene" has somewhere to link to (nearest-entity -> person).
        store.Upsert(new ArtifactEntry("known", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["person"] = ["someone"] }), "local:findings");
        store.Upsert(Mentioning("note", "jolene"), "local:findings");

        var map = new Dictionary<string, float[]> { ["jolene"] = [0f, 0f, 1f], ["someone"] = [0f, 0f, 1f] };
        var provider = new InterleavingEmbeddingProvider(map, 3, triggerText: "jolene", onTrigger: () =>
        {
            // Fires when the classifier embeds "jolene" (Phase 2, mid-classification, no lock
            // held) -- i.e. strictly after the sink's Phase-1 read of mention=["jolene"] and
            // strictly before its Phase-3 TryUpdateEntry call. Simulate a concurrent editor
            // replacing the entry so "jolene" is no longer under mention.
            store.Upsert(new ArtifactEntry("note", "uri", 2, 1, DateTimeOffset.UtcNow,
                Attributes: new(StringComparer.OrdinalIgnoreCase) { ["mention"] = ["someone-else"] }),
                "local:findings");
        });
        var sink = new EntityTypingEventHandler(provider);

        await sink.OnStoredAsync("/findings/note", ["We saw Jolene."], store, default);

        var reloaded = store.LoadIndex("local:findings").Single(e => e.Name == "note");
        reloaded.Attributes!.Should().NotContainKey("person",
            "jolene was already gone from mention by the time Phase 3 ran -- the under-lock " +
            "re-check must not resurrect it under the typed key");
        reloaded.Attributes!["mention"].Should().Contain("someone-else",
            "the concurrent write's own content must survive, not be clobbered by a stale promotion");
    }

    /// <summary>
    /// The sink's OWN entry lookup — upstream of <c>TryUpdateEntry</c> — matched sanitized names
    /// with an ordinal <c>==</c>, so a memory stored as "Note" and written under the qualified name
    /// "/findings/note" was gated out at the very first read: the sink returned before ever calling
    /// <c>TryUpdateEntry</c>, and the promotion silently did not happen. Fixing the store alone
    /// would not have fixed this; the gate is above it. The sink also hands <c>TryUpdateEntry</c>
    /// the resolved entry's OWN casing (<c>entry.Name</c>, "Note"), not the event's ("note") —
    /// otherwise this would still fail on a case-sensitive filesystem, where
    /// <c>FileMemoryStore.TryUpdateEntry</c>'s own <c>File.Exists</c> gate is case-sensitive.
    /// </summary>
    [Fact]
    public async Task Promotes_WhenTheStoredNameDiffersInCasingFromTheEvent()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(new ArtifactEntry("known", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["thing"] = ["susie"] }), "local:findings");
        store.Upsert(Mentioning("Note", "seraphim"), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f], ["susie"] = [0f, 0f, 1f] };
        var sink = new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3));

        // Lowercase subject; the entry is stored as "Note".
        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        var reloaded = store.LoadIndex("local:findings").Single(e => e.Name == "Note");
        (reloaded.Attributes!.TryGetValue("mention", out var m) ? m : []).Should().NotContain("seraphim",
            because: "the promotion must run even though the event's subject casing differs from " +
                     "the stored entry's");
        reloaded.Attributes!["thing"].Should().Contain("seraphim");
    }

    /// <summary>
    /// Pins that <c>OnStoredAsync</c> actually widens through <see cref="CoreSinkStore.Require"/>
    /// rather than, say, a bare <c>(IMemoryStore)store</c> cast that would happen to compile and
    /// throw too. A raw cast's <see cref="InvalidCastException"/> would not name
    /// <see cref="EntityTypingEventHandler"/> anywhere in its message, so asserting on that name
    /// distinguishes the two: this fails under the raw-cast mutation and passes only when
    /// <c>Require</c> is the thing that ran.
    /// </summary>
    [Fact]
    public async Task OnStoredAsync_ThrowsNamingTheHandler_WhenStoreIsNotAFullMemoryStore()
    {
        var sink = new EntityTypingEventHandler(new UnavailableEmbeddingProvider());
        var narrowStore = new NarrowPluginStore();

        var act = async () => await sink.OnStoredAsync(
            "/test/narrow", ["content"], narrowStore, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*EntityTypingEventHandler is a Core-internal event handler*",
                because: "this is CoreSinkStore.Require's own message, naming the handler -- a raw " +
                         "cast mutation would throw InvalidCastException with no such text");
    }
}

/// <summary>An <see cref="IPluginStore"/> double that is deliberately NOT an <see cref="IMemoryStore"/>,
/// used to drive <see cref="CoreSinkStore.Require"/>'s throw path from a real handler's own
/// <c>OnStoredAsync</c> rather than by calling <c>Require</c> directly.</summary>
internal sealed class NarrowPluginStore : IPluginStore
{
    public (string Scope, string Subject) ParseQualifiedName(string name) => ("local", name);
    public bool IsEphemeral(string name) => false;
    public Task<string> ResolveArtifactAsync(string n, CancellationToken ct = default) => Task.FromResult("");
    public Task<string> ReadArtifactAsync(string s, string sc, CancellationToken ct = default) => Task.FromResult("");
}

internal sealed class UnavailableEmbeddingProvider : Scrinia.Core.Embeddings.IEmbeddingProvider
{
    public bool IsAvailable => false;
    public int Dimensions => 3;
    public string Signature => "stub:unavailable";
    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
    public void Dispose() { }
}

/// <summary>
/// Deterministic stub that fires a caller-provided side effect the FIRST time it is asked to embed
/// a specific trigger text, then returns that text's mapped vector as normal. Used to land a
/// concurrent store write precisely between an <see cref="EntityTypingEventHandler"/>'s Phase-1
/// read and its Phase-3 <c>TryUpdateEntry</c> apply, since Phase-2 classification is the only
/// point where the sink awaits without holding the store's per-scope write lock.
/// </summary>
internal sealed class InterleavingEmbeddingProvider : Scrinia.Core.Embeddings.IEmbeddingProvider
{
    private readonly Dictionary<string, float[]> _map;
    private readonly int _dim;
    private readonly string _triggerText;
    private readonly Action _onTrigger;
    private bool _triggered;

    public InterleavingEmbeddingProvider(Dictionary<string, float[]> map, int dim, string triggerText, Action onTrigger)
    {
        _map = map;
        _dim = dim;
        _triggerText = triggerText;
        _onTrigger = onTrigger;
    }

    public bool IsAvailable => true;
    public int Dimensions => _dim;
    public string Signature => "stub:interleave";

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (!_triggered && string.Equals(text, _triggerText, StringComparison.OrdinalIgnoreCase))
        {
            _triggered = true;
            _onTrigger();
        }
        return Task.FromResult<float[]?>(_map.TryGetValue(text, out var v) ? v : new float[_dim]);
    }

    public void Dispose() { }
}
