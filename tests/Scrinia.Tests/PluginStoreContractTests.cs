using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Pins the narrowed sink contract. IPluginStore exists so a sink is handed what it needs
/// (two members) rather than all 30 of IMemoryStore's. Nothing enforces that but this file:
/// widening IPluginStore is a one-line edit that no other test would notice.
/// </summary>
public class PluginStoreContractTests
{
    [Fact]
    public void IPluginStore_DeclaresExactlyTheSinkPathMembers()
    {
        string[] declared = [.. typeof(IPluginStore).GetMembers().Select(m => m.Name).OrderBy(n => n)];

        declared.Should().BeEquivalentTo(
            ["ReadArtifactAsync", "ResolveAddress"],
            because: "the contract's value is its narrowness; adding a member here should be a " +
                     "deliberate act that updates this list, not a quiet widening");
    }

    [Fact]
    public void IMemoryStore_DerivesFromIPluginStore()
    {
        typeof(IPluginStore).IsAssignableFrom(typeof(IMemoryStore)).Should().BeTrue(
            because: "every real store must satisfy both, so a store can be handed to a sink " +
                     "without a cast or an adapter");
    }

    [Fact]
    public void IMemoryStore_StillDeclaresTheNamingMembersThatDidNotMove()
    {
        string[] names = [.. typeof(IMemoryStore).GetMembers().Select(m => m.Name)];

        names.Should().Contain("FormatQualifiedName").And.Contain("SanitizeName",
            because: "only the plugin-path members move; Core's own handlers use " +
                     "these two through the full store");
    }
}
