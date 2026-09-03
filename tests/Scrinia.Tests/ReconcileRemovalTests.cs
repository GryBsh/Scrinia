using System.Reflection;
using System.Text.RegularExpressions;
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
/// </summary>
public sealed class ReconcileRemovalTests
{
    private static readonly string[] GoldenSnapshots = Directory
        .GetFiles(RepoPaths.Combine("tests", "Scrinia.Tests", "Golden", "__snapshots__"), "*.yaml")
        .ToArray();

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

    /// <summary>No golden response case references reconcile. Task 2 deleted reconcile-scan.yaml;
    /// a re-added or orphaned snapshot would surface here.</summary>
    [Fact]
    public void Reconcile_HasNoGoldenCase()
    {
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

    /// <summary>When restore detects an unresolved conflict it must not instruct the agent to run a
    /// tool that no longer exists. Drives the warning-string cleanup in
    /// <c>BuildRestoreContextAsync</c>.</summary>
    [Fact]
    public async Task Restore_ConflictWarning_DoesNotNameReconcile()
    {
        using var scope = new TestHelpers.StoreScope();

        string scriniaDir = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(scriniaDir);
        File.WriteAllText(Path.Combine(scriniaDir, "conflict.txt"), "<<<<<<< ours\nfoo\n");

        var response = ResponseParser.Parse(await new ScriniaMcpTools().Guide());

        response.ActionNeeded
            .Where(warning => warning.Contains("unresolved merge conflict", StringComparison.OrdinalIgnoreCase))
            .Should().NotContain(warning => warning.Contains("reconcile", StringComparison.OrdinalIgnoreCase),
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
