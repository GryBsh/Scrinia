using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Downloads the m2v-MiniLM-L6-v2 Model2Vec model from HuggingFace.
/// This is a 384-dim distillation of all-MiniLM-L6-v2, vector-compatible with the
/// Vulkan GGUF provider so both can share the same VectorStore without reindexing.
/// Files: model.safetensors (~22MB, F16) + vocab.txt (~220KB).
/// </summary>
public static class Model2VecModelManager
{
    private const string BaseUrl = "https://huggingface.co/grybsh/m2v-MiniLM-L6-v2/resolve/main";

    private static readonly string[] Files = ["model.safetensors", "vocab.txt"];

    /// <summary>Whether all model files exist in the given directory.</summary>
    public static bool IsModelAvailable(string modelDir)
        => Files.All(f => File.Exists(Path.Combine(modelDir, f)));

    /// <summary>Downloads model files if not already present.</summary>
    public static async Task EnsureModelAsync(string modelDir, ILogger logger, CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelDir);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        foreach (var file in Files)
        {
            string filePath = Path.Combine(modelDir, file);
            if (File.Exists(filePath))
                continue;

            string url = $"{BaseUrl}/{file}";
            logger.LogInformation("Downloading {File} from {Url}...", file, url);

            string tmpPath = filePath + ".tmp";
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fs, ct);

            File.Move(tmpPath, filePath, overwrite: true);
            logger.LogInformation("Downloaded {File} ({Size:F1} MB)", file,
                new FileInfo(filePath).Length / (1024.0 * 1024));
        }
    }
}
