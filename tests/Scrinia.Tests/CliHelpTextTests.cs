using System.Diagnostics;
using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

public sealed class CliHelpTextTests
{
    [Fact]
    public async Task RestoreHelp_UsesTaskFirstSummary()
    {
        string help = await GetHelpAsync("restore");

        help.Should().Contain("Restore saved agent context.");
    }

    [Fact]
    public async Task ListHelp_DescribesTheOptInSummaryOption()
    {
        string help = await GetHelpAsync("list");

        help.Should().Contain("List stored memories.");
        help.Should().Contain("--summary");
        help.Should().Contain("Show summary (topics, keywords, stats) instead of full table.");
    }

    [Fact]
    public async Task FactsHelp_ExposesWorkspaceRoot()
    {
        string help = await GetHelpAsync("facts");

        help.Should().Contain("Query entity facts.");
        help.Should().Contain("--workspace-root");
    }

    [Fact]
    public async Task MaintenanceHelp_DescribesBoundedBatchCleanup()
    {
        string help = await GetHelpAsync("maintenance");

        help.Should().Contain("Clean eligible archive pairs and verified terminal recovery evidence.");
        help.Should().Contain("--batch-size");
    }

    [Fact]
    public async Task ServeHelp_ExposesTheEmbeddingSetupOption()
    {
        string help = await GetHelpAsync("serve");

        help.Should().Contain("Start the local MCP server over stdio.");
        help.Should().Contain("--no-auto-setup");
    }

    [Fact]
    public async Task SharedServeHelp_DescribesTheSharedStdioServerAndWorkspaceRoot()
    {
        string help = await GetHelpAsync("shared", "serve");

        help.Should().Contain("Serve enabled shared-memory MCP tools over stdio.");
        help.Should().Contain("--workspace-root");
    }

    [Theory]
    [InlineData("store")]
    [InlineData("show")]
    [InlineData("forget")]
    [InlineData("append")]
    [InlineData("compact")]
    public async Task MemoryCommandHelp_DescribesOnlyAPersistentMemoryAddress(string command)
    {
        string help = await GetHelpAsync(command);

        help.Should().Contain("Persistent memory address (e.g. '/api/auth-flow').");
        help.Should().NotContain("~scratch");
        help.Should().NotContain("MCP supports ephemeral addresses");
    }

    [Fact]
    public async Task StoreHelp_DescribesFileAndStandardInputSources()
    {
        string help = await GetHelpAsync("store");

        help.Should().Contain("Store text read from a file or standard input as a named memory.");
    }

    [Fact]
    public async Task SearchHelp_DescribesAllLexicallySearchableMemoryFields()
    {
        string help = await GetHelpAsync("search");

        help.Should().Contain("names, content, and attributes");
    }

    private static async Task<string> GetHelpAsync(params string[] command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(ScriniaCommands).Assembly.Location);
        foreach (string segment in command)
            startInfo.ArgumentList.Add(segment);
        startInfo.ArgumentList.Add("--help");

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(0);
        return output + error;
    }
}
