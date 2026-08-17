namespace Scrinia.Core.Embeddings;

/// <summary>Configuration POCO for embedding providers.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>Provider: "model2vec", "ollama", "openai", "voyageai", "azure", "google", or "none".</summary>
    public string Provider { get; set; } = "model2vec";

    /// <summary>Weight applied to cosine similarity in hybrid scoring.</summary>
    public double SemanticWeight { get; set; } = 50.0;

    /// <summary>Ollama base URL (for provider=ollama).</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model name.</summary>
    public string OllamaModel { get; set; } = "all-minilm";

    /// <summary>OpenAI API key (for provider=openai).</summary>
    public string? OpenAiApiKey { get; set; }

    /// <summary>OpenAI model name.</summary>
    public string OpenAiModel { get; set; } = "text-embedding-3-small";

    /// <summary>OpenAI base URL (for custom endpoints).</summary>
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>Voyage AI API key (for provider=voyageai).</summary>
    public string? VoyageAiApiKey { get; set; }

    /// <summary>Voyage AI model name.</summary>
    public string VoyageAiModel { get; set; } = "voyage-3.5";

    /// <summary>Voyage AI base URL.</summary>
    public string VoyageAiBaseUrl { get; set; } = "https://api.voyageai.com/v1";

    /// <summary>Azure AI Foundry endpoint URL (e.g. https://myresource.openai.azure.com).</summary>
    public string? AzureEndpoint { get; set; }

    /// <summary>Azure AI Foundry API key.</summary>
    public string? AzureApiKey { get; set; }

    /// <summary>Azure deployment name (for classic URL pattern).</summary>
    public string AzureDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>Azure model name (for v1 URL pattern body).</summary>
    public string AzureModel { get; set; } = "text-embedding-3-small";

    /// <summary>Azure API version.</summary>
    public string AzureApiVersion { get; set; } = "2024-10-21";

    /// <summary>Use Azure v1 URL pattern instead of classic deployment-scoped.</summary>
    public bool AzureUseV1 { get; set; }

    /// <summary>Google Gemini API key (for provider=google).</summary>
    public string? GoogleApiKey { get; set; }

    /// <summary>Google Gemini embedding model name.</summary>
    public string GoogleModel { get; set; } = "gemini-embedding-001";

    /// <summary>Google Gemini API base URL.</summary>
    public string GoogleBaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    /// <summary>Google Gemini output dimensions (0 = model default).</summary>
    public int GoogleDimensions { get; set; }

    // ── Resilience ─────────────────────────────────────────────────────────

    /// <summary>Maximum retry attempts for transient failures (0 = no retries).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay in milliseconds for exponential backoff.</summary>
    public int RetryBaseDelayMs { get; set; } = 200;

    /// <summary>Consecutive failures before the circuit breaker opens.</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>Seconds to wait before transitioning from open to half-open.</summary>
    public int CircuitBreakerCooldownSeconds { get; set; } = 30;

    // ── Chunking ───────────────────────────────────────────────────────────
    // Long memories get sliced into overlapping windows; each window becomes one vector.
    // Search dedupes results back to one entry per memory but lets the best-matching
    // chunk drive the score, so a needle buried deep in a long memory stays vector-reachable.
    // Bake into the active embedding signature via ChunkedSignature.Compose so changes
    // trigger automatic reindex.

    /// <summary>Sliding-window size in characters when slicing for embedding (~1200 ≈ 300 tokens).</summary>
    public int ChunkSize { get; set; } = TextChunker.DefaultWindowSize;

    /// <summary>Overlap in characters between adjacent windows. Must be strictly less than <see cref="ChunkSize"/>.</summary>
    public int ChunkOverlap { get; set; } = TextChunker.DefaultOverlap;

    /// <summary>
    /// Safety cap on chunks per memory; excess tail is dropped from embedding (BM25 still
    /// indexes full text).
    ///
    /// <para>The cap counts <b>windows across the whole content array</b>, and every non-blank
    /// content element contributes at least one window. A memory of N short elements therefore
    /// plans at least N chunks — so this value is also a floor on how many content elements can
    /// be vector-reachable. Because <c>append</c> adds an element at the end, the tail this cap
    /// drops is the <i>newest</i> content, which is why the default is generous rather than
    /// tight. Must stay strictly below <see cref="MemoryVectorIndexer.ChunkIndexStride"/> (1000);
    /// <see cref="MemoryVectorIndexer"/>'s constructor enforces that.</para>
    /// </summary>
    public int MaxChunksPerMemory { get; set; } = DefaultMaxChunksPerMemory;

    /// <summary>Default for <see cref="MaxChunksPerMemory"/>, restored by <see cref="NormalizeGuardrails"/>
    /// when a configured value is non-positive.</summary>
    private const int DefaultMaxChunksPerMemory = 500;

    /// <summary>Largest <see cref="MaxChunksPerMemory"/> <see cref="MemoryVectorIndexer"/> accepts.</summary>
    public static int MaxChunksPerMemoryCeiling => MemoryVectorIndexer.ChunkIndexStride - 1;

    /// <summary>
    /// Clamps <see cref="MaxChunksPerMemory"/> to <see cref="MaxChunksPerMemoryCeiling"/>, returning
    /// <c>true</c> when it had to. Called by <see cref="NormalizeGuardrails"/>; kept public because
    /// <see cref="PluginConfigProtocol"/> and the Vulkan plugin's own startup path call it directly
    /// rather than the full guardrail set.
    ///
    /// <para>Without it, an out-of-range config value reaches <see cref="MemoryVectorIndexer"/>'s
    /// constructor and throws from wherever that construction happens: swallowed into a generic
    /// init failure on the CLI, a 500 on every request from the server's DI factory, and a leaked
    /// GPU context per retry in the plugin. A clamp plus a warning is the same treatment the
    /// sibling <see cref="ChunkOverlap"/>/<see cref="ChunkSize"/> invariant already gets.</para>
    /// </summary>
    public bool ClampChunkCapBelowStride()
    {
        if (MaxChunksPerMemory <= MaxChunksPerMemoryCeiling) return false;
        MaxChunksPerMemory = MaxChunksPerMemoryCeiling;
        return true;
    }

    /// <summary>
    /// Applies every config guardrail, returning one message per correction made. Hosts report
    /// these their own way (the CLI writes stderr, the server logs) — this method only corrects
    /// the values and describes what it did.
    ///
    /// <para>Binding raw config straight into <see cref="MemoryVectorIndexer"/>'s constructor (or
    /// <c>TextChunker.SliceWindows</c>) lets any of three out-of-range values reach it undetected:
    /// a config source has no type past "it parsed as an int/double," so nothing upstream of this
    /// stops a caller from handing over <c>MaxChunksPerMemory: 0</c> or <c>ChunkOverlap &gt;=
    /// ChunkSize</c>. Three corrections, in this order:</para>
    /// <list type="number">
    /// <item><description>A non-positive <see cref="MaxChunksPerMemory"/> truncates every write's
    /// indexing plan to an empty list (<c>Take(0)</c> against zero or fewer), silently wiping a
    /// memory's vectors on every single write. Reverted to <see cref="DefaultMaxChunksPerMemory"/>.
    /// </description></item>
    /// <item><description><see cref="ChunkOverlap"/> at or above <see cref="ChunkSize"/> makes
    /// <c>TextChunker.SliceWindows</c> throw. Both revert to their defaults.</description></item>
    /// <item><description><see cref="ClampChunkCapBelowStride"/> — a cap at or above
    /// <see cref="MemoryVectorIndexer.ChunkIndexStride"/> would let one element's windows overflow
    /// into the next element's index space, which <see cref="MemoryVectorIndexer"/>'s constructor
    /// rejects.</description></item>
    /// </list>
    /// </summary>
    public IReadOnlyList<string> NormalizeGuardrails()
    {
        var messages = new List<string>();

        if (MaxChunksPerMemory <= 0)
        {
            int requested = MaxChunksPerMemory;
            MaxChunksPerMemory = DefaultMaxChunksPerMemory;
            messages.Add(
                $"MaxChunksPerMemory ({requested}) must be positive; reverting to default {MaxChunksPerMemory}.");
        }

        if (ChunkOverlap >= ChunkSize)
        {
            messages.Add(
                $"ChunkOverlap ({ChunkOverlap}) >= ChunkSize ({ChunkSize}); reverting to defaults "
                + $"{TextChunker.DefaultWindowSize}/{TextChunker.DefaultOverlap}.");
            ChunkSize = TextChunker.DefaultWindowSize;
            ChunkOverlap = TextChunker.DefaultOverlap;
        }

        int requestedCap = MaxChunksPerMemory;
        if (ClampChunkCapBelowStride())
        {
            messages.Add(
                $"MaxChunksPerMemory ({requestedCap}) must be below {MemoryVectorIndexer.ChunkIndexStride}; "
                + $"clamping to {MaxChunksPerMemory}.");
        }

        return messages;
    }
}
