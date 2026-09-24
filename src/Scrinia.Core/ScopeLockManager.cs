using System.Collections.Concurrent;
using System.Threading;

namespace Scrinia.Core;

internal sealed class ScopeLockManager
{
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AdmissionGate> _admissionLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _admissionLifecycleGate = new();
    private bool _admissionDisposed;

    // internal: FileMemoryStore (same assembly) routes its frozen method bodies through these.
    internal AdmissionGate AcquireAdmissionGate(string scope)
    {
        lock (_admissionLifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_admissionDisposed, this);
            AdmissionGate gate = _admissionLocks.GetOrAdd(scope, _ => new AdmissionGate());
            gate.Leases++;
            return gate;
        }
    }

    internal void ReleaseAdmissionGate(string scope, AdmissionGate gate)
    {
        lock (_admissionLifecycleGate)
        {
            gate.Leases--;
            if (gate.Leases != 0)
                return;

            _admissionLocks.TryGetValue(scope, out AdmissionGate? current);
            if (ReferenceEquals(current, gate))
                _admissionLocks.TryRemove(scope, out _);
            gate.Dispose();
        }
    }

    internal ReaderWriterLockSlim GetIndexLock(string scope) =>
        _indexLocks.GetOrAdd(scope, _ => new ReaderWriterLockSlim());

    internal void Dispose()
    {
        AdmissionGate[] admissionGatesToDispose;
        lock (_admissionLifecycleGate)
        {
            _admissionDisposed = true;
            admissionGatesToDispose = _admissionLocks.Values
                .Where(gate => gate.Leases == 0)
                .ToArray();
            foreach (AdmissionGate gate in admissionGatesToDispose)
            {
                foreach (var pair in _admissionLocks.Where(pair => ReferenceEquals(pair.Value, gate)).ToArray())
                    _admissionLocks.TryRemove(pair.Key, out _);
                gate.Dispose();
            }
        }
    }
}

internal sealed class AdmissionGate : IDisposable
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
    internal int Leases { get; set; }

    public void Dispose() => Semaphore.Dispose();
}
