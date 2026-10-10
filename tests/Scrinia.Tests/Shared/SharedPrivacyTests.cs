using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

public sealed class SharedPrivacyTests
{
    [Fact]
    public void Disabled_names_and_roots_never_appear_in_any_shared_surface()
    {
        var enabledEntry = new SharedBaseEntry { Name = "enabled", Root = "C:\\shared\\enabled" };
        var disabledEntry = new SharedBaseEntry { Name = "disabled", Root = "C:\\private\\disabled" };
        var registry = new RecordingRegistry(enabledEntry, disabledEntry);
        var server = new SharedServer(
            new WorkspaceConfig(new Dictionary<string, string> { [WorkspaceConfig.EnabledKey] = "enabled;missing" }),
            registry,
            new SharedBaseRuntimeFactory());

        IReadOnlyList<SharedBaseSelection> selections = server.SelectEnabledBases();
        IReadOnlyList<SharedBaseSummary> summaries = server.GetEnabledBaseSummaries();

        selections.Select(selection => selection.Base).Should().Equal("enabled", "missing");
        summaries.Select(summary => summary.Base).Should().Equal("enabled", "missing");
        summaries.Select(summary => summary.Root).Should().Equal("C:\\shared\\enabled", null);
        summaries.Should().NotContain(summary => summary.Base == "disabled");
        summaries.Should().NotContain(summary => summary.Root == "C:\\private\\disabled");
        server.SelectBase("disabled").Should().BeNull();
        Action openDisabled = () => server.OpenSelectedBase("disabled");
        openDisabled.Should().Throw<SharedAddressException>()
            .Which.Message.Should().NotContain("disabled");
        registry.ListCalls.Should().Be(0, "the complete registry is an explicit private CLI surface");
        // Each enabled name is resolved exactly once at startup, including the absent entry.
        registry.Lookups.Should().Equal("enabled", "missing");
    }

    private sealed class RecordingRegistry(params SharedBaseEntry[] entries) : ISharedBaseRegistry
    {
        public List<string> Lookups { get; } = [];
        public int ListCalls { get; private set; }
        public SharedBaseResult Add(string name, string root) => throw new NotSupportedException();
        public SharedBaseResult Remove(string name) => throw new NotSupportedException();
        public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled) { ListCalls++; return entries; }
        public SharedBaseResult Enable(string name) => throw new NotSupportedException();
        public SharedBaseResult Disable(string name) => throw new NotSupportedException();
        public SharedBaseEntry? Lookup(string name)
        {
            Lookups.Add(name);
            return entries.SingleOrDefault(entry => entry.Name == name);
        }
        public IReadOnlyList<SharedBaseCollision> Collisions() => [];
    }
}
