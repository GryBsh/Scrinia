# MCP Response Shape Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move `ToYaml()` from 20 internal handlers to the 12 public `[McpServerTool]` methods so no code recovers response structure by substring-matching rendered YAML.

**Architecture:** `McpResponse` becomes the currency inside `Scrinia.Mcp`. Internal handlers return `Task<McpResponse>`; the 12 public tool methods are the only callers of `.ToYaml()`. Consumers that today grep the rendered string — the CLI's exit code, `InjectInfoHint`, `Link`'s partial-failure check, the benchmark harness — read `.Status` instead. Because C# will not compile a partial retype across a call graph that spans two projects, the type flip is one atomic task, guarded by golden-file characterization tests committed *before* it.

**Tech Stack:** C# / .NET 10, xunit, FluentAssertions, no mocking frameworks. Projects: `src/Scrinia.Mcp`, `src/Scrinia` (CLI, `InternalsVisibleTo`), `tests/Scrinia.Tests`.

## Global Constraints

- Branch name: `mcp-response-shape`, cut from `main` after `merge-driver-correctness` merges.
- **YAML output must be byte-identical.** This is the load-bearing property of the whole branch. It constrains the MCP envelope only — the one deliberate behaviour change is CLI exit codes for `scri show` / `scri forget` on not-found (Task 3, Step 6), which are wrong today.
- Golden characterization files are the **first commit**, before any production code changes.
- `dotnet build Scrinia.sln -c Debug` must be clean before any test run.
- **Never** run two `dotnet test` invocations against one project concurrently.
- **Never** run `dotnet build` while a test run is in flight — it fails on locked DLLs and can wedge the run.
- Clear stray `testhost.exe` / `dotnet.exe` before a full run; apparent hangs are orphaned processes holding `FileLock`s on `%TEMP%` stores.
- Test projects in scope: `Scrinia.Tests`, `Scrinia.Server.Tests`. `Scrinia.Merge.Tests` is unaffected.
- Do not change `ErrorCodes`, `ResponseBuilder`'s truncation behaviour, or the `content: |` block format.
- Do not add a per-test `BackgroundWork` drain. See `/findings/event-sink-throw-crashes-test-host`.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `tests/Scrinia.Tests/Golden/ResponseGoldenTests.cs` | Captures and asserts byte-identical YAML per tool |
| `tests/Scrinia.Tests/Golden/GoldenNormalizer.cs` | Strips the three volatile fields from YAML before comparison |
| `tests/Scrinia.Tests/Golden/__snapshots__/*.yaml` | The committed golden output, one file per case |
| `tests/Scrinia.Tests/ResponseStatusIsolationTests.cs` | Pins the defect: body text must not be read as envelope status |
| `src/Scrinia.Mcp/SearchResultRenderer.cs` | Renders `SearchResult` blocks into the response body |

**Modified:**

| File | Change |
|---|---|
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | 12 handlers retyped; `Link` reads `.Status`; renderer moved out |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | `Reconcile`, `Compact`, `Restore`, `Bundle` retyped |
| `src/Scrinia.Mcp/MemoryTools.Bundling.cs` | `Export`, `Import` retyped |
| `src/Scrinia.Mcp/MemoryTools.Skills.cs` | `SkillCreate`, `SkillLoad` retyped |
| `src/Scrinia.Mcp/MemoryTools.Facts.cs` | `Facts` serializes at the boundary |
| `src/Scrinia.Mcp/FactsResponseFormatter.cs` | Returns `McpResponse` instead of YAML |
| `src/Scrinia.Mcp/MemoryTools.cs` | The 12 public methods call `.ToYaml()`; skill routers retyped |
| `src/Scrinia.Mcp/McpResponse.cs` | `InjectInfoHint` deleted |
| `src/Scrinia/Commands/ScriniaCommands.cs` | `EmitMcpResult` takes `McpResponse` |
| `src/Scrinia/Commands/ScriniaCommands.Memory.cs` | CLI call sites take typed responses |
| `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` | Export/Import call sites |
| `src/Scrinia/Commands/ScriniaCommands.Facts.cs` | Facts call site |
| `tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs` | Checks `.Status`, not a substring |

---

### Task 1: Golden YAML characterization harness

This lands **before** any production change. Its whole purpose is to make the retype provably behaviour-preserving rather than merely compiling.

**Files:**
- Create: `tests/Scrinia.Tests/Golden/GoldenNormalizer.cs`
- Create: `tests/Scrinia.Tests/Golden/ResponseGoldenTests.cs`
- Create: `tests/Scrinia.Tests/Golden/__snapshots__/` (populated by the first run)

**Interfaces:**
- Consumes: `ScriniaMcpTools` public tool methods (all return `Task<string>` today), `TestHelpers.StoreScope`, `MemoryStoreContext.Current`.
- Produces: `GoldenNormalizer.Normalize(string yaml) → string` and `ResponseGoldenTests`, both used unchanged by Task 3 as its gate.

**Why normalization is needed.** Three parts of the output are not a pure function of the fixture:
1. `score=NN` — the ranker adds `α_recency · exp(-Δt_days/τ) · RecencyScale`, and `Δt` is measured from *now*, so scores drift every day the suite runs.
2. `scrinia_test_<32 hex>` — `StoreScope` builds a GUID temp directory that appears in path-bearing responses.
3. `yyyy-MM-dd` dates — `AppendDatesLine` (`MemoryTools.Core.cs:1071-1076`) renders `created:` / `updated:` date-only, and entries written by the fixture get `DateTimeOffset.UtcNow`.

All three are normalized. Dates are normalized wholesale rather than surgically: the harness's job is structural equivalence across a refactor, and the exact date rendering is already pinned by existing tests. Say so in the file header so a later reader does not "tighten" it back into flakiness.

- [ ] **Step 1: Write the normalizer**

Create `tests/Scrinia.Tests/Golden/GoldenNormalizer.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Scrinia.Tests.Golden;

/// <summary>
/// Strips the three fields of an MCP YAML response that are not a pure function of the
/// fixture, so a golden file stays stable across days and machines.
///
/// Deliberately blunt about dates: this harness pins STRUCTURE across a refactor, not date
/// formatting. `created:` / `updated:` rendering is pinned by the existing search-result
/// tests. Do not narrow these patterns to "make the golden stricter" — recency scoring makes
/// scores drift daily and the temp-dir GUID changes every run.
/// </summary>
internal static class GoldenNormalizer
{
    private static readonly Regex Score = new(@"score=\d+(\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex TempDir = new(@"scrinia_test_[0-9a-fA-F]{32}", RegexOptions.Compiled);
    private static readonly Regex IsoTimestamp = new(@"\d{4}-\d{2}-\d{2}T[\d:.]+(?:Z|[+\-]\d{2}:\d{2})", RegexOptions.Compiled);
    private static readonly Regex BareDate = new(@"\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

    public static string Normalize(string yaml)
    {
        string s = yaml.Replace("\r\n", "\n");
        s = Score.Replace(s, "score=<SCORE>");
        s = TempDir.Replace(s, "<WORKSPACE>");
        s = IsoTimestamp.Replace(s, "<TIMESTAMP>");
        s = BareDate.Replace(s, "<DATE>");
        return s.TrimEnd('\n');
    }
}
```

- [ ] **Step 2: Write the golden test**

Create `tests/Scrinia.Tests/Golden/ResponseGoldenTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests.Golden;

/// <summary>
/// Characterization tests. These assert nothing about whether the output is GOOD — only that
/// it is UNCHANGED. They exist to make the ToYaml-at-the-boundary refactor provably
/// behaviour-preserving.
///
/// To re-baseline after an INTENTIONAL output change, set SCRINIA_UPDATE_GOLDEN=1 and re-run.
/// During the response-shape refactor, needing to re-baseline means the refactor changed
/// behaviour — investigate, do not re-baseline.
///
/// `guide` and `restore` are deliberately excluded: both fold live workspace state
/// (available skills, today's session log) into the body, so they are pinned by their own
/// structural tests instead.
/// </summary>
public class ResponseGoldenTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static void Seed(IMemoryStore store)
    {
        store.Upsert(new ArtifactEntry("auth-flow", "uri", 2048, 1, Fixed,
            ContentPreview: "How session rotation works.",
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["topic"] = ["auth", "security"],
                ["person"] = ["jolene"],
            }), "local:findings");

        store.Upsert(new ArtifactEntry("retry", "uri", 1024, 1, Fixed,
            ContentPreview: "Exponential backoff with jitter.",
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["topic"] = ["resilience"],
            }), "local:patterns");
    }

    public static TheoryData<string> Cases() =>
    [
        "remember", "recall", "search", "search-filtered",
        "list-summary", "list-full", "append", "forget",
        "compact", "link", "reconcile-scan", "facts-entities",
        "error-missing-path", "error-empty-content",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Response_MatchesGolden(string name)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var tools = new ScriniaMcpTools();

        string yaml = name switch
        {
            "remember"        => await tools.MemoryRemember("/findings/new-note", ["first chunk", "second chunk"]),
            "recall"          => await tools.MemoryRecall("/findings/auth-flow"),
            "search"          => await tools.MemorySearch("session rotation"),
            "search-filtered" => await tools.MemorySearch("session rotation", pathPrefix: "/findings/"),
            "list-summary"    => await tools.MemoryList(),
            "list-full"       => await tools.MemoryList(mode: "full"),
            "append"          => await tools.MemoryAppend("/findings/auth-flow", "an appended chunk"),
            "forget"          => await tools.MemoryForget("/patterns/retry"),
            "compact"         => await tools.MemoryCompact("/findings/auth-flow"),
            "link"            => await tools.MemoryLink("/findings/auth-flow", "/patterns/retry"),
            "reconcile-scan"  => await tools.MemoryReconcile(),
            "facts-entities"  => await tools.Facts("entities"),
            "error-missing-path" => await tools.MemoryRecall(""),
            "error-empty-content" => await tools.MemoryRemember("/findings/x", []),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown golden case"),
        };

        string actual = GoldenNormalizer.Normalize(yaml);
        string path = GoldenPath(name);

        if (Environment.GetEnvironmentVariable("SCRINIA_UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        string expected = GoldenNormalizer.Normalize(File.ReadAllText(path));
        actual.Should().Be(expected,
            $"golden '{name}' must be byte-identical; set SCRINIA_UPDATE_GOLDEN=1 only for an intentional change");
    }

    /// <summary>Golden files live next to the source, not in bin/ — they are committed artifacts.</summary>
    private static string GoldenPath(string name, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "__snapshots__", $"{name}.yaml");
}
```

- [ ] **Step 3: Build**

Run: `dotnet build Scrinia.sln -c Debug`
Expected: succeeds with 0 errors.

- [ ] **Step 4: Generate the goldens**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ResponseGoldenTests"`
Expected: PASS (15 cases — 14 theory cases plus the SearchAndSearchFiltered_ProduceDifferentOutput guard added in Task 1 fix round 1). First run writes `__snapshots__/*.yaml` because the files do not exist yet.

- [ ] **Step 5: Verify the goldens are stable, not just written**

Run the same command a second time:
`dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ResponseGoldenTests"`
Expected: PASS (15 cases — 14 theory cases plus the SearchAndSearchFiltered_ProduceDifferentOutput guard added in Task 1 fix round 1), this time *comparing* against the files written in step 4.

If any case fails on the second run, a volatile field escaped `GoldenNormalizer`. Read the FluentAssertions diff, add a pattern, delete `__snapshots__/`, and repeat steps 4–5. Do not proceed with a case that only passes on the write path.

- [ ] **Step 6: Inspect one golden by eye**

Open `tests/Scrinia.Tests/Golden/__snapshots__/search.yaml`. Confirm it contains `status: success`, `action: searched`, a `results:` count, and at least one `[1] entry` block. A golden of `status: success` with an empty body means the fixture did not seed — fix the fixture before continuing, or the harness will happily pin nothing.

- [ ] **Step 7: Commit**

```bash
git add tests/Scrinia.Tests/Golden/
git commit -m "test(mcp): pin every tool's YAML output before the response-shape refactor

Characterization only — asserts the output is unchanged, not that it is good.
Scores, temp-dir GUIDs and dates are normalized because recency scoring makes
scores drift daily. guide and restore are excluded: both fold live workspace
state into the body."
```

---

### Task 2: Pin the defect — body text must not be read as envelope status

> **Amendment (2026-08-07, human ruling during execution):** Task 2 does **not** get its own
> commit. Its tests and Task 3's fix land in a **single commit**, so no commit on this branch is
> ever red and `git bisect` never lands on a failing tree.
>
> The red step is still performed and still evidenced — the implementer writes these tests
> first, runs them, and records the failing output in its report — but the commit happens only
> after Task 3's fix makes them green. Tasks 2 and 3 are therefore dispatched together to one
> implementer, and the "Commit the failing test" step below is superseded by Task 3's commit.

The bug is that `ToYaml` writes content as an unescaped `content: |` block indented two spaces, and `string.Contains` ignores the indent. These tests fail on `main` today. They are the reason the branch exists, so they are written and run red before the fix.

**Files:**
- Create: `tests/Scrinia.Tests/ResponseStatusIsolationTests.cs`

**Interfaces:**
- Consumes: `ScriniaMcpTools.MemoryRemember`, `ScriniaMcpTools.MemoryRecall`, `ScriniaCommands.Restore`, `TestHelpers.StoreScope`.
- Produces: nothing consumed by later tasks; it is a permanent regression guard.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/ResponseStatusIsolationTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// A memory body is data. It must never be able to impersonate the response envelope.
///
/// ToYaml emits content as `content: |` with each line indented two spaces and no escaping,
/// so a body line reading "status: error" is a substring of the rendered document. Every
/// consumer that greps the rendered string therefore misreads a successful response as a
/// failure. These tests pin the consumers, not the format.
/// </summary>
public class ResponseStatusIsolationTests
{
    private const string Poison = "When the tool fails you will see status: error in the envelope.";

    [Fact]
    public async Task Recall_OfBodyContainingStatusError_IsStillSuccess()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();

        await tools.MemoryRemember("/findings/response-format", [Poison]);
        string yaml = await tools.MemoryRecall("/findings/response-format");

        yaml.Should().StartWith("status: success");
    }

    [Fact]
    public async Task CliRestore_WithPoisonedMemory_ExitsZero()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/patterns/response-format", [Poison]);

        int exitCode = await new ScriniaCommands().Restore(workspaceRoot: scope.WorkspaceDir);

        exitCode.Should().Be(0, "the memory body is data, not the response envelope");
    }

    /// <summary>
    /// The mirror defect, found while planning this branch. ScriniaCommands.Memory.cs:406 and
    /// :449 guard with `result.StartsWith("Error:")` — a shape ToYaml has never produced, since
    /// every envelope starts with `status: `. Both branches are dead, so `scri show` and
    /// `scri forget` report success on a genuine not-found.
    /// </summary>
    [Fact]
    public async Task CliShow_OfMissingMemory_ExitsNonZero()
    {
        using var scope = new TestHelpers.StoreScope();

        int exitCode = await new ScriniaCommands().Show("/findings/does-not-exist",
            workspaceRoot: scope.WorkspaceDir);

        exitCode.Should().Be(1, "a not-found recall is a failure, not a success");
    }
}
```

**Before writing this third test, confirm the CLI method name.** `ScriniaCommands.Memory.cs:~398` is the command that calls `tools.Recall`; the plan assumes it is named `Show`. Run `rg -n "tools\.Recall" -B 12 src/Scrinia/Commands/ScriniaCommands.Memory.cs` and use the actual method name and parameter list.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ResponseStatusIsolationTests"`
Expected: **two** failures.
- `CliRestore_WithPoisonedMemory_ExitsZero` — `Expected exitCode to be 0, but found 1`.
- `CliShow_OfMissingMemory_ExitsNonZero` — `Expected exitCode to be 1, but found 0`.

`Recall_OfBodyContainingStatusError_IsStillSuccess` is expected to PASS already — `MemoryRecall` returns the envelope directly and nothing greps it. It is included because it is the property the whole branch preserves, and it must keep passing through Task 3.

If the CLI test passes unexpectedly, the poisoned body did not reach the restore envelope. Confirm `Restore` folds memory content into `ContentSections` (`MemoryTools.Lifecycle.cs:589`) and that `/patterns/` is one of the scopes it gathers; adjust the memory path until the body is present in the response, then re-run.

- [ ] **Step 3: ~~Commit the failing test~~ — SUPERSEDED**

Per the amendment at the top of this task, do **not** commit here. Record the red output in the
report and carry straight on into Task 3; the tests are committed together with the fix, using
Task 3's commit message extended with the paragraph below.

<details><summary>Superseded original step (kept for the record)</summary>

```bash
git add tests/Scrinia.Tests/ResponseStatusIsolationTests.cs
git commit -m "test(mcp): pin two exit-code defects the rendered-YAML coupling causes

scri restore exits 1 on a fully successful run when any restored memory
contains the text 'status: error' — the content block is indented but not
escaped, so a substring check cannot tell body from envelope.

The mirror defect: scri show and scri forget guard on StartsWith(\"Error:\"),
a shape ToYaml has never emitted, so both report success on a real not-found.

Both red until the response-shape refactor."
```

</details>

---

### Task 3: Flip `Scrinia.Mcp` and the CLI to `McpResponse`

> **Amendment (2026-08-07):** this task also carries Task 2's tests, in the same commit. Its
> commit message gains this paragraph:
>
> ```
> The two exit-code regression tests land in this commit rather than red ahead of
> it: scri restore exited 1 on success whenever a restored memory contained the
> text 'status: error', and scri show / scri forget exited 0 on a real not-found
> because their guards tested StartsWith("Error:"), a shape ToYaml never emits.
> ```

**This task is irreducibly atomic and that is a deliberate choice.** The CLI calls the internal handlers directly through `InternalsVisibleTo("scri")` — `tools.Recall`, `tools.Remember`, `tools.Forget`, `tools.Link`, `tools.Compact`, `tools.Restore`, `tools.Reconcile`, `tools.Export`, `tools.Import`. Retyping a handler breaks its public method *and* its CLI caller in the same compile. Splitting this into smaller commits requires adding string-returning shims and then deleting them, which is more churn and more risk than one mechanical commit with two green gates in front of it.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — `Recall`, `Remember`, `UpdateMeta`, `List`, `Search`, `Copy`, `Forget`, `Append`, `References`, `Link`, `BuildDriftList`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:60` `Reconcile`, `:497` `Compact`, `:586` `Restore`, `:20` `Bundle`
- Modify: `src/Scrinia.Mcp/MemoryTools.Bundling.cs:19` `Export`, `:73` `Import`
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs:120` `SkillCreate`, `:229` `SkillLoad`
- Modify: `src/Scrinia.Mcp/MemoryTools.Facts.cs:18` `Facts`
- Modify: `src/Scrinia.Mcp/FactsResponseFormatter.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` — 11 public methods, `TryRouteSkillRecall`, `TryRouteSkillRemember`
- Modify: `src/Scrinia.Mcp/McpResponse.cs:210-241` — delete `InjectInfoHint`
- Modify: `src/Scrinia/Commands/ScriniaCommands.cs:41-57` `EmitMcpResult`, `:100`, `:144`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:371`, `:404`, `:447`, `:504`, `:524`, `:547`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs:48`, `:85`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Facts.cs:40`
- Modify: `tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs:36`

**Interfaces:**
- Consumes: `McpResponse`, `ResponseBuilder`, `ErrorCodes` (all unchanged), plus the Task 1 goldens and the Task 2 test as gates.
- Produces:
  - 20 handlers with signature `Task<McpResponse>` (same names, same parameters).
  - `ScriniaCommands.EmitMcpResult(McpResponse r, bool json, string action) → int`.
  - `FactsResponseFormatter.ToResponse(FactsResult r) → McpResponse` (replaces `ToYaml`).
  - `InjectInfoHint` **removed** — later tasks must not call it.

- [ ] **Step 1: Retype the handlers**

For every handler listed above, change `Task<string>` to `Task<McpResponse>` and delete `.ToYaml()` from its return expressions. The bodies are otherwise untouched. Example, `MemoryTools.Core.cs:22`:

```csharp
// before
internal async Task<string> Recall(...)
{
    ...
    return ResponseBuilder.Error("...", ErrorCodes.NotFound, "...").ToYaml();
}

// after
internal async Task<McpResponse> Recall(...)
{
    ...
    return ResponseBuilder.Error("...", ErrorCodes.NotFound, "...");
}
```

Non-async handlers returning `Task.FromResult(x.ToYaml())` become `Task.FromResult(x)`. The two skill routers keep their nullable shape: `Task<McpResponse>? TryRouteSkillRecall(...)`.

- [ ] **Step 2: Fix `Link` to read the typed status**

`MemoryTools.Core.cs:1613-1621`:

```csharp
McpResponse result1 = await UpdateMeta(from, keywords: [$"ref:{to}"], cancellationToken: cancellationToken);
McpResponse result2 = await UpdateMeta(to, keywords: [$"ref:{from}"], cancellationToken: cancellationToken);

if (result1.Status == "error" || result2.Status == "error")
    return ResponseBuilder.Error(
        $"Partial link failure:\n  {from}: {result1.Error ?? result1.Content}\n  {to}: {result2.Error ?? result2.Content}",
        ErrorCodes.Internal,
        $"Verify both '{from}' and '{to}' exist via recall.");
```

- [ ] **Step 3: Serialize in the 12 public tool methods**

In `MemoryTools.cs`, each `[McpServerTool]` method calls `.ToYaml()` exactly once on the way out. `MemoryRemember` also absorbs the deleted `InjectInfoHint`:

```csharp
McpResponse response = await Remember(content, path,
    keywords: keywords, attributes: attributes,
    actionLabel: "remembered", cancellationToken: cancellationToken);

// Was InjectInfoHint doing string surgery on rendered YAML. The no-op-on-error rule is
// preserved deliberately: errors carry recovery text on actionNeeded, not info.
string? typoHint = ReservedPrefixGuard.HintFor(path);
if (typoHint is not null && response.Status != "error")
    response = response.WithInfo([.. response.Info ?? [], typoHint]);

return response.ToYaml();
```

Apply the same shape in `MemoryAppend`.

`FactsResponseFormatter` (`src/Scrinia.Mcp/FactsResponseFormatter.cs:15-28`) renames `ToYaml` to `ToResponse` and returns the typed value; its two private helpers drop their trailing `.ToYaml()`:

```csharp
public static McpResponse ToResponse(FactsResult result) => result switch
{
    EntitiesListResult r  => Success(Entities(r)),
    ProfileResult r       => Success(Profile(r)),
    IntersectionResult r  => Success(Intersection(r)),
    DisambiguationResult r => Warning(Disambiguation(r)),
    NotFoundResult r      => Warning($"not found: no entity named \"{r.Arg}\""),
    _                     => Success("no result"),
};

private static McpResponse Success(string content) =>
    ResponseBuilder.Success(content).WithAction("facts");
private static McpResponse Warning(string content) =>
    ResponseBuilder.Warning(content).WithAction("facts");
```

`Facts` in `MemoryTools.Facts.cs:57` becomes `return Task.FromResult(FactsResponseFormatter.ToResponse(result).ToYaml());`. Its three inline `ResponseBuilder.Error(...).ToYaml()` guards at `:38`, `:45` and `:51` **keep** their `.ToYaml()` — `Facts` is itself a public tool method, so it is already at the serialization boundary.

Update `ScriniaCommands.Facts.cs:40`, which calls `FactsResponseFormatter` on the CLI side, to match the new name.

- [ ] **Step 4: Delete `InjectInfoHint`**

Remove `McpResponseExtensions.InjectInfoHint` entirely — `src/Scrinia.Mcp/McpResponse.cs:204-241`, including its doc comment. Leave `YamlEscape` and `ToYaml` alone.

- [ ] **Step 5: Retype `EmitMcpResult` and its three call sites**

`src/Scrinia/Commands/ScriniaCommands.cs:41`:

```csharp
private static int EmitMcpResult(McpResponse r, bool json, string action)
{
    bool isError = r.Status == "error";

    if (json)
    {
        // --json output is unchanged: CliMcpOutput still carries the rendered YAML.
        WriteJson(new CliMcpOutput(action, r.Status, r.ToYaml()), CliJsonContext.Default.CliMcpOutput);
    }
    else
    {
        string yaml = r.ToYaml();
        Console.Write(yaml);
        if (!yaml.EndsWith('\n')) Console.WriteLine();
    }
    return isError ? 1 : 0;
}
```

Call sites become `EmitMcpResult(await tools.Restore(ct), json, "restore")` and so on. `ScriniaCommands.cs:107` passes the YAML to `HintCommand.FormatRestoreHook`, which takes a string — call `.ToYaml()` there:

```csharp
McpResponse response = await tools.Restore(cancellationToken);
if (hook)
{
    Console.WriteLine(HintCommand.FormatRestoreHook(response.ToYaml(), HintCommand.ParseClient(client)));
    return 0;
}
return EmitMcpResult(response, json, "restore");
```

- [ ] **Step 6: Retype the CLI sites that render their own output, and fix the two dead error branches**

Five sites assign `string result = await tools.X(...)` and then print or inspect it: `ScriniaCommands.Memory.cs:371` (Remember), `:404` (Recall), `:447` (Forget), and `ScriniaCommands.Bundle.cs:48` (Export), `:85` (Import). Change each to `McpResponse result = ...` and call `.ToYaml()` at the point of printing.

Two of them carry a **dead** error guard. `Memory.cs:406` and `:449` read:

```csharp
if (result.StartsWith("Error:", StringComparison.Ordinal))
{
    if (json) { WriteJsonError(result); return 1; }
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(result)}[/]");
    return 1;
}
```

`ToYaml` always begins with `status: `, so `"Error:"` has never matched — `scri show` and `scri forget` return 0 on a genuine not-found. Replace both with:

```csharp
if (result.Status == "error")
{
    string message = result.Error ?? "unknown error";
    if (json) { WriteJsonError(message); return 1; }
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
    return 1;
}
```

**This is a user-visible behaviour change**, and the only one on the branch: those two commands now report failure when they fail. It is not covered by the byte-identical-YAML rule, which constrains the MCP envelope, not CLI exit codes. It must appear in the CHANGELOG.

`Memory.cs:371` (Remember) has no such guard — leave its control flow alone and only retype it.

- [ ] **Step 7: Retype the benchmark harness**

`tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs:36` has the same defect: a benchmark memory containing `status: error` throws a spurious write failure and corrupts a retrieval result. `Scrinia.Mcp` already grants `InternalsVisibleTo("Scrinia.Tests")`, so call the internal handler:

```csharp
/// <summary>
/// Surfaces a failed write instead of letting it pass silently. A discarded error turns an
/// infrastructure failure (lock timeout under load) into a misleading correctness verdict —
/// the memory keeps its previous content and the benchmark reports it as stale data.
/// </summary>
private static void ThrowIfWriteFailed(McpResponse response, string name)
{
    if (response.Status == "error")
        throw new InvalidOperationException($"Write to '{name}' failed:\n{response.Error}");
}
```

Update its caller to hold the `McpResponse` rather than a string.

- [ ] **Step 8: Build**

Run: `dotnet build Scrinia.sln -c Debug`
Expected: succeeds with 0 errors.

Expect a long error list on the first attempt — that is the type system enumerating the call graph for you. Work the list until clean; do not add `.ToYaml()` inside a handler to silence one.

- [ ] **Step 9: Run the golden gate**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ResponseGoldenTests"`
Expected: PASS (15 cases — 14 theory cases plus the SearchAndSearchFiltered_ProduceDifferentOutput guard added in Task 1 fix round 1).

A failure here means the refactor changed output. **Do not set `SCRINIA_UPDATE_GOLDEN`.** Read the diff and find what moved.

- [ ] **Step 10: Run the defect gate**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ResponseStatusIsolationTests"`
Expected: PASS (3 tests). `CliRestore_WithPoisonedMemory_ExitsZero` and `CliShow_OfMissingMemory_ExitsNonZero` were both red in Task 2 and are now green.

- [ ] **Step 11: Run the full suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Expected: all green.

The ~30 existing `.Should().Contain("status: error")` assertions must pass **unmodified** — they assert against the public tool methods, which still return strings. Needing to edit one is a signal the refactor leaked; investigate rather than adjusting the assertion.

- [ ] **Step 12: Run the server suite**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`
Expected: all green. `Scrinia.Server` references `Scrinia.Mcp`, so it is in the blast radius even though this task changes no server file.

- [ ] **Step 13: Commit**

```bash
git add src/Scrinia.Mcp src/Scrinia/Commands tests/Scrinia.Tests/Benchmarks
git commit -m "refactor(mcp): return McpResponse from handlers, serialize at the tool boundary

Twenty internal handlers returned rendered YAML, so four consumers recovered
structure by substring-matching it — including the CLI exit code, which reported
failure whenever a restored memory happened to contain 'status: error'.

ToYaml now runs once, in the twelve public tool methods. InjectInfoHint and its
YAML-section splicing are deleted; the hint is applied to the typed response
before serialization, keeping its no-op-on-error rule. Link and the benchmark
harness read Status.

Also fixes two dead CLI guards on StartsWith(\"Error:\"), a shape ToYaml has
never emitted: scri show and scri forget now exit 1 on a not-found instead of
reporting success.

MCP output is byte-identical: the golden suite added in the previous commit
passes unchanged, as do the existing string assertions."
```

---

### Task 4: Extract `SearchResultRenderer`

With returns typed, the ~205-line rendering block in `MemoryTools.Core.cs` can move out. All eight methods are already `static` with explicit parameters, so this is a move, not a redesign.

**Note a correction to the spec:** it estimated ~350 lines and a drop to ~1380. The real block is `MemoryTools.Core.cs:932-1136` — about 205 lines, leaving the file near 1526.

**Files:**
- Create: `src/Scrinia.Mcp/SearchResultRenderer.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:834`, `:932-1136`

**Interfaces:**
- Consumes: `IMemoryStore`, `SearchResult`, `EntityFactSummary`, `AttributeReaders` — all unchanged.
- Produces: `SearchResultRenderer.FormatRichSearchResults(IMemoryStore store, IReadOnlyList<SearchResult> matches, IReadOnlyList<EntityFactSummary> entityFacts) → string` and `SearchResultRenderer.AppendResultBlock(StringBuilder sb, IMemoryStore store, SearchResult match, int index)`. `AppendResultBlock` stays `internal` — it has an external caller.

- [ ] **Step 1: Find every caller of the methods being moved**

Run: `rg -n "FormatRichSearchResults|AppendResultBlock|AppendAnchors|AppendEntityFactsBlock|AppendTagsLine|AppendDatesLine|AppendAttributesLine|AppendDescription" --type cs src tests benchmarks`

Record the list. `AppendResultBlock` and `AppendAnchors` are `internal` and may have callers outside `MemoryTools.Core.cs`; the other six are `private` and cannot. Every caller found must be updated in step 3.

- [ ] **Step 2: Create the file by moving the block verbatim**

Create `src/Scrinia.Mcp/SearchResultRenderer.cs` containing, moved unchanged from `MemoryTools.Core.cs:932-1136`: the `MaxDescriptionChars = 1500` and `MaxAnchorsPerResult = 7` constants, `FormatRichSearchResults`, `AppendEntityFactsBlock`, `AppendResultBlock`, `AppendTagsLine`, `AppendDatesLine`, `AppendAttributesLine`, `AppendDescription`, `AppendAnchors` — including the block-layout doc comment on `FormatRichSearchResults`.

```csharp
using System.Text;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

/// <summary>
/// Renders ranked <see cref="SearchResult"/>s into the plain-text body that the MCP search
/// response carries inside its `content: |` block. Formatting only — no store access beyond
/// name qualification, no ranking, no filtering.
/// </summary>
internal static class SearchResultRenderer
{
    private const int MaxDescriptionChars = 1500;
    private const int MaxAnchorsPerResult = 7;

    // ... the eight methods, moved verbatim. `private static` members keep `private`;
    // `FormatRichSearchResults` and `AppendResultBlock` become `internal static`.
}
```

Delete the same block from `MemoryTools.Core.cs`.

- [ ] **Step 3: Update the call sites**

`MemoryTools.Core.cs:834` becomes:

```csharp
string body = SearchResultRenderer.FormatRichSearchResults(store, outcome.Matches, outcome.EntityFacts);
```

Update every other caller recorded in step 1 the same way.

- [ ] **Step 4: Build**

Run: `dotnet build Scrinia.sln -c Debug`
Expected: succeeds with 0 errors.

- [ ] **Step 5: Run the golden gate**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ResponseGoldenTests"`
Expected: PASS (15 cases — 14 theory cases plus the SearchAndSearchFiltered_ProduceDifferentOutput guard added in Task 1 fix round 1). The `search` and `search-filtered` goldens are the ones that matter — a pure move must not change a byte.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Expected: all green.

- [ ] **Step 7: Verify the file actually shrank**

Run: `wc -l src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/SearchResultRenderer.cs`
Expected: `MemoryTools.Core.cs` near 1526 (down from 1731), `SearchResultRenderer.cs` near 210. If `MemoryTools.Core.cs` did not shrink by roughly 205 lines, the block was copied rather than moved — delete the original.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Mcp/SearchResultRenderer.cs src/Scrinia.Mcp/MemoryTools.Core.cs
git commit -m "refactor(mcp): move search-result rendering out of MemoryTools.Core

MemoryTools.Core.cs was the repo's highest-churn file at 1731 lines, holding
both the tool handlers and ~205 lines of StringBuilder formatting. The
formatting moves to SearchResultRenderer unchanged — the golden suite confirms
byte-identical output."
```

---

### Task 5: Naming and dependency cleanup

Two small items the review flagged in these files. Grouped because each is a one-line change that does not warrant its own review gate, and both are confined to files the branch already touches.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:1586`
- Modify: `src/Scrinia.Mcp/Scrinia.Mcp.csproj:16` (conditionally)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing. Both changes are internal.

- [ ] **Step 1: Fix the typo'd method name**

`MemoryTools.Core.cs:1586` — rename `MatchesRefAttributeb` to `MatchesRefAttribute`, and update its callers.

Run first: `rg -n "MatchesRefAttributeb" --type cs src tests`

- [ ] **Step 2: Audit the YamlDotNet reference**

`McpResponse.cs:142-144` states the serializer is hand-rolled specifically to avoid a YamlDotNet dependency ("AOT-safe, zero reflection"), yet `Scrinia.Mcp.csproj:16` still declares `<PackageReference Include="YamlDotNet" Version="16.*" />`.

Run: `rg -n "YamlDotNet|using YamlDotNet" --type cs src/Scrinia.Mcp`

- If there are **no** hits in `src/Scrinia.Mcp`, delete the `PackageReference` line.
- If there **are** hits, leave the reference and delete the "No YamlDotNet dependency" claim from the `McpResponseExtensions` doc comment instead — the comment is then wrong and is exactly the kind of stale prose the review flagged.

Note: `tests/Scrinia.Tests/TestHelpers.cs:5` has `using YamlDotNet.Serialization;`, but `Scrinia.Tests` declares its own reference. A test-project usage does not justify the reference in `Scrinia.Mcp`.

- [ ] **Step 3: Build**

Run: `dotnet build Scrinia.sln -c Debug`
Expected: succeeds with 0 errors. If removing the package reference breaks the build, restore the line — that is the "there are hits" branch of step 2.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp
git commit -m "chore(mcp): fix MatchesRefAttribute typo and reconcile the YamlDotNet claim"
```

---

## Branch close

- [ ] **Add the CHANGELOG entry** under `[Unreleased]` in `CHANGELOG.md`, covering both user-visible fixes:
  - **Fixed:** `scri restore`, `scri reconcile`, `scri facts`, `scri append`, `scri compact` and `scri link` returned exit code 1 on a successful run whenever the response body contained the text `status: error`.
  - **Fixed:** `scri show` and `scri forget` returned exit code 0 when the memory did not exist. They now return 1. Scripts that relied on the old always-zero behaviour will start seeing failures — which is the point, but it is a behaviour change and belongs under a `Changed` note as well as `Fixed`.

- [ ] **Record residuals** to `.scrinia` under `/findings/mcp-response-shape-deferred-followups` if any finding was consciously shipped, matching how the last three branches closed. If nothing was deferred, skip — do not write an empty finding.

- [ ] **Final gate:** `dotnet build Scrinia.sln -c Debug` clean, then `Scrinia.Tests` and `Scrinia.Server.Tests` green, run one at a time.

## Out of scope for this branch

Carried by later branches in `docs/superpowers/specs/2026-08-06-design-review-remediation-design.md`:

- `SearchQuery`, the `SearchAll` overload collapse, `PluginPipeline` unification, server-side filtering — branch `search-contract`.
- `ILocalStoreLayout`, the sink install point, the server naming pass — branch `store-boundaries`.
- Envelope hardening — rejected in the spec; the defect is parsing the rendered string, not the format.
