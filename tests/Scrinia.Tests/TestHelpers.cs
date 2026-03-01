using System.Reflection;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Shared factory methods and constants used across the test suite.
/// </summary>
internal static class TestHelpers
{
    // ── Embedded resource ─────────────────────────────────────────────────────

    /// <summary>
    /// Loads the embedded facts.txt resource and returns its full text.
    /// </summary>
    public static string LoadFactsText() =>
        LoadEmbedded("Scrinia.Tests.TestData.facts.txt");

    /// <summary>
    /// Returns the first N bytes of facts.txt as UTF-8.
    /// </summary>
    public static byte[] LoadFactsBytes(int maxBytes = int.MaxValue)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(LoadFactsText());
        return maxBytes >= bytes.Length ? bytes : bytes[..maxBytes];
    }

    /// <summary>Loads humaneval_sample.txt (MIT, Chen et al. 2021 — HumanEval problems 0–19).</summary>
    public static string LoadHumanEvalText() =>
        LoadEmbedded("Scrinia.Tests.TestData.humaneval_sample.txt");

    /// <summary>Returns the first N bytes of humaneval_sample.txt as UTF-8.</summary>
    public static byte[] LoadHumanEvalBytes(int maxBytes = int.MaxValue)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(LoadHumanEvalText());
        return maxBytes >= bytes.Length ? bytes : bytes[..maxBytes];
    }

    public static string LoadGsm8kText() =>
        LoadEmbedded("Scrinia.Tests.TestData.gsm8k_sample.txt");

    public static string LoadInfiniteBenchText() =>
        LoadEmbedded("Scrinia.Tests.TestData.infinitebench_qa.txt");

    public static string LoadMmluText() =>
        LoadEmbedded("Scrinia.Tests.TestData.mmlu_sample.txt");

    public static string LoadQualityArticleText() =>
        LoadEmbedded("Scrinia.Tests.TestData.quality_article.txt");

    /// <summary>
    /// Returns all test data files as (name, content) pairs for benchmark iteration.
    /// </summary>
    public static IReadOnlyList<(string Name, string Content)> AllTestDataFiles() =>
    [
        ("facts.txt", LoadFactsText()),
        ("humaneval_sample.txt", LoadHumanEvalText()),
        ("gsm8k_sample.txt", LoadGsm8kText()),
        ("infinitebench_qa.txt", LoadInfiniteBenchText()),
        ("mmlu_sample.txt", LoadMmluText()),
        ("quality_article.txt", LoadQualityArticleText()),
    ];

    private static string LoadEmbedded(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. " +
                $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── Store isolation ───────────────────────────────────────────────────────

    /// <summary>
    /// Temporarily redirects <see cref="ScriniaArtifactStore.StoreDir"/> to a fresh temp
    /// directory, isolating tests from the real user store. Restored on dispose.
    /// <see cref="WorkspaceDir"/> is the workspace root; <see cref="TempDir"/> is the flat local store.
    /// Local topics resolve to <c>{WorkspaceDir}/.scrinia/topics/topic/</c>.
    /// </summary>
    internal sealed class StoreScope : IDisposable
    {
        public string WorkspaceDir { get; }
        public string TempDir { get; }

        public StoreScope()
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), $"scrinia_test_{Guid.NewGuid():N}");
            TempDir = Path.Combine(WorkspaceDir, ".scrinia", "store");
            Directory.CreateDirectory(TempDir);
            ScriniaArtifactStore.OverrideWorkspaceRoot(WorkspaceDir);
            ScriniaArtifactStore.OverrideStoreDir(TempDir);
            ScriniaArtifactStore.OverrideEphemeralStore(
                new System.Collections.Concurrent.ConcurrentDictionary<string, EphemeralEntry>(
                    StringComparer.OrdinalIgnoreCase));
            SessionBudget.OverrideStore(
                new System.Collections.Concurrent.ConcurrentDictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase));
            MemoryStoreContext.Current = new FileMemoryStore(WorkspaceDir);
        }

        public void Dispose()
        {
            ScriniaArtifactStore.OverrideWorkspaceRoot(null);
            ScriniaArtifactStore.OverrideStoreDir(null);
            ScriniaArtifactStore.OverrideEphemeralStore(null);
            SessionBudget.OverrideStore(null);
            MemoryStoreContext.Current = null;
            try { Directory.Delete(WorkspaceDir, recursive: true); } catch { }
        }
    }

    // ── Byte helpers ──────────────────────────────────────────────────────────

    public static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    public static string FromUtf8(byte[] b) => System.Text.Encoding.UTF8.GetString(b);

    /// <summary>
    /// Known-good facts from the embedded file — pulled verbatim for use as test fixtures.
    /// </summary>
    public static class Facts
    {
        public const string Fact1  = "The human brain contains approximately 86 billion neurons, each connected to thousands of others, forming a network more complex than any computer ever built.";
        public const string Fact5  = "A teaspoon of neutron star material would weigh about 10 million tons on Earth due to its incredible density.";
        public const string Fact13 = "There are more possible iterations of a game of chess than there are atoms in the observable universe.";
        public const string Fact31 = "Cleopatra lived closer in time to the Moon landing than to the construction of the Great Pyramid of Giza.";
        public const string Fact50 = "Marie Curie is the only person to have won Nobel Prizes in two different sciences, Physics in 1903 and Chemistry in 1911.";

        /// <summary>A small multi-line excerpt with ASCII, digits, and punctuation.</summary>
        public const string Excerpt =
            "Fact #000001: The human brain contains approximately 86 billion neurons.\n" +
            "Fact #000002: Light travels at approximately 299,792,458 meters per second.\n" +
            "Fact #000003: DNA is so tightly coiled that if you stretched it out it would be 2 meters long.";
    }
}
