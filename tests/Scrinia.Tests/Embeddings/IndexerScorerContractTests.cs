using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// The finding-H3 conformance proof: the write side and the read side agree on what a chunk key
/// means, end to end.
///
/// <para>WeightedAttributeScorer's chunk pass probes "{scope}|{name}|{chunkIndex}" where
/// chunkIndex comes from ChunkEntry — 1-based over nmp2 content elements (TextAnalysis
/// emits `ChunkIndex: i + 1`). Before this branch, Core and Server wrote 0-based CHAR-WINDOW
/// ordinals into that key position, so the probe could never hit; the plugin wrote matching
/// element indices but never windowed, so a long element became one mean-pooled vector. This
/// class asserts both halves at once.</para>
///
/// <para>These tests cannot be run against pre-branch code to watch them fail —
/// <c>MemoryVectorIndexer</c> and <c>VectorScoreProjection</c> are both new here, so there is
/// nothing to compile them against. What they pin is the joined contract going forward: the
/// keys the indexer produces are exactly the keys the scorer consumes, verified by running a
/// real projection through <see cref="WeightedAttributeScorer.SearchAll(string,
/// IEnumerable{ScopedArtifact}, IEnumerable{TopicInfo}, int, IReadOnlyDictionary{string,
/// double}?, IReadOnlyDictionary{string, double}?)"/> rather than by comparing string
/// literals.</para>
/// </summary>
public sealed class IndexerScorerContractTests
{
    private sealed class OrdinalProvider : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:ordinal";
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f]);
        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([.. texts.Select((_, i) => new[] { 1f, (float)i })]);
        public void Dispose() { }
    }

    /// <summary>
    /// Embeds one marker term onto the X axis and everything else onto the Y axis, so a query for
    /// the marker is cosine-1 against exactly the elements that contain it and cosine-0 against
    /// the rest. That is what lets a real <see cref="VectorIndex.Search"/> +
    /// <see cref="VectorScoreProjection"/> pass produce a known per-element score spread instead
    /// of a hand-built dictionary.
    /// </summary>
    private sealed class MarkerProvider(string marker) : IEmbeddingProvider
    {
        public bool IsAvailable => true;
        public int Dimensions => 2;
        public string Signature => "test:marker";

        private float[] Encode(string text) =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase) ? [1f, 0f] : [0f, 1f];

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(Encode(text));

        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<float[][]?>([.. texts.Select(Encode)]);

        public void Dispose() { }
    }

    private static VectorStore NewStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new VectorStore(dir);
    }

    [Fact]
    public async Task EveryKeyTheScorerProbes_IsAKeyTheIndexerProduces()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new VectorStore(dir);
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        var indexer = new MemoryVectorIndexer(new OrdinalProvider(), store, NullLogger.Instance, options);

        // Three elements; the second is long enough to window several times.
        string[] content = ["short one", new string('b', 400), "short three"];
        await indexer.IndexAsync("local", "note", content, default);

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var hits = store.GetVectors("local").Select(v => (Entry: v, Similarity: 0.5)).ToList();
        VectorScoreProjection.Project("local", hits, weight: 100.0, into: scores);

        using var scope = new FluentAssertions.Execution.AssertionScope();

        scores.Should().ContainKey("local|note");
        for (int elementIndex = 1; elementIndex <= content.Length; elementIndex++)
        {
            scores.Should().ContainKey($"local|note|{elementIndex}",
                because: "TextAnalysis stamps ChunkEntry.ChunkIndex as i+1, and "
                       + "WeightedAttributeScorer.GetSupplemental probes exactly that value");
        }

        // And the long element really was windowed rather than mean-pooled over 400 chars.
        store.GetVectors("local")
            .Count(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value) == 2)
            .Should().BeGreaterThan(1);
    }

    // ── The two halves joined: indexer → projection → scorer ────────────────────

    private const string Marker = "xylophone";

    /// <summary>
    /// Content whose second element is the only one carrying the marker term — so both BM25 and
    /// the vector projection point at element 2, and nothing else in the memory does.
    /// </summary>
    private static readonly string[] JoinedContent =
    [
        "alpha bravo charlie notes about deployment",
        $"{Marker} marimba vibraphone percussion instruments",
        "delta echo foxtrot notes about monitoring",
    ];

    /// <summary>
    /// Builds the candidate the scorer sees, with ChunkEntries numbered exactly the way
    /// TextAnalysis numbers them for a real write (1-based, one per content element).
    /// </summary>
    private static ScopedArtifact JoinedCandidate()
    {
        var (keywords, tf) = TextAnalysis.AnalyzeText(string.Concat(JoinedContent));
        return new ScopedArtifact("local", new ArtifactEntry(
            Name: "note",
            Uri: "",
            OriginalBytes: 100,
            ChunkCount: JoinedContent.Length,
            CreatedAt: DateTimeOffset.UtcNow,
            Keywords: keywords,
            TermFrequencies: tf,
            ChunkEntries: TextAnalysis.ComputeChunkEntries(JoinedContent)));
    }

    /// <summary>
    /// Runs the real read path — embed the query, rank the stored vectors, project them — and
    /// returns the supplemental dictionary a live search would hand the scorer.
    /// </summary>
    private static async Task<Dictionary<string, double>> ProjectAsync(
        IEmbeddingProvider provider, VectorStore store, string query, double weight)
    {
        var queryVec = await provider.EmbedAsync(query);
        var vectors = store.GetVectors("local");
        var topK = VectorIndex.Search(queryVec!, vectors, vectors.Count);

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        VectorScoreProjection.Project(
            "local", topK.Select(t => (t.Entry, (double)t.Similarity)), weight, scores);
        return scores;
    }

    private static double ScoreOf(IReadOnlyList<SearchResult> results, out SearchResult winner)
    {
        winner = results.First(r =>
            r is EntryResult e && e.Item.Entry.Name == "note"
            || r is ChunkEntryResult c && c.ParentItem.Entry.Name == "note");
        return winner.Score;
    }

    [Fact]
    public async Task AVectorHitOnOneElement_RaisesThatChunksScoreInSearchAll()
    {
        // Write side: index the real content array through the real indexer.
        var provider = new MarkerProvider(Marker);
        var store = NewStore();
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        await new MemoryVectorIndexer(provider, store, NullLogger.Instance, options)
            .IndexAsync("local", "note", JoinedContent, default);

        // Read side: the projection a live search would produce for this query.
        var supplemental = await ProjectAsync(provider, store, Marker, weight: 50.0);

        var scorer = new WeightedAttributeScorer();
        var candidates = new[] { JoinedCandidate() };

        var without = scorer.SearchAll(Marker, candidates, [], 10, supplementalScores: null);
        var with = scorer.SearchAll(Marker, candidates, [], 10, supplementalScores: supplemental);

        double baseline = ScoreOf(without, out _);
        double boosted = ScoreOf(with, out var winner);

        using var _scope = new FluentAssertions.Execution.AssertionScope();

        winner.Should().BeOfType<ChunkEntryResult>(
            because: "the marker term appears in exactly one content element, so the chunk-level "
                   + "result must outrank the whole-memory one");
        ((ChunkEntryResult)winner).Chunk.ChunkIndex.Should().Be(2,
            because: "ChunkEntry.ChunkIndex is 1-based over content elements and the marker is "
                   + "in element 2");
        boosted.Should().BeGreaterThan(baseline,
            because: "the projected vector score must actually reach the scorer");
    }

    [Fact]
    public async Task TheChunkKeyAloneCarriesTheScore_WithNoMemoryLevelKeyToFallBackOn()
    {
        // The headline bug in one assertion. VectorScoreProjection emits BOTH "{scope}|{name}"
        // and "{scope}|{name}|{element}", and WeightedAttributeScorer.GetSupplemental falls back
        // from the second to the first — so a run with both keys present cannot distinguish "the
        // chunk probe hit" from "the chunk probe missed and fell back to the memory key". That is
        // precisely the confusion under which the pre-branch mismatch survived: Core and Server
        // wrote window ordinals into the chunk key position, the chunk probe never hit, and the
        // memory-level fallback made search look like it still worked.
        //
        // Dropping the memory-level key removes the fallback. Any lift that survives can only
        // have come through a "{scope}|{name}|{elementIndex}" key that the indexer produced and
        // the scorer probed for under the same meaning of elementIndex.
        var provider = new MarkerProvider(Marker);
        var store = NewStore();
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        await new MemoryVectorIndexer(provider, store, NullLogger.Instance, options)
            .IndexAsync("local", "note", JoinedContent, default);

        var projected = await ProjectAsync(provider, store, Marker, weight: 50.0);
        var chunkKeysOnly = projected
            .Where(kv => kv.Key.Count(c => c == '|') == 2)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        chunkKeysOnly.Should().ContainKey("local|note|2",
            because: "element 2 is the one the marker vector belongs to");
        chunkKeysOnly.Should().NotContainKey("local|note");

        var scorer = new WeightedAttributeScorer();
        var candidates = new[] { JoinedCandidate() };

        double baseline = ScoreOf(scorer.SearchAll(Marker, candidates, [], 10, null), out _);
        double boosted = ScoreOf(
            scorer.SearchAll(Marker, candidates, [], 10, chunkKeysOnly), out var winner);

        using var _scope = new FluentAssertions.Execution.AssertionScope();

        boosted.Should().BeGreaterThan(baseline,
            because: "with no memory-level key to fall back on, the only path to a higher score "
                   + "is the chunk key — the probe that could never hit before this branch");
        winner.Should().BeOfType<ChunkEntryResult>();
        ((ChunkEntryResult)winner).Chunk.ChunkIndex.Should().Be(2);
    }

    [Fact]
    public async Task AWindowedElement_StillProjectsToItsElementKey_NotAWindowOrdinal()
    {
        // The plugin's half of H3: an element long enough to window must still resolve to ONE
        // element key, not to a set of keys named after window ordinals.
        var provider = new MarkerProvider(Marker);
        var store = NewStore();
        var options = new EmbeddingOptions { ChunkSize = 60, ChunkOverlap = 10 };

        // Element 2 is long enough to slice into several windows, all carrying the marker.
        string[] content =
        [
            "alpha bravo charlie",
            string.Join(' ', Enumerable.Repeat($"{Marker} marimba", 30)),
            "delta echo foxtrot",
        ];
        await new MemoryVectorIndexer(provider, store, NullLogger.Instance, options)
            .IndexAsync("local", "note", content, default);

        int windowCount = store.GetVectors("local")
            .Count(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value) == 2);
        windowCount.Should().BeGreaterThan(1, because: "element 2 must be windowed, not mean-pooled");

        var projected = await ProjectAsync(provider, store, Marker, weight: 50.0);

        // Elements 1 and 3 are cosine-0 against the query, so VectorIndex.Search does not return
        // them at all — only element 2's windows reach the projection. All of them, and they must
        // land on ONE key: window ordinals are an implementation detail that never appears in a
        // supplemental key, which is the guarantee VectorScoreProjection exists to make.
        projected.Keys.Where(k => k.StartsWith("local|note|", StringComparison.Ordinal))
            .Should().BeEquivalentTo(["local|note|2"],
                because: $"element 2's {windowCount} windows must roll up into the single key the "
                       + "scorer probes for element 2, not into one key per window ordinal");
    }

    [Fact]
    public async Task AppendingToAMemory_KeepsVectorsForTheElementsItAlreadyHad()
    {
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };
        var store = NewStore();
        var indexer = new MemoryVectorIndexer(new OrdinalProvider(), store, NullLogger.Instance, options);

        await indexer.IndexAsync("local", "note", ["first", "second"], default);
        // What an append must do: index the FULL post-append content, not the fragment alone.
        await indexer.IndexAsync("local", "note", ["first", "second", "third"], default);

        store.GetVectors("local")
            .Select(v => MemoryVectorIndexer.ElementIndexOf(v.ChunkIndex!.Value))
            .Distinct().Should().BeEquivalentTo([1, 2, 3],
                because: "indexing only the appended fragment would leave the memory with vectors "
                       + "for that fragment and nothing else — the indexer replaces atomically");
    }

    [Fact]
    public async Task ReindexProducesTheSameLayoutAsTheWriteItRebuilds()
    {
        string[] content = ["short one", new string('b', 400), "short three"];
        var options = new EmbeddingOptions { ChunkSize = 100, ChunkOverlap = 20 };

        // The write path: the content array arrives in flight.
        var written = NewStore();
        await new MemoryVectorIndexer(new OrdinalProvider(), written, NullLogger.Instance, options)
            .IndexAsync("local", "note", content, default);

        // The reindex path: the array is reconstructed from the stored artifact.
        var rebuilt = NewStore();
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(content);
        await new MemoryVectorIndexer(new OrdinalProvider(), rebuilt, NullLogger.Instance, options)
            .IndexAsync("local", "note", Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks, default);

        rebuilt.GetVectors("local").Select(v => v.ChunkIndex)
            .Should().BeEquivalentTo(written.GetVectors("local").Select(v => v.ChunkIndex),
                because: "Task 7's signature bump rebuilds every existing vector through reindex — if "
                       + "reindex collapsed a memory to element 1, the element contract would hold "
                       + "only for memories written after this branch");
    }
}
