using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>
/// Linear-scan weighted attribute scorer with multi-term support and BM25 content search.
///
/// Multi-term queries split on whitespace; each term is scored independently
/// and per-term max scores are summed. This ensures "DI patterns" ranks entries
/// matching both terms higher than entries matching only one.
///
/// Hybrid scoring: finalScore = weightedAttributeScore + bm25Score * 5.0
///
/// Entry scoring (per term):
///
/// Match type                    Score
/// ─────────────────────         ─────
/// Exact name match               100
/// Attribute key:value pair exact  50
/// Keyword exact match             40
/// Attribute value exact match     40
/// Name starts with                30
/// Name contains                   20
/// Keyword contains                12
/// Attribute value contains        12
/// Content preview contains         5
///
/// Tags, retrieval anchors, and description are no longer scored directly — read-time
/// forward-fill (<see cref="Scrinia.Core.Migration.AttributeMigration"/>) promotes tags into
/// Attributes["tag"] and anchors into Attributes["anchor"] before scoring ever runs, so they score
/// as ordinary attribute rows above (P6.5 cleanup: Attributes is populated everywhere, so the legacy
/// direct-carrier rows and the key:value-in-Keywords dual-read were retired).
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
internal sealed class WeightedAttributeScorer
{
    private const double Bm25Weight = 5.0;

    /// <summary>
    /// Ranker composition weights. <see cref="SearchAll(string, IEnumerable{ScopedArtifact},
    /// IEnumerable{TopicInfo}, int, IReadOnlyDictionary{string, double}?)"/> combines
    /// relevance (attribute + BM25 + supplemental) with exp-decay recency additively per
    /// <see cref="RankerOptions"/>. The legacy <c>Search</c> method does not apply
    /// recency — only SearchAll does.
    /// </summary>
    private readonly RankerOptions _options;

    public WeightedAttributeScorer() : this(RankerOptions.Default) { }

    public WeightedAttributeScorer(RankerOptions options)
    {
        _options = options ?? RankerOptions.Default;
    }

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
            candidateList.Select(c => (IReadOnlyDictionary<string, int>?)c.Entry.TermFrequencies),
            docCountHint: candidateList.Count);

        var queryTerms = TextAnalysis.Tokenize(query);
        int corpusSize = candidateList.Count;

        var scored = new List<ScoredArtifact>();

        foreach (var candidate in candidateList)
        {
            double attributeScore = ScoreEntry(query, candidate.Entry);
            double bm25Score = ComputeBm25(queryTerms, candidate.Entry, avgDocLen, corpusSize, docFreqs);
            double total = attributeScore + bm25Score * Bm25Weight;
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
    /// BM25 scores are normalized to 0–100 (max-norm: raw / corpus-max) before combining with
    /// attribute scores. Max-norm (not min-max) so a single/weakest positive match still surfaces —
    /// min-max would zero out a candidate that is simultaneously the min and the max.
    /// Multi-term intersection bonus of (matchedTerms - 1) * 15 rewards queries matching multiple terms.
    /// Uses a min-heap (size K) for top-K selection instead of full sort for efficiency.
    /// </summary>
    public IReadOnlyList<SearchResult> SearchAll(
        string query,
        IEnumerable<ScopedArtifact> candidates,
        IEnumerable<TopicInfo> topics,
        int limit,
        IReadOnlyDictionary<string, double>? supplementalScores,
        IReadOnlyDictionary<string, double>? entityGraphScores = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var candidateList = candidates as IList<ScopedArtifact> ?? candidates.ToList();

        // Compute BM25 corpus stats
        var (avgDocLen, docFreqs) = Bm25Scorer.ComputeCorpusStats(
            candidateList.Select(c => (IReadOnlyDictionary<string, int>?)c.Entry.TermFrequencies),
            docCountHint: candidateList.Count);

        var queryTerms = TextAnalysis.Tokenize(query);
        int corpusSize = candidateList.Count;

        // ── Pass 1: Compute raw BM25 scores for normalization ────────────
        var entryBm25 = new double[candidateList.Count];
        var chunkBm25List = new List<(int candidateIdx, int chunkIdx, double rawBm25)>();
        double maxBm25 = 0;

        for (int i = 0; i < candidateList.Count; i++)
        {
            double raw = ComputeBm25(queryTerms, candidateList[i].Entry, avgDocLen, corpusSize, docFreqs);
            entryBm25[i] = raw;
            if (raw > maxBm25) maxBm25 = raw;

            if (candidateList[i].Entry.ChunkEntries is { Length: > 0 } chunks)
            {
                foreach (var chunk in chunks)
                {
                    double chunkRaw = ComputeChunkBm25(queryTerms, chunk, avgDocLen, corpusSize, docFreqs);
                    chunkBm25List.Add((i, chunk.ChunkIndex, chunkRaw));
                    if (chunkRaw > maxBm25) maxBm25 = chunkRaw;
                }
            }
        }

        // ── Pass 2: Combine normalized BM25 with attribute scores, deduplicate inline ──
        // Track best score per memory for deduplication during scoring. Relevance gates
        // the composition (recency is a bump, not a standalone signal) so entries with
        // zero text match never surface even if they're fresh.
        var bestPerMemory = new Dictionary<string, (SearchResult Result, double Score)>(StringComparer.OrdinalIgnoreCase);
        var topicResults = new List<SearchResult>();

        void TryInsert(ScopedArtifact candidate, double relevance, Func<double, SearchResult> resultFactory)
        {
            if (relevance <= 0) return;
            double total = ComputeTotalScore(relevance, candidate.Entry);
            string key = $"{candidate.Scope}|{candidate.Entry.Name}";
            if (!bestPerMemory.TryGetValue(key, out var existing) || total > existing.Score)
                bestPerMemory[key] = (resultFactory(total), total);
        }

        if (_options.Fusion == FusionMode.Rrf)
        {
            FuseRrf(query, candidateList, entryBm25, chunkBm25List, supplementalScores, entityGraphScores, TryInsert);
        }
        else
        {
            for (int i = 0; i < candidateList.Count; i++)
            {
                var candidate = candidateList[i];
                double attributeScore = ScoreEntry(query, candidate.Entry);
                double normalizedBm25 = maxBm25 > 0 ? entryBm25[i] / maxBm25 * 100.0 : 0;
                double supplemental = GetSupplemental(candidate.Scope, candidate.Entry.Name, null, supplementalScores);
                // Entity-graph PPR is its own additive source (keyed at entry level), not folded
                // into supplemental — so it stays a distinct signal under any fusion mode.
                double entityGraph = GetSupplemental(candidate.Scope, candidate.Entry.Name, null, entityGraphScores);
                TryInsert(candidate, attributeScore + normalizedBm25 * Bm25Weight + supplemental + entityGraph,
                    total => new EntryResult(candidate, total));
            }

            foreach (var (candidateIdx, chunkIdx, rawBm25) in chunkBm25List)
            {
                var candidate = candidateList[candidateIdx];
                var chunk = candidate.Entry.ChunkEntries!.First(c => c.ChunkIndex == chunkIdx);
                double chunkAttributeScore = ScoreChunkEntry(query, chunk, candidate.Entry);
                double normalizedChunkBm25 = maxBm25 > 0 ? rawBm25 / maxBm25 * 100.0 : 0;
                double chunkSupplemental = GetSupplemental(candidate.Scope, candidate.Entry.Name, chunkIdx, supplementalScores);
                // Entity scores are entry-level; reuse the parent's value for chunk cells.
                double chunkEntityGraph = GetSupplemental(candidate.Scope, candidate.Entry.Name, null, entityGraphScores);
                TryInsert(candidate, chunkAttributeScore + normalizedChunkBm25 * Bm25Weight + chunkSupplemental + chunkEntityGraph,
                    total => new ChunkEntryResult(candidate, chunk, candidate.Entry.ChunkCount, total));
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

        // Attribute matching. Compound "key:value" query terms hitting a (key, value)
        // pair score 50 (tag-exact parity); a term equal to a value scores 40 (keyword-exact
        // parity); contained in a value, 12. Explicit OrdinalIgnoreCase everywhere: dictionary
        // comparers don't survive JSON round-trips. Attributes is now populated everywhere (read-time
        // forward-fill promotes legacy Tags/RetrievalAnchors/key:value-shaped Keywords into Attributes
        // before this ever runs — see AttributeMigration.Forward), so this is the sole tag/keyword
        // -pair scorer; the old direct key:value-in-Keywords parse here was retired in P6.5.
        int sep = term.IndexOf(':');
        string? termKey = sep > 0 && sep < term.Length - 1 ? term[..sep] : null;
        string? termValue = termKey is not null ? term[(sep + 1)..] : null;

        if (entry.Attributes is { Count: > 0 })
        {
            foreach (var attr in entry.Attributes)
            {
                double w = KnownAttributes.AttributeWeight(attr.Key);   // curation knob; 1.0 at parity
                foreach (string value in attr.Value)
                {
                    if (termKey is not null
                        && attr.Key.Equals(termKey, StringComparison.OrdinalIgnoreCase)
                        && value.Equals(termValue, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 50 * w);
                    else if (value.Equals(term, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 40 * w);
                    else if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 12 * w);
                }
            }
        }

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

        // Parent tags at half weight (20 exact, 6 contains) — sourced from Attributes["tag"] (Tags->Attributes cutover)
        if (parentEntry.Attributes is { } pf && pf.TryGetValue(KnownAttributes.TagKey, out var parentTags) && parentTags is not null)
        {
            foreach (string tag in parentTags)
            {
                if (tag.Equals(term, StringComparison.OrdinalIgnoreCase))
                {
                    score = Math.Max(score, 20);
                    break;
                }

                if (tag.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 6);
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

    // ── Reciprocal Rank Fusion ──────────────────────────────────────────────

    /// <summary>
    /// RRF k constant. The standard value from Cormack/Clarke/Buettcher (2009);
    /// dampens contributions from low-ranked items so the top of each source's list
    /// dominates the fused score. Larger k → flatter fusion; smaller k → top-heavy.
    /// </summary>
    private const double RrfK = 60.0;

    /// <summary>
    /// Reciprocal Rank Fusion across the retrieval signals — attribute-match score, raw BM25,
    /// supplemental (embedding) score, and the entity-graph PPR score. Builds a unified cell
    /// list across entry-level and chunk-level candidates, ranks each cell independently within
    /// each source, then sums <c>1/(k + rank)</c> contributions. Cells with zero score in a
    /// source contribute nothing from that source — the standard "not in this source's results"
    /// semantics, so an empty entity-graph dict leaves the existing three-source fusion
    /// unchanged. Each cell is then passed to the shared <paramref name="tryInsert"/> so recency
    /// composition and dedup happen identically to the sum path.
    /// </summary>
    private void FuseRrf(
        string query,
        IList<ScopedArtifact> candidateList,
        double[] entryBm25,
        List<(int candidateIdx, int chunkIdx, double rawBm25)> chunkBm25List,
        IReadOnlyDictionary<string, double>? supplementalScores,
        IReadOnlyDictionary<string, double>? entityGraphScores,
        Action<ScopedArtifact, double, Func<double, SearchResult>> tryInsert)
    {
        int cellCount = candidateList.Count + chunkBm25List.Count;
        if (cellCount == 0) return;

        // Cell layout: entries first [0..candidateList.Count), then chunks. All signals are
        // computed eagerly because we need them to build per-source rankings before any single
        // cell can be fused. Entity-graph scores are entry-level (keyed "{scope}|{name}"), so
        // chunk cells reuse their parent's value.
        var attributeScores = new double[cellCount];
        var bm25Scores = new double[cellCount];
        var supplementals = new double[cellCount];
        var entityScores = new double[cellCount];

        for (int i = 0; i < candidateList.Count; i++)
        {
            var candidate = candidateList[i];
            attributeScores[i] = ScoreEntry(query, candidate.Entry);
            bm25Scores[i] = entryBm25[i];
            supplementals[i] = GetSupplemental(candidate.Scope, candidate.Entry.Name, null, supplementalScores);
            entityScores[i] = GetSupplemental(candidate.Scope, candidate.Entry.Name, null, entityGraphScores);
        }

        for (int ci = 0; ci < chunkBm25List.Count; ci++)
        {
            int idx = candidateList.Count + ci;
            var (candidateIdx, chunkIdx, rawBm25) = chunkBm25List[ci];
            var candidate = candidateList[candidateIdx];
            var chunk = candidate.Entry.ChunkEntries!.First(c => c.ChunkIndex == chunkIdx);
            attributeScores[idx] = ScoreChunkEntry(query, chunk, candidate.Entry);
            bm25Scores[idx] = rawBm25;
            supplementals[idx] = GetSupplemental(candidate.Scope, candidate.Entry.Name, chunkIdx, supplementalScores);
            entityScores[idx] = GetSupplemental(candidate.Scope, candidate.Entry.Name, null, entityGraphScores);
        }

        int[] rankAttribute = ComputeRanks(attributeScores);
        int[] rankBm25 = ComputeRanks(bm25Scores);
        int[] rankSupp = ComputeRanks(supplementals);
        int[] rankEntity = ComputeRanks(entityScores);

        for (int ci = 0; ci < chunkBm25List.Count; ci++)
        {
            int idx = candidateList.Count + ci;
            var (candidateIdx, chunkIdx, _) = chunkBm25List[ci];
            var candidate = candidateList[candidateIdx];
            var chunk = candidate.Entry.ChunkEntries!.First(c => c.ChunkIndex == chunkIdx);
            double rrf = RrfScore(attributeScores[idx], bm25Scores[idx], supplementals[idx], entityScores[idx],
                rankAttribute[idx], rankBm25[idx], rankSupp[idx], rankEntity[idx]);
            if (rrf <= 0) continue;
            tryInsert(candidate, rrf, total => new ChunkEntryResult(candidate, chunk, candidate.Entry.ChunkCount, total));
        }

        for (int i = 0; i < candidateList.Count; i++)
        {
            double rrf = RrfScore(attributeScores[i], bm25Scores[i], supplementals[i], entityScores[i],
                rankAttribute[i], rankBm25[i], rankSupp[i], rankEntity[i]);
            if (rrf <= 0) continue;
            var candidate = candidateList[i];
            tryInsert(candidate, rrf, total => new EntryResult(candidate, total));
        }
    }

    private static double RrfScore(
        double attributeScore, double bm25, double supplemental, double entityGraph,
        int rankAttribute, int rankBm25, int rankSupp, int rankEntity)
    {
        double rrf = 0;
        if (attributeScore > 0) rrf += 1.0 / (RrfK + rankAttribute);
        if (bm25 > 0) rrf += 1.0 / (RrfK + rankBm25);
        if (supplemental > 0) rrf += 1.0 / (RrfK + rankSupp);
        if (entityGraph > 0) rrf += 1.0 / (RrfK + rankEntity);
        return rrf;
    }

    /// <summary>
    /// 1-based descending rank within the score array. Equal scores get sequential ranks
    /// (no tied-rank handling) — RRF's k=60 dampens the noise from arbitrary tie ordering.
    /// </summary>
    private static int[] ComputeRanks(double[] scores)
    {
        int n = scores.Length;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => scores[b].CompareTo(scores[a]));
        var ranks = new int[n];
        for (int r = 0; r < n; r++) ranks[order[r]] = r + 1;
        return ranks;
    }

    // ── Ranker composition ───────────────────────────────────────────────

    /// <summary>
    /// Combines relevance with the recency term per <see cref="RankerOptions"/>.
    /// Relevance is presumed to already be &gt; 0 — callers gate on that before invoking this.
    /// </summary>
    private double ComputeTotalScore(double relevance, ArtifactEntry entry)
    {
        double recency = ComputeRecencyTerm(entry.UpdatedAt ?? entry.CreatedAt, _options.TauDays);
        return _options.AlphaRelevance * relevance
             + _options.AlphaRecency   * recency * _options.RecencyScale;
    }

    /// <summary>
    /// Exponential decay: returns 1.0 at Δt = 0, ~0.368 at Δt = τ, asymptotes to 0 for
    /// older entries. Future-dated entries (clock skew across sync setups) clamp to "today".
    /// </summary>
    internal static double ComputeRecencyTerm(DateTimeOffset lastUpdate, double tauDays)
    {
        double daysSince = (DateTimeOffset.UtcNow - lastUpdate).TotalDays;
        if (daysSince < 0) daysSince = 0;
        if (tauDays <= 0) return 0; // pathological config — treat all entries as fully decayed
        return Math.Exp(-daysSince / tauDays);
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
