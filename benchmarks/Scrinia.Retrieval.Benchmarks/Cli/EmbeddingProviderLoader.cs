using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;
using Scrinia.Plugin.Embeddings;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Resolves an <see cref="IEmbeddingProvider"/> for the benchmark harness. Supports
/// the static Model2Vec distillation (default) and the contextual GGUF/Vulkan
/// encoder, with on-demand HTTP download into the resolved models dir when files
/// are missing so first-time runs Just Work.
///
/// <para>Download semantics: the first candidate dir (the user's <c>--model-dir</c>
/// if given, else <c>&lt;cwd&gt;/models</c>) is used as the write target. The actual
/// network fetch + atomic .tmp rename lives in <see cref="Model2VecModelManager.EnsureModelAsync"/>
/// and <see cref="VulkanModelManager.EnsureModelAsync"/>; this class just picks the
/// target and routes the call.</para>
/// </summary>
internal static class EmbeddingProviderLoader
{
    public static EmbedProviderKind ParseKind(string? raw) =>
        raw?.ToLowerInvariant() switch
        {
            null or "" or "model2vec" => EmbedProviderKind.Model2Vec,
            "vulkan" or "gguf"        => EmbedProviderKind.Vulkan,
            _ => throw new ArgumentException(
                $"unknown --embed-provider '{raw}'. Expected: model2vec | vulkan"),
        };

    public static async Task<IEmbeddingProvider?> LoadAsync(
        EmbedProviderKind kind,
        string? explicitModelsDir,
        CancellationToken ct)
    {
        ILogger logger = NullLogger.Instance;
        var candidates = CandidateModelsDirs(explicitModelsDir).ToList();

        foreach (string root in candidates)
        {
            IEmbeddingProvider? provider = TryLoad(kind, root, logger);
            if (provider is not null) return provider;
        }

        string target = candidates[0];
        Console.Error.WriteLine($"models: {kind} not found locally, downloading into {target}...");
        try
        {
            switch (kind)
            {
                case EmbedProviderKind.Model2Vec:
                    string m2vTarget = Path.Combine(target, "m2v-MiniLM-L6-v2");
                    SweepStaleTmpFiles(m2vTarget);
                    await Model2VecModelManager.EnsureModelAsync(m2vTarget, logger, ct);
                    break;
                case EmbedProviderKind.Vulkan:
                    SweepStaleTmpFiles(target);
                    await VulkanModelManager.EnsureModelAsync(target, logger, ct);
                    break;
            }
        }
        catch (IOException ex) when (ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"models: download failed: a .tmp file in {target} is locked by another process.");
            Console.Error.WriteLine("       common causes: a stale .tmp from a prior interrupted run, another");
            Console.Error.WriteLine("       scrinia-bench instance, or another process holding the file open.");
            Console.Error.WriteLine($"       fix: delete *.tmp in {target}, or pass --model-dir <other-dir>.");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"models: download failed: {ex.Message}");
            return null;
        }
        return TryLoad(kind, target, logger);
    }

    private static void SweepStaleTmpFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string tmp in Directory.EnumerateFiles(dir, "*.tmp"))
        {
            try { File.Delete(tmp); }
            catch (IOException) { /* still locked — the catch below will surface a useful hint */ }
        }
    }

    private static IEmbeddingProvider? TryLoad(EmbedProviderKind kind, string root, ILogger logger)
    {
        try
        {
            switch (kind)
            {
                case EmbedProviderKind.Model2Vec:
                    string m2vDir = Path.Combine(root, "m2v-MiniLM-L6-v2");
                    if (Model2VecModelManager.IsModelAvailable(m2vDir))
                        return Model2VecProvider.Load(m2vDir, logger);
                    return null;
                case EmbedProviderKind.Vulkan:
                    if (VulkanModelManager.IsModelAvailable(root))
                        return VulkanEmbeddingProvider.Create(
                            VulkanModelManager.GetModelPath(root),
                            dimensions: 384,
                            logger);
                    return null;
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load {Kind} from {Dir}", kind, root);
            return null;
        }
    }

    private static IEnumerable<string> CandidateModelsDirs(string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir)) yield return explicitDir;
        yield return Path.Combine(Environment.CurrentDirectory, "models");
        yield return Path.Combine(AppContext.BaseDirectory, "models");
        string cur = AppContext.BaseDirectory;
        for (int i = 0; i < 5 && !string.IsNullOrEmpty(cur); i++)
        {
            cur = Path.GetDirectoryName(cur) ?? string.Empty;
            if (string.IsNullOrEmpty(cur)) break;
            yield return Path.Combine(cur, "models");
        }
    }
}

internal enum EmbedProviderKind { Model2Vec, Vulkan }
