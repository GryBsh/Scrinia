using Microsoft.Extensions.Logging;

namespace Scrinia.Plugin.Llm;

/// <summary>
/// Downloads a GGUF instruction-tuned LLM from a configurable URL for use with
/// <see cref="VulkanLlmProvider"/>. Mirrors <c>VulkanModelManager</c> in the embeddings
/// plugin — atomic <c>.tmp</c> swap on completion so a killed download leaves no
/// half-written file.
///
/// <para>Default model: <c>LFM2.5-1.2B-Thinking</c> Q5_K_M quant. LFM2's hybrid SSM+attention
/// architecture requires a llama.cpp snapshot that supports it; LLamaSharp 0.25 ships one
/// recent enough to load it. If the loaded GGUF fails at runtime (e.g. on older LLamaSharp
/// builds), users can override via <c>scri config Scrinia:Llm:LocalModelUrl &lt;url&gt;</c>
/// and <c>Scrinia:Llm:LocalModelFile &lt;name&gt;</c>. The recommended fallback is
/// <see cref="FallbackModelUrl"/> (Qwen2.5-1.5B-Instruct), known-compatible with LLamaSharp 0.25.</para>
/// </summary>
public static class LlmModelManager
{
    /// <summary>Default GGUF URL — LFM2.5-1.2B-Thinking, Q5_K_M quantization.</summary>
    public const string DefaultModelUrl =
        "https://huggingface.co/LiquidAI/LFM2.5-1.2B-Thinking-GGUF/resolve/main/LFM2.5-1.2B-Thinking-Q5_K_M.gguf";

    /// <summary>Default GGUF filename matching <see cref="DefaultModelUrl"/>.</summary>
    public const string DefaultModelFile = "LFM2.5-1.2B-Thinking-Q5_K_M.gguf";

    /// <summary>Fallback URL when the user wants a known-LLamaSharp-0.25-compatible model.</summary>
    public const string FallbackModelUrl =
        "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q5_k_m.gguf";

    /// <summary>Filename matching <see cref="FallbackModelUrl"/>.</summary>
    public const string FallbackModelFile = "qwen2.5-1.5b-instruct-q5_k_m.gguf";

    /// <summary>True when the named GGUF file exists in <paramref name="modelDir"/>.</summary>
    public static bool IsModelAvailable(string modelDir, string fileName)
        => File.Exists(Path.Combine(modelDir, fileName));

    /// <summary>Resolves the absolute path of the named GGUF.</summary>
    public static string GetModelPath(string modelDir, string fileName)
        => Path.Combine(modelDir, fileName);

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="modelDir"/>/<paramref name="fileName"/>
    /// if not already present. Atomic via <c>.tmp</c> rename so a partial transfer never appears
    /// complete. Skips silently when the destination exists.
    /// </summary>
    public static async Task EnsureModelAsync(
        string modelDir, string url, string fileName, ILogger logger, CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelDir);

        string filePath = Path.Combine(modelDir, fileName);
        if (File.Exists(filePath)) return;

        logger.LogInformation("Downloading GGUF LLM model from {Url}...", url);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        string tmpPath = filePath + ".tmp";

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.CopyToAsync(fs, ct);
            }

            File.Move(tmpPath, filePath, overwrite: true);
            logger.LogInformation(
                "Downloaded GGUF LLM model ({Size:F1} MB) to {Path}",
                new FileInfo(filePath).Length / (1024.0 * 1024), filePath);
        }
        catch
        {
            // Best-effort cleanup so a retry does not see a corrupt .tmp.
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }
}
