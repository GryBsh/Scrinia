namespace Scrinia.Core.Search;

/// <summary>
/// BM25 scoring over pre-computed term frequencies stored in artifact entries.
/// </summary>
internal static class Bm25Scorer
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    /// <summary>
    /// Computes BM25 score for a single document against the given query terms.
    /// </summary>
    /// <param name="queryTerms">Tokenized, lowercased query terms (no stop words).</param>
    /// <param name="entryTf">Term frequencies for this document.</param>
    /// <param name="entryDocLength">Total token count of the document.</param>
    /// <param name="avgDocLength">Average document length across the corpus.</param>
    /// <param name="corpusSize">Total number of documents in the corpus.</param>
    /// <param name="documentFrequencies">Number of documents containing each term.</param>
    public static double Score(
        IReadOnlyList<string> queryTerms,
        IReadOnlyDictionary<string, int> entryTf,
        int entryDocLength,
        double avgDocLength,
        int corpusSize,
        IReadOnlyDictionary<string, int> documentFrequencies)
    {
        if (queryTerms.Count == 0 || entryTf.Count == 0 || corpusSize == 0)
            return 0;

        double score = 0;
        int n = corpusSize;

        foreach (string term in queryTerms)
        {
            if (!entryTf.TryGetValue(term, out int tf) || tf == 0)
                continue;

            documentFrequencies.TryGetValue(term, out int df);
            if (df == 0) df = 1; // safety: if term appears in TF, df should be >= 1

            // IDF: ln((N - n + 0.5) / (n + 0.5) + 1)
            double idf = Math.Log((n - df + 0.5) / (df + 0.5) + 1.0);

            // TF normalization with document length
            double tfNorm = (tf * (K1 + 1.0)) /
                            (tf + K1 * (1.0 - B + B * (entryDocLength / avgDocLength)));

            score += idf * tfNorm;
        }

        return score;
    }

    /// <summary>
    /// Computes corpus statistics from a collection of entry term frequencies.
    /// Returns (avgDocLength, documentFrequencies).
    /// </summary>
    public static (double AvgDocLength, Dictionary<string, int> DocumentFrequencies)
        ComputeCorpusStats(IEnumerable<IReadOnlyDictionary<string, int>?> allTfs)
    {
        var docFreqs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        int docCount = 0;

        foreach (var tf in allTfs)
        {
            if (tf is null || tf.Count == 0) continue;
            docCount++;
            int docLen = 0;
            foreach (var kvp in tf)
            {
                docLen += kvp.Value;
                docFreqs.TryGetValue(kvp.Key, out int count);
                docFreqs[kvp.Key] = count + 1;
            }
            totalLength += docLen;
        }

        double avgDocLen = docCount > 0 ? (double)totalLength / docCount : 0;
        return (avgDocLen, docFreqs);
    }
}
