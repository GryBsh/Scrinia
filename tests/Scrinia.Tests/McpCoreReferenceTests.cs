using FluentAssertions;
using Scrinia.TestDoubles;

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
}
