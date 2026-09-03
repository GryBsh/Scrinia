using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;
using Scrinia.Commands;
using Scrinia.Mcp;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Phase 1 merge-retirement gate for Task 3: the <c>reconcile</c> MCP tool and CLI command are
/// gone from every agent-visible surface — CLI entrypoint set, reflected MCP tool set, golden
/// responses, the embedded guide, and the restore conflict warning — while the Phase 2
/// migration-only recognition contract (<c>LegacyMergeRecognition</c> + its test) is left exactly
/// in place.
///
/// <para>Task 2 already deleted the tool methods, the command, the golden snapshot, and the active
/// reconcile tests. This file pins that state so a future re-introduction of reconcile goes red
/// here rather than silently reappearing. The embedded-guide and restore-warning rules drive the
/// cleanup of the residual reconcile references that Task 2's deletion did not reach; the CLI,
/// reflection, golden, and retention rules are regression guards for the already-removed surface.
/// Every filesystem path is built with <see cref="RepoPaths.Combine"/> so deleted-directory and
/// golden-case assertions inspect the real path, not a platform-literal ghost.</para>
///
/// <para>The CLI assertion goes two ways: the reflection filter proves reconcile is not a
/// registered entrypoint, and <c>Reconcile_IsRejectedByTheRealCliParser</c> drives the actual
/// <c>scri</c> process to prove the parser itself rejects reconcile as unknown. The two cannot
/// be collapsed into one because ConsoleAppFramework's source generator forbids registering
/// command types across project boundaries (CAF014), so the parser cannot be constructed from the
/// test project — the subprocess is the only way to exercise the real dispatch path.</para>
/// </summary>
public sealed class ReconcileRemovalTests
{
    /// <summary>The CLI exposes no <c>reconcile</c> command. Mirrors the
    /// <c>CliSurfaceConformanceTests.Entrypoints</c> filter: public instance methods on the two
    /// registered command classes that return an exit code.</summary>
    [Fact]
    public void Reconcile_IsNotACliEntrypoint()
    {
        var commandNames = new[] { typeof(ScriniaCommands), typeof(BundleCommands) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.ReturnType == typeof(int) || method.ReturnType == typeof(Task<int>))
            .Select(method => method.Name.ToLowerInvariant());

        commandNames.Should().NotContain("reconcile",
            "scri reconcile must not be a CLI command after Task 3 removed the reconcile surface");
    }

    /// <summary>
    /// Runs the built <c>scri</c> CLI as a real process against a throwaway workspace, returning
    /// its combined exit code and stdout/stderr. The CLI assembly is located off the
    /// <c>ScriniaCommands</c> type so the test runs whatever binary the project actually produced
    /// rather than a path baked at build time.
    /// </summary>
    private static async Task<(int Exit, string Output)> RunScriAsync(string workspaceRoot, params string[] args)
    {
        string scriDll = typeof(ScriniaCommands).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(scriDll);
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        // Fully qualified: the Scrinia.Tests namespace declares its own `Process` test double,
        // which would otherwise shadow System.Diagnostics.Process at the call site.
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(30000);
        if (!exited || !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"scri did not exit within 30s for args [{string.Join(" ", args)}]");
        }

        return (process.ExitCode, output + error);
    }

    /// <summary>The real scri parser rejects <c>reconcile</c> as an unknown command: it prints the
    /// usage listing of registered commands and runs no handler. The reflection guard above cannot
    /// reach this because ConsoleAppFramework's source generator forbids registering command types
    /// across project boundaries (CAF014), so this drives the actual <c>app.RunAsync</c> dispatch.
    /// Confirms reconcile is unknown at the parser, not just the symbol level.</summary>
    [Fact]
    public async Task Reconcile_IsRejectedByTheRealCliParser()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "scrinia-rt3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var (exit, output) = await RunScriAsync(workspace, "reconcile", "--workspace-root", workspace);

            output.Should().Contain("Usage:",
                "an unknown command makes the CLI print its usage listing, proving reconcile is " +
                "rejected by the parser rather than dispatched to a handler");
            exit.Should().Be(0, "ConsoleAppFramework reports an unknown command by printing usage " +
                                "and returning 0, not by throwing");
            output.Should().NotContain("reconcile",
                "reconcile must not appear as a registered command in the real CLI usage listing");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    /// <summary>The MCP facade exposes no <c>reconcile</c> tool and no <c>Reconcile</c>/<c>MemoryReconcile</c>
    /// method. Tool identity is read off the reflected <c>[McpServerTool]</c> name so a renamed
    /// wrapper (e.g. <c>MemoryReconcile</c>) still goes red.</summary>
    [Fact]
    public void Reconcile_IsNotAnMcpTool()
    {
        var toolNames = typeof(ScriniaMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttributes(false)
                .Any(attribute => attribute.GetType().Name == "McpServerToolAttribute"))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        toolNames.Should().NotContain("reconcile",
            "reconcile must not be a reflected MCP tool after Task 3 removed the reconcile surface");

        var methodNames = typeof(ScriniaMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        methodNames.Should().NotContain("reconcile");
        methodNames.Should().NotContain("memoryreconcile",
            "no active Reconcile/MemoryReconcile method may survive Task 3");
    }

    private static readonly string[] GoldenSnapshots = Directory
        .GetFiles(RepoPaths.Combine("tests", "Scrinia.Tests", "Golden", "__snapshots__"), "*.yaml")
        .ToArray();

    /// <summary>Task 2 deleted <c>reconcile-scan.yaml</c>. The retired filename must stay absent
    /// — an explicit name check, not just a content scan — because a snapshot re-added under a
    /// different name (e.g. <c>reconcile-conflict.yaml</c>) would slip past a body-only scan. The
    /// content scan below still catches any stale reconcile text in surviving snapshots.</summary>
    [Fact]
    public void Reconcile_HasNoGoldenCase()
    {
        var reNamed = GoldenSnapshots
            .Where(snapshot => Path.GetFileName(snapshot).Contains("reconcile", StringComparison.OrdinalIgnoreCase))
            .ToList();
        reNamed.Should().BeEmpty(
            "no golden snapshot may be named like reconcile-scan.yaml after Task 3 removed it: {0}",
            reNamed.Select(Path.GetFileName));

        var referencing = GoldenSnapshots
            .Where(snapshot => File.ReadAllText(snapshot).Contains("reconcile", StringComparison.OrdinalIgnoreCase))
            .ToList();

        referencing.Should().BeEmpty(
            "no golden snapshot may reference reconcile after Task 3: {0}", referencing);
    }

    /// <summary>The embedded guide describes only live tools. reconcile was a documented tool; its
    /// section must be gone. Drives the guide.md cleanup that Task 2's deletion left behind.</summary>
    [Fact]
    public void EmbeddedGuide_DescribesNoReconcileTool()
    {
        string guide = EmbeddedPrompts.LoadGuide()
            ?? throw new InvalidOperationException("Built-in guide not found in embedded resources");

        guide.Should().NotContain("reconcile",
            "the embedded guide must not describe reconcile as a tool after Task 3 removed it");
    }

    /// <summary>Restore still detects an unresolved conflict (single-user restore behavior
    /// preserved) but must not instruct the agent to run a tool that no longer exists. Drives the
    /// warning-string cleanup in <c>BuildRestoreContextAsync</c>.</summary>
    [Fact]
    public async Task Restore_ConflictWarning_DetectsButDoesNotNameReconcile()
    {
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(scriniaDir);
        File.WriteAllText(Path.Combine(scriniaDir, "conflict.txt"), "<<<<<<< ours\nfoo\n");

        var response = ResponseParser.Parse(await new ScriniaMcpTools().Guide());

        var conflictWarnings = response.ActionNeeded
            .Where(warning => warning.Contains("unresolved merge conflict", StringComparison.OrdinalIgnoreCase))
            .ToList();

        conflictWarnings.Should().HaveCountGreaterThanOrEqualTo(1,
            "restore must still surface the unresolved-conflict warning — single-user restore " +
            "behavior is preserved, only the dangling tool reference is retired");

        conflictWarnings.Should().NotContain(warning => warning.Contains("reconcile", StringComparison.OrdinalIgnoreCase),
            "the restore conflict warning must not point at the removed reconcile tool");
    }

    /// <summary>The Phase 2 recognition contract survives Task 3 untouched — only reconcile surfaces
    /// are retired, not the migration-only contract owned by Phase 2.</summary>
    [Fact]
    public void LegacyMergeRecognition_ContractRetained()
    {
        File.Exists(RepoPaths.Combine("src", "Scrinia.Core", "Migration", "LegacyMergeRecognition.cs"))
            .Should().BeTrue("LegacyMergeRecognition.cs must survive Task 3 (Phase 2 migration contract).");
        File.Exists(RepoPaths.Combine("tests", "Scrinia.Tests", "LegacyMergeRecognitionTests.cs"))
            .Should().BeTrue("LegacyMergeRecognitionTests.cs must survive Task 3 (Phase 2 migration contract).");
    }
}
