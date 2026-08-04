# File-Entity Unification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `.scrinia/skills/` and `.scrinia/agent/` exactly one path authority in `PathRouter`, move `agent` back out of the memory store, and delete the dead `workflows` category and the vestigial `local:skill` NMP/2 read fallback.

**Architecture:** `PathRouter` gains a file-entity table (`skill`→`skills`, `agent`→`agent`) and becomes the sole resolver for both directories. Nine hardcoded call sites collapse onto it. `agent` is removed from `StoreLayoutMigration`'s sweep in the same commit that repoints its readers, so `.scrinia/memories/` holds only NMP/2 store content and can never contain a foreign `.meta.json`.

**Tech Stack:** .NET 10, C#, xunit 2.9.3, FluentAssertions 6.12.2.

**Spec:** `docs/superpowers/specs/2026-08-04-file-entity-unification-design.md` (commit `85b3eae`).

## Global Constraints

- This is a **code change only**. No data migration anywhere in this plan. Never add a move-back or fixup rule for `memories/agent/`.
- **Atomicity (Task 3):** removing the `agent` sweep from `StoreLayoutMigration` and repointing the four agent readers MUST land in a single commit. No intermediate commit may have one without the other.
- `.scrinia/memories/` holds NMP/2 store content and nothing else. No `.md` file entities, no foreign `.meta.json`.
- `.scrinia/skills/` and `.scrinia/agent/` keep their current on-disk locations. Nothing relocates user files.
- Build must stay at **0 errors**. `TreatWarningsAsErrors` is on for all non-test projects.
- Run tests with: `dotnet test tests\Scrinia.Tests\ -c Debug`
- Never run two `dotnet test` invocations against the same project concurrently, and never run `dotnet build` while a test run is in flight — both wedge on locked DLLs. Kill strays first: `Stop-Process -Name testhost,vstest.console -Force -ErrorAction SilentlyContinue`
- Prefix shell commands with `rtk` (e.g. `rtk git commit`).
- Preserve the agent NMP/2 **fall-through** on recall (`MemoryTools.Core.cs:48`) and the agent NMP/2 listing fallback (`MemoryTools.Lifecycle.cs:642-648`). Only the *skill* NMP/2 fallback is deleted by this plan.

---

## File Structure

| File | Responsibility | Task |
|------|----------------|------|
| `src/Scrinia.Core/FileEntityName.cs` | **Moved from `Scrinia.Mcp`.** Validates user-supplied skill/agent name slugs. Must be in Core so `PathRouter` can use it. | 1 |
| `src/Scrinia.Core/PathRouter.cs` | Single authority for both store scopes and file-entity directories. | 1, 4 |
| `src/Scrinia.Core/IMemoryStore.cs` | Gains `WorkspaceRoot`. | 1 |
| `src/Scrinia.Core/FileMemoryStore.cs` | Implements `WorkspaceRoot`; loses `IsEntityScope`. | 1, 5 |
| `src/Scrinia/HttpMemoryStore.cs` | Throws `NotSupportedException` for `WorkspaceRoot`. | 1 |
| `src/Scrinia.Mcp/MemoryTools.Skills.cs` | Skill write/list/load repointed; `GetScriniaBaseDir` and `ReadLegacySkillNmpAsync` deleted. | 2 |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | Skills listing and agent followUp listing repointed. | 2, 3 |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | Agent recall + remember repointed. | 3 |
| `src/Scrinia.Core/StoreLayoutMigration.cs` | Agent sweep removed; doc comments corrected. | 3, 5 |
| `src/Scrinia.Core/Bundles/BundleFormatService.cs` | `CategoryDir` special-case and local `GetScriniaBaseDir` deleted; `workflows` dropped. | 3, 4 |
| `src/Scrinia.Core/MemoryNaming.cs` | Loses `EntityTopics`. | 5 |

---

## Task 1: PathRouter file-entity authority

**Files:**
- Move: `src/Scrinia.Mcp/FileEntityName.cs` → `src/Scrinia.Core/FileEntityName.cs`
- Modify: `src/Scrinia.Core/PathRouter.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs:215` (after `GetStoreDirForScope`)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (near `GetStoreDirForScope`, line ~415)
- Modify: `src/Scrinia/HttpMemoryStore.cs:458`
- Test: `tests/Scrinia.Tests/PathRouterTests.cs`

**Interfaces:**
- Produces:
  - `PathRouter.IsFileEntity(string segment) -> bool`
  - `PathRouter.ToFileEntityDir(string kind, string workspaceRoot) -> string` (throws `ArgumentException` on unknown kind)
  - `PathRouter.TryToFileEntityPath(string kind, string name, string workspaceRoot, out string path) -> bool`
  - `IMemoryStore.WorkspaceRoot { get; }` -> `string`
  - `Scrinia.Core.FileEntityName.IsValid(string? name) -> bool` (now `public`, was `internal` in `Scrinia.Mcp`)

- [ ] **Step 1: Move `FileEntityName` into Core**

`PathRouter` lives in `Scrinia.Core`; `FileEntityName` is `internal` in `Scrinia.Mcp`. `Scrinia.Mcp` references `Scrinia.Core`, not the reverse, so the type must move. Delete `src/Scrinia.Mcp/FileEntityName.cs` and create `src/Scrinia.Core/FileEntityName.cs`:

```csharp
namespace Scrinia.Core;

/// <summary>
/// Validation for user-supplied skill/agent name slugs that become on-disk filenames.
/// Rejects anything that could escape the target directory or is not a plain slug.
/// <para>Lives in Core (not Mcp) because <see cref="PathRouter.TryToFileEntityPath"/> is the
/// single place that resolves a file-entity name to a path, and it enforces this check so no
/// caller can forget it.</para>
/// </summary>
public static class FileEntityName
{
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
```

Existing `Scrinia.Mcp` callers (`MemoryTools.Core.cs:36`, `MemoryTools.Skills.cs:386`) need no change — `Scrinia.Mcp` has `using Scrinia.Core;` and the type is now public.

- [ ] **Step 2: Write the failing tests**

Append to `tests/Scrinia.Tests/PathRouterTests.cs`:

```csharp
    // ── File entities ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("skill", "skills")]
    [InlineData("agent", "agent")]
    public void ToFileEntityDir_MapsKindToItsLiteralDirectory(string kind, string dirName)
    {
        PathRouter.ToFileEntityDir(kind, _root)
            .Should().Be(Path.Combine(_root, ".scrinia", dirName),
                because: "the singular segment name and the on-disk directory name differ for " +
                         "'skill'; this table is the only place that mapping exists");
    }

    [Fact]
    public void ToFileEntityDir_IsCaseInsensitive()
    {
        PathRouter.ToFileEntityDir("SKILL", _root)
            .Should().Be(Path.Combine(_root, ".scrinia", "skills"));
    }

    [Fact]
    public void ToFileEntityDir_UnknownKind_Throws()
    {
        var act = () => PathRouter.ToFileEntityDir("workflow", _root);
        act.Should().Throw<ArgumentException>(
            because: "workflows are deleted; an unknown kind is a programming error, " +
                     "matching ToStoreDir's contract");
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public void IsFileEntity_TrueForKnownKinds(string kind) =>
        PathRouter.IsFileEntity(kind).Should().BeTrue();

    [Theory]
    [InlineData("findings")]
    [InlineData("workflow")]
    [InlineData("")]
    public void IsFileEntity_FalseOtherwise(string segment) =>
        PathRouter.IsFileEntity(segment).Should().BeFalse();

    [Fact]
    public void TryToFileEntityPath_ValidName_ResolvesUnderTheKindDirectory()
    {
        PathRouter.TryToFileEntityPath("skill", "qa", _root, out string path)
            .Should().BeTrue();
        path.Should().Be(Path.Combine(_root, ".scrinia", "skills", "qa.md"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData("sub\\dir")]
    [InlineData("")]
    [InlineData("  ")]
    public void TryToFileEntityPath_RejectsUnsafeNames(string name)
    {
        PathRouter.TryToFileEntityPath("skill", name, _root, out string path)
            .Should().BeFalse(because: "the resolver enforces containment so no caller can forget it");
        path.Should().BeEmpty();
    }

    [Fact]
    public void ToFilesystemPath_SkillTopic_ResolvesToTopLevelSkillsDir()
    {
        var result = PathRouter.ToFilesystemPath(P("/skill/qa"), _root);
        result.Should().Be(Path.Combine(_root, ".scrinia", "skills", "qa.md"),
            because: "hand-authored file entities live at the top level of .scrinia/, " +
                     "never inside the memory store");
    }

    [Fact]
    public void ToFilesystemPath_AgentTopic_ResolvesToTopLevelAgentDir()
    {
        var result = PathRouter.ToFilesystemPath(P("/agent/profile"), _root);
        result.Should().Be(Path.Combine(_root, ".scrinia", "agent", "profile.md"));
    }

    [Fact]
    public void ToFilesystemPath_FileEntityWithExtraSegments_ReturnsNull()
    {
        PathRouter.ToFilesystemPath(P("/skill/a/b"), _root)
            .Should().BeNull(because: "file entities are flat; a nested path has no location");
    }
```

Replace the two existing tests that assert the old behaviour. Delete `ToFilesystemPath_WorkflowTopic_ReturnsJson` outright (workflows are gone) and delete the old `ToFilesystemPath_SkillTopic_ReturnsMd` and `ToFilesystemPath_AgentTopic_ReturnsMd`, which assert the `memories/` location the new tests above replace.

- [ ] **Step 3: Run the tests to verify they fail**

```
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~PathRouterTests"
```
Expected: compile error — `ToFileEntityDir`, `IsFileEntity`, `TryToFileEntityPath` do not exist.

- [ ] **Step 4: Implement the file-entity authority in `PathRouter`**

Add to `src/Scrinia.Core/PathRouter.cs`, after the `MemoriesDir` constant:

```csharp
    /// <summary>
    /// Path segment name -> on-disk directory name for hand-authored file entities.
    /// <para>The singular/plural mismatch ("skill" -> "skills", "agent" -> "agent") is historical.
    /// It was previously implicit in five separate call sites; this table is the only place it
    /// exists.</para>
    /// <para>These directories sit at the top level of <c>.scrinia/</c>, deliberately NOT under
    /// <c>memories/</c>: their <c>.meta.json</c> sidecars share a filename namespace with the
    /// store's own <c>ArtifactEntry</c> sidecars, so a file entity inside a store scope directory
    /// would be destroyed by <see cref="FileMemoryStore.SaveIndex"/>'s orphan sweep.</para>
    /// </summary>
    private static readonly Dictionary<string, string> FileEntityDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skill"] = "skills",
        ["agent"] = "agent",
    };

    /// <summary>True when <paramref name="segment"/> names a hand-authored file-entity kind.</summary>
    public static bool IsFileEntity(string? segment) =>
        !string.IsNullOrEmpty(segment) && FileEntityDirs.ContainsKey(segment);

    /// <summary>
    /// Resolves a file-entity kind to its directory. The single authority for where skills and
    /// agent configs live on disk.
    /// </summary>
    /// <exception cref="ArgumentException">The kind is not a known file entity. Callers pass a
    /// literal, never user input — an unknown kind is a programming error, matching
    /// <see cref="ToStoreDir"/>'s contract.</exception>
    public static string ToFileEntityDir(string kind, string workspaceRoot)
    {
        if (!FileEntityDirs.TryGetValue(kind, out string? dirName))
            throw new ArgumentException($"Unknown file-entity kind: {kind}", nameof(kind));
        return Path.Combine(workspaceRoot, ".scrinia", dirName);
    }

    /// <summary>
    /// Resolves a file entity's full path, enforcing both name validity and directory containment.
    /// Returns false for a name that is invalid or would escape — callers surface their own error.
    /// <para>Validation lives here so it cannot be forgotten: it was previously duplicated at two
    /// call sites and absent from the rest.</para>
    /// </summary>
    public static bool TryToFileEntityPath(string kind, string? name, string workspaceRoot, out string path)
    {
        path = "";
        if (!FileEntityName.IsValid(name))
            return false;

        string dir = ToFileEntityDir(kind, workspaceRoot);
        string candidate = Path.Combine(dir, name + ".md");
        if (!PathContainment.IsWithin(dir, candidate))
            return false;

        path = candidate;
        return true;
    }
```

Then rewrite `ToFilesystemPath`'s body (currently lines 21-47) so file entities route to the top-level directories rather than under `memories/`:

```csharp
    public static string? ToFilesystemPath(ParsedPath path, string workspaceRoot)
    {
        if (IsEphemeral(path)) return null;

        var segments = path.Segments;
        if (segments.Count == 0) return null;

        // Hand-authored file entities are flat and live outside the store.
        if (IsFileEntity(segments[0].Value))
        {
            if (segments.Count != 2) return null;
            return TryToFileEntityPath(segments[0].Value, segments[1].Value, workspaceRoot, out string fePath)
                ? fePath
                : null;
        }

        // Everything else is an NMP/2 memory under .scrinia/memories/.
        string dir = Path.Combine(workspaceRoot, ".scrinia", MemoriesDir);
        for (int i = 0; i < segments.Count - 1; i++)
            dir = Path.Combine(dir, segments[i].Value);

        return Path.Combine(dir, segments[^1].Value + ".nmp2");
    }
```

Note the `"workflow" => ".json"` arm is gone with the rest of the extension switch — file entities now get `.md` from `TryToFileEntityPath`, and everything else is `.nmp2`.

- [ ] **Step 5: Add `WorkspaceRoot` to the store interface**

In `src/Scrinia.Core/IMemoryStore.cs`, after `string GetStoreDirForScope(string scope);` (line 215):

```csharp
    /// <summary>
    /// Absolute path to the workspace root — the directory that contains <c>.scrinia/</c>.
    /// Needed to resolve hand-authored file entities via <see cref="PathRouter.ToFileEntityDir"/>.
    /// <para>Callers previously reverse-engineered this from <c>GetStoreDirForScope("local")</c>
    /// using two different algorithms that agreed only by coincidence.</para>
    /// </summary>
    /// <exception cref="NotSupportedException">The store is remote. File entities are local-only.</exception>
    string WorkspaceRoot { get; }
```

In `src/Scrinia.Core/FileMemoryStore.cs`, next to `GetStoreDirForScope` (~line 415):

```csharp
    public string WorkspaceRoot => _workspaceRoot;
```

In `src/Scrinia/HttpMemoryStore.cs`, next to `GetStoreDirForScope` (line 458):

```csharp
    public string WorkspaceRoot =>
        throw new NotSupportedException(
            "WorkspaceRoot is not supported in remote mode. Skills and agent configs are local-only.");
```

- [ ] **Step 6: Run the tests to verify they pass**

```
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~PathRouterTests"
```
Expected: PASS.

- [ ] **Step 7: Build the whole solution**

```
rtk dotnet build Scrinia.sln -c Debug
```
Expected: 0 errors. Any test double implementing `IMemoryStore` (e.g. `tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs:249`'s stub) needs `public string WorkspaceRoot => throw new NotSupportedException();` added — fix each one the compiler names.

- [ ] **Step 8: Commit**

```
rtk git add src/Scrinia.Core/FileEntityName.cs src/Scrinia.Core/PathRouter.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Tests/PathRouterTests.cs
rtk git rm src/Scrinia.Mcp/FileEntityName.cs
rtk git commit -m "feat(core): make PathRouter the single authority for file-entity paths"
```

---

## Task 2: Repoint the skills readers and delete the NMP/2 fallback

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs` (lines 85-92, 132-139, 219-221, 249-252, 273-274, 391-393, 406-408, 447)
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:605-606`
- Test: `tests/Scrinia.Tests/SkillPathAuthorityTests.cs` (create)

**Interfaces:**
- Consumes: `PathRouter.ToFileEntityDir`, `PathRouter.TryToFileEntityPath`, `IMemoryStore.WorkspaceRoot` (Task 1)

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/SkillPathAuthorityTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Skills resolve through PathRouter and nowhere else, and the legacy NMP/2 skill fallback
/// is gone. Before this change the location ".scrinia/skills/" was hardcoded at five sites
/// behind two divergent GetScriniaBaseDir implementations.
/// </summary>
public class SkillPathAuthorityTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task SkillWrite_LandsInThePathRouterDirectory()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        await Tools().MemoryRemember(path: "/skill/api-reviewer", content: ["review the API"]);

        string expected = Path.Combine(
            PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot), "api-reviewer.md");
        File.Exists(expected).Should().BeTrue(
            because: "the writer must resolve through the single authority");
        (await File.ReadAllTextAsync(expected)).Should().Contain("review the API");
    }

    [Fact]
    public async Task SkillWrite_StaysOutOfTheMemoryStore()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        await Tools().MemoryRemember(path: "/skill/api-reviewer", content: ["review the API"]);

        string memories = store.GetStoreDirForScope("local");
        Directory.Exists(Path.Combine(memories, "skill")).Should().BeFalse(
            because: "hand-authored file entities never enter a store scope directory");
    }

    [Fact]
    public async Task SkillLoad_LegacyNmp2Entry_IsNotFound()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        // Seed an NMP/2 entry under the legacy local:skill scope. Nothing may read it back:
        // the fallback is deleted, and such entries only exist in unreleased dev builds.
        string legacyScope = MemoryNaming.BuildScopedTopicScope("skill");
        await store.WriteArtifactAsync(
            "ghost", legacyScope, Scrinia.Core.Encoding.Nmp2ChunkedEncoder.Encode("legacy body"),
            CancellationToken.None);

        string result = await Tools().MemoryRecall(path: "/skill/ghost");

        result.Should().NotContain("legacy body",
            because: "the local:skill NMP/2 read fallback is deleted");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~SkillPathAuthorityTests"
```
Expected: `SkillLoad_LegacyNmp2Entry_IsNotFound` FAILS (the fallback still returns the body). The first two may already pass, since the location is unchanged — they are regression guards, and they must keep passing after the refactor.

- [ ] **Step 3: Delete `GetScriniaBaseDir` and `ReadLegacySkillNmpAsync`**

In `src/Scrinia.Mcp/MemoryTools.Skills.cs`, delete the whole `GetScriniaBaseDir` method (lines 85-92) and the whole `ReadLegacySkillNmpAsync` method (lines 132-139).

- [ ] **Step 4: Repoint the three skills sites**

Skill write (was line 218-220):

```csharp
        string skillsDir = PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot);
        if (!PathRouter.TryToFileEntityPath("skill", name, store.WorkspaceRoot, out string filePath))
            return ResponseBuilder.Error(
                $"Invalid skill name '{name}'.",
                ErrorCodes.InvalidParameter,
                "remember({ path: '/skill/my-helper', content: [...] })").ToYaml();
        FileMemoryStore.EnsureGitIgnore(store.WorkspaceRoot);
        Directory.CreateDirectory(skillsDir);
```

Note `EnsureGitIgnore` now takes the workspace root directly. It previously received `Path.GetDirectoryName(baseDir)!` where `baseDir` was `.scrinia` — the same value, expressed without the indirection.

Skill list (was line 273-274):

```csharp
            string skillsDir = PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot);
```

Skill load (was lines 391-396):

```csharp
        if (!PathRouter.TryToFileEntityPath("skill", skillName, store.WorkspaceRoot, out string filePath))
            return ResponseBuilder.Error(
                $"Invalid skill name '{skillName}'.", ErrorCodes.NotFound,
                "recall({ path: '/skill/' }) to list available skills").ToYaml();
```

The separate `FileEntityName.IsValid` guard at line 386 and the `PathContainment.IsWithin` check at line 393 are both now enforced inside `TryToFileEntityPath` — delete them.

- [ ] **Step 5: Delete the two legacy-fallback call sites**

At the old line 249, delete the `try { await ReadLegacySkillNmpAsync(...); migrationNote = ...; } catch { }` block and the `migrationNote` variable, plus the `if (migrationNote.Length > 0) response = response.WithInfo(migrationNote);` that consumes it.

At the old line 403-408, delete the `nmpContent` fallback block so a missing disk file is simply not found:

```csharp
        string? diskContent = null;
        if (File.Exists(filePath))
            diskContent = await File.ReadAllTextAsync(filePath, ct);
```

Then delete the now-unreachable `nmpContent` branches further down, including the `MemoryNaming.BuildScopedTopicScope("skill")` use at line 447. Follow the compiler.

- [ ] **Step 6: Repoint the skills listing in the primer**

In `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, replace lines 605-606:

```csharp
            string skillsDir = PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot);
```

- [ ] **Step 7: Run the tests**

```
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~SkillPathAuthorityTests|FullyQualifiedName~ScriniaMcpToolsTests"
```
Expected: PASS, including the pre-existing `MemoryRecall_Skill_WithBuiltin_ReturnsBothBuiltInAndOverride` and `MemoryRemember_SkillPath_ProducesGitIgnore`, which pin the unchanged on-disk location.

- [ ] **Step 8: Commit**

```
rtk git add src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs tests/Scrinia.Tests/SkillPathAuthorityTests.cs
rtk git commit -m "refactor(mcp): route skills through PathRouter, drop the local:skill NMP/2 fallback"
```

---

## Task 3: Move agent back out of the memory store — ATOMIC

> **This task's changes MUST land in one commit.** Removing the migration sweep without repointing the readers (or vice versa) silently orphans live agent configs. Do not split steps 3-6 across commits.

**Files:**
- Modify: `src/Scrinia.Core/StoreLayoutMigration.cs:76` and `:398-412`, plus the class doc comment (~lines 31-50)
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:38` and `:275-283`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:625`
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs:277-291, 302, 355`
- Test: `tests/Scrinia.Tests/AgentFileEntityTests.cs` (create)

**Interfaces:**
- Consumes: `PathRouter.ToFileEntityDir`, `PathRouter.TryToFileEntityPath`, `IMemoryStore.WorkspaceRoot` (Task 1)

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/AgentFileEntityTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Agent configs live at .scrinia/agent/, outside the memory store.
///
/// <para>The store-unification work moved them into .scrinia/memories/agent/, which put
/// AgentFileMeta sidecars in a directory FileMemoryStore enumerates. Because
/// WriteSidecarMeta names "profile.md"'s sidecar "profile.meta.json" — the same filename
/// the store writes for an NMP/2 entry named "profile" — SaveIndex's orphan sweep deleted
/// them. Moving agent back out makes that impossible rather than merely unlikely.</para>
/// </summary>
public class AgentFileEntityTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task AgentWrite_LandsAtTopLevelAgentDir_NotInTheStore()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"]);

        string expected = Path.Combine(
            PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot), "profile.md");
        File.Exists(expected).Should().BeTrue();

        Directory.Exists(Path.Combine(store.GetStoreDirForScope("local"), "agent"))
            .Should().BeFalse(because: "the store holds NMP/2 content only");
    }

    [Fact]
    public async Task AgentRecall_ReadsFromTopLevelAgentDir()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"]);
        string result = await Tools().MemoryRecall(path: "/agent/profile");

        result.Should().Contain("you are helpful");
    }

    [Fact]
    public void Migration_DoesNotSweepTheAgentDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scrinia_agentsweep_{Guid.NewGuid():N}");
        string agentDir = Path.Combine(root, ".scrinia", "agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "profile.md"), "you are helpful");
        // A legacy root so the migration actually has work to do and runs to completion.
        string topics = Path.Combine(root, ".scrinia", "topics", "findings");
        Directory.CreateDirectory(topics);
        File.WriteAllText(Path.Combine(topics, "note.nmp2"), "NMP/2\n\nbody");

        try
        {
            StoreLayoutMigration.Migrate(root, dryRun: false, backup: false);

            File.Exists(Path.Combine(agentDir, "profile.md")).Should().BeTrue(
                because: "agent is a hand-authored file entity and is never swept");
            Directory.Exists(Path.Combine(root, ".scrinia", "memories", "agent"))
                .Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BundleImportOfAnAgentTopic_DoesNotDeleteAgentConfigMetadata()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"]);
        string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        string sidecar = Path.Combine(agentDir, "profile.meta.json");
        File.Exists(sidecar).Should().BeTrue("precondition: the agent write produced a sidecar");

        // SaveIndex's orphan sweep deletes every *.meta.json it does not recognise in the
        // scope directory. Before this change agent configs lived there and were destroyed.
        store.SaveIndex(
            [new ArtifactEntry("unrelated", "file:///x.nmp2", 10, 1, DateTimeOffset.UtcNow)],
            "local:agent");

        File.Exists(sidecar).Should().BeTrue(
            because: "agent metadata lives outside every store scope directory");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~AgentFileEntityTests"
```
Expected: all four FAIL — writes still land in `memories/agent/`, the migration still sweeps, and the sidecar is still deleted.

- [ ] **Step 3: Remove the agent sweep from the migration**

In `src/Scrinia.Core/StoreLayoutMigration.cs`, line 76:

```csharp
    private static readonly string[] LegacyRoots = ["store", "topics"];
```

Delete the entire step-3 block (lines 398-412), from the `// 3. .scrinia/agent/* -> ...` comment through its closing brace. Then replace the note at lines 414-418 so it covers both file entities:

```csharp
        // NOTE: no .scrinia/skills/* or .scrinia/agent/* sweep. Both hold live, hand-authored
        // Markdown that PathRouter.ToFileEntityDir resolves at the top level of .scrinia/.
        // They deliberately stay OUT of memories/: their .meta.json sidecars share a filename
        // namespace with the store's own ArtifactEntry sidecars, so a file entity inside a scope
        // directory is deleted by SaveIndex's orphan sweep. topics/entity/skill/ (FileMemoryStore's
        // own NMP2 layout) still migrates via step 2 — unrelated to the top-level directories.
```

- [ ] **Step 4: Correct the class doc comment**

In the same file, replace the paragraphs at roughly lines 31-50 (the "`agent` is swept; `skills` deliberately is not" block and the Task 8 paragraph that follows it) with:

```csharp
/// <para><b>Neither <c>agent</c> nor <c>skills</c> is swept.</b> Both top-level directories hold
/// live, hand-authored Markdown resolved by <see cref="PathRouter.ToFileEntityDir"/>. An earlier
/// revision swept <c>agent</c> into <c>memories/agent/</c>; that put <c>AgentFileMeta</c> sidecars
/// in a directory <see cref="FileMemoryStore"/> enumerates, where
/// <see cref="FileMemoryStore.SaveIndex"/>'s orphan sweep deleted them. Keeping file entities out
/// of every scope directory makes that impossible by construction.</para>
```

- [ ] **Step 5: Repoint the three agent readers**

`src/Scrinia.Mcp/MemoryTools.Core.cs`, recall (lines 35-40) — note `TryToFileEntityPath` subsumes both the `FileEntityName.IsValid` and `PathContainment.IsWithin` checks:

```csharp
                string agentSubject = artifactOrName["/agent/".Length..].Trim();
                if (PathRouter.TryToFileEntityPath("agent", agentSubject, agentStore.WorkspaceRoot, out string agentFilePath)
                    && File.Exists(agentFilePath))
                {
                    string mdContent = await File.ReadAllTextAsync(agentFilePath, cancellationToken);
                    SessionBudget.RecordAccess(artifactOrName, mdContent.Length);
                    return ResponseBuilder.Success(mdContent).WithAction(actionLabel).ToYaml();
                }
```

Keep the `// Fall through to NMP/2 resolution for legacy entries` comment and behaviour below it — only the *skill* fallback is deleted by this plan.

`src/Scrinia.Mcp/MemoryTools.Core.cs`, remember (lines 275-283):

```csharp
            string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
            if (!PathRouter.TryToFileEntityPath("agent", agentSubject, store.WorkspaceRoot, out string filePath))
                return ResponseBuilder.Error(
                    "Invalid agent config name.", ErrorCodes.InvalidParameter,
                    "Use a path like '/agent/profile' for the agent config.").ToYaml();
            FileMemoryStore.EnsureGitIgnore(store.WorkspaceRoot);
            Directory.CreateDirectory(agentDir);
```

`src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, line 625:

```csharp
            string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
```

Leave the `if (!usedMdFiles)` NMP/2 listing fallback below it intact.

- [ ] **Step 6: Collapse `CategoryDir` in the bundle service**

In `src/Scrinia.Core/Bundles/BundleFormatService.cs`, delete both `GetScriniaBaseDir` (lines 277-278) and `CategoryDir` (lines 288-291). Both categories now resolve identically.

Note the bundle manifest keys categories in the **plural** (`"skills"`) while `PathRouter` keys on the **singular** (`"skill"`). The manifest key is on-the-wire format in already-distributed bundles, so it stays as-is and a map bridges the two. `"workflows"` is deliberately absent from the map — it is still in `FileEntityCategories` until Task 4, so `CategoryDir` must return `null` for it rather than throw:

```csharp
    /// <summary>
    /// Bundle manifest category key -> PathRouter file-entity kind. The manifest has used the
    /// plural "skills" since v2 bundles shipped; changing it would break existing bundles.
    /// </summary>
    private static readonly Dictionary<string, string> CategoryToKind = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skills"] = "skill",
        ["agent"] = "agent",
    };

    /// <summary>
    /// Resolves a file-entity category's directory, or null when the category has no
    /// file-entity kind. Every kind resolves the same way now — the agent special-case is
    /// gone because agent no longer lives inside the memory store.
    /// </summary>
    private static string? CategoryDir(IMemoryStore store, string category) =>
        CategoryToKind.TryGetValue(category, out string? kind)
            ? PathRouter.ToFileEntityDir(kind, store.WorkspaceRoot)
            : null;
```

Update the two callers. `ExportFileEntities` — delete the `baseDir` local (line 302) and replace line 306:

```csharp
            string? categoryDir = CategoryDir(store, category);
            if (categoryDir is null || !Directory.Exists(categoryDir))
                continue;
```

`ImportFileEntities` — delete the `baseDir` local (line 355). The containment check at lines 366-369 compared `categoryDir` against `baseDir`; `CategoryDir` now only ever returns a known literal directory or null, so that check is replaced by the null guard:

```csharp
            string? categoryDir = CategoryDir(store, category);
            if (categoryDir is null)
                continue; // no file-entity kind for this manifest key
            Directory.CreateDirectory(categoryDir);
```

Leave the `FileEntityCategories.Contains(category, ...)` allowlist check at line 360 in place — Task 4 replaces it.

- [ ] **Step 7: Run the tests**

```
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~AgentFileEntityTests|FullyQualifiedName~StoreLayoutMigrationTests|FullyQualifiedName~BundleV2Tests"
```
Expected: `AgentFileEntityTests` PASS. `StoreLayoutMigrationTests` and `BundleV2Tests` will have cases asserting the old `memories/agent/` location — update each to the top-level `.scrinia/agent/` path. `BundleV2Tests.cs:212` and `:309` carry comments describing agent's store routing; correct them too.

- [ ] **Step 8: Commit — everything from steps 3-6 together**

```
rtk git add src/Scrinia.Core/StoreLayoutMigration.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs src/Scrinia.Core/Bundles/BundleFormatService.cs tests/Scrinia.Tests/
rtk git commit -m "fix(core): move agent configs back out of the memory store"
```

---

## Task 4: Delete the workflows category

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs:35, 85, 361`
- Modify: `tests/Scrinia.Tests/BundleV2Tests.cs` (lines 126, 138, 154, 182, 194, 216, 221, 273, 291, 309, 313)
- Modify: `tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs:13`

**Interfaces:**
- Consumes: `PathRouter.IsFileEntity` (Task 1), `CategoryDir(IMemoryStore, string)` (Task 3)

- [ ] **Step 1: Write the failing test**

Append to `tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs`:

```csharp
    [Fact]
    public void ImportingABundleWithWorkflows_SucceedsAndReportsTheSkip()
    {
        // Workflows are deleted: nothing ever wrote one, so the category could only be
        // populated by importing a bundle. Old bundles must still import cleanly, and the
        // user must be told what was dropped rather than silently losing it.
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        string bundlePath = Path.Combine(scope.WorkspaceDir, "legacy.scrinia-bundle");
        using (var zip = System.IO.Compression.ZipFile.Open(
            bundlePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipText(zip, "manifest.json", """
                {"version":2,"topics":{},"fileEntities":{"workflows":["ci.json"]}}
                """);
            WriteZipText(zip, "files/workflows/ci.json", """{"steps":["build"]}""");
        }

        var result = BundleFormatService.Import(bundlePath, store, overwrite: false);

        result.SkippedCategories.Should().Contain("workflows");
        Directory.Exists(Path.Combine(store.WorkspaceRoot, ".scrinia", "workflows"))
            .Should().BeFalse(because: "the category is deleted, not merely unwritten");
    }
```

If `BundleImportCategoryAllowlistTests` has no `WriteZipText` helper, copy the one from `tests/Scrinia.Tests/BundleV2Tests.cs`.

- [ ] **Step 2: Run the test to verify it fails**

```
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~BundleImportCategoryAllowlistTests"
```
Expected: compile error — `SkippedCategories` does not exist on the import result.

- [ ] **Step 3: Drop workflows from the category list**

`src/Scrinia.Core/Bundles/BundleFormatService.cs:35`:

```csharp
    private static readonly string[] FileEntityCategories = ["skills", "agent"];
```

Update the comment at line 85 to `// Export disk-based file entities (skills, agent)`.

`CategoryToKind` already exists from Task 3 and already omits `"workflows"` — do not redeclare it.

- [ ] **Step 4: Report skipped categories**

Replace the import allowlist check (the `FileEntityCategories.Contains(...)` guard Task 3 left in place, around line 360) so an unrecognised category is recorded rather than silently dropped:

```csharp
            if (!CategoryToKind.ContainsKey(category))
            {
                skippedCategories.Add(category);
                continue; // deleted or unknown category — reported, never written
            }
```

`CategoryToKind` is now the single allowlist; the separate `FileEntityCategories.Contains` check is redundant and goes away with it.

Declare `var skippedCategories = new List<string>();` at the top of `ImportFileEntities`, return it, and surface it on the import result type as `SkippedCategories`. Follow the existing result record's shape — add the property with a default of `[]` so other call sites keep compiling.

- [ ] **Step 5: Delete the workflows test coverage**

In `tests/Scrinia.Tests/BundleV2Tests.cs`, remove every workflows assertion and fixture: lines 126, 138, 154, 182, 194, 216, 221, 273, 291, 313, and correct the comments at 81, 212 and 309 that describe workflows keeping the flat layout.

- [ ] **Step 6: Run the tests**

```
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~Bundle"
```
Expected: PASS.

- [ ] **Step 7: Commit**

```
rtk git add src/Scrinia.Core/Bundles/BundleFormatService.cs tests/Scrinia.Tests/
rtk git commit -m "feat(bundles): delete the dead workflows category, report skipped ones on import"
```

---

## Task 5: Delete `EntityTopics` and `IsEntityScope`

**Files:**
- Modify: `src/Scrinia.Core/MemoryNaming.cs:10-26`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:268, 523-531, 534-555`
- Modify: `src/Scrinia.Core/StoreLayoutMigration.cs:440, 618, 632` (doc comments only)
- Modify: `tests/Scrinia.Tests/MemoryNamingTests.cs` (any `EntityTopics` cases)
- Test: `tests/Scrinia.Tests/FileMemoryStoreTests.cs`

**Interfaces:** none produced.

- [ ] **Step 1: Write the failing test**

Append to `tests/Scrinia.Tests/FileMemoryStoreTests.cs`:

```csharp
    [Fact]
    public void DefaultScopes_IncludeEveryDiscoveredTopic()
    {
        // IsEntityScope excluded "skill" (and any local:entity/* scope) from unfiltered
        // searches. Both branches are dead under v3: the migration leaves no entity/
        // directories, and skills are file entities rather than store entries. A
        // memories/skill/ topic is only reachable from an unreleased dev build's
        // migration, so including it costs nothing and removes a half-dead predicate.
        _store.Upsert(new ArtifactEntry("a", "file:///a.nmp2", 10, 1, DateTimeOffset.UtcNow), "local:skill");
        _store.Upsert(new ArtifactEntry("b", "file:///b.nmp2", 10, 1, DateTimeOffset.UtcNow), "local:findings");

        IReadOnlyList<string> scopes = _store.ResolveReadScopes(null);

        scopes.Should().Contain("local:skill");
        scopes.Should().Contain("local:findings");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~DefaultScopes_IncludeEveryDiscoveredTopic"
```
Expected: FAIL — `local:skill` is excluded.

- [ ] **Step 3: Simplify the default-scope loop**

`src/Scrinia.Core/FileMemoryStore.cs`, replacing lines 523-531:

```csharp
        // Default (null): every discovered topic. There is no longer an "entity-classified"
        // category to exclude — see the commit that deleted IsEntityScope.
        var ordered = new List<string> { "local" };
        ordered.AddRange(DiscoverTopics());
        return ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
```

- [ ] **Step 4: Delete the predicate and the set**

Delete `IsEntityScope` entirely (lines 534-555 including its doc comment) and `MemoryNaming.EntityTopics` (lines 10-26).

At `FileMemoryStore.cs:268`, `PathParser.Parse` still requires an `entityTypes` argument; pass an empty set:

```csharp
            var parsed = PathParser.Parse(name, System.Collections.Frozen.FrozenSet<string>.Empty);
```

If the parameter's declared type is `IReadOnlySet<string>`, use `new HashSet<string>()` instead — match the signature the compiler reports. The classification is inert here: only `PathSegment.Value` feeds the result, never `IsEntityType`.

- [ ] **Step 5: Reword the three dangling doc comments**

`StoreLayoutMigration.cs` lines 440, 618 and 632 name `MemoryNaming.EntityTopics` and `FileMemoryStore.IsEntityScope` as explanatory references. They are `<c>` tags so the build will not break, but they must not point at removed symbols. Replace each mention with a self-contained phrase, e.g. at line 440:

```csharp
    /// the same target (e.g. <c>entity/skill/x</c> and <c>memory/skill/x</c>, realistic because
    /// "skill" was historically classified as an entity topic) would pick an
```

and at lines 618/632 replace "`FileMemoryStore.IsEntityScope`'s own comment names" with "historical entity topics included".

- [ ] **Step 6: Run the full suite**

```
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests\Scrinia.Tests\ -c Debug
```
Expected: PASS. Delete any `MemoryNamingTests` case asserting `EntityTopics` membership.

- [ ] **Step 7: Commit**

```
rtk git add src/Scrinia.Core/ tests/Scrinia.Tests/
rtk git commit -m "refactor(core): delete the dead IsEntityScope predicate and EntityTopics"
```

---

## Task 6: Cross-cutting invariant tests

**Files:**
- Test: `tests/Scrinia.Tests/StorePurityInvariantTests.cs` (create)

**Interfaces:**
- Consumes: everything from Tasks 1-5.

- [ ] **Step 1: Write the tests**

Create `tests/Scrinia.Tests/StorePurityInvariantTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// The structural guarantee this workstream exists to establish: .scrinia/memories/ holds
/// NMP/2 store content and nothing else, so a store scope directory can never contain a
/// foreign .meta.json for SaveIndex's orphan sweep to delete.
/// </summary>
public class StorePurityInvariantTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task MemoriesDirectory_NeverContainsFileEntities()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        await Tools().MemoryRemember(path: "/skill/qa", content: ["skill body"]);
        await Tools().MemoryRemember(path: "/agent/profile", content: ["agent body"]);
        await Tools().MemoryRemember(path: "/findings/note", content: ["ordinary memory"]);

        string memories = store.GetStoreDirForScope("local");
        Directory.EnumerateFiles(memories, "*.md", SearchOption.AllDirectories)
            .Should().BeEmpty(because: "hand-authored Markdown never enters the store");
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public void FileEntityDirectories_SitOutsideTheStore(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        string dir = PathRouter.ToFileEntityDir(kind, store.WorkspaceRoot);
        string memories = store.GetStoreDirForScope("local");

        PathContainment.IsWithin(memories, dir).Should().BeFalse(
            because: $"{kind} is a hand-authored file entity, not store content");
    }

    [Fact]
    public async Task EveryReaderResolvesToThePathRouterDirectory()
    {
        // The DRY guarantee made executable: ".scrinia/skills/" was previously named at five
        // sites behind two divergent GetScriniaBaseDir implementations that agreed only by
        // coincidence. Writing through the MCP surface and finding the file where PathRouter
        // says it should be proves writer and authority agree; the listing proves the reader does.
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        await Tools().MemoryRemember(path: "/skill/qa", content: ["skill body"]);

        string authoritative = Path.Combine(
            PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot), "qa.md");
        File.Exists(authoritative).Should().BeTrue();

        string listing = await Tools().MemoryRecall(path: "/skill/");
        listing.Should().Contain("qa");

        string loaded = await Tools().MemoryRecall(path: "/skill/qa");
        loaded.Should().Contain("skill body");
    }
}
```

- [ ] **Step 2: Run them**

```
rtk dotnet test tests\Scrinia.Tests\ -c Debug --filter "FullyQualifiedName~StorePurityInvariantTests"
```
Expected: PASS. If `MemoryRecall(path: "/skill/")` is not the listing form, use whatever `SkillLoad` with no name is exposed as — check `MemoryTools.Skills.cs:264`.

- [ ] **Step 3: Run the full suite twice**

```
Stop-Process -Name testhost,vstest.console -Force -ErrorAction SilentlyContinue
rtk dotnet test tests\Scrinia.Tests\ -c Debug
rtk dotnet test tests\Scrinia.Server.Tests\ -c Debug
```
Expected: 0 failures in both. Run `Scrinia.Tests` a second time — this suite has a documented history of intermittent failures, and a single green run is weaker evidence than it looks.

- [ ] **Step 4: Update the CHANGELOG**

Add under `## [Unreleased]`:

```markdown
### Changed — file-entity unification

- `.scrinia/skills/` and `.scrinia/agent/` now resolve through a single authority
  (`PathRouter.ToFileEntityDir`). The location was previously hardcoded at nine call sites
  behind two divergent `GetScriniaBaseDir` implementations that agreed only by coincidence.
- Agent configs move back out of `.scrinia/memories/agent/` to `.scrinia/agent/`. Keeping
  hand-authored Markdown inside a store scope directory put `AgentFileMeta` sidecars in a
  filename namespace shared with the store's own `ArtifactEntry` sidecars, where
  `SaveIndex`'s orphan sweep deleted them — reachable by importing a bundle containing a
  memory topic named `agent`. The v3 migration never shipped, so no released workspace is
  affected and no data migration is required.
- `.scrinia/workflows/` is deleted. Nothing ever wrote a workflow; the category could only
  be populated by importing a bundle. Bundles containing one still import cleanly and now
  report the skipped category rather than dropping it silently.
- The legacy `local:skill` NMP/2 read fallback is deleted. `v0.4.0` contains no skill code
  at all, so such entries exist only in unreleased dev builds.
- `IMemoryStore` gains `WorkspaceRoot`. Remote stores throw `NotSupportedException`: skills
  and agent configs are local-only, which was previously accidental rather than stated.
```

- [ ] **Step 5: Commit**

```
rtk git add tests/Scrinia.Tests/StorePurityInvariantTests.cs CHANGELOG.md
rtk git commit -m "test(core): pin the store-purity invariant and the single path authority"
```

---

## Self-Review

**1. Spec coverage**

| Spec requirement | Task |
|---|---|
| §2 `FileEntityDirs` table, `IsFileEntity`, `ToFileEntityDir`, `TryToFileEntityPath` | 1 |
| §2 `ToFilesystemPath` corrected, `workflow` arm deleted | 1 |
| §3 five skills sites repointed | 2 (×4), 3 (bundle) |
| §3 four agent sites repointed | 3 |
| §4 `workflows` deleted | 4 |
| §4 `ReadLegacySkillNmpAsync` + call sites deleted | 2 |
| §4 `agent` out of `LegacyRoots` + class docs | 3 |
| §4 both `GetScriniaBaseDir` deleted | 2 (Mcp), 3 (Bundles) |
| §4 `EntityTopics` + `IsEntityScope` + doc comments | 5 |
| §5 `IMemoryStore.WorkspaceRoot` | 1 |
| §6 atomic agent commit | 3 (stated as a Global Constraint) |
| §7 validation in the resolver, unknown kind throws, remote throws | 1 |
| §7 skipped categories reported | 4 |
| Testing 1 store purity | 6 |
| Testing 2 bundle-import regression | 3 |
| Testing 3 all readers agree | 6 |
| Testing 4 PathRouter table | 1 |
| Testing 5 round-trip | 2, 6 |
| Testing 6 deliberate breaks | 2 (legacy skill), 3 (LegacyRoots) |
| Testing 7 workflows import | 4 |

No gaps.

**2. Placeholder scan** — no TBD/TODO. Every code step carries real code. Three steps direct the implementer to match a signature the compiler reports (`PathParser.Parse`'s set type, the import result record's shape, the scope-resolution method's name); each names the exact file and line so it is a lookup, not a decision.

**3. Type consistency** — `ToFileEntityDir(kind, workspaceRoot)`, `TryToFileEntityPath(kind, name, workspaceRoot, out path)` and `IsFileEntity(segment)` are used with those exact signatures in Tasks 2, 3, 4 and 6. `WorkspaceRoot` is a property throughout. `CategoryDir` changes arity in Task 3 (drops `baseDir`) and Task 4 adds `CategoryToKind` in front of it — Task 4 restates the full method body so a reader arriving out of order sees the final shape.

**One deliberate wrinkle worth flagging to the reviewer:** the bundle manifest keys categories as `"skills"` (plural) while `PathRouter` keys on `"skill"` (singular). Task 4 bridges this with `CategoryToKind` rather than renaming either, because the manifest key is on-the-wire format in already-distributed bundles.
