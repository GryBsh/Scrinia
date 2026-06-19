# Codebase Leanness Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove dead/stale code and flatten two oversized files in the Scrinia solution without changing any behavior or public surface.

**Architecture:** Pure deletion and mechanical file-splitting. No new abstractions, no DI, no base classes. Existing tests + the compiler are the safety net: every task ends green on `dotnet build` and the relevant test project. The only shipped *new* code is one flat static helper (Task 6) that deduplicates verbatim-shared methods.

**Tech Stack:** .NET 10 / C# (`Scrinia.sln`), ConsoleAppFramework v5 (CLI), xUnit + FluentAssertions (tests), PowerShell (`publish.ps1`).

## Global Constraints

- **No behavior change.** No public API, CLI flag (except removing the already-broken `-WithLlm`), MCP tool, or REST endpoint changes. Splits move code verbatim.
- **No new abstractions/indirection.** Partial classes and one flat static helper only — no interfaces, base classes, or DI handlers.
- **Namespaces unchanged.** Moved types keep their existing namespace (`Scrinia.Core.Search`, `Scrinia.Commands`, `Scrinia.Commands.Hooks`).
- **Commit trailer (verbatim) on every commit:**
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```
- **`.scrinia/` changes are part of commits.** Do not exclude them.
- **Do not push.** Commit locally only; the user controls pushing.
- **Build/test commands** run from the repo root `C:\SynologyDrive\Source\repos\workspace-scrinia\Scrinia`.

---

### Task 1: Remove the dead `-WithLlm` path from `publish.ps1`

`publish.ps1` references `src/Scrinia.Plugin.Llm.Cli/Scrinia.Plugin.Llm.Cli.csproj`, which does not exist; `publish.ps1 -WithLlm` fails at runtime. `publish.sh` has no equivalent. Remove the dead path entirely.

**Files:**
- Modify: `publish.ps1`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing (script-only change).

- [ ] **Step 1: Remove the `-WithLlm` parameter**

Delete this block (lines 6-8, the `$WithLlm` switch and its surrounding blank line):

```powershell
    [switch]$WithLlm,

```

Leave `[switch]$WithVulkan,` (line 5) and the `[ValidateSet(...)]$Platform` parameter intact.

- [ ] **Step 2: Remove the `$LlmProject` variable**

Delete line 19:

```powershell
$LlmProject = 'src/Scrinia.Plugin.Llm.Cli/Scrinia.Plugin.Llm.Cli.csproj'
```

- [ ] **Step 3: Remove the `if ($WithLlm)` publish block in the build loop**

Delete the entire block currently at lines 80-102 (the `if ($WithLlm) { ... }` that publishes `$LlmProject` and overwrites Vulkan DLLs), including its closing brace and the trailing blank line before the loop's closing `}`. The preceding `if ($WithVulkan) { ... }` block (lines 53-78) stays.

- [ ] **Step 4: Remove the `if ($WithLlm)` reporting block in the summary loop**

Delete the block currently at lines 118-123:

```powershell
    if ($WithLlm) {
        $ext = if ($rid -like 'win-*') { '.exe' } else { '' }
        Get-ChildItem "$ridDir/plugins/scri-plugin-llm$ext" -ErrorAction SilentlyContinue |
            ForEach-Object { '{0}  {1}' -f $_.Length.ToString('N0').PadLeft(12), $_.FullName } |
            Write-Host
    }
```

The preceding `if ($WithVulkan) { ... }` reporting block stays.

- [ ] **Step 5: Verify the script parses and `-WithLlm` is gone**

Run:
```bash
pwsh -NoProfile -Command "Get-Command -Syntax ./publish.ps1"
```
Expected: syntax prints with `-Platform` and `-WithVulkan` but **no** `-WithLlm`. No parse error.

Also confirm no stragglers:
```bash
grep -n "WithLlm\|LlmProject\|Plugin.Llm" publish.ps1
```
Expected: no output.

- [ ] **Step 6: Commit**

```bash
git add publish.ps1
git commit -m "chore: remove dead -WithLlm path from publish.ps1

Referenced src/Scrinia.Plugin.Llm.Cli which does not exist; the flag
failed at runtime. publish.sh has no equivalent.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Wire `tests/Scrinia.Benchmarks` into the solution

The BenchmarkDotNet micro-benchmark project (`Bm25Scorer`, `HnswIndex`) is not in `Scrinia.sln`. Add it under the existing `Tests` solution folder and confirm it still compiles against current `Scrinia.Core`.

**Files:**
- Modify: `Scrinia.sln`
- (Possibly) Modify: `tests/Scrinia.Benchmarks/*.cs` only if compile drift is found (see Step 4).

**Interfaces:**
- Consumes: the `Tests` solution-folder GUID `{0AB3BF05-4346-4AA6-1389-037BE0695223}` (for nesting).
- Produces: a buildable `Scrinia.Benchmarks` project in the solution.

- [ ] **Step 1: Confirm the project's own GUID**

Run:
```bash
grep -i "ProjectGuid" tests/Scrinia.Benchmarks/Scrinia.Benchmarks.csproj
```
If a `<ProjectGuid>` exists, use that value below (call it `<BENCH_GUID>`). If none exists (SDK-style projects usually omit it), generate a fresh uppercase GUID:
```bash
pwsh -NoProfile -Command "[guid]::NewGuid().ToString().ToUpper()"
```
Use the result as `<BENCH_GUID>` consistently in every step below.

- [ ] **Step 2: Add the `Project` declaration**

In `Scrinia.sln`, immediately after the `Scrinia.Retrieval.Benchmarks.Tests` `EndProject` (line 49), add:

```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Scrinia.Benchmarks", "tests\Scrinia.Benchmarks\Scrinia.Benchmarks.csproj", "{<BENCH_GUID>}"
EndProject
```

- [ ] **Step 3: Add the build-configuration rows**

In the `GlobalSection(ProjectConfigurationPlatforms) = postSolution` section, add the following six rows (matching the pattern used by every other project — find an existing project's six lines as a template). Use `<BENCH_GUID>`:

```
		{<BENCH_GUID>}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{<BENCH_GUID>}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{<BENCH_GUID>}.Debug|x64.ActiveCfg = Debug|Any CPU
		{<BENCH_GUID>}.Debug|x86.ActiveCfg = Debug|Any CPU
		{<BENCH_GUID>}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{<BENCH_GUID>}.Release|Any CPU.Build.0 = Release|Any CPU
```

(Match the exact platform rows a neighboring test project uses; if neighbors also list `x64`/`x86` `.Build.0` rows, mirror them. The point is parity with siblings.)

- [ ] **Step 4: Nest it under the `Tests` solution folder**

In the `GlobalSection(NestedProjects) = preSolution` section, add:

```
		{<BENCH_GUID>} = {0AB3BF05-4346-4AA6-1389-037BE0695223}
```

(Confirm `{0AB3BF05-...}` is the `Tests` folder by checking the `Project(...) = "Tests", "Tests", ...` line. The other `tests\` projects' nesting rows confirm the target GUID — mirror them.)

- [ ] **Step 5: Build the solution and fix any drift**

Run:
```bash
dotnet build Scrinia.sln -c Debug
```
Expected: build succeeds and the output includes `Scrinia.Benchmarks`. If `Scrinia.Benchmarks` fails to compile (it drifted while out of the solution), fix **only** the minimal compile errors (renamed types/signatures in `Scrinia.Core`) to restore it — do not rewrite benchmark logic. If the drift is non-trivial (more than mechanical rename/signature fixes), STOP and report it for a decision rather than guessing.

- [ ] **Step 6: Commit**

```bash
git add Scrinia.sln tests/Scrinia.Benchmarks
git commit -m "build: add Scrinia.Benchmarks micro-benchmarks to the solution

Bm25Scorer/HnswIndex BenchmarkDotNet project was orphaned (not in
Scrinia.sln). Wire it under the Tests folder so it builds with the
solution.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Delete the vestigial `IMemorySearcher` interface

`IMemorySearcher` is `internal`, has a single `internal sealed` implementation (`WeightedFieldScorer`), is never swapped, and does not capture the real contract (`SearchAll` is not on it). Remove it.

**Files:**
- Modify: `src/Scrinia.Core/Search/IMemorySearcher.cs`
- Modify: `src/Scrinia.Core/Embeddings/TextChunker.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `WeightedFieldScorer` as a plain `internal sealed class` (no interface). Its public/internal method signatures are unchanged.

- [ ] **Step 1: Delete the interface declaration**

In `src/Scrinia.Core/Search/IMemorySearcher.cs`, delete lines 41-47:

```csharp
internal interface IMemorySearcher
{
    IReadOnlyList<ScoredArtifact> Search(
        string query,
        IEnumerable<ScopedArtifact> candidates,
        int limit);
}
```

- [ ] **Step 2: Drop the interface from the class declaration**

In the same file, change:

```csharp
internal sealed class WeightedFieldScorer : IMemorySearcher
```
to:
```csharp
internal sealed class WeightedFieldScorer
```

- [ ] **Step 3: Fix the dangling doc-comment cref**

In `src/Scrinia.Core/Embeddings/TextChunker.cs` (line ~6), the comment references the deleted type:

```csharp
/// memory name (see <see cref="Search.IMemorySearcher"/>) collapses chunk matches back to a
```
Change `Search.IMemorySearcher` to `Search.WeightedFieldScorer`:
```csharp
/// memory name (see <see cref="Search.WeightedFieldScorer"/>) collapses chunk matches back to a
```

- [ ] **Step 4: Verify no other references exist**

Run:
```bash
grep -rn "IMemorySearcher" src tests
```
Expected: no output.

- [ ] **Step 5: Build and test**

Run:
```bash
dotnet build Scrinia.sln -c Debug
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
```
Expected: build clean with no `CS1574`/`CS1580` cref warnings; `Scrinia.Tests` all green (search tests still pass — the scorer behaves identically).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/Search/IMemorySearcher.cs src/Scrinia.Core/Embeddings/TextChunker.cs
git commit -m "refactor: drop vestigial IMemorySearcher interface

Single internal sealed impl (WeightedFieldScorer), never swapped, and
the interface did not even capture the real contract (SearchAll). Make
the scorer a plain class; fix the TextChunker cref.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Split `IMemorySearcher.cs` into focused files

After Task 3 the file (~759 lines) holds two unrelated things: result records and the scorer. Split them; remove the misnamed file.

**Files:**
- Create: `src/Scrinia.Core/Search/SearchResults.cs`
- Create: `src/Scrinia.Core/Search/WeightedFieldScorer.cs`
- Delete: `src/Scrinia.Core/Search/IMemorySearcher.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: same public types in the same `Scrinia.Core.Search` namespace, now in two files. No signature changes.

- [ ] **Step 1: Create `SearchResults.cs` with the result records**

Move the result record types (current lines 5-39: `SearchResult`, `EntryResult`, `TopicResult`, `ChunkEntryResult`, `ScoredArtifact`, `TopicInfo`, including the `// ── Result types ──` banner comment) verbatim into a new file:

```csharp
using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

// ── Result types ────────────────────────────────────────────────────────────

// (paste record declarations SearchResult … TopicInfo here, verbatim from
//  the original IMemorySearcher.cs lines 7-39)
```

- [ ] **Step 2: Create `WeightedFieldScorer.cs` with the scorer**

Move the XML doc comment (current lines 49-87) and the `internal sealed class WeightedFieldScorer { ... }` body (lines 88-766, post-Task-3, with no interface) verbatim into a new file:

```csharp
using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

// (paste the /// <summary> … </summary> doc block, then
//  internal sealed class WeightedFieldScorer { … } verbatim)
```

Carry over the original file's `using Scrinia.Core.Models;` to both new files (it is the only using directive). Same-namespace types (`Bm25Scorer`, `TextAnalysis`, `RankerOptions`) need no using.

- [ ] **Step 3: Delete the original file**

```bash
git rm src/Scrinia.Core/Search/IMemorySearcher.cs
```

- [ ] **Step 4: Build and test**

Run:
```bash
dotnet build Scrinia.sln -c Debug
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
```
Expected: build clean (any missing using surfaces here); `Scrinia.Tests` all green.

- [ ] **Step 5: Verify the split is a pure move**

Run:
```bash
git diff --cached --stat
```
Expected: `IMemorySearcher.cs` deleted; `SearchResults.cs` + `WeightedFieldScorer.cs` added; combined added lines ≈ deleted lines (no logic rewritten).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/Search/SearchResults.cs src/Scrinia.Core/Search/WeightedFieldScorer.cs
git commit -m "refactor: split IMemorySearcher.cs into SearchResults + WeightedFieldScorer

Mechanical move; result records and the scorer live in their own files.
No logic change.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Deduplicate the hook-installer JSON helpers

`ClaudeCodeHookInstaller` and `CodexHookInstaller` carry the same JSON helpers. The constant-free ones are byte-identical; the marker-dependent ones differ only by the `ManagedMarker`/`MarkerVersion` constants (which are in fact equal — `"_scriniaManaged"` / `"v1"` — but pass them as parameters to keep the helper flat and decoupled). `ResolveConfigPath` stays per-installer (config dir/file names genuinely differ). No base class.

**Files:**
- Create: `src/Scrinia/Commands/Hooks/HookInstallerJson.cs`
- Modify: `src/Scrinia/Commands/Hooks/ClaudeCodeHookInstaller.cs`
- Modify: `src/Scrinia/Commands/Hooks/CodexHookInstaller.cs`

**Interfaces:**
- Consumes: per-installer `ManagedMarker` / `MarkerVersion` constants passed as arguments.
- Produces: `internal static class HookInstallerJson` with:
  - `JsonObject GetOrCreateObject(JsonObject parent, string key)`
  - `JsonArray GetOrCreateArray(JsonObject parent, string key)`
  - `JsonObject LoadOrCreate(string path)`
  - `void Save(string path, JsonObject root)`
  - `string? ExtractCommand(JsonObject block)`
  - `bool IsScriniaBlock(JsonObject block, string marker)`
  - `JsonObject FindOrCreateManagedBlock(JsonArray events, string marker)`
  - `void WriteManagedBlock(JsonObject block, string command, string marker, string markerVersion)`

- [ ] **Step 1: Create the shared helper**

Create `src/Scrinia/Commands/Hooks/HookInstallerJson.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scrinia.Commands.Hooks;

/// <summary>
/// JSON config helpers shared verbatim by the Claude Code and Codex hook
/// installers (both manage a JSON settings file with a marked, Scrinia-owned
/// block). Flat static utility — no base class. The managed-block marker and
/// version are passed in so each installer keeps its own constants.
/// </summary>
internal static class HookInstallerJson
{
    public static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var fresh = new JsonObject();
        parent[key] = fresh;
        return fresh;
    }

    public static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing) return existing;
        var fresh = new JsonArray();
        parent[key] = fresh;
        return fresh;
    }

    public static JsonObject LoadOrCreate(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        string raw = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        var node = JsonNode.Parse(raw);
        return node as JsonObject ?? new JsonObject();
    }

    public static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, root.ToJsonString(opts));
    }

    public static bool IsScriniaBlock(JsonObject block, string marker) =>
        block[marker] is JsonValue;

    public static JsonObject FindOrCreateManagedBlock(JsonArray events, string marker)
    {
        foreach (var node in events)
        {
            if (node is JsonObject obj && IsScriniaBlock(obj, marker)) return obj;
        }
        var fresh = new JsonObject();
        events.Add((JsonNode)fresh);
        return fresh;
    }

    public static void WriteManagedBlock(JsonObject block, string command, string marker, string markerVersion)
    {
        block["matcher"] = "";
        var hookEntry = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
        };
        var hooksArr = new JsonArray();
        hooksArr.Add((JsonNode)hookEntry);
        block["hooks"] = hooksArr;
        block[marker] = markerVersion;
    }

    public static string? ExtractCommand(JsonObject block)
    {
        if (block["hooks"] is not JsonArray hooks) return null;
        if (hooks.Count == 0 || hooks[0] is not JsonObject first) return null;
        return first["command"]?.GetValue<string>();
    }
}
```

- [ ] **Step 2: Point `ClaudeCodeHookInstaller` at the shared helper**

In `src/Scrinia/Commands/Hooks/ClaudeCodeHookInstaller.cs`:
- Delete the private methods `LoadOrCreate`, `Save`, `GetOrCreateObject`, `GetOrCreateArray`, `FindOrCreateManagedBlock`, `IsScriniaBlock`, `WriteManagedBlock`, `ExtractCommand` (lines 157-223). Keep `ResolveConfigPath` (lines 145-155) and the constants.
- Replace every call to the removed methods with the `HookInstallerJson` equivalent, threading the constants:
  - `LoadOrCreate(x)` → `HookInstallerJson.LoadOrCreate(x)`
  - `Save(x, y)` → `HookInstallerJson.Save(x, y)`
  - `GetOrCreateObject(p, k)` → `HookInstallerJson.GetOrCreateObject(p, k)`
  - `GetOrCreateArray(p, k)` → `HookInstallerJson.GetOrCreateArray(p, k)`
  - `FindOrCreateManagedBlock(e)` → `HookInstallerJson.FindOrCreateManagedBlock(e, ManagedMarker)`
  - `IsScriniaBlock(b)` → `HookInstallerJson.IsScriniaBlock(b, ManagedMarker)` (includes the call at line 124 inside `StatusOf`)
  - `WriteManagedBlock(b, cmd)` → `HookInstallerJson.WriteManagedBlock(b, cmd, ManagedMarker, MarkerVersion)`
  - `ExtractCommand(b)` → `HookInstallerJson.ExtractCommand(b)`

- [ ] **Step 3: Point `CodexHookInstaller` at the shared helper**

Apply the identical changes in `src/Scrinia/Commands/Hooks/CodexHookInstaller.cs`: delete the same private helper methods (lines 141-206, keeping `ResolveConfigPath` at 129-139 and the constants), and replace call sites with the `HookInstallerJson.*` equivalents, threading `ManagedMarker` / `MarkerVersion` exactly as in Step 2.

- [ ] **Step 4: Verify the duplicates are gone**

Run:
```bash
grep -rn "private static .* GetOrCreateObject\|private static .* WriteManagedBlock\|private static .* FindOrCreateManagedBlock" src/Scrinia/Commands/Hooks
```
Expected: no output (the only copies now live in `HookInstallerJson`).

- [ ] **Step 5: Build and test**

Run:
```bash
dotnet build Scrinia.sln -c Debug
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
```
Expected: build clean; all green. (Hook-installer behavior is unchanged; any test exercising install/status still passes.)

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia/Commands/Hooks/HookInstallerJson.cs src/Scrinia/Commands/Hooks/ClaudeCodeHookInstaller.cs src/Scrinia/Commands/Hooks/CodexHookInstaller.cs
git commit -m "refactor: dedup shared hook-installer JSON helpers

Extract the verbatim-shared JSON config helpers from the Claude Code and
Codex installers into one flat static HookInstallerJson; marker/version
passed as args. No base class, no behavior change.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Split `ScriniaCommands.cs` into partial files by command group

The 2267-line class holds ~18 command concerns. Split into partial-class files grouped by area. Same class, same ConsoleAppFramework registration, no DI/handlers. Each member's **body moves verbatim**; only its file location changes.

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.cs` (becomes the base: class header, fields, shared helpers, recovery commands)
- Create: `src/Scrinia/Commands/ScriniaCommands.Serve.cs`
- Create: `src/Scrinia/Commands/ScriniaCommands.Memory.cs`
- Create: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`
- Create: `src/Scrinia/Commands/ScriniaCommands.Setup.cs`
- Create: `src/Scrinia/Commands/ScriniaCommands.Hooks.cs`
- Create: `src/Scrinia/Commands/ScriniaCommands.HintCommand.cs`
- Create: `src/Scrinia/Commands/ScriniaCommands.Migration.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: the same `ScriniaCommands` type (now `partial`) with identical members. No signature/attribute changes.

**Member → file assignment** (by current line number from the inventory):

- **Base `ScriniaCommands.cs`:** class header (21, becomes `public partial class ScriniaCommands`), any fields/ctor (21-28), `WriteJsonError` (29), `EmitMcpResult` (522), and the bootstrap/recovery commands `Guide` (544), `Restore` (654), `Reconcile` (685).
- **`.Serve.cs`:** `Serve` (40).
- **`.Memory.cs`:** `List` (101), `BuildCliSummary` (201), `Search` (285), `Store` (380), `Show` (447), `Forget` (489), `Append` (569), `Compact` (612), `Link` (633).
- **`.Bundle.cs`:** `Export` (718), `Import` (760), `Bundle` (800).
- **`.Setup.cs`:** `Setup` (943), `TryConfigureOllamaAsync` (1132), `PromptForOllamaModelAsync` (1190), `ClearStaleOllamaConfig` (1277), `ConfigureMultiUser` (1295), `RunGit` (1349), `EnsureEmbeddingModelAsync` (1379), `DownloadFilesAsync` (1425), `Config` (1491), `TryReindexAfterConfigChangeAsync` (1589), `Reindex` (1611).
- **`.Hooks.cs`:** `InstallHooksFlowAsync` (1073), `UninstallHooksFlowAsync` (1104).
- **`.HintCommand.cs`:** `Hint` (1659), `ReadAllStdinAsync` (1742), `ExtractPromptFromStdin` (1759), `JsonEscape` (1777), `ResolveFiles` (1780).
- **`.Migration.cs`:** `Migrate` (1814), `BuildMigrationPlan` (1915), `MapTopicFileToV2` (1988), `RunCleanup` (2016), `CopyDirectoryRecursive` (2076), `CleanEmptyDirectories` (2093), `Consolidate` (2113), `IsSessionScope` (2229), `CompactEntryToSingleChunk` (2243).

> If, while moving, a private helper is used by members now in two different files, leave it in the base `ScriniaCommands.cs`. The build will tell you (a missing method is a hard error). Do not duplicate helpers.

- [ ] **Step 1: Make the class `partial`**

In `src/Scrinia/Commands/ScriniaCommands.cs`, change line 21:
```csharp
public class ScriniaCommands
```
to:
```csharp
public partial class ScriniaCommands
```

- [ ] **Step 2: Create each partial file with the same header**

For each new file, use this skeleton (copy only the `using` directives the moved members actually need — start by copying **all** the usings from the original file's lines 1-17; the build will let you trim unused ones, but unused usings are harmless and may trip `TreatWarningsAsErrors` only under Release/publish, not the Debug build here):

```csharp
// using directives copied from ScriniaCommands.cs (lines 1-17)
namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    // moved members for this group go here, verbatim
}
```

The namespace is `Scrinia.Commands` (confirm against the original file's `namespace` line).

- [ ] **Step 3: Move members group-by-group**

For each group in the assignment table, cut the listed members (signature + attributes + full body, verbatim) from `ScriniaCommands.cs` and paste them into the matching partial file's class body. Move one group at a time and rebuild between groups (Step 4) so a misplacement is caught immediately.

- [ ] **Step 4: Build after each group (and finally)**

Run after each group move and once at the end:
```bash
dotnet build src/Scrinia/Scrinia.csproj -c Debug
```
Expected: clean build. A `CS0103`/`CS0117` (name does not exist) means a helper was moved away from a caller — move that shared helper to the base file. A duplicate-definition error means a member was pasted without being cut from the original — remove the original copy.

- [ ] **Step 5: Verify the CLI command surface is unchanged**

Run:
```bash
dotnet build Scrinia.sln -c Debug
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
```
Expected: full build clean; `Scrinia.Tests` all green (command behavior unchanged).

Then smoke-test the CLI help to confirm ConsoleAppFramework still discovers every command (the command list must be identical to before the split):
```bash
dotnet run --project src/Scrinia/Scrinia.csproj -c Debug -- --help
```
Expected: the same set of commands (serve, list, search, store, show, forget, guide, append, compact, link, restore, reconcile, export, import, bundle, setup, config, reindex, hint, migrate, consolidate) as before.

- [ ] **Step 6: Verify the split is a pure move**

Run:
```bash
git add -A src/Scrinia/Commands
git diff --cached --stat
```
Expected: `ScriniaCommands.cs` shrinks substantially; the seven new partial files account for the moved lines; total added ≈ total removed (no logic rewritten).

- [ ] **Step 7: Commit**

```bash
git commit -m "refactor: split ScriniaCommands god-file into partial files by command group

2267-line class split into focused partials (Serve, Memory, Bundle,
Setup, Hooks, HintCommand, Migration) plus a base file for shared
helpers and recovery commands. Same class, same command surface, no
behavior change.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Final full-suite verification

Confirm the whole solution is green end-to-end after all changes.

**Files:** none (verification only).

- [ ] **Step 1: Clean build of the whole solution**

Run:
```bash
dotnet build Scrinia.sln -c Debug
```
Expected: 0 errors, no new warnings (especially no cref warnings).

- [ ] **Step 2: Run the full test suite**

Run:
```bash
dotnet test Scrinia.sln -c Debug
```
Expected: all test projects green — `Scrinia.Tests`, `Scrinia.Server.Tests`, `Scrinia.Merge.Tests`, `Scrinia.Plugin.Embeddings.Tests`, `Scrinia.Retrieval.Benchmarks.Tests`. Baselines to meet or exceed: `Scrinia.Tests` 874, `Scrinia.Server.Tests` 87.

- [ ] **Step 3: Confirm no stray behavior/API drift**

Run:
```bash
git log --oneline -7
git diff --stat main
```
Expected: seven cleanup commits; the diff is deletions + moves + one new helper file + one new sln entry — no edits to MCP tools, REST endpoints, or `Scrinia.Core` storage/search logic beyond the interface removal.

---

## Self-Review

**Spec coverage:**
- Spec item 1 (publish.ps1 `-WithLlm`) → Task 1. ✓
- Spec item 2 (wire Scrinia.Benchmarks) → Task 2. ✓
- Spec item 3 (delete IMemorySearcher) → Task 3. ✓
- Spec item 4 (split IMemorySearcher.cs) → Task 4. ✓
- Spec item 5 (split ScriniaCommands.cs) → Task 6. ✓
- Spec item 6 (hook-helper dedup) → Task 5. ✓
- Spec "out of scope" items → not present in any task. ✓
- Spec verification (build clean, full suite green, behavior preserved) → Task 7 + per-task gates. ✓
- `archive/` (optional, gitignored) → deliberately omitted, as the spec states. ✓

**Placeholder scan:** No "TBD"/"add error handling"/"similar to Task N". The one parameterized value (`<BENCH_GUID>`) has an explicit derivation step (Task 2 Step 1). Verbatim code-move tasks reference exact line ranges rather than re-transcribing thousands of unchanged lines — appropriate for mechanical relocations; the new helper (Task 5) is transcribed in full.

**Type consistency:** `HookInstallerJson` method signatures in the Interfaces block match the implementation in Task 5 Step 1 and the call-site replacements in Steps 2-3 (marker/version threaded consistently). `WeightedFieldScorer` stays `internal sealed` across Tasks 3-4. `ScriniaCommands` is `public partial class` across all Task 6 files.
