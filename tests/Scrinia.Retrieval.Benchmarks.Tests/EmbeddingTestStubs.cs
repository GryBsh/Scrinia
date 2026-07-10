using Scrinia.Core.Embeddings;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Deterministic, network-free <see cref="IEmbeddingProvider"/> stub used to exercise the
/// real embedding pipeline (VectorStore + HybridReranker) in tests without a live model.
///
/// <para>Produces an 8-dim bag-of-words vector: the text is tokenized on non-alphanumeric
/// boundaries, each token is hashed via a stable (non-randomized) FNV-1a hash into one of 8
/// bins, bin counts accumulate, and the result is L2-normalized. Same text always yields the
/// same vector within and across processes; <c>string.GetHashCode()</c> is deliberately
/// avoided because .NET randomizes it per process.</para>
/// </summary>
public sealed class StubEmbeddingProvider : IEmbeddingProvider
{
    public bool IsAvailable => true;

    public int Dimensions => 8;

    public string Signature => "stub:test";

    public void Dispose() { }

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var bins = new float[Dimensions];
        foreach (string token in Tokenize(text))
        {
            int bin = (int)(StableHash(token) % (uint)Dimensions);
            bins[bin] += 1f;
        }

        double normSq = 0;
        foreach (float b in bins) normSq += (double)b * b;
        if (normSq > 0)
        {
            double norm = Math.Sqrt(normSq);
            for (int i = 0; i < bins.Length; i++)
                bins[i] = (float)(bins[i] / norm);
        }

        return Task.FromResult<float[]?>(bins);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
        if (start >= 0)
            yield return text[start..].ToLowerInvariant();
    }

    /// <summary>Stable (process-independent) FNV-1a 32-bit hash — deliberately not
    /// <c>string.GetHashCode()</c>, which is randomized per process in .NET.</summary>
    private static uint StableHash(string s)
    {
        unchecked
        {
            const uint fnvPrime = 16777619;
            uint hash = 2166136261;
            foreach (char c in s)
            {
                hash ^= c;
                hash *= fnvPrime;
            }
            return hash;
        }
    }
}
