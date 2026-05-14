namespace Scrinia.Plugin.Llm;

/// <summary>
/// Process-local LLM inference contract. The plugin process exposes a single
/// implementation via the MCP <c>complete</c> tool; <see cref="NullLlmProvider"/>
/// is the fallback used when no model is loaded so the plugin still responds to
/// <c>status</c> calls.
///
/// <para>Generic completion shape: a {system, user} pair plus standard sampling
/// controls. Plugin-side templating uses the GGUF's embedded chat template so this
/// surface is model-agnostic — callers in <c>Scrinia.Core</c> own the prompts.</para>
/// </summary>
public interface ILocalLlm
{
    /// <summary>True when a model is loaded and inference can be attempted.</summary>
    bool IsAvailable { get; }

    /// <summary>Path to the loaded GGUF model, or empty when unavailable.</summary>
    string ModelPath { get; }

    /// <summary>GGUF architecture string (e.g. "llama", "qwen2", "lfm2"), or "unknown".</summary>
    string ModelArchitecture { get; }

    /// <summary>"vulkan" when GPU-accelerated, "cpu" when fell back, or "none".</summary>
    string Hardware { get; }

    /// <summary>Last fatal error during load or inference, surfaced via status.</summary>
    string? LastError { get; }

    /// <summary>
    /// Runs a single completion. Returns the model's response text, or <c>null</c>
    /// on any failure the caller should treat as skip-and-continue (timeout,
    /// inference error, model unavailable).
    /// </summary>
    Task<string?> CompleteAsync(
        string system,
        string user,
        int maxTokens,
        double temperature,
        IReadOnlyList<string>? stopSequences,
        CancellationToken ct);
}
