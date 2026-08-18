using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// Regression coverage for the qualifiedName round-trip loss described in
/// docs task "F": <see cref="FileMemoryStore.FormatQualifiedName"/> intentionally strips the
/// "memory/"/"entity/" namespace prefix for human-friendly display (v1 "topic:subject" scopes
/// reconstruct as bare "/topic/subject" paths). That string is safe to *display*, but it is NOT
/// safe to re-parse: <c>ParseQualifiedName(FormatQualifiedName(scope, subject))</c> reconstructs
/// a scope WITHOUT the namespace prefix, which differs from the original scope for every
/// ordinary named-topic memory (the "memory/" namespace covers all topics except the "skill"
/// entity topic and the "agent" topic).
///
/// <see cref="EmbeddingReindexer"/> used this lossy round trip in two ways:
///   1. Resolving the artifact to embed (<c>ResolveArtifactAsync(qualifiedName, ct)</c>) — this
///      re-derives the WRONG scope, so <c>FindArtifactPath</c> can't find the file and the memory
///      is silently skipped (FileNotFoundException -> skipped++).
///   2. Handing the qualifiedName to <see cref="IMemoryEventSink.OnStoredAsync"/> (the sink-driven
///      reindex path) — a real embedding sink re-parses that string to decide which scope to
///      upsert vectors under, so vectors land under the WRONG scope. Search later looks up
///      vectors under the entry's REAL scope (from <c>ListScoped</c>) and finds nothing.
/// </summary>
public sealed class EmbeddingReindexerNamespacedScopeTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public EmbeddingReindexerNamespacedScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_reindex_ns_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
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
    public async Task ReindexAsync_NamespacedTopicScope_EmbedsRatherThanSkips()
    {
        // "api:notes" v1 syntax -> "local:memory/api" (the ordinary case for every
        // non-skill, non-agent named topic).
        const string scope = "local:memory/api";
        SeedEntry(scope, "notes", "auth flow notes");

        string embeddingsDir = Path.Combine(_root, ".scrinia", "embeddings");
        Directory.CreateDirectory(embeddingsDir);
        using var vectorStore = new VectorStore(embeddingsDir, expectedSignature: null);
        var provider = new FakeProvider();

        var result = await EmbeddingReindexer.ReindexAsync(
            _store, provider, vectorStore, NullLogger.Instance, progress: null, CancellationToken.None);

        result.Skipped.Should().Be(0, because: "the artifact exists and should resolve via the entry's real scope, not a re-derived one");
        result.Embedded.Should().Be(1);

        var vectors = vectorStore.GetVectors(scope);
        vectors.Should().HaveCount(1, because: "vectors must be written under the memory's REAL scope so search (which looks up by real scope) can find them");
    }

    [Fact]
    public async Task ReindexViaSinkAsync_NamespacedTopicScope_SinkReceivesCorrectScope()
    {
        const string scope = "local:memory/api";
        SeedEntry(scope, "notes", "auth flow notes");

        var capturingSink = new CapturingSink();

        var result = await EmbeddingReindexer.ReindexViaSinkAsync(
            _store, capturingSink, NullLogger.Instance, progress: null, CancellationToken.None);

        result.Skipped.Should().Be(0, because: "the artifact should resolve via the entry's real scope");
        result.Embedded.Should().Be(1);

        capturingSink.ReceivedQualifiedNames.Should().ContainSingle();
        var (parsedScope, parsedSubject) = _store.ParseQualifiedName(capturingSink.ReceivedQualifiedNames[0]);
        parsedScope.Should().Be(scope, because: "the sink must receive a qualifiedName that re-parses back to the memory's real scope");
        parsedSubject.Should().Be("notes");
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

    private sealed class CapturingSink : IMemoryEventSink
    {
        public List<string> ReceivedQualifiedNames { get; } = [];

        public Task OnStoredAsync(string qualifiedName, string[] content, IPluginStore store, CancellationToken ct)
        {
            ReceivedQualifiedNames.Add(qualifiedName);
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(string qualifiedName, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
