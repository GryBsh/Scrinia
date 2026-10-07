using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// Topic discovery reflects authoritative disk changes after local writes, removals, and updates.
/// Local entry assertions also prove that refreshing discovery does not lose the requested mutation.
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

    // Discovery recognizes physical sidecar markers without reading their metadata.
    // This external marker exercises freshness independently of local mutations.
    private void PlantExternalTopic(string scope)
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

    [Fact]
    public void AdditionalWritesKeepExternalTopicsVisible()
    {
        _store.Upsert(E("first"), "local:alpha");
        _store.DiscoverTopics().Should().Contain("local:alpha");
        PlantExternalTopic("local:epsilon");

        _store.Upsert(E("second"), "local:alpha");

        _store.DiscoverTopics().Should().BeEquivalentTo(["local:alpha", "local:epsilon"]);
        _store.LoadIndex("local:alpha").Select(e => e.Name).Should().BeEquivalentTo(["first", "second"]);
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
    public void RemovingOneEntryKeepsExternalTopicsVisible()
    {
        _store.Upsert(E("a"), "local:gamma");
        _store.Upsert(E("b"), "local:gamma");
        _store.DiscoverTopics().Should().Contain("local:gamma");
        PlantExternalTopic("local:zeta");

        _store.Remove("a", "local:gamma");

        _store.DiscoverTopics().Should().BeEquivalentTo(["local:gamma", "local:zeta"]);
        _store.LoadIndex("local:gamma").Should().ContainSingle().Which.Name.Should().Be("b");
    }

    [Fact]
    public void UpdatingAnEntryKeepsExternalTopicsVisible()
    {
        _store.Upsert(E("only"), "local:eta");
        _store.DiscoverTopics().Should().Contain("local:eta");
        PlantExternalTopic("local:theta");

        _store.TryUpdateEntry("only", "local:eta", e => e with { ContentPreview = "patched" }).Should().BeTrue();

        _store.DiscoverTopics().Should().BeEquivalentTo(["local:eta", "local:theta"]);
        _store.LoadIndex("local:eta").Should().ContainSingle().Which.ContentPreview.Should().Be("patched");
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
