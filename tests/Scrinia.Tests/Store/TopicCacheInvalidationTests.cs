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

    /// <summary>
    /// Plants a topic directly on disk, bypassing the store entirely, so it is invisible to any
    /// currently-cached <c>_cachedTopics</c> but WOULD be picked up by a fresh directory scan.
    /// DiscoverTopics only checks for the presence of a "*.meta.json"/"*.nmp2" file — it never
    /// parses one — so an empty file with the right extension is sufficient. Used to prove a
    /// cache survived an operation: if DiscoverTopics still does not see the phantom topic
    /// afterward, the operation did not force a rescan.
    /// </summary>
    private void PlantPhantomTopicBehindTheStoresBack(string scope)
    {
        string dir = _store.GetStoreDirForScope(scope);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ghost.meta.json"), "{}");
    }

    [Fact]
    public void ANewTopicScope_AppearsInDiscoverTopics_AfterItsFirstWrite()
    {
        _store.DiscoverTopics().Should().NotContain("local:alpha");

        _store.Upsert(E("first"), "local:alpha");

        _store.DiscoverTopics().Should().Contain("local:alpha",
            because: "the first write into a new topic scope creates its directory, which changes "
                   + "the topic list");
    }

    /// <summary>
    /// Diagnostic for the narrowing itself, not just correctness: a content-only assertion here
    /// (e.g. "still Contain local:alpha") would pass identically whether the cache stayed warm
    /// or was invalidated and rescanned, because the true on-disk state contains "alpha" either
    /// way — over-invalidation and a correctly-served warm cache are indistinguishable to it. So
    /// this plants a topic behind the store's back before the write under test, and asserts
    /// DiscoverTopics still cannot see it afterward — proving the write into an
    /// already-populated topic did NOT force a rescan.
    /// </summary>
    [Fact]
    public void AdditionalWritesIntoAnExistingTopic_DoNotInvalidateTheCache()
    {
        _store.Upsert(E("first"), "local:alpha");
        _store.DiscoverTopics().Should().Contain("local:alpha"); // primes the cache

        PlantPhantomTopicBehindTheStoresBack("local:epsilon");

        _store.Upsert(E("second"), "local:alpha");

        _store.DiscoverTopics().Should().NotContain("local:epsilon",
            because: "a write into an already-populated topic must not invalidate _cachedTopics "
                   + "— if it did, this call would rescan the filesystem and pick up the topic "
                   + "planted directly on disk above");
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

    /// <summary>
    /// Diagnostic for the narrowing itself — see
    /// <see cref="AdditionalWritesIntoAnExistingTopic_DoNotInvalidateTheCache"/> for why a
    /// content-only assertion can't distinguish a warm cache from an invalidated-and-correctly
    /// -rescanned one. Plants a topic behind the store's back before the removal under test, and
    /// asserts DiscoverTopics still cannot see it afterward — proving a removal that leaves
    /// other entries behind did NOT force a rescan.
    /// </summary>
    [Fact]
    public void RemovingOneOfSeveralEntries_DoesNotInvalidateTheCache()
    {
        _store.Upsert(E("a"), "local:gamma");
        _store.Upsert(E("b"), "local:gamma");
        _store.DiscoverTopics().Should().Contain("local:gamma"); // primes the cache

        PlantPhantomTopicBehindTheStoresBack("local:zeta");

        _store.Remove("a", "local:gamma");

        _store.DiscoverTopics().Should().NotContain("local:zeta",
            because: "removing one of several entries must not invalidate _cachedTopics — if it "
                   + "did, this call would rescan the filesystem and pick up the topic planted "
                   + "directly on disk above");
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
