using FluentAssertions;
using Scrinia.TestDoubles;
using Scrinia.Commands;
using ConsoleAppFramework;
using System.Reflection;

namespace Scrinia.Tests;

public sealed class McpCoreReferenceTests
{
    [Fact]
    public void McpReferencesCoreAndNeverExecutable()
    {
        string mcpProject = File.ReadAllText(RepoPaths.Combine("src", "Scrinia.Mcp", "Scrinia.Mcp.csproj"));
        mcpProject.Should().Contain("Scrinia.Core\\Scrinia.Core.csproj");
        mcpProject.Should().NotContain("Scrinia\\Scrinia.csproj");
    }

    [Fact]
    public void ConfigurationContractsAreNotDuplicatedInCliAdapters()
    {
        string[] adapters = ["SharedBaseCommands.cs", "SharedEnableCommands.cs", "TrackingCommands.cs"];
        foreach (string adapter in adapters)
        {
            string source = File.ReadAllText(RepoPaths.Combine("src", "Scrinia", "Commands", adapter));
            source.Should().NotContain("class HomeConfig");
            source.Should().NotContain("class SharedBaseRegistry");
            source.Should().NotContain("enum ConfigurationErrorCode");
        }
    }

    [Fact]
    public void ConfigurationCommandsHaveExactArgumentAndOptionBindings()
    {
        AssertParameters<SharedBaseCommands>(nameof(SharedBaseCommands.Add), ["name", "root", "json"], ["name", "root"]);
        AssertParameters<SharedBaseCommands>(nameof(SharedBaseCommands.Remove), ["name", "json"], ["name"]);
        AssertParameters<SharedBaseCommands>(nameof(SharedBaseCommands.List), ["all", "json"], []);
        AssertParameters<SharedEnableCommands>(nameof(SharedEnableCommands.Enable), ["name", "json"], ["name"]);
        AssertParameters<SharedEnableCommands>(nameof(SharedEnableCommands.Disable), ["name", "json"], ["name"]);
        AssertParameters<TrackingCliCommands>(nameof(TrackingCliCommands.Status), ["json"], []);
        AssertParameters<TrackingCliCommands>(nameof(TrackingCliCommands.Set), ["policy", "json"], ["policy"]);
        AssertParameters<TrackingCliCommands>(nameof(TrackingCliCommands.MigrateLegacy), ["to", "json"], []);
        typeof(TrackingCliCommands).GetMethod(nameof(TrackingCliCommands.MigrateLegacy))!
            .GetParameters().Single(parameter => parameter.Name == "to")
            .GetCustomAttributesData().Should().NotContain(attribute => attribute.AttributeType.Name == "ArgumentAttribute");
    }

    private static void AssertParameters<T>(string methodName, string[] names, string[] argumentNames)
    {
        ParameterInfo[] parameters = typeof(T).GetMethod(methodName)!.GetParameters();
        parameters.Select(parameter => parameter.Name).Should().Equal(names);
        parameters.Where(parameter => parameter.GetCustomAttributesData()
                .Any(attribute => attribute.AttributeType.Name == "ArgumentAttribute"))
            .Select(parameter => parameter.Name).Should().BeEquivalentTo(argumentNames);
        parameters.Where(parameter => !parameter.GetCustomAttributesData()
                .Any(attribute => attribute.AttributeType.Name == "ArgumentAttribute"))
            .Select(parameter => parameter.Name).Should().NotContain("workspace");
    }
}
