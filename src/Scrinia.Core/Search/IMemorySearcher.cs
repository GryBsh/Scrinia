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

        var results = new List<SearchResult>();

        foreach (var candidate in candidateList)
        {
            double fieldScore = ScoreEntry(query, candidate.Entry);
            double bm25Score = ComputeBm25(queryTerms, candidate.Entry, avgDocLen, corpusSize, docFreqs);
            double supplemental = GetSupplemental(candidate.Scope, candidate.Entry.Name, null, supplementalScores);
            double total = fieldScore + bm25Score * Bm25Weight + supplemental;
            if (total > 0)
                results.Add(new EntryResult(candidate, total));
        }

        // ── Chunk-level scoring ─────────────────────────────────────────
        foreach (var candidate in candidateList)
        {
            if (candidate.Entry.ChunkEntries is not { Length: > 0 } chunkEntries)
                continue;
            foreach (var chunk in chunkEntries)
            {
                double chunkFieldScore = ScoreChunkEntry(query, chunk, candidate.Entry);
                double chunkBm25 = ComputeChunkBm25(queryTerms, chunk, avgDocLen, corpusSize, docFreqs);
                double chunkSupplemental = GetSupplemental(candidate.Scope, candidate.Entry.Name, chunk.ChunkIndex, supplementalScores);
                double chunkTotal = chunkFieldScore + chunkBm25 * Bm25Weight + chunkSupplemental;
                if (chunkTotal > 0)
                    results.Add(new ChunkEntryResult(candidate, chunk, candidate.Entry.ChunkCount, chunkTotal));
            }
        }

        foreach (var topic in topics)
        {
            double score = ScoreTopic(query, topic);
            if (score > 0)
                results.Add(new TopicResult(topic.Scope, topic.TopicName, topic.Description, topic.EntryCount, topic.Tags, score));
        }

        // Deduplicate: keep only the highest-scoring result per memory
        results = DeduplicateResults(results);

        results.Sort((a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0) return cmp;

            // Break ties: entries before topics, then by creation date
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

        return results.Take(Math.Max(1, limit)).ToList();
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
        foreach (string term in terms)
            total += ScoreEntryTerm(term, entry);
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
        foreach (string term in terms)
            total += ScoreTopicTerm(term, topic);
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
        foreach (string term in terms)
            total += ScoreChunkEntryTerm(term, chunk, parentEntry);
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

        // Parent name at half weight
        if (parentEntry.Name.Equals(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 50);
        else if (parentEntry.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 10);

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

    /// <summary>
    /// Deduplicates results: for each memory (scope+name), keeps only the highest-scoring
    /// result (entry or chunk). Topic results pass through unaffected.
    /// </summary>
    private static List<SearchResult> DeduplicateResults(List<SearchResult> results)
    {
        // Group by (scope, name) and keep the best result per memory
        var bestByMemory = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        var passThrough = new List<SearchResult>();

        foreach (var result in results)
        {
            string? key = result switch
            {
                EntryResult er => $"{er.Item.Scope}|{er.Item.Entry.Name}",
                ChunkEntryResult cr => $"{cr.ParentItem.Scope}|{cr.ParentItem.Entry.Name}",
                _ => null,
            };

            if (key is null)
            {
                passThrough.Add(result);
                continue;
            }

            if (!bestByMemory.TryGetValue(key, out var existing) || result.Score > existing.Score)
                bestByMemory[key] = result;
        }

        var deduped = new List<SearchResult>(bestByMemory.Count + passThrough.Count);
        deduped.AddRange(bestByMemory.Values);
        deduped.AddRange(passThrough);
        return deduped;
    }
}
