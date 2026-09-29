# Security containment & authz hardening — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the confirmed path-containment and server-authorization holes from the 2026-07-29 security review, hardened to a multi-tenant threat model.

**Architecture:** Two groups. Group A hardens the shared `PathContainment` primitive and applies containment/slug validation at the four boundaries that build filesystem paths from untrusted input (skill/agent names, merge-config `ConflictDir`, bundle import categories, bundle `--output`). Group B closes five server authorization gaps (MCP `facts` gating, key-management store isolation, chat permission scope, read-only access enforcement, rate-limit ordering). Every fix is TDD: failing test first, minimal fix, green.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions, ASP.NET Core minimal APIs, `WebApplicationFactory<Program>` integration tests, source-generated JSON.

## Global Constraints

- **Threat model:** untrusted multi-tenant HTTP server. Tenant isolation (keys, chat, read-only, rate-limit) is a **hard boundary**; containment must resist hostile bundle/merge-config content from other tenants.
- **Build/test ONLY with** `-p:NuGetAudit=false` — `dotnet build Scrinia.sln` fails at restore on the pre-existing `Scrinia.AppHost` NuGet-audit blocker. Never use bare `dotnet build Scrinia.sln` as a gate.
- `TreatWarningsAsErrors=true` (repo `Directory.Build.props`) — all code must compile warning-clean.
- MCP/server paths use **source-generated JSON only** — no reflection-based serialization.
- Reuse the existing `Scrinia.Core.PathContainment` primitive; do not invent a second containment check.
- Conventional-commit messages (`feat:`/`fix:`). End every commit message with:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Do not touch** the user's uncommitted working-tree files (`.gitignore`, `src/Scrinia.Mcp/plugins/opencode/package.json`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`). Stage only files this plan creates/modifies.
- Test commands per project:
  - Core + MCP + CLI: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
  - Server: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false`
  - Merge: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false`

## File Structure

**Group A — containment & traversal**
- Modify `src/Scrinia.Core/PathContainment.cs` — resolve symlinks on both operands; OS-appropriate case. (A1)
- Create `src/Scrinia.Mcp/FileEntityName.cs` — shared skill/agent slug validator. (A2)
- Modify `src/Scrinia.Mcp/MemoryTools.Skills.cs` — validate in `SkillCreate` + `LoadSkillAsync`. (A2)
- Modify `src/Scrinia.Mcp/MemoryTools.Core.cs` — validate agent read (`~L35`) + write (`~L265`). (A2)
- Modify `src/Scrinia.Merge/Nmp2ConflictHandler.cs` — contain `ConflictDir`. (A3)
- Modify `src/Scrinia.Core/Bundles/BundleFormatService.cs` — allowlist import categories. (A4)
- Modify `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` — contain `--output`. (A5)

**Group B — server authorization**
- Modify `src/Scrinia.Server/Auth/McpPermissionFilter.cs` — gate `facts`. (B1)
- Modify `src/Scrinia.Server/Auth/RequestContext.cs` — add `IsGlobalAdmin`. (B2)
- Modify `src/Scrinia.Server/Endpoints/KeyEndpoints.cs` — store-scoped List/Get/Revoke. (B2)
- Modify `src/Scrinia.Server/Endpoints/ChatEndpoints.cs` — require chat+read+search. (B3)
- Modify `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` — enforce read-only access level on writes. (B4)
- Modify `src/Scrinia.Server/Program.cs` — move `UseRateLimiter()` after auth. (B5)

**Test files (new)**
- `tests/Scrinia.Tests/PathContainmentHardeningTests.cs` (A1)
- `tests/Scrinia.Tests/FileEntityNameTests.cs` (A2)
- `tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs` (A3)
- `tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs` (A4)
- `tests/Scrinia.Tests/BundleOutputContainmentTests.cs` (A5)
- `tests/Scrinia.Server.Tests/McpPermissionFilterTests.cs` (extend — B1)
- `tests/Scrinia.Server.Tests/KeyIsolationTests.cs` (B2)
- `tests/Scrinia.Server.Tests/ChatEndpointTests.cs` (extend — B3)
- `tests/Scrinia.Server.Tests/ReadOnlyAccessTests.cs` (B4)
- `tests/Scrinia.Server.Tests/RateLimitTests.cs` (extend — B5)

**Implementation order:** A1 first (foundational). Then A2–A5 (independent once A1 lands). Then B1–B5 (independent of Group A; B2 depends on the B2 `RequestContext` change).

---

### Task A1: Harden `PathContainment.IsWithin`

**Files:**
- Modify: `src/Scrinia.Core/PathContainment.cs`
- Test: `tests/Scrinia.Tests/PathContainmentHardeningTests.cs`

**Interfaces:**
- Produces: `bool PathContainment.IsWithin(string baseDir, string candidate)` — signature unchanged; behavior now resolves symlinks on both operands and compares with OS-appropriate case sensitivity.

- [ ] **Step 1: Write the failing tests**

```csharp
using Scrinia.Core;
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class PathContainmentHardeningTests
{
    [Fact]
    public void NestedPath_IsContained()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "pc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        PathContainment.IsWithin(baseDir, Path.Combine(baseDir, "sub", "file.txt")).Should().BeTrue();
    }

    [Fact]
    public void SiblingAndTraversal_AreRejected()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "pc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        PathContainment.IsWithin(baseDir, baseDir + "-sibling").Should().BeFalse();
        PathContainment.IsWithin(baseDir, Path.Combine(baseDir, "..", "escape")).Should().BeFalse();
    }

    [Fact]
    public void SymlinkedBase_LegitPath_StillContained()
    {
        string real = Path.Combine(Path.GetTempPath(), "pc-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(real);
        string link = Path.Combine(Path.GetTempPath(), "pc-link-" + Guid.NewGuid().ToString("N"));
        if (!TryCreateDirSymlink(link, real)) return; // environment can't create symlinks; skip
        try
        {
            // A file addressed through the symlinked base must still count as contained,
            // whether the base is given as the link or the real dir.
            string fileViaLink = Path.Combine(link, "note.txt");
            PathContainment.IsWithin(link, fileViaLink).Should().BeTrue();
            PathContainment.IsWithin(real, fileViaLink).Should().BeTrue();
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void SymlinkUnderBase_PointingOutward_IsRejected()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "pc-base-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "pc-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(outside);
        string evil = Path.Combine(baseDir, "evil");
        if (!TryCreateDirSymlink(evil, outside)) return; // skip if unsupported
        try
        {
            // base/evil resolves outside the base → escape must be rejected.
            PathContainment.IsWithin(baseDir, Path.Combine(evil, "secret.txt")).Should().BeFalse();
        }
        finally { Directory.Delete(evil); }
    }

    [Fact]
    public void CaseDifferingPath_RejectedOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return; // case-sensitivity contract only holds on Linux
        string baseDir = Path.Combine(Path.GetTempPath(), "pc-CASE-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        PathContainment.IsWithin(baseDir, baseDir.ToLowerInvariant() + "/x").Should().BeFalse();
    }

    private static bool TryCreateDirSymlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { return false; }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter PathContainmentHardeningTests -p:NuGetAudit=false`
Expected: `SymlinkUnderBase_PointingOutward_IsRejected` (and `CaseDifferingPath_RejectedOnLinux` on Linux) FAIL against the current lexical implementation.

- [ ] **Step 3: Rewrite `PathContainment` to resolve links + OS-appropriate case**

```csharp
namespace Scrinia.Core;

/// <summary>Symlink-resolving, trailing-separator-safe path containment check for filesystem sandboxing.</summary>
public static class PathContainment
{
    // Windows/macOS default filesystems are case-insensitive; Linux is case-sensitive.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>True iff <paramref name="candidate"/> resolves to <paramref name="baseDir"/> or a path nested under it.
    /// Both operands are canonicalized (symlinks/junctions resolved on every existing segment) before comparison.</summary>
    public static bool IsWithin(string baseDir, string candidate)
    {
        string baseFull = ResolveReal(baseDir);
        string candFull = ResolveReal(candidate);
        if (string.Equals(baseFull, candFull, PathComparison))
            return true;
        string withSep = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;
        return candFull.StartsWith(withSep, PathComparison);
    }

    /// <summary>
    /// Canonicalizes <paramref name="path"/> to a real absolute path, resolving symlinks/junctions on every
    /// segment that already exists. The path may not exist yet (write destinations), so non-existent trailing
    /// segments are kept lexically. Resolving per-segment (not just the final node) means a symlinked ancestor
    /// is followed too.
    /// </summary>
    private static string ResolveReal(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        if (root.Length == 0)
            return full;

        string[] segments = full[root.Length..]
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                   StringSplitOptions.RemoveEmptyEntries);

        string resolved = root;
        foreach (string seg in segments)
        {
            resolved = Path.Combine(resolved, seg);
            FileSystemInfo? info =
                Directory.Exists(resolved) ? new DirectoryInfo(resolved)
                : File.Exists(resolved) ? new FileInfo(resolved)
                : null;
            if (info is null)
                continue; // not-yet-existing segment: keep lexical form

            string? target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is not null)
                resolved = Path.IsPathRooted(target)
                    ? target
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(resolved)!, target));
        }
        return Path.GetFullPath(resolved);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter PathContainmentHardeningTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Run the full Core/MCP suite to confirm no regression in existing containment callers**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS (bundle zip-slip, workspace-containment, import tests all still green — this is the regression gate for the shared primitive).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/PathContainment.cs tests/Scrinia.Tests/PathContainmentHardeningTests.cs
git commit -m "fix(security): resolve symlinks and use OS-appropriate case in PathContainment"
```

---

### Task A2: Validate `/skill` and `/agent` names

**Files:**
- Create: `src/Scrinia.Mcp/FileEntityName.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs` (`SkillCreate` ~L208-211, `LoadSkillAsync` ~L370-371)
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (agent read ~L35-37, agent write ~L265-274)
- Test: `tests/Scrinia.Tests/FileEntityNameTests.cs`

**Interfaces:**
- Produces: `bool Scrinia.Mcp.FileEntityName.IsValid(string? name)` — true only for a safe filename slug.
- Consumes: `Scrinia.Core.PathContainment.IsWithin` (from A1) for the belt-and-suspenders containment assert.

- [ ] **Step 1: Write the failing test for the validator**

```csharp
using Scrinia.Mcp;
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class FileEntityNameTests
{
    [Theory]
    [InlineData("api-reviewer")]
    [InlineData("auth_researcher")]
    [InlineData("v1.helper")]
    public void AcceptsSafeSlugs(string name) => FileEntityName.IsValid(name).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows")]
    [InlineData("a..b")]
    public void RejectsTraversalAndSeparators(string? name) => FileEntityName.IsValid(name).Should().BeFalse();
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FileEntityNameTests -p:NuGetAudit=false`
Expected: FAIL — `FileEntityName` does not exist.

- [ ] **Step 3: Create the validator**

```csharp
namespace Scrinia.Mcp;

/// <summary>
/// Validation for user-supplied skill/agent name slugs that become on-disk filenames.
/// Rejects anything that could escape the target directory or is not a plain slug.
/// </summary>
internal static class FileEntityName
{
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
```

Note: `Scrinia.Tests` already has `InternalsVisibleTo` for `Scrinia.Mcp` internals (existing MCP tool tests rely on it). If the test cannot see `FileEntityName`, add `[assembly: InternalsVisibleTo("Scrinia.Tests")]` where the other MCP internals expose it — do NOT make the type public.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FileEntityNameTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Apply the guard at all four path-building sites**

In `MemoryTools.Skills.cs` `SkillCreate`, immediately after `var store = CurrentStore;` (before any path building):

```csharp
if (!FileEntityName.IsValid(name))
    return ResponseBuilder.Error(
        $"Invalid skill name '{name}'. Use letters, digits, '-', '_' or '.' only.",
        ErrorCodes.InvalidParameter,
        "remember({ path: '/skill/my-helper', content: [...] })").ToYaml();
```

In `MemoryTools.Skills.cs` `LoadSkillAsync`, at the top of the method:

```csharp
if (!FileEntityName.IsValid(skillName))
    return ResponseBuilder.Error(
        $"Invalid skill name '{skillName}'.", ErrorCodes.NotFound,
        "recall({ path: '/skill/' }) to list available skills").ToYaml();
```

In `MemoryTools.Core.cs` agent READ block (where `agentSubject` is parsed, ~L35), replace the bare parse with a validated one; on invalid, fall through to normal resolution (do not touch disk):

```csharp
string agentSubject = artifactOrName["/agent/".Length..].Trim();
if (FileEntityName.IsValid(agentSubject))
{
    string agentBaseDir = GetScriniaBaseDir(agentStore);
    string agentFilePath = Path.Combine(agentBaseDir, "agent", $"{agentSubject}.md");
    if (PathContainment.IsWithin(Path.Combine(agentBaseDir, "agent"), agentFilePath) && File.Exists(agentFilePath))
    {
        string mdContent = await File.ReadAllTextAsync(agentFilePath, cancellationToken);
        SessionBudget.RecordAccess(artifactOrName, mdContent.Length);
        return ResponseBuilder.Success(mdContent).WithAction(actionLabel).ToYaml();
    }
}
// Fall through to NMP/2 resolution for legacy entries
```

In `MemoryTools.Core.cs` agent WRITE block (~L265), replace the whitespace-only check with a full validity check:

```csharp
string agentSubject = name["/agent/".Length..].Trim();
if (!FileEntityName.IsValid(agentSubject))
    return ResponseBuilder.Error(
        "Invalid agent config name. Use letters, digits, '-', '_' or '.' only.",
        ErrorCodes.InvalidParameter,
        "Use a path like '/agent/profile' for the agent config.").ToYaml();

string baseDir = GetScriniaBaseDir(store);
string agentDir = Path.Combine(baseDir, "agent");
string filePath = Path.Combine(agentDir, $"{agentSubject}.md");
if (!PathContainment.IsWithin(agentDir, filePath))
    return ResponseBuilder.Error(
        "Invalid agent config name.", ErrorCodes.InvalidParameter,
        "Use a path like '/agent/profile' for the agent config.").ToYaml();
Directory.CreateDirectory(agentDir);
```

Add `using Scrinia.Core;` to `MemoryTools.Core.cs` if not already present (for `PathContainment`).

- [ ] **Step 6: Add a regression test proving a traversal write is rejected**

Add to `FileEntityNameTests.cs` (or a sibling MCP-tools test that already sets `MemoryStoreContext.Current` — match the existing pattern in `tests/Scrinia.Tests`). Assert that a `remember({ path: '/agent/../../evil', content: [...] })` call returns an error YAML and that no file `evil.md` exists outside the `agent` dir. If the MCP-tools test harness is not readily reusable, the validator unit test (Steps 1-4) is the gating test and this integration assertion is best-effort.

- [ ] **Step 7: Run the suite and commit**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS.

```bash
git add src/Scrinia.Mcp/FileEntityName.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/FileEntityNameTests.cs
git commit -m "fix(security): validate skill/agent names against path traversal"
```

---

### Task A3: Contain `merge.config` `ConflictDir`

**Files:**
- Modify: `src/Scrinia.Merge/Nmp2ConflictHandler.cs` (`Handle`, ~L24-30)
- Test: `tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs`

**Interfaces:**
- Consumes: `MergeConfig.ConflictDir` (string), `Scrinia.Core.PathContainment.IsWithin`.
- Produces: `Nmp2ConflictHandler.Handle` now throws `InvalidOperationException` on an out-of-bounds `ConflictDir` instead of writing outside the artifact dir.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Merge;
using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

public sealed class ConflictDirContainmentTests
{
    [Theory]
    [InlineData("../../../escape")]
    [InlineData("/abs/escape")]
    public void OutOfBoundsConflictDir_Throws(string conflictDir)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Three distinct files so neither trivial-merge branch short-circuits.
        string ancestor = Path.Combine(dir, "a.nmp2"); File.WriteAllText(ancestor, "ANC");
        string ours = Path.Combine(dir, "ours.nmp2"); File.WriteAllText(ours, "OURS");
        string theirs = Path.Combine(dir, "theirs.nmp2"); File.WriteAllText(theirs, "THEIRS");
        var config = new MergeConfig(ConflictDir: conflictDir);

        Action act = () => Nmp2ConflictHandler.Handle(ancestor, ours, theirs, config);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RelativeConflictDir_IsAllowed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string ancestor = Path.Combine(dir, "a.nmp2"); File.WriteAllText(ancestor, "ANC");
        string ours = Path.Combine(dir, "ours.nmp2"); File.WriteAllText(ours, "OURS");
        string theirs = Path.Combine(dir, "theirs.nmp2"); File.WriteAllText(theirs, "THEIRS");
        var config = new MergeConfig(); // default ConflictDir = "conflict"

        Nmp2ConflictHandler.Handle(ancestor, ours, theirs, config);
        Directory.Exists(Path.Combine(dir, "conflict", "current")).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run it to verify the traversal test fails**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj --filter ConflictDirContainmentTests -p:NuGetAudit=false`
Expected: `OutOfBoundsConflictDir_Throws` FAILS (no exception today).

- [ ] **Step 3: Add the containment guard**

In `Nmp2ConflictHandler.Handle`, replace the `conflictDir` construction (currently `string conflictDir = Path.Combine(dir, config.ConflictDir);`) with:

```csharp
string dir = Path.GetDirectoryName(oursPath)!;
string conflictDir = Path.Combine(dir, config.ConflictDir);
if (!Scrinia.Core.PathContainment.IsWithin(dir, conflictDir))
    throw new InvalidOperationException(
        $"merge.config ConflictDir '{config.ConflictDir}' escapes the artifact directory.");
```

Add `using Scrinia.Core;` at the top if not present (then drop the fully-qualified name). Confirm `Scrinia.Merge` references `Scrinia.Core` (it uses other Core types already; if not, add the ProjectReference).

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj --filter ConflictDirContainmentTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Run the merge suite and commit**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false`
Expected: PASS.

```bash
git add src/Scrinia.Merge/Nmp2ConflictHandler.cs tests/Scrinia.Merge.Tests/ConflictDirContainmentTests.cs
git commit -m "fix(security): reject out-of-bounds merge.config ConflictDir"
```

---

### Task A4: Allowlist bundle import categories

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs` (`ImportFileEntities`, ~L344-355)
- Test: `tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs`

**Interfaces:**
- Consumes: the existing `FileEntityCategories` allowlist (`["workflows", "skills", "agent"]`, `BundleFormatService.cs:35`).

**Context:** import already containment-checks the category dir and dest path, but places NO allowlist on the manifest category key. An empty category (`""`) makes `categoryDir == baseDir` (which `IsWithin` accepts as equal), letting a crafted bundle write `.scrinia/merge.config`. Fix = allowlist the category.

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Scrinia.Core.Bundles;
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class BundleImportCategoryAllowlistTests
{
    [Fact]
    public void Import_WithNonAllowlistedCategory_DoesNotWriteFile()
    {
        // Build an in-memory bundle whose manifest declares an empty ("") category
        // holding merge.config — the escape the allowlist must block.
        string tmp = Path.Combine(Path.GetTempPath(), "bi-" + Guid.NewGuid().ToString("N"));
        string scriniaDir = Path.Combine(tmp, ".scrinia", "stores", "local");
        Directory.CreateDirectory(scriniaDir);
        string baseDir = Path.Combine(tmp, ".scrinia");
        string bundlePath = Path.Combine(tmp, "evil.scrinia-bundle");

        using (var fs = File.Create(bundlePath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var manifest = """
                { "version": 2, "fileEntities": { "": ["merge.config"] } }
                """;
            WriteEntry(zip, "manifest.json", manifest);
            WriteEntry(zip, "files//merge.config", "resolver = evil");
        }

        // Act: import (use the same entry point production uses — adapt to the real signature).
        BundleImportTestHarness.Import(bundlePath, baseDir);

        // Assert: the malicious file was NOT written under .scrinia.
        File.Exists(Path.Combine(baseDir, "merge.config")).Should().BeFalse();

        static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open(), Encoding.UTF8);
            w.Write(content);
        }
    }
}
```

Note: `BundleImportTestHarness.Import` is a stand-in — wire the test to the actual public import entry point in `BundleFormatService` (match how existing bundle round-trip tests in `tests/Scrinia.Tests` invoke import; reuse their store/`baseDir` setup helper rather than the sketch above if one exists).

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter BundleImportCategoryAllowlistTests -p:NuGetAudit=false`
Expected: FAIL — `merge.config` is written today.

- [ ] **Step 3: Add the allowlist guard**

In `ImportFileEntities`, at the top of the `foreach (var categoryProp in ...)` loop, before building `categoryDir`:

```csharp
string category = categoryProp.Name;
if (!FileEntityCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
    continue; // only workflows/skills/agent may be imported — reject arbitrary category keys
if (categoryProp.Value.ValueKind != JsonValueKind.Array)
    continue;
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter BundleImportCategoryAllowlistTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Run the suite and commit**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS (existing bundle round-trip tests use allowlisted categories, so they stay green).

```bash
git add src/Scrinia.Core/Bundles/BundleFormatService.cs tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs
git commit -m "fix(security): allowlist bundle import categories"
```

---

### Task A5: Contain `bundle --output`

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` (~L143-153)
- Test: `tests/Scrinia.Tests/BundleOutputContainmentTests.cs`

**Interfaces:**
- Produces: `internal static bool ScriniaCommands.TryResolveBundlePath(string exportsDir, string? output, out string bundlePath, out string? error)` — extracted, unit-testable path resolver.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Commands; // adjust to the real namespace of ScriniaCommands
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class BundleOutputContainmentTests
{
    private static readonly string Exports = Path.Combine(Path.GetTempPath(), "exp-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultOrPlainName_ResolvesInsideExports()
    {
        ScriniaCommands.TryResolveBundlePath(Exports, "my-topic", out string path, out string? err)
            .Should().BeTrue();
        err.Should().BeNull();
        Scrinia.Core.PathContainment.IsWithin(Exports, path).Should().BeTrue();
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("/abs/escape")]
    public void TraversalOrAbsolute_IsRejected(string output)
    {
        ScriniaCommands.TryResolveBundlePath(Exports, output, out _, out string? err)
            .Should().BeFalse();
        err.Should().NotBeNull();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter BundleOutputContainmentTests -p:NuGetAudit=false`
Expected: FAIL — method does not exist.

- [ ] **Step 3: Extract + guard the resolver, and call it from the command**

Add to `ScriniaCommands.Bundle.cs`:

```csharp
/// <summary>Resolves the bundle output path inside <paramref name="exportsDir"/>, rejecting any
/// --output that escapes it (absolute path or traversal).</summary>
internal static bool TryResolveBundlePath(string exportsDir, string? output, out string bundlePath, out string? error)
{
    error = null;
    string bundleName = string.IsNullOrWhiteSpace(output)
        ? $"bundle-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
        : output;
    if (!bundleName.EndsWith(".scrinia-bundle", StringComparison.OrdinalIgnoreCase))
        bundleName += ".scrinia-bundle";

    bundlePath = Path.GetFullPath(Path.Combine(exportsDir, bundleName));
    if (!Scrinia.Core.PathContainment.IsWithin(exportsDir, bundlePath))
    {
        error = $"--output '{output}' must stay within the exports directory.";
        bundlePath = "";
        return false;
    }
    return true;
}
```

Replace the inline `bundleName`/`bundlePath` block (~L147-153) with a call to `TryResolveBundlePath`, using the caller's `sanitizedTopic` for the default-name prefix; on `false`, emit the error the same way other CLI errors are surfaced (JSON or `AnsiConsole.MarkupLine("[red]Error:[/] ...")`) and return exit code 1.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter BundleOutputContainmentTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Run the suite and commit**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS.

```bash
git add src/Scrinia/Commands/ScriniaCommands.Bundle.cs tests/Scrinia.Tests/BundleOutputContainmentTests.cs
git commit -m "fix(security): contain bundle --output within exports dir"
```

---

### Task B1: Gate the `facts` MCP tool

**Files:**
- Modify: `src/Scrinia.Server/Auth/McpPermissionFilter.cs` (`RequiredPermission`, ~L47-55)
- Test: `tests/Scrinia.Server.Tests/McpPermissionFilterTests.cs` (extend)

**Interfaces:**
- Produces: `RequiredPermission("tools/call", "facts") == "search"`.

- [ ] **Step 1: Write the failing test** (add to the existing `McpPermissionFilterTests`)

```csharp
[Fact]
public void Facts_RequiresSearchPermission()
{
    McpPermissionFilter.RequiredPermission("tools/call", "facts").Should().Be("search");
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter McpPermissionFilterTests -p:NuGetAudit=false`
Expected: FAIL — currently returns `null`.

- [ ] **Step 3: Add `facts` to the map**

In `RequiredPermission`, add to the `tools/call` switch:

```csharp
"search" or "list" or "facts" => "search",
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter McpPermissionFilterTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Auth/McpPermissionFilter.cs tests/Scrinia.Server.Tests/McpPermissionFilterTests.cs
git commit -m "fix(security): gate facts MCP tool behind search permission"
```

---

### Task B2: Key-management store isolation

**Files:**
- Modify: `src/Scrinia.Server/Auth/RequestContext.cs` (add `IsGlobalAdmin`)
- Modify: `src/Scrinia.Server/Endpoints/KeyEndpoints.cs` (`ListKeys`, `GetKey`, `RevokeKey`)
- Test: `tests/Scrinia.Server.Tests/KeyIsolationTests.cs`

**Interfaces:**
- Produces: `bool RequestContext.IsGlobalAdmin` (true iff `Stores` contains `"*"`).
- Consumes: `RequestContext.CanAccessStore`, `ApiKeyStore.KeySummary { Stores, ... }`, `ApiKeyStore.ListKeys/GetKey/RevokeKey`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class KeyIsolationTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;
    public KeyIsolationTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task StoreScopedAdmin_CannotSeeOtherStoresKeys()
    {
        var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();

        // A manage_keys admin scoped ONLY to SecondaryStore ("store-2").
        var (scopedAdminRaw, scopedAdminId, _) = keyStore.CreateKey(
            "store2-admin", [_factory.SecondaryStore], ["manage_keys"], "scoped-admin");

        // A target key scoped ONLY to PrimaryStore ("test-store").
        var (_, primaryOnlyId, _) = keyStore.CreateKey(
            "primary-user", [_factory.PrimaryStore], ["search"], "primary-only");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scopedAdminRaw);

        // List: must NOT include the primary-only key; must include the scoped admin's own key.
        var listResp = await client.GetAsync("/api/v1/keys/");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var keys = await listResp.Content.ReadFromJsonAsync<KeySummaryDto[]>();
        keys!.Select(k => k.Id).Should().Contain(scopedAdminId);
        keys!.Select(k => k.Id).Should().NotContain(primaryOnlyId);

        // Get + Revoke of the out-of-scope key: 404 (no cross-tenant existence leak).
        (await client.GetAsync($"/api/v1/keys/{primaryOnlyId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/v1/keys/{primaryOnlyId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GlobalAdmin_WithWildcardStore_SeesAllKeys()
    {
        var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();
        var (globalRaw, _, _) = keyStore.CreateKey("global-admin", ["*"], ["manage_keys"], "global");
        var (_, someId, _) = keyStore.CreateKey("someone", [_factory.PrimaryStore], ["search"], "target");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", globalRaw);

        var keys = await (await client.GetAsync("/api/v1/keys/"))
            .Content.ReadFromJsonAsync<KeySummaryDto[]>();
        keys!.Select(k => k.Id).Should().Contain(someId);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter KeyIsolationTests -p:NuGetAudit=false`
Expected: `StoreScopedAdmin_CannotSeeOtherStoresKeys` FAILS — today every manage_keys holder sees all keys.

- [ ] **Step 3: Add `IsGlobalAdmin` to `RequestContext`**

```csharp
/// <summary>True when the caller holds wildcard store access ("*") — the global admin role.</summary>
public bool IsGlobalAdmin => Stores.Contains("*", StringComparer.OrdinalIgnoreCase);
```

- [ ] **Step 4: Scope the three key handlers**

In `KeyEndpoints.cs`, add a private helper and inject `RequestContext`:

```csharp
// A caller may manage a key iff it is a global admin, or every store the key grants is
// one the caller can access (all, not any — a key granting an out-of-scope store stays hidden).
private static bool CanManage(RequestContext ctx, string[] keyStores) =>
    ctx.IsGlobalAdmin || (keyStores.Length > 0 && keyStores.All(ctx.CanAccessStore));

private static IResult ListKeys(RequestContext ctx, ApiKeyStore keyStore)
{
    var dtos = keyStore.ListKeys()
        .Where(k => CanManage(ctx, k.Stores))
        .Select(k => new KeySummaryDto(
            k.Id, k.UserId, k.Stores, k.Permissions,
            k.Label, k.CreatedAt, k.LastUsedAt, k.Revoked))
        .ToArray();
    return Results.Ok(dtos);
}

private static IResult GetKey(string keyId, RequestContext ctx, ApiKeyStore keyStore)
{
    var key = keyStore.GetKey(keyId);
    if (key is null || !CanManage(ctx, key.Stores))
        return Results.NotFound(new ErrorResponse($"Key '{keyId}' not found."));
    return Results.Ok(new KeySummaryDto(
        key.Id, key.UserId, key.Stores, key.Permissions,
        key.Label, key.CreatedAt, key.LastUsedAt, key.Revoked));
}

private static IResult RevokeKey(string keyId, RequestContext ctx, ApiKeyStore keyStore)
{
    var key = keyStore.GetKey(keyId);
    if (key is null || !CanManage(ctx, key.Stores))
        return Results.NotFound(new ErrorResponse($"Key '{keyId}' not found or already revoked."));
    bool revoked = keyStore.RevokeKey(keyId);
    if (!revoked)
        return Results.NotFound(new ErrorResponse($"Key '{keyId}' not found or already revoked."));
    return Results.Ok(new { message = $"Key '{keyId}' revoked." });
}
```

(Minimal-API model binding injects `RequestContext` from DI just like `CreateKey` already does.)

- [ ] **Step 5: Run it to verify it passes**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter KeyIsolationTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 6: Run the key-management + server suite (regression) and commit**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false`
Expected: PASS (existing `KeyManagementTests` use the admin key which accesses both stores, so its keys remain visible).

```bash
git add src/Scrinia.Server/Auth/RequestContext.cs src/Scrinia.Server/Endpoints/KeyEndpoints.cs tests/Scrinia.Server.Tests/KeyIsolationTests.cs
git commit -m "fix(security): scope key management to caller-accessible stores"
```

---

### Task B3: Chat requires read + search

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/ChatEndpoints.cs` (POST handler ~L20; leave `/providers` at chat-only)
- Test: `tests/Scrinia.Server.Tests/ChatEndpointTests.cs` (extend)

**Interfaces:**
- Consumes: `RequestContext.HasPermission`.

**Context:** the chat POST drives `AgentLoop` which runs `search_memory`/`recall_memory` unchecked. Because chat answers surface memory content, the endpoint must require `read` and `search` in addition to `chat`.

- [ ] **Step 1: Write the failing test** (add to `ChatEndpointTests`)

```csharp
[Fact]
public async Task Chat_WithoutReadAndSearch_Is403()
{
    var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();
    // chat-only key (no read/search) scoped to the primary store.
    var (rawKey, _, _) = keyStore.CreateKey("chat-only", [_factory.PrimaryStore], ["chat"], "chat-only");

    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);

    var req = new ChatRequest([new ChatMessage("user", "hello", null)], null); // adapt to real ChatRequest shape
    var resp = await client.PostAsJsonAsync($"/api/v1/stores/{_factory.PrimaryStore}/chat/", req);
    resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
}
```

(Match the real `ChatRequest`/`ChatMessage` constructor shape used elsewhere in `ChatEndpointTests`.)

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter ChatEndpointTests -p:NuGetAudit=false`
Expected: FAIL — a chat-only key currently reaches the stream (200) instead of 403.

- [ ] **Step 3: Require chat+read+search on the POST handler**

Replace the guard at the top of the POST handler:

```csharp
if (!ctx.HasPermission("chat") || !ctx.HasPermission("read") || !ctx.HasPermission("search"))
    return Results.Json(
        new ErrorResponse("Permissions 'chat', 'read', and 'search' are required."),
        statusCode: 403);
```

Leave the `/providers` GET guard as chat-only (it lists provider names, surfaces no memory).

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter ChatEndpointTests -p:NuGetAudit=false`
Expected: PASS (the existing chat tests use the full-permission admin key, so they stay green).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Endpoints/ChatEndpoints.cs tests/Scrinia.Server.Tests/ChatEndpointTests.cs
git commit -m "fix(security): require read+search for chat endpoint"
```

Note for changelog: existing chat-only API keys will stop working — they now also need `read` and `search`.

---

### Task B4: Enforce read-only store access on writes

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` (`StoreMemory`, `ForgetMemory`, `AppendMemory`, `CopyMemory`, `ImportBundle`)
- Test: `tests/Scrinia.Server.Tests/ReadOnlyAccessTests.cs`

**Interfaces:**
- Consumes: `RequestContext.GetStoreAccessLevel(string) -> "read-only" | "read-write"`.

**Context:** the `store_access` claim ("<store>:read-only") is populated but never enforced; write handlers check only `HasPermission`. API keys default to `read-write`, so this closes the SSO read-only path.

- [ ] **Step 1: Write the failing test**

Because `store_access` comes from an SSO claim (not settable on an API key through the factory), assert enforcement at the handler contract by simulating a read-only `RequestContext`. Two options — pick the one that matches the existing test style:

Option A (preferred if handlers are reachable via a thin wrapper or the DI `RequestContext` can be overridden in the factory): drive an HTTP write with a client whose `RequestContext.StoreAccessLevels` is `["test-store:read-only"]` and assert 403.

Option B (direct): add a focused test that constructs a `RequestContext { Stores = ["test-store"], Permissions = ["store"], StoreAccessLevels = ["test-store:read-only"] }` and asserts the shared guard helper (Step 3) returns the 403 result.

```csharp
using FluentAssertions;
using Scrinia.Server.Auth;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class ReadOnlyAccessTests
{
    [Fact]
    public void ReadOnlyStore_WriteGuard_Denies()
    {
        var ctx = new RequestContext
        {
            Stores = ["test-store"],
            Permissions = ["store"],
            StoreAccessLevels = ["test-store:read-only"],
        };
        MemoryEndpoints.IsWritable(ctx, "test-store").Should().BeFalse();
    }

    [Fact]
    public void ApiKeyDefault_IsWritable()
    {
        var ctx = new RequestContext { Stores = ["test-store"], Permissions = ["store"] };
        MemoryEndpoints.IsWritable(ctx, "test-store").Should().BeTrue(); // default read-write
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter ReadOnlyAccessTests -p:NuGetAudit=false`
Expected: FAIL — `MemoryEndpoints.IsWritable` does not exist.

- [ ] **Step 3: Add the shared guard and apply it to every write handler**

Add to `MemoryEndpoints`:

```csharp
/// <summary>True when the caller may write to <paramref name="store"/> — i.e. the store is not
/// marked read-only by an SSO store_access claim. API keys default to read-write.</summary>
internal static bool IsWritable(RequestContext ctx, string store) =>
    !string.Equals(ctx.GetStoreAccessLevel(store), "read-only", StringComparison.OrdinalIgnoreCase);
```

Then, in each of `StoreMemory`, `ForgetMemory`, `AppendMemory`, `CopyMemory`, and `ImportBundle`, immediately after the existing `HasPermission` guard, add:

```csharp
if (!IsWritable(ctx, store))
    return Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403);
```

For the handlers returning `Task<IResult>` synchronously (`CopyMemory`), wrap with `Task.FromResult<IResult>(...)` to match their existing return style.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter ReadOnlyAccessTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Run the server suite (regression) and commit**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false`
Expected: PASS (existing write tests use API keys → default read-write → unaffected).

```bash
git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs tests/Scrinia.Server.Tests/ReadOnlyAccessTests.cs
git commit -m "fix(security): enforce read-only store access on write endpoints"
```

---

### Task B5: Move rate limiting after authentication

**Files:**
- Modify: `src/Scrinia.Server/Program.cs` (~L382-386)
- Test: `tests/Scrinia.Server.Tests/RateLimitTests.cs` (extend)

**Interfaces:**
- No new API. Behavioral: the `"api"` rate-limit partition key resolves to the authenticated user id instead of always falling back to IP.

- [ ] **Step 1: Write the failing test** (add to `RateLimitTests`)

```csharp
// Two distinct users share the same loopback IP under the test server. If limiting
// partitions by user (correct), exhausting user A does not rate-limit user B.
[Fact]
public async Task RateLimit_PartitionsByUser_NotSharedIp()
{
    var keyStore = _factory.Services.GetRequiredService<ApiKeyStore>();
    var (aRaw, _, _) = keyStore.CreateKey("rl-user-a", [_factory.PrimaryStore], ["search"], "a");
    var (bRaw, _, _) = keyStore.CreateKey("rl-user-b", [_factory.PrimaryStore], ["search"], "b");

    // Read the "api" policy PermitLimit from Program.cs and set LIMIT to it.
    const int LIMIT = /* PermitLimit from the "api" policy in Program.cs */ 100;

    var a = _factory.CreateClient();
    a.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aRaw);
    HttpResponseMessage last = null!;
    for (int i = 0; i < LIMIT + 5; i++)
        last = await a.GetAsync($"/api/v1/stores/{_factory.PrimaryStore}/memories");
    last.StatusCode.Should().Be(HttpStatusCode.TooManyRequests); // user A is now limited

    var b = _factory.CreateClient();
    b.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bRaw);
    var bResp = await b.GetAsync($"/api/v1/stores/{_factory.PrimaryStore}/memories");
    bResp.StatusCode.Should().Be(HttpStatusCode.OK); // user B unaffected → per-user partition
}
```

Set `LIMIT` to the actual `PermitLimit` configured for the `"api"` policy in `Program.cs` (~L235). If the limit is large enough to make the loop slow, add a small dedicated low-limit policy for tests OR use `[Trait]` to mark it slow — match existing `RateLimitTests` conventions.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter RateLimitTests -p:NuGetAudit=false`
Expected: FAIL — today both users share the IP partition, so user B is limited too (429).

- [ ] **Step 3: Reorder the middleware**

In `Program.cs`, change the order so the limiter runs after authentication/authorization:

```csharp
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();
```

(Move `app.UseRateLimiter();` from its current position above `UseAuthentication()` to just below `UseAuthorization()`. The `RequestContext`-populating middleware that follows is unchanged; the partition key reads `context.User`, which `UseAuthentication` now populates first.)

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter RateLimitTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Run the full server suite and commit**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false`
Expected: PASS.

```bash
git add src/Scrinia.Server/Program.cs tests/Scrinia.Server.Tests/RateLimitTests.cs
git commit -m "fix(security): run rate limiter after authentication for per-user partitioning"
```

---

## Final verification

- [ ] **Run all three affected suites:**

```bash
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false
dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false
dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false
```

Expected: all green (allowing for the one pre-existing parallel-load flake `UpdateCorrectnessBenchmarkTests.UpdatedFact_OldContentNotReturned`, which passes in isolation and is unrelated).

- [ ] **Confirm the working tree contains only this plan's files** (`git status`) — the user's three uncommitted files must remain untouched and unstaged.
