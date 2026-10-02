using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Owns the breadth-listing composition for <see cref="FileMemoryStore"/>. The listing
/// composition members (ListScoped×2, AnyScoped, GatherTopicInfos, ListTopicArtifacts,
/// FilterToQueryScopes, BuildTopicInfosFromCandidates) live here; the store keeps the five public
/// listing forwarders and the ListEphemeral/ProjectEphemeralEntry write-path members. Every
/// cross-seam dependency (LoadIndex from P4, GetStoreDirForScope/SanitizeName, ListEphemeral,
/// IncludesEphemeral from P6, ResolveReadScopes×2) arrives as an injected Func&lt;&gt; hook so the
/// service never reaches back into the store. Behavior is byte-identical to the pre-extraction store.
/// </summary>
internal sealed class ListingService
{
    private readonly Func<string, IReadOnlyList<ArtifactEntry>> _loadIndex;
    private readonly Func<string, string> _getStoreDirForScope;
    private readonly Func<string, string> _sanitizeName;
    private readonly Func<IReadOnlyList<ScopedArtifact>> _listEphemeral;
    private readonly Func<IReadOnlyList<string>?, bool> _includesEphemeral;
    private readonly Func<IReadOnlyList<string>?, IReadOnlyList<string>> _resolveReadScopes;
    private readonly Func<IReadOnlyList<string>?, IReadOnlyList<string>?, IReadOnlyList<string>> _resolveReadScopesWithExcludes;

    internal ListingService(
        Func<string, IReadOnlyList<ArtifactEntry>> loadIndex,
        Func<string, string> getStoreDirForScope,
        Func<string, string> sanitizeName,
        Func<IReadOnlyList<ScopedArtifact>> listEphemeral,
        Func<IReadOnlyList<string>?, bool> includesEphemeral,
        Func<IReadOnlyList<string>?, IReadOnlyList<string>> resolveReadScopes,
        Func<IReadOnlyList<string>?, IReadOnlyList<string>?, IReadOnlyList<string>> resolveReadScopesWithExcludes)
    {
        _loadIndex = loadIndex;
        _getStoreDirForScope = getStoreDirForScope;
        _sanitizeName = sanitizeName;
        _listEphemeral = listEphemeral;
        _includesEphemeral = includesEphemeral;
        _resolveReadScopes = resolveReadScopes;
        _resolveReadScopesWithExcludes = resolveReadScopesWithExcludes;
    }

    internal List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes)
    {
        var result = new List<ScopedArtifact>();

        if (_includesEphemeral(scopes))
            result.AddRange(_listEphemeral());

        foreach (string scope in _resolveReadScopes(scopes))
        {
            foreach (var entry in _loadIndex(scope))
                result.Add(new ScopedArtifact(scope, entry));
        }
        return result;
    }

    /// <summary>
    /// Lazy override of the interface default: walks the same source ListScoped does —
    /// ephemeral then per-scope index — but returns on the first match instead of materializing
    /// a List first. A scope after the match is never loaded at all; the default
    /// (ListScoped(scopes).Any(predicate)) would load every scope before testing any of them.
    /// </summary>
    internal bool AnyScoped(IReadOnlyList<string>? scopes, Func<ScopedArtifact, bool> predicate)
    {
        if (_includesEphemeral(scopes) && _listEphemeral().Any(predicate))
            return true;

        foreach (string scope in _resolveReadScopes(scopes))
        {
            foreach (var entry in _loadIndex(scope))
            {
                if (predicate(new ScopedArtifact(scope, entry)))
                    return true;
            }
        }
        return false;
    }

    internal static List<TopicInfo> BuildTopicInfosFromCandidates(IReadOnlyList<ScopedArtifact> candidates)
    {
        var topics = new List<TopicInfo>();
        foreach (var group in candidates
            .Where(c => c.Scope.StartsWith("local:", StringComparison.Ordinal))
            .GroupBy(c => c.Scope, StringComparer.OrdinalIgnoreCase))
        {
            string topicName = group.Key["local:".Length..];
            int count = group.Count();
            topics.Add(new TopicInfo(
                Scope: group.Key,
                TopicName: topicName,
                EntryCount: count,
                Description: $"{topicName} ({count} {(count == 1 ? "entry" : "entries")})",
                Tags: null,
                EntryNames: group.Select(c => c.Entry.Name).ToArray()));
        }
        return topics;
    }

    /// <summary>
    /// Efficient override of the default interface method: filters at scope-resolution level
    /// rather than post-filtering, avoiding loading indices for excluded topics.
    /// </summary>
    internal List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)
    {
        // Task 9, D-4: an empty excludeTopics list behaves identically to null.
        if (excludeTopics is not { Count: > 0 })
            return ListScoped(scopes);

        var result = new List<ScopedArtifact>();

        if (_includesEphemeral(scopes))
            result.AddRange(_listEphemeral());

        foreach (string scope in _resolveReadScopesWithExcludes(scopes, excludeTopics))
        {
            foreach (var entry in _loadIndex(scope))
                result.Add(new ScopedArtifact(scope, entry));
        }
        return result;
    }

    internal IReadOnlyList<ScopedArtifact> SelectLexicalCandidates(
        IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics, string query, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var selected = new List<ScopedArtifact>(limit);
        IEnumerable<ScopedArtifact> source = EnumerateScoped(scopes, excludeTopics);
        foreach (ScopedArtifact candidate in source)
        {
            selected.Add(candidate);
            if (selected.Count <= limit)
                continue;

            selected = SearchCandidateOptions.RankLexically(selected, query).Take(limit).ToList();
        }

        return SearchCandidateOptions.RankLexically(selected, query);
    }

    private IEnumerable<ScopedArtifact> EnumerateScoped(
        IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)
    {
        if (_includesEphemeral(scopes))
        {
            foreach (ScopedArtifact artifact in _listEphemeral())
                yield return artifact;
        }

        IReadOnlyList<string> resolved = excludeTopics is { Count: > 0 }
            ? _resolveReadScopesWithExcludes(scopes, excludeTopics)
            : _resolveReadScopes(scopes);
        foreach (string scope in resolved)
        foreach (ArtifactEntry entry in _loadIndex(scope))
            yield return new ScopedArtifact(scope, entry);
    }

    /// <summary>
    /// Constrains a caller-supplied candidate list to exactly the scopes this query resolves to, so
    /// a wider or differently-scoped SearchQuery.Candidates cannot change a search's answer. A list
    /// narrower than the query's scopes is not corrected here — it still under-returns. Mirrors
    /// ListScoped(scopes, excludeTopics)'s scope resolution, including its rule for when ephemeral
    /// memories are in play.
    /// </summary>
    internal List<ScopedArtifact> FilterToQueryScopes(
        IReadOnlyList<ScopedArtifact> candidates, IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics)
    {
        var allowed = new HashSet<string>(
            _resolveReadScopesWithExcludes(scopes, excludeTopics), StringComparer.OrdinalIgnoreCase);

        if (_includesEphemeral(scopes))
            allowed.Add("ephemeral");

        var result = new List<ScopedArtifact>(candidates.Count);
        foreach (var candidate in candidates)
            if (allowed.Contains(candidate.Scope))
                result.Add(candidate);
        return result;
    }

    /// <summary>
    /// Efficient override of the default interface method: resolves scopes then excludes
    /// the specified topic scopes.
    /// </summary>
    internal List<TopicInfo> GatherTopicInfos(IReadOnlyList<string>? scopes)
    {
        var topics = new List<TopicInfo>();
        foreach (string scope in _resolveReadScopes(scopes))
        {
            if (!scope.StartsWith("local:", StringComparison.Ordinal))
                continue;

            string topicName = scope["local:".Length..];
            var entries = _loadIndex(scope);
            if (entries.Count == 0) continue;
            topics.Add(new TopicInfo(
                Scope: scope,
                TopicName: topicName,
                EntryCount: entries.Count,
                Description: $"{topicName} ({entries.Count} {(entries.Count == 1 ? "entry" : "entries")})",
                Tags: null,
                EntryNames: entries.Select(e => e.Name).ToArray()));
        }
        return topics;
    }

    internal List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope)
    {
        var result = new List<(string, string)>();
        var entries = _loadIndex(topicScope);
        string storeDir = _getStoreDirForScope(topicScope);
        foreach (var entry in entries)
        {
            string subject = entry.Address.Kind == MemoryAddressKind.Persistent
                ? entry.Address.Value.Trim('/').Split('/')[^1]
                : entry.Address.Value;
            string filePath = Path.Combine(storeDir, _sanitizeName(subject) + ".nmp2");
            if (File.Exists(filePath))
                result.Add((subject, filePath));
        }
        return result;
    }
}
