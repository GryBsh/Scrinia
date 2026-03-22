using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Server.Services;

/// <summary>
/// Background service that pre-computes maintenance state (staleness, drift, orphans)
/// every 5 minutes and writes the results to a cache file per store.
/// </summary>
public sealed class MaintenanceCacheService(StoreManager storeManager) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run first scan immediately, then every 5 minutes
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ScanAllStores();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[scrinia:warn] MaintenanceCacheService error: {ex.GetType().Name}: {ex.Message}");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ScanAllStores()
    {
        foreach (var storeName in storeManager.StoreNames)
        {
            try
            {
                var store = storeManager.GetStore(storeName);

                var (staleCount, reviewCount) = ScriniaProjectTools.ScanStaleness(store);
                var (driftCount, missingCount) = ScriniaProjectTools.ScanDrift(store);

                // Count orphans
                int orphanCount = 0;
                try
                {
                    var allEntries = store.ListScoped(null);
                    orphanCount = allEntries.Count(sa =>
                        sa.Entry.Keywords?.Any(k => k.Equals("orphan", StringComparison.OrdinalIgnoreCase)) == true);
                }
                catch { }

                MaintenanceCache.WriteCache(store, staleCount, reviewCount, driftCount, missingCount, orphanCount);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[scrinia:warn] MaintenanceCacheService error for store '{storeName}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
