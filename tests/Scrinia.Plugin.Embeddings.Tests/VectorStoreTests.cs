using FluentAssertions;
using Scrinia.Plugin.Embeddings;

namespace Scrinia.Plugin.Embeddings.Tests;

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

        // Create a new store instance pointing at the same directory
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

        // New instance should not see ephemeral vectors
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

        _store.TotalVectorCount().Should().Be(3);
    }

    [Fact]
    public void GetVectors_EmptyScope_ReturnsEmpty()
    {
        _store.GetVectors("nonexistent").Should().BeEmpty();
    }

    // ── SVF2 append-only format tests ────────────────────────────────────────

    [Fact]
    public async Task Svf2_AppendOnly_UpsertAppends()
    {
        // First upsert creates SVF2 file
        await _store.UpsertAsync("local", "first", null, [0.1f, 0.2f, 0.3f]);

        // Second upsert should append rather than rewrite
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

        // After upsert with same key, new instance should see updated value
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

        // New instance should only see "keep"
        using var store2 = new VectorStore(_tempDir);
        var vectors = store2.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Name.Should().Be("keep");
    }
}
