using System.Collections.Concurrent;
using Scrinia.Core.Models;

namespace Scrinia.Core;

internal sealed class ScopeIndexCache
{
    private readonly ConcurrentDictionary<string, CachedIndex> _indexCache = new(StringComparer.OrdinalIgnoreCase);

    public void Replace(string scope, List<ArtifactEntry> entries) =>
        _indexCache[scope] = new CachedIndex(entries);

    public void Remove(string scope) => _indexCache.TryRemove(scope, out _);

    public bool TryGet(string scope, out CachedIndex cachedIndex)
    {
        if (_indexCache.TryGetValue(scope, out CachedIndex? found))
        {
            cachedIndex = found!;
            return true;
        }

        cachedIndex = null!;
        return false;
    }

    public void Clear() => _indexCache.Clear();
}

internal sealed class CachedIndex
{
    public List<ArtifactEntry> Entries { get; }
    public CachedIndex(List<ArtifactEntry> entries) => Entries = entries;
}
