using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

/// <summary>
/// Task 11 — Git boundary. The shared surface never launches a process: it prints exact Git commands
/// only (§7.1 / §7.2) and never executes them, never inspects the index, and never imports the
/// process-start APIs. The whole shared call graph stays filesystem-only.
/// </summary>
public sealed class SharedNoGitProcessTests
{
    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    private static readonly string[] SharedSurfaceFiles =
    [
        "src/Scrinia.Mcp/Shared/SharedTools.cs",
        "src/Scrinia.Mcp/Shared/SharedServer.cs",
        "src/Scrinia.Mcp/Shared/SharedGitGuidance.cs",
        "src/Scrinia/Commands/ScriniaCommands.SharedServe.cs",
        "src/Scrinia/Commands/SharedServeHostAdapter.cs",
    ];

    // Matches any process-start / process-start-info / System.Diagnostics.Process usage.
    private static readonly Regex ProcessApi = new(
        @"(Process\.Start|new\s+Process\s*\(|ProcessStartInfo|System\.Diagnostics\.Process)",
        RegexOptions.Compiled);

    [Fact]
    public void Shared_surface_never_starts_a_git_process()
    {
        foreach (string relative in SharedSurfaceFiles)
        {
            string source = File.ReadAllText(Path.Combine(RepositoryRoot, relative));
            ProcessApi.IsMatch(source).Should().BeFalse(
                "{0} must not launch any process — the shared surface is filesystem-only (§7.1)", relative);
        }

        // The only Git contact is print-only guidance: Render returns a string and takes only
        // operation inputs (registered root + manifest) — no process, no prompt.
        MethodInfo render = typeof(SharedGitGuidance).GetMethod(nameof(SharedGitGuidance.Render))!;
        render.Should().NotBeNull();
        render.ReturnType.Should().BeSameAs(typeof(string));
        ParameterInfo[] parameters = render.GetParameters();
        parameters.Select(p => p.Name?.ToLowerInvariant()).Should().BeEquivalentTo("registeredroot", "manifest");
    }
}
