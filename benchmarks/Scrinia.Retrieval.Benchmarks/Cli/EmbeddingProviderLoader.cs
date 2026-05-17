using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Loads the Model2Vec MiniLM-L6-v2 provider for benchmark runs. Searches a few
/// well-known locations (CWD, repo root, app base dir) so the harness Just Works
/// whether invoked from the repo root or a publish drop.
/// </summary>
internal static class EmbeddingProviderLoader
{
    private const string ModelSubdir = "m2v-MiniLM-L6-v2";

    public static IEmbeddingProvider? TryLoadModel2Vec(string? explicitModelsDir, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        foreach (string candidate in CandidateModelsDirs(explicitModelsDir))
        {
            string modelDir = Path.Combine(candidate, ModelSubdir);
            if (!Model2VecModelManager.IsModelAvailable(modelDir))
                continue;

            try
            {
                return Model2VecProvider.Load(modelDir, logger);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load Model2Vec from {Dir}", modelDir);
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateModelsDirs(string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir)) yield return explicitDir;
        yield return Path.Combine(Environment.CurrentDirectory, "models");
        yield return Path.Combine(AppContext.BaseDirectory, "models");
        // Walk up a few levels in case we're invoked from bin/ inside the project tree.
        string cur = AppContext.BaseDirectory;
        for (int i = 0; i < 5 && !string.IsNullOrEmpty(cur); i++)
        {
            cur = Path.GetDirectoryName(cur) ?? string.Empty;
            if (string.IsNullOrEmpty(cur)) break;
            yield return Path.Combine(cur, "models");
        }
    }
}
