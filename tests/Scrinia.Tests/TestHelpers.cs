using System.Reflection;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using YamlDotNet.Serialization;

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

    /// <summary>The ambient store, narrowed to the local contract. StoreScope always installs a
    /// FileMemoryStore, so this is total inside a StoreScope. Exists so that tests reaching a
    /// path member do so through one named helper rather than ~20 inline casts.</summary>
    internal static ILocalMemoryStore LocalStore =>
        MemoryStoreContext.Current as ILocalMemoryStore
        ?? throw new InvalidOperationException(
            "No local store is installed. Is this test inside a StoreScope?");

    /// <summary>
    /// Builds an isolated <see cref="FileMemoryStore"/> over a fresh temp directory and installs
    /// it as <see cref="MemoryStoreContext.Current"/>, isolating tests from the real user store.
    /// Restored on dispose. <see cref="WorkspaceDir"/> is the workspace root; <see cref="TempDir"/>
    /// is the flat local store — <c>.scrinia/memories/</c> under the v3 layout (PathRouter.ToStoreDir
    /// routes the bare "local" scope there; it also doubles as the root every topic/path scope
    /// nests under, e.g. <c>{WorkspaceDir}/.scrinia/memories/topic/</c>).
    /// </summary>
    internal sealed class StoreScope : IDisposable
    {
        public string WorkspaceDir { get; }
        public string TempDir { get; }

        public StoreScope(bool createStoreDir = true)
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), $"scrinia_test_{Guid.NewGuid():N}");
            TempDir = Path.Combine(WorkspaceDir, ".scrinia", "memories");
            if (createStoreDir)
                Directory.CreateDirectory(TempDir);
            SessionBudget.OverrideStore(
                new System.Collections.Concurrent.ConcurrentDictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase));

            // Clean room, via the AsyncLocal cell rather than the process-wide one.
            //
            // The need is unchanged: a real embed/entity-typing/maintenance pipeline left installed
            // fires on every later test's writes and rewrites their entries mid-assertion — this is
            // what made MentionSpanWiringTests flaky.
            //
            // This USED to be `Default = null`, and that was itself a race. Default is a
            // process-wide static that production depends on; xUnit runs test classes in parallel;
            // so this line — and the matching one in Dispose — could null the value another test
            // was mid-way through asserting on. That is the intermittent failure in
            // MemoryEventSinkContextInstallDefaultTests.InstallDefault_ComposesSinksInOrder
            // (~1 full run in 5).
            //
            // Current is AsyncLocal and its getter is `_current.Value ?? _default`, so assigning a
            // non-null sentinel SHADOWS Default for this test's own async flow and touches nothing
            // process-wide. Null would not shadow — it falls straight back to Default — which is
            // why SilentEventSink exists. Tests that want a real sink keep overwriting Current, as
            // they always did.
            //
            // Safe because this constructor is SYNCHRONOUS and called from the test body: the write
            // lands in the test's own ExecutionContext and survives its awaits (measured). It would
            // NOT survive if this were an `async` method — see SilentEventSink's doc.
            MemoryEventSinkContext.Current = TestDoubles.SilentEventSink.Instance;
            MemoryStoreContext.Current = new FileMemoryStore(WorkspaceDir);
        }

        public void Dispose()
        {
            // Deliberately does NOT drain BackgroundWork. The registry is process-wide, so a
            // per-test drain makes every disposal wait on every other test's in-flight sinks —
            // and CoreEmbeddingEventHandler's VectorWriteRetry backs off up to 42s. Blocking on
            // that from hundreds of parallel disposals starves the thread pool and takes the
            // suite from ~52s to over 10 minutes (measured). Clearing the sink context in the
            // constructor is what actually prevents the leak; TestHostLifetime drains once at
            // the end of the run.
            SessionBudget.OverrideStore(null);
            // Releases this flow's shadow so a later scope in the same flow starts from Default
            // again. Per-flow, so it cannot disturb a test running in parallel — which the old
            // `Default = null` here could, and did.
            MemoryEventSinkContext.Current = null;
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

/// <summary>
/// Deserializes YAML tool responses (produced by <see cref="McpResponseExtensions.ToYaml"/>)
/// into an <see cref="McpResponseAssert"/> for easy assertion in tests.
/// </summary>
internal static class ResponseParser
{
    private static readonly IDeserializer Deserializer =
        new DeserializerBuilder().Build();

    public static McpResponseAssert Parse(string yamlResponse)
    {
        var dict = Deserializer.Deserialize<Dictionary<string, object>>(yamlResponse)
            ?? throw new InvalidOperationException("YAML deserialized to null");
        return new McpResponseAssert(dict);
    }
}

/// <summary>
/// Lightweight wrapper over a deserialized MCP YAML response dictionary,
/// exposing typed accessors for each standard field.
/// </summary>
internal sealed class McpResponseAssert
{
    private readonly Dictionary<string, object> _data;

    public McpResponseAssert(Dictionary<string, object> data) => _data = data;

    public string Status => GetString("status") ?? "";
    public string? Action => GetString("action");
    public string? Path => GetString("path");
    public string? Instruction => GetString("instruction");
    public string? Error => GetString("error");
    public string? ErrorCode => GetString("errorCode");
    public string? Content => GetString("content");
    public List<string> ActionNeeded => GetList("actionNeeded");
    public List<string> Info => GetList("info");
    public List<string> FollowUp => GetList("followUp");

    private string? GetString(string key) =>
        _data.GetValueOrDefault(key)?.ToString();

    private List<string> GetList(string key)
    {
        if (_data.GetValueOrDefault(key) is List<object> list)
            return list.Select(x => x.ToString()!).ToList();
        return new List<string>();
    }
}
