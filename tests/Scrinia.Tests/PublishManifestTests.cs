using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// The publish scripts had two hand-maintained project lists and they had diverged: publish.ps1
/// published three projects, publish.sh two. The missing one was Scrinia.Merge — the .nmp2 merge
/// driver — so a Linux or macOS build produced a tree whose configured merge driver pointed at a
/// binary that did not exist, and .nmp2 merges fell back to git's line-based text merge over
/// Base64 blobs.
///
/// <para>These tests hold the fix's shape rather than its instance: the project list lives in
/// publish.projects.tsv, neither script may name a project itself, and a NEW executable project
/// fails <see cref="EveryShippableExecutable_IsInTheManifestOrNamedAsExcluded"/> until someone
/// decides which it is.</para>
/// </summary>
public sealed class PublishManifestTests
{
    private static readonly string ManifestPath = RepoPaths.Combine("publish.projects.tsv");

    /// <summary>
    /// Executable-producing projects under src/ that deliberately do NOT ship through the publish
    /// scripts. Each entry states why; an unexplained absence is what this test exists to prevent.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedFromPublish = new()
    {
        ["Scrinia.AppHost"] =
            "Aspire development orchestrator — a local run-the-stack harness, never shipped to users.",
        ["Scrinia.Server"] =
            "Shipped by release.yml's build-server job and as a container image " +
            "(src/Scrinia.Server/Dockerfile, docker-compose.yml) — never by the developer publish " +
            "scripts, which build the CLI and the plugin it loads.",
    };

    /// <summary>
    /// Every `dotnet publish &lt;project&gt;` line in the release workflow. This is the list that
    /// actually produces what users download, and it is hand-maintained: rule 5 below is what
    /// stops it drifting from the manifest.
    /// </summary>
    private static List<string> ReleaseWorkflowProjects()
    {
        string yaml = File.ReadAllText(RepoPaths.Combine(".github", "workflows", "release.yml"));
        return Regex.Matches(yaml, @"dotnet publish\s+(\S+\.csproj)")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record ManifestRow(
        string Id, string Project, string Condition, string Profile, string Output, string Rids);

    private static List<ManifestRow> ReadManifest()
    {
        var rows = new List<ManifestRow>();
        foreach (string line in File.ReadAllLines(ManifestPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] parts = line.Split('\t');
            parts.Should().HaveCount(6, because: $"every manifest row has six tab-separated columns: '{line}'");
            rows.Add(new ManifestRow(
                parts[0], parts[1], parts[2], parts[3], parts[4], parts[5].TrimEnd('\r')));
        }
        return rows;
    }

    [Fact]
    public void EveryManifestProject_ExistsOnDisk()
    {
        var rows = ReadManifest();
        rows.Should().NotBeEmpty();

        using var scope = new AssertionScope();
        foreach (var row in rows)
        {
            File.Exists(RepoPaths.Combine(row.Project.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue(because: $"the manifest names {row.Project} for '{row.Id}'");
        }
    }

    [Fact]
    public void NeitherScript_NamesAProjectItself()
    {
        using var scope = new AssertionScope();
        foreach (string script in new[] { "publish.sh", "publish.ps1" })
        {
            string text = File.ReadAllText(RepoPaths.Combine(script));
            text.Should().NotContain(".csproj",
                because: $"{script} must take its project list from publish.projects.tsv — a literal " +
                         "project path here is how the two lists diverged in the first place");
        }
    }

    [Fact]
    public void EveryShippableExecutable_IsInTheManifestOrNamedAsExcluded()
    {
        var manifestProjects = ReadManifest()
            .Select(r => Path.GetFileNameWithoutExtension(r.Project))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var executables = Directory
            .EnumerateFiles(RepoPaths.Combine("src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p =>
            {
                string text = File.ReadAllText(p);
                return text.Contains("<OutputType>Exe</OutputType>", StringComparison.Ordinal)
                    || text.Contains("Sdk=\"Microsoft.NET.Sdk.Web\"", StringComparison.Ordinal);
            })
            .Select(Path.GetFileNameWithoutExtension)
            .Select(n => n!)
            .ToList();

        executables.Should().NotBeEmpty(because: "the enumeration itself must not silently match nothing");

        using var scope = new AssertionScope();
        foreach (string name in executables)
        {
            (manifestProjects.Contains(name) || ExcludedFromPublish.ContainsKey(name))
                .Should().BeTrue(
                    because: $"{name} produces an executable but is neither in publish.projects.tsv nor " +
                             "named in ExcludedFromPublish — decide which it is and record the reason");
        }

        foreach (string excluded in ExcludedFromPublish.Keys)
        {
            executables.Should().Contain(excluded,
                because: $"{excluded} is named as excluded but no longer produces an executable — " +
                         "a stale exclusion hides the next real one");
        }
    }

    [Fact]
    public void VulkanBackendVersion_MatchesThePackageReference()
    {
        string csproj = File.ReadAllText(RepoPaths.Combine(
            "src", "Scrinia.Plugin.Embeddings", "Scrinia.Plugin.Embeddings.csproj"));
        var declared = Regex.Match(csproj,
            """<PackageReference\s+Include="LLamaSharp\.Backend\.Vulkan"\s+Version="([^"]+)"\s*/?>""");
        declared.Success.Should().BeTrue(because: "the plugin must still reference the Vulkan backend");
        string version = declared.Groups[1].Value;

        using var scope = new AssertionScope();
        foreach (string script in new[] { "publish.sh", "publish.ps1" })
        {
            File.ReadAllText(RepoPaths.Combine(script))
                .Should().Contain(version,
                    because: $"{script} builds the Vulkan native path from a hardcoded version; when it " +
                             "drifts from the PackageReference the path does not exist and the overwrite " +
                             "is skipped, shipping the CPU backend under a Vulkan switch");
        }
    }

    /// <summary>
    /// The manifest governs the two developer scripts; .github/workflows/release.yml is a third,
    /// hand-maintained list — and the one that produces the archives users download. Without this
    /// rule a new project could be added to the manifest, pass every check above, and never ship.
    /// </summary>
    [Fact]
    public void TheReleaseWorkflow_ShipsEveryManifestProject_AndNothingUndeclared()
    {
        var manifestProjects = ReadManifest()
            .Select(r => Path.GetFileNameWithoutExtension(r.Project))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var released = ReleaseWorkflowProjects();

        released.Should().NotBeEmpty(because: "the regex must not silently match nothing");

        using var scope = new AssertionScope();
        foreach (string name in manifestProjects)
        {
            released.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue(
                    because: $"{name} is in publish.projects.tsv but release.yml never publishes it, so " +
                             "it would ship to nobody");
        }

        foreach (string name in released)
        {
            (manifestProjects.Contains(name) || ExcludedFromPublish.ContainsKey(name))
                .Should().BeTrue(
                    because: $"release.yml publishes {name}, but it is neither in publish.projects.tsv " +
                             "nor named in ExcludedFromPublish — a developer building locally would not " +
                             "get what the release ships");
        }
    }
}
