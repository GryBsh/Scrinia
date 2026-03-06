using Microsoft.Extensions.Logging;

namespace Scrinia.Plugin.Embeddings;

/// <summary>
/// Downloads a GGUF embedding model from HuggingFace for use with VulkanEmbeddingProvider.
/// Default model: all-MiniLM-L6-v2 in GGUF format.
/// </summary>
public static class VulkanModelManager
{
    private const string DefaultModelUrl =
        "https://huggingface.co/second-state/All-MiniLM-L6-v2-Embedding-GGUF/resolve/main/all-MiniLM-L6-v2-Q8_0.gguf";

    private const string DefaultModelFile = "all-MiniLM-L6-v2-Q8_0.gguf";

    /// <summary>Whether the GGUF model file exists.</summary>
    public static bool IsModelAvailable(string modelDir)
        => File.Exists(Path.Combine(modelDir, DefaultModelFile));

    /// <summary>Gets the path to the default GGUF model.</summary>
    public static string GetModelPath(string modelDir)
        => Path.Combine(modelDir, DefaultModelFile);

    /// <summary>Downloads the GGUF model if not already present.</summary>
    public static async Task EnsureModelAsync(string modelDir, ILogger logger, CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelDir);

        string filePath = Path.Combine(modelDir, DefaultModelFile);
        if (File.Exists(filePath))
            return;

        logger.LogInformation("Downloading GGUF embedding model...");

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        string tmpPath = filePath + ".tmp";
        using var response = await http.GetAsync(DefaultModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fs, ct);

        File.Move(tmpPath, filePath, overwrite: true);
        logger.LogInformation("Downloaded GGUF model ({Size:F1} MB)",
            new FileInfo(filePath).Length / (1024.0 * 1024));
    }
}
