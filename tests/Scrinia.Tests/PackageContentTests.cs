using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Scrinia.Core.Process;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// Phase 5 Task 4 — the immutable <c>scri</c> NuGet global tool package.
///
/// <para>These tests pack <c>src/Scrinia/Scrinia.csproj</c> in Release, then assert the exact
/// allowlist/denylist contract from the global-tool architecture: the package is a
/// <c>DotnetTool</c> whose single command is <c>scri</c>, it carries the managed command and its
/// managed dependencies, the offline-required embedded prompts/skills/setup templates, and the
/// package readme; and it contains no <c>Scrinia.Merge</c>/<c>scri-merge</c>, no Vulkan or
/// <c>LLamaSharp</c>, no native RID asset, no downloaded model, no repository or workspace data,
/// and no mutable cache.</para>
///
/// <para>The pack runs once per process (a real <c>dotnet pack</c>), so the collection is
/// non-parallel and the snapshot is shared; the mutable <c>SCRINIA_HOME</c> and workspace state are
/// never touched by these tests.</para>
/// </summary>
[Collection(PackageContentCollection.Name)]
public sealed class PackageContentTests
{
    private static readonly Lazy<PackageSnapshot> Pack = new(
        PackageSnapshot.Build, LazyThreadSafetyMode.ExecutionAndPublication);

    private static PackageSnapshot Snapshot => Pack.Value;

    private static IReadOnlyList<string> Entries => Snapshot.Entries;

    private static string PackageId => Snapshot.PackageId;

    private static string PackageType => Snapshot.PackageType;

    private static IReadOnlyList<string> McpEmbeddedResources => Snapshot.McpEmbeddedResources;

    [Fact]
    public void Package_DeclaresScriToolId_AndDotnetToolType()
    {
        // The package id is the stable product name, not the assembly/command name, and the package
        // type must be the immutable global-tool type — the pre-fix library package declared neither.
        PackageId.Should().Be("scrinia");
        PackageType.Should().Be("DotnetTool");
    }

    [Fact]
    public void Package_PutsScriCommandUnderTools_NotLib()
    {
        // A global tool ships its command under tools/<tfm>/; a library package ships under lib/.
        // The managed command assembly is `scri.dll` (no native .exe for a framework-dependent tool).
        Entries.Should().Contain(p =>
            p.StartsWith("tools/", StringComparison.Ordinal) &&
            p.EndsWith("scri.dll", StringComparison.OrdinalIgnoreCase),
            because: "the one packaged scri command must live under tools/ for a global tool");
    }

    [Fact]
    public void Package_ContainsManagedDependencies()
    {
        // ConsoleAppFramework v5 is a source generator with no runtime assembly, so it never ships
        // as its own DLL; the managed dependencies that do ship are the ones asserted below.
        string[] expectedManagedDependencies =
        [
            "Scrinia.Core.dll",
            "Scrinia.Mcp.dll",
            "Spectre.Console.dll",
            "Microsoft.Extensions.Hosting.dll",
            "ModelContextProtocol.dll",
        ];

        foreach (string dependency in expectedManagedDependencies)
        {
            Entries.Should().Contain(
                p => p.Split('/').Last().Equals(dependency, StringComparison.OrdinalIgnoreCase),
                because: $"{dependency} is a managed dependency of the scri command and must ship");
        }
    }

    [Fact]
    public void Package_IncludesEmbeddedPromptsSkillsSchemasAndSetupTemplates()
    {
        McpEmbeddedResources.Should().NotBeEmpty(
            because: "the offline prompts/skills/setup templates are embedded in Scrinia.Mcp");

        McpEmbeddedResources.Should().Contain("Scrinia.Mcp.prompts.guide.md",
            because: "the guide prompt must be embedded for offline guide()");
        McpEmbeddedResources.Should().Contain("Scrinia.Mcp.skills.qa.md",
            because: "the built-in skills must be embedded");
        McpEmbeddedResources.Should().Contain("Scrinia.Mcp.prompts.scaffolds.researcher.md",
            because: "the setup templates (scaffolds) must be embedded");
        McpEmbeddedResources.Should().Contain("Scrinia.Mcp.plugins.opencode.scrinia.ts",
            because: "the opencode plugin must be embedded");
    }

    [Fact]
    public void Package_IncludesPackageReadme_AtRoot()
    {
        Entries.Should().Contain("package-readme.md",
            because: "the package readme is a required part of the immutable tool package");
    }

    [Fact]
    public void Package_ExcludesMergeAndScriMerge()
    {
        Entries.Should().NotContain(p =>
            p.Contains("Scrinia.Merge", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("scri-merge", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith("Merge.dll", StringComparison.OrdinalIgnoreCase),
            because: "Merge is retired and must never ship in the global tool package");
    }

    [Fact]
    public void Package_ExcludesVulkanAndLLamaSharp()
    {
        Entries.Should().NotContain(p =>
            p.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("LLamaSharp", StringComparison.OrdinalIgnoreCase),
            because: "Vulkan is a separately installed plugin, not part of the immutable tool");
    }

    [Fact]
    public void Package_ExcludesNativeRidAssets()
    {
        Entries.Should().NotContain(p =>
            p.EndsWith(".so", StringComparison.Ordinal) ||
            p.EndsWith(".dylib", StringComparison.Ordinal),
            because: "native RID assets belong under SCRINIA_HOME, not the immutable tool package");
    }

    [Fact]
    public void Package_ExcludesDownloadedModels()
    {
        Entries.Should().NotContain(p =>
            p.Contains(".gguf", StringComparison.OrdinalIgnoreCase) ||
            p.Contains(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("models/", StringComparison.Ordinal),
            because: "models live under SCRINIA_HOME/models, never in the immutable tool package");
    }

    [Fact]
    public void Package_ExcludesRepositoryAndWorkspaceData()
    {
        Entries.Should().NotContain(p =>
            p.Contains(Path.DirectorySeparatorChar.ToString() + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            p.EndsWith(".git", StringComparison.Ordinal) ||
            p.Contains(".scrinia", StringComparison.Ordinal),
            because: "repository and per-workspace .scrinia data never ship in the tool package");
    }

    [Fact]
    public void Package_ExcludesMutableCaches()
    {
        Entries.Should().NotContain(p =>
            p.Contains("/embeddings/", StringComparison.Ordinal) ||
            p.Contains("/indexes/", StringComparison.Ordinal) ||
            p.Contains("/exports/", StringComparison.Ordinal) ||
            p.Contains("/versions/", StringComparison.Ordinal) ||
            p.EndsWith(".tmp", StringComparison.Ordinal) ||
            p.EndsWith(".lock", StringComparison.Ordinal),
            because: "mutable caches are derived/rebuildable and must not ship in the immutable tool");
    }

    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class PackageContentCollection
    {
        public const string Name = "PackageContent";
    }
}

/// <summary>
/// A single packed <c>scri</c> global-tool package: its nuspec metadata, the ordered list of entry
/// paths, and the embedded-resource names of the packaged <c>Scrinia.Mcp</c> assembly.
/// </summary>
internal sealed class PackageSnapshot
{
    public required string PackageId { get; init; }

    public required string PackageType { get; init; }

    public required string NuspecText { get; init; }

    public required IReadOnlyList<string> Entries { get; init; }

    public required IReadOnlyList<string> McpEmbeddedResources { get; init; }

    public static PackageSnapshot Build()
    {
        string? dotnet = new ProcessRunner().ResolveExecutable("dotnet");
        dotnet.Should().NotBeNullOrEmpty("the dotnet SDK is required to pack the global tool package");

        string csproj = RepoPaths.Combine("src", "Scrinia", "Scrinia.csproj");
        string packOut = Path.Combine(Path.GetTempPath(), $"scrinia-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packOut);

        ProcessResult result = new ProcessRunner().RunAsync(
            dotnet!,
            ["pack", csproj, "-c", "Release", "-o", packOut],
            null,
            CancellationToken.None).GetAwaiter().GetResult();

        result.ExitCode.Should().Be(0, because:
            $".dotnet pack exited {result.ExitCode}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");

        string[] nupkgs = Directory.GetFiles(packOut, "*.nupkg");
        nupkgs.Should().HaveCount(1, because: "dotnet pack must produce exactly one .nupkg");
        string nupkg = nupkgs[0];

        string extractDir = Path.Combine(Path.GetTempPath(), $"scrinia-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        string mcpDllPath = Path.Combine(extractDir, "Scrinia.Mcp.dll");

        List<string> entries;
        string nuspecText;
        List<string> mcpResources;

        using (var zip = ZipFile.OpenRead(nupkg))
        {
            entries = zip.Entries
                .Select(e => e.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            ZipArchiveEntry nuspecEntry = zip.Entries
                .First(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            nuspecText = new StreamReader(nuspecEntry.Open(), Encoding.UTF8).ReadToEnd();

            ZipArchiveEntry? mcpEntry = zip.Entries.FirstOrDefault(e =>
                e.Name == "Scrinia.Mcp.dll" && e.FullName.StartsWith("tools/", StringComparison.Ordinal));
            if (mcpEntry is not null)
            {
                mcpEntry.ExtractToFile(mcpDllPath, overwrite: true);
            }
        }

        if (File.Exists(mcpDllPath))
        {
            Assembly assembly = Assembly.LoadFrom(mcpDllPath);
            mcpResources = assembly
                .GetManifestResourceNames()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        else
        {
            mcpResources = new List<string>();
        }

        (string id, string type) = ParseNuspec(nuspecText);

        return new PackageSnapshot
        {
            PackageId = id,
            PackageType = type,
            NuspecText = nuspecText,
            Entries = entries,
            McpEmbeddedResources = mcpResources,
        };
    }

    private static (string Id, string Type) ParseNuspec(string nuspec)
    {
        // The <id> is an element; the <packageType> is a self-closing element carrying the type in
        // its `name` attribute, so both shapes are parsed here.
        string id = Regex.Match(nuspec, @"<id\b[^>]*>(?<v>.+?)</id>", RegexOptions.Singleline)
            .Groups["v"].Value.Trim();
        string type = Regex.Match(nuspec, @"<packageType\b[^>]*name=""(?<v>[^""]+)""", RegexOptions.Singleline)
            .Groups["v"].Value.Trim();
        return (id, type);
    }
}
