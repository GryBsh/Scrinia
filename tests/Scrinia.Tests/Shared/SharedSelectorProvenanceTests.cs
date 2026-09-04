using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

public sealed class SharedSelectorProvenanceTests
{
    [Fact]
    public void Selectors_are_enabled_only_and_preserve_base_provenance()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-task3-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".scrinia"));
        try
        {
            var workspace = new WorkspaceConfig(new Dictionary<string, string>
            {
                [WorkspaceConfig.EnabledKey] = "alpha"
            });
            var registry = new RecordingRegistry(new SharedBaseEntry { Name = "alpha", Root = root });
            var server = new SharedServer(workspace, registry, new SharedBaseRuntimeFactory());

            SharedBaseSelection selection = server.SelectBase("alpha");

            selection.Base.Should().Be("alpha");
            selection.Entry.Should().NotBeNull();
            selection.Entry!.Root.Should().Be(root);
            registry.Lookups.Should().Equal("alpha");
            server.SelectBase("disabled").Should().BeNull();
            registry.Lookups.Should().Equal("alpha");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingRegistry(SharedBaseEntry entry) : ISharedBaseRegistry
    {
        public List<string> Lookups { get; } = [];
        public SharedBaseResult Add(string name, string root) => throw new NotSupportedException();
        public SharedBaseResult Remove(string name) => throw new NotSupportedException();
        public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled) => [entry];
        public SharedBaseResult Enable(string name) => throw new NotSupportedException();
        public SharedBaseResult Disable(string name) => throw new NotSupportedException();
        public SharedBaseEntry? Lookup(string name)
        {
            Lookups.Add(name);
            return name == entry.Name ? entry : null;
        }
        public IReadOnlyList<SharedBaseCollision> Collisions() => [];
    }
}
