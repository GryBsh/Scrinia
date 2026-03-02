namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Abstract base for memory system benchmark implementations.
/// Each system tracks token consumption (chars/4) for fair comparison.
/// </summary>
internal abstract class MemorySystemBase : IAsyncDisposable
{
    /// <summary>Total tokens charged so far (chars / 4).</summary>
    public int TokensConsumed { get; protected set; }

    /// <summary>Ingest a corpus of facts into the memory system.</summary>
    public abstract Task SetupAsync(IReadOnlyList<BenchmarkFact> corpus);

    /// <summary>
    /// Query the system and return results. If <paramref name="targetFactKey"/> is specified,
    /// <see cref="QueryResult.FoundTarget"/> indicates whether that fact was found.
    /// </summary>
    public abstract Task<QueryResult> QueryAsync(string query, string? targetFactKey = null);

    /// <summary>Tokens always loaded before any query (cold-start cost).</summary>
    public abstract int GetColdStartTokens();

    /// <summary>Total tokens if the entire corpus were loaded at once.</summary>
    public abstract int GetTotalCorpusTokens();

    /// <summary>Overwrite a fact with updated content.</summary>
    public abstract Task UpdateFactAsync(BenchmarkFact updated);

    /// <summary>Reset token budget between benchmark iterations.</summary>
    public void ResetBudget() => TokensConsumed = 0;

    protected static int CharsToTokens(int chars) => chars / 4;
    protected static int CharsToTokens(long chars) => (int)(chars / 4);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
