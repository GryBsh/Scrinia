using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests;

/// <summary>
/// Regression coverage for the qualifiedName round-trip loss in
/// <see cref="BuiltInEmbeddingsService.ReindexStoreAsync"/> (docs task "F", part 2 — the
/// "duplicated reindex logic" half of the bug already fixed for <c>EmbeddingReindexer</c> in
/// commit 1e81666 and covered by <c>EmbeddingReindexerNamespacedScopeTests</c>).
///
/// <see cref="IMemoryStore.FormatQualifiedName"/> intentionally strips the "memory/"/"entity/"
/// namespace prefix for human-friendly display (a v1 "topic:subject" scope like
/// "local:memory/api" displays as "/api/subject"). Re-parsing that display string via
/// <see cref="IPluginStore.ParseQualifiedName"/> reconstructs a scope MISSING the namespace
/// prefix, so <c>ResolveArtifactAsync</c> can't find the artifact on disk and the memory is
/// silently skipped (caught, logged, and dropped from the reindex count) for every ordinary
/// named-topic memory. This is wired into production via <c>Program.cs</c>.
/// </summary>
public sealed class BuiltInEmbeddingsServiceReindexTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;
    private readonly string _embeddingsDir;

    public BuiltInEmbeddingsServiceReindexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_builtin_reindex_ns_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
        _embeddingsDir = Path.Combine(_root, ".scrinia", "embeddings");
        Directory.CreateDirectory(_embeddingsDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Seeds a persistent entry under the given (already-correct) scope, matching how
    /// a normal v1 "topic:subject" remember() call lands on disk.</summary>
    private void SeedEntry(string scope, string name, string content)
    {
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow);
        _store.Upsert(entry, scope);
        string path = _store.ArtifactPath(name, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public async Task ReindexStoreAsync_NamespacedTopicScope_EmbedsRatherThanSkips()
    {
        // "api:notes" v1 syntax -> "local:memory/api" (the ordinary case for every
        // non-skill, non-agent named topic).
        const string scope = "local:memory/api";
        SeedEntry(scope, "notes", "auth flow notes");

        using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
        var provider = new FakeProvider();
        var service = new BuiltInEmbeddingsService(
            provider, vectorStore, semanticWeight: 50.0, NullLogger<BuiltInEmbeddingsService>.Instance);

        int count = await service.ReindexStoreAsync(_store, CancellationToken.None);

        count.Should().Be(1, because: "the artifact exists and should resolve via the entry's real scope, not a re-derived one");

        var vectors = vectorStore.GetVectors(scope);
        vectors.Should().HaveCount(1, because: "vectors must be written under the memory's REAL scope so search (which looks up by real scope) can find them");
    }

    /// <summary>
    /// Scrinia has FOUR full-corpus reindex loops and they split on the ephemeral scope.
    /// EmbeddingReindexer.ReindexAsync and ReindexViaSinkAsync filter it out; this one and
    /// EmbeddingsPlugin.ReindexStoreAsync do not, because they iterate store.ListScoped(), which
    /// includes ephemeral memories by design. So a literal scope of "ephemeral" reaches
    /// MemoryArtifactSource ONLY from here, and only this path exercises its scope-keyed
    /// IsEphemeral(scope) check.
    ///
    /// <para>On 2026-08-17 a review residual asserted that check was provably dead and instructed its
    /// deletion — a claim derived from inspecting only EmbeddingReindexer's two loops. No test drove
    /// this path, so the deletion would have shipped green. This test is what makes it fail. Before
    /// calling MemoryArtifactSource.IsEphemeral or EphemeralScope dead, run this.</para>
    /// </summary>
    [Fact]
    public async Task ReindexStoreAsync_EphemeralMemory_IndexesItUnderTheEphemeralScope()
    {
        const string name = "scratch-note";
        const string content = "ephemeral working note";
        _store.RememberEphemeral(name, new EphemeralEntry(
            Name: name,
            Artifact: Nmp2ChunkedEncoder.Encode(content),
            OriginalBytes: content.Length,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow));

        using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
        var service = new BuiltInEmbeddingsService(
            new FakeProvider(), vectorStore, semanticWeight: 50.0,
            NullLogger<BuiltInEmbeddingsService>.Instance);

        int count = await service.ReindexStoreAsync(_store, CancellationToken.None);

        count.Should().Be(1, because: "ListScoped includes ephemeral memories, so reindex must embed them");
        vectorStore.GetVectors(MemoryArtifactSource.EphemeralScope)
            .Should().HaveCount(1,
                because: "the scope-keyed ephemeral branch in MemoryArtifactSource.ReadArtifactAsync is " +
                         "what resolves '~name'; without it this memory is silently skipped");
    }

    private sealed class FakeProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 3;
        public string Signature => "fake:test";

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult<float[]?>([(float)text.Length, 0f, 0f]);

        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<float[][]?>(texts.Select(t => new[] { (float)t.Length, 0f, 0f }).ToArray());

        public void Dispose() { }
    }
}
