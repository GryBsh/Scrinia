using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Shared;

public sealed class SharedCacheIsolationTests
{
    [Fact]
    public void Cache_keys_include_canonical_root_and_base_name()
    {
        using var rootAlphaOne = Root.Create("alpha");
        using var rootAlphaTwo = Root.Create("alpha");
        using var rootBetaOne = Root.Create("beta");
        var factory = new SharedBaseRuntimeFactory();

        SharedBaseRuntime alphaAtOne = factory.Open(rootAlphaOne.WithName("alpha").Entry);
        SharedBaseRuntime alphaAtTwo = factory.Open(rootAlphaTwo.WithName("alpha").Entry);
        SharedBaseRuntime alphaAtOneAgain = factory.Open(rootAlphaOne.WithName("alpha").Entry);
        SharedBaseRuntime betaAtOne = factory.Open(rootBetaOne.WithName("beta").Entry);

        // Same name, different canonical root -> distinct runtimes (root is part of the key).
        alphaAtOne.Should().NotBe(alphaAtTwo);
        // Same identity is cached and stable (both root and name are part of the key).
        alphaAtOne.Should().BeSameAs(alphaAtOneAgain);
        // Different name, same root -> distinct runtime (name is part of the key).
        alphaAtOne.Should().NotBe(betaAtOne);
        // Each runtime carries its own canonical identity, never a cross-base alias.
        alphaAtOne.BaseName.Should().Be("alpha");
        alphaAtTwo.BaseName.Should().Be("alpha");
        betaAtOne.BaseName.Should().Be("beta");
        alphaAtOne.RegisteredRoot.Should().NotBe(alphaAtTwo.RegisteredRoot);
    }

    [Fact]
    public void Registry_root_repoint_requires_restart_or_refresh_and_does_not_reuse_prior_generation()
    {
        using var rootOne = Root.Create("alpha");
        using var rootTwo = Root.Create("alpha");
        File.WriteAllText(Path.Combine(rootOne.Path, ".scrinia", "shared.txt"), "first");
        File.WriteAllText(Path.Combine(rootTwo.Path, ".scrinia", "shared.txt"), "second");

        var registry = new SharedBaseRegistryFixture();
        registry.Register("alpha", rootOne.Path);

        var factory = new SharedBaseRuntimeFactory();
        SharedBaseRuntime prior = factory.Open(registry.Lookup("alpha")!);
        string priorGeneration = prior.Snapshot.Generation!;

        // Repoint the same registry name to the second physical root.
        registry.Repoint("alpha", rootTwo.Path);

        // Restart path: a fresh factory resolves only the newly repointed root's generation.
        var restarted = new SharedBaseRuntimeFactory();
        SharedBaseRuntime afterRestart = restarted.Open(registry.Lookup("alpha")!);
        afterRestart.Snapshot.Generation.Should().NotBe(priorGeneration);
        afterRestart.Snapshot.Fingerprint.Should().NotBe(prior.Snapshot.Fingerprint);

        // Reopen path: the same factory now serves the repointed root with a reconstructed runtime.
        SharedBaseRuntime afterReopen = factory.Open(registry.Lookup("alpha")!);
        afterReopen.Should().NotBe(prior);
        afterReopen.Snapshot.Generation.Should().NotBe(priorGeneration);
        afterReopen.Snapshot.Fingerprint.Should().NotBe(prior.Snapshot.Fingerprint);

        // Explicit refresh on the repointed runtime returns only the second root's generation.
        SharedRefreshCommit refresh = afterReopen.Refresh();
        refresh.PreviousGeneration.Should().NotBe(priorGeneration);
        refresh.Snapshot.Generation.Should().NotBe(priorGeneration);
        refresh.Snapshot.Fingerprint.Should().Be(afterReopen.Snapshot.Fingerprint);
    }

    [Fact]
    public void Equal_logical_paths_in_two_bases_do_not_share_index_facts_vectors_or_cache_state()
    {
        using var rootOne = Root.Create("alpha");
        using var rootTwo = Root.Create("alpha");

        // Same canonical logical memory path in both bases, seeded with different content.
        const string logicalPath = "memories/notes/greeting.nmp2";
        string bodyOne = Path.Combine(rootOne.Path, ".scrinia", logicalPath);
        string bodyTwo = Path.Combine(rootTwo.Path, ".scrinia", logicalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(bodyOne)!);
        Directory.CreateDirectory(Path.GetDirectoryName(bodyTwo)!);
        File.WriteAllText(bodyOne, "NMP/2 one");
        File.WriteAllText(bodyTwo, "NMP/2 two");

        var factory = new SharedBaseRuntimeFactory();
        SharedBaseRuntime left = factory.Open(rootOne.Entry);
        SharedBaseRuntime right = factory.Open(rootTwo.Entry);

        // Warm every read/cache layer in both runtimes.
        _ = left.Snapshot;
        _ = right.Snapshot;
        _ = left.Snapshot.Entries;
        _ = right.Snapshot.Entries;
        _ = left.Snapshot.Fingerprint;
        _ = right.Snapshot.Fingerprint;
        left.RevalidateBeforeMutation("/" + logicalPath, null);
        right.RevalidateBeforeMutation("/" + logicalPath, null);

        // Equal logical path but distinct generation records; the index/facts/vector derived
        // from authoritative content cannot be shared across the two canonical roots.
        left.Snapshot.Generation.Should().NotBe(right.Snapshot.Generation);
        left.Snapshot.Fingerprint.Should().NotBe(right.Snapshot.Fingerprint);
        left.RegisteredRoot.Should().NotBe(right.RegisteredRoot);
    }

    private sealed class Root : IDisposable
    {
        public required string Path { get; init; }
        public required SharedBaseEntry Entry { get; init; }

        public Root WithName(string name) =>
            new() { Path = Path, Entry = new SharedBaseEntry { Name = name, Root = Path } };

        public static Root Create(string name)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scrinia-task7a-cache-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(path, ".scrinia"));
            return new Root { Path = path, Entry = new SharedBaseEntry { Name = name, Root = path } };
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class SharedBaseRegistryFixture : ISharedBaseRegistry
    {
        private readonly Dictionary<string, SharedBaseEntry> _entries = new(StringComparer.Ordinal);

        public SharedBaseResult Add(string name, string root) => Register(name, root);

        public SharedBaseResult Register(string name, string root)
        {
            _entries[name] = new SharedBaseEntry { Name = name, Root = root };
            return new SharedBaseResult(Base: new SharedBaseEntry { Name = name, Root = root });
        }

        public SharedBaseResult Remove(string name)
        {
            _entries.Remove(name);
            return new SharedBaseResult();
        }

        public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled) =>
            _entries.Values.ToArray();

        public SharedBaseResult Enable(string name) =>
            _entries.TryGetValue(name, out SharedBaseEntry? entry) ? new SharedBaseResult(Base: entry) : new();

        public SharedBaseResult Disable(string name) => new();

        public SharedBaseEntry? Lookup(string name) =>
            _entries.TryGetValue(name, out SharedBaseEntry? entry) ? entry : null;

        public IReadOnlyList<SharedBaseCollision> Collisions() => [];

        public void Repoint(string name, string root)
        {
            if (!_entries.TryGetValue(name, out SharedBaseEntry? existing))
                Add(name, root);
            else
                _entries[name] = new SharedBaseEntry { Name = name, Root = root };
        }
    }
}
