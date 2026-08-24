using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Scrinia.Commands;
using Scrinia.TestDoubles;

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

    // { typeof(ScriniaCommands), typeof(BundleCommands) } is hardcoded here rather than derived
    // at runtime — ConsoleAppFramework's app object exposes no public list of what was registered
    // onto it, and Program.cs is a top-level statements file with no entry point to invoke short
    // of running the CLI. Entrypoints_CoverEveryTypeProgramCsRegisters() below closes the gap this
    // would otherwise leave: it parses Program.cs's `app.Add<T>()` calls from source and asserts
    // that set equals this one, so a third registered command class goes red there instead of
    // silently passing every rule in this file uncovered.
    private static IEnumerable<(Type Owner, MethodInfo Method)> Entrypoints() =>
        new[] { typeof(ScriniaCommands), typeof(BundleCommands) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(int) || m.ReturnType == typeof(Task<int>))
                .Select(m => (t, m)));

    /// <summary>Every command type registered onto the ConsoleAppFramework app in Program.cs via
    /// <c>app.Add&lt;T&gt;()</c>, parsed from source. Mirrors the pattern
    /// PublishManifestTests.ReleaseWorkflowProjects/SolutionProjectPaths already use for
    /// release.yml/Scrinia.sln: the authoritative list is a file on disk, not something reflection
    /// over a running process can see here.
    ///
    /// <para>Residual risk, not exploitable today: this regex assumes Program.cs's actual shape —
    /// every registration written as a single-line, unqualified <c>app.Add&lt;T&gt;()</c> call,
    /// where <c>app</c> is exactly that identifier. A registration split across lines, using a
    /// receiver variable other than <c>app</c> (e.g. a locally-scoped builder), or
    /// namespace-qualified (<c>app.Add&lt;Scrinia.Commands.Foo&gt;()</c>) would not match and would
    /// silently drop that entry from the parsed set. <see cref="Entrypoints_CoverEveryTypeProgramCsRegisters"/>'s
    /// <c>NotEmpty()</c> guard only catches the regex matching NOTHING (e.g. Program.cs rewritten
    /// so no line matches at all) — it has no way to detect a PARTIAL match, where some
    /// registrations are found and others are silently missed. If Program.cs's registration shape
    /// ever changes, this regex needs to change with it.</para></summary>
    private static HashSet<string> RegisteredCommandTypeNames()
    {
        string programCs = File.ReadAllText(RepoPaths.Combine("src", "Scrinia", "Program.cs"));
        return Regex.Matches(programCs, @"app\.Add<(\w+)>")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Entrypoints_CoverEveryTypeProgramCsRegisters()
    {
        var registered = RegisteredCommandTypeNames();
        registered.Should().NotBeEmpty(because: "the regex must actually find Program.cs's app.Add<T>() calls");

        var covered = new[] { typeof(ScriniaCommands), typeof(BundleCommands) }
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Registering a third command class in Program.cs (app.Add<SomeNewCommands>()) makes this
        // go red instead of leaving that class checked by no rule in this file: Entrypoints_AreFound's
        // exact HaveCount(23) only catches a class being DROPPED from the two known types, it
        // cannot see one ADDED alongside them.
        registered.Should().BeEquivalentTo(covered,
            because: "every command class Program.cs registers with app.Add<T>() must also be in " +
                     "the { typeof(ScriniaCommands), typeof(BundleCommands) } list Entrypoints() " +
                     "reflects over — otherwise a newly registered command class is covered by none " +
                     "of the rules in this file");
    }

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
                // `bool?` may default to null OR false: the rule's intent is "absence means off",
                // and for a tri-state flag (null/true/false, e.g. distinguishing "not specified"
                // from an explicit "off") absence IS null, not false — a plain `bool` has no third
                // state to represent that with, so it must default to exactly false. `true` stays
                // forbidden for both: it would make the option one no user can ever turn off,
                // since ConsoleAppFramework binds a bool as a bare presence flag and accepts no
                // value syntax for it.
                if (p.ParameterType == typeof(bool?))
                {
                    (p.DefaultValue is null or false).Should().BeTrue(
                        because: $"{owner.Name}.{method.Name}'s --{p.Name} defaults to true, which " +
                                 "makes it an option no user can ever turn off");
                }
                else
                {
                    p.DefaultValue.Should().Be(false,
                        because: $"{owner.Name}.{method.Name}'s --{p.Name} defaults to true, which makes " +
                                 "it an option no user can ever turn off — ConsoleAppFramework binds a " +
                                 "bool as a bare presence flag and accepts no value syntax for it");
                }
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
