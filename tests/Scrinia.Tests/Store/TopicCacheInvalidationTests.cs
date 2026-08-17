using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// The topic list changes when a scope directory appears or empties — not on every write into an
/// existing scope. Before this, every write to a "local:" scope nulled _cachedTopics and forced
/// DiscoverTopics to re-enumerate directories (finding H5).
///
/// DiscoverTopics() returns fully-qualified scope strings (e.g. "local:alpha"), not bare topic
/// names — verified against FileMemoryStore.cs's DiscoverMemoriesRecursive, which prefixes every
/// result with "local:".
/// </summary>
public sealed class TopicCacheInvalidationTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public TopicCacheInvalidationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_topic_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ArtifactEntry E(string name) =>
        new(name, "", 4, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void ANewTopicScope_AppearsInDiscoverTopics_AfterItsFirstWrite()
    {
        _store.DiscoverTopics().Should().NotContain("local:alpha");

        _store.Upsert(E("first"), "local:alpha");

        _store.DiscoverTopics().Should().Contain("local:alpha",
            because: "the first write into a new topic scope creates its directory, which changes "
                   + "the topic list");
    }

    [Fact]
    public void AdditionalWritesIntoAnExistingTopic_KeepItDiscoverable()
    {
        _store.Upsert(E("first"), "local:alpha");
        _store.DiscoverTopics().Should().Contain("local:alpha");

        _store.Upsert(E("second"), "local:alpha");

        _store.DiscoverTopics().Should().Contain("local:alpha",
            because: "narrowing invalidation must not stale the topic list");
    }

    [Fact]
    public void EmptyingATopicScope_RemovesItFromDiscoverTopics()
    {
        _store.Upsert(E("only"), "local:beta");
        _store.DiscoverTopics().Should().Contain("local:beta");

        _store.Remove("only", "local:beta");

        _store.DiscoverTopics().Should().NotContain("local:beta",
            because: "DiscoverTopics must not keep reporting a topic with no remaining entries");
    }

    [Fact]
    public void RemovingOneOfSeveralEntries_KeepsTheTopic()
    {
        _store.Upsert(E("a"), "local:gamma");
        _store.Upsert(E("b"), "local:gamma");

        _store.Remove("a", "local:gamma");

        _store.DiscoverTopics().Should().Contain("local:gamma");
    }

    /// <summary>
    /// Remove() deletes only the .meta.json sidecars, never the scope directory itself — so an
    /// emptied topic leaves an empty directory behind on disk. A later write into that same
    /// scope must still be treated as "new" for topic-discovery purposes: Directory.Exists
    /// alone cannot distinguish this case from an already-populated scope, which is why the
    /// invalidation check counts sidecars rather than checking directory existence.
    /// </summary>
    [Fact]
    public void RewritingIntoAPreviouslyEmptiedTopic_MakesItDiscoverableAgain()
    {
        _store.Upsert(E("only"), "local:delta");
        _store.Remove("only", "local:delta");
        _store.DiscoverTopics().Should().NotContain("local:delta");

        _store.Upsert(E("again"), "local:delta");

        _store.DiscoverTopics().Should().Contain("local:delta",
            because: "the directory left behind by Remove is empty, not absent, but writing "
                   + "into it still changes the topic list from the caller's perspective");
    }
}
