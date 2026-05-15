namespace Scrinia.Core.Embeddings;

/// <summary>
/// Slices arbitrary text into overlapping fixed-size windows for chunked embedding. Each
/// window becomes one vector in the store, keyed by <c>chunkIndex</c>; search-time dedup by
/// memory name (see <see cref="Search.IMemorySearcher"/>) collapses chunk matches back to a
/// single result per memory while letting the score come from whichever window matched best.
///
/// <para>Char-based rather than token-aware: avoids a tokenizer dependency and produces
/// behavior that's deterministic across embedding providers. 1200 chars maps to roughly
/// 300 tokens for English prose — comfortably inside every supported provider's context
/// window (smallest is nomic-embed-text at 2048 tokens). The default 200-char overlap
/// ensures a needle that lands at a window boundary is still represented intact in the
/// adjacent window.</para>
/// </summary>
public static class TextChunker
{
    public const int DefaultWindowSize = 1200;
    public const int DefaultOverlap = 200;

    /// <summary>
    /// Slice <paramref name="text"/> into overlapping windows of <paramref name="windowSize"/>
    /// chars that share <paramref name="overlap"/> chars at each boundary. Returns one chunk
    /// <c>(0, text)</c> for short inputs so we don't pay for slicing when there's nothing to
    /// slice. Window boundaries are trimmed of leading/trailing whitespace to avoid embedding
    /// pure indent or trailing newline noise.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="windowSize"/> &lt;= <paramref name="overlap"/> (the step would
    /// be zero or negative, producing an infinite loop) or either value is non-positive.
    /// </exception>
    public static IReadOnlyList<(int Index, string Text)> SliceWindows(
        string text, int windowSize = DefaultWindowSize, int overlap = DefaultOverlap)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (windowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Must be positive.");
        if (overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(overlap), overlap, "Must be non-negative.");
        if (overlap >= windowSize)
            throw new ArgumentOutOfRangeException(nameof(overlap), overlap,
                $"Overlap ({overlap}) must be smaller than windowSize ({windowSize}); otherwise the sliding step is zero.");

        if (text.Length == 0)
            return [];

        if (text.Length <= windowSize)
        {
            string trimmed = text.Trim();
            return trimmed.Length == 0 ? [] : [(0, trimmed)];
        }

        int step = windowSize - overlap;
        var windows = new List<(int Index, string Text)>(capacity: (text.Length / step) + 1);
        int idx = 0;

        for (int start = 0; start < text.Length; start += step)
        {
            int end = Math.Min(start + windowSize, text.Length);
            string slice = text[start..end].Trim();
            if (slice.Length > 0)
                windows.Add((idx++, slice));

            if (end >= text.Length)
                break;
        }

        return windows;
    }
}
