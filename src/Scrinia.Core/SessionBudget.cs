using System.Collections.Concurrent;

namespace Scrinia.Core;

/// <summary>
/// Tracks estimated token consumption for the current session.
/// Records chars loaded via show() and get_chunk(), with per-memory breakdown.
/// </summary>
internal static class SessionBudget
{
    private static readonly ConcurrentDictionary<string, long> _globalStore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<ConcurrentDictionary<string, long>?> _storeOverride = new();

    private static ConcurrentDictionary<string, long> Store =>
        _storeOverride.Value ?? _globalStore;

    /// <summary>Records chars loaded for a memory name. Accumulates across multiple accesses.</summary>
    public static void RecordAccess(string memoryName, long charsLoaded)
    {
        Store.AddOrUpdate(memoryName, charsLoaded, (_, existing) => existing + charsLoaded);
    }

    /// <summary>Total chars loaded across all memories this session.</summary>
    public static long TotalCharsLoaded
    {
        get
        {
            long total = 0;
            foreach (var kvp in Store)
                total += kvp.Value;
            return total;
        }
    }

    /// <summary>Rough token estimate: chars / 4.</summary>
    public static int EstimatedTokensLoaded => (int)(TotalCharsLoaded / 4);

    /// <summary>Per-memory breakdown of chars and estimated tokens loaded.</summary>
    public static IReadOnlyDictionary<string, (long Chars, int EstTokens)> Breakdown
    {
        get
        {
            var result = new Dictionary<string, (long, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in Store)
                result[kvp.Key] = (kvp.Value, (int)(kvp.Value / 4));
            return result;
        }
    }

    /// <summary>Override the backing store for test isolation (AsyncLocal).</summary>
    internal static void OverrideStore(ConcurrentDictionary<string, long>? store) =>
        _storeOverride.Value = store;
}
