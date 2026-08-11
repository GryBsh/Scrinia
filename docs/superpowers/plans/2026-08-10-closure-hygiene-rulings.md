# Closure: Group F + Group D + E1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the first ready block of `docs/superpowers/specs/2026-08-10-residuals-closure.md` — make "matched nothing" fail visibly (E1), do the five hygiene fixes (Group D), and retire the six standing items (Group F) by fixing the cheap ones and writing the rulings for the rest.

**Architecture:** Three independent strands that share one branch. E1 adds a single typed predicate (`McpResponse.IsWarning`) and routes all four CLI envelope-print sites plus the one HTTP site through it, so a non-success envelope stops rendering as success. Group D is mechanical dead-code and redundant-I/O removal. Group F writes each standing decision into the file a future reader will open, except where fixing is cheaper than explaining — two of the six are.

**Tech Stack:** C#/.NET 10, xunit 2.9.3, FluentAssertions 6.12.2, Spectre.Console (CLI), ASP.NET Core minimal APIs (server). No mocking frameworks — hand-written test doubles only.

## Global Constraints

- **Never `git add .` or `git add -A`.** `cmds.txt` is a pre-existing untracked file at the repo root that is not part of this work. Stage explicit paths on every commit. (Task 3 adds a `.gitignore` rule for it; the constraint holds regardless.)
- **Nothing is pushed.** Local commits only.
- Prefix every shell command with `rtk`, including inside `&&` chains.
- `TreatWarningsAsErrors=true` for all non-`.Tests`/`.Benchmarks` projects (`Directory.Build.props:44-46`). Test projects carry ~1723 pre-existing analyzer warnings — **the gate is 0 errors, not 0 warnings.**
- The C# language server emits phantom `CS0101`/`CS1061` errors against deleted or cached files. **Verify against `rtk dotnet build Scrinia.sln` only**, never the diagnostic stream.
- `rtk grep` silently undercounts. Use the `Grep` tool or `rtk proxy grep --include=*.cs`. A bare recursive grep over `src/` matches compiled DLLs under `bin/`/`obj/`.
- `tests/Scrinia.Tests/GlobalUsings.cs` has `global using Xunit;`. `tests/Scrinia.Server.Tests` has none until Task 6 adds one.
- `TestHelpers.LocalStore` (`tests/Scrinia.Tests/TestHelpers.cs:83`) returns `ILocalMemoryStore`, not `FileMemoryStore`. `TestHelpers.StoreScope` (`:96`) is `IDisposable` with `WorkspaceDir`/`TempDir`.
- **This branch does not get to write new deferred-followups.** Anything found mid-branch is fixed here, or raised as an explicit decision at the time. A finding that genuinely cannot be closed is a stop-and-ask, not a `/findings/` note.

---

## File Structure

| File | Change | Task |
|---|---|---|
| `src/Scrinia.Mcp/McpResponse.cs` | Add `IsWarning` predicate beside `IsError` | 1 |
| `src/Scrinia/Commands/ScriniaCommands.cs` | Add `RenderEnvelope` helper; make `EmitMcpResult` (`:64-81`) warning-aware | 1 |
| `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` | Route `:62`, `:101` through the helper | 1 |
| `src/Scrinia/Commands/ScriniaCommands.Memory.cs` | Route `:396`, `:479` through the helper; add the missing `IsError` guard to `Store` | 1 |
| `tests/Scrinia.Tests/BundleExitCodeTests.cs` | Invert `Import_OnEmptyBundle_ExitsZero`, rewrite its doc comment as the ruling | 1 |
| `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` | `:436-437` stops returning 200 | 2 |
| `tests/Scrinia.Server.Tests/BundleEndpointTests.cs` | Pin the new status code | 2 |
| `CHANGELOG.md` | Breaking-change entry covering all three E1 surfaces | 2 |
| `.gitignore` | Ignore `/cmds.txt` | 3 |
| `src/Scrinia.Core/FileMemoryStore.cs` | Drop `using System.Linq;` (Task 3); delete `CachedIndex` dead members (Task 4) | 3, 4 |
| `src/Scrinia.Core/FileLock.cs` | Move `Directory.CreateDirectory` off the shared-lock path | 3 |
| `tests/Scrinia.Tests/FileLockTests.cs` (create if absent) | Pin that `AcquireExclusive` still creates the directory | 3 |
| `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` | Remove the citation of a spec that never existed | 3 |
| `docs/architecture/core.md` | Rewrite the `CachedIndex` section for the deletion | 4 |
| `docs/architecture/cli.md`, `AGENTS.md` | Remove references to the deleted `ScriniaArtifactStore` | 5 |
| `tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs` | F1 — use `IsError` (fix, not ruling) | 6 |
| `tests/Scrinia.Server.Tests/GlobalUsings.cs` (create) + 30 test files | F4 — add global using, strip per-file (fix, not ruling) | 6 |
| `src/Scrinia/HttpMemoryStore.cs` | F2 and F5 rulings as comments | 6 |
| `src/Scrinia.Core/Search/AttributeExtractor.cs` | F6 ruling as a comment | 6 |
| `docs/architecture/cli.md` | F3 ruling — the CLI/HTTP bundle-filter divergence | 6 |
| `docs/superpowers/specs/2026-08-10-residuals-closure.md` | Strike the closed groups | 6 |

---

## Two classification changes from the spec, made deliberately

The closure spec put **F1** and **F4** in "rule closed, do not carry". Both are cheaper to fix than to explain, and fixing removes them permanently rather than leaving a comment a future reader must re-evaluate:

- **F1** — `McpResponse.IsError` is `public` (`src/Scrinia.Mcp/McpResponse.cs:48`) and reachable from the benchmark file. Using it is a two-line change; writing a paragraph on why we don't is more code than the fix.
- **F4** — adding `tests/Scrinia.Server.Tests/GlobalUsings.cs` and stripping 30 redundant `using Xunit;` lines makes the two test projects consistent, which is what kept regenerating the item. The build verifies it exhaustively: a missed line is merely redundant, a wrongly-removed one is a compile error.

The other four Group F items become written rulings as specified.

---

### Task 1: `IsWarning` — a non-success envelope stops rendering as success

**Files:**
- Modify: `src/Scrinia.Mcp/McpResponse.cs:48` (add `IsWarning` after `IsError`)
- Modify: `src/Scrinia/Commands/ScriniaCommands.cs:45-55` (add `RenderEnvelope`), `:64-81` (`EmitMcpResult`)
- Modify: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs:59-63`, `:98-102`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:386-398`, `:476-480`
- Test: `tests/Scrinia.Tests/BundleExitCodeTests.cs`

**Interfaces:**
- Produces: `McpResponse.IsWarning` (`bool`, `=> Status == "warning"`), and a private static CLI helper `int RenderEnvelope(McpResponse response, string yaml)` that writes the YAML in the status-appropriate colour and returns the process exit code. Task 2 relies on the ruling this task establishes but shares no code with it.

**Context an implementer cannot infer:**

There are exactly **three** `ResponseBuilder.Warning` call sites in the whole of `src/`, verified with the `Grep` tool:

- `src/Scrinia.Mcp/FactsResponseFormatter.cs:28` — an entity argument matched nothing
- `src/Scrinia.Mcp/MemoryTools.Core.cs:626` — "No changes specified"
- `src/Scrinia.Mcp/MemoryTools.Bundling.cs:124` — "No topics were imported"

All three are the same shape: *you asked for something and got nothing*. That is why this task generalises the rule to the `warning` status rather than special-casing bundle import — the ruling applies identically to all three, and a bundle-only fix would leave the other two lying about their outcome.

**There are FIVE print sites, not four.** The verification pass caught this: besides the four explicit `[green]{yaml}[/]` sites, `ScriniaCommands.cs:64-81` declares `EmitMcpResult`, the shared passthrough helper used by `scri facts`, `scri append`, `scri compact`, `scri link`, `scri restore` and `scri reconcile`. It computes `bool isError = r.IsError;` at `:66` and returns `isError ? 1 : 0` at `:80` — it never consults `IsWarning`. **`scri facts` is how `FactsResponseFormatter.cs:28` — one of the three motivating warning sites — reaches a user.** Skipping `EmitMcpResult` would fix bundle import while leaving two of the three sites still reporting success, which is precisely the failure this task's rationale argues against. Fix all five.

`EmitMcpResult` writes with `Console.Write`, not `AnsiConsole`, so it takes the exit-code half of the change only — do not add colour markup to a passthrough that callers pipe into parsers.

**Exit code: `1`, not a new code.** A distinct code for warnings (e.g. `2`) was considered and rejected — the ruling asks for "non-zero", and inventing an exit-code taxonomy is a design decision nobody requested. `1` is what every existing failure path returns.

`ScriniaCommands.Memory.cs`'s `Store` method (`:337-399`) **never checks `response.IsError` at all** — it returns `0` unconditionally. This is the same dead-guard defect already fixed at `Show` (`:421`), `Forget` (`:467`), `Export` (`:50`) and `Import` (`:89`); `Store` was missed. Fix it in this task: it is the identical class and the identical file.

- [ ] **Step 1: Write the failing tests**

In `tests/Scrinia.Tests/BundleExitCodeTests.cs`, **replace** `Import_OnEmptyBundle_ExitsZero` (currently at `:41-62`, including its doc comment) with the inverted test. The existing comment says *"If someone later decides an empty import is a failure, this test is the place that records it was a decision and not an accident."* This is that decision — record it here:

```csharp
    /// <summary>
    /// RULED 2026-08-10: an import that matched no topics is a failure, not a success.
    ///
    /// <para>This test previously asserted exit 0 and said, in as many words, that if someone
    /// later decided an empty import was a failure, this was the place to record it as a
    /// decision. That decision has now been taken: a caller that asked for topics and received
    /// none has had its request silently not-performed, and reporting exit 0 makes a CI step
    /// gating on it pass. The same ruling applies to every <c>warning</c> envelope — see
    /// <see cref="McpResponse.IsWarning"/>.</para>
    ///
    /// <para>Breaking for anything gating on exit 0; recorded in CHANGELOG.md.</para>
    /// </summary>
    [Fact]
    public async Task Import_OnEmptyBundle_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["auth flow doc"], "/api/auth");
        string exportContent = ResponseParser.Parse(
            (await new ScriniaMcpTools().Export(["api"])).ToYaml()).Content!;
        string bundlePath = exportContent[(exportContent.LastIndexOf(") to ", StringComparison.Ordinal) + 5)..];

        // Filter on a topic the bundle does not contain -> topicCount == 0, skippedCategories
        // empty -> ResponseBuilder.Warning("No topics were imported ...").
        int exit = await new ScriniaCommands().Import(
            bundlePath, workspaceRoot: scope.WorkspaceDir, topics: "does-not-match-anything");

        exit.Should().Be(1, "an import that matched nothing must not report success to a CI step");
    }
```

Add two more tests to the same file:

```csharp
    [Fact]
    public void IsWarning_IsTrueOnlyForTheWarningStatus()
    {
        ResponseBuilder.Warning("x").IsWarning.Should().BeTrue();
        ResponseBuilder.Success("x").IsWarning.Should().BeFalse();
        ResponseBuilder.Error("x").IsWarning.Should().BeFalse();
        ResponseBuilder.Warning("x").IsError.Should().BeFalse(
            because: "a warning is not an error — the two predicates must not overlap");
    }

    /// <summary>
    /// `scri store` had no IsError guard at all and returned 0 unconditionally — the same dead-guard
    /// defect already fixed at Show, Forget, Export and Import.
    /// </summary>
    [Fact]
    public async Task Store_OnGenuineFailure_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();
        string contentFile = Path.Combine(scope.TempDir, "content.txt");
        await File.WriteAllTextAsync(contentFile, "some content");

        // A space is not a valid FileEntityName character, so the /agent/ route rejects this with
        // ResponseBuilder.Error(..., INVALID_PARAMETER) at MemoryTools.Core.cs:273-274 — before the
        // ILocalMemoryStore check below it, so the verdict does not depend on the store mode.
        int exit = await new ScriniaCommands().Store(
            "/agent/bad name", contentFile, workspaceRoot: scope.WorkspaceDir);

        exit.Should().Be(1);
    }
```

The input is `/agent/bad name` deliberately. A first draft used `/skill/not-a-real-skill`; the verification pass established that `FileEntityName.IsValid` (`src/Scrinia.Core/FileEntityName.cs:12-17`) accepts that name, so `TryRouteSkillRemember` would simply **create** the skill and return success. Do not substitute a `/skill/` path back in. If `/agent/bad name` somehow does not error, report it and pick another guard from `MemoryTools.Core.cs` — never weaken the assertion to `0`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
rtk dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~BundleExitCodeTests"
```

Expected: `Import_OnEmptyBundle_ExitsNonZero` fails (actual 0), `IsWarning_...` fails to compile or fails, `Store_OnGenuineFailure_ExitsNonZero` fails (actual 0).

- [ ] **Step 3: Add the `IsWarning` predicate**

In `src/Scrinia.Mcp/McpResponse.cs`, directly after `IsError` (`:48`):

```csharp
    /// <summary>True when this response reports that the operation completed without doing what
    /// was asked — an empty import, an entity that matched nothing, a no-op update. Not an error:
    /// nothing failed. Not a success either: the caller's request was not performed, so a surface
    /// that renders this as success (green text, exit 0, HTTP 200) is lying to a CI step. Callers
    /// must treat it as a non-success outcome.</summary>
    public bool IsWarning => Status == "warning";
```

- [ ] **Step 4: Add the CLI rendering helper**

Put it beside the existing `FormatCliError` helper (find it with the `Grep` tool — it is used at `ScriniaCommands.Bundle.cs:53` and `Memory.cs:424`, so it lives in whichever `ScriniaCommands.*.cs` partial declares it; add the new helper to that same file).

```csharp
    /// <summary>
    /// Writes a response envelope in its status-appropriate colour and returns the process exit
    /// code. Every non-JSON envelope print goes through here: printing a warning in green and
    /// returning 0 reported "matched nothing" as success on four separate commands.
    /// </summary>
    private static int RenderEnvelope(McpResponse response, string yaml)
    {
        string colour = response.IsWarning ? "yellow" : "green";
        AnsiConsole.MarkupLine($"[{colour}]{Markup.Escape(yaml)}[/]");
        return response.IsWarning ? 1 : 0;
    }
```

- [ ] **Step 5: Route the four print sites through it**

`ScriniaCommands.Bundle.cs`, `Export` (replacing `:59-63`):

```csharp
        if (json)
        {
            WriteJson(new CliExportOutput("", result), CliJsonContext.Default.CliExportOutput);
            return response.IsWarning ? 1 : 0;
        }
        return RenderEnvelope(response, result);
```

`ScriniaCommands.Bundle.cs`, `Import` (replacing `:98-102`):

```csharp
        if (json)
        {
            WriteJson(new CliImportOutput(result), CliJsonContext.Default.CliImportOutput);
            return response.IsWarning ? 1 : 0;
        }
        return RenderEnvelope(response, result);
```

`ScriniaCommands.Memory.cs`, `Forget` (replacing `:476-480`):

```csharp
        if (json)
        {
            WriteJson(new CliForgetOutput(name, true, result), CliJsonContext.Default.CliForgetOutput);
            return response.IsWarning ? 1 : 0;
        }
        return RenderEnvelope(response, result);
```

`ScriniaCommands.Memory.cs`, `Store` — this one also gains the missing error guard. Replace `:384-398` with:

```csharp
        // Store had no IsError guard at all and returned 0 unconditionally — the same dead-guard
        // defect already fixed at Show, Forget, Export and Import.
        if (response.IsError)
        {
            if (json) { WriteJsonError(response.Error ?? "unknown error"); return 1; }
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(FormatCliError(response))}");
            return 1;
        }

        string result = response.ToYaml();

        if (json)
        {
            var (scope, subject) = WorkspaceSetup.Store.ParseQualifiedName(name);
            string qualifiedName = WorkspaceSetup.Store.FormatQualifiedName(scope, subject);
            long bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            WriteJson(new CliStoreOutput(qualifiedName, 1, bytes, result),
                CliJsonContext.Default.CliStoreOutput);
            return response.IsWarning ? 1 : 0;
        }
        return RenderEnvelope(response, result);
```

Note `Store` declares `string result = response.ToYaml();` at `:384`, *above* the `json` branch — the replacement moves it below the new error guard. Do not leave both declarations.

- [ ] **Step 5b: Make the shared passthrough helper warning-aware**

`ScriniaCommands.cs:64-81`. This is the fifth print site and the one that carries `scri facts`. Change `:66`:

```csharp
        // A warning envelope means the command completed without doing what was asked — an empty
        // import, an entity that matched nothing, an update specifying no changes. Exiting 0 for
        // that reported a no-op as success to any script gating on this command.
        bool failed = r.IsError || r.IsWarning;
```

and `:80`:

```csharp
        return failed ? 1 : 0;
```

Leave the two output branches alone: this helper writes raw YAML through `Console.Write` for callers that parse it, so it gains the exit-code change and no colour.

- [ ] **Step 6: Run the full suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Tests
```

Expected: the three new/changed tests pass, 0 build errors. **If any other test fails, it is pinning the old "warning reports success" behaviour** — read it, and if it is asserting exit 0 on a warning envelope, update it and record which test and why in your report. Do not update a test that fails for any other reason without reporting it.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Mcp/McpResponse.cs src/Scrinia/Commands/ tests/Scrinia.Tests/BundleExitCodeTests.cs
rtk git commit -m "fix(cli): a warning envelope stopped reporting itself as success"
```

---

### Task 2: The HTTP surface — empty import stops returning 200

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:436-437`
- Test: `tests/Scrinia.Server.Tests/BundleEndpointTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing from Task 1 — this is the third E1 surface and shares no code. It does share the ruling, so the CHANGELOG entry written here covers all three surfaces.

**Context an implementer cannot infer:**

`ImportBundle` at `:400-452` returns `Results.Ok(new { message = "No topics were imported." })` at `:437` when `topicCount == 0 && skippedCategories.Count == 0`. **No existing test pins that 200** — verified with the `Grep` tool across `tests/`; the only other occurrence of the string is the MCP-layer one in `MemoryTools.Bundling.cs:124`.

**Status code: `422 Unprocessable Content`.** The request was well-formed and authorised — it parsed, the file uploaded, the archive opened — but it yielded nothing to import. `400` is wrong (the request was fine), `404` is wrong (the store exists). The endpoint's existing codes are `400`/`403`/`501`; `422` does not collide. This is a deliberate choice, so state it in the CHANGELOG.

- [ ] **Step 1: Write the failing test**

Append to `tests/Scrinia.Server.Tests/BundleEndpointTests.cs`. Read the file first and copy its established pattern — do not invent a new one.

The pattern lives in `Import_WithMultipartUpload_ReachesTheHandler` (`:124-162`) and `Import_WithTopicsFilter_OnlyImportsSpecifiedTopic` (`:164-202`). There is **no shared multipart helper method**: each test inlines `MultipartFormDataContent` + `ByteArrayContent` + `content.Add(bundle, "file", "x.scrinia-bundle")` + `client.PostAsync(...)`. The client comes from the fixture pattern used uniformly across the file: `new ScriniaServerFactory()` then `factory.CreateAuthenticatedClient()`. `Import_WithTopicsFilter_OnlyImportsSpecifiedTopic` is the closest existing test — it already builds a bundle and filters by topic, so start from it and change the filter to one that matches nothing.

The shape:

```csharp
    /// <summary>
    /// RULED 2026-08-10: an import that matched no topics is not a success. Returning 200 made a
    /// deploy step gating on the status code pass while nothing had been imported.
    /// </summary>
    [Fact]
    public async Task ImportBundle_WhenNoTopicsMatch_Returns422()
    {
        // ... build a valid bundle containing topic "api", then import it filtered to a topic
        // that is not in it, using this file's existing multipart-post helper.

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableContent);
    }
```

`HttpStatusCode.UnprocessableContent` and `StatusCodes.Status422UnprocessableEntity` both exist in this SDK (.NET 10.0.9 — verified against the installed reference assemblies). No cast fallback is needed.

- [ ] **Step 2: Run it to verify it fails**

```bash
rtk dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~ImportBundle_WhenNoTopicsMatch"
```

Expected: FAIL, actual `OK`/200.

- [ ] **Step 3: Change the endpoint**

Replace `MemoryEndpoints.cs:436-437`:

```csharp
            // RULED 2026-08-10: "imported nothing" is not a success. A 200 here made any deploy
            // step gating on the status code pass while the bundle had not been imported. 422 —
            // the request was well-formed and authorised, but yielded nothing to import.
            if (topicCount == 0 && skippedCategories.Count == 0)
                return Results.Json(
                    new ErrorResponse("No topics were imported (empty bundle or all filtered out)."),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
```

- [ ] **Step 4: Run the server suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Server.Tests
```

Expected: new test passes, 0 build errors. Report any other test that changed status.

- [ ] **Step 5: Add the CHANGELOG entry**

Insert a new subsection into `CHANGELOG.md` under `## [Unreleased]`, after the existing `### Changed (breaking) — the HTTP store request and response shapes` block:

```markdown
### Changed (breaking) — "matched nothing" is no longer reported as success

- An operation that completed without doing what was asked now reports a non-success outcome on
  every surface. Previously the `warning` envelope rendered in green, exited `0`, and returned
  `200` — so a CI step gating on the exit code or the status code passed while nothing had
  happened. Three call sites produce this envelope: an empty or fully-filtered bundle import, a
  `facts` entity argument that matched nothing, and an update specifying no changes.
- **CLI:** `scri bundle import`, `scri bundle export`, `scri store` and `scri forget` exit `1` on a
  warning envelope and print it in yellow. Concretely,
  `scri bundle import --topics doesnotexist` now exits `1`.
- **HTTP:** `POST /api/v1/stores/{store}/memories/import` returns `422 Unprocessable Content`
  instead of `200` when no topics were imported. `422` rather than `400`, because the request was
  well-formed and authorised — it simply yielded nothing to import.
- **Also fixed:** `scri store` had no error guard at all and returned `0` unconditionally, so a
  genuinely failed store reported success. This is the same dead-guard defect previously fixed at
  `scri show`, `scri forget`, `scri bundle export` and `scri bundle import`; `store` was missed.
- Anything gating on exit `0` or HTTP `200` from these paths will now see a failure where it
  previously saw success. That is the point of the change, but it is breaking.
```

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs tests/Scrinia.Server.Tests/BundleEndpointTests.cs CHANGELOG.md
rtk git commit -m "fix(server): an import that matched nothing stopped returning 200"
```

---

### Task 3: Group D hygiene — ignore rule, dead using, redundant filesystem op, stale citation

**Files:**
- Modify: `.gitignore`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:3`
- Modify: `src/Scrinia.Core/FileLock.cs:31-32`, `:49-51`
- Test: `tests/Scrinia.Tests/FileLockTests.cs` (create if it does not exist)
- Modify: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs:263-265`

**Interfaces:** none — four independent mechanical changes.

**Context an implementer cannot infer:**

**D3 is the only one with a design constraint.** `FileLock.Acquire` (`:49`) is private and called from exactly two places: `AcquireExclusive` (`:31-32`) and `AcquireShared` (`:40-47`). `Acquire` calls `Directory.CreateDirectory` unconditionally at `:51`. On the shared path that call is **always** a no-op, because `AcquireShared` has already returned early at `:43-44` if the directory does not exist. So the shared path pays for a redundant filesystem op on every cold read. The fix is to move the create to the writer, which is the only caller that needs it.

This introduces one behaviour change in a race: if the directory is deleted between `AcquireShared`'s `Directory.Exists` check and the `FileStream` open, the open now throws `DirectoryNotFoundException` (a subclass of `IOException`), which the existing retry loop at `:65` catches and retries until timeout, ending in `FileLockTimeoutException`. Previously `CreateDirectory` would silently recreate the directory. **This is acceptable and must be documented in the code comment** — a store directory being deleted underneath an in-flight read is already a torn state, and failing loudly beats recreating a directory the caller has no reason to want back.

**D1:** `git check-ignore -v cmds.txt` finds no rule (verified). Add the ignore rule so the next branch stops routing around it.

- [ ] **Step 1: D1 — ignore `cmds.txt`**

Read `.gitignore` first and add the entry to whichever section holds local scratch files, matching the file's existing comment style. If there is no such section, append:

```gitignore

# Local scratch, not project artifacts
/cmds.txt
```

Anchored with a leading `/` so it matches only the repo root. **Do not delete, move, or stage `cmds.txt` itself** — it is the user's file.

- [ ] **Step 2: D2 — drop the dead `using`**

Delete line 3 of `src/Scrinia.Core/FileMemoryStore.cs`:

```csharp
using System.Linq;
```

It is redundant under `ImplicitUsings`. Invisible to the build today because `EnforceCodeStyleInBuild` is unset and there is no `.editorconfig`, so removing it must not break the build — confirm in Step 6.

- [ ] **Step 3: D3 — check the existing coverage before adding any**

`tests/Scrinia.Tests/FileLockTests.cs` **already exists** with 10 tests, a constructor/`IDisposable` fixture and a `_tempDir`/`LockPath` convention. The verification pass established this; an earlier draft of this plan wrongly said to create it.

Read it. `CreatesDirectoryIfMissing` (`:94-101`) already exercises `AcquireExclusive` against a missing nested directory — that is the exact regression Step 5 risks, so **it is already covered and you should not duplicate it**.

Add only what is genuinely missing: a test that a *shared* lock does not create the directory as a side effect of reading. Follow the file's existing fixture style (`_tempDir`, no hand-rolled `Path.GetTempPath()` + try/finally):

```csharp
    /// <summary>A shared lock over a missing directory is a lock over nothing — it must not
    /// create the directory as a side effect of reading. Pins the read path after the
    /// directory-creating call moved onto AcquireExclusive, its only real caller.</summary>
    [Fact]
    public void AcquireShared_WhenDirectoryDoesNotExist_DoesNotCreateIt()
    {
        string missingDir = Path.Combine(_tempDir, "nested");
        string lockPath = Path.Combine(missingDir, ".lock");

        using (var fileLock = FileLock.AcquireShared(lockPath))
            Directory.Exists(missingDir).Should().BeFalse();

        Directory.Exists(missingDir).Should().BeFalse(
            because: "disposing a lock over nothing must not create anything either");
    }
```

Match the field name the fixture actually uses — if it is not `_tempDir`, use whatever it declares.

- [ ] **Step 4: Run the file, then prove the guard can fail**

```bash
rtk dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~FileLockTests"
```

Expected: all PASS against unmodified code. **That is deliberate** — these are regression guards for a refactor, not tests of new behaviour.

A guard that cannot fail is worse than no guard, so run the negative control now: temporarily **delete** `Directory.CreateDirectory` from `Acquire` (`:51`) without adding it to `AcquireExclusive`, re-run, and verify `CreatesDirectoryIfMissing` FAILS. Restore it, re-run, verify it passes again. **Report both results.** If `CreatesDirectoryIfMissing` passes with the line deleted, it is not guarding what Step 5 needs it to guard — say so and add one that does.

- [ ] **Step 5: D3 — move the create onto the writer**

In `src/Scrinia.Core/FileLock.cs`, replace `AcquireExclusive` (`:31-32`):

```csharp
    public static FileLock AcquireExclusive(string lockPath, TimeSpan? timeout = null, ILogger? logger = null)
    {
        // Writers create the directory; readers do not. An exclusive lock guards a write, and the
        // write needs somewhere to land. AcquireShared has already returned a lock over nothing if
        // the directory is missing, so this call was pure cost on every cold read.
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        return Acquire(lockPath, FileAccess.ReadWrite, FileShare.None, timeout ?? TimeSpan.FromSeconds(5), logger);
    }
```

And delete line `:51` from `Acquire`:

```csharp
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
```

Then extend the `AcquireShared` doc comment (`:34-39`) with the race note:

```csharp
    /// <para>If the directory is deleted between that check and the open, the open throws
    /// <see cref="DirectoryNotFoundException"/> — an <see cref="IOException"/>, so the retry loop
    /// below retries it to timeout rather than recreating the directory. That is intended: a store
    /// directory vanishing under an in-flight read is a torn state, and failing loudly beats
    /// silently resurrecting a directory the caller never asked for.</para>
```

- [ ] **Step 6: D6 — remove the citation of a spec that never existed**

`tests/Scrinia.Tests/ScriniaMcpToolsTests.cs:263-265` cites
`docs/superpowers/specs/2026-07-10-memory-model-keywords-attributes-design.md`, which does not exist and was never tracked. Keep the explanation, drop the dead reference:

```csharp
        // P6.2 reader cutover: the description column sources ContentPreview (always-populated)
        // ahead of the legacy Description field — see AttributeReaders.PreviewText.
```

- [ ] **Step 7: Build and run the full suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Tests
```

Expected: 0 build errors, all tests pass.

- [ ] **Step 8: Commit**

```bash
rtk git add .gitignore src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/FileLock.cs tests/Scrinia.Tests/FileLockTests.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
rtk git commit -m "chore: four hygiene closures — ignore rule, dead using, redundant mkdir, stale citation"
```

---

### Task 4: D4 — delete the `CachedIndex` members that have no readers

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:59-109`
- Modify: `docs/architecture/core.md:436-465`, and the cross-reference at `:222-224`

**Interfaces:**
- Produces: `CachedIndex` reduced to a single `Entries` property. The class itself **stays** — see below.

**Context an implementer cannot infer:**

`NameToPosition` (`:73`), `Stats` (`:76`), `Rebuild()` (`:84`), `GetOrComputeCorpusStats()` (`:91`) and `BuildNameMap` (`:103`) have **zero readers anywhere in `src/` or `tests/`** — verified with the `Grep` tool; the only occurrences are their own declarations and the comments describing them. `BuildNameMap` runs on every cache write (`:81`, called from the constructor, which is invoked at `:673`, `:955`, `:982`, `:1010`, `:1053`) to build a dictionary nothing reads.

`docs/architecture/core.md:464-465` currently says *"They are left in place — removing them is a code change outside the scope of the docs fix that noted them here."* That is a deferral, not a ruling, and this task is where it gets closed.

**Keep the `CachedIndex` class.** After the deletion it wraps one field, which normally argues for collapsing `_indexCache` to `ConcurrentDictionary<string, List<ArtifactEntry>>`. Do not: the class is the documented home of the aliasing contract established in `docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md` (condition C1 — `LoadIndex` hands out a live view, and mutating callers must `.ToList()`). That comment needs somewhere to live, and a raw `List<ArtifactEntry>` in a dictionary has nowhere to hang it. Collapsing also churns seven call sites in the exact code that analysis was written to make safe.

`CorpusStats` and `Bm25Scorer.ComputeCorpusStats` are used elsewhere (`WeightedAttributeScorer` computes stats directly on every search) — **delete only the `CachedIndex` member, not the type**.

- [ ] **Step 1: Confirm the members are unread**

```bash
rtk proxy grep -rn "NameToPosition\|BuildNameMap\|GetOrComputeCorpusStats\|\.Rebuild()\|\.Stats" --include=*.cs src/ tests/
```

Expected: matches only inside `src/Scrinia.Core/FileMemoryStore.cs` (declarations and comments). **If any other file appears, stop and report it** — the deletion premise is wrong and the task needs re-scoping.

- [ ] **Step 2: Replace the class**

Replace `src/Scrinia.Core/FileMemoryStore.cs:59-109` (from the `/// <summary>` opening the `CachedIndex` doc comment through the closing brace of `BuildNameMap`) with:

```csharp
    /// <summary>
    /// In-memory cache of a scope's index entries, handed out by reference on a
    /// <see cref="LoadIndex"/> cache hit.
    ///
    /// <para>This wraps a single list rather than being one because it is the documented home of
    /// the aliasing contract below — see
    /// docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md. It previously also
    /// carried a name-to-position map, a lazy BM25 corpus-stats cache and a rebuild method; none
    /// had a single reader anywhere in the repo, and the map was rebuilt on every cache write.
    /// Deleted 2026-08-10.</para>
    /// </summary>
    internal sealed class CachedIndex
    {
        /// <summary>The cached entries. Handed out by reference from LoadIndex — callers get a
        /// live view of this list, not a copy. That is safe only because no reader mutates it
        /// and every cache write REPLACES the CachedIndex rather than mutating this instance.
        /// A caller that needs to mutate must call ToList() explicitly, as ImportTopicEntries
        /// does.</summary>
        public List<ArtifactEntry> Entries { get; }

        public CachedIndex(List<ArtifactEntry> entries) => Entries = entries;
    }
```

- [ ] **Step 3: Fix the stale cross-reference in `LoadIndex`**

`FileMemoryStore.cs:604-606` names the deleted members in a comment justifying the live-view return. Rewrite so it no longer references them:

```csharp
            // Live view, not a copy. Safe because no reader mutates the returned list — there is
            // no derived structure on CachedIndex left to desync — and every cache write REPLACES
            // the CachedIndex rather than mutating this instance.
```

- [ ] **Step 4: Build**

```bash
rtk dotnet build Scrinia.sln
```

Expected: 0 errors. A `CS0246`/`CS1061` here means a reader exists that Step 1 missed — report it rather than reinstating the member.

- [ ] **Step 5: Update the docs**

In `docs/architecture/core.md`, rewrite the `### CachedIndex` section (`:436-465`) so the code block matches the new class and the "left in place" paragraph is replaced by the ruling:

```markdown
### CachedIndex

Internal structure holding a scope's cached index entries:

```csharp
internal sealed class CachedIndex
{
    /// <summary>The cached entries. Handed out by reference from LoadIndex — callers get a
    /// live view of this list, not a copy.</summary>
    public List<ArtifactEntry> Entries { get; }
}
```

`Entries` is the list `LoadIndex` hands back directly on a cache hit — see **Keyword-Only Index
Scans**, below, for why that is safe.

The class previously also carried `NameToPosition`, `Rebuild()` and `GetOrComputeCorpusStats()`.
None had a reader anywhere in the repo, and `NameToPosition` was rebuilt on every cache write for
nothing. They were deleted on 2026-08-10. `Upsert` and `Remove` never consulted the position map —
they reload the scope from sidecars via `LoadEntriesFromSidecars` and replace the `CachedIndex`
outright — and corpus stats are computed directly by `WeightedAttributeScorer` on every search.
```

Also fix the cross-reference at `:222-224`, which currently says `CachedIndex` "has a
`GetOrComputeCorpusStats()` method that would cache them per scope, but nothing calls it":

```markdown
(`WeightedAttributeScorer.cs:80-82`, `:141-143`) — not cached. A per-scope cache for them was
carried on `CachedIndex` and never called; it was deleted on 2026-08-10. See **CachedIndex** below.
```

- [ ] **Step 6: Run the full suite**

```bash
rtk dotnet test tests/Scrinia.Tests
```

Expected: all pass — this deletes unread code, so nothing should change behaviourally.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Core/FileMemoryStore.cs docs/architecture/core.md
rtk git commit -m "refactor(core): delete the CachedIndex members that never had a reader"
```

---

### Task 5: D5 — the docs stop describing a class that was deleted

**Files:**
- Modify: `docs/architecture/cli.md:17`, `:60`, `:62`, `:71`, `:75`
- Modify: `AGENTS.md:42`

**Interfaces:** none.

**Context an implementer cannot infer:**

`ScriniaArtifactStore` was deleted from the codebase. A repo-wide search (all file types) finds **13 files, 97 occurrences** — the closure spec's "14 hits, 12 historical" figure was wrong, corrected here by the verification pass. Only **two files get edited**:

| File | Hits | Verdict |
|---|---|---|
| `docs/architecture/cli.md` | 5 | **Live docs — edit** (`:17`, `:60`, `:62`, `:71`, `:75`) |
| `AGENTS.md` | 1 | **Live docs — edit** (`:42`) |
| `CHANGELOG.md` | 3 | **Leave.** A changelog is a dated record by construction; editing a shipped entry to match the present is the same audit-trail loss as editing a spec |
| 4 files under `docs/superpowers/**` (two 2026-08-03 spec/plan pairs) | 72 | **Leave** — dated records |
| `docs/superpowers/specs/2026-08-10-residuals-closure.md` | 1 | **Leave** — Task 6 handles that file |
| `tests/Scrinia.Tests/FileMemoryStoreTests.cs` (`:899`, `:901`), `WriterCutoverTests.cs` (`:19`) | 3 | **Leave** — the name appears in comments explaining what a test replaced, which is accurate history |
| 2 files under `.scrinia/memories/findings/` | 4 | **Leave** — stored memories, not docs |

**Editing anything outside the first two rows fails review.** Rewriting dated records to match the present is how a project loses its audit trail.

Read `src/Scrinia/` first to establish what actually replaced it (`WorkspaceSetup` holds the store — see `WorkspaceSetup.Store` and `WorkspaceSetup.LocalStore`, used throughout `ScriniaCommands.*.cs`) and describe *that*, rather than deleting the section and leaving a hole where the CLI's storage story should be.

- [ ] **Step 1: Establish the replacement**

```bash
rtk proxy grep -rn "class WorkspaceSetup" --include=*.cs src/Scrinia/
```

Read the class. Note how `Store` and `LocalStore` are exposed and where they are configured (`WorkspaceSetup.Configure`, called at the top of every command). You are documenting this.

- [ ] **Step 2: Rewrite `docs/architecture/cli.md`**

- `:17` — in the file-tree listing, replace the `ScriniaArtifactStore.cs` line with the file that actually holds the CLI's store access. Verify the path exists before writing it.
- `:60-75` — retitle the `## ScriniaArtifactStore (CLI Store)` section to match the real mechanism and rewrite its three paragraphs (`:62`, `:71`, `:75`) to describe `WorkspaceSetup`: how the store is configured per command, and that both CLI commands and MCP tool handlers reach it.

Keep the section's structure and the ASCII diagram at `:71` — update the label, do not delete the diagram.

- [ ] **Step 3: Fix `AGENTS.md:42`**

Replace the tree line:

```
      Mcp/ScriniaArtifactStore.cs <- CLI store wrapper (static, delegates to FileMemoryStore)
```

with the real file and description. Verify the path exists.

- [ ] **Step 4: Verify no live reference survives**

```bash
rtk proxy grep -rn "ScriniaArtifactStore" docs/architecture/ AGENTS.md README.md
```

Expected: **no output.** (`CHANGELOG.md` is deliberately excluded — see the table above.) Then confirm the historical records are untouched:

```bash
rtk git diff --stat
```

Expected: only `docs/architecture/cli.md` and `AGENTS.md`. **No path under `docs/planning/` or `docs/superpowers/`.**

- [ ] **Step 5: Commit**

```bash
rtk git add docs/architecture/cli.md AGENTS.md
rtk git commit -m "docs: stop describing the deleted ScriniaArtifactStore"
```

---

### Task 6: Group F — fix two, rule four, strike the closed groups

**Files:**
- Modify: `tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs:36`, `:63` (F1 — fix)
- Create: `tests/Scrinia.Server.Tests/GlobalUsings.cs`; modify 30 test files (F4 — fix)
- Modify: `src/Scrinia/HttpMemoryStore.cs:436`, `:446` (F2 — ruling), `:213`, `:348`, `:481`, `:590` (F5 — ruling)
- Modify: `src/Scrinia.Core/Search/AttributeExtractor.cs:111` (F6 — ruling)
- Modify: `docs/architecture/cli.md` (F3 — ruling)
- Modify: `docs/superpowers/specs/2026-08-10-residuals-closure.md`

**Interfaces:** none.

**Context an implementer cannot infer:**

Each of these six has been deliberately left with sound reasoning more than once. An item that keeps earning a good reason not to fix it is a decision nobody wrote down. Two are cheaper to fix than to explain; four get the reason written where a reader will hit it.

**A ruling is not a TODO.** It states the decision and why, in the present tense, and does not invite revisiting. If you find yourself writing "for now" or "eventually", you are writing a deferral — stop and write the decision instead.

- [ ] **Step 1: F1 — use the typed predicate (fix)**

`tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs:36`:

```csharp
        if (response.IsError)
```

`:63`:

```csharp
                if (!recalled.IsError)
```

`McpResponse.IsError` is `public` (`src/Scrinia.Mcp/McpResponse.cs:48`) and reachable here. This removes the last two hand-spelled status comparisons in the repo.

- [ ] **Step 2: F4 — one global using instead of thirty (fix)**

Create `tests/Scrinia.Server.Tests/GlobalUsings.cs`:

```csharp
// Matches tests/Scrinia.Tests/GlobalUsings.cs. This project previously had no global usings, so
// all 30 test files carried `using Xunit;` individually — an inconsistency between the two test
// projects that kept getting re-reported as a defect. It was never a defect; it is now moot.
global using Xunit;
```

Then remove the line `using Xunit;` from each of the 30 files listed by:

```bash
rtk proxy grep -rln "^using Xunit;" --include=*.cs tests/Scrinia.Server.Tests/
```

Remove **only** that exact line. If a file's `using` block is left with a stray blank line at the top, tidy it. Do not reorder or otherwise touch the remaining usings.

- [ ] **Step 3: Build after the two fixes**

```bash
rtk dotnet build Scrinia.sln
```

Expected: 0 errors. A missed `using Xunit;` is merely redundant (harmless); a wrongly-removed line is a compile error, so the build verifies this step exhaustively.

- [ ] **Step 4: F2 — rule the `ListScoped` scopes check**

At `src/Scrinia/HttpMemoryStore.cs:445`, immediately above the URL construction:

```csharp
        // The `scopes is not null` test here selects WHICH URL is built — filtered or unfiltered —
        // rather than whether an empty filter parameter gets appended to one. That makes it a
        // different question from the sibling check that was changed to a whitespace test, and the
        // two are not an inconsistency to reconcile. An empty-but-non-null `scopes` correctly
        // produces `?scopes=`, which the server reads as "no scopes matched" — the same answer the
        // caller asked for.
```

- [ ] **Step 5: F5 — rule the fabricated `Uri` and `ChunkCount`**

`src/Scrinia/HttpMemoryStore.cs:544` already carries a partial version of this. Extend it, and add a one-line pointer at each of `:213`, `:348`, `:481`, `:590`:

```csharp
                // Uri and ChunkCount are placeholders on the remote path — see the note at
                // BuildArtifactEntry for the two capability decisions that own them.
```

At `:544`, make the note name the owning work explicitly:

```csharp
            // Uri: "" and ChunkCount: 1 are placeholders, not defects, and are not tracked
            // separately from the two capability decisions that own them. A dereferenceable
            // remote:// scheme is an addressing design (RULED 2026-08-10: build it); carrying real
            // chunk structure across the wire is a wire-parity design (RULED 2026-08-10: send the
            // chunks). Until those land, remote reads flatten to a single chunk and the URI has
            // nothing to resolve against. Both are specced work, not backlog.
```

- [ ] **Step 6: F6 — rule the timestamp tokenization**

Above `SeedTermFrequencies` in `src/Scrinia.Core/Search/AttributeExtractor.cs:111`, extend the existing doc comment with:

```csharp
    /// <para>Values are tokenized as prose, which means a round-tripped timestamp in a `review`
    /// attribute seeds junk terms (`00`, `0000000`, `2026`) at +3 each. This is accepted: it is
    /// ranking noise, not data loss, it affects the local and remote paths identically, and the
    /// alternative — type-aware seeding that skips date-shaped values — buys a small precision
    /// gain for a per-key type model this scorer does not otherwise need.</para>
```

- [ ] **Step 7: F3 — rule the CLI/HTTP bundle-filter divergence**

The two surfaces parse topic filters differently, both correctly for their own conventions:

- CLI (`src/Scrinia/Commands/ScriniaCommands.Bundle.cs:83-84`) comma-splits a single `--topics` string.
- HTTP (`src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:404`) binds `[FromQuery] string[]? topics`, which reads repeated `?topics=` keys.

Add a subsection to `docs/architecture/cli.md`, near the bundle commands (find the right home; if the file has no bundle section, put it under the command reference):

```markdown
#### Topic filters: CLI and HTTP differ deliberately

`scri bundle import --topics a,b` comma-splits one string. The HTTP endpoint binds
`[FromQuery] string[] topics` and reads repeated keys — `?topics=a&topics=b`. Each follows its own
surface's convention: comma-separated lists are idiomatic for a CLI flag, repeated keys are
idiomatic for a query string and are what ASP.NET Core's model binder produces.

This is not an inconsistency to unify. Unifying requires first deciding which surface is canonical,
and neither has a claim over the other. Documented and pinned 2026-08-10.
```

Then add a pointer comment at each code site so a reader finds the ruling from the code:

```csharp
        // CLI comma-splits; the HTTP endpoint binds repeated ?topics= keys. Deliberate — see
        // "Topic filters: CLI and HTTP differ deliberately" in docs/architecture/cli.md.
```

- [ ] **Step 8: Run the full suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Tests && rtk dotnet test tests/Scrinia.Server.Tests
```

Expected: 0 build errors, all tests pass.

- [ ] **Step 9: Strike the closed groups from the closure spec**

In `docs/superpowers/specs/2026-08-10-residuals-closure.md`, replace the **Group D**, **Group F** and **E1** sections with a short closed-record noting the branch and date, and update the "Closure order" list so item 1 is marked done and the remaining items renumber. Keep Groups A, B, C, E2–E5 and G exactly as they are.

Record the two reclassifications (F1 and F4 fixed rather than ruled) so the spec matches what happened.

- [ ] **Step 10: Commit**

```bash
rtk git add tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs tests/Scrinia.Server.Tests/ src/Scrinia/HttpMemoryStore.cs src/Scrinia/Commands/ScriniaCommands.Bundle.cs src/Scrinia.Server/Endpoints/MemoryEndpoints.cs src/Scrinia.Core/Search/AttributeExtractor.cs docs/architecture/cli.md docs/superpowers/specs/2026-08-10-residuals-closure.md
rtk git commit -m "chore: retire the six standing items — two fixed, four ruled"
```

---

## Verification pass — RUN 2026-08-10

A subagent was dispatched to falsify this plan's factual claims before any implementer ran. Five corrections were folded into the tasks above; recorded here so a reviewer can see what the plan got wrong.

**Corrected — one of these was load-bearing:**

1. **The plan fixed four print sites and needed five.** `ScriniaCommands.cs:64-81` declares `EmitMcpResult`, the shared passthrough behind `scri facts`, `append`, `compact`, `link`, `restore` and `reconcile`, and it reads `IsError` only. `scri facts` is how `FactsResponseFormatter.cs:28` — one of the three warning sites the design is *justified by* — reaches a user. As first written, the plan would have shipped a rationale its own change contradicted. **Task 1 Step 5b added.**
2. **The `Store` failure test would not have failed.** `/skill/not-a-real-skill` is a valid `FileEntityName`, so the skill route would create it and return success. Changed to `/agent/bad name`, which hits the `INVALID_PARAMETER` guard at `MemoryTools.Core.cs:273-274` before the store-mode check.
3. **`tests/Scrinia.Tests/FileLockTests.cs` already exists** — 10 tests, a `_tempDir` fixture, and `CreatesDirectoryIfMissing` (`:94-101`) already covering the exact regression Task 3 risks. The plan said "create it" and proposed a duplicate. Task 3 now adds only the genuinely missing shared-lock test and reuses the existing one as the negative control.
4. **Task 2's pointer to the test pattern named comment lines**, not tests, and implied a shared multipart helper that does not exist. Corrected to `:124-162` / `:164-202` and the `ScriniaServerFactory` / `CreateAuthenticatedClient()` fixture.
5. **Task 5's reference accounting was wrong by ~6×** — 13 files and 97 occurrences, not "14 hits, 12 historical". The two files to edit were right; the arithmetic around them was not. Replaced with a full classification table, including an explicit ruling to leave `CHANGELOG.md`'s three hits alone.

**Confirmed as stated:** `Store` has no `IsError` guard (`Memory.cs:337-399`); exactly three `ResponseBuilder.Warning` sites; no test pins the empty-import `200`; all five `CachedIndex` members have zero readers (`.Stats` checked separately against 24 same-named hits on unrelated types); the `FileLock` call-graph and the `DirectoryNotFoundException : IOException` race analysis; `FormatCliError`/`WriteJsonError` live in `ScriniaCommands.cs` with `Spectre.Console` in scope and `ScriniaCommands` partial across all nine files; `InternalsVisibleTo Include="Scrinia.Tests"` at `src/Scrinia/Scrinia.csproj:30` makes the `internal` `Export`/`Import` reachable from tests; both 422 constants exist in .NET 10.0.9; `cmds.txt` unignored with a `# Local misc files` section at `.gitignore:37-39` as its natural home; 30 files with `using Xunit;` and no `GlobalUsings.cs` in `Scrinia.Server.Tests`; F1's two lines; and every `docs/architecture/core.md`, `HttpMemoryStore.cs` and `AttributeExtractor.cs` line number cited.
