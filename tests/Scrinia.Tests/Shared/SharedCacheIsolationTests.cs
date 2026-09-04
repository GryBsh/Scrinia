using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Shared;

public sealed class SharedCacheIsolationTests
{
    [Fact]
    public void Cache_keys_include_canonical_root_and_base_name()
    {
        using var first = Root.Create("alpha");
        using var second = Root.Create("alpha");
        var factory = new SharedBaseRuntimeFactory();

        SharedBaseRuntime left = factory.Open(first.Entry);
        SharedBaseRuntime right = factory.Open(second.Entry);

        left.Should().NotBeSameAs(right);
        left.RegisteredRoot.Should().NotBe(right.RegisteredRoot);
        left.BaseName.Should().Be("alpha");
    }

    [Fact]
    public void Equal_logical_paths_in_two_bases_do_not_share_generation_state()
    {
        using var first = Root.Create("alpha");
        using var second = Root.Create("alpha");
        File.WriteAllText(Path.Combine(first.Path, ".scrinia", "same.txt"), "first");
        File.WriteAllText(Path.Combine(second.Path, ".scrinia", "same.txt"), "second");
        var factory = new SharedBaseRuntimeFactory();

        SharedBaseRuntime left = factory.Open(first.Entry);
        SharedBaseRuntime right = factory.Open(second.Entry);

        left.Snapshot.Generation.Should().NotBe(right.Snapshot.Generation);
        factory.Open(first.Entry).Should().BeSameAs(left);
        factory.Open(second.Entry).Should().BeSameAs(right);
    }

    private sealed class Root : IDisposable
    {
        public required string Path { get; init; }
        public required SharedBaseEntry Entry { get; init; }
        public static Root Create(string name)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scrinia-task3-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(path, ".scrinia"));
            return new Root { Path = path, Entry = new SharedBaseEntry { Name = name, Root = path } };
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
