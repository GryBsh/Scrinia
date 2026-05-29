namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Locates (or downloads on first use) the ONNX cross-encoder model file and its
/// BERT <c>vocab.txt</c>. Mirrors <see cref="EmbeddingProviderLoader"/>'s
/// candidate-dir + on-demand HTTP fetch pattern so a first run Just Works.
///
/// <para>Defaults point at <c>cross-encoder/ms-marco-MiniLM-L6-v2</c>'s INT8 ONNX
/// export (the quint8/AVX2 variant — broadest CPU compatibility). Both URLs are
/// overridable from the CLI (<c>--rerank-model-url</c>, <c>--rerank-vocab-url</c>),
/// and a local <c>--rerank-model-dir</c> lets you drop files in by hand if the HF
/// path ever moves — same escape hatch the embedding loader offers.</para>
/// </summary>
internal static class CrossEncoderModelManager
{
    public const string DefaultModelUrl =
        "https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2/resolve/main/onnx/model_quint8_avx2.onnx";
    public const string DefaultVocabUrl =
        "https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2/resolve/main/vocab.txt";

    private const string SubDir = "cross-encoder-ms-marco-minilm-l6";
    private const string ModelFile = "model.onnx";
    private const string VocabFile = "vocab.txt";

    /// <summary>
    /// Returns <c>(modelPath, vocabPath)</c>, downloading either file into the first
    /// candidate dir if missing. Throws on download failure — the caller surfaces a
    /// helpful message and falls back to no reranking.
    /// </summary>
    public static async Task<(string ModelPath, string VocabPath)> EnsureAsync(
        string? explicitModelDir, string modelUrl, string vocabUrl, CancellationToken ct)
    {
        var candidates = CandidateDirs(explicitModelDir).ToList();

        // Already present in any candidate dir?
        foreach (string dir in candidates)
        {
            string m = Path.Combine(dir, ModelFile);
            string v = Path.Combine(dir, VocabFile);
            if (File.Exists(m) && File.Exists(v)) return (m, v);
        }

        string target = candidates[0];
        Directory.CreateDirectory(target);
        string modelPath = Path.Combine(target, ModelFile);
        string vocabPath = Path.Combine(target, VocabFile);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!File.Exists(modelPath))
        {
            Console.Error.WriteLine($"reranker: cross-encoder ONNX not found, downloading into {target}...");
            await DownloadAsync(http, modelUrl, modelPath, ct);
        }
        if (!File.Exists(vocabPath))
        {
            Console.Error.WriteLine($"reranker: vocab.txt not found, downloading into {target}...");
            await DownloadAsync(http, vocabUrl, vocabPath, ct);
        }
        return (modelPath, vocabPath);
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destPath, CancellationToken ct)
    {
        string tmp = destPath + ".tmp";
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(fs, ct);
            }
            // Scope closed above so the write handle is released before the rename —
            // Windows refuses File.Move while the handle is open (Linux tolerates it).
            File.Move(tmp, destPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    private static IEnumerable<string> CandidateDirs(string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir)) yield return explicitDir;
        yield return Path.Combine(Environment.CurrentDirectory, "models", SubDir);
        yield return Path.Combine(AppContext.BaseDirectory, "models", SubDir);
        string cur = AppContext.BaseDirectory;
        for (int i = 0; i < 5 && !string.IsNullOrEmpty(cur); i++)
        {
            cur = Path.GetDirectoryName(cur) ?? string.Empty;
            if (string.IsNullOrEmpty(cur)) break;
            yield return Path.Combine(cur, "models", SubDir);
        }
    }
}
