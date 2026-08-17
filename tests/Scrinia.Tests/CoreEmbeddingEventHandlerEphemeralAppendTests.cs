using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Services;

namespace Scrinia.Tests;

/// <summary>
/// Regression coverage for residual 1 of the 2026-08-16 embeddings-search-consolidation review:
/// appending to an ephemeral memory (<c>~note</c>) silently left its vectors stale.
///
/// <para><see cref="FileMemoryStore.ParseQualifiedName"/> has no <c>~</c> branch — a bare
/// ephemeral name falls through to <c>("local", SanitizeName(name))</c>, so the parsed SCOPE is
/// never <c>"ephemeral"</c>. Before the fix, <c>OnAppendedAsync</c> read via
/// <c>store.ReadArtifactAsync(subject, scope, ct)</c> unconditionally, which throws
/// <see cref="FileNotFoundException"/> for an ephemeral memory (it has no on-disk artifact at that
/// (scope, subject) location) — caught and downgraded to a warning, so the append becomes a
/// silent no-op and the memory's later elements never get vectors.</para>
///
/// <para>The tempting fix — key on <c>store.IsEphemeral(qualifiedName)</c> BEFORE the parse and
/// index under scope <c>"ephemeral"</c> — is wrong too: the STORE path (<c>OnStoredAsync</c> /
/// <c>EmbedAndIndexAsync</c>) always indexes under whatever <c>ParseQualifiedName</c> returns
/// (<c>"local"</c>), so indexing the append under <c>"ephemeral"</c> would orphan the store path's
/// vectors under a second, unreachable scope rather than updating them. Both assertions below
/// exist for that reason: the first catches the current bug, the second catches that wrong fix.</para>
/// </summary>
public sealed class CoreEmbeddingEventHandlerEphemeralAppendTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public CoreEmbeddingEventHandlerEphemeralAppendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_core_eph_append_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task OnAppendedAsync_EphemeralMemory_ReembedsUnderTheSameScopeSubjectTheStorePathUsed()
    {
        // The (scope, subject) the STORE path indexes an ephemeral "~note" under — the ground
        // truth the append path must match, not a value this test hard-codes.
        var (storeScope, storeSubject) = _store.ParseQualifiedName("~note");

        // Seed through the real ephemeral store path (StoreAsync), so this lands exactly where a
        // real `remember(name: "~note", ...)` call would.
        await _store.StoreAsync(new MemoryWrite("~note", "local", ["first element"]));

        string embeddingsDir = Path.Combine(_root, ".scrinia", "embeddings");
        Directory.CreateDirectory(embeddingsDir);
        using var vectorStore = new VectorStore(embeddingsDir, expectedSignature: null);
        var handler = new CoreEmbeddingEventHandler(new FakeProvider(), vectorStore, NullLogger.Instance);

        // "~note" — the exact qualifiedName MemoryTools.Core.cs's QualifiedName() helper builds
        // for an ephemeral entry (`$"~{entry.Name}"`), never scope-qualified.
        const string qualifiedName = "~note";

        // Drive OnStoredAsync the way the real store call site does, so the store path's vectors
        // exist to compare the append path's write against.
        await handler.OnStoredAsync(qualifiedName, ["first element"], _store, CancellationToken.None);

        // Grow the memory (still ephemeral) and drive the append path — mirrors
        // MemoryTools.Core.cs's append call site, which passes only the appended fragment.
        await _store.AppendAsync(new MemoryWrite("~note", "local", ["second element"]));
        await handler.OnAppendedAsync(qualifiedName, "second element", _store, CancellationToken.None);

        // Assertion 1: vectors exist for EVERY element, under the (scope, subject) the store path
        // used. Fails against unfixed code — the append's read throws FileNotFoundException
        // (swallowed to a warning), so only element 1's vector (from the earlier OnStoredAsync
        // call) is ever present; element 2 never gets one.
        var storeScopeVectors = vectorStore.GetVectors(storeScope)
            .Where(v => v.Name.Equals(storeSubject, StringComparison.OrdinalIgnoreCase))
            .ToList();

        storeScopeVectors
            .Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Distinct().Should().BeEquivalentTo([1, 2],
                because: "the append must re-embed the whole grown memory under the SAME " +
                    "(scope, subject) the store path indexed under, so element 2 gets vectors too " +
                    "instead of the append silently no-oping");

        // Assertion 2: no OTHER scope gained vectors for this memory. This is what catches the
        // tempting-but-wrong fix — keying the append's index write on
        // store.IsEphemeral(qualifiedName) before the parse, so it indexes under scope
        // "ephemeral" instead of the store path's "local" — which would orphan the store path's
        // vectors under a key no reindex loop, search path, or the store path itself ever reads.
        if (!storeScope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
        {
            vectorStore.GetVectors("ephemeral").Should().BeEmpty(
                because: "the append must index under the same scope the store path used " +
                    "(\"local\"), not a second, unreachable \"ephemeral\" scope");
        }
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
