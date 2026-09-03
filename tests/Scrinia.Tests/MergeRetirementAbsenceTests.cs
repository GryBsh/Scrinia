using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Phase 1 merge-retirement gate for Task 2: the merge driver project, its test project, and the
/// active <c>Scrinia.Core.Merge</c> sidecar rules are gone — every solution, project-reference, and
/// namespace import that pointed at them is removed — while the Phase 2 migration-only recognition
/// contract (<c>LegacyMergeRecognition</c> + its test) is left exactly in place.
///
/// <para>Every filesystem path is built with <see cref="RepoPaths.Combine"/>, which joins the
/// segments with the platform-native separator. A backslash literal (e.g. <c>"src\\Scrinia.Merge"</c>)
/// would instead be a single literal directory name on Unix, so a deleted-directory assertion would
/// pass trivially without ever inspecting the real path. Coverage stays strictly scoped to Task 2:
/// it asserts the Task 2-owned deleted directories, the solution build list, the test project
/// reference, and the retained recognition contract. It does not later claim ownership of the CLI
/// <c>reconcile</c> command, docs, or the release workflow — those are asserted by their own
/// plan-owned tasks.</para>
/// </summary>
public sealed class MergeRetirementAbsenceTests
{
    private static readonly string[] DeletedMergeDirectories =
    {
        RepoPaths.Combine("src", "Scrinia.Merge"),
        RepoPaths.Combine("tests", "Scrinia.Merge.Tests"),
        RepoPaths.Combine("src", "Scrinia.Core", "Merge"),
    };

    private static readonly string[] RetainedMigrationFiles =
    {
        RepoPaths.Combine("src", "Scrinia.Core", "Migration", "LegacyMergeRecognition.cs"),
        RepoPaths.Combine("tests", "Scrinia.Tests", "LegacyMergeRecognitionTests.cs"),
    };

    /// <summary>Every active merge directory Task 2 must have deleted.</summary>
    [Fact]
    public void MergeDirectories_AreDeleted()
    {
        foreach (string dir in DeletedMergeDirectories)
        {
            Directory.Exists(dir).Should().BeFalse($"{dir} must be deleted — only the Migration/ "
                + "recognition contract survives Task 2.");
        }
    }

    /// <summary>Neither merge project may be listed in the authoritative solution build list.</summary>
    [Fact]
    public void Solution_ListsNoMergeProject()
    {
        string sln = File.ReadAllText(RepoPaths.Combine("Scrinia.sln"));

        sln.Should().NotContain("Scrinia.Merge.csproj");
        sln.Should().NotContain("Scrinia.Merge.Tests.csproj");
    }

    /// <summary>The test project must no longer reference the deleted driver assembly.</summary>
    [Fact]
    public void TestProject_HasNoMergeReference()
    {
        string csproj = File.ReadAllText(
            RepoPaths.Combine("tests", "Scrinia.Tests", "Scrinia.Tests.csproj"));

        Regex projectReference = new(
            @"<ProjectReference[^>]*Include=""[^""]*Scrinia\.Merge",
            RegexOptions.IgnoreCase);

        projectReference.IsMatch(csproj).Should().BeFalse(
            "Scrinia.Tests.csproj must not ProjectReference the deleted Scrinia.Merge assembly.");
    }

    /// <summary>
    /// No remaining source or test file may import the deleted namespaces, except the two retained
    /// migration files (the only merge-named artifacts Phase 2 keeps).
    /// </summary>
    [Fact]
    public void NoRemainingFile_ImportsDeletedMergeNamespace()
    {
        var imported = new List<string>();

        foreach (string file in AllSourceFiles())
        {
            if (IsRetainedMigrationFile(file)) continue;

            string contents = File.ReadAllText(file);
            if (
                Regex.IsMatch(contents, @"\busing\s+Scrinia\.Merge\s*;") ||
                Regex.IsMatch(contents, @"\busing\s+Scrinia\.Core\.Merge\s*;"))
            {
                imported.Add(file);
            }
        }

        imported.Should().BeEmpty(
            "These files still import a deleted merge namespace after Task 2 removed the merge driver, "
                + "its tests, and the Scrinia.Core/Merge sidecar rules: {0}", imported);
    }

    /// <summary>The Phase 2 recognition contract is untouched and still present.</summary>
    [Fact]
    public void RetainedMigrationFiles_StillPresent()
    {
        foreach (string file in RetainedMigrationFiles)
        {
            File.Exists(file).Should().BeTrue($"{file} must survive Task 2 (Phase 2 migration contract).");
        }
    }

    private static IEnumerable<string> AllSourceFiles()
    {
        string root = RepoPaths.Root();
        foreach (string dir in new[] { Path.Combine(root, "src"), Path.Combine(root, "tests") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // The new absence test references neither namespace; keep the corpus to pre-existing files.
                if (file.EndsWith("MergeRetirementAbsenceTests.cs", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return file;
            }
        }
    }

    private static bool IsRetainedMigrationFile(string file) =>
        RetainedMigrationFiles.Contains(file);
}
