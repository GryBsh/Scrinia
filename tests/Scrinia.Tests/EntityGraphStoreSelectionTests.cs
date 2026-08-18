using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class EntityGraphStoreSelectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public EntityGraphStoreSelectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-egsel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private void Upsert(string name, string scope, params string[] keywords) =>
        _store.Upsert(new ArtifactEntry(name, $"test://{name}", 0, 0, DateTimeOffset.UtcNow, Keywords: keywords), scope);

    [Fact]
    public void UnfilteredCacheBranch_EqualsBuildFromAllCandidates()
    {
        // The call site substitutes GetEntityFactStore() for BuildStore(ListScoped(null)) in the
        // fully-unfiltered case. That substitution must be behavior-identical.
        Upsert("d1", "local", "person:alice");
        Upsert("d2", "local", "person:alice", "person:bob");
        Upsert("d3", "local", "person:bob");
        var contributor = new EntityGraphScoreContributor();

        var viaCache = contributor.Score(_store.GetEntityFactStore(), "alice");
        var viaBuild = contributor.Score(EntityFactDerivation.BuildStore(_store.ListScoped(null)), "alice");

        viaCache.Should().NotBeNull();
        viaCache!.Keys.Should().BeEquivalentTo(viaBuild!.Keys);
        foreach (var (k, v) in viaBuild)
            viaCache[k].Should().BeApproximately(v, 1e-9);
    }

    [Fact]
    public void ExcludedTopicBridge_DropsTheAssociation()
    {
        // A bridge memory in an excludable topic connects alice<->bob. When that topic is excluded,
        // the bridge is absent from the built store, so bob's memory loses the alice-seeded lift.
        Upsert("d_alice", "local", "person:alice");
        Upsert("d_bob", "local", "person:bob");
        Upsert("bridge", "local:plan", "person:alice", "person:bob"); // the only alice-bob link

        var contributor = new EntityGraphScoreContributor();

        var withBridge = contributor.Score(_store.GetEntityFactStore(), "alice");
        var excluded = contributor.Score(
            EntityFactDerivation.BuildStore(_store.ListScoped(null, excludeTopics: ["plan"])), "alice");

        withBridge!.Should().ContainKey("local|d_bob");          // reached via the bridge
        (excluded is null || !excluded.ContainsKey("local|d_bob")).Should().BeTrue(); // bridge gone -> no lift
    }
}
