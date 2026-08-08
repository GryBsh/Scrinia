# Surface Defects Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to
> implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix seven places where an outward surface declares one contract and honours another —
including one endpoint that has likely never worked and one command pair that reports success on
failure.

**Architecture:** No new abstractions. Six of the seven are one- to ten-line corrections at the
boundary where a value is bound, tested, or validated. The seventh (Task 1) adds a single
predicate so the stringly-typed status check has one home before Task 2 adds two more readers.

**Tech Stack:** C# / .NET 10, xunit 2.9.3, FluentAssertions 6.12.2, ASP.NET Core Minimal API.

**Source spec:** `docs/superpowers/specs/2026-08-08-surface-defects-design.md`
**Verified inventory:** `docs/superpowers/specs/2026-08-08-residuals-triage.md`

## Global Constraints

- **No mocking frameworks anywhere in this repo.** Test doubles are hand-written classes.
- `TreatWarningsAsErrors=true` for every project NOT ending `.Tests`/`.Benchmarks`
  (`Directory.Build.props:44-46`). Test projects carry ~1700 pre-existing analyzer warnings —
  **warning count is not a health signal; the gate is 0 errors.**
- `rtk dotnet build Scrinia.sln -c Debug` must be clean before any test run.
- **`rtk err` is NOT a build gate.** It has reported a failed build as clean, hiding CS0246s.
  Use `rtk dotnet build` or `rtk proxy dotnet build` with your own grep for `error CS` /
  `N Error(s)`. Grepping raw output for the bare word "error" produces false positives —
  CA1707 warnings quote test method names containing "Error".
- Never run two `dotnet test` invocations against one project concurrently; never `dotnet build`
  during a test run.
- Prefix every shell command with `rtk`, including inside `&&` chains.
- **Stage explicit paths. Never `git add .`** — `cmds.txt` is untracked, pre-existing, and is not
  part of this work.
- **Nothing is pushed.** Commit locally only.
- Branch `surface-defects` is already cut from `main` at `02d1a29`.

## A warning about this plan's facts

Every fact below carries the `file:line` that produced it at `02d1a29`. Where this plan
enumerates call sites, **treat the list as a starting point and the compiler as the authority** —
the previous branch in this sequence shipped four wrong "facts" into execution, all of them
claims of *completeness* rather than claims of *mechanism*. If a grep returns a count different
from the one stated here, the grep is right.

## File Structure

| File | Change |
|---|---|
| `src/Scrinia.Mcp/McpResponse.cs` | Add `IsError` predicate |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | `:1377` route through `IsError` |
| `src/Scrinia/Commands/ScriniaCommands.cs` | `:66` route through `IsError` |
| `src/Scrinia/Commands/ScriniaCommands.Memory.cs` | `:421`, `:467` route through `IsError` |
| `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` | Replace two dead guards (`:57`, `:99`) |
| `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` | `[FromQuery]` on `:370`; value bound near `:300`; delete usings `:1`, `:7` |
| `src/Scrinia.Core/IMemoryEventSink.cs` | Validate `InstallDefault` arguments |
| `src/Scrinia/HttpMemoryStore.cs` | `:411` scopes consistency |
| `CHANGELOG.md` | Entries for Tasks 2 and 3 |

---

### Task 1: One predicate for error status

**Files:**
- Modify: `src/Scrinia.Mcp/McpResponse.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:1377`
- Modify: `src/Scrinia/Commands/ScriniaCommands.cs:66`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:421`, `:467`

**Interfaces:**
- Produces: `McpResponse.IsError` — a `bool` instance property. Task 2 depends on it.

**Established facts:**
- `McpResponse.Status` is `required string` with values `"success"`, `"error"`, `"warning"`
  (`McpResponse.cs:41`).
- Four production sites test `Status == "error"`. A fifth match in
  `tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs:36` is test-project code — **decide
  whether to include it and say which you chose**; it is not counted in the four.

**This task is a pure refactor. No behaviour changes. If a test result changes, stop.**

- [ ] **Step 1: Add the predicate**

In `McpResponse.cs`, inside the `McpResponse` record, after the `Status` property:

```csharp
    /// <summary>True when this response reports a failure. The one predicate for the
    /// stringly-typed <see cref="Status"/> — before this, four sites across two projects
    /// each spelled <c>Status == "error"</c> independently, and a fifth spelled it as a
    /// substring match on rendered YAML that never matched anything.</summary>
    public bool IsError => Status == "error";
```

- [ ] **Step 2: Find every site**

Run: `rtk grep -rn 'Status == "error"' src/`
Expected: four hits, at the lines listed above. **If the count differs, the grep is right and
this plan is wrong** — route all of them and note the discrepancy in your report.

- [ ] **Step 3: Route them**

`MemoryTools.Core.cs:1377` — note this one is a compound condition:

```csharp
        if (result1.IsError || result2.IsError)
```

`ScriniaCommands.cs:66`:

```csharp
        bool isError = r.IsError;
```

`ScriniaCommands.Memory.cs:421` and `:467`:

```csharp
        if (response.IsError)
```

- [ ] **Step 4: Build and run the full suite**

```
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Tests
rtk dotnet test tests/Scrinia.Server.Tests
```
Expected: 0 errors; all previously-passing tests still pass. This is the whole test for a
refactor — do not add new tests for a property that restates an existing comparison.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Mcp/McpResponse.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia/Commands/ScriniaCommands.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs
rtk git commit -m "refactor(mcp): give the error-status check one home"
```

---

### Task 2: Bundle commands report failure

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs:47-68` (Export), `:91-112` (Import)
- Modify: `CHANGELOG.md`
- Test: `tests/Scrinia.Tests/` — new file, name it for the behaviour (e.g. `BundleExitCodeTests.cs`)

**Interfaces:**
- Consumes: `McpResponse.IsError` from Task 1.

**Established facts — read all four before writing code:**

- F2.1 `ScriniaCommands.Bundle.cs:57` tests `result.StartsWith("Error:")` where `result` is
  `response.ToYaml()`. `ToYaml` always opens with `status: `, so this never matches and
  `scri bundle export` returns 0 on a genuine failure.
- F2.2 `:99-100` is the same guard plus `result.StartsWith("No topics")`, equally dead.
- F2.3 **The fix pattern already exists.** `ScriniaCommands.Memory.cs:418-426` is the
  `scri show` version, written on the `mcp-response-shape` branch. Copy its shape, including
  `FormatCliError(response)` for the human-readable message and `response.Error` for `--json`.
- F2.4 **This one decides the task.** `MemoryTools.Bundling.cs:123-124` returns
  `ResponseBuilder.Warning("No topics were imported (empty bundle or all filtered out).")` —
  status `"warning"`, not `"error"`. The dead guard at F2.2 intended to exit 1 on that case,
  but **it never ran**, so today's observable behaviour is exit 0.

**The ruling, and do not deviate from it without escalating:** map `IsError` to exit 1 and
nothing else. The empty-bundle case keeps exiting 0.

The reasoning matters more than the rule. The `warning` status was chosen deliberately by
whoever wrote that return — `ResponseBuilder.Warning` exists and this is a considered use of it.
The dead guard expresses a *different* author's intent, and it has never once executed. Honouring
it now would not be "restoring" behaviour; it would be inventing a new exit-1 case under cover of
a bug fix. Fix what is broken — errors exiting 0 — and leave the warning question to someone who
decides it on purpose.

- [ ] **Step 1: Write the failing tests**

Two cases per command. Establish how the existing CLI tests drive these commands — look for
prior art before inventing a harness — and follow it.

```csharp
    [Fact]
    public async Task Export_OnGenuineFailure_ExitsNonZero()
    {
        // A topic that does not exist -> ResponseBuilder.Error(..., ErrorCodes.NotFound, ...)
        // per MemoryTools.Bundling.cs:66-69.
        int exit = await RunExport(topics: "no-such-topic-xyz");
        exit.Should().Be(1, "a failed export must not report success to a shell or CI step");
    }

    [Fact]
    public async Task Import_OnEmptyBundle_ExitsZero()
    {
        // Status is "warning", not "error" (MemoryTools.Bundling.cs:123-124). Pinning today's
        // behaviour deliberately: see F2.4. If someone later decides an empty import is a
        // failure, this test is the place that records it was a decision.
        int exit = await RunImport(bundleWithNoMatchingTopics);
        exit.Should().Be(0);
    }
```

Add the genuine-failure Import case too (a nonexistent bundle path → `ErrorCodes.NotFound`).

- [ ] **Step 2: Run them and watch export fail**

Run: `rtk dotnet test tests/Scrinia.Tests --filter BundleExitCode`
Expected: the two genuine-failure tests FAIL (returning 0, expected 1). The empty-bundle test
should already PASS — it pins current behaviour. **Report both outcomes.** A red check that was
never red is not evidence.

- [ ] **Step 3: Replace the Export guard**

In `ScriniaCommands.Bundle.cs`, replace lines 51-62 (the comment block and the dead guard) with:

```csharp
        if (response.IsError)
        {
            if (json) { WriteJsonError(response.Error ?? "unknown error"); return 1; }
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(FormatCliError(response))}");
            return 1;
        }

        string result = response.ToYaml();
```

Note the `ToYaml()` call at `:49` moves below the guard — on the error path the rendered YAML is
no longer used.

- [ ] **Step 4: Replace the Import guard**

Same shape, replacing lines 95-105. The `StartsWith("No topics")` condition is **deleted, not
translated** — per F2.4 and the ruling above.

- [ ] **Step 5: Run the tests**

Run: `rtk dotnet test tests/Scrinia.Tests --filter BundleExitCode`
Expected: all PASS.

- [ ] **Step 6: Full suite**

```
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Tests
rtk dotnet test tests/Scrinia.Server.Tests
```
Anything asserting `scri bundle` exits 0 on failure was asserting a defect — but read it before
changing it, and say so in your report.

- [ ] **Step 7: CHANGELOG**

Under `## [Unreleased]`, in the Fixed section:

```markdown
- `scri bundle export` and `scri bundle import` exited 0 on genuine failure. Both tested
  `result.StartsWith("Error:")` against rendered YAML, a shape the renderer has never emitted —
  every envelope opens with `status: `. Any CI step or script gating on their exit code was not
  gated. Both now read the typed status, matching the fix applied to `scri show` / `scri forget`.
  An import that matches no topics still exits 0: that response is a `warning`, not an error.
```

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia/Commands/ScriniaCommands.Bundle.cs tests/Scrinia.Tests/BundleExitCodeTests.cs CHANGELOG.md
rtk git commit -m "fix(cli): bundle export and import exited 0 on genuine failure"
```

---

### Task 3: POST /import accepts the requests it was built for

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:366-371`
- Modify: `CHANGELOG.md`
- Test: `tests/Scrinia.Server.Tests/` — extend `BundleEndpointTests.cs` if it fits, else a new file

**Established facts:**
- F3.1 `ImportBundle` declares `string[]? topics = null` at `:370` with no binding attribute
  (`:366-371`).
- F3.2 ASP.NET Core Minimal API infers a complex-typed parameter as JSON-body-bound. A
  multipart request therefore fails content negotiation with **415 before the handler runs**, so
  none of the handler's own guards (`:373`, `:376`, `:379`, `:393`) are ever reached.
- F3.3 This reproduces over `TestServer`, not only over a real socket — confirmed during the
  store-boundaries branch. A `WebApplicationFactory` test is sufficient; you do not need Kestrel.
- F3.4 `bool overwrite` at `:371` is a simple type and binds from the query string by default.
  **Only `topics` needs the attribute.**
- F3.5 The sibling `ExportTopics` (`:335-338`) takes an `ExportRequest` DTO. It is *not* the
  model to copy here — export has no file payload, so it can afford a JSON body. Import cannot.
- F3.6 Route registration is `group.MapPost("/import", ImportBundle);` at `:32`.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task Import_WithMultipartUpload_ReachesTheHandler()
    {
        using var factory = /* the existing local-store factory used by BundleEndpointTests */;
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var bundle = new ByteArrayContent(BuildMinimalBundleBytes());
        bundle.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(bundle, "file", "x.scrinia-bundle");

        var response = await client.PostAsync("/stores/{store}/import", content);

        response.StatusCode.Should().NotBe(HttpStatusCode.UnsupportedMediaType,
            because: "a complex-typed parameter with no [FromQuery] is inferred as JSON-body-bound, "
                   + "so every genuine multipart upload was rejected before the handler ran");
    }
```

**Then assert the import outcome, not merely "not 415".** A test that only excludes one status
code would pass against a handler that 500s. Assert the bundle's content actually landed.

Fill in the real route template from `MemoryEndpoints.cs:20-32` and the auth/permission setup the
neighbouring tests use — `import` permission is required (`:373`) and the store must be writable
(`:376`).

- [ ] **Step 2: Run it and watch it fail**

Run: `rtk dotnet test tests/Scrinia.Server.Tests --filter Import_WithMultipart`
Expected: FAIL with 415 Unsupported Media Type.

- [ ] **Step 3: Fix the binding**

```csharp
    private static async Task<IResult> ImportBundle(
        string store,
        HttpRequest request,
        RequestContext ctx,
        [FromQuery] string[]? topics = null,
        bool overwrite = false)
```

`FromQuery` is in `Microsoft.AspNetCore.Mvc`. Check whether the file already has that using
before adding one — and see Task 6, which deletes two unused usings from this same file.

- [ ] **Step 4: Run it**

Run: `rtk dotnet test tests/Scrinia.Server.Tests --filter Import_WithMultipart`
Expected: PASS.

- [ ] **Step 5: Add a topics-filter test**

The binding fix is only proven if `topics` actually arrives. Post a bundle with two topics and
`?topics=one`, and assert only that topic imported.

- [ ] **Step 6: Full suite**

```
rtk dotnet build Scrinia.sln -c Debug
rtk dotnet test tests/Scrinia.Tests
rtk dotnet test tests/Scrinia.Server.Tests
```

- [ ] **Step 7: CHANGELOG**

```markdown
- `POST /stores/{store}/import` rejected every multipart bundle upload with 415 Unsupported
  Media Type. Its `topics` parameter was declared without `[FromQuery]`, so Minimal API inferred
  it as JSON-body-bound and content negotiation failed before the handler ran — meaning none of
  the endpoint's own permission, writability or file-presence guards were ever reached. The
  endpoint had likely never worked for a real caller.
```

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs tests/Scrinia.Server.Tests/BundleEndpointTests.cs CHANGELOG.md
rtk git commit -m "fix(server): POST /import 415'd every multipart upload"
```

---

### Task 4: `InstallDefault` validates its arguments

**Files:**
- Modify: `src/Scrinia.Core/IMemoryEventSink.cs:39-40`
- Test: `tests/Scrinia.Tests/` — extend the existing event-sink tests if one fits

**Established facts:**
- F4.1 `InstallDefault(params IMemoryEventSink[] sinks) => Default = new CompositeEventSink(sinks);`
  — no validation (`IMemoryEventSink.cs:39-40`).
- F4.2 An empty array installs a composite over nothing: every memory event is silently dropped.
- F4.3 A null element becomes an NRE inside `CompositeEventSink`'s per-sink `try`/`catch`, which
  swallows it — so the sink does nothing rather than failing loudly.
- F4.4 Six call sites in `WorkspaceSetup.cs` route through this (`:240`, `:400`, `:417`, `:426`,
  `:446`, `:526`). **Verify none of them can legitimately pass an empty array before you make it
  throw** — if one can, that is a finding, and you must report it rather than making a live path
  throw.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void InstallDefault_WithNoSinks_Throws() =>
        FluentActions.Invoking(() => MemoryEventSinkContext.InstallDefault())
            .Should().Throw<ArgumentException>(
                because: "an empty composite silently discards every memory event");

    [Fact]
    public void InstallDefault_WithANullSink_Throws() =>
        FluentActions.Invoking(() => MemoryEventSinkContext.InstallDefault(new NoOpSink(), null!))
            .Should().Throw<ArgumentException>(
                because: "CompositeEventSink's per-sink catch would swallow the NRE and do nothing");
```

`MemoryEventSinkContext.Default` is process-wide static state. Follow whatever isolation the
neighbouring event-sink tests already use — there is a known, unclosed race around `StoreScope`
clearing `Default`, so do not invent a new isolation approach here.

- [ ] **Step 2: Run and watch fail**

Run: `rtk dotnet test tests/Scrinia.Tests --filter InstallDefault`
Expected: both FAIL (no exception thrown).

- [ ] **Step 3: Add the guards**

```csharp
    public static void InstallDefault(params IMemoryEventSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        if (sinks.Length == 0)
            throw new ArgumentException(
                "At least one sink is required — an empty composite silently discards every memory event.",
                nameof(sinks));
        if (Array.IndexOf(sinks, null) >= 0)
            throw new ArgumentException(
                "Sinks must not contain nulls — CompositeEventSink's per-sink catch would swallow the failure.",
                nameof(sinks));

        Default = new CompositeEventSink(sinks);
    }
```

Keep the existing doc comment; add a line noting the guards exist because centralising six call
sites turned six chances to notice into one trap for the seventh caller.

- [ ] **Step 4: Run**

Run: `rtk dotnet test tests/Scrinia.Tests --filter InstallDefault` — expected PASS.

- [ ] **Step 5: Full suite, then commit**

```bash
rtk dotnet build Scrinia.sln -c Debug && rtk dotnet test tests/Scrinia.Tests
rtk git add src/Scrinia.Core/IMemoryEventSink.cs tests/Scrinia.Tests/<file>
rtk git commit -m "fix(core): InstallDefault accepted input that silently disabled all sinks"
```

---

### Task 5: Bound the other dimension of `attributeFilter`

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` near `:300`
- Test: `tests/Scrinia.Server.Tests/` — alongside the existing search-bounds tests

**Established facts:**
- F5.1 `:300-301` bounds key *count* at 64 and returns 400. Per-key value-array length is
  unbounded.
- F5.2 The neighbouring bounds are `q` at 10 KB (`:292`), `scopes`/`excludeTopics`/`pathPrefix`
  at 1 KB each (`:294-299`). Match their style and their error-message shape.
- F5.3 Kestrel's body limit is the only current backstop.

- [ ] **Step 1: Write the failing test**

One key carrying 1,000 values → 400. Assert the status and that the message names
`attributeFilter`.

- [ ] **Step 2: Run and watch fail** (expected: 200, not 400)

- [ ] **Step 3: Add the bound**, immediately after the key-count check:

```csharp
        if (attributeFilter is not null && attributeFilter.Any(kv => kv.Value is { Length: > 64 }))
            return Results.BadRequest(
                new ErrorResponse("attributeFilter may not exceed 64 values per key."));
```

64 mirrors the key bound. If you pick a different number, say why in your report.

- [ ] **Step 4: Run, full suite, commit**

```bash
rtk git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs tests/Scrinia.Server.Tests/<file>
rtk git commit -m "fix(server): bound attributeFilter value arrays, not just key count"
```

---

### Task 6: Two consistency fixes

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:1`, `:7`
- Modify: `src/Scrinia/HttpMemoryStore.cs:411`

**Established facts:**
- F6.1 `:1` is `using System.Threading.RateLimiting;` and `:7` is
  `using Scrinia.Plugin.Abstractions;`. The only rate-limiting reference in the file is
  `.RequireRateLimiting("api")` at `:20`, which resolves through `Microsoft.AspNetCore.Builder`,
  not through the `:1` import. **Verify with the compiler, not by eye.**
- F6.2 `EnforceCodeStyleInBuild` is unset on that project and there is no `.editorconfig`, so
  nothing flags unused usings — that is why these survived.
- F6.3 `BuildSearchGet` (`HttpMemoryStore.cs:409-417`) checks `scopes is not null` at `:411`
  while `excludeTopics` (`:413`) and `pathPrefix` (`:415`) check
  `!string.IsNullOrWhiteSpace(...)`. An empty-string `scopes` emits `&scopes=` where the others
  would omit the parameter.
- F6.4 `ListScoped` at `:288` uses the same `scopes is not null` pattern for its URL build.
  **This is out of scope — do not change it.** Recorded so you know it was seen, not missed.
  Note it in your report.

- [ ] **Step 1: Delete the two usings, then build**

```
rtk dotnet build Scrinia.sln -c Debug
```
Expected: 0 errors. If either was load-bearing the compiler says so immediately — restore it and
report which.

- [ ] **Step 2: Make `scopes` consistent**

```csharp
        if (!string.IsNullOrWhiteSpace(scopes))
            url += $"&scopes={Uri.EscapeDataString(scopes)}";
```

- [ ] **Step 3: Full suite**

No new test. This is a consistency fix with no known caller passing an empty string — say that
plainly in your report rather than writing a test that asserts a URL shape nobody depends on.

- [ ] **Step 4: Commit**

```bash
rtk git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs src/Scrinia/HttpMemoryStore.cs
rtk git commit -m "chore: drop two dead usings and align the scopes null check"
```

---

### Task 7: Branch close

The `internal-consistency` branch's residuals recorded a process gap: its ledger had no
branch-close section, which is how a CHANGELOG entry and a spec correction were both missed until
the final review. This task is that section, made explicit.

- [ ] **Step 1: Verify the CHANGELOG covers every user-visible change**

Tasks 2 and 3 change observable behaviour. Confirm both entries exist and read correctly to
someone who has not seen this branch. Tasks 1, 4, 5, 6 are internal or additive — confirm that
judgement rather than assuming it.

- [ ] **Step 2: Re-verify the source spec against what shipped**

Read `docs/superpowers/specs/2026-08-08-surface-defects-design.md` and correct any claim this
branch falsified. The spec's own F6 count (four `Status == "error"` sites) is the most likely
candidate.

- [ ] **Step 3: Full gate**

```
rtk dotnet build Scrinia.sln -c Debug --no-incremental
rtk dotnet test tests/Scrinia.Tests
rtk dotnet test tests/Scrinia.Server.Tests
rtk dotnet test tests/Scrinia.Merge.Tests
```
Expected: 0 errors, 0 failures.

- [ ] **Step 4: Record residuals to Scrinia** under
`/findings/surface-defects-deferred-followups`, including anything this plan told you to note
(F4.4's call-site check, F6.4's `ListScoped` sibling, the Task 1 test-project fifth site, and any
number you chose differently from the plan).

- [ ] **Step 5: Commit** the `.scrinia/` changes and the spec correction with explicit paths.
