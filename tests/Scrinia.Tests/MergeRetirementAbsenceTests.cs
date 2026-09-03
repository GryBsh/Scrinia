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
/// <para>The two retained files below are the ONLY merge-named sources still permitted to exist;
/// every path/reference assertion here excludes them by design.</para>
/// </summary>
public sealed class MergeRetirementAbsenceTests
{
    private const string ScriniaMergeDir = "src\\Scrinia.Merge";
    private const string ScriniaMergeTestsDir = "tests\\Scrinia.Merge.Tests";
    private const string CoreMergeDir = "src\\Scrinia.Core\\Merge";

    private static readonly string[] RetainedMigrationFiles =
    {
        RepoPaths.Combine("src", "Scrinia.Core", "Migration", "LegacyMergeRecognition.cs"),
        RepoPaths.Combine("tests", "Scrinia.Tests", "LegacyMergeRecognitionTests.cs"),
    };

    /// <summary>Every active merge surface Task 2 must have deleted.</summary>
    [Fact]
    public void MergeDirectories_AreDeleted()
    {
        Directory.Exists(RepoPaths.Combine(ScriniaMergeDir)).Should().BeFalse(
            $"{ScriniaMergeDir} must be deleted — only the Migration/ recognition contract survives.");
        Directory.Exists(RepoPaths.Combine(ScriniaMergeTestsDir)).Should().BeFalse(
            $"{ScriniaMergeTestsDir} must be deleted — only the Migration/ recognition test survives.");
        Directory.Exists(RepoPaths.Combine(CoreMergeDir)).Should().BeFalse(
            $"{CoreMergeDir} must be deleted — no active merge implementation remains.");
    }

    /// <summary>Neither merge project may be listed in the authoritative solution build list.</summary>
    [Fact]
    public void Solution_ListsNoMergeProject()
    {
        string sln = File.ReadAllText(RepoPaths.Combine("Scrinia.sln"));

        sln.Should().NotContain("src\\Scrinia.Merge\\Scrinia.Merge.csproj");
        sln.Should().NotContain("tests\\Scrinia.Merge.Tests\\Scrinia.Merge.Tests.csproj");
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
