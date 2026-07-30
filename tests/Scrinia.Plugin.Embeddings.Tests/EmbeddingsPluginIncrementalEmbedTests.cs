using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Regression coverage for the incremental embed path (docs task B2): re-storing a memory with
/// fewer chunks must not leave stale higher-index chunk vectors behind, and appending to a memory
/// must re-embed the FULL current content (not just the appended fragment) into the null-chunk
/// document vector. Both bugs share the same root cause: <c>EmbedAndIndexAsync</c> previously
/// upserted without first removing prior vectors for the name, and <c>OnAppendedAsync</c> embedded
/// only the fragment.
///
/// This plugin has no Vulkan model available in the test environment (no GGUF at the expected
/// path), so the private <c>_vectorStore</c>/<c>_provider</c>/<c>_logger</c> fields are injected
/// via reflection (mirrors <see cref="EmbeddingsPluginReindexTests"/>) — setting <c>_vectorStore</c>
/// makes <c>EnsureInitialized</c> short-circuit at its <c>if (_vectorStore is not null) return;</c>
/// guard, so the injected deterministic provider survives.
/// </summary>
public sealed class EmbeddingsPluginIncrementalEmbedTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;
    private readonly string _vecDir;
    private readonly VectorStore _vectors;

    // Deterministic provider: vector encodes the input length so we can tell WHICH text was embedded.
    private sealed class LengthProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 1;
        public string Signature => "fake:len";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(new float[] { text.Length });
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>(texts.Select(t => new float[] { t.Length }).ToArray());
        public void Dispose() { }
    }

    public EmbeddingsPluginIncrementalEmbedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_inc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
        _vecDir = Path.Combine(_root, "vec");
        Directory.CreateDirectory(_vecDir);
        _vectors = new VectorStore(_vecDir, expectedSignature: null);
    }

    public void Dispose()
    {
        _store.Dispose();
        _vectors.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private EmbeddingsPlugin BuildPlugin()
    {
        var plugin = new EmbeddingsPlugin();
        typeof(EmbeddingsPlugin).GetField("_vectorStore", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, _vectors);
        typeof(EmbeddingsPlugin).GetField("_provider", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, new LengthProvider());
        typeof(EmbeddingsPlugin).GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return plugin;
    }

    private void SeedArtifact(string scope, string name, string content)
    {
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow);
        _store.Upsert(entry, scope);
        string path = _store.ArtifactPath(name, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public async Task ReStore_WithFewerChunks_LeavesNoStaleChunkVectors()
    {
        using var plugin = BuildPlugin();
        const string scope = "local:memory";
        // v2 path syntax ("/memory/notes") is required: ParseQualifiedName rejects the legacy
        // "topic:subject" colon form ("local:memory/notes" is NOT valid input here — it throws
        // ArgumentException, which EmbedAndIndexAsync's catch-and-log swallows, silently indexing
        // nothing). "/memory/notes" parses to scope "local:memory", subject "notes".
        const string qn = "/memory/notes";

        // First store: 3 chunks -> null-chunk doc vector + chunks 1,2,3 = 4 vectors.
        await plugin.OnStoredAsync(qn, new[] { "aaa", "bbb", "ccc" }, _store, CancellationToken.None);
        _vectors.GetVectors(scope).Count(v => v.Name == "notes").Should().Be(4);

        // Re-store: 1 chunk -> only the null-chunk doc vector. Without Remove-first, the 3 old
        // chunk vectors would remain (count 4). With the fix: exactly 1.
        await plugin.OnStoredAsync(qn, new[] { "single" }, _store, CancellationToken.None);
        _vectors.GetVectors(scope).Count(v => v.Name == "notes").Should().Be(1);
    }

    [Fact]
    public async Task Append_ReEmbedsFullMemory_NotJustTheFragment()
    {
        using var plugin = BuildPlugin();
        const string scope = "local:memory";
        const string subject = "log";
        const string qn = "/memory/log"; // parses to scope "local:memory", subject "log"

        SeedArtifact(scope, subject, "0123456789"); // length 10
        await plugin.OnStoredAsync(qn, new[] { "0123456789" }, _store, CancellationToken.None);

        // Append 5 chars on disk (simulating the store's append behavior), then fire the append
        // event with only the fragment — mirrors how OnAppendedAsync is invoked by the real
        // append path (content = just what was appended, not the full memory).
        string artifact = await _store.ReadArtifactAsync(subject, scope, CancellationToken.None);
        string decoded = System.Text.Encoding.UTF8.GetString(new Nmp2Strategy().Decode(artifact));
        string appendedFull = decoded + "ABCDE";
        SeedArtifact(scope, subject, appendedFull); // full content now length 15
        await plugin.OnAppendedAsync(qn, "ABCDE", _store, CancellationToken.None);

        // The doc (null-chunk) vector must encode the DECODED full current content length (15),
        // not the 5-char fragment. LengthProvider makes this observable.
        var doc = _vectors.GetVectors(scope).Single(v => v.Name == subject && v.ChunkIndex is null);
        doc.Vector[0].Should().Be(appendedFull.Length);
    }
}
