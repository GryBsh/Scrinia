using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

public class VectorStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VectorStore _store;

    public VectorStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_vectest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new VectorStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpsertAndGet_PersistsVector()
    {
        float[] vec = [0.1f, 0.2f, 0.3f];
        await _store.UpsertAsync("local", "test-memory", null, vec);

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Name.Should().Be("test-memory");
        vectors[0].ChunkIndex.Should().BeNull();
        vectors[0].Vector.Should().BeEquivalentTo(vec);
    }

    [Fact]
    public async Task Upsert_OverwritesExisting()
    {
        float[] vec1 = [0.1f, 0.2f, 0.3f];
        float[] vec2 = [0.4f, 0.5f, 0.6f];

        await _store.UpsertAsync("local", "test", null, vec1);
        await _store.UpsertAsync("local", "test", null, vec2);

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Vector.Should().BeEquivalentTo(vec2);
    }

    [Fact]
    public async Task Upsert_ChunkVectors_StoredSeparately()
    {
        float[] entryVec = [0.1f, 0.2f, 0.3f];
        float[] chunk1Vec = [0.4f, 0.5f, 0.6f];
        float[] chunk2Vec = [0.7f, 0.8f, 0.9f];

        await _store.UpsertAsync("local", "multi", null, entryVec);
        await _store.UpsertAsync("local", "multi", 1, chunk1Vec);
        await _store.UpsertAsync("local", "multi", 2, chunk2Vec);

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(3);
    }

    [Fact]
    public async Task Remove_DeletesAllVectorsForName()
    {
        await _store.UpsertAsync("local", "rem", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("local", "rem", 1, [0.3f, 0.4f]);
        await _store.UpsertAsync("local", "keep", null, [0.5f, 0.6f]);

        await _store.RemoveAsync("local", "rem");

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Name.Should().Be("keep");
    }

    [Fact]
    public async Task Persistence_SurvivesNewInstance()
    {
        float[] vec = [0.1f, 0.2f, 0.3f, 0.4f];
        await _store.UpsertAsync("local", "persist", null, vec);

        using var store2 = new VectorStore(_tempDir);
        var vectors = store2.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Name.Should().Be("persist");
        vectors[0].Vector.Should().BeEquivalentTo(vec);
    }

    [Fact]
    public async Task EphemeralScope_NotPersistedToDisk()
    {
        await _store.UpsertAsync("ephemeral", "temp", null, [0.1f, 0.2f]);

        var vectors = _store.GetVectors("ephemeral");
        vectors.Should().HaveCount(1);

        using var store2 = new VectorStore(_tempDir);
        var vectors2 = store2.GetVectors("ephemeral");
        vectors2.Should().BeEmpty();
    }

    [Fact]
    public async Task TotalVectorCount_SumsAllScopes()
    {
        await _store.UpsertAsync("local", "a", null, [0.1f]);
        await _store.UpsertAsync("local-topic:api", "b", null, [0.2f]);
        await _store.UpsertAsync("ephemeral", "c", null, [0.3f]);

        _store.Count().Should().Be(3);
    }

    [Fact]
    public void GetVectors_EmptyScope_ReturnsEmpty()
    {
        _store.GetVectors("nonexistent").Should().BeEmpty();
    }

    // -- SVF2 append-only format tests --

    [Fact]
    public async Task Svf2_AppendOnly_UpsertAppends()
    {
        await _store.UpsertAsync("local", "first", null, [0.1f, 0.2f, 0.3f]);
        await _store.UpsertAsync("local", "second", null, [0.4f, 0.5f, 0.6f]);

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(2);
    }

    [Fact]
    public async Task Svf2_SurvivesNewInstance()
    {
        await _store.UpsertAsync("local", "svf2-persist", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("local", "svf2-persist2", null, [0.3f, 0.4f]);

        using var store2 = new VectorStore(_tempDir);
        var vectors = store2.GetVectors("local");
        vectors.Should().HaveCount(2);
        vectors.Should().Contain(v => v.Name == "svf2-persist");
        vectors.Should().Contain(v => v.Name == "svf2-persist2");
    }

    [Fact]
    public async Task Svf2_UpsertExisting_DeletesAndAdds()
    {
        await _store.UpsertAsync("local", "update-me", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("local", "update-me", null, [0.3f, 0.4f]);

        using var store2 = new VectorStore(_tempDir);
        var vectors = store2.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Vector.Should().BeEquivalentTo(new[] { 0.3f, 0.4f });
    }

    [Fact]
    public async Task Svf2_Remove_AppendsDeleteOp()
    {
        await _store.UpsertAsync("local", "del-me", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("local", "keep", null, [0.3f, 0.4f]);

        await _store.RemoveAsync("local", "del-me");

        using var store2 = new VectorStore(_tempDir);
        var vectors = store2.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Name.Should().Be("keep");
    }

    // ── Signature / migration tests ─────────────────────────────────────────────

    [Fact]
    public async Task Svf3_PersistsSignatureAcrossSessions()
    {
        using var sigStore = new VectorStore(_tempDir, expectedSignature: "ollama:nomic-embed-text");
        await sigStore.UpsertAsync("local", "x", null, [0.1f, 0.2f, 0.3f]);

        using var reopen = new VectorStore(_tempDir, expectedSignature: "ollama:nomic-embed-text");
        var vectors = reopen.GetVectors("local");
        vectors.Should().HaveCount(1);
        reopen.HasStaleQuarantines.Should().BeFalse();
    }

    [Fact]
    public async Task Svf3_MismatchedSignature_QuarantinesAndStartsFresh()
    {
        // Write vectors as ollama
        using (var oldStore = new VectorStore(_tempDir, expectedSignature: "ollama:nomic-embed-text"))
        {
            await oldStore.UpsertAsync("local", "x", null, [0.1f, 0.2f, 0.3f]);
        }

        // Reopen as openai — should quarantine the file
        using var newStore = new VectorStore(_tempDir, expectedSignature: "openai:text-embedding-3-small");
        var vectors = newStore.GetVectors("local");

        vectors.Should().BeEmpty("signature mismatch should produce an empty store");
        newStore.HasStaleQuarantines.Should().BeTrue();
        newStore.StaleQuarantineScopes.Should().Contain("local");

        // Quarantined file should still exist on disk under a .stale name (recoverable).
        string localDir = Path.Combine(_tempDir, "local");
        Directory.GetFiles(localDir, "vectors.bin.stale-*").Should().NotBeEmpty();
        File.Exists(Path.Combine(localDir, "vectors.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Svf3_ChunkedSignature_RoundTrips()
    {
        // The composed signature {provider}|c{size}o{overlap} should persist + match on
        // reopen the same way bare signatures do — VectorStore is opaque to the format.
        string sig = ChunkedSignature.Compose("ollama:nomic-embed-text", 1200, 200);

        using (var w = new VectorStore(_tempDir, expectedSignature: sig))
        {
            await w.UpsertAsync("local", "memo", 0, [0.1f, 0.2f, 0.3f]);
            await w.UpsertAsync("local", "memo", 1, [0.4f, 0.5f, 0.6f]);
        }

        using var r = new VectorStore(_tempDir, expectedSignature: sig);
        r.HasStaleQuarantines.Should().BeFalse();
        var vectors = r.GetVectors("local");
        vectors.Should().HaveCount(2);
        vectors.Select(v => v.ChunkIndex).Should().BeEquivalentTo(new int?[] { 0, 1 });
    }

    [Fact]
    public async Task Svf3_ChunkSizeChange_TriggersQuarantine()
    {
        // Changing ChunkSize via config must produce a different composed signature, which
        // must quarantine the old file — same flow as a model change.
        string oldSig = ChunkedSignature.Compose("ollama:nomic-embed-text", 1200, 200);
        string newSig = ChunkedSignature.Compose("ollama:nomic-embed-text", 1800, 300);

        using (var old = new VectorStore(_tempDir, expectedSignature: oldSig))
        {
            await old.UpsertAsync("local", "memo", 0, [0.1f, 0.2f, 0.3f]);
        }

        using var fresh = new VectorStore(_tempDir, expectedSignature: newSig);
        fresh.GetVectors("local").Should().BeEmpty();
        fresh.HasStaleQuarantines.Should().BeTrue();
    }

    [Fact]
    public async Task Svf3_NullExpectedSignature_DisablesMismatchCheck()
    {
        // Write with one signature
        using (var oldStore = new VectorStore(_tempDir, expectedSignature: "ollama:nomic-embed-text"))
        {
            await oldStore.UpsertAsync("local", "x", null, [0.1f, 0.2f, 0.3f]);
        }

        // Reopen without an expected signature — test/ad-hoc path. Should read OK,
        // no quarantine.
        using var reopen = new VectorStore(_tempDir, expectedSignature: null);
        var vectors = reopen.GetVectors("local");
        vectors.Should().HaveCount(1);
        reopen.HasStaleQuarantines.Should().BeFalse();
    }
}
