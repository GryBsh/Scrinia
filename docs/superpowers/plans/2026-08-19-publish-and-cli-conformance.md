# Publish and CLI Conformance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the two publish scripts one source of truth for what ships, give the CLI option family a conformance check, repair a dead environment-variable convention, state what the read-only store gate actually enforces, and write the two guardrails carried from branch 4.

**Architecture:** Each finding is paired with the artifact that fails when a further instance diverges: a manifest plus a coverage test for the publish scripts, a reflective conformance test for the CLI entrypoints, and a test apiece for the two carried guardrails. Comment-only changes are grouped into one task so no behaviour change hides among them.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, `System.Text.Json` source-generated contexts, ConsoleAppFramework, bash, PowerShell 7.

**Spec:** `docs/superpowers/specs/2026-08-19-publish-and-cli-conformance-design.md`

## Global Constraints

- **Breaking changes are permitted and recorded.** The product is at `0.5.0`, pre-1.0. No compatibility shims, no dual-accept paths, no deprecated aliases. Every break gets a CHANGELOG entry.
- **Every exclusion is named.** Where a test enumerates from an authoritative list and something is deliberately outside it, the exclusion set is a named constant with a written reason per entry. Never an unexplained absence.
- **No new copy of an existing helper.** If a helper already exists in a referenced project, use it; if it exists three times, consolidate rather than adding a fourth.
- **Build gate:** `dotnet build Scrinia.sln` must report 0 errors. `rtk err` is not a build gate — it has reported a failed build as clean. Use `rtk dotnet build`, whose summary is accurate.
- **Windows/Synology hazards:** SynologyDrive can resurrect git-deleted `.cs` files as untracked ghosts with old mtimes, producing CS0101 duplicates. Check `git status` for untracked twins before blaming a compiler or LSP error. The LSP also emits phantom CS1061/CS0103/CS8019 diagnostics against cached files while `dotnet build` is green — trust the build, not the diagnostic stream.
- **Tests must be seen to fail.** Every task that adds a test includes a step that runs it against the unfixed state, or mutates the code under test and confirms red before reverting the mutation.

---

### Task 1: The publish manifest and both scripts

**Files:**
- Create: `publish.projects.tsv`
- Modify: `publish.sh` (whole file)
- Modify: `publish.ps1` (whole file)
- Modify: `.gitattributes`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `publish.projects.tsv` with the header `id`, `project`, `condition`, `profile`, `output`, tab-separated. Task 2's pin test reads it. The switch is `--with-embeddings` in bash and `-WithEmbeddings` in PowerShell.

- [ ] **Step 1: Create the manifest**

Create `publish.projects.tsv`. **The separator is a literal TAB character**, not spaces — the readers split on tab.

```
id	project	condition	profile	output
scri	src/Scrinia/Scrinia.csproj	always	trimmed	rid
scri-merge	src/Scrinia.Merge/Scrinia.Merge.csproj	always	plain	rid
scri-plugin-embeddings	src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj	with-embeddings	plain	plugin
```

Verify the file really contains tabs:

```bash
grep -cP '\t' publish.projects.tsv
```

Expected: `4` (header plus three rows).

- [ ] **Step 2: Pin the manifest's line endings**

Append to `.gitattributes`:

```
# Read by publish.sh with a tab-splitting `read`. A CRLF checkout would leave a carriage
# return on the last field of every row and silently corrupt the `output` column.
publish.projects.tsv text eol=lf
```

- [ ] **Step 3: Rewrite publish.sh**

Replace the whole file with:

```bash
#!/usr/bin/env bash
set -euo pipefail

WITH_EMBEDDINGS=false
OUTPUT_DIR=""
PLATFORM=""
ALL_RIDS=("win-x64" "linux-x64" "osx-arm64")
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MANIFEST="$SCRIPT_DIR/publish.projects.tsv"
# Kept in step with the LLamaSharp.Backend.Vulkan PackageReference in
# src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj by a test — a drift here makes
# the overwrite path below not exist, which used to be a silent skip.
VULKAN_BACKEND_VERSION="0.25.0"

# Parse arguments
for arg in "$@"; do
    case "$arg" in
        --with-embeddings) WITH_EMBEDDINGS=true ;;
        --platform=*) PLATFORM="${arg#--platform=}" ;;
        -*) echo "Unknown flag: $arg" >&2; exit 1 ;;
        *) OUTPUT_DIR="$arg" ;;
    esac
done

if [ -z "$OUTPUT_DIR" ]; then
    echo "Usage: ./publish.sh <output-folder> [--platform=win-x64|linux-x64|osx-arm64] [--with-embeddings]" >&2
    exit 1
fi

# Validate platform if specified
if [ -n "$PLATFORM" ]; then
    valid=false
    for r in "${ALL_RIDS[@]}"; do
        if [ "$r" = "$PLATFORM" ]; then valid=true; break; fi
    done
    if [ "$valid" = false ]; then
        echo "Error: invalid platform '$PLATFORM'. Must be one of: ${ALL_RIDS[*]}" >&2
        exit 1
    fi
    RIDS=("$PLATFORM")
    SINGLE_PLATFORM=true
else
    RIDS=("${ALL_RIDS[@]}")
    SINGLE_PLATFORM=false
fi

# The manifest is the one source of truth for what ships. publish.ps1 reads the same file.
# The two lists used to be maintained by hand and had already diverged: scri-merge — the
# .nmp2 merge driver — shipped only from PowerShell, so a Linux or macOS build produced a
# tree whose configured merge driver pointed at a binary that did not exist.
if [ ! -f "$MANIFEST" ]; then
    echo "Error: manifest not found at $MANIFEST" >&2
    exit 1
fi

IDS=(); PROJECTS=(); CONDITIONS=(); PROFILES=(); OUTPUTS=()
while IFS=$'\t' read -r m_id m_project m_condition m_profile m_output; do
    # Strip a trailing CR: .gitattributes pins this file to LF, but a user whose git
    # configuration ignores it would otherwise get a corrupted final column.
    m_output="${m_output%$'\r'}"
    if [ -z "$m_id" ]; then continue; fi
    case "$m_id" in \#*) continue ;; esac
    IDS+=("$m_id"); PROJECTS+=("$m_project"); CONDITIONS+=("$m_condition")
    PROFILES+=("$m_profile"); OUTPUTS+=("$m_output")
done < <(tail -n +2 "$MANIFEST")

if [ "${#IDS[@]}" -eq 0 ]; then
    echo "Error: manifest $MANIFEST lists no projects" >&2
    exit 1
fi

# Overwrites the published native DLLs with the Vulkan-compiled variants. Single-file publish
# flattens all native DLLs to the root, and the CPU backend's (ggml-base, ggml, llama, mtmd)
# overwrite the Vulkan ones since they share filenames — so LLamaSharp would load CPU.
overwrite_vulkan_natives() {
    local rid="$1" dest="$2" pkg src
    case "$rid" in
        win-*)   pkg="llamasharp.backend.vulkan.windows" ;;
        linux-*) pkg="llamasharp.backend.vulkan.linux" ;;
        *)
            echo "  [warn] no Vulkan backend package exists for $rid; shipping the CPU backend." >&2
            return 0 ;;
    esac
    src="$HOME/.nuget/packages/$pkg/$VULKAN_BACKEND_VERSION/runtimes/$rid/native/vulkan"
    if [ -d "$src" ]; then
        cp -f "$src"/* "$dest"/
        echo "    Overwrote root native DLLs with Vulkan variants"
    else
        echo "  [warn] Vulkan natives not found at $src; shipping the CPU backend." >&2
    fi
}

mkdir -p "$OUTPUT_DIR"

for rid in "${RIDS[@]}"; do
    if [ "$SINGLE_PLATFORM" = true ]; then
        rid_dir="$OUTPUT_DIR"
    else
        rid_dir="$OUTPUT_DIR/$rid"
    fi

    for i in "${!IDS[@]}"; do
        if [ "${CONDITIONS[$i]}" = "with-embeddings" ] && [ "$WITH_EMBEDDINGS" != true ]; then
            continue
        fi

        case "${OUTPUTS[$i]}" in
            rid)    out_dir="$rid_dir" ;;
            plugin) out_dir="$rid_dir/plugins/${IDS[$i]}" ;;
            *)
                echo "Error: unknown output kind '${OUTPUTS[$i]}' for ${IDS[$i]}" >&2
                exit 1 ;;
        esac

        echo "Publishing ${IDS[$i]} for $rid ..."
        case "${PROFILES[$i]}" in
            trimmed)
                # Trim-analysis warnings are gated as errors so a regression silently dropping a
                # member via reflection cannot ship. If a trim warning fires, fix the root cause —
                # either move to a JsonSerializerContext, annotate with [DynamicallyAccessedMembers],
                # or rewrite the reflection-heavy call to a trim-safe shape.
                dotnet publish "${PROJECTS[$i]}" \
                    --runtime "$rid" \
                    --self-contained \
                    --configuration Release \
                    -p:PublishSingleFile=true \
                    -p:PublishTrimmed=true \
                    -p:SuppressTrimAnalysisWarnings=false \
                    -p:TreatWarningsAsErrors=true \
                    --output "$out_dir"
                ;;
            plain)
                # Single-file + self-contained + native bundling is configured in the .csproj.
                # Only --runtime is needed here for RID selection.
                dotnet publish "${PROJECTS[$i]}" \
                    --runtime "$rid" \
                    --configuration Release \
                    --output "$out_dir"
                ;;
            *)
                echo "Error: unknown profile '${PROFILES[$i]}' for ${IDS[$i]}" >&2
                exit 1 ;;
        esac

        if [ "${OUTPUTS[$i]}" = "plugin" ]; then
            overwrite_vulkan_natives "$rid" "$out_dir"
        fi
        echo "  -> $out_dir"
    done
done

echo ""
echo "Done. Builds:"
for rid in "${RIDS[@]}"; do
    if [ "$SINGLE_PLATFORM" = true ]; then
        rid_dir="$OUTPUT_DIR"
    else
        rid_dir="$OUTPUT_DIR/$rid"
    fi
    ls -lh "$rid_dir"/scri* 2>/dev/null || true
    if [ "$WITH_EMBEDDINGS" = true ]; then
        ls -lh "$rid_dir"/plugins/scri-plugin-embeddings/scri-plugin-embeddings* 2>/dev/null || true
    fi
done
```

Note what left: the `find ... -exec rm -rf {} +` and the unused `ext` variable are both gone. The plugin now publishes into its own subdirectory, so nothing strays beside it and there is nothing to clean up. `WorkspaceSetup.ResolveEmbeddingsPluginExe` tries that subdirectory layout **first**, so this is the better-supported of the two layouts, not merely a different one.

- [ ] **Step 4: Rewrite publish.ps1**

Replace the whole file with:

```powershell
param(
    [Parameter(Mandatory)]
    [string]$OutputDir,

    [switch]$WithEmbeddings,

    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string]$Platform
)

$ErrorActionPreference = 'Stop'

$Rids = if ($Platform) { @($Platform) } else { @('win-x64', 'linux-x64', 'osx-arm64') }
# When a single platform is specified, output directly into OutputDir (no RID subdirectory).
$SinglePlatform = [bool]$Platform

# Kept in step with the LLamaSharp.Backend.Vulkan PackageReference in
# src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj by a test — a drift here makes
# the overwrite path below not exist, which used to be a silent skip.
$VulkanBackendVersion = '0.25.0'

# The manifest is the one source of truth for what ships. publish.sh reads the same file.
$Manifest = Join-Path $PSScriptRoot 'publish.projects.tsv'
if (-not (Test-Path $Manifest)) { throw "Manifest not found at $Manifest" }
$Projects = @(Import-Csv -Path $Manifest -Delimiter "`t")
if ($Projects.Count -eq 0) { throw "Manifest $Manifest lists no projects" }

# Overwrites the published native DLLs with the Vulkan-compiled variants. Single-file publish
# flattens all native DLLs to the root, and the CPU backend's (ggml-base, ggml, llama, mtmd)
# overwrite the Vulkan ones since they share filenames — so LLamaSharp would load CPU.
function Set-VulkanNatives {
    param([string]$Rid, [string]$Dest)

    $pkg = if ($Rid -like 'win-*') { 'llamasharp.backend.vulkan.windows' }
           elseif ($Rid -like 'linux-*') { 'llamasharp.backend.vulkan.linux' }
           else { $null }

    if (-not $pkg) {
        Write-Warning "  no Vulkan backend package exists for $Rid; shipping the CPU backend."
        return
    }

    $src = "$env:USERPROFILE/.nuget/packages/$pkg/$VulkanBackendVersion/runtimes/$Rid/native/vulkan"
    if (Test-Path $src) {
        Copy-Item "$src/*" $Dest -Force
        Write-Host '    Overwrote root native DLLs with Vulkan variants'
    } else {
        Write-Warning "  Vulkan natives not found at $src; shipping the CPU backend."
    }
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

foreach ($rid in $Rids) {
    $ridDir = if ($SinglePlatform) { $OutputDir } else { "$OutputDir/$rid" }

    foreach ($p in $Projects) {
        if ($p.condition -eq 'with-embeddings' -and -not $WithEmbeddings) { continue }

        $outDir = switch ($p.output) {
            'rid'    { $ridDir }
            'plugin' { "$ridDir/plugins/$($p.id)" }
            default  { throw "Unknown output kind '$($p.output)' for $($p.id)" }
        }

        Write-Host "Publishing $($p.id) for $rid ..."
        switch ($p.profile) {
            'trimmed' {
                # Trim-analysis warnings are gated as errors so a regression silently dropping a
                # member via reflection cannot ship. If a trim warning fires, fix the root cause —
                # either move to a JsonSerializerContext, annotate with [DynamicallyAccessedMembers],
                # or rewrite the reflection-heavy call to a trim-safe shape.
                dotnet publish $p.project `
                    --runtime $rid `
                    --self-contained `
                    --configuration Release `
                    -p:PublishSingleFile=true `
                    -p:PublishTrimmed=true `
                    -p:SuppressTrimAnalysisWarnings=false `
                    -p:TreatWarningsAsErrors=true `
                    --output $outDir
            }
            'plain' {
                # Single-file + self-contained + native bundling is configured in the .csproj.
                # Only --runtime is needed here for RID selection.
                dotnet publish $p.project `
                    --runtime $rid `
                    --configuration Release `
                    --output $outDir
            }
            default { throw "Unknown profile '$($p.profile)' for $($p.id)" }
        }
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($p.id) / $rid" }

        if ($p.output -eq 'plugin') { Set-VulkanNatives -Rid $rid -Dest $outDir }
        Write-Host "  -> $outDir"
    }
}

Write-Host ''
Write-Host 'Done. Builds:'
foreach ($rid in $Rids) {
    $ridDir = if ($SinglePlatform) { $OutputDir } else { "$OutputDir/$rid" }
    Get-ChildItem "$ridDir/scri*" -ErrorAction SilentlyContinue |
        ForEach-Object { '{0}  {1}' -f $_.Length.ToString('N0').PadLeft(12), $_.FullName } |
        Write-Host
    if ($WithEmbeddings) {
        $ext = if ($rid -like 'win-*') { '.exe' } else { '' }
        Get-ChildItem "$ridDir/plugins/scri-plugin-embeddings/scri-plugin-embeddings$ext" -ErrorAction SilentlyContinue |
            ForEach-Object { '{0}  {1}' -f $_.Length.ToString('N0').PadLeft(12), $_.FullName } |
            Write-Host
    }
}
```

- [ ] **Step 5: Syntax-check both scripts without running a publish**

```bash
bash -n publish.sh
```
Expected: no output, exit 0.

```powershell
$null = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path ./publish.ps1), [ref]$null, [ref]$errs); $errs
```
Expected: no parse errors printed.

Then confirm the bash manifest reader produces the three expected ids, without invoking `dotnet`:

```bash
bash -c 'IDS=(); while IFS=$'"'"'\t'"'"' read -r a b c d e; do [ -z "$a" ] && continue; IDS+=("$a"); done < <(tail -n +2 publish.projects.tsv); echo "${IDS[@]}"'
```
Expected: `scri scri-merge scri-plugin-embeddings`

- [ ] **Step 6: Commit**

```bash
git add publish.projects.tsv publish.sh publish.ps1 .gitattributes
git commit -m "fix(publish): one manifest both scripts read, and delete the directory-eating find"
```

---

### Task 2: Consolidate the repo-root helper, then pin the manifest

**Files:**
- Create: `tests/Scrinia.TestDoubles/RepoPaths.cs`
- Modify: `tests/Scrinia.Server.Tests/TestPaths.cs`
- Modify: `tests/Scrinia.Tests/StoreBoundaryTests.cs:171-181`
- Create: `tests/Scrinia.Tests/PublishManifestTests.cs`

**Interfaces:**
- Consumes: `publish.projects.tsv` from Task 1.
- Produces: `Scrinia.TestDoubles.RepoPaths.Root()` and `RepoPaths.Combine(params string[])`. Later tasks needing a repo-relative path use these.

**Why the consolidation is in this task and not a separate one:** the walk-up-to-`Scrinia.sln` helper already exists three times — `tests/Scrinia.Merge.Tests/GitMergeHarness.cs`, `tests/Scrinia.Server.Tests/TestPaths.cs`, and a private copy in `tests/Scrinia.Tests/StoreBoundaryTests.cs` whose own comment acknowledges the duplication. This task needs the helper in `Scrinia.Tests`. Adding a fourth copy inside a branch whose subject is single sources of truth is not acceptable, so it moves to `Scrinia.TestDoubles`, which both `Scrinia.Tests` and `Scrinia.Server.Tests` already reference. `Scrinia.Merge.Tests` does not reference `Scrinia.TestDoubles` and keeps its copy — adding a project reference for a six-line helper costs more than it saves. Say so in the new file's doc comment.

- [ ] **Step 1: Create the shared helper**

`tests/Scrinia.TestDoubles/RepoPaths.cs`:

```csharp
namespace Scrinia.TestDoubles;

/// <summary>
/// Resolves paths relative to the repository root from a test assembly's output directory,
/// for tests that read source files on disk rather than reflecting over loaded assemblies.
///
/// <para>This lived in three places — <c>Scrinia.Server.Tests/TestPaths.cs</c>, a private copy
/// in <c>Scrinia.Tests/StoreBoundaryTests.cs</c>, and <c>Scrinia.Merge.Tests/GitMergeHarness.cs</c>.
/// The first two now delegate here. <c>Scrinia.Merge.Tests</c> keeps its copy deliberately: it
/// does not reference this project, and a ProjectReference for six lines costs more than the
/// duplication does.</para>
/// </summary>
public static class RepoPaths
{
    /// <summary>The repository root — the nearest ancestor of the test output directory
    /// containing <c>Scrinia.sln</c>.</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrinia.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not find Scrinia.sln walking up from {AppContext.BaseDirectory}.");
    }

    /// <summary>Combines <paramref name="relativeSegments"/> onto <see cref="Root"/>.</summary>
    public static string Combine(params string[] relativeSegments) =>
        Path.Combine([Root(), .. relativeSegments]);
}
```

- [ ] **Step 2: Point the two existing copies at it**

In `tests/Scrinia.Server.Tests/TestPaths.cs`, keep the type and its `RepoRoot(params string[])` signature so no call site changes, and make the body delegate:

```csharp
public static string RepoRoot(params string[] relativeSegments) =>
    Scrinia.TestDoubles.RepoPaths.Combine(relativeSegments);
```

Delete the private `FindRepoRoot`. Update the class doc comment's second sentence to say the walk lives in `Scrinia.TestDoubles.RepoPaths`.

In `tests/Scrinia.Tests/StoreBoundaryTests.cs`, delete the private `RepoRoot()` method (lines 171-181) and replace its call sites with `Scrinia.TestDoubles.RepoPaths.Root()`.

- [ ] **Step 3: Build and run the two affected suites**

```bash
rtk dotnet build Scrinia.sln
```
Expected: 0 errors.

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~StoreBoundaryTests" --no-build
```
Expected: PASS, same count as before the change.

- [ ] **Step 4: Write the manifest pin**

`tests/Scrinia.Tests/PublishManifestTests.cs`:

```csharp
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
            "Ships as a container image (src/Scrinia.Server/Dockerfile, docker-compose.yml), " +
            "not as a published binary in the CLI's output directory.",
    };

    private sealed record ManifestRow(string Id, string Project, string Condition, string Profile, string Output);

    private static List<ManifestRow> ReadManifest()
    {
        var rows = new List<ManifestRow>();
        foreach (string line in File.ReadAllLines(ManifestPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] parts = line.Split('\t');
            parts.Should().HaveCount(5, because: $"every manifest row has five tab-separated columns: '{line}'");
            rows.Add(new ManifestRow(parts[0], parts[1], parts[2], parts[3], parts[4].TrimEnd('\r')));
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
}
```

- [ ] **Step 5: Run the pin**

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~PublishManifestTests"
```
Expected: 4 passed.

- [ ] **Step 6: Prove each assertion can fail (mutation check)**

Do these one at a time, confirming red, then reverting before the next:

1. In `publish.sh`, add a comment line containing `src/Scrinia/Scrinia.csproj`. Run the filter. Expected: `NeitherScript_NamesAProjectItself` FAILS. Revert.
2. In `publish.projects.tsv`, change `scri-merge`'s project path to `src/Scrinia.Merge/Nope.csproj`. Expected: `EveryManifestProject_ExistsOnDisk` FAILS. Revert.
3. Delete the `scri-merge` row from the manifest. Expected: `EveryShippableExecutable_IsInTheManifestOrNamedAsExcluded` FAILS naming `Scrinia.Merge`. Revert.
4. In `publish.sh`, change `VULKAN_BACKEND_VERSION` to `0.24.0`. Expected: `VulkanBackendVersion_MatchesThePackageReference` FAILS. Revert.

Record the four observed failure messages in the task report. A pin that has never been seen red is not a pin.

- [ ] **Step 7: Commit**

```bash
git add tests/Scrinia.TestDoubles/RepoPaths.cs tests/Scrinia.Server.Tests/TestPaths.cs \
        tests/Scrinia.Tests/StoreBoundaryTests.cs tests/Scrinia.Tests/PublishManifestTests.cs
git commit -m "test(publish): pin the manifest as the only source of what ships"
```

---

### Task 3: CLI conformance test, and the `scri migrate` fixes it catches

**Files:**
- Create: `tests/Scrinia.Tests/CliSurfaceConformanceTests.cs`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Migration.cs:24-73`
- Modify: `src/Scrinia/Commands/CliJsonContext.cs`
- Modify: `docs/cli-reference.md:489-513`

**Interfaces:**
- Consumes: `RepoPaths` from Task 2 (not required by this task's tests, which reflect rather than read files — do not use it unless a step calls for it).
- Produces: `CliStoreMigrateOutput` in `CliJsonContext`. Task 4 adds two more records to the same file; expect that file to be touched twice.

- [ ] **Step 1: Write the conformance test (it must fail)**

`tests/Scrinia.Tests/CliSurfaceConformanceTests.cs`:

```csharp
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
    /// <summary>Parameters that are not really options: the framework binds these itself.</summary>
    private static readonly HashSet<string> FrameworkParameters = new(StringComparer.Ordinal)
    {
        "cancellationToken",
    };

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
        Entrypoints().Should().HaveCountGreaterThan(15,
            because: "the CLI has 23 command entrypoints; a filter matching almost none would make " +
                     "every rule below pass without checking anything");
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
                if (p.ParameterType != typeof(bool)) continue;
                if (FrameworkParameters.Contains(p.Name!)) continue;
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
        // The filter in Entrypoints() already selects on the return type, so this asserts the
        // inverse: no public instance method on these types looks like a command yet returns
        // something that cannot carry an exit code.
        using var scope = new AssertionScope();
        foreach (var type in new[] { typeof(ScriniaCommands), typeof(BundleCommands) })
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;
                if (m.GetParameters().All(p => p.ParameterType != typeof(CancellationToken))) continue;
                (m.ReturnType == typeof(int) || m.ReturnType == typeof(Task<int>)).Should().BeTrue(
                    because: $"{type.Name}.{m.Name} takes a CancellationToken like every command does, " +
                             $"but returns {m.ReturnType.Name} — a command that cannot return an exit " +
                             "code cannot report failure");
            }
        }
    }
}
```

- [ ] **Step 2: Run it and confirm the two expected failures**

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CliSurfaceConformanceTests"
```

Expected: `Entrypoints_AreFound` PASSES, `EveryEntrypoint_CanReportFailure` PASSES,
`NoCommand_NamesItsWorkspaceRootParameterWorkspace` FAILS naming `ScriniaCommands.Migrate`,
`EveryBoolOption_DefaultsToFalse` FAILS naming `ScriniaCommands.Migrate`'s `--backup`.

If either failing test passes, stop and report: the reflection filter is not seeing `Migrate`, and every rule is then vacuous.

- [ ] **Step 3: Add the JSON output record**

In `src/Scrinia/Commands/CliJsonContext.cs`, add the attribute line alongside the others:

```csharp
[JsonSerializable(typeof(CliStoreMigrateOutput))]
```

and the record, next to `CliMigrateOutput`:

```csharp
/// <summary>Output of the store-layout migration command (<c>scri migrate</c>).</summary>
internal sealed record CliStoreMigrateOutput(
    bool Ran,
    int Moved,
    int Conflicts,
    string? BackupPath,
    bool BackupSkipped,
    bool DryRun);
```

Correct the neighbouring comment, which names the wrong command:

```csharp
/// <summary>Output of the attributes-migration command (<c>scri migrate_attributes</c>).</summary>
internal sealed record CliMigrateOutput(int Total, int Migrated, int Skipped, bool DryRun);
```

- [ ] **Step 4: Fix `Migrate`**

In `src/Scrinia/Commands/ScriniaCommands.Migration.cs`, change the signature and doc comments:

```csharp
/// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
/// <param name="dryRun">Print how many files would move without moving any.</param>
/// <param name="skipBackup">Skip the timestamped .scrinia/ backup (backup runs by default).</param>
/// <param name="json">Output as JSON instead of formatted text.</param>
public Task<int> Migrate(
    string? workspaceRoot = null,
    bool dryRun = false,
    bool skipBackup = false,
    bool json = false,
    CancellationToken cancellationToken = default)
{
    WorkspaceSetup.Configure(workspaceRoot);
```

Change the call through to the migration to pass the inverted flag:

```csharp
var result = StoreLayoutMigration.Migrate(root, dryRun, backup: !skipBackup);
```

Route the three existing exits through JSON when `--json` is given. The missing-store branch keeps its exit code:

```csharp
if (!Directory.Exists(Path.Combine(root, ".scrinia")))
{
    if (json) { WriteJsonError("No .scrinia/ directory found."); return Task.FromResult(1); }
    AnsiConsole.MarkupLine("[red]Error:[/] No .scrinia/ directory found.");
    return Task.FromResult(1);
}
```

and both success exits emit the record instead of markup:

```csharp
if (json)
{
    WriteJson(
        new CliStoreMigrateOutput(
            result.Ran, result.Moved, result.Conflicts,
            result.BackupPath, result.BackupSkipped, dryRun),
        CliJsonContext.Default.CliStoreMigrateOutput);
    return Task.FromResult(0);
}
```

Place that block immediately after `var result = ...` so it covers the nothing-to-migrate, dry-run and migrated cases with one shape, rather than three JSON branches that could disagree. Leave the existing markup path untouched below it.

- [ ] **Step 5: Run the conformance test again**

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CliSurfaceConformanceTests"
```
Expected: 4 passed.

- [ ] **Step 6: Update the documented signature**

In `docs/cli-reference.md`, replace the usage line at :497:

```
scri migrate [--workspace-root <path>] [--dry-run] [--skip-backup] [--json]
```

Update the surrounding flag table in that section to match: `--workspace-root`, `--dry-run`, `--skip-backup` (described as "skip the timestamped backup; the backup runs by default"), `--json`. Remove any `--backup=true|false` text.

- [ ] **Step 7: Full suite for the CLI assembly**

```bash
dotnet test tests/Scrinia.Tests --no-build
```
Expected: 0 failed. If a test asserted `--workspace` or the old backup flag, fix the test to the new surface and say so in the report — that is a real caller of the changed surface, and the report is where it gets recorded.

- [ ] **Step 8: Commit**

```bash
git add tests/Scrinia.Tests/CliSurfaceConformanceTests.cs src/Scrinia/Commands/ScriniaCommands.Migration.cs \
        src/Scrinia/Commands/CliJsonContext.cs docs/cli-reference.md
git commit -m "fix(cli)!: migrate joins the option family, pinned by a conformance test"
```

---

### Task 4: `scri hint --json` through the generated context

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.HintCommand.cs:88-100, :149`
- Modify: `src/Scrinia/Commands/CliJsonContext.cs`
- Create: `tests/Scrinia.Tests/HintJsonOutputTests.cs`

**Interfaces:**
- Consumes: `CliJsonContext` and the `WriteJson` helper on `ScriniaCommands` (`src/Scrinia/Commands/ScriniaCommands.cs:23`).
- Produces: `CliHintOutput`, `CliHintMatch`.

- [ ] **Step 1: Write the failing test**

`tests/Scrinia.Tests/HintJsonOutputTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;

namespace Scrinia.Tests;

/// <summary>
/// `scri hint --json` built its output by string interpolation, escaping the memory name with a
/// two-character escaper and interpolating the scope with no escaping at all — from the one
/// command whose whole purpose is to be consumed by another program.
///
/// <para><b>Reachability.</b> A path segment is sanitized (MemoryNaming.TrySanitizeSegment
/// replaces `"` and `\`), but the ENTRY NAME held in the index is not the path: a store write
/// keeps the name verbatim and derives a separate sanitized filename. `hint`'s matches read the
/// index, so a quote in a name reaches this emitter intact. Bundle import writes entry names
/// straight from an external bundle, which makes the route an untrusted one rather than only a
/// theoretical one.</para>
///
/// <para>The hook envelope was never affected: Hint.BuildHookEnvelope uses Utf8JsonWriter.
/// Only --json hand-rolled.</para>
/// </summary>
public sealed class HintJsonOutputTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;
    private readonly TextWriter _originalOut;

    public HintJsonOutputTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_hint_json_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".scrinia", "store", "local"));
        _store = new FileMemoryStore(_root);
        _originalOut = Console.Out;
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Same seeding shape as HintCommandTests.AddMemory — one way to populate a store
    /// in this project, not two. The entry name is stored verbatim; ArtifactPath sanitizes the
    /// filename separately, which is exactly why an awkward name survives into the index.</summary>
    private void AddMemory(string name, string content)
    {
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow);
        _store.Upsert(entry, "local");
        string path = _store.ArtifactPath(name, "local");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public async Task HintJson_WithAQuoteInTheName_IsParseableJson()
    {
        const string awkwardName = "oauth\"quoted\\flow";
        AddMemory(awkwardName, "OAuth implementation notes for the quoted flow");

        var writer = new StringWriter();
        Console.SetOut(writer);
        int exit;
        try
        {
            var cmd = new ScriniaCommands();
            exit = await cmd.Hint(
                prompt: "OAuth implementation notes for the quoted flow",
                workspaceRoot: _root,
                json: true);
        }
        finally
        {
            Console.SetOut(_originalOut);
        }

        exit.Should().Be(0);
        string output = writer.ToString().Trim();
        output.Should().NotBeEmpty(because: "the seeded memory matches the prompt, so a hint is emitted");

        Action parse = () => JsonDocument.Parse(output);
        parse.Should().NotThrow(because: "a --json surface must emit JSON a consumer can parse");

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("matches")[0].GetProperty("name").GetString()
            .Should().Be(awkwardName, because: "the name must survive the round trip verbatim");
    }
}
```

Required usings: `System.Text.Json`, `FluentAssertions`, `Scrinia.Commands`, `Scrinia.Core`, `Scrinia.Core.Encoding`, `Scrinia.Core.Models`. Note that `tests/Scrinia.Tests/GlobalUsings.cs` carries only `global using Xunit;` — every other namespace must be declared in the file.

If the seeded memory does not score above `HintCommand.DefaultMinScore` and no hint is emitted, adjust the *prompt* to overlap the content more strongly — never the assertion. A test that passes because nothing was emitted proves nothing.

- [ ] **Step 2: Run it and confirm it fails on the parse**

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~HintJsonOutputTests"
```
Expected: FAIL at `parse.Should().NotThrow(...)`.

If it fails earlier — no output emitted — the seeding is not producing a match; fix the seeding before touching the production code, or the test proves nothing.

- [ ] **Step 3: Add the records**

In `src/Scrinia/Commands/CliJsonContext.cs`:

```csharp
[JsonSerializable(typeof(CliHintOutput))]
```

```csharp
/// <summary>Output of <c>scri hint --json</c> — the raw structured HintResult shape for
/// programmatic consumers, distinct from the hook envelope Hint.BuildHookEnvelope emits.</summary>
internal sealed record CliHintOutput(int Count, CliHintMatch[] Matches);

internal sealed record CliHintMatch(string Scope, string Name, double Score);
```

`CliHintMatch` needs no `[JsonSerializable]` of its own — the generator reaches it through `CliHintOutput`.

- [ ] **Step 4: Replace the hand-rolled emission**

In `src/Scrinia/Commands/ScriniaCommands.HintCommand.cs`, replace the `if (json)` block:

```csharp
if (json)
{
    // Raw structured HintResult shape for programmatic consumers — distinct from the hook
    // envelope (which wraps a model-facing instruction string). Through the generated context,
    // not string interpolation: this used to escape the name with a two-character escaper and
    // interpolate the scope with none at all.
    WriteJson(
        new CliHintOutput(
            result.Matches.Count,
            [.. result.Matches.Select(m => new CliHintMatch(m.Scope, m.Name, m.Score))]),
        CliJsonContext.Default.CliHintOutput);
}
```

Delete the `JsonEscape` method at `:149`. Confirm it has no other caller first:

```bash
grep -rn "JsonEscape" src/ tests/ --include=*.cs
```
Expected after deletion: no matches.

- [ ] **Step 5: Run the test**

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~HintJsonOutputTests"
```
Expected: PASS.

Then check nothing else asserted the old shape:

```bash
dotnet test tests/Scrinia.Tests --no-build
```
Expected: 0 failed. `CliJsonContext` uses `WriteIndented = true` and camelCase, so the emitted JSON is now pretty-printed where it used to be one line — any golden file or substring assertion over `hint --json` output must be updated, and the change recorded in the report.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia/Commands/ScriniaCommands.HintCommand.cs src/Scrinia/Commands/CliJsonContext.cs \
        tests/Scrinia.Tests/HintJsonOutputTests.cs
git commit -m "fix(cli): hint --json emits real JSON through the generated context"
```

---

### Task 5: Make the double-underscore environment variable convention real

**Files:**
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs:616-650`
- Modify: `docs/cli-reference.md:551`
- Create: `tests/Scrinia.Tests/ConfigEnvVarConventionTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: nothing later tasks consume.

- [ ] **Step 1: Write the failing test**

`tests/Scrinia.Tests/ConfigEnvVarConventionTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// WorkspaceSetup.GetConfigValue performed two environment lookups whose expressions produce a
/// byte-identical string for every key in this repository — no key contains a '.' or a doubled
/// underscore — so the second could never find anything the first did not. Its own doc comment
/// claimed SCRINIA__EMBEDDINGS__PROVIDER resolved. It did not.
///
/// <para>This matters across surfaces, not just within the CLI: the server builds configuration
/// through WebApplication.CreateBuilder, whose environment provider maps the double-underscore
/// form natively. The same key took one form on the server and a different one here.</para>
/// </summary>
[Collection(EnvVarConventionCollection.Name)]
public sealed class ConfigEnvVarConventionTests : IDisposable
{
    private const string Key = "Scrinia:Embeddings:Provider";
    private const string SingleForm = "SCRINIA_EMBEDDINGS_PROVIDER";
    private const string DoubleForm = "SCRINIA__EMBEDDINGS__PROVIDER";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SingleForm, null);
        Environment.SetEnvironmentVariable(DoubleForm, null);
    }

    /// <summary><c>WorkspaceSetup</c> is `internal static` and `src/Scrinia/Scrinia.csproj:30`
    /// declares `InternalsVisibleTo Scrinia.Tests`, so this calls the method directly — no
    /// reflection, and a rename becomes a compile error rather than a silent skip.</summary>
    private static string? Resolve(string key) => WorkspaceSetup.GetConfigValue(key);

    [Fact]
    public void SingleUnderscoreForm_Resolves()
    {
        Environment.SetEnvironmentVariable(SingleForm, "from-single");
        Resolve(Key).Should().Be("from-single");
    }

    [Fact]
    public void DoubleUnderscoreForm_Resolves()
    {
        Environment.SetEnvironmentVariable(DoubleForm, "from-double");
        Resolve(Key).Should().Be("from-double",
            because: "the documented .NET-standard form must resolve — the server already accepts it");
    }

    [Fact]
    public void SingleUnderscore_WinsWhenBothAreSet()
    {
        Environment.SetEnvironmentVariable(SingleForm, "from-single");
        Environment.SetEnvironmentVariable(DoubleForm, "from-double");
        Resolve(Key).Should().Be("from-single",
            because: "single-underscore is the historically documented form and keeps priority, so " +
                     "adding the double form cannot change an existing installation's behaviour");
    }
}

/// <summary>Environment variables are process-wide, so these tests must not run beside anything
/// else that reads Scrinia config. Same shape as Nmp2DecodeCapCollection and the three other
/// serialised collections in this project.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvVarConventionCollection
{
    public const string Name = "EnvVarConvention";
}
```

`WorkspaceSetup.GetConfigValue` also consults the workspace config file when neither environment form is set. These tests never assert the unset case, so no config fixture is needed — but if a test is added that does, it must set an explicit workspace root first, or it will read whatever `.scrinia/config.json` exists in the directory the test host happens to run from.

- [ ] **Step 2: Run and confirm exactly one failure**

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~ConfigEnvVarConventionTests"
```
Expected: `DoubleUnderscoreForm_Resolves` FAILS; the other two PASS.

- [ ] **Step 3: Fix both lookups**

In `src/Scrinia/Commands/WorkspaceSetup.cs`, `GetConfigValue`:

```csharp
/// <summary>
/// Reads config from environment variables, then falls back to the workspace config file.
/// A colon-separated key (e.g. "Scrinia:Embeddings:Provider") is accepted in two environment
/// forms: single-underscore (SCRINIA_EMBEDDINGS_PROVIDER), which is the historically documented
/// one and keeps priority, and the .NET-standard double-underscore form
/// (SCRINIA__EMBEDDINGS__PROVIDER), which the server accepts natively through
/// WebApplication.CreateBuilder's environment provider.
/// </summary>
internal static string? GetConfigValue(string key)
{
    // 1. Environment variable, single-underscore form (highest priority)
    string envKey = key.Replace(':', '_').Replace("__", "_").ToUpperInvariant();
    string? value = Environment.GetEnvironmentVariable(envKey);
    if (value is not null) return value;

    // 2. Environment variable, .NET-standard double-underscore form. This branch used to
    //    compute a string byte-identical to the one above for every key in the repository, so
    //    the documented form never resolved.
    envKey = key.Replace(":", "__").ToUpperInvariant();
    value = Environment.GetEnvironmentVariable(envKey);
    if (value is not null) return value;

    // 3. Config file (workspace-scoped)
    return WorkspaceConfig.GetValue(Root, key);
}
```

Apply the same second lookup to `GetPluginName` (`:618-629`), between its existing environment lookup and its config-file fallback, so `plugins:embeddings` behaves like every other key. Update that method's doc comment to name both forms.

- [ ] **Step 4: Run the test**

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~ConfigEnvVarConventionTests"
```
Expected: 3 passed.

- [ ] **Step 5: Document both forms**

In `docs/cli-reference.md`, replace the settings-resolution bullet at :551:

```
1. **Environment variable** (highest) -- either the single-underscore form, key with `:` replaced by `_` and uppercased (e.g., `SCRINIA_EMBEDDINGS_PROVIDER`), or the .NET-standard double-underscore form (e.g., `SCRINIA__EMBEDDINGS__PROVIDER`), which the server also accepts. When both are set the single-underscore form wins.
```

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia/Commands/WorkspaceSetup.cs docs/cli-reference.md \
        tests/Scrinia.Tests/ConfigEnvVarConventionTests.cs
git commit -m "fix(cli): the documented double-underscore env form actually resolves"
```

---

### Task 6: Say what actually ships (L1a and the deliberate zero)

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:133-136`
- Modify: `src/Scrinia.Server/Auth/RequestContext.cs:26-27`
- Modify: `src/Scrinia/Commands/ScriniaCommands.HintCommand.cs` (the final `return 0`)

**Interfaces:** none. This task changes no behaviour and adds no test. Its diff must be comments only — if a step here changes an expression, stop and report it.

- [ ] **Step 1: Comment the read-only gate**

Replace the doc comment on `MemoryEndpoints.IsWritable`:

```csharp
/// <summary>True when the caller may write to <c>store</c> — i.e. the store is not
/// marked read-only by an SSO store_access claim. API keys default to read-write.
///
/// <para><b>No shipped configuration can make this false today.</b> The claim is read from
/// <see cref="RequestContext.StoreAccessLevels"/>, which <c>Program.cs</c> fills from
/// <c>store_access</c> claims — and nothing outside the test suite emits one. The only
/// <c>IClaimsTransformation</c> in the tree is a test stand-in
/// (<c>Scrinia.Server.Tests.MarkTargetUserReadOnlyTransform</c>), and no authentication handler
/// adds the claim, so every request evaluates as writable at all eight call sites. This is
/// deliberate forward-looking scaffolding, not a live enforcement path: a reader of the guards
/// below should not conclude that read-only stores are enforced today. To make it bite, register
/// an <c>IClaimsTransformation</c> outside tests that emits <c>store_access</c> claims of the
/// form <c>{store}:read-only</c>.</para></summary>
```

- [ ] **Step 2: Comment the claim source**

On `RequestContext.StoreAccessLevels`:

```csharp
/// <summary>Store access level claims, populated from an SSO claim transformer.
/// <b>Empty in every shipped configuration</b> — no <c>IClaimsTransformation</c> outside the test
/// suite emits <c>store_access</c>, so <see cref="GetStoreAccessLevel"/> always returns its
/// read-write default. See <c>MemoryEndpoints.IsWritable</c> for what would have to be added.</summary>
```

- [ ] **Step 3: Comment the deliberate zero**

At the final `return 0;` of `ScriniaCommands.Hint`:

```csharp
// Deliberate unconditional zero, not a missing failure path. This command runs as a
// UserPromptSubmit hook: a non-zero exit is how a hook signals "block this prompt", so
// reporting an internal failure that way would fail the user's turn over a missing hint.
// Every early return above is zero for the same reason.
return 0;
```

- [ ] **Step 4: Verify the diff is comments only**

```bash
git diff -U0 -- src/ | grep '^[+-]' | grep -v '^[+-][+-]' | grep -v '^\s*[+-]\s*//' | grep -v '^\s*[+-]\s*///'
```
Expected: no output. Any line printed is a code change and must be reverted or reported.

- [ ] **Step 5: Build and commit**

```bash
rtk dotnet build Scrinia.sln
```
Expected: 0 errors.

```bash
git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs src/Scrinia.Server/Auth/RequestContext.cs \
        src/Scrinia/Commands/ScriniaCommands.HintCommand.cs
git commit -m "docs(server): say that the read-only store gate ships inert"
```

---

### Task 7: The two guardrails carried from branch 4

**Files:**
- Modify: `tests/Scrinia.Server.Tests/PluginPipelineTests.cs`
- Modify: `tests/Scrinia.Server.Tests/BuiltInEmbeddingsServiceReindexTests.cs`

**Interfaces:**
- Consumes: the existing harnesses in both files — `new PluginPipeline([...])` with `GetStore()`, and the `SeedEntry` / `FakeProvider` / `VectorStore` setup in the reindex tests. Do not build new harnesses.

- [ ] **Step 1: Pin the pipeline's deduplication**

Add to `tests/Scrinia.Server.Tests/PluginPipelineTests.cs`:

```csharp
/// <summary>
/// A plugin may register itself as an IMemoryOperationHook inside its own ConfigureServices AND
/// be registered again by the loader that discovered it — EmbeddingsPlugin.ConfigureServices and
/// Program.cs both add the same instance. AddSingleton appends rather than replaces, so
/// GetServices&lt;IMemoryOperationHook&gt;() hands back one instance twice, and PluginPipeline's
/// constructor deduplicates by reference. Firing one instance twice per operation would break the
/// documented once-per-operation guarantee. Only a comment held this; now a test does.
/// </summary>
[Fact]
public async Task Store_withTheSameHookRegisteredTwice_firesItOnce()
{
    var hook = new CountingHook();
    var pipeline = new PluginPipeline([hook, hook]);
    var store = GetStore();

    await pipeline.StoreAsync(store, new StoreRequest(["dedupe"], "pipeline-dedupe-1"));

    hook.BeforeStoreCalls.Should().Be(1,
        because: "the same instance registered twice is one hook, not two");
}
```

`CountingHook` is a minimal `IMemoryOperationHook` counting its before-store invocations. This file already contains hook doubles — reuse one that counts, or add the smallest possible counter beside them, following the shape already in the file. Do not introduce a mocking library.

- [ ] **Step 2: Confirm it can fail**

Temporarily change `PluginPipeline`'s constructor (`src/Scrinia.Server/Services/PluginPipeline.cs:25`) from `hooks.Distinct()` to `hooks`, run the test, and confirm it FAILS with `2` observed. Revert the mutation immediately and re-run to confirm green. Record the observed failure in the report.

- [ ] **Step 3: Pin ephemeral reindex coverage**

Add to `tests/Scrinia.Server.Tests/BuiltInEmbeddingsServiceReindexTests.cs`:

```csharp
/// <summary>
/// Scrinia has FOUR full-corpus reindex loops and they split on the ephemeral scope.
/// EmbeddingReindexer.ReindexAsync and ReindexViaSinkAsync filter it out; this one and
/// EmbeddingsPlugin.ReindexStoreAsync do not, because they iterate store.ListScoped(), which
/// includes ephemeral memories by design. So a literal scope of "ephemeral" reaches
/// MemoryArtifactSource ONLY from here, and only this path exercises its scope-keyed
/// IsEphemeral(scope) check.
///
/// <para>On 2026-08-17 a review residual asserted that check was provably dead and instructed its
/// deletion — a claim derived from inspecting only EmbeddingReindexer's two loops. No test drove
/// this path, so the deletion would have shipped green. This test is what makes it fail. Before
/// calling MemoryArtifactSource.IsEphemeral or EphemeralScope dead, run this.</para>
/// </summary>
[Fact]
public async Task ReindexStoreAsync_EphemeralMemory_IndexesItUnderTheEphemeralScope()
{
    const string name = "scratch-note";
    const string content = "ephemeral working note";
    _store.RememberEphemeral(name, new EphemeralEntry(
        Name: name,
        Artifact: Nmp2ChunkedEncoder.Encode(content),
        OriginalBytes: content.Length,
        ChunkCount: 1,
        CreatedAt: DateTimeOffset.UtcNow));

    using var vectorStore = new VectorStore(_embeddingsDir, expectedSignature: null);
    var service = new BuiltInEmbeddingsService(
        new FakeProvider(), vectorStore, semanticWeight: 50.0,
        NullLogger<BuiltInEmbeddingsService>.Instance);

    int count = await service.ReindexStoreAsync(_store, CancellationToken.None);

    count.Should().Be(1, because: "ListScoped includes ephemeral memories, so reindex must embed them");
    vectorStore.GetVectors(MemoryArtifactSource.EphemeralScope)
        .Should().HaveCount(1,
            because: "the scope-keyed ephemeral branch in MemoryArtifactSource.ReadArtifactAsync is " +
                     "what resolves '~name'; without it this memory is silently skipped");
}
```

Add `using Scrinia.Core.Models;` if the file does not already have it.

- [ ] **Step 4: Confirm it can fail**

Temporarily change `MemoryArtifactSource.ReadArtifactAsync` (`src/Scrinia.Core/Embeddings/MemoryArtifactSource.cs:62-65`) so the ephemeral branch is not taken — replace the conditional with the non-ephemeral call alone. Run the test and confirm it FAILS. Revert immediately, re-run, confirm green. Record the observed failure.

This is the whole point of the task: the mutation is the deletion that was nearly shipped.

- [ ] **Step 5: Run the server suite**

```bash
dotnet test tests/Scrinia.Server.Tests --no-build
```
Expected: 0 failed.

- [ ] **Step 6: Commit**

```bash
git add tests/Scrinia.Server.Tests/PluginPipelineTests.cs \
        tests/Scrinia.Server.Tests/BuiltInEmbeddingsServiceReindexTests.cs
git commit -m "test: hold the hook dedupe and the ephemeral reindex path"
```

---

### Task 8: CHANGELOG

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Read the file's existing style before writing**

```bash
head -60 CHANGELOG.md
```

Entries use `### Category — descriptive summary` under `## [Unreleased]`. Match what is there; do not introduce a new heading shape.

- [ ] **Step 2: Add the entries**

Four sections under `## [Unreleased]`, each naming the user-visible change and, for the two breaking ones, what a user must change:

1. **Breaking — `scri migrate` joins the option family.** `--workspace` is now `--workspace-root`; `--backup=true|false` is now `--skip-backup` (the backup still runs by default); `--json` added. A script passing `--workspace` or `--backup` must be updated.
2. **Breaking — `publish.ps1`'s `-WithVulkan` is now `-WithEmbeddings`,** matching `publish.sh`'s `--with-embeddings`. The switch decides whether the embeddings plugin ships; Vulkan is the backend it carries.
3. **Fixed — the non-Windows publish path.** `publish.sh` now publishes `scri-merge`, so a Linux or macOS build no longer leaves a configured merge driver pointing at a binary that does not exist. Its cleanup step, which deleted the plugin directory it had just populated, is gone. Both scripts now take their project list from `publish.projects.tsv`, and warn instead of silently skipping when the Vulkan natives are absent.
4. **Fixed — `scri hint --json` emits parseable JSON,** and the documented `SCRINIA__EMBEDDINGS__PROVIDER` environment form now resolves alongside `SCRINIA_EMBEDDINGS_PROVIDER`.

- [ ] **Step 3: Full gate**

```bash
rtk dotnet build Scrinia.sln
dotnet test Scrinia.sln --no-build
```
Expected: 0 errors; 0 failed. Record the passed/failed/skipped counts per assembly in the report — the skipped count has flaked before (4 → 6 under full-suite load), so a change there is worth naming rather than glossing.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs: record the publish and CLI surface changes"
```
