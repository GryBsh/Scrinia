using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Scrinia.Core;

/// <summary>
/// Owns the lazily-created, per-<c>(storeDirectory, scope)</c>
/// <see cref="MaintenanceDiscoveryPageService"/> cache and its producer, extracted
/// out of <see cref="FileMemoryStore"/> as the fifth god-store seam. Behavior is
/// identical to the inline members it replaces — the store holds one
/// <c>readonly</c> instance and delegates.
/// </summary>
internal sealed class MaintenancePageServiceCache
{
    internal Action? MaintenancePageServiceCreationBarrierForTest { get; set; }

    private readonly ConcurrentDictionary<string, MaintenanceDiscoveryPageService> _maintenancePages = new(StringComparer.Ordinal);
    private readonly object _maintenanceLifecycleGate = new();
    private bool _maintenanceDisposed;

    private readonly RecoveryDiscoveryCoordinator _recoveryDiscovery;
    private readonly MaintenanceRunRegistry _maintenanceRuns;

    internal MaintenancePageServiceCache(RecoveryDiscoveryCoordinator recoveryDiscovery, MaintenanceRunRegistry maintenanceRuns)
    {
        _recoveryDiscovery = recoveryDiscovery;
        _maintenanceRuns = maintenanceRuns;
    }

    internal MaintenanceDiscoveryPageService GetMaintenancePageService(string scope, string storeDirectory)
    {
        MaintenancePageServiceCreationBarrierForTest?.Invoke();
        string key = $"{Path.GetFullPath(storeDirectory)}\0{scope}";
        lock (_maintenanceLifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_maintenanceDisposed, this);
            if (_maintenancePages.TryGetValue(key, out MaintenanceDiscoveryPageService? existing))
                return existing;

            var created = new MaintenanceDiscoveryPageService(cancellationToken =>
                _recoveryDiscovery.EnumerateMaintenanceCandidates(scope, storeDirectory, cancellationToken), _maintenanceRuns);
            if (!_maintenancePages.TryAdd(key, created))
            {
                created.Dispose();
                return _maintenancePages[key];
            }

            return created;
        }
    }

    internal MaintenanceDiscoveryPageService[] TakeDisposalSnapshot()
    {
        lock (_maintenanceLifecycleGate)
        {
            if (_maintenanceDisposed) return Array.Empty<MaintenanceDiscoveryPageService>();
            _maintenanceDisposed = true;
            var pages = _maintenancePages.Values.ToArray();
            _maintenancePages.Clear();
            return pages;
        }
    }
}
