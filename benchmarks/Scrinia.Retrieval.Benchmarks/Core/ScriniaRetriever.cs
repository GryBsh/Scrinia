using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// <see cref="IRetriever"/> backed by a per-question <see cref="FileMemoryStore"/>.
/// Pure BM25 mode: ingest populates <c>TermFrequencies</c> via
/// <see cref="TextAnalysis.AnalyzeText"/> and stores the original doc text in
/// <see cref="ArtifactEntry.ContentPreview"/> so the scorer can do substring matching
/// without re-reading the artifact file. No artifact file is written at all — the
/// metadata sidecar carries everything the BM25 path needs.
///
/// Embedding-aware modes will be added in a later commit; this commit covers the
/// apples-to-apples BM25 baseline against MemPalace's "raw" track.
/// </summary>
public sealed class ScriniaRetriever : IRetriever
{
    private readonly string _rootDir;
    private readonly RankerOptions _rankerOptions;
    private FileMemoryStore _store;
    private int _resetCount;

    public ScriniaRetriever(string rootDir, RankerOptions? rankerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        _rootDir = rootDir;
        _rankerOptions = rankerOptions ?? RankerOptions.Default;
        _store = CreateStore();
    }

    public Task IngestAsync(HaystackDoc doc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ct.ThrowIfCancellationRequested();

        var (keywords, tf) = TextAnalysis.AnalyzeText(doc.Text);
        var entry = new ArtifactEntry(
            // Description and Name are both scored by the field scorer; we keep the
            // benchmark doc ID *only* in Uri so query tokens never accidentally match
            // an ID component (e.g. "user" / "msg" in `convomem_user_5_msg_12`).
            Name: SanitizeName(doc.Id),
            Uri: $"bench://{doc.Id}",
            OriginalBytes: doc.Text.Length,
            ChunkCount: 0,
            CreatedAt: doc.Timestamp ?? DateTimeOffset.UtcNow,
            Description: string.Empty,
            Keywords: keywords,
            TermFrequencies: tf,
            ContentPreview: doc.Text);

        _store.Upsert(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ct.ThrowIfCancellationRequested();

        var results = _store.SearchAll(query, scopes: null, limit: Math.Max(1, topK));
        var hits = new List<RetrievalHit>(results.Count);
        foreach (var r in results)
        {
            switch (r)
            {
                case EntryResult er:
                    hits.Add(new RetrievalHit(
                        Id: ExtractDocId(er.Item.Entry.Uri),
                        Text: er.Item.Entry.ContentPreview ?? string.Empty,
                        Score: er.Score));
                    break;
                case ChunkEntryResult cr:
                    hits.Add(new RetrievalHit(
                        Id: ExtractDocId(cr.ParentItem.Entry.Uri),
                        Text: cr.ParentItem.Entry.ContentPreview ?? string.Empty,
                        Score: cr.Score));
                    break;
            }
        }
        return Task.FromResult<IReadOnlyList<RetrievalHit>>(hits);
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Clean up the previous question's dir before moving on so a 500q LongMemEval
        // run doesn't accumulate 26k metadata files on disk for the full run duration.
        string previous = Path.Combine(_rootDir, $"q{_resetCount:D6}");
        try
        {
            if (Directory.Exists(previous))
                Directory.Delete(previous, recursive: true);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }

        _resetCount++;
        _store = CreateStore();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_rootDir))
                Directory.Delete(_rootDir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        return ValueTask.CompletedTask;
    }

    private FileMemoryStore CreateStore()
    {
        // Per-question subdirs keep haystacks isolated and cheap to discard.
        string per = Path.Combine(_rootDir, $"q{_resetCount:D6}");
        Directory.CreateDirectory(per);
        return new FileMemoryStore(per) { RankerOptions = _rankerOptions };
    }

    /// <summary>
    /// Maps a benchmark doc ID into a filename-safe artifact name. The benchmark IDs
    /// (e.g. <c>conv_5_msg_12</c>, session UUIDs) already use safe characters, but we
    /// guard against slashes and Windows-reserved characters defensively.
    /// </summary>
    private static string SanitizeName(string id)
    {
        Span<char> buf = stackalloc char[id.Length];
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            buf[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }
        return new string(buf);
    }

    /// <summary>Reads the benchmark doc ID from the entry's <c>bench://</c> URI.</summary>
    private static string ExtractDocId(string uri) =>
        uri.StartsWith("bench://", StringComparison.Ordinal) ? uri["bench://".Length..] : uri;
}
