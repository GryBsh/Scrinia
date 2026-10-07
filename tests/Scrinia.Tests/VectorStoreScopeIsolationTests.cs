using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for the scope-path collision in <c>VectorStore.GetFilePath</c>: the old
/// mapping replaced every separator with <c>'_'</c>, so <c>local:foo/bar</c> and
/// <c>local:foo_bar</c> both resolved to <c>local_foo_bar</c> and shared one
/// <c>vectors.bin</c>. Both scopes arise from ordinary naming — <c>PathParser</c> permits
/// underscores in a segment.
/// </summary>
public class VectorStoreScopeIsolationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VectorStore _store;

    public VectorStoreScopeIsolationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_vectest_scope_{Guid.NewGuid():N}");
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
    public async Task NestedPathScope_And_UnderscoreTopicScope_DoNotShareStorage()
    {
        await _store.UpsertAsync("local:foo/bar", "nested", null, [1f, 0f]);
        await _store.UpsertAsync("local:foo_bar", "flat", null, [0f, 1f]);

        var nested = _store.GetVectors("local:foo/bar");
        var flat = _store.GetVectors("local:foo_bar");

        nested.Should().ContainSingle().Which.Name.Should().Be("nested");
        flat.Should().ContainSingle().Which.Name.Should().Be("flat");
    }

    [Fact]
    public async Task CollidingScopes_SurviveReloadFromDisk_Independently()
    {
        // The in-memory cache is keyed by the raw scope string, so a collision only shows up
        // once the data round-trips through the file layer. Reload with a fresh store.
        await _store.UpsertAsync("local:foo/bar", "nested", null, [1f, 0f]);
        await _store.UpsertAsync("local:foo_bar", "flat", null, [0f, 1f]);

        using var reloaded = new VectorStore(_tempDir);

        reloaded.GetVectors("local:foo/bar").Should().ContainSingle()
            .Which.Name.Should().Be("nested", because: "the nested-path scope keeps its own file");
        reloaded.GetVectors("local:foo_bar").Should().ContainSingle()
            .Which.Name.Should().Be("flat", because: "the underscore topic keeps a separate file");
    }

    [Fact]
    public async Task LegacyFlatLayout_IsMigratedRatherThanOrphaned()
    {
        // A store written before the fix put "local:api" at {base}/local_api/vectors.bin.
        // Upgrading must adopt that file rather than starting empty and forcing a re-embed.
        await _store.UpsertAsync("local:api", "doc", null, [0.5f, 0.5f]);

        string nestedPath = Path.Combine(_tempDir, "local", "api", "vectors.bin");
        File.Exists(nestedPath).Should().BeTrue("the new layout nests scope segments");

        string legacyDir = Path.Combine(_tempDir, "local_api");
        Directory.CreateDirectory(legacyDir);
        File.Move(nestedPath, Path.Combine(legacyDir, "vectors.bin"));
        // The scope directory also holds vectors.bin.lock — remove the whole thing so the
        // new layout genuinely has nothing for this scope.
        Directory.Delete(Path.Combine(_tempDir, "local", "api"), recursive: true);

        using var reloaded = new VectorStore(_tempDir);
        var vectors = reloaded.GetVectors("local:api");

        vectors.Should().ContainSingle().Which.Name.Should().Be("doc");
        File.Exists(nestedPath).Should().BeTrue("the legacy file is moved into the new layout");
    }
}
