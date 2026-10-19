using System.Linq;

namespace Scrinia.Core.Configuration;

/// <summary>
/// The single deterministic authority for an authoritative mutation manifest (§7.2). Every successful
/// shared mutation reports the exact set of authoritative files it created, modified, or deleted; this
/// factory reduces that set to the canonical manifest form so staging guidance and fingerprints never
/// depend on caller ordering, separators, duplicates, or stray derived paths.
/// </summary>
public static class SharedManifest
{
    private const string StorePrefix = ".scrinia/";

    /// <summary>
    /// Normalizes the authoritative file paths a mutation changed into a manifest: each entry is made
    /// relative to the registered repository root, `/`-separated, sorted by UTF-8 ordinal bytes, and
    /// deduplicated, retaining only exact authoritative files. Directories, wildcards, and every path
    /// the durable classifier marks as derived are dropped so no control, derived, or directory
    /// pathspec can ever enter staging guidance.
    /// </summary>
    /// <param name="changedAuthoritativePaths">The exact authoritative files the mutation touched, either store-relative or already repository-root-relative.</param>
    /// <returns>The canonical, sorted, deduplicated, repository-root-relative manifest; empty for a no-op.</returns>
    public static string[] Create(IEnumerable<string> changedAuthoritativePaths)
    {
        ArgumentNullException.ThrowIfNull(changedAuthoritativePaths);

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in changedAuthoritativePaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Path.IsPathFullyQualified(raw) || raw.EndsWith('/') || raw.EndsWith('\\') || raw.Any(char.IsControl)) continue;

            string storeRelative = raw.Replace('\\', '/').Trim('/');
            if (storeRelative.Length == 0) continue;
            if (storeRelative.Contains('*') || storeRelative.Contains('?')) continue; // no wildcard pathspec

            // The durable classifier classifies store-relative paths, so resolve any repository-root
            // prefix to the store-relative form first; a path supplied already rooted at the repository
            // still classifies against the derived rules and is re-rooted identically below.
            string classifyForm = storeRelative.StartsWith(StorePrefix, StringComparison.Ordinal)
                ? storeRelative[StorePrefix.Length..]
                : storeRelative;
            if (classifyForm.Split('/').Any(part => part is "" or "." or "..") || classifyForm.Contains(':')) continue;
            if (!SharedBaseRuntime.IsAuthoritative(classifyForm)) continue;
            if (!classifyForm.EndsWith(".nmp2", StringComparison.Ordinal) &&
                !classifyForm.EndsWith(".md", StringComparison.Ordinal) &&
                !classifyForm.EndsWith(".meta.json", StringComparison.Ordinal)) continue;
            if (classifyForm.Length == 0 || SharedClassifier.IsExcludedFromFingerprint(classifyForm)) continue; // derived, not authoritative

            string rootRelative = StorePrefix + classifyForm;

            unique.Add(rootRelative);
        }

        return unique
            .OrderBy(path => System.Text.Encoding.UTF8.GetBytes(path), Utf8ByteArrayComparer.Instance)
            .ToArray();
    }

    private sealed class Utf8ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly Utf8ByteArrayComparer Instance = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            byte[] leftBytes = left ?? Array.Empty<byte>();
            byte[] rightBytes = right ?? Array.Empty<byte>();
            int length = Math.Min(leftBytes.Length, rightBytes.Length);
            for (int index = 0; index < length; index++)
            {
                int difference = leftBytes[index] - rightBytes[index];
                if (difference != 0) return difference;
            }
            return leftBytes.Length - rightBytes.Length;
        }
    }
}
