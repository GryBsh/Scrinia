using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

internal sealed class EntityFactMaterializer : IDisposable
{
    private readonly EntityFactMaterializerSource _source;
    private readonly Func<string, string?> _resolveSidecarPath;
    private readonly FactSidecarWriter _writeSidecar;
    private readonly object _stateGate = new();
    private readonly object _writeGate = new();
    private readonly object _buildGate = new();
    private readonly Dictionary<string, PendingFactWrite> _dirtyQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly FactFlushParticipant _flushParticipant;
    private readonly IBackgroundWorkRegistration _registration;
    private IReadOnlyList<ScopedArtifact>? _cachedSelection;
    private EntityFactStore? _cachedStore;
    private long _generation;
    private bool _isDirty;
    private bool _writeInFlight;
    private bool _disposed;

    internal EntityFactMaterializer(
        EntityFactMaterializerSource source,
        Func<string, string?> resolveSidecarPath,
        FactSidecarWriter writeSidecar,
        Func<IBackgroundWorkParticipant, IBackgroundWorkRegistration> registerParticipant)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _resolveSidecarPath = resolveSidecarPath ?? throw new ArgumentNullException(nameof(resolveSidecarPath));
        _writeSidecar = writeSidecar ?? throw new ArgumentNullException(nameof(writeSidecar));
        _flushParticipant = new FactFlushParticipant(this);
        _registration = (registerParticipant ?? throw new ArgumentNullException(nameof(registerParticipant)))
            (_flushParticipant);
    }

    internal EntityFactStore Materialize(
        IReadOnlyList<ScopedArtifact> items,
        IReadOnlyDictionary<string, PersistedFactResult> persistedFacts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(persistedFacts);

        long buildGeneration;
        lock (_stateGate)
        {
            if (_cachedStore is not null && SelectionsMatch(_cachedSelection!, items))
                return _cachedStore;
            buildGeneration = _generation;
        }

        lock (_buildGate)
        {
            lock (_stateGate)
            {
                if (_cachedStore is not null && SelectionsMatch(_cachedSelection!, items))
                    return _cachedStore;
                buildGeneration = _generation;
            }

            var merged = new List<MemoryEntities>();
            var pendingWrites = new List<(string Path, string Content)>();
            foreach (var group in items.GroupBy(item => item.Scope, StringComparer.Ordinal))
            {
                string scope = group.Key;
                var selected = group.ToArray();
                if (persistedFacts.TryGetValue(scope, out var persisted))
                {
                    merged.AddRange(persisted.Memories);
                    continue;
                }

                var memories = _source(selected);
                merged.AddRange(memories);
                string? sidecarPath = _resolveSidecarPath(scope);
                if (sidecarPath is not null)
                    pendingWrites.Add((sidecarPath, EntityFactSidecar.Serialize(
                        EntityFactSidecar.ComputeToken(selected.Select(item => (
                            $"{scope}|{item.Entry.Name}",
                            item.Entry.UpdatedAt ?? item.Entry.CreatedAt,
                            EntitySignature(item.Entry)))),
                        memories)));
            }

            var built = new EntityFactStore(merged);
            foreach (var (path, content) in pendingWrites)
                QueueSidecarWrite(path, content, buildGeneration);
            lock (_stateGate)
            {
                if (_cachedStore is not null && SelectionsMatch(_cachedSelection!, items))
                    return _cachedStore;
                if (_generation == buildGeneration)
                {
                    _cachedSelection = items.ToArray();
                    _cachedStore = built;
                }
                return built;
            }
        }
    }

    internal EntityFactStore? CachedStore { get { lock (_stateGate) return _cachedStore; } }
    internal long Generation { get { lock (_stateGate) return _generation; } }
    internal bool IsDirty { get { lock (_stateGate) return _isDirty; } }
    internal IReadOnlyDictionary<string, PendingFactWrite> DirtyQueue
    {
        get { lock (_stateGate) return new Dictionary<string, PendingFactWrite>(_dirtyQueue, StringComparer.OrdinalIgnoreCase); }
    }
    internal IBackgroundWorkParticipant FlushParticipant => _flushParticipant;

    internal static string ComputeToken(IEnumerable<ScopedArtifact> items) =>
        EntityFactSidecar.ComputeToken(items.Select(item => (
            $"{item.Scope}|{item.Entry.Name}",
            item.Entry.UpdatedAt ?? item.Entry.CreatedAt,
            EntitySignature(item.Entry))));

    internal void Invalidate()
    {
        lock (_writeGate)
        lock (_stateGate)
        {
            _cachedStore = null;
            _cachedSelection = null;
            _generation++;
            _isDirty |= _dirtyQueue.Count != 0 || _writeInFlight;
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string path;
            PendingFactWrite pending;
            lock (_stateGate)
            {
                if (_dirtyQueue.Count == 0)
                {
                    if (!_isDirty) return;
                    _isDirty = false;
                    continue;
                }

                var next = _dirtyQueue.First();
                path = next.Key;
                pending = next.Value;
                _dirtyQueue.Remove(path);
                _writeInFlight = true;
            }

            bool stale;
            bool written = false;
            lock (_writeGate)
            {
                lock (_stateGate)
                    stale = pending.Generation != _generation
                        || _dirtyQueue.TryGetValue(path, out var newer) && newer.Generation > pending.Generation;

                if (!stale)
                {
                    try
                    {
                        written = _writeSidecar(path, pending.Content).Written;
                    }
                    catch
                    {
                        lock (_stateGate)
                        {
                            _dirtyQueue[path] = pending;
                            _isDirty = true;
                            _writeInFlight = false;
                        }
                        throw;
                    }
                }
            }

            if (!stale && !written)
            {
                lock (_stateGate)
                {
                    _dirtyQueue[path] = pending;
                    _isDirty = true;
                    _writeInFlight = false;
                }
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            lock (_stateGate)
            {
                _writeInFlight = false;
                if (stale)
                    _isDirty = true;
                else
                    _isDirty = _dirtyQueue.Count != 0 || pending.Generation != _generation;
            }

            if (stale)
            {
                continue;
            }
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _registration.Dispose();
    }

    private void QueueSidecarWrite(string path, string content, long generation)
    {
        lock (_stateGate)
        {
            _isDirty = true;
            if (!_dirtyQueue.TryGetValue(path, out var previous) || generation >= previous.Generation)
                _dirtyQueue[path] = new PendingFactWrite(content, generation);
        }
    }

    private static string EntitySignature(ArtifactEntry entry)
    {
        string attributes = entry.Attributes is null
            ? string.Empty
            : string.Join(";", entry.Attributes
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key.ToLowerInvariant()}=[{string.Join(",", pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}]"));
        string keywords = entry.Keywords is null
            ? string.Empty
            : string.Join(",", entry.Keywords.OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase));
        return $"{attributes}|kw=[{keywords}]";
    }

    private static bool SelectionsMatch(IReadOnlyList<ScopedArtifact> cached, IReadOnlyList<ScopedArtifact> current)
    {
        if (cached.Count != current.Count) return false;
        for (int i = 0; i < cached.Count; i++)
        {
            if (!string.Equals(cached[i].Scope, current[i].Scope, StringComparison.Ordinal)
                || !string.Equals(cached[i].Entry.Name, current[i].Entry.Name, StringComparison.Ordinal)
                || !string.Equals(EntitySignature(cached[i].Entry), EntitySignature(current[i].Entry), StringComparison.Ordinal)
                || cached[i].Entry.UpdatedAt != current[i].Entry.UpdatedAt
                || cached[i].Entry.CreatedAt != current[i].Entry.CreatedAt)
                return false;
        }
        return true;
    }

    private sealed class FactFlushParticipant(EntityFactMaterializer owner) : IBackgroundWorkParticipant
    {
        public bool IsDisposed => owner._disposed || !owner.IsDirty;
        public bool HasPendingWork => owner.IsDirty;
        public Task FlushAsync(CancellationToken cancellationToken) => owner.FlushAsync(cancellationToken);
    }
}

internal sealed record PersistedFactResult(string Token, IReadOnlyList<MemoryEntities> Memories);
internal sealed record PendingFactWrite(string Content, long Generation);
internal delegate IReadOnlyList<MemoryEntities> EntityFactMaterializerSource(IEnumerable<ScopedArtifact> items);
internal delegate (bool Written, byte[] Bytes) FactSidecarWriter(string path, string content);
