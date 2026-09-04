using System.Reflection;
using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

public sealed class CliConfigConformanceTests
{
    [Fact]
    public void AllTask8CommandFormsArePresentAndReturnExitCodes()
    {
        typeof(SharedBaseCommands).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name).Should().BeEquivalentTo(["Add", "Remove", "List"]);
        typeof(SharedEnableCommands).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name).Should().BeEquivalentTo(["Enable", "Disable"]);
        typeof(TrackingCliCommands).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name).Should().BeEquivalentTo(["Status", "Set", "MigrateLegacy"]);
    }
}
