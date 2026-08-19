using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// The CLI grew command by command with nothing checking the family for agreement, and it had
/// drifted: `scri migrate` was the only command taking --workspace where the other fourteen take
/// --workspace-root, and the only one whose backup flag defaulted ON, so it could not be written
/// as a bare --flag like every other switch.
///
/// <para>ConsoleAppFramework derives each option name from the parameter name, so these rules are
/// checkable by reflection over the entrypoints. Each rule carries a NAMED exclusion set; an
/// exclusion must be justified in code, never absent.</para>
/// </summary>
public sealed class CliSurfaceConformanceTests
{
    /// <summary>Public instance methods on these types that are deliberately NOT commands.
    /// Empty: both types exist to declare the CLI surface, so a public instance method on either
    /// is a command. An entry here would be a method the conformance rules stop covering.</summary>
    private static readonly HashSet<string> NotCommands = new(StringComparer.Ordinal);

    /// <summary>Commands allowed a parameter named `workspace`. Empty: the family's name for the
    /// workspace root is `workspaceRoot`, with no exception.</summary>
    private static readonly HashSet<string> WorkspaceNameExemptions = new(StringComparer.Ordinal);

    /// <summary>Commands allowed a bool parameter defaulting to true. Empty: a flag is opt-in, so
    /// its absence must mean "off". An entry here would be a switch a user cannot turn on.</summary>
    private static readonly HashSet<string> DefaultTrueBoolExemptions = new(StringComparer.Ordinal);

    private static IEnumerable<(Type Owner, MethodInfo Method)> Entrypoints() =>
        new[] { typeof(ScriniaCommands), typeof(BundleCommands) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(int) || m.ReturnType == typeof(Task<int>))
                .Select(m => (t, m)));

    [Fact]
    public void Entrypoints_AreFound()
    {
        // Guards the other three tests against passing vacuously if the reflection filter breaks.
        // Exact, not a loose floor: 20 entrypoints on ScriniaCommands plus 3 on BundleCommands.
        // A loose ">15" threshold caught a total filter break but not a partial one — e.g.
        // dropping BundleCommands entirely still leaves 20 > 15, so a real command class (and
        // any --workspace/bool-default regression on it) could fall out of every rule below
        // without this test noticing. Adding or removing a command must restate this number
        // deliberately — that restatement is the point of the guard.
        Entrypoints().Should().HaveCount(23,
            because: "the CLI has exactly 23 command entrypoints (20 on ScriniaCommands, 3 on " +
                     "BundleCommands); a filter that stops seeing some of them — even not all — " +
                     "would make the rules below pass without checking what it silently dropped");
    }

    [Fact]
    public void NoCommand_NamesItsWorkspaceRootParameterWorkspace()
    {
        using var scope = new AssertionScope();
        foreach (var (owner, method) in Entrypoints())
        {
            if (WorkspaceNameExemptions.Contains(method.Name)) continue;
            method.GetParameters().Select(p => p.Name).Should().NotContain("workspace",
                because: $"{owner.Name}.{method.Name} would expose --workspace where the rest of the " +
                         "family exposes --workspace-root");
        }
    }

    [Fact]
    public void EveryBoolOption_DefaultsToFalse()
    {
        using var scope = new AssertionScope();
        foreach (var (owner, method) in Entrypoints())
        {
            if (DefaultTrueBoolExemptions.Contains(method.Name)) continue;
            foreach (var p in method.GetParameters())
            {
                // Covers `bool?` too — a nullable-bool flag defaulting to true would evade the
                // opt-in rule this test exists to enforce just as a plain `bool` would.
                if (p.ParameterType != typeof(bool) && p.ParameterType != typeof(bool?)) continue;
                p.HasDefaultValue.Should().BeTrue(
                    because: $"{owner.Name}.{method.Name}'s --{p.Name} must be optional");
                p.DefaultValue.Should().Be(false,
                    because: $"{owner.Name}.{method.Name}'s --{p.Name} defaults to true, so the flag " +
                             "cannot be turned on by naming it — the family's flags are opt-in");
            }
        }
    }

    [Fact]
    public void EveryEntrypoint_CanReportFailure()
    {
        // The inverse of the Entrypoints() filter: every public instance method on these two
        // types must be able to carry an exit code. Do NOT narrow this by "takes a
        // CancellationToken" — ScriniaCommands.List does not take one, so that filter would
        // silently drop a real command and cover 22 of 23.
        using var scope = new AssertionScope();
        foreach (var type in new[] { typeof(ScriniaCommands), typeof(BundleCommands) })
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;
                if (NotCommands.Contains(m.Name)) continue;
                (m.ReturnType == typeof(int) || m.ReturnType == typeof(Task<int>)).Should().BeTrue(
                    because: $"{type.Name}.{m.Name} is part of the CLI surface but returns " +
                             $"{m.ReturnType.Name} — a command that cannot return an exit code cannot " +
                             "report failure; if it is not a command, name it in NotCommands");
            }
        }
    }
}
