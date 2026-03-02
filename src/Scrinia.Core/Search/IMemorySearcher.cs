using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

// ── Result types ────────────────────────────────────────────────────────────

public abstract record SearchResult(double Score);

public sealed record EntryResult(
    ScopedArtifact Item,
    double Score) : SearchResult(Score);

public sealed record TopicResult(
    string Scope,
    string TopicName,
    string Description,
    int EntryCount,
    string[]? Tags,
    double Score) : SearchResult(Score);

public sealed record ChunkEntryResult(
    ScopedArtifact ParentItem,
    ChunkEntry Chunk,
    int TotalChunks,
    double Score) : SearchResult(Score);

// Backward-compatible entry-only result (used by Find/Search methods)
public sealed record ScoredArtifact(
    ScopedArtifact Item,
    double Score);

// Topic metadata for search scoring
public sealed record TopicInfo(
    string Scope,
    string TopicName,
    int EntryCount,
    string Description,
    string[]? Tags,
    string[] EntryNames);

internal interface IMemorySearcher
{
    IReadOnlyList<ScoredArtifact> Search(
        string query,
        IEnumerable<ScopedArtifact> candidates,
        int limit);
}

/// <summary>
/// Linear-scan weighted field scorer with multi-term support and BM25 content search.
///
/// Multi-term queries split on whitespace; each term is scored independently
/// and per-term max scores are summed. This ensures "DI patterns" ranks entries
/// matching both terms higher than entries matching only one.
///
/// Hybrid scoring: finalScore = weightedFieldScore + bm25Score * 5.0
///
/// Entry scoring (per term):
///
/// Match type               Score
/// ─────────────────────    ─────
/// Exact name match          100
/// Tag exact match            50
/// Keyword exact match        40
/// Name starts with           30
/// Name contains              20
/// Tag contains               15
/// Keyword contains           12
/// Description contains       10
/// Content preview contains    5
///
/// Topic scoring (per term):
///
/// Match type               Score
/// ─────────────────────    ─────
/// Exact topic name          100
/// Topic tag exact match      50
/// Topic name starts with     30
/// Topic name contains        20
/// Topic tag contains         15
/// Topic description cont.    10
/// 3+ entry names match        8
///
/// All matching is case-insensitive. Results sorted by score desc, then newest first.
/// Entries without TF data (v2 index) get bm25Score=0 (graceful degradation).
/// </summary>
internal sealed class WeightedFieldScorer : IMemorySearcher
{
    private const double Bm25Weight = 5.0;

    public IReadOnlyList<ScoredArtifact> Search(
        string query,
        IEnumerable<ScopedArtifact> candidates,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var candidateList = candidates as IList<ScopedArtifact> ?? candidates.ToList();

        // Compute BM25 corpus stats from all candidates with TF data
        var (avgDocLen, docFreqs) = Bm25Scorer.ComputeCorpusStats(
            candidateList.Select(c => (IReadOnlyDictionary<string, int>?)c.Entry.TermFrequencies));

        var queryTerms = TextAnalysis.Tokenize(query);
        int corpusSize = candidateList.Count;

        var scored = new List<ScoredArtifact>();

        foreach (var candidate in candidateList)
        {
            double fieldScore = ScoreEntry(query, candidate.Entry);
            double bm25Score = ComputeBm25(queryTerms, candidate.Entry, avgDocLen, corpusSize, docFreqs);
            double total = fieldScore + bm25Score * Bm25Weight;
            if (total > 0)
                scored.Add(new ScoredArtifact(candidate, total));
        }

        scored.Sort((a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : b.Item.Entry.CreatedAt.CompareTo(a.Item.Entry.CreatedAt);
        });

        return scored.Take(Math.Max(1, limit)).ToList();
    }

    /// <summary>
    /// Searches both entries and topics, returning polymorphic results interleaved by score.
    /// </summary>
    public IReadOnlyList<SearchResult> SearchAll(
        string query,
        IEnumerable<ScopedArtifact> candidates,
        IEnumerable<TopicInfo> topics,
        int limit)
        => SearchAll(query, candidates, topics, limit, supplementalScores: null);

    /// <summary>
    /// Searches both entries and topics with optional supplemental scores (e.g. from embeddings).
    /// Supplemental score keys: "{scope}|{name}" for entries, "{scope}|{name}|{chunkIndex}" for chunks.
    ///
    /// BM25 scores are normalized to 0–100 (min-max) before combining with field scores.
    /// Multi-term intersection bonus of (matchedTerms - 1) * 15 rewards queries matching multiple terms.
    /// Uses a min-heap (size K) for top-K selection instead of full sort for efficiency.
    /// </summary>
    public IReadOnlyList<SearchResult> SearchAll(
        string query,
        IEnumerable<ScopedArtifact> candidates,
        IEnumerable<TopicInfo> topics,
        int limit,
        IReadOnlyDictionary<string, double>? supplementalScores)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var candidateList = candidates as IList<ScopedArtifact> ?? candidates.ToList();

        // Compute BM25 corpus stats
        var (avgDocLen, docFreqs) = Bm25Scorer.ComputeCorpusStats(
            candidateList.Select(c => (IReadOnlyDictionary<string, int>?)c.Entry.TermFrequencies));

        var queryTerms = TextAnalysis.Tokenize(query);
        int corpusSize = candidateList.Count;

        // ── Pass 1: Compute raw BM25 scores for normalization ────────────
        var entryBm25 = new double[candidateList.Count];
        var chunkBm25List = new List<(int candidateIdx, int chunkIdx, double rawBm25)>();
        double minBm25 = double.MaxValue, maxBm25 = double.MinValue;

        for (int i = 0; i < candidateList.Count; i++)
        {
            double raw = ComputeBm25(queryTerms, candidateList[i].Entry, avgDocLen, corpusSize, docFreqs);
            entryBm25[i] = raw;
            if (raw > 0) { minBm25 = Math.Min(minBm25, raw); maxBm25 = Math.Max(maxBm25, raw); }

            if (candidateList[i].Entry.ChunkEntries is { Length: > 0 } chunks)
            {
                foreach (var chunk in chunks)
                {
                    double chunkRaw = ComputeChunkBm25(queryTerms, chunk, avgDocLen, corpusSize, docFreqs);
                    chunkBm25List.Add((i, chunk.ChunkIndex, chunkRaw));
                    if (chunkRaw > 0) { minBm25 = Math.Min(minBm25, chunkRaw); maxBm25 = Math.Max(maxBm25, chunkRaw); }
                }
            }
        }

        double bm25Range = maxBm25 - minBm25;
        if (bm25Range <= 0) bm25Range = 1; // Avoid division by zero

        // ── Pass 2: Combine normalized BM25 with field scores, deduplicate inline ──
        // Track best score per memory for deduplication during scoring
        var bestPerMemory = new Dictionary<string, (SearchResult Result, double Score)>(StringComparer.OrdinalIgnoreCase);
        var topicResults = new List<SearchResult>();

        for (int i = 0; i < candidateList.Count; i++)
        {
            var candidate = candidateList[i];
            double fieldScore = ScoreEntry(query, candidate.Entry);
            double normalizedBm25 = entryBm25[i] > 0 ? (entryBm25[i] - minBm25) / bm25Range * 100.0 : 0;
            double supplemental = GetSupplemental(candidate.Scope, candidate.Entry.Name, null, supplementalScores);
            double total = fieldScore + normalizedBm25 * Bm25Weight + supplemental;
            if (total > 0)
            {
                string key = $"{candidate.Scope}|{candidate.Entry.Name}";
                if (!bestPerMemory.TryGetValue(key, out var existing) || total > existing.Score)
                    bestPerMemory[key] = (new EntryResult(candidate, total), total);
            }
        }

        // ── Chunk-level scoring ─────────────────────────────────────────
        foreach (var (candidateIdx, chunkIdx, rawBm25) in chunkBm25List)
        {
            var candidate = candidateList[candidateIdx];
            var chunk = candidate.Entry.ChunkEntries!.First(c => c.ChunkIndex == chunkIdx);
            double chunkFieldScore = ScoreChunkEntry(query, chunk, candidate.Entry);
            double normalizedChunkBm25 = rawBm25 > 0 ? (rawBm25 - minBm25) / bm25Range * 100.0 : 0;
            double chunkSupplemental = GetSupplemental(candidate.Scope, candidate.Entry.Name, chunkIdx, supplementalScores);
            double chunkTotal = chunkFieldScore + normalizedChunkBm25 * Bm25Weight + chunkSupplemental;
            if (chunkTotal > 0)
            {
                string key = $"{candidate.Scope}|{candidate.Entry.Name}";
                if (!bestPerMemory.TryGetValue(key, out var existing) || chunkTotal > existing.Score)
                    bestPerMemory[key] = (new ChunkEntryResult(candidate, chunk, candidate.Entry.ChunkCount, chunkTotal), chunkTotal);
            }
        }

        foreach (var topic in topics)
        {
            double score = ScoreTopic(query, topic);
            if (score > 0)
                topicResults.Add(new TopicResult(topic.Scope, topic.TopicName, topic.Description, topic.EntryCount, topic.Tags, score));
        }

        // ── Min-heap top-K selection ─────────────────────────────────────
        int k = Math.Max(1, limit);
        var heap = new PriorityQueue<SearchResult, double>(); // min-heap: lowest score at top

        // Feed deduplicated memory results into heap
        foreach (var (result, score) in bestPerMemory.Values)
            HeapInsert(heap, result, score, k);

        // Feed topic results into heap
        foreach (var result in topicResults)
            HeapInsert(heap, result, result.Score, k);

        // Drain heap into array in descending score order
        var final = new SearchResult[heap.Count];
        for (int i = final.Length - 1; i >= 0; i--)
            final[i] = heap.Dequeue();

        // Stable tie-breaking: sort by score desc, then by creation date desc
        Array.Sort(final, (a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0) return cmp;

            DateTimeOffset aDate = a switch
            {
                EntryResult er => er.Item.Entry.CreatedAt,
                ChunkEntryResult cr => cr.ParentItem.Entry.CreatedAt,
                _ => DateTimeOffset.MinValue,
            };
            DateTimeOffset bDate = b switch
            {
                EntryResult er2 => er2.Item.Entry.CreatedAt,
                ChunkEntryResult cr2 => cr2.ParentItem.Entry.CreatedAt,
                _ => DateTimeOffset.MinValue,
            };
            return bDate.CompareTo(aDate);
        });

        return final;
    }

    /// <summary>Inserts into a min-heap of size K, evicting the smallest when full.</summary>
    private static void HeapInsert(PriorityQueue<SearchResult, double> heap, SearchResult result, double score, int k)
    {
        if (heap.Count < k)
        {
            heap.Enqueue(result, score);
        }
        else
        {
            heap.TryPeek(out _, out double minScore);
            if (score > minScore)
            {
                heap.DequeueEnqueue(result, score);
            }
        }
    }

    // ── BM25 scoring ─────────────────────────────────────────────────────────

    private static double ComputeBm25(
        IReadOnlyList<string> queryTerms,
        ArtifactEntry entry,
        double avgDocLen,
        int corpusSize,
        Dictionary<string, int> docFreqs)
    {
        if (entry.TermFrequencies is null || entry.TermFrequencies.Count == 0)
            return 0;

        int docLen = 0;
        foreach (var kvp in entry.TermFrequencies)
            docLen += kvp.Value;

        return Bm25Scorer.Score(queryTerms, entry.TermFrequencies, docLen, avgDocLen, corpusSize, docFreqs);
    }

    // ── Multi-term entry scoring ─────────────────────────────────────────────

    private static double ScoreEntry(string query, ArtifactEntry entry)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return 0;

        double total = 0;
        int matchedTerms = 0;
        foreach (string term in terms)
        {
            double termScore = ScoreEntryTerm(term, entry);
            total += termScore;
            if (termScore > 0) matchedTerms++;
        }

        // Intersection bonus: reward matching multiple query terms
        if (matchedTerms > 1)
            total += (matchedTerms - 1) * 15.0;

        return total;
    }

    private static double ScoreEntryTerm(string term, ArtifactEntry entry)
    {
        double score = 0;

        // Name matching
        if (entry.Name.Equals(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 100);
        else if (entry.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 30);
        else if (entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 20);

        // Tag matching
        if (entry.Tags is { Length: > 0 })
        {
            foreach (string tag in entry.Tags)
            {
                if (tag.Equals(term, StringComparison.OrdinalIgnoreCase))
                {
                    score = Math.Max(score, 50);
                    break;
                }

                if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 15);
            }
        }

        // Keyword matching
        if (entry.Keywords is { Length: > 0 })
        {
            foreach (string kw in entry.Keywords)
            {
                if (kw.Equals(term, StringComparison.OrdinalIgnoreCase))
                {
                    score = Math.Max(score, 40);
                    break;
                }

                if (kw.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 12);
            }
        }

        // Description contains
        if (!string.IsNullOrEmpty(entry.Description)
            && entry.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 10);

        // Content preview contains
        if (!string.IsNullOrEmpty(entry.ContentPreview)
            && entry.ContentPreview.Contains(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 5);

        return score;
    }

    // ── Multi-term topic scoring ─────────────────────────────────────────────

    private static double ScoreTopic(string query, TopicInfo topic)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return 0;

        double total = 0;
        int matchedTerms = 0;
        foreach (string term in terms)
        {
            double termScore = ScoreTopicTerm(term, topic);
            total += termScore;
            if (termScore > 0) matchedTerms++;
        }

        if (matchedTerms > 1)
            total += (matchedTerms - 1) * 15.0;

        return total;
    }

    private static double ScoreTopicTerm(string term, TopicInfo topic)
    {
        double score = 0;

        // Topic name matching
        if (topic.TopicName.Equals(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 100);
        else if (topic.TopicName.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 30);
        else if (topic.TopicName.Contains(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 20);

        // Topic tags
        if (topic.Tags is { Length: > 0 })
        {
            foreach (string tag in topic.Tags)
            {
                if (tag.Equals(term, StringComparison.OrdinalIgnoreCase))
                {
                    score = Math.Max(score, 50);
                    break;
                }

                if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 15);
            }
        }

        // Topic description
        if (!string.IsNullOrEmpty(topic.Description)
            && topic.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 10);

        // Entry names aggregate: 3+ matching entries indicates topical relevance
        int matchingEntries = topic.EntryNames.Count(n => n.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (matchingEntries >= 3)
            score = Math.Max(score, 8);

        return score;
    }

    // ── Chunk-level scoring ─────────────────────────────────────────────────

    private static double ScoreChunkEntry(
        string query,
        ChunkEntry chunk,
        ArtifactEntry parentEntry)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return 0;

        double total = 0;
        int matchedTerms = 0;
        foreach (string term in terms)
        {
            double termScore = ScoreChunkEntryTerm(term, chunk, parentEntry);
            total += termScore;
            if (termScore > 0) matchedTerms++;
        }

        if (matchedTerms > 1)
            total += (matchedTerms - 1) * 15.0;

        return total;
    }

    private static double ScoreChunkEntryTerm(
        string term,
        ChunkEntry chunk,
        ArtifactEntry parentEntry)
    {
        double score = 0;

        // Chunk keyword matching
        if (chunk.Keywords is { Length: > 0 })
        {
            foreach (string kw in chunk.Keywords)
            {
                if (kw.Equals(term, StringComparison.OrdinalIgnoreCase))
                {
                    score = Math.Max(score, 40);
                    break;
                }

                if (kw.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 12);
            }
        }

        // Chunk content preview
        if (!string.IsNullOrEmpty(chunk.ContentPreview)
            && chunk.ContentPreview.Contains(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 5);

        // Parent name matching
        if (parentEntry.Name.Equals(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 50);
        else if (parentEntry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 10);

        // Parent keywords at half weight (20 exact, 6 contains)
        if (parentEntry.Keywords is { Length: > 0 })
        {
            foreach (string kw in parentEntry.Keywords)
            {
                if (kw.Equals(term, StringComparison.OrdinalIgnoreCase))
                {
                    score = Math.Max(score, 20);
                    break;
                }

                if (kw.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 6);
            }
        }

        // Parent tags at half weight (25 exact, 8 contains)
        if (parentEntry.Tags is { Length: > 0 })
        {
            foreach (string tag in parentEntry.Tags)
            {
                if (tag.Equals(term, StringComparison.OrdinalIgnoreCase))
                {
                    score = Math.Max(score, 25);
                    break;
                }

                if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 8);
            }
        }

        return score;
    }

    private static double ComputeChunkBm25(
        IReadOnlyList<string> queryTerms,
        ChunkEntry chunk,
        double avgDocLen,
        int corpusSize,
        Dictionary<string, int> docFreqs)
    {
        if (chunk.TermFrequencies is null || chunk.TermFrequencies.Count == 0)
            return 0;

        int docLen = 0;
        foreach (var kvp in chunk.TermFrequencies)
            docLen += kvp.Value;

        return Bm25Scorer.Score(queryTerms, chunk.TermFrequencies, docLen, avgDocLen, corpusSize, docFreqs);
    }

    // ── Supplemental score lookup ─────────────────────────────────────────

    private static double GetSupplemental(
        string scope, string name, int? chunkIndex,
        IReadOnlyDictionary<string, double>? scores)
    {
        if (scores is null) return 0;
        // Check chunk-specific key first, then fall back to entry-level key
        if (chunkIndex is not null && scores.TryGetValue($"{scope}|{name}|{chunkIndex}", out var chunkScore))
            return chunkScore;
        return scores.GetValueOrDefault($"{scope}|{name}");
    }

}
