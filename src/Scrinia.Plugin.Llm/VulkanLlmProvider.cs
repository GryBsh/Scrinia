using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Scrinia.Plugin.Llama;

namespace Scrinia.Plugin.Llm;

/// <summary>
/// Vulkan GPU-accelerated local LLM via LLamaSharp. Loads a GGUF model and exposes a
/// single <see cref="CompleteAsync"/> entry point that the plugin's MCP <c>complete</c>
/// tool delegates to. Prompts are applied via the GGUF's embedded chat template so this
/// provider is model-agnostic — the same code works for LFM2.5, Qwen2.5, Llama3, etc.
///
/// <para>Concurrency: callers serialise via the plugin process (only one MCP call in flight
/// at a time) so the internal <see cref="StatelessExecutor"/> needs no extra synchronization.
/// Tier 2 consolidation is sequential by design — there is no parallel inference path.</para>
/// </summary>
public sealed partial class VulkanLlmProvider : ILocalLlm, IDisposable
{
    // Thinking-model overhead: LFM2.5-Thinking, DeepSeek-R1, Qwen-QwQ et al. emit a chain-of-
    // thought block before the final answer. The caller's MaxTokens budget is for the *answer*,
    // so we silently expand the executor's budget when a thinking model is loaded; otherwise the
    // entire output is reasoning and the answer never appears.
    private const int ThinkingMaxTokensMultiplier = 8;
    private const int ThinkingMaxTokensCap = 2048;

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockPattern();

    [GeneratedRegex(@"<\|reasoning\|>.*?<\|/reasoning\|>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningBlockPattern();

    private readonly LLamaWeights _weights;
    private readonly StatelessExecutor _executor;
    private readonly ILogger _logger;
    private readonly bool _isThinkingModel;
    private string? _lastError;
    private bool _disposed;

    public bool IsAvailable => !_disposed;
    public string ModelPath { get; }
    public string ModelArchitecture { get; }
    public string Hardware { get; }
    public string? LastError => _lastError;

    private VulkanLlmProvider(
        LLamaWeights weights, StatelessExecutor executor,
        string modelPath, string arch, string hardware, bool isThinkingModel, ILogger logger)
    {
        _weights = weights;
        _executor = executor;
        _logger = logger;
        _isThinkingModel = isThinkingModel;
        ModelPath = modelPath;
        ModelArchitecture = arch;
        Hardware = hardware;
    }

    /// <summary>
    /// Loads the GGUF at <paramref name="modelPath"/>. Throws on load failure so the caller
    /// can fall through to <see cref="NullLlmProvider"/> with a clear error string surfaced
    /// via plugin status. <paramref name="contextSize"/> is the KV-cache budget; small models
    /// (1–2B params) typically run well at 4096 even on modest GPUs.
    /// </summary>
    public static VulkanLlmProvider Create(string modelPath, int contextSize, ILogger logger)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("GGUF model not found.", modelPath);

        LlamaVulkanInit.EnsureConfigured(logger);

        var modelParams = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = -1, // -1 = offload everything to GPU when Vulkan available
        };

        var weights = LLamaWeights.LoadFromFile(modelParams);
        var executor = new StatelessExecutor(weights, modelParams);

        // Best-effort metadata reads — GGUF files vary on whether these keys are present.
        // Failures here are informational only and must not crash the load.
        string arch = TryGetMetadata(weights, "general.architecture") ?? "unknown";
        string hardware = modelParams.GpuLayerCount != 0 ? "vulkan" : "cpu";
        bool isThinking = DetectThinkingModel(modelPath, weights);

        logger.LogInformation(
            "Vulkan LLM provider loaded: {ModelPath} (arch={Arch}, hardware={Hardware}, ctx={Ctx}, thinking={Thinking})",
            modelPath, arch, hardware, contextSize, isThinking);
        return new VulkanLlmProvider(weights, executor, modelPath, arch, hardware, isThinking, logger);
    }

    /// <summary>
    /// Heuristic for "this model emits a reasoning block before the answer." Catches LFM2.5-
    /// Thinking, DeepSeek-R1 derivatives, Qwen-QwQ, etc. by filename or basename. The signal is
    /// rough on purpose — false positives only cost a few extra tokens per call, false negatives
    /// cost empty completions.
    /// </summary>
    private static bool DetectThinkingModel(string modelPath, LLamaWeights weights)
    {
        string filename = Path.GetFileName(modelPath);
        if (filename.Contains("thinking", StringComparison.OrdinalIgnoreCase)) return true;
        if (filename.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) return true;
        if (filename.Contains("-R1", StringComparison.OrdinalIgnoreCase)) return true;
        if (filename.Contains("QwQ", StringComparison.OrdinalIgnoreCase)) return true;

        // Also check GGUF basename metadata in case the file was renamed.
        string? basename = TryGetMetadata(weights, "general.basename") ?? TryGetMetadata(weights, "general.name");
        if (basename is not null
            && (basename.Contains("thinking", StringComparison.OrdinalIgnoreCase)
                || basename.Contains("reasoning", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    public async Task<string?> CompleteAsync(
        string system, string user, int maxTokens, double temperature,
        IReadOnlyList<string>? stopSequences, CancellationToken ct)
    {
        if (_disposed) return null;

        try
        {
            // Apply the model's embedded chat template. The GGUF carries the template under
            // tokenizer.chat_template — this is the same template llama.cpp uses internally,
            // so e.g. LFM2.5-Thinking gets its specific instruct format and Qwen2.5 gets its
            // own. AddAssistant=true opens the assistant turn so generation continues from there.
            var template = new LLamaTemplate(_weights)
            {
                AddAssistant = true,
            };
            template.Add("system", system);
            template.Add("user", user);
            ReadOnlySpan<byte> applied = template.Apply();
            string prompt = Encoding.UTF8.GetString(applied);

            // Thinking models burn most of their token budget on the reasoning block before
            // the final answer. Multiply the caller's budget so something actually reaches
            // the answer phase, with a hard cap so we never blow past plausible Tier 2 sizes.
            int effectiveMaxTokens = _isThinkingModel
                ? Math.Min(maxTokens * ThinkingMaxTokensMultiplier, ThinkingMaxTokensCap)
                : maxTokens;

            var inferenceParams = new InferenceParams
            {
                MaxTokens = effectiveMaxTokens,
                AntiPrompts = stopSequences is null ? [] : [.. stopSequences],
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = (float)temperature,
                },
            };

            var sb = new StringBuilder();
            await foreach (string chunk in _executor.InferAsync(prompt, inferenceParams, ct))
            {
                if (ct.IsCancellationRequested) break;
                sb.Append(chunk);
            }
            string output = StripReasoningBlocks(sb.ToString()).Trim();
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogWarning(ex, "Vulkan LLM inference failed");
            return null;
        }
    }

    /// <summary>
    /// Strips chain-of-thought wrappers so callers see only the final answer. Two patterns
    /// cover the common cases: <c>&lt;think&gt;...&lt;/think&gt;</c> (DeepSeek-R1, Qwen-QwQ,
    /// LFM2.5-Thinking textual form) and <c>&lt;|reasoning|&gt;...&lt;|/reasoning|&gt;</c>
    /// (some templates expose the special tokens as literal text). If the model leaves an
    /// unclosed block (max-tokens truncation in the middle of thinking), we strip from the
    /// open tag to EOS so we don't leak reasoning into descriptions/facts.
    /// </summary>
    public static string StripReasoningBlocks(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        string s = ThinkBlockPattern().Replace(raw, "");
        s = ReasoningBlockPattern().Replace(s, "");

        // Truncated thinking — open tag without close. Drop from the open tag forward so the
        // partial reasoning doesn't end up in the final output. If there's nothing after,
        // the caller already treats empty as a skip-and-continue signal.
        int openThink = s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (openThink >= 0) s = s[..openThink];

        int openReason = s.IndexOf("<|reasoning|>", StringComparison.OrdinalIgnoreCase);
        if (openReason >= 0) s = s[..openReason];

        return s;
    }

    private static string? TryGetMetadata(LLamaWeights weights, string key)
    {
        try
        {
            return weights.Metadata.TryGetValue(key, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _weights.Dispose();
    }
}
