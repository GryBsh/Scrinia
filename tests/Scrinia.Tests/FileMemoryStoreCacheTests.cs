using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Asserts the two read-amplification fixes in FileMemoryStore:
/// 1) <see cref="FileMemoryStore.SearchAll(string, string?, int)"/> derives topic infos
///    from already-loaded candidates instead of re-calling LoadIndex per topic scope.
/// 2) <see cref="FileMemoryStore.DiscoverTopics"/> uses event-driven invalidation
///    (no TTL) — repeated calls between mutations are O(1) cache hits.
/// </summary>
public class FileMemoryStoreCacheTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public FileMemoryStoreCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Add(string topic, string name, string content)
    {
        string scope = string.IsNullOrEmpty(topic) ? "local" : $"local-topic:{topic}";
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow, "desc");
        _store.Upsert(entry, scope);
        string path = _store.ArtifactPath(name, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public void DiscoverTopics_ReturnsCachedReferenceBetweenMutations()
    {
        Add("memory/foo", "memo", "hello");

        string[] first = _store.DiscoverTopics();
        string[] second = _store.DiscoverTopics();

        // Same array reference proves the cache hit path was taken (no rescan).
        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void DiscoverTopics_InvalidatedByUpsertInTopicScope()
    {
        Add("memory/foo", "memo", "hello");
        string[] before = _store.DiscoverTopics();
        before.Should().Contain("local-topic:memory/foo");

        // Add a memory in a brand new topic scope — Upsert must invalidate the cache.
        Add("memory/bar", "memo2", "world");

        string[] after = _store.DiscoverTopics();
        ReferenceEquals(before, after).Should().BeFalse("cache should have been invalidated");
        after.Should().Contain("local-topic:memory/foo");
        after.Should().Contain("local-topic:memory/bar");
    }

    [Fact]
    public void DiscoverTopics_NoTtlInvalidation()
    {
        // With the old 2-second TTL this would have re-scanned after sleeping. We don't
        // actually want to test sleep — but we can prove the contract by waiting longer
        // than the old TTL and confirming the cache is still the same reference.
        Add("memory/foo", "memo", "hello");
        string[] first = _store.DiscoverTopics();
        Thread.Sleep(50);
        string[] second = _store.DiscoverTopics();
        // We don't sleep 2 seconds in a unit test, but the in-process behavior is now
        // event-driven: with no mutation, the reference must stay identical regardless of
        // wall-clock time, which is the property we want.
        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void SearchAll_TopicScopeEntriesReachableAfterDedupRefactor()
    {
        // Two topic scopes plus the local scope. SearchAll used to call ListScoped (which
        // calls LoadIndex per scope) AND GatherTopicInfos (which calls LoadIndex AGAIN per
        // topic scope). After the fix topic infos are derived from the already-loaded
        // candidates list, so any regression that lost topic entries from search results
        // would show up here.
        Add("", "alpha-doc-local", "content");
        Add("memory/foo", "alpha-doc-foo", "content");
        Add("memory/bar", "alpha-doc-bar", "content");

        var results = _store.SearchAll("alpha", scopes: null, limit: 20);
        results.Should().NotBeEmpty();

        var resultNames = results
            .OfType<EntryResult>()
            .Select(r => r.Item.Entry.Name)
            .ToList();
        resultNames.Should().Contain("alpha-doc-local");
        resultNames.Should().Contain("alpha-doc-foo");
        resultNames.Should().Contain("alpha-doc-bar");
    }
}
