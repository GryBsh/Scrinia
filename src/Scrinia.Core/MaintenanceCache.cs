using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core;

/// <summary>
/// Writes and reads per-store maintenance cache files that hold pre-computed
/// staleness, drift, and orphan counts. Updated periodically by the background
/// <c>MaintenanceCacheService</c> in the server, or on-demand by the CLI.
/// </summary>
public static class MaintenanceCache
{
    private const string CacheFileName = "maintenance.json";
    private static readonly TimeSpan StalenessThreshold = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Writes maintenance metrics to a cache file in the .scrinia/cache/ directory.
    /// </summary>
    public static void WriteCache(IMemoryStore store, int staleCount, int reviewCount,
        int driftCount, int missingCount, int orphanCount)
    {
        var data = new MaintenanceCacheData(
            DateTimeOffset.UtcNow, staleCount, reviewCount, driftCount, missingCount, orphanCount);

        try
        {
            string cacheDir = ResolveCacheDir(store);
            Directory.CreateDirectory(cacheDir);
            string cachePath = Path.Combine(cacheDir, CacheFileName);
            string json = JsonSerializer.Serialize(data, MaintenanceCacheJsonContext.Default.MaintenanceCacheData);
            string tmp = $"{cachePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scrinia:warn] MaintenanceCache.WriteCache failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the cached maintenance metrics for a store.
    /// Returns false if no cache exists or if the cache is older than 10 minutes.
    /// </summary>
    public static bool TryReadCache(IMemoryStore store, out MaintenanceCacheData? data)
    {
        data = null;
        string cacheDir = ResolveCacheDir(store);
        string cachePath = Path.Combine(cacheDir, CacheFileName);

        if (!File.Exists(cachePath))
            return false;

        try
        {
            string json = File.ReadAllText(cachePath);
            var cached = JsonSerializer.Deserialize(json, MaintenanceCacheJsonContext.Default.MaintenanceCacheData);
            if (cached is null)
                return false;

            // Staleness check — cache older than threshold is treated as absent
            if (DateTimeOffset.UtcNow - cached.ComputedAt > StalenessThreshold)
                return false;

            data = cached;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the cache directory for a store: .scrinia/cache/ (sibling of the store directory).
    /// </summary>
    private static string ResolveCacheDir(IMemoryStore store)
    {
        string storeDir = store.GetStoreDirForScope("local");
        // storeDir is typically .scrinia/store or .scrinia/topics/...
        // Navigate up to .scrinia/ and then into cache/
        string scriniaDir = Path.GetDirectoryName(storeDir) ?? storeDir;
        return Path.Combine(scriniaDir, "cache");
    }
}

/// <summary>Serializable maintenance cache data.</summary>
public sealed record MaintenanceCacheData(
    DateTimeOffset ComputedAt,
    int StaleCount,
    int ReviewCount,
    int DriftCount,
    int MissingCount,
    int OrphanCount);

[JsonSerializable(typeof(MaintenanceCacheData))]
internal sealed partial class MaintenanceCacheJsonContext : JsonSerializerContext;
