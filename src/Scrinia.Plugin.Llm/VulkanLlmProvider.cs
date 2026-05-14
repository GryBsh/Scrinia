using System.Text;
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
public sealed class VulkanLlmProvider : ILocalLlm, IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly StatelessExecutor _executor;
    private readonly ILogger _logger;
    private string? _lastError;
    private bool _disposed;

    public bool IsAvailable => !_disposed;
    public string ModelPath { get; }
    public string ModelArchitecture { get; }
    public string Hardware { get; }
    public string? LastError => _lastError;

    private VulkanLlmProvider(
        LLamaWeights weights, StatelessExecutor executor,
        string modelPath, string arch, string hardware, ILogger logger)
    {
        _weights = weights;
        _executor = executor;
        _logger = logger;
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

        logger.LogInformation(
            "Vulkan LLM provider loaded: {ModelPath} (arch={Arch}, hardware={Hardware}, ctx={Ctx})",
            modelPath, arch, hardware, contextSize);
        return new VulkanLlmProvider(weights, executor, modelPath, arch, hardware, logger);
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

            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens,
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
            string output = sb.ToString().Trim();
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
