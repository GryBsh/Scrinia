using Microsoft.Extensions.Logging;

namespace Scrinia.Plugin.Embeddings.Onnx;

/// <summary>
/// Downloads and caches the all-MiniLM-L6-v2 model from HuggingFace.
/// Model files: model.onnx, vocab.txt
/// </summary>
public static class ModelManager
{
    private const string HuggingFaceBase = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main";

    /// <summary>
    /// Files to download with their repo-relative paths.
    /// model.onnx lives in the onnx/ subdirectory, vocab.txt at the repo root.
    /// </summary>
    private static readonly (string LocalName, string RepoPath)[] RequiredFiles =
    [
        ("model.onnx", "onnx/model.onnx"),
        ("vocab.txt", "vocab.txt"),
    ];

    /// <summary>Ensures the model is downloaded and returns the model directory path.</summary>
    public static async Task<string> EnsureModelAsync(string modelDir, ILogger logger, CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelDir);

        foreach (var (localName, repoPath) in RequiredFiles)
        {
            string filePath = Path.Combine(modelDir, localName);
            if (File.Exists(filePath))
                continue;

            string url = $"{HuggingFaceBase}/{repoPath}";
            logger.LogInformation("Downloading {File} from HuggingFace...", localName);

            await DownloadFileAsync(url, filePath, ct);
            logger.LogInformation("Downloaded {File} ({Size})", localName, FormatSize(new FileInfo(filePath).Length));
        }

        return modelDir;
    }

    /// <summary>Checks if all model files exist without downloading.</summary>
    public static bool IsModelAvailable(string modelDir) =>
        RequiredFiles.All(f => File.Exists(Path.Combine(modelDir, f.LocalName)));

    private static async Task DownloadFileAsync(string url, string destPath, CancellationToken ct)
    {
        string tmpPath = destPath + ".tmp";
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Write to .tmp, then close all handles before renaming
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fs, ct);
        }

        // Atomic rename (handles must be closed first)
        File.Move(tmpPath, destPath, overwrite: true);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB",
    };
}
