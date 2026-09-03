using System.Reflection;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core.Migration;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// Task 4 — retire the active <c>scri setup --multi-user</c>, merge-driver install, Git-process,
/// and merge-config-surfaces. Nothing here launches a process: the retired setup behavior is
/// replaced by a print-only <c>PrintMergeRemovedHint</c>, and the only surviving merge artifacts
/// are the two Phase 2 recognition files. Each assertion pins an absence; the recognition files
/// are the sole, explicitly allowlisted merge survivors.
/// </summary>
public sealed class SetupMergeRetirementTests
{
    private const string SetupRelativePath = "src/Scrinia/Commands/ScriniaCommands.Setup.cs";

    private static string SetupSource() =>
        File.ReadAllText(RepoPaths.Combine(SetupRelativePath.Split('/')));

    private static Type SetupType => typeof(ScriniaCommands);

    // ---- the retirement hint prints exact guidance, never executes it ----

    [Fact]
    public void PrintMergeRemovedHint_EmitsTheExactConflictAndDriverUnsetCommands()
    {
        string root = @"C:\repos\team-memory";
        string hint = ScriniaCommands.PrintMergeRemovedHint(root);

        // §8.3 legacy driver-unset argv, with the workspace root substituted for the placeholder.
        hint.Should().Contain(
            $"git -C \"{root}\" config --local --unset-all merge.scrinia-meta.driver");
        hint.Should().Contain(
            $"git -C \"{root}\" config --local --unset-all merge.scrinia-nmp2.driver");

        // §7.2 manual conflict cleanup guidance, in its exact comment form.
        hint.Should().Contain(
            "# Resolve the listed files manually, then stage and commit them with Git.");
        hint.Should().Contain(
            "# When no shared mutation is running, call shared_refresh for this base.");
    }

    [Fact]
    public void PrintMergeRemovedHint_IsPureTextAndResolvesNoDriverExecutable()
    {
        // The hint must never resolve or embed a merge-driver executable path (scri-merge was
        // retired). It is guidance text only, so no driver binary name survives it.
        ScriniaCommands.PrintMergeRemovedHint(@"C:\repos\team-memory")
            .Should().NotContain("scri-merge");
    }

    // ---- retired setup behavior is gone from the setup command ----

    [Fact]
    public void Setup_Source_RetiresMultiUserDriverConfigAndGitProcess()
    {
        string source = SetupSource();

        string[] retired =
        [
            "ConfigureMultiUser", "SerializeMergeConfig", "ResolveMergeDriverPath",
            "RunGit", "Process.Start", "ProcessStartInfo", "Environment.ProcessPath",
            "multiUser", "scri-merge", ".gitattributes", "merge.config", "%O %A %B %P",
        ];
        foreach (string token in retired)
        {
            source.Should().NotContain(token,
                $"ScriniaCommands.Setup.cs must not contain '{token}' — that surface is retired.");
        }
    }

    [Fact]
    public void Setup_Reflection_NoRetiredMergeOrGitMembers()
    {
        var type = SetupType;

        string[] retiredMembers =
        [
            "ConfigureMultiUser", "SerializeMergeConfig",
            "ResolveMergeDriverPath", "RunGit",
        ];
        foreach (string member in retiredMembers)
        {
            type.GetMethod(member,
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Should().BeNull($"{member} must not exist on ScriniaCommands.");
        }

        // The --multi-user switch is gone; Setup keeps its remaining parameters.
        type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Setup")
            .SelectMany(m => m.GetParameters())
            .Should().NotContain(p => p.Name == "multiUser");

        // The retirement hint is the replacement and must be reachable from setup tests.
        SetupType.GetMethod("PrintMergeRemovedHint",
            BindingFlags.NonPublic | BindingFlags.Static)
            .Should().NotBeNull("PrintMergeRemovedHint must exist as a setup helper.");
    }

    // ---- Scrinia never launches Git anywhere in src/ or tests/ ----

    [Fact]
    public void Repository_NeverLaunchesGit()
    {
        string[] gitLaunchPatterns =
            ["Process.Start(\"git\")", "ProcessStartInfo(\"git\")", "RunGit("];

        // The asserting test file necessarily embeds these pattern literals, so it is excluded
        // from its own scan.
        const string assertingFile = "SetupMergeRetirementTests.cs";

        foreach (string csFile in RepositorySourceFiles())
        {
            if (string.Equals(Path.GetFileName(csFile), assertingFile, StringComparison.Ordinal))
                continue;

            string source = File.ReadAllText(csFile);
            foreach (string pattern in gitLaunchPatterns)
            {
                source.Should().NotContain(pattern,
                    $"{PathRelative(csFile)} must not launch git ({pattern}).");
            }
        }
    }

    // ---- no leftover merge hooks ----

    [Fact]
    public void Hooks_DontInstallMergeDrivers()
    {
        string hooksDir = RepoPaths.Combine("src", "Scrinia", "Commands", "Hooks");
        if (!Directory.Exists(hooksDir)) return;

        foreach (string hookFile in Directory.EnumerateFiles(hooksDir, "*.cs"))
        {
            string source = File.ReadAllText(hookFile);
            source.Should().NotContain(".gitattributes", $"{PathRelative(hookFile)} installs no merge driver.");
            source.Should().NotContain("merge.config", $"{PathRelative(hookFile)} writes no merge config.");
            source.Should().NotContain("merge.scrinia", $"{PathRelative(hookFile)} references no merge driver.");
        }
    }

    // ---- the two named recognition files are the sole allowlisted survivors ----

    [Fact]
    public void RecognitionFiles_RetainOnlyTheNamedContract()
    {
        string core = File.ReadAllText(
            RepoPaths.Combine("src", "Scrinia.Core", "Migration", "LegacyMergeRecognition.cs"));
        string test = File.ReadAllText(
            RepoPaths.Combine("tests", "Scrinia.Tests", "LegacyMergeRecognitionTests.cs"));

        core.Should().Contain(LegacyMergeRecognition.ScriniaMetaAttributeLine);
        core.Should().Contain(LegacyMergeRecognition.ScriniaNmp2AttributeLine);
        core.Should().Contain("merge.scrinia-meta.driver");
        core.Should().NotContain("Process.Start");

        test.Should().Contain("LegacyMergeRecognition");
    }

    private static IEnumerable<string> RepositorySourceFiles()
    {
        string repoRoot = RepoPaths.Root();
        foreach (string root in new[] { "src", "tests" })
        {
            string dir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static string PathRelative(string full) =>
        full.Replace(RepoPaths.Root() + Path.DirectorySeparatorChar, string.Empty);
}
