using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for task 21: <see cref="VectorStore.UpsertAsync"/> must dedup
/// existing entries case-insensitively so its in-memory behavior matches
/// <see cref="VectorStore.RemoveAsync"/> and the on-disk op-log replay
/// (<c>ReadEntryOps</c>), both of which key on <see cref="StringComparer.OrdinalIgnoreCase"/>.
/// Before the fix, upserting "Foo" then "foo" left two in-memory entries that a persisted
/// store would collapse to one on reload, causing an in-memory vs on-disk divergence.
/// </summary>
public class VectorStoreCaseTests : IDisposable
{
    private static readonly float[] Vector03F04F = new float[] { 0.3f, 0.4f };

    private readonly string _tempDir;
    private readonly VectorStore _store;

    public VectorStoreCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_vectest_case_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new VectorStore(_tempDir);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Upsert_DifferentCaseSameName_Ephemeral_DedupesCaseInsensitively()
    {
        await _store.UpsertAsync("ephemeral", "Foo", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("ephemeral", "foo", null, [0.3f, 0.4f]);

        var vectors = _store.GetVectors("ephemeral");
        vectors.Should().HaveCount(1);
        vectors[0].Vector.Should().BeEquivalentTo(Vector03F04F);
    }

    [Fact]
    public async Task Upsert_DifferentCaseSameName_Persistent_MatchesReloadSemantics()
    {
        await _store.UpsertAsync("local", "Foo", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("local", "foo", null, [0.3f, 0.4f]);

        // In-memory view (cached, no reload) must already be deduped...
        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Vector.Should().BeEquivalentTo(Vector03F04F);

        // ...and match what a fresh instance sees after replaying the on-disk op-log,
        // which has always deduped case-insensitively (ReadEntryOps).
        using var reloaded = new VectorStore(_tempDir);
        var reloadedVectors = reloaded.GetVectors("local");
        reloadedVectors.Should().HaveCount(1);
        reloadedVectors[0].Vector.Should().BeEquivalentTo(Vector03F04F);
    }
}
