using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Scrinia.Core;

/// <summary>
/// Owning type for the topic-discovery + scope-resolution family extracted from
/// <see cref="FileMemoryStore"/> (P6 seam). Holds the sole <c>_cachedTopics</c> backing field and
/// the <c>DiscoverTopics</c> scan, the <c>NormalizeScopeFilters</c>/<c>ResolvePathPrefix</c>/
/// <c>MatchesPathPrefix</c>/<c>ResolveReadScopes</c> resolution chain, and the
/// <c>IncludesEphemeral</c> predicate. The store keeps same-signature forwarding members so the
/// public/internal surface (IMemoryStore, FakeMemoryStore, direct test calls) is byte-identical.
/// </summary>
internal sealed class TopicDiscoveryService
{
    private string[]? _cachedTopics;
    private readonly Func<string> _memoriesRootProvider;

    internal TopicDiscoveryService(Func<string> memoriesRootProvider) =>
        _memoriesRootProvider = memoriesRootProvider;

    internal string[] DiscoverTopics()
    {
        // Cache hit — only re-scans when explicitly invalidated by Upsert / SaveIndex.
        if (_cachedTopics is not null)
            return _cachedTopics;

        // Gate first: on the very first store access this migrates any legacy content into
        // memories/ before the scan below runs, so a first-ever call with real legacy content
        // sees it exactly once, at its post-migration v3 location.
        string memoriesRoot = _memoriesRootProvider();

        var results = new List<string>();
        if (Directory.Exists(memoriesRoot))
            DiscoverMemoriesRecursive(memoriesRoot, "", results);

        _cachedTopics = results.ToArray();
        return _cachedTopics;
    }

    /// <summary>
    /// Recursively scans the v2 memories directory tree and adds leaf directories
    /// (those containing .nmp2 or .meta.json files) as v2 scopes.
    /// </summary>
    internal static void DiscoverMemoriesRecursive(string dir, string relativePath, List<string> results)
    {
        // Check if this directory itself is a leaf (contains artifact files)
        bool hasArtifacts = Directory.EnumerateFiles(dir, "*.nmp2").Any()
                           || Directory.EnumerateFiles(dir, "*.meta.json").Any();

        if (hasArtifacts && !string.IsNullOrEmpty(relativePath))
            results.Add($"local:{relativePath}");

        // Recurse into subdirectories
        foreach (string subDir in Directory.GetDirectories(dir))
        {
            string subName = Path.GetFileName(subDir);
            if (subName.Equals("versions", StringComparison.OrdinalIgnoreCase))
                continue; // skip version archive dirs

            string childPath = string.IsNullOrEmpty(relativePath)
                ? subName
                : $"{relativePath}/{subName}";
            DiscoverMemoriesRecursive(subDir, childPath, results);
        }
    }

    internal IReadOnlyList<string> NormalizeScopeFilters(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ["local"];

        string s = token.Trim();
        if (s.Equals("local", StringComparison.OrdinalIgnoreCase)) return ["local"];
        // Ephemeral is handled separately in ListScoped — exclude from filesystem scopes
        if (s.Equals("ephemeral", StringComparison.OrdinalIgnoreCase)) return [];

        // Path prefix query: "/goal/G-5/" → find all scopes under that path tree
        if (s.StartsWith('/'))
            return ResolvePathPrefix(s);

        // Everything else is a topic, whether or not the caller spelled the "local:" prefix.
        // Both spellings go through the ONE scope constructor (Task 13). This branch used to
        // return an already-"local:"-prefixed token RAW, which meant a filter naming
        // "local:topic." became a second CachedIndex key over the same directory that no write
        // to "local:topic" would ever invalidate — a permanently stale read. It also let
        // "LOCAL:foo" through, which ToStoreDir then rejected outright as an unknown scope
        // (its prefix test is Ordinal); re-constructing normalizes the prefix too.
        string topic = s.StartsWith("local:", StringComparison.OrdinalIgnoreCase)
            ? s["local:".Length..]
            : s;

        // READ path: a topic that cannot be constructed cannot name a stored memory either, so
        // matching nothing is the correct answer rather than a refusal. See
        // MemoryNaming.TryBuildScopedTopicScope.
        return MemoryNaming.TryBuildScopedTopicScope(topic, out string scope) ? [scope] : [];
    }

    /// <summary>
    /// Resolves a path prefix (e.g. "/goal/G-5/") to all discovered scopes whose
    /// topic part starts with the prefix. Enables subtree queries for hierarchical v2 paths
    /// as well as v1 scopes whose stripped topic name matches.
    /// </summary>
    internal IReadOnlyList<string> ResolvePathPrefix(string pathPrefix)
    {
        string prefix = pathPrefix.TrimStart('/').TrimEnd('/');
        if (string.IsNullOrEmpty(prefix))
        {
            // "/" alone means everything — equivalent to "all"
            var all = new List<string> { "local" };
            all.AddRange(DiscoverTopics());
            return all;
        }

        var matching = new List<string>();
        foreach (string scope in DiscoverTopics())
        {
            if (!scope.StartsWith("local:", StringComparison.Ordinal))
                continue;

            string topicPart = scope["local:".Length..];

            // The topic part starts with the prefix at a path boundary — e.g.
            // prefix="goal/G-5" matches "goal/G-5", "goal/G-5/research" but NOT "goal/G-50".
            // v3: every scope's topic part is already flat (see MemoryNaming.BuildScopedTopicScope),
            // so there is no separate namespaced form left to also match against.
            if (MatchesPathPrefix(topicPart, prefix))
                matching.Add(scope);
        }

        return matching;
    }

    /// <summary>
    /// Returns true if <paramref name="topicPart"/> equals or is a child path of <paramref name="prefix"/>.
    /// Matches at path separator boundaries to avoid "goal/G-5" matching "goal/G-50".
    /// </summary>
    internal static bool MatchesPathPrefix(string topicPart, string prefix)
    {
        if (topicPart.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            return true;

        if (topicPart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && topicPart.Length > prefix.Length
            && topicPart[prefix.Length] == '/')
            return true;

        return false;
    }

    internal IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null)
    {
        // Task 9 fix round 1 (F-1/F-2/F-3): normalize at entry, for callers that reach the store
        // directly rather than through a SearchQuery (whose Scopes/ExcludeTopics init accessors do
        // the same thing). This is what turns ASP.NET Core's `?scopes=` -> ["" ] into "no filter"
        // (F-1) instead of NormalizeScopeFilters("") -> ["local"], drops a null element before it
        // can reach a bare .Trim() and throw (F-2), and drops blank entries so ["", "api"] resolves
        // exactly as ["api"] instead of silently adding "local" (F-3).
        scopes = MemoryNaming.NormalizeFilterList(scopes);

        // Task 9, D-4: an empty list means "no filter", identically to null.
        if (scopes is { Count: > 0 })
        {
            // "all" (case-insensitive) as the SOLE entry returns every scope including
            // entity-classified ones. Matches the old single-string behaviour, where only an
            // exact (trimmed) "all" tripped this branch — "all,foo" fell through to the general
            // per-token path below, same as it does here when the list has more than one entry.
            // (No .Trim() needed here any more — NormalizeFilterList already trimmed every entry.)
            if (scopes.Count == 1 && scopes[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var all = new List<string> { "local" };
                all.AddRange(DiscoverTopics());
                return all.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }

            return scopes
                .SelectMany(NormalizeScopeFilters)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // Default (null or empty): every discovered topic. There is no longer an
        // "entity-classified" category to exclude — see the commit that deleted IsEntityScope.
        var ordered = new List<string> { "local" };
        ordered.AddRange(DiscoverTopics());
        return ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)
    {
        var resolved = ResolveReadScopes(scopes);
        if (excludeTopics is not { Count: > 0 })
            return resolved;
        var excluded = IMemoryStore.BuildExcludedScopeSet(excludeTopics);
        return resolved.Where(s => !excluded.Contains(s)).ToArray();
    }

    internal static bool IncludesEphemeral(IReadOnlyList<string>? scopes)
    {
        // Task 9 fix round 1 (F-2): normalize at entry so a null element (schema-valid JSON for
        // string[] regardless of the non-nullable element annotation) cannot reach the bare
        // .Trim() below and throw.
        scopes = MemoryNaming.NormalizeFilterList(scopes);
        return scopes is not { Count: > 0 }
            || scopes.Any(s => s.Equals("ephemeral", StringComparison.OrdinalIgnoreCase));
    }

    internal void InvalidateCachedTopics() => _cachedTopics = null;
}
