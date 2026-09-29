# store-boundaries Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split `IMemoryStore` at the line where `HttpMemoryStore` throws, so that a store which cannot do local filesystem work says so at compile time instead of at runtime — leaving exactly one `NotSupportedException` in `HttpMemoryStore`.

**Architecture:** A new `ILocalMemoryStore : IMemoryStore` carries the six members that are meaningless without a local filesystem. `FileMemoryStore` implements it; `HttpMemoryStore` implements only `IMemoryStore`. Because the new interface *extends* the old one, every call site that needs both member sets takes one parameter, not two. `SaveIndex` and `GenerateContentPreview` leave the interface entirely. MCP tools reach the narrower type through one guarded ambient accessor, which turns six scattered runtime traps into one named boundary that returns a clean error.

**Tech Stack:** C# / .NET 10, xunit 2.9.3, FluentAssertions 6.12.2, no mocking frameworks (test doubles are hand-written classes).

**Branch:** `store-boundaries`, cut from `main` at `c5cbb21`. Working tree clean apart from a pre-existing untracked `cmds.txt` that is **not part of this work**.

> **This plan was audited by two independent verification subagents on 2026-08-07, and they found a lot.** Corrections are recorded inline — marked "corrected by the verification pass" — rather than silently applied, because the pattern in them is instructive: the first draft's errors were almost all **claims of completeness**. It called a call-site list exhaustive when the grep had never looked at `tests/`; it said `Scrinia.Server` did not change when a two-line forwarder made it change; it gave one guard form for six methods when two of them were `async`. Individually small, collectively enough that the tree would not have built at Task 2's commit.
>
> The practical consequence for anyone executing this: **where the plan enumerates call sites, treat the list as a starting point and the compiler as the authority.** Where it gives code, it has been checked to compile — but check anyway.

## Global Constraints

- `TreatWarningsAsErrors=true` for projects not ending `.Tests` / `.Benchmarks` (`Directory.Build.props:44-46`). In `src/`, a warning is a build failure. In test projects it is not — and there are ~1694 pre-existing analyzer warnings there (F-18), so **warning count is not a health signal on this branch.** Do not fix warnings you did not introduce.
- There is **no `.editorconfig`** in this repo, and `EnforceCodeStyleInBuild` is unset, so IDE-prefixed rules never run at build. `NoWarn` covers `CA1848;CA1873;CA1822;CA1725;CA1051;CA1305;CA1310`. `ImplicitUsings` is on and `tests/Scrinia.Tests/GlobalUsings.cs` declares `global using Xunit;`.
- **Never `git add .`** — `cmds.txt` is untracked and unrelated. Stage explicit paths only.
- **Nothing is pushed.** Commit locally; do not `git push`.
- `dotnet build Scrinia.sln -c Debug` must be clean before any test run.
- **Never** two `dotnet test` invocations against one project concurrently.
- **Never** `dotnet build` during a test run — it fails on locked DLLs and can wedge the in-flight run.
- Clear stray `testhost.exe` / `dotnet.exe` before a full run; apparent hangs are orphaned processes holding `FileLock`s on `%TEMP%` stores.
- Test scope for this branch: `Scrinia.Tests`, `Scrinia.Server.Tests`, `Scrinia.Retrieval.Benchmarks.Tests`, `Scrinia.Plugin.Embeddings.Tests`. `Scrinia.Merge.Tests` is unaffected (spec fact 18).
- **Every new guard test must be observed red before the fix**, with the observed failure recorded in the task report. A test that passes with the defect present is not a test.
- Phantom LSP diagnostics are common in this repo (see `/findings/` and the Synology note): CS0246 / CS1061 against files that build fine. Verify every diagnostic against a real `dotnet build` before acting on it.

---

## Established Facts

Measured against the working tree at `c5cbb21` on 2026-08-07, with the command that produced each. **Re-run rather than re-trust.** Where a fact contradicts the spec `docs/superpowers/specs/2026-08-06-design-review-remediation-design.md`, the contradiction is called out — the spec's facts were measured at `a3e75ff`, three branches ago.

**F-1. `HttpMemoryStore` has exactly 8 `NotSupportedException` sites.**
`rtk grep -n "NotSupportedException" src/Scrinia/HttpMemoryStore.cs`
→ `:218` (`file://` rejection), `:257` (`SaveIndex`), `:496` (`ArtifactPath`), `:502` (`FindArtifactPath`), `:505` (`GetStoreDirForScope`), `:508` (`WorkspaceRoot`), `:612` (`ListTopicArtifacts`), `:616` (`ImportTopicEntries`).
Spec fact 9 said the same count at different line numbers; the count is unchanged, the lines moved.

**F-2. `ArtifactUri` is NOT local-only. The spec is wrong to list it.**
`src/Scrinia/HttpMemoryStore.cs:498-499` implements it:
```csharp
public string ArtifactUri(string name, string scope = "local") =>
    $"remote://{_store}/{scope}/{SanitizeName(name)}";
```
`rtk grep -rn "ArtifactUri" --include=*.cs src/` → four call sites outside `Scrinia.Core`: `MemoryTools.Core.cs:310`, `MemoryTools.Core.cs:1102`, `MemoryOrchestrator.cs:120`, `MemoryOrchestrator.cs:281`. The first two run under `scri serve --remote` (see F-9). Moving `ArtifactUri` to the local interface would break remote `remember`.
The spec's own arithmetic already required this: 8 throws − 1 kept (`file://`) − 1 `SaveIndex` = **6** layout members, not the 7 it listed.
**`ILocalMemoryStore` therefore has 6 members: `ArtifactPath`, `FindArtifactPath`, `GetStoreDirForScope`, `WorkspaceRoot`, `ListTopicArtifacts`, `ImportTopicEntries`.**

**F-3. The interface is named `ILocalMemoryStore` and extends `IMemoryStore`.**
Decided by the user on 2026-08-07, superseding the spec's standalone `ILocalStoreLayout`.

**Corrected 2026-08-07 by the verification pass.** An earlier draft justified this with "every call site that touches a layout member also touches ordinary store members." That is **false** — layout-only sites exist: `BundleFormatService.CategoryDir` (`:288`), `BundleFormatService.ExportFileEntities` (`:299`), and every `store.` use in `MemoryTools.Skills.cs` (`:195,196,201,238,307`, all `WorkspaceRoot`). The decision still holds, but on the narrower and true grounds that the **boundary** sites need both, and C# has no intersection types:

| Site | Layout members | Ordinary members |
|---|---|---|
| `BundleFormatService.ExportTopicsToZip` | `ListTopicArtifacts` | `SanitizeName`, `LoadIndex` |
| `BundleFormatService.ImportTopicsFromZip` | `ImportTopicEntries` | `SanitizeName` |
| `MemoryTools.Core.BuildDriftList:1337` | `WorkspaceRoot` | `ListScoped` |
| `MemoryTools.Lifecycle.BuildRestoreContextAsync` | `WorkspaceRoot` ×3 | `ParseQualifiedName`, `LoadIndex`, `ResolveArtifactAsync` |
| `ScriniaCommands.Migration.CompactEntryToSingleChunk:280` | `FindArtifactPath` | `ReadArtifactAsync`, `ArchiveVersion`, `WriteArtifactAsync`, `Upsert` |

A standalone interface would force two parameters referring to the same object at each of these. The name follows the design — it is a store, not a layout.

**F-4. The two `GenerateContentPreview` implementations are byte-identical.**
`rtk grep -n -A 8 "public string GenerateContentPreview" src/Scrinia.Core/FileMemoryStore.cs` (`:1504-1509`) and `src/Scrinia/HttpMemoryStore.cs:514-519` both read:
```csharp
if (string.IsNullOrEmpty(content)) return "";
string preview = content[..Math.Min(maxLength, content.Length)];
return preview.Replace('\n', ' ').Replace('\r', ' ').Trim();
```
Extraction to a static helper is therefore provably behaviour-preserving **in production**. One test double differs — `tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs:279` returns a truncation that is neither trimmed nor newline-flattened — so any assertion that runs a preview through `FakeMemoryStore` *does* change. It is deleted along with the interface member; if a `HybridScorerTests` assertion moves as a result, that is the divergence surfacing, not a regression. Do not describe Task 1 as behaviour-preserving without this qualification.

**F-5. `GenerateContentPreview` has 21 matching lines: 15 call sites, of which 12 are production.**
`rtk grep -rn "GenerateContentPreview" --include=*.cs src/ tests/` → 3 declarations (`IMemoryStore.cs:172`, `FileMemoryStore.cs:1504`, `HttpMemoryStore.cs:514`), 1 test-double declaration (`HybridScorerTests.cs:279`), 1 comment (`ScriniaMcpToolsTests.cs:287`), 1 test method name (`FileMemoryStoreTests.cs:246`), and 15 call sites.
**Production (12):** `ScriniaCommands.Bundle.cs:189`, `TextAnalysis.cs:231`, `MemoryTools.Core.cs:219,318,1011,1034,1081`, `MemoryOrchestrator.cs:71,128,218,233,266`.
**Test (3):** `EmbeddingBenchmarkAllTests.cs:442`, `EmbeddingBenchmarkTests.cs:462`, `FileMemoryStoreTests.cs:249`.
(An earlier draft said "15 production call sites" and its accounting summed to 20 of 21 lines. Corrected by the verification pass.)

**F-6. `SaveIndex` has zero production callers outside the store implementations — but one test call breaks.**
`rtk grep -rn "SaveIndex" --include=*.cs .` → the only non-comment, non-declaration call in `src/` is `FileMemoryStore.cs:1630` (inside `ImportTopicEntries`, i.e. `this`). Confirms spec fact 10.
**Corrected by the verification pass:** an earlier draft said every test use holds a concrete `FileMemoryStore`. `tests/Scrinia.Tests/AgentFileEntityTests.cs:89` does not — its `store` comes from `var store = MemoryStoreContext.Current!;` at `:80`, static type `IMemoryStore?`. That call breaks when `SaveIndex` leaves the interface. `FileMemoryStoreTests.cs:277,929,1029,1040` and `SaveIndexOrphanSidecarTests.cs:32,39` do hold concrete fields and are safe.

**F-7. Layout-member call sites in `src/`, by member. NOT EXHAUSTIVE — see the warning below.**
`rtk grep -rn "\.<member>\b" --include=*.cs src/Scrinia.Mcp src/Scrinia src/Scrinia.Server`
- `ArtifactPath`, `GetStoreDirForScope`: **zero** sites outside `Scrinia.Core`.
- `ListTopicArtifacts`, `ImportTopicEntries`: `BundleFormatService.cs:55,258` only.
- `FindArtifactPath`: one — `ScriniaCommands.Migration.cs:282`, inside `CompactEntryToSingleChunk(ScopedArtifact item, IMemoryStore store, CancellationToken ct)` (`:280`).
- `WorkspaceRoot`: **21** — `MemoryTools.Bundling.cs:31,32,82`; `MemoryTools.Core.cs:36,266,267,271,354,726,1339`; `MemoryTools.Lifecycle.cs:74,620,644,663`; `MemoryTools.Skills.cs:195,196,201,238,307`; `ScriniaCommands.Bundle.cs:155,156`; `BundleFormatService.cs:290`.

> **CORRECTED 2026-08-07 by the verification pass, and the correction is the important part.**
> An earlier draft of this plan called this list exhaustive and said the total was 20. Both were wrong: the arithmetic was off by one, and the grep was scoped to three `src/` projects, so it could not have been exhaustive — it never looked at `tests/`. **It missed the entire test-side blast radius (F-19) and the whole `Scrinia.Server` bundle chain (F-8).**
>
> **Treat the compiler, not this list, as the authority on what Task 2 must touch.** The list above tells an implementer where to *start* and which sites are subtle. It does not bound the work. An error in a file this plan never names is expected, not a surprise — see Task 2 Step 5's rule for how to fix one.

**F-8. `Scrinia.Server` DOES change — the earlier claim that it does not was wrong.**
Corrected 2026-08-07 by the verification pass. `src/Scrinia.Server/Services/BundleService.cs:13` and `:24` take `IMemoryStore store` and forward it to `BundleFormatService.ExportTopicsToZip` / `ImportTopicsFromZip` (`:17`, `:27`). Retyping those per F-13 breaks `BundleService`, which breaks its callers at `MemoryEndpoints.cs:346` and `:391`, which pass `ctx.Store!` — and `RequestContext.Store` is `public IMemoryStore? Store { get; set; }` (`src/Scrinia.Server/Auth/RequestContext.cs:15`).

**The server's store cannot simply be retyped, because it is a plugin seam.** `RequestContext.Store` is assigned once, at `Program.cs:434`, from `StoreManager.GetStore(string) → IMemoryStore` (`StoreManager.cs:27`), which delegates to `IStorageBackend.CreateStore` (`:33`). `Program.cs:92-95` registers `FilesystemBackend` and comments that plugin DI "runs before StoreManager is constructed **so plugins can replace `IStorageBackend`**". Narrowing `GetStore`'s return type would require every future backend to expose local filesystem paths — closing a seam this branch has no mandate to close. Task 3a narrows at the two bundle endpoints instead, with an HTTP error, mirroring what the MCP tools do one layer up.

**F-19. The test-side blast radius is large and this plan does not enumerate it.**
`rg "\.(ArtifactPath|WorkspaceRoot|GetStoreDirForScope|FindArtifactPath|SaveIndex|ListTopicArtifacts|ImportTopicEntries)\b" tests/` returns 40+ hits across ~20 files. Most hold a concrete `FileMemoryStore _store` field and are unaffected. The ones that break hold an `IMemoryStore`-typed receiver — overwhelmingly `var store = MemoryStoreContext.Current!;`, whose static type is `IMemoryStore?` (`src/Scrinia.Core/MemoryStoreContext.cs:16`). Known breakers, from the verification pass:
`ScriniaMcpToolsTests.cs:562,669`; `AgentFileEntityTests.cs:31,34,83,89`; `SkillPathAuthorityTests.cs:26,40`; `StorePurityInvariantTests.cs:27,40,41,66`; `WorkspaceRootUnificationTests.cs:21,24,34,37`; `ConsolidateReembedTests.cs:73`; `BundleImportCategoryAllowlistTests.cs:103,106`; `BundleV2Tests.cs:108`; `EntityFactStorePersistenceTests.cs:28,42`; `AttributeMigrationTests.cs:207`.
Also `HybridScorerTests.cs` declares **all six** layout members (`:274,276,277,278,285,286`) plus `SaveIndex` (`:264`) and `GenerateContentPreview` (`:279`) — the plan's Task 1 and Task 2 each name only part of that.
Task 2 Step 5a adds one test helper so this is a mechanical substitution rather than 20 ad-hoc casts.

**F-20. There are exactly three `IMemoryStore` implementations in the repo.**
Verified repo-wide: `FileMemoryStore.cs:23`, `HttpMemoryStore.cs:17`, and the hand-written `FakeMemoryStore` at `tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs:256`. `benchmarks/` contains zero references to `IMemoryStore` or any affected member. The plugin projects consume `IMemoryStore` only as an `IMemoryEventSink` parameter and touch none of the eight moved members. `tests/Scrinia.Server.Tests/Conformance/StoreConformanceFixture.cs:33,34,47` holds `IMemoryStore Local`/`Remote` but uses only `SearchAll` and `FormatQualifiedName`, so it survives unchanged.

**F-9. `scri serve --remote <url>` runs the whole MCP tool surface against `HttpMemoryStore`.**
`src/Scrinia/Commands/ScriniaCommands.Serve.cs:40-63`: when `--remote` is set, `MemoryStoreContext.Current = new HttpMemoryStore(httpClient, store ?? "default")` and the MCP host starts. `rtk grep -rn "new HttpMemoryStore" --include=*.cs src/ tests/` shows this is the only production construction site. So every fact below about "remote mode" is about a real, reachable configuration.

**F-10. `remember()` throws in remote mode today — but NOT unconditionally.**

> **CORRECTED 2026-08-07 after Task 2's review.** This fact originally read "throws in remote mode today, **unconditionally**", and justified that with "`:354` is on the main `Remember` path — not inside any conditional." That reasoning was the same species of error the plan's other corrections record: it established that the *statement* is not nested, and concluded that *control always reaches it*. Those are different claims.
>
> `Remember` tests `store.IsEphemeral(name)` at `:207` and **returns at `:257`**. `HttpMemoryStore` implements ephemeral storage for real — `// Ephemeral storage stays client-side` (`:15`), backed by `_ephemeral` (`:85`), `RememberEphemeral` (`:164`), `GetEphemeral` (`:179`). So `remember({ path: '/temp/scratch' })` **worked** against a remote store and must keep working.
>
> The Task 2 implementer reproduced the original error exactly, auditing reachability by brace depth, and it cost a Critical-severity regression elsewhere (`guide()`). **Brace depth cannot see an early `return` upstream, and cannot see a `catch { }` that made the old throw a no-op.** Trace control flow, or run it.

The true claim: `src/Scrinia.Mcp/MemoryTools.Core.cs:354` reads `store.WorkspaceRoot` on the **non-ephemeral** `Remember` path, and `store` is `CurrentStore` (`MemoryTools.cs:16-18`) = `MemoryStoreContext.Current`, which under F-9 is `HttpMemoryStore`, whose `WorkspaceRoot` throws (`:507-510`). The value is used only to hash `file` attribute references for drift detection, which most calls do not have. So an ordinary remote `remember` of a normal memory throws; an ephemeral one does not.

**Task 3's red check must distinguish the two cases** — assert the ephemeral path succeeds AND the non-ephemeral path throws before the fix.

**F-10a. `restore()` and `guide()` work in remote mode today, degraded — and must keep doing so.**
Established 2026-08-07 by Task 2's review, verified against source. `BuildRestoreContextAsync`'s three `WorkspaceRoot` reads (`Lifecycle.cs:620,644,663`) each sit inside a `try { … } catch { /* best-effort */ }` (catches at `:639,658,688`). Remotely the throw lands in those catches and is swallowed: the method returns a valid context minus the conflict-scan, skills, and agent sections.
`BuildRestoreContextAsync` has **two** callers — `Restore` (`Lifecycle.cs:593`) and **`Guide` (`MemoryTools.cs:68`)**, the tool this workspace's `CLAUDE.md` instructs agents to call at session start.
**This settles Task 3 Step 4a empirically.** Option (b) — a degraded path — is not a design preference; it is what the code already does. Restoring it is behaviour preservation, and any option that makes `guide()` fail outright against a remote store is a regression.

**F-11. Three further layout uses are already conditional or already have a fallback.**
- `MemoryTools.Core.cs:723-726` (`BuildFullList`): `workspaceRoot` is `string?`, assigned only `if (anyCodeRefs)`. Remote `list()` works until an entry carries `FileHashes`.
- `MemoryTools.Core.cs:30-44` (`Recall`): the `/agent/` branch reads `agentStore.WorkspaceRoot` and is followed by the comment `// Fall through to NMP/2 resolution for legacy entries` — a fallback already exists.
- `MemoryTools.Core.cs:1337-1339` (`BuildDriftList`): takes `IMemoryStore store` and reads `WorkspaceRoot`; drift detection is local-only by nature.

**F-12. The MCP tool surface reaches the store ambiently, not by parameter.**
`src/Scrinia.Mcp/MemoryTools.cs:16-18`:
```csharp
private static IMemoryStore CurrentStore =>
    MemoryStoreContext.Current ?? throw new InvalidOperationException(
        "No memory store configured. Call MemoryStoreContext.Current = ... before using MCP tools.");
```
`rtk grep -rn "CurrentStore" --include=*.cs src/Scrinia.Mcp/` shows `var store = CurrentStore;` as the opening line of tool methods. **The spec's instruction to "change their parameter type" does not fit these sites — they have no parameter.** The guarded accessor in Task 2 is the answer.

**F-13. `BundleFormatService` needs both member sets in one method, and has four `IMemoryStore` parameters, not two.**
`rtk grep -n "store\." src/Scrinia.Core/Bundles/BundleFormatService.cs` → `SanitizeName` (`:54,61,74,147,148,247`) and `LoadIndex` (`:56`) alongside `ListTopicArtifacts` (`:55`), `ImportTopicEntries` (`:258`), `WorkspaceRoot` (`:290`).
Public signatures: `ExportTopicsToZip(ZipArchive zip, IMemoryStore store, string[] topics)` (`:46-47`) and `ImportTopicsFromZip(ZipArchive zip, IMemoryStore store, string[]? filterTopics, bool overwrite)` (`:110-111`).
**Added by the verification pass — an earlier draft named only the two public methods.** Two private helpers in the same file also take `IMemoryStore` and must be retyped with them: `CategoryDir(IMemoryStore store, string category)` (`:288`, the actual holder of the `:290` `WorkspaceRoot` read) and `ExportFileEntities(ZipArchive, IMemoryStore)` (`:299`). A fourth parameter at `:353` also takes `IMemoryStore` — read it and decide. This is the concrete case F-3 was decided for.

**F-14. `ErrorCodes` has no code for an unsupported capability.**
`src/Scrinia.Mcp/McpResponse.cs:13-28` declares exactly: `InvalidParameter`, `InvalidAction`, `InvalidPath`, `NotFound`, `Conflict`, `Internal`. Task 3 adds `NotSupported = "NOT_SUPPORTED"`.

**F-15. `MemoryEventSinkContext.Default` is assigned from six places, all in `WorkspaceSetup.cs`.**
`rtk grep -rn "MemoryEventSinkContext" --include=*.cs src/` → assignments at `:231`, `:391`, `:409`, `:418`, `:438`, `:519`. Confirms spec fact 16 exactly, line numbers included. Reads of `MemoryEventSinkContext.Current` (`ScriniaCommands.Migration.cs:311`, `MemoryTools.Core.cs:874,903,936,1433`) and the server's per-request `Current` assignment (`Program.cs:296`) are **out of scope** — the spec is explicit that `Default` itself, the AsyncLocal fallback, `StoreScope`'s clear, and `BackgroundWork` are untouched.

**F-16. The server's search handlers are now a pair, not a single method.**
`rtk grep -rn "StoreMemory\|ShowMemory\|SearchMemories\|ListMemories" --include=*.cs src/ tests/` → `MemoryEndpoints.cs:22,23,24,29,30` (registrations) and `:43,79,120,250,262` (declarations). `SearchMemoriesPost` (`:262`) was added by the `search-contract` branch and postdates the spec, which named only four methods to rename.
**No test file references these names** — the grep found zero matches under `tests/`. The rename is confined to one file.

**F-17. The stale tool-list comment is `ScriniaMcpToolsTests.cs:10-25`, not `:11-23`.**
Lines `:11-20` are the stale inventory (`Show`, `Store`, `List`, `Search`, `Copy`, `Forget`, `Export`, `Import`, `Append`). Lines `:22-24` are still true — "All tests are offline" and "Store/List/Forget tests use TestHelpers.StoreScope to isolate from the real user store." The spec said "delete"; this plan deletes only the stale inventory and keeps the two accurate notes, because deleting a true statement is not a cleanup.

**F-18. Baseline builds clean, but "0 warnings" is not the signal it looks like.**
`rtk dotnet build Scrinia.sln -c Debug` on an already-built tree → `0 errors, 0 warnings`. **Corrected by the verification pass:** that was a no-op incremental build. A cold build reports **1694 warnings, 0 errors** across **16** projects. Every one of the 1694 is an analyzer warning (CA1707, CA1861, …) in a **test** project, and `TreatWarningsAsErrors` covers `src/` only (`Directory.Build.props:44-46`) — so they are pre-existing and not a gate.

**CORRECTED 2026-08-07 by Task 6's doc pass.** The project count above is wrong: a cold `dotnet build Scrinia.sln -c Debug --no-incremental` reports **17** projects, not 16. The warning count (1694) was right and is unaffected. Not worth its own commit; folded into Task 6's documentation changes.
**Consequence for implementers:** do not use warning count as a health signal, and do not "fix" warnings you did not introduce. The gate is `0 errors`, plus no *new* warning in a `src/` project (where any warning is an error anyway).

---

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `src/Scrinia.Core/ILocalMemoryStore.cs` | **create** | The six local-filesystem members, as an interface extending `IMemoryStore`. |
| `src/Scrinia.Core/ContentPreview.cs` | **create** | The one implementation of preview truncation, as a static. |
| `src/Scrinia.Core/IMemoryStore.cs` | modify | Loses 8 members: the 6 layout members, `SaveIndex`, `GenerateContentPreview`. |
| `src/Scrinia.Core/FileMemoryStore.cs` | modify | Implements `ILocalMemoryStore`; keeps `SaveIndex` as a public member. |
| `src/Scrinia/HttpMemoryStore.cs` | modify | Loses 7 throwing members. One `NotSupportedException` remains. |
| `src/Scrinia.Core/Bundles/BundleFormatService.cs` | modify | Both methods take `ILocalMemoryStore`. |
| `src/Scrinia.Mcp/MemoryTools.cs` | modify | Adds `CurrentLayout` and `TryGetLayout`. |
| `src/Scrinia.Mcp/McpResponse.cs` | modify | Adds `ErrorCodes.NotSupported`. |
| `src/Scrinia.Mcp/MemoryTools.{Core,Bundling,Lifecycle,Skills}.cs` | modify | Local-only tools guard; optional uses narrow. |
| `src/Scrinia/Commands/{WorkspaceSetup,ScriniaCommands.Bundle,ScriniaCommands.Migration}.cs` | modify | CLI holds a concrete store; sink installs route through one method. |
| `src/Scrinia.Core/IMemoryEventSink.cs` | modify | Adds `InstallDefault`. |
| `src/Scrinia.Server/Services/BundleService.cs` | modify | Takes `ILocalMemoryStore` (F-8). |
| `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` | modify | Bundle endpoints narrow with a 501 (F-8); handler renames (F6). |
| `tests/Scrinia.Tests/TestHelpers.cs` | modify | `LocalStore` narrowing helper, so ~10 test files need no casts (F-19). |
| `tests/Scrinia.Tests/StoreBoundaryTests.cs` | **create** | The one-remaining-throw property; remote-mode tool behaviour. |
| ~10 further test files (F-19) | modify | Receiver narrowed via the helper. **The compiler is the authority, not F-7.** |
| `docs/architecture/core.md` (+ any live doc that names the moved members) | modify | Interface description. |
| `CHANGELOG.md` | modify | `[Unreleased]` entries. |

---

## Task 1: Extract `GenerateContentPreview` to a static

**Why first:** it is the only piece of this branch that is pure mechanical substitution with a proof of equivalence (F-4). Landing it alone keeps it out of the interface-split diff, where it would be noise.

**Files:**
- Create: `src/Scrinia.Core/ContentPreview.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs:172`, `src/Scrinia.Core/FileMemoryStore.cs:1504-1509`, `src/Scrinia/HttpMemoryStore.cs:514-519`
- Modify (call sites, per F-5): `src/Scrinia/Commands/ScriniaCommands.Bundle.cs:189`, `src/Scrinia.Core/Search/TextAnalysis.cs:231`, `src/Scrinia.Mcp/MemoryTools.Core.cs:219,318,1011,1034,1081`, `src/Scrinia.Server/Services/MemoryOrchestrator.cs:71,128,218,233,266`
- Modify (tests): `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingBenchmarkAllTests.cs:442`, `tests/Scrinia.Tests/Benchmarks/EmbeddingBenchmarkTests.cs:462`, `tests/Scrinia.Tests/FileMemoryStoreTests.cs:246-250`, `tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs:279`

**Interfaces:**
- Produces: `Scrinia.Core.ContentPreview.Generate(string content, int maxLength = 500) → string`. Later tasks do not depend on it.

- [ ] **Step 1: Create the static helper**

`src/Scrinia.Core/ContentPreview.cs`:
```csharp
namespace Scrinia.Core;

/// <summary>
/// Content preview truncation. A pure string function — it was previously a member of
/// <see cref="IMemoryStore"/>, where both implementations carried byte-identical bodies
/// and neither touched any store state.
/// </summary>
public static class ContentPreview
{
    /// <summary>
    /// Truncates <paramref name="content"/> to <paramref name="maxLength"/> characters and
    /// flattens newlines to spaces, so the result is safe to place in a single-line column.
    /// </summary>
    public static string Generate(string content, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(content)) return "";
        string preview = content[..Math.Min(maxLength, content.Length)];
        return preview.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
```

- [ ] **Step 2: Move the existing unit test onto the static, and confirm it still passes**

`tests/Scrinia.Tests/FileMemoryStoreTests.cs:246-250` currently reads `_store.GenerateContentPreview(long_content)`. Change the call to `ContentPreview.Generate(long_content)` and leave the assertions untouched. The test moves with the code; its assertions are the equivalence proof.

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~GenerateContentPreview"`
Expected: PASS. (This step does not need a red check — the behaviour is unchanged by construction, and the equivalence is established by F-4, not by this test.)

- [ ] **Step 3: Retype every call site**

Replace `store.GenerateContentPreview(x)` → `ContentPreview.Generate(x)` and `_store.GenerateContentPreview(x)` → `ContentPreview.Generate(x)` at each site in the Files list. Add `using Scrinia.Core;` where the file does not already have it.

Do **not** change `WorkspaceSetup.Store.GenerateContentPreview(content)` at `ScriniaCommands.Bundle.cs:189` into anything store-shaped — it becomes `ContentPreview.Generate(content)` and the `WorkspaceSetup.Store` reference disappears from that line entirely.

- [ ] **Step 4: Remove the member from the interface and both implementations**

Delete `IMemoryStore.cs:171-172` (the `// Content utility` comment and the declaration), `FileMemoryStore.cs:1502-1509` (**including its `// ── Content utility ──` banner at `:1502`** — an earlier draft ordered the banner deleted in `HttpMemoryStore` only, leaving this one orphaned), `HttpMemoryStore.cs:512-519` (banner at `:512`), and the test double at `HybridScorerTests.cs:279`.

**Also handle the parameter this strands.** `TextAnalysis.ComputeChunkEntries(IMemoryStore store, string[] chunks)` (`TextAnalysis.cs:224`) uses `store` **only** at `:231`. After the substitution it is an unused parameter on a `public static` `Scrinia.Core` API. It is not a build break (IDE analyzers do not run at build here), but leaving it is worse than removing it: it advertises a dependency the method no longer has. Remove the parameter and fix its callers, or — if a caller makes that costly — say so in the report and leave it, rather than deciding silently.

- [ ] **Step 5: Build and run the affected suites**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj
rtk dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj
```
Expected: 0 errors, 0 warnings; all suites pass. Record the counts in the report.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/ContentPreview.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia/HttpMemoryStore.cs src/Scrinia.Core/Search/TextAnalysis.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Server/Services/MemoryOrchestrator.cs src/Scrinia/Commands/ScriniaCommands.Bundle.cs tests/
git commit -m "refactor(core): move content preview off the store interface

It was a pure string function on a storage abstraction, with byte-identical
bodies in both implementations."
```

---

## Task 2: Introduce `ILocalMemoryStore` and split the interface

**Why this is behaviour-preserving:** every site that reaches a layout member today still reaches it after this task. The ambient accessor `CurrentLayout` throws `NotSupportedException` in exactly the case where `HttpMemoryStore`'s member threw it. Nothing that worked stops working, and nothing that threw stops throwing. Task 3 is where behaviour changes, in a small diff that can be judged on its own.

**Files:**
- Create: `src/Scrinia.Core/ILocalMemoryStore.cs`
- Create: `tests/Scrinia.Tests/StoreBoundaryTests.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs` (remove 7 members), `src/Scrinia.Core/FileMemoryStore.cs` (declaration line), `src/Scrinia/HttpMemoryStore.cs` (remove 7 members), `src/Scrinia.Core/Bundles/BundleFormatService.cs:46-47,110-111`, `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/MemoryTools.{Core,Bundling,Lifecycle,Skills}.cs`, `src/Scrinia/Commands/ScriniaCommands.{Bundle,Migration}.cs`
- Modify (tests): `tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs` (remove the `SaveIndex` stub at `:264`)

**Interfaces:**
- Produces: `Scrinia.Core.ILocalMemoryStore : IMemoryStore` with the six members from F-2.
- Produces: `ScriniaMcpTools.CurrentLayout → ILocalMemoryStore` (private static, in `MemoryTools.cs`). Task 3 adds `TryGetLayout` beside it and converts the local-only tools to use that instead.

- [ ] **Step 1: Create the interface**

`src/Scrinia.Core/ILocalMemoryStore.cs`:
```csharp
using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// A memory store backed by a local filesystem. Extends <see cref="IMemoryStore"/> with the
/// members that have no meaning without one — the members <c>HttpMemoryStore</c> could only
/// ever implement by throwing.
/// <para>The split exists so that "this operation needs local files" is a compile-time fact.
/// Before it, six members of the shared interface threw <see cref="NotSupportedException"/>
/// at runtime in remote mode, and nothing told a caller which six.</para>
/// <para>It extends rather than stands alone because every caller that needs a path also
/// needs ordinary store operations — bundle export reads the index, drift detection formats
/// qualified names — and C# has no intersection types. One parameter, both member sets.</para>
/// </summary>
public interface ILocalMemoryStore : IMemoryStore
{
    // Paths
    string ArtifactPath(string name, string scope = "local");
    string FindArtifactPath(string subject, string normalizedScope);
    string GetStoreDirForScope(string scope);

    /// <summary>
    /// Absolute path to the workspace root — the directory that contains <c>.scrinia/</c>.
    /// The authority for the workspace's filesystem layout: hand-authored file entities
    /// (via <see cref="PathRouter.ToFileEntityDir"/>), bundle export/import, conflict
    /// scanning, and drift detection all resolve their on-disk location from this one
    /// property.
    /// </summary>
    string WorkspaceRoot { get; }

    // Export/Import support
    List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope);
    void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, string> artifactContents, bool overwrite);
}
```

Note what is **not** here: `ArtifactUri` (F-2 — `HttpMemoryStore` implements it for real) and `SaveIndex` (F-6 — no production caller needs it through an interface at all).

- [ ] **Step 2: Write the property test, and run it to watch it pass for the wrong reason**

This is the branch's entry gate as an executable claim. Write it now so it is red-by-construction until the split lands.

`tests/Scrinia.Tests/StoreBoundaryTests.cs`:
```csharp
using System.Reflection;
using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Pins the store boundary: <see cref="IMemoryStore"/> is the contract both stores can honour,
/// so a remote store must be able to implement all of it. The failure mode this catches is
/// someone adding a local-only member back to the shared interface — which nothing else does.
/// </summary>
public sealed class StoreBoundaryTests
{
    [Fact]
    public void HttpMemoryStore_ThrowsNotSupported_ForExactlyOneReason()
    {
        // The one legitimate NotSupportedException in remote mode is the file:// rejection
        // inside ResolveArtifactAsync — a rejection of an input value, not of a capability.
        var sourcePath = Path.Combine(RepoRoot(), "src", "Scrinia", "HttpMemoryStore.cs");
        var throwLines = File.ReadAllLines(sourcePath)
            .Select((line, i) => (Line: line, Number: i + 1))
            .Where(x => x.Line.Contains("throw new NotSupportedException"))
            .ToList();

        throwLines.Should().ContainSingle(
            because: "every other NotSupportedException marked a member that belongs on " +
                     "ILocalMemoryStore, not on the interface a remote store implements");
        throwLines[0].Line.Should().Contain("file://");
    }

    [Fact]
    public void IMemoryStore_DoesNotDeclareLocalOnlyMembers()
    {
        var names = typeof(IMemoryStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        names.Should().NotContain("WorkspaceRoot");
        names.Should().NotContain("ArtifactPath");
        names.Should().NotContain("FindArtifactPath");
        names.Should().NotContain("GetStoreDirForScope");
        names.Should().NotContain("ListTopicArtifacts");
        names.Should().NotContain("ImportTopicEntries");
        names.Should().NotContain("SaveIndex");
        names.Should().NotContain("GenerateContentPreview");

        names.Should().Contain("ArtifactUri",
            because: "HttpMemoryStore implements it for real, returning a remote:// URI");
    }

    private static string RepoRoot()
    {
        // Same walk-up as tests/Scrinia.Merge.Tests/GitMergeHarness.cs:33-38. Copy its
        // failure message too — it names the directory it started from, which is the only
        // thing worth knowing when this fails. That project is outside this branch's test
        // scope, so the helper is duplicated rather than referenced.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrinia.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not find Scrinia.sln walking up from {AppContext.BaseDirectory}.");
    }
}
```

**Implementer notes on the first test.**
- It reads source text rather than reflecting, because the property is about *how many throw sites exist*, which reflection cannot see. If a substitute is needed it must still count throw sites, not merely check that one exists.
- The walk-up is verified to work here: test binaries land in `tests/Scrinia.Tests/bin/Debug/net10.0/`, four levels below the root, and VSTest does not shadow-copy by default, so `AppContext.BaseDirectory` is the real output directory.

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~StoreBoundaryTests"`
Expected **before** the split: both tests FAIL — the first finds 8 throw sites, the second finds `WorkspaceRoot` on `IMemoryStore`. **Record the observed numbers in the report.** If either passes at this point, the test is not measuring what it claims and must be fixed before proceeding.

- [ ] **Step 3: Move the members**

In `src/Scrinia.Core/IMemoryStore.cs`, delete:
- `:21` — `void SaveIndex(List<ArtifactEntry> entries, string scope = "local");`
- `:152-169` — the `// Paths` block, keeping **only** `ArtifactUri` (`:154`), which moves up under a `// Naming / addressing` comment.
- `:183-186` — the `// Export/Import support` block.

In `src/Scrinia.Core/FileMemoryStore.cs`, change the type declaration to implement `ILocalMemoryStore`. Its member bodies do not change; `SaveIndex` stays a public member of the class (F-6 — the tests that call it hold a concrete store).

In `src/Scrinia/HttpMemoryStore.cs`, delete the seven members that threw: `SaveIndex` (`:256-257`), `ArtifactPath` (`:495-496`), `FindArtifactPath` (`:501-502`), `GetStoreDirForScope` (`:504-505`), `WorkspaceRoot` (`:507-510`), `ListTopicArtifacts` (`:611-612`), `ImportTopicEntries` (`:614-616`). Keep `ArtifactUri` (`:498-499`). Delete the now-empty `// ── Paths (not supported in remote mode) ──` banner and move `ArtifactUri` under a banner that describes what it is.

Delete the now-orphaned `// ── Export/Import helpers ──` banner at `HttpMemoryStore.cs:609`.

Delete the `SaveIndex` stub at `tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs:264` **and its six layout stubs at `:274,276,277,278,285,286`** — the double implements all of them.

- [ ] **Step 4: Add the guarded ambient accessor**

In `src/Scrinia.Mcp/MemoryTools.cs`, directly below `CurrentStore` (`:16-18`):
```csharp
/// <summary>
/// The active store, narrowed to <see cref="ILocalMemoryStore"/>. Tools that touch the
/// workspace filesystem — bundle export/import, skill and agent files, conflict scanning —
/// go through here.
/// <para>This is the one place the local-store requirement is checked at runtime. It
/// replaces six separate members that each threw from inside <c>HttpMemoryStore</c>, where
/// a caller had no way to know which members were safe. Task 3 gives the tools that use it
/// a clean error response instead of this exception.</para>
/// </summary>
private static ILocalMemoryStore CurrentLayout =>
    MemoryStoreContext.Current as ILocalMemoryStore
    ?? throw new NotSupportedException(
        "This operation needs local filesystem access, and the active store is remote.");
```

**A reviewer will ask whether this is the capability probe the spec rejected. It is not, and the plan says why:** a `SupportsLocalPaths` bool leaves all members on one interface and asks every caller to remember a check that nothing enforces. Here the type system carries the requirement — a method that needs a path must hold an `ILocalMemoryStore`, and the only ambient way to obtain one is this accessor. The check moves from six unenforced places to one that cannot be bypassed.

- [ ] **Step 5: Make it compile — the mechanical wave**

Change `var store = CurrentStore;` to `var store = CurrentLayout;` in the methods that touch a layout member. **The line numbers below are the `var store = …` assignment lines, not the method declarations** — an earlier draft cited declaration lines here, which sent an implementer to the wrong line in every case:

| File | Method | **Assignment line to edit** | Layout uses |
|---|---|---|---|
| `MemoryTools.Bundling.cs` | `Export` (decl `:19`) | **`:24`** | `:31,32` |
| `MemoryTools.Bundling.cs` | `Import` (decl `:73`) | **`:79`** | `:82` |
| `MemoryTools.Lifecycle.cs` | `Reconcile` (decl `:67`) | **`:73`** | `:74` |
| `MemoryTools.Lifecycle.cs` | `BuildRestoreContextAsync` (decl `:608`) | **`:610`** | `:620,644,663` |
| `MemoryTools.Skills.cs` | `SkillCreate` (decl `:120`) | **`:127`** | `:195,196,201` |
| `MemoryTools.Skills.cs` | `SkillLoad` (decl `:229`) | **`:234`** | `:238` |
| `MemoryTools.Core.cs` | `Remember` (decl `:109`) | **`:134`** | `:266,267,271,354` |

Note the collision hazard: `Bundling.cs`'s `Import` is declared at `:73` and `Lifecycle.cs`'s `Reconcile` *assigns* at `:73`. Different files, same number.

Retype parameters where the method receives a store:
- `MemoryTools.Skills.cs:304-305` — `LoadSkillAsync(IMemoryStore store, …)` → `LoadSkillAsync(ILocalMemoryStore store, …)`
- `MemoryTools.Core.cs:715` — `BuildFullList(List<ScopedArtifact> entries, IMemoryStore store, int offset, int limit)` → `ILocalMemoryStore store`
- `MemoryTools.Core.cs:1337` — `BuildDriftList(IMemoryStore store)` → `ILocalMemoryStore store`
- `BundleFormatService.cs:47`, `:111`, and the private helpers `CategoryDir` (`:288`), `ExportFileEntities` (`:299`), and `:353` — `IMemoryStore store` → `ILocalMemoryStore store` (F-13)
- `ScriniaCommands.Migration.cs:280` — `CompactEntryToSingleChunk(ScopedArtifact item, IMemoryStore store, CancellationToken ct)` → `ILocalMemoryStore store`

**Then fix the callers those retypes break. Both were missing from an earlier draft and each one alone stops the tree building at this commit:**

- **`MemoryTools.Core.cs:588`** — `List` does `var store = CurrentStore;` and passes it to *both* retyped callees: `BuildDriftList(store)` at `:591` and `BuildFullList(entries, store, offset, limit)` at `:602`. Change `:588` to `var store = CurrentLayout;`.
  **This site is why F-7's grep could not be exhaustive:** the word `WorkspaceRoot` never appears in `List`, so a member-name search could not find it. Expect more of this shape.
  Task 3 Step 6 revisits `:588` — `list()` is not a local-only tool.
- **`ScriniaCommands.Migration.cs:198`** — `IMemoryStore store = WorkspaceSetup.Store;`, passed to `CompactEntryToSingleChunk` at `:206`. Change to `ILocalMemoryStore store = WorkspaceSetup.LocalStore;`.

Test callers of the retyped methods, which the compiler will also flag: `McpDisplayCutoverTests.cs:55` (passes a concrete `FileMemoryStore` — **survives unchanged**), `ConsolidateReembedTests.cs:94` and `ConsolidateSingleStoreTests.cs:35` (pass `IMemoryStore` — fixed by Step 5a).

For `MemoryTools.Core.cs:36` (`Recall`'s `/agent/` branch), the local is `MemoryStoreContext.Current` with an explicit null check. Narrow it in place, preserving the existing fall-through:
```csharp
var agentStore = MemoryStoreContext.Current as ILocalMemoryStore;
if (agentStore is not null)
```
This is the smallest change that compiles and it is behaviour-changing in the safe direction — remote `/agent/` recall now falls through instead of throwing. Note it in the report; Task 3 covers it with a test.

For the CLI, `WorkspaceSetup.Store` is typed `IMemoryStore` (`WorkspaceSetup.cs:36-38`) but `Configure` (`:49`) only ever assigns a `FileMemoryStore`. Add a sibling property rather than changing `Store`'s type, so remote-mode CLI paths keep the wider type:
```csharp
/// <summary>The configured store, narrowed to the local-filesystem contract. CLI commands
/// that read or write workspace files use this. Throws when the process is in remote mode
/// (<c>scri serve --remote</c>), which is the only configuration that installs a non-local
/// store — see ScriniaCommands.Serve.</summary>
internal static ILocalMemoryStore LocalStore =>
    MemoryStoreContext.Current as ILocalMemoryStore
    ?? throw new InvalidOperationException(
        "This command needs a local workspace; the configured store is remote.");
```
Then `ScriniaCommands.Bundle.cs:155-156` uses `WorkspaceSetup.LocalStore`, and `ScriniaCommands.Migration.cs`'s caller of `CompactEntryToSingleChunk` passes it.

**Expect compile errors in files this plan does not name.** F-7's list is a starting point, not a bound — an earlier draft claimed it was exhaustive and was wrong twice over (F-8, F-19). The compiler produces the real list. For each error, apply this rule, in order:

1. **Is the receiver already a concrete `FileMemoryStore`?** Then it should not be erroring; re-read it.
2. **Is the whole operation local-only?** Retype the parameter or local to `ILocalMemoryStore`. Preferred.
3. **Is it a test using `MemoryStoreContext.Current!`?** Use the helper from Step 5a. Do not write an inline cast.
4. **Does it need a cast at a genuine remote/local boundary?** Stop and report it. That is a finding — it means a member is on the wrong interface, or a real operation is about to start throwing where it used to work.

Never silence an error with `(ILocalMemoryStore)` at a site where the store could legitimately be remote. That converts a compile-time guarantee back into the runtime trap this branch exists to remove.

- [ ] **Step 5a: Add the test-side narrowing helper before fixing any test**

Per F-19, roughly ten test files reach layout members through `var store = MemoryStoreContext.Current!;`, whose static type is `IMemoryStore?`. `TestHelpers.StoreScope` always installs a `FileMemoryStore` (`TestHelpers.cs:111`), so in those tests the narrowing is always valid — it just needs saying once.

In `tests/Scrinia.Tests/TestHelpers.cs`, beside `StoreScope`:
```csharp
/// <summary>The ambient store, narrowed to the local contract. StoreScope always installs a
/// FileMemoryStore, so this is total inside a StoreScope. Exists so that tests reaching a
/// path member do so through one named helper rather than ~20 inline casts.</summary>
internal static ILocalMemoryStore LocalStore =>
    MemoryStoreContext.Current as ILocalMemoryStore
    ?? throw new InvalidOperationException(
        "No local store is installed. Is this test inside a StoreScope?");
```
Then `MemoryStoreContext.Current!.ArtifactPath(x)` → `TestHelpers.LocalStore.ArtifactPath(x)`, and `var store = MemoryStoreContext.Current!;` → `var store = TestHelpers.LocalStore;`.

**The eight files that break, with the lines that fail** (verified by the verification pass; `Scrinia.Server.Tests` and `Scrinia.Plugin.Embeddings.Tests` are unaffected — their `_store` fields are concrete `FileMemoryStore`):

| File | Failing lines | `store` declared at |
|---|---|---|
| `AgentFileEntityTests.cs` | `:31, 34, 83, 89` | `:26`, `:80` |
| `BundleImportCategoryAllowlistTests.cs` | `:106` | `:90` |
| `ConsolidateReembedTests.cs` | `:73, 94` | `:54` |
| `ConsolidateSingleStoreTests.cs` | `:35` | `:15` |
| `ScriniaMcpToolsTests.cs` | `:562, 669` | inline `MemoryStoreContext.Current!` |
| `SkillPathAuthorityTests.cs` | `:26, 40` | `:21`, `:36`, `:49` |
| `StorePurityInvariantTests.cs` | `:27, 40, 41, 66` | `:21`, `:38`, `:61` |
| `WorkspaceRootUnificationTests.cs` | `:21, 24, 34, 37` | `:19`, `:32` |

`BundleV2Tests.cs:108` also calls a private helper `ImportFromBytes(byte[], IMemoryStore store, …)` that forwards to `ImportTopicsFromZip`; retype that helper's parameter.

This table is a starting point like F-7 — **run the build and fix what it reports**, do not assume the table is complete.

`HybridScorerTests.cs`'s `FakeMemoryStore` (`:256`) declares all six layout members (`:274,276,277,278,285,286`) plus `SaveIndex` (`:264`) — delete all eight from it, not just the two named earlier in this plan.

**Three of Step 5's production edits are deliberately temporary, and Task 3 revisits them.** `Remember` (`CurrentLayout`), `BuildFullList` (`ILocalMemoryStore` parameter) and `Recall`'s `/agent/` narrow are not local-only operations — they merely *touch* a local-only member on one branch. Task 2 gives them the widest shape that preserves today's behaviour exactly, so the mechanical wave carries no behaviour claim; Task 3 narrows the touch instead of the method. This churn is the price of keeping the two diffs separately reviewable. Do not "fix" it early.

- [ ] **Step 6: Run the property test and watch it turn green**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~StoreBoundaryTests"`
Expected: both PASS. The first now finds exactly one throw site, at the `file://` line.

- [ ] **Step 7: Full build and the four suites**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj
rtk dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj
rtk dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj
```
Expected: 0 errors, 0 warnings, all green. Record every count.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Core/ILocalMemoryStore.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/Bundles/BundleFormatService.cs src/Scrinia/HttpMemoryStore.cs src/Scrinia.Mcp/ src/Scrinia/Commands/ tests/Scrinia.Tests/StoreBoundaryTests.cs tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs
git commit -m "refactor(core): split IMemoryStore at the line where remote mode throws

Six members that HttpMemoryStore could only implement by throwing move to
ILocalMemoryStore. SaveIndex leaves the interface entirely — it had no
production caller outside the store implementations."
```

---

## Task 3: Turn the remaining runtime traps into answers

**This is the branch's only behaviour change.** Each item below is a defect that exists at `c5cbb21` and each gets a red check first.

**Files:**
- Modify: `src/Scrinia.Mcp/McpResponse.cs` (add `ErrorCodes.NotSupported`)
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` (add `TryGetLayout`)
- Modify: `src/Scrinia.Mcp/MemoryTools.{Bundling,Lifecycle,Skills,Core}.cs`
- Modify: `tests/Scrinia.Tests/StoreBoundaryTests.cs` (add the remote-mode cases)

**Interfaces:**
- Consumes: `ILocalMemoryStore` and `CurrentLayout` from Task 2.
- Produces: `ScriniaMcpTools.TryGetLayout(out ILocalMemoryStore layout) → bool`.

- [ ] **Step 1: Add the error code**

In `src/Scrinia.Mcp/McpResponse.cs`, after `Internal` (`:28`):
```csharp
/// <summary>The operation needs a capability the active store does not have — in practice,
/// local filesystem access against a remote store.</summary>
public const string NotSupported = "NOT_SUPPORTED";
```

- [ ] **Step 2: Add the non-throwing accessor**

In `src/Scrinia.Mcp/MemoryTools.cs`, beside `CurrentLayout`:
```csharp
/// <summary>Non-throwing counterpart to <see cref="CurrentLayout"/>, for tool entry points
/// that should answer with an error response rather than propagate an exception.</summary>
private static bool TryGetLayout([NotNullWhen(true)] out ILocalMemoryStore? layout)
{
    layout = MemoryStoreContext.Current as ILocalMemoryStore;
    return layout is not null;
}
```
Requires `using System.Diagnostics.CodeAnalysis;`. The nullable annotation is not decoration: without `[NotNullWhen(true)]` every caller's success branch needs a `!`, and with a non-nullable `out` the false branch holds a typed null.

- [ ] **Step 3: Write the red checks for the local-only tools**

Add to `tests/Scrinia.Tests/StoreBoundaryTests.cs`. The test double is a hand-written class — **no mocking frameworks** (repo-wide rule). It implements `IMemoryStore` only, which is the whole point: after Task 2 the compiler will not let it be mistaken for a local store.

**Do not hand-write two full doubles.** `IMemoryStore` declares 30 required members today; after Tasks 1 and 2 remove 8, each double would need **22 member bodies** — 44 across two classes. Instead, extract the double that already exists.

`tests/Scrinia.Tests/Embeddings/HybridScorerTests.cs:256` holds `private sealed class FakeMemoryStore : IMemoryStore` with all 30 implemented. Task 2 already opens that file (to delete its `SaveIndex` stub at `:264`, its preview stub at `:279`, and its six layout stubs at `:274,276,277,278,285,286`). Finish the job here:

1. Move it to its own file under `tests/Scrinia.Tests/`, as `internal class FakeMemoryStore : IMemoryStore` — not `sealed`, not nested — with `virtual` members.
2. `HybridScorerTests` uses it unchanged.
3. `RemoteOnlyStore` and `RecordingRemoteStore` become subclasses overriding only what each needs — roughly six members between them instead of 44.

If moving it turns out to perturb `HybridScorerTests` (it currently sees the test class's private state, if any), say so in the report and fall back to hand-writing — but check first.

```csharp
/// <summary>An IMemoryStore with no local filesystem — the shape HttpMemoryStore has after
/// the split. Overrides nothing: these tests assert the tools refuse before touching it.</summary>
private sealed class RemoteOnlyStore : FakeMemoryStore { }

[Fact]
public async Task BundleExport_AgainstRemoteStore_ReturnsErrorNotException()
{
    // StoreScope's constructor installs a FileMemoryStore (TestHelpers.cs:111); overwrite
    // Current after it, so the clean-room setup (sink cleared, session budget overridden)
    // still applies and Dispose still nulls Current. StoreScope does NOT take a store —
    // its only parameter is `bool createStoreDir`.
    using var scope = new TestHelpers.StoreScope();
    MemoryStoreContext.Current = new RemoteOnlyStore();

    var response = await Tools().Export(topics: ["api"], cancellationToken: default);

    response.Status.Should().Be("error");
    response.ErrorCode.Should().Be(ErrorCodes.NotSupported);
}
```

`Status` and `ErrorCode` are the real property names — `McpResponse` is a record declared at `McpResponse.cs:35`, with `Status` at `:37` and `ErrorCode` at `:46`.

**Drive `Export`/`Import`, not `Bundle`.** An earlier draft called `Tools().Bundle("export", …)` and asserted `.Status` on the result. That does not compile: `Bundle` is `public async Task<string>` (`MemoryTools.Lifecycle.cs:27`) — it returns *rendered YAML*, deliberately (its own remarks explain why). `Export` (`Bundling.cs:19`) and `Import` (`:73`) are `internal Task<McpResponse>` and are what these tests must call. `Scrinia.Mcp` declares `InternalsVisibleTo` for `Scrinia.Tests`, so `internal` is reachable.

Write the equivalent case for each guarded entry: `Export`, `Import`, `Reconcile`, `SkillCreate`, `SkillLoad`, and the restore path (see Step 4a).

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~StoreBoundaryTests"`
Expected: every new case FAILS with `NotSupportedException` escaping from `CurrentLayout`, not with an assertion failure on the status. **Record the exception message observed.** A case that fails on the assertion instead means the tool never reached the store — investigate before "fixing" it.

- [ ] **Step 4: Guard the local-only tools**

Replace `var store = CurrentLayout;` with a guard. **The return form differs per method — an earlier draft gave one form for all six and was wrong for the async ones.** Verified signatures:

| Method | Location | Async? | Guard return |
|---|---|---|---|
| `Export` | `Bundling.cs:19` | no | `return Task.FromResult(ResponseBuilder.Error(…));` |
| `Import` | `Bundling.cs:73` | no | `return Task.FromResult(…);` |
| `Reconcile` | `Lifecycle.cs:67` | no | `return Task.FromResult(…);` |
| `SkillLoad` | `Skills.cs:229` | no | `return Task.FromResult(…);` |
| `SkillCreate` | `Skills.cs:120` | **yes** | `return ResponseBuilder.Error(…);` — **no** `Task.FromResult` |
| `Restore` | `Lifecycle.cs:591` | **yes** | `return ResponseBuilder.Error(…);` |

```csharp
if (!TryGetLayout(out var store))
    return Task.FromResult(ResponseBuilder.Error(
        "bundle export needs a local workspace; the active store is remote.",
        ErrorCodes.NotSupported,
        "run this against a local store, or use the server's export endpoint"));
```

**The third argument is `params string[] recovery`, not a hint string.** `ResponseBuilder.Error(string error, string code, params string[] recovery)` lives at `McpResponse.cs:81` and populates `ActionNeeded`. The 3-arg call compiles, but write the string as an action the caller can take, not as explanatory prose. For export and import there is a real alternative to name — the deleted `HttpMemoryStore` members carried it at `:612` and `:616` ("Use export endpoint" / "Use import endpoint").

- [ ] **Step 4a: Decide what `guide()` does in remote mode**

`BuildRestoreContextAsync` returns `RestoreContext`, not `McpResponse`, so it cannot carry the guard itself. It has **two** callers, not one:
- `Restore` (`MemoryTools.Lifecycle.cs:593`)
- **`Guide` (`MemoryTools.cs:54`)**

An earlier draft named only `Restore`. Guarding just it leaves `guide()` propagating `NotSupportedException` in remote mode — and `guide()` is the tool this workspace's `CLAUDE.md` instructs agents to call at session start, so it is the worst possible place to leave a raw exception.

**This choice is now settled — see F-10a. Take option (b); the other two are regressions.**

Originally this step offered a three-way choice to be decided by reading. The reading has since been done, during Task 2's review: all three `WorkspaceRoot` reads sit inside swallowing `try`/`catch` blocks, so `restore()` and `guide()` **already work remotely in a degraded form**. Giving `BuildRestoreContextAsync` a degraded path is therefore not a design decision at all — it restores what the code did before the split. Making `guide()` return `NOT_SUPPORTED` would break the tool this workspace tells agents to call first.

Narrow inside each of the three `try` blocks so a remote store skips that section exactly as the swallowed exception used to make it skip. Add tests driving both `guide()` and `restore()` against `RemoteOnlyStore`.

Run the new cases: expected PASS.

- [ ] **Step 5: Fix `remember()` in remote mode (F-10)**

Red check first:
```csharp
[Fact]
public async Task Remember_AgainstRemoteStore_DoesNotNeedWorkspaceRoot()
{
    // A remember() with no `file` attributes needs no workspace root at all. Before this
    // fix, MemoryTools.Core.cs:354 read store.WorkspaceRoot unconditionally, so every
    // remote remember threw — see plan fact F-10.
    using var scope = new TestHelpers.StoreScope();
    MemoryStoreContext.Current = new RecordingRemoteStore();

    var response = await Tools().Remember(
        content: ["a note with no file references"],
        name: "/api/note",
        cancellationToken: default);

    response.Status.Should().Be("success");
}
```
`RecordingRemoteStore` is a second hand-written double: an `IMemoryStore` whose `Upsert` / `WriteArtifactAsync` / `LoadIndex` succeed as no-ops, so `Remember` can reach the end. It is **not** an `ILocalMemoryStore`, so any code path that needs a workspace root cannot compile against it — which is the point.

Read `Remember`'s real parameter list at `MemoryTools.Core.cs:109` before writing the call; this plan did not pin it.

Expected pre-fix: `NotSupportedException` from `WorkspaceRoot` — except that after Task 2 the member no longer exists on `IMemoryStore`, so what the pre-fix state actually produces is whatever Task 2's `CurrentLayout` does: a `NotSupportedException` from the accessor. **Either way, record the exact exception and message observed.** If the call succeeds before the fix, F-10 is wrong — stop and report, because the fix's entire justification rests on it.

Then change `MemoryTools.Core.cs:354` from an unconditional read to one that happens only when there is something to hash, mirroring the shape `BuildFullList:723-726` already uses:
The existing code is:
```csharp
string workspaceRoot = store.WorkspaceRoot;                              // :354

var allRefPaths = (entryAttributes is not null && entryAttributes.TryGetValue("file", out var fileRefs)
        ? fileRefs : [])
    .Distinct(StringComparer.OrdinalIgnoreCase);                          // :356

Dictionary<string, string>? codeRefDict = null;
foreach (var refPath in allRefPaths)                                      // :361
```

Reorder so the root is resolved only when there is something to hash. **Keep the name `allRefPaths`** — an earlier draft of this plan introduced a new `refPaths` and left `:361`'s loop referencing `allRefPaths`, which is CS0103:
```csharp
var allRefPaths = (entryAttributes is not null && entryAttributes.TryGetValue("file", out var fileRefs)
        ? fileRefs : [])
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

// Drift detection hashes files on disk, so it needs a local workspace. A remote store has
// none; the memory is stored without file hashes rather than the whole call failing.
string? workspaceRoot = allRefPaths.Count > 0
    ? (store as ILocalMemoryStore)?.WorkspaceRoot
    : null;
```
The `.ToList()` is required — `allRefPaths` is now read twice (`.Count` and the loop) and `Distinct` is lazy.
`store` is `CurrentLayout` after Task 2 — revert that method to `CurrentStore` and narrow here, since `Remember` is no longer local-only. Its `/agent/` and `/skill/` write branches (`:262-277`) **are** local-only and take the Step 4 guard.

Follow the downstream uses of `workspaceRoot` (`ResolveWorkspacePath` at `:1392`, `ComputeFileHash` at `:1398`) and skip hashing when it is null. Do not fabricate a root.

- [ ] **Step 6: Cover the two narrowed optional sites**

`Recall`'s `/agent/` branch (narrowed in Task 2 Step 5) and `BuildFullList`'s drift column both now degrade instead of throwing. Add one test each:
- `recall('/agent/x')` against a remote store falls through to NMP/2 resolution and returns whatever that path returns — no exception.
- `list()` against a remote store whose entries carry `FileHashes` returns the listing with no drift column, no exception.

For `BuildFullList`, Task 2 retyped its parameter to `ILocalMemoryStore`; revert that to `IMemoryStore` and narrow at `:726` instead — `list()` is not a local-only tool:
```csharp
if (anyCodeRefs)
    workspaceRoot = (store as ILocalMemoryStore)?.WorkspaceRoot;
```

`BuildDriftList` (`:1337`) stays `ILocalMemoryStore` — drift detection genuinely is local-only. **Its guard goes on the drift branch, not on `List`.** An earlier draft said "its caller takes the Step 4 guard", which contradicted this same step's requirement that remote `list()` succeed: `List` is the caller, and guarding its entry would refuse every remote listing. Revert `:588` to `var store = CurrentStore;` and guard only the drift branch at `:590-591`:
```csharp
if (string.Equals(mode, "drift", StringComparison.OrdinalIgnoreCase))
{
    if (!TryGetLayout(out var localStore))
        return Task.FromResult(ResponseBuilder.Error(
            "drift detection needs a local workspace; the active store is remote.",
            ErrorCodes.NotSupported,
            "run list() without mode='drift' for a plain listing"));
    return BuildDriftList(localStore);
}
```
So `list()` works remotely, `list(mode: 'drift')` returns a clean refusal, and the drift column is simply absent from a remote plain listing. Add a test for each of the three.

- [ ] **Step 7: Build and run all four suites, then commit**

```bash
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj
rtk dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj
rtk dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj
```

```bash
git add src/Scrinia.Mcp/ tests/Scrinia.Tests/StoreBoundaryTests.cs
git commit -m "fix(mcp): answer instead of throwing when a tool needs local files

remember() read WorkspaceRoot unconditionally, so every remember against a
remote store threw. Local-only tools now return NOT_SUPPORTED; tools that
merely prefer a local store degrade."
```

---

## Task 3a: The server's bundle endpoints (F-8)

**Added 2026-08-07 by the verification pass.** An earlier draft of this plan asserted that `Scrinia.Server` did not change at all. It does: retyping `BundleFormatService` per F-13 breaks `BundleService`, which breaks two endpoints. This task exists because that claim was wrong, and it is separated from Task 3 so the MCP-side and HTTP-side guards can be judged independently.

**Files:**
- Modify: `src/Scrinia.Server/Services/BundleService.cs:13,24`
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:346,391`
- Modify: `tests/Scrinia.Server.Tests/` — one endpoint test per branch

**Interfaces:**
- Consumes: `ILocalMemoryStore`, and `BundleFormatService`'s retyped signatures from Task 2.

- [ ] **Step 1: Retype `BundleService`**

`ExportToStream(ILocalMemoryStore store, string[] topics)` and `ImportFromStream(ILocalMemoryStore store, Stream bundle, string[]? topics, bool overwrite)`. Bodies unchanged — they only forward.

- [ ] **Step 2: Narrow at the endpoints, and do NOT narrow `StoreManager`**

`MemoryEndpoints.cs:346` and `:391` pass `ctx.Store!`, typed `IMemoryStore?` (`RequestContext.cs:15`). Narrow there:
```csharp
if (ctx.Store is not ILocalMemoryStore localStore)
    return Results.Problem(
        detail: "Bundle export requires a store backed by a local filesystem.",
        statusCode: StatusCodes.Status501NotImplemented);
```

**Do not "fix" this by changing `StoreManager.GetStore`'s return type to `ILocalMemoryStore`.** It looks like the tidier fix and it is the wrong one: `GetStore` (`StoreManager.cs:27`) delegates to `IStorageBackend.CreateStore` (`:33`), and `Program.cs:92-95` registers `FilesystemBackend` behind a comment stating that plugin DI runs first **so plugins can replace `IStorageBackend`**. Narrowing the return type would oblige every future backend to expose local filesystem paths — closing a deliberate seam, on a branch whose mandate is to stop conflating local-only capability with the shared contract. Match the shape to the fact: the *server* can host a non-local backend, so the *endpoint* is where the check belongs.

Check the surrounding handlers' actual return type before writing the guard — if they return `IResult`, the above fits; if they return a typed result, adapt and say so.

- [ ] **Step 3: Test both branches**

Two tests in `Scrinia.Server.Tests`: a local-backend export returns 200 (this should already pass — it is the regression guard), and an export against a `RequestContext` whose `Store` is not an `ILocalMemoryStore` returns 501. For the second, a hand-written non-local `IMemoryStore` double is needed — check whether `StoreConformanceFixture` or another file already has one before writing a third.

Red check: the 501 test cannot be written before Step 2 exists, so instead assert the *current* behaviour first — that the endpoint with a non-local store fails with an unhandled exception rather than a 501 — and record what you observe.

- [ ] **Step 4: Build, run `Scrinia.Server.Tests`, commit**

```bash
git add src/Scrinia.Server/ tests/Scrinia.Server.Tests/
git commit -m "fix(server): answer 501 when a bundle endpoint needs a local store"
```

---

## Task 4: One install point for the default event sink (F4)

**Files:**
- Modify: `src/Scrinia.Core/IMemoryEventSink.cs` (add `InstallDefault` to `MemoryEventSinkContext`, `:24`)
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs:231,391,409,418,438,519`
- Create/modify: a test file under `tests/Scrinia.Tests/` for the composition assertion

**Interfaces:**
- Produces: `MemoryEventSinkContext.InstallDefault(params IMemoryEventSink[] sinks)`.

**Out of scope, per the spec:** `Default` itself, the AsyncLocal fallback, `StoreScope`'s clear, and `BackgroundWork`. **No new event-sink lifetime tests, and specifically no per-test drain** — a drain in `StoreScope.Dispose` was measured taking the suite from 52s to over 10 minutes via sync-over-async thread-pool starvation. That trap is on record at `/findings/event-sink-throw-crashes-test-host`; do not reopen it.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void InstallDefault_ComposesSinksInOrder()
{
    var a = new RecordingSink();
    var b = new RecordingSink();
    MemoryEventSinkContext.InstallDefault(a, b);

    MemoryEventSinkContext.Default.Should().BeOfType<CompositeEventSink>();
    // Assert both sinks receive an event, in the order given.
}
```
`RecordingSink` is a hand-written `IMemoryEventSink` that appends to a list — no mocking frameworks. Restore `MemoryEventSinkContext.Default` to its prior value in a `finally`; this is process-wide static state and the suite runs in parallel. If that proves flaky, mark the class `[Collection]`-serialized rather than leaving a race — and say so in the report.

Run: expected FAIL, "InstallDefault does not exist".

- [ ] **Step 2: Add `InstallDefault`**

```csharp
/// <summary>Installs the process-wide default sink as a composite over <paramref name="sinks"/>.
/// The one place <see cref="Default"/> is assigned from — six call sites in WorkspaceSetup each
/// built their own CompositeEventSink, so a change to how the default is composed had six
/// places to miss.</summary>
public static void InstallDefault(params IMemoryEventSink[] sinks) =>
    Default = new CompositeEventSink(sinks);
```

Run: expected PASS.

- [ ] **Step 3: Route the six sites**

At `WorkspaceSetup.cs:231`, `:409`, `:418`: `MemoryEventSinkContext.Default = new CompositeEventSink([new MaintenanceEventSink()]);` → `MemoryEventSinkContext.InstallDefault(new MaintenanceEventSink());`

At `:391`, `:438`, `:519`: same transformation, preserving each site's sink list **in order** — `:519` is `[host, new MaintenanceEventSink()]`, and order is observable. Read each site before editing; do not assume the three multi-sink lists are identical.

- [ ] **Step 4: Build, run `Scrinia.Tests` and `Scrinia.Server.Tests`, commit**

```bash
git add src/Scrinia.Core/IMemoryEventSink.cs src/Scrinia/Commands/WorkspaceSetup.cs tests/
git commit -m "refactor(core): install the default event sink from one place"
```

---

## Task 5: One vocabulary (F6)

Rename the server's internal handler methods to the MCP vocabulary, which is the published one. **Route paths and the HTTP contract do not change** — this is internal naming only. Per F-16 the rename is confined to `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs`; no test references these names.

**CORRECTED 2026-08-07 — SPEC CLAIM FALSIFIED, verified independently by the controller during Task 5.** "Internal naming only; route paths and the HTTP contract do not change" is FALSE when OpenAPI is exposed. `Program.cs:252` calls `AddOpenApi()`; `Program.cs:517` calls `MapOpenApi()`, gated on Development or `Scrinia:ExposeOpenApi=true`; `tests/Scrinia.Server.Tests/OpenApiTests.cs` covers it. There are ZERO `.WithName(` calls anywhere in `src/`, so Minimal API derives each endpoint's OpenAPI `operationId` from the handler method name. Renaming the five handlers below therefore changes five published operationIds — this is the third spec/plan fact this branch falsified (after F-7 completeness and F-8 server-unchanged), and Step 1's own caveat at the bottom of this task is what caught it. A CHANGELOG entry was added as a result (see `CHANGELOG.md`, "server handler names now match the published MCP tool vocabulary").

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs`
- Modify: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs:10-25`

- [ ] **Step 1: Rename the handlers**

| Current | New | Registration | Declaration |
|---|---|---|---|
| `StoreMemory` | `RememberMemory` | `:22` | `:43` |
| `ListMemories` | `ListMemory` | `:23` | `:79` |
| `ShowMemory` | `RecallMemory` | `:24` | `:120` |
| `SearchMemories` | `SearchMemory` | `:29` | `:250` |
| `SearchMemoriesPost` | `SearchMemoryPost` | `:30` | `:262` |

The last row is not in the spec — `SearchMemoriesPost` was added by the `search-contract` branch after the spec was written (F-16). It is renamed for the same reason as its GET sibling, preserving the existing `…Post` suffix convention. None of the five new names collides with an existing member in the file (`ForgetMemory`, `AppendMemory`, `CopyMemory`, `GetChunk`, `RunSearch`, `ExportTopics`, `ImportBundle` are all distinct).

**Before committing, check one thing the "internal naming only" claim depends on.** ASP.NET Core Minimal API derives an endpoint's `DisplayName` — and, where OpenAPI generation is on, its `operationId` — from the handler method name. Grep for OpenAPI/Swagger generation and for anything consuming a generated document:
```bash
rtk grep -rn "AddOpenApi\|MapOpenApi\|Swagger\|operationId" --include=*.cs --include=*.json src/ tests/
```
If nothing generates or consumes a document, the claim holds as written. If something does, the rename changes a published identifier and belongs in the CHANGELOG — say which you found.

- [ ] **Step 2: Delete the stale tool inventory**

In `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`, delete lines `:11-21` — the inventory naming `Show` / `Store` / `Export` / `Import` as the tool surface, which has not been true since the tool consolidation. **Keep** `:22-24` ("All tests are offline…" and the `StoreScope` note): both are still accurate, and the spec's instruction to delete was aimed at the stale inventory, not at true statements. Reflow the remaining `<summary>` so it reads as a paragraph.

- [ ] **Step 3: Build, run `Scrinia.Server.Tests` and `Scrinia.Tests`, commit**

```bash
git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -m "refactor(server): name the handlers after the published tool vocabulary"
```

---

## Task 6: Documentation, changelog, residuals

**Files:**
- Modify: `docs/architecture/core.md`, plus any other **live** architecture doc that names a moved member
- Modify: `CHANGELOG.md`
- Write: `.scrinia` memory at `/findings/store-boundaries-deferred-followups`

- [ ] **Step 1: Update the live docs**

```bash
rtk grep -rln "IMemoryStore\|GenerateContentPreview\|WorkspaceRoot" docs/architecture/ docs/plugin-authoring.md
```
Known hits: `docs/architecture/{cli,core,overview,server,embeddings}.md`, `docs/plugin-authoring.md`. Read each hit and correct only what is now false — the interface split, the two members that left it, the handler renames.

**Do not touch `docs/planning/`.** Those are historical plans and specs; rewriting them falsifies the record. Same for `docs/superpowers/specs/` and prior plans.

- [ ] **Step 2: CHANGELOG**

Under `[Unreleased]`, three entries. State plainly that `remember` against a remote store previously threw — a user-visible fix, not an internal refactor. Note that local-only MCP tools now return a `NOT_SUPPORTED` error response where they previously propagated an exception, since anything parsing tool errors sees a new code.

- [ ] **Step 3: Record residuals**

Write every deferral this branch made to `/findings/store-boundaries-deferred-followups` via the Scrinia `remember` tool, one chunk per item, following the shape of the four existing `*-deferred-followups` memories. `.scrinia/` changes are part of the commit.

Known entries before implementation even starts:
- `HttpMemoryStore`'s fabricated result metadata (`OriginalBytes: 0`, `CreatedAt: DateTimeOffset.UtcNow`) — spec's Out of Scope, recorded as a known limitation.
- The new-client/older-server capability gap inherited from `search-contract`: a new client's filtered search against an older server silently returns unfiltered results. This branch's split does not address it.
- `IMemoryStore.GetResultScope` — dead code stranded by the `SearchResultFilter.Matches` deletion on `search-contract`. **Consider deleting it in Task 2** while the interface is already open; if you do, record that instead of deferring it.

- [ ] **Step 4: Full gate and commit**

```bash
rtk dotnet build Scrinia.sln -c Debug --no-incremental
rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj
rtk dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj
rtk dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj
```
Record every count. Then stage explicit paths (**never `git add .`**) and commit.

---

## Risks

**The Task 2 mechanical wave is one large commit that cannot be partially landed.** Removing members from an interface breaks the repo until every call site is fixed. Mitigations: the property test lands first and is observed red; Task 1 removes the largest mechanical piece from this diff; the exhaustive call-site list in F-7 and Step 5 means the compiler's error list should match the plan's list, and any site not on it is a reportable finding.

**Task 3 changes behaviour in six tools at once.** Mitigation: every case is red-checked individually and the fixes are separable — if one guard is wrong, it does not implicate the others.

**The plan's own completeness is the risk the verification pass kept finding.** Three separate claims of exhaustiveness were false (F-7, F-8, and Task 2's step list, which omitted `MemoryTools.Core.cs:588` — a site no member-name grep could have found, since `WorkspaceRoot` never appears in `List`). All are corrected above, but the class of error should be assumed to persist. Task 2's build **will** report errors in files this plan does not name; that is expected, and Step 5's four-rule ladder says how to resolve each.

**F-10 is the plan's most consequential factual claim** — that `remember()` is broken in remote mode today. It is traced (`Serve.cs:62` → `MemoryStoreContext.Current` → `MemoryTools.cs:16` → `MemoryTools.Core.cs:354` → `HttpMemoryStore.cs:507`) but not yet executed. Task 3 Step 5's red check is the execution. **If the red check does not reproduce the throw, stop and report** — the claim is wrong and the fix's justification with it.

## Out of scope

- `ArtifactUri`'s remote fabrication and the wire-format widening it would take to fix (recorded as a residual).
- Event-sink lifetime work of any kind, including per-test drains (measured trap on record).
- The `[Unreleased]` CHANGELOG sweep, tracked separately.
- `ScriniaCommands.Migration.cs:341`'s `is not FileMemoryStore` downcast — it exists for `internal` migration members, not path members, and survives the split (spec fact 17).
