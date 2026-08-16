# Ephemeral Addressing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `/temp/x` and `~x` the same address on every agent-facing surface, and stop the CLI reporting success over an ephemeral write it discards.

**Architecture:** `/temp/` is a reserved-path alias on the *surface*. There are **two** surfaces, not one: the MCP facade (`MemoryTools.cs`) and the CLI commands (`ScriniaCommands.Memory.cs`), which call the internal `MemoryTools.Core` methods directly and bypass the facade entirely. Both canonicalize; nothing below them ever sees the alias. `IsEphemeral` and `StripEphemeralPrefix` stay `~`-only.

**Tech Stack:** C# / .NET, xunit 2.9.3 (+ Xunit.SkippableFact), Spectre.Console, ASP.NET Core minimal APIs.

**Spec:** `docs/superpowers/specs/2026-08-16-ephemeral-addressing-design.md` (committed at `5da9417`)

**Branch:** `fix/ephemeral-addressing`

> **This plan was revised after a falsification pass** (see "Corrections from falsification" at the end). Eight factual errors in the first draft were fixed, including a task ordering that would have left an intermediate commit worse than HEAD. Do not restore the original ordering.

## Global Constraints

- **Task order is load-bearing.** The CLI guard (Task 2) must land *before* the literals are deleted (Task 3). The CLI calls the internal `Remember`/`Append`, so deleting the internal canonicalization first would make `scri store /temp/x` silently write a *persistent* memory under `local:temp` with exit 0 — a worse defect than the one being fixed, sitting in a bisectable commit.
- The C# string literal `"/temp/"` (with quotes) must survive in `src/` **only** in `MemoryNaming.cs`. The check is exactly `grep -rn '"/temp/"' --include=*.cs src/ | grep -v MemoryNaming.cs`. Bare `/temp/` in prose and doc comments is fine and expected — nine such mentions survive by design.
- Prefix matching is `StringComparison.OrdinalIgnoreCase`.
- `IsEphemeral` and `StripEphemeralPrefix` remain `~`-only.
- Responses and error messages echo the **canonical** `~x`.
- Exactly **one** pre-existing test file changes: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` (one test, in Task 3, for a named and understood reason). Any *other* pre-existing test going red means the store layer changed — stop and report.
- Build with `dotnet build Scrinia.sln`. Do not add `-p:NuGetAudit=false`.
- CLI commands take the address as a positional `[Argument]`: `scri store /temp/x`, not `--name`.
- `using Xunit;` is redundant in test files — both test projects have `global using Xunit;` in `GlobalUsings.cs` and emit CS8933 for it. Do not add it.

---

### Task 1: The alias rule, with one home

**Files:**
- Modify: `src/Scrinia.Core/MemoryNaming.cs` (add after `StripEphemeralPrefix`, ends `:37`)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:273-274`
- Test: `tests/Scrinia.Tests/EphemeralAliasTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `MemoryNaming.IsEphemeralAlias(string) → bool`, `MemoryNaming.CanonicalizeEphemeralAlias(string) → string`, `MemoryNaming.IsEphemeralName(string) → bool`. Tasks 2, 3, 4 all use these.

**Why `IsEphemeralName`, which the spec did not call for:** Task 2's CLI guard must ask "is this ephemeral?" without an `IMemoryStore` instance, and `IsEphemeral` is an instance member. Without this it would hand-roll the tilde check — a second home for the predicate, the exact defect this branch removes. Mirrors how `StripEphemeralPrefix` already works (`FileMemoryStore.cs:299-301` is `[Obsolete]` and delegates).

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/EphemeralAliasTests.cs`:

```csharp
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// The "/temp/" surface alias. These pin the rule itself; the round-trip behaviour it enables
/// is pinned in EphemeralAliasRoundTripTests.
/// </summary>
public class EphemeralAliasTests
{
    [Theory]
    [InlineData("/temp/x", "~x")]
    [InlineData("/temp/scratch", "~scratch")]
    [InlineData("/temp/a/b", "~a/b")]
    [InlineData("/TEMP/x", "~x")]
    [InlineData("/Temp/Probe", "~Probe")]   // prefix folds case; the subject does NOT
    public void CanonicalizeEphemeralAlias_RewritesTempPrefix(string input, string expected)
        => Assert.Equal(expected, MemoryNaming.CanonicalizeEphemeralAlias(input));

    [Theory]
    [InlineData("~x")]
    [InlineData("/temp")]              // no trailing slash — an ordinary persistent path
    [InlineData("/tempest/x")]
    [InlineData("/findings/SEC-001")]
    [InlineData("temp/x")]             // no leading slash
    [InlineData("")]
    public void CanonicalizeEphemeralAlias_LeavesEverythingElseAlone(string input)
        => Assert.Equal(input, MemoryNaming.CanonicalizeEphemeralAlias(input));

    [Theory]
    [InlineData("/temp/x", true)]
    [InlineData("/TEMP/x", true)]
    [InlineData("/temp", false)]
    [InlineData("/tempest/x", false)]
    [InlineData("~x", false)]          // canonical form is NOT the alias
    [InlineData("", false)]
    public void IsEphemeralAlias_MatchesOnlyTheTempPrefix(string input, bool expected)
        => Assert.Equal(expected, MemoryNaming.IsEphemeralAlias(input));

    [Theory]
    [InlineData("~x", true)]
    [InlineData("~", true)]
    [InlineData("/temp/x", false)]     // an alias is not yet an ephemeral NAME
    [InlineData("/findings/a", false)]
    [InlineData("", false)]
    public void IsEphemeralName_MatchesOnlyTheTildeForm(string input, bool expected)
        => Assert.Equal(expected, MemoryNaming.IsEphemeralName(input));

    /// <summary>
    /// The delegation in Step 4 must be behaviour-preserving, and the body it replaces is
    /// null-safe. Pin that.
    /// </summary>
    [Fact]
    public void IsEphemeralName_IsNullSafe()
        => Assert.False(MemoryNaming.IsEphemeralName(null!));

    /// <summary>
    /// D-4: the store predicate stays "~"-only. If this goes red, a surface alias has leaked
    /// into the store layer and the design has been violated.
    /// </summary>
    [Fact]
    public void FileMemoryStore_IsEphemeral_DoesNotKnowTheAlias()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-alias-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileMemoryStore(dir);
            Assert.True(store.IsEphemeral("~x"));
            Assert.False(store.IsEphemeral("/temp/x"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EphemeralAliasTests"`

Expected: FAIL to compile — `CS0117: 'MemoryNaming' does not contain a definition for 'IsEphemeralAlias'`.

- [ ] **Step 3: Add the three members**

In `src/Scrinia.Core/MemoryNaming.cs`, after `StripEphemeralPrefix` (ends `:37`):

```csharp
    /// <summary>
    /// True for an ephemeral name in its canonical form (<c>~subject</c>).
    /// <para>THE static authority for the predicate. <see cref="FileMemoryStore.IsEphemeral"/>
    /// delegates here, the same way <see cref="StripEphemeralPrefix"/> already owns the strip.
    /// Null-safe, because the body it replaced was.</para>
    /// </summary>
    public static bool IsEphemeralName(string name) =>
        !string.IsNullOrEmpty(name) && name[0] == '~';

    /// <summary>
    /// True for the ephemeral SURFACE alias — a path under the reserved temp prefix.
    /// <para>The alias is a surface concern, canonicalized at the MCP facade and at the CLI
    /// commands, and it never reaches the store or the wire. Note this is false for
    /// <c>~x</c>: an alias is not yet an ephemeral name.</para>
    /// </summary>
    public static bool IsEphemeralAlias(string name) =>
        name is not null && name.StartsWith("/temp/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// THE ONE PLACE the ephemeral alias becomes <c>~subject</c>; returns anything else unchanged.
    ///
    /// <para>This rule used to be two verbatim string literals in <c>MemoryTools.Core.cs</c>
    /// (Remember and Append) and existed nowhere else, so <c>recall</c>, <c>forget</c>,
    /// <c>compact</c> and <c>link</c> answered NOT_FOUND for a path <c>remember</c> had just
    /// accepted — including the literal example in <c>forget</c>'s own tool description. It
    /// lives here because the aliasing rule only holds if it is intrinsic rather than layered
    /// over each site, which is the reasoning that already put scope construction in this file.</para>
    ///
    /// <para>Case folding applies to the PREFIX only; the subject keeps its casing, matching
    /// the literals this replaces.</para>
    /// </summary>
    public static string CanonicalizeEphemeralAlias(string name) =>
        IsEphemeralAlias(name) ? "~" + name["/temp/".Length..] : name;
```

- [ ] **Step 4: Delegate the store predicate**

`src/Scrinia.Core/FileMemoryStore.cs:273-274` currently reads
`!string.IsNullOrEmpty(name) && name[0] == '~';`. Replace the body:

```csharp
    public bool IsEphemeral(string name) =>
        MemoryNaming.IsEphemeralName(name);
```

Identical behavior including the null case. `HttpMemoryStore.IsEphemeral:290-291` delegates to `_localHelper`, so it inherits this with no edit.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EphemeralAliasTests"`

Expected: PASS, 24 tests (5 + 6 + 6 + 5 `InlineData`, plus 2 `Fact`).

- [ ] **Step 6: Build**

Run: `dotnet build Scrinia.sln`

Expected: 0 errors. `dotnet build`'s own summary is the gate — do not substitute a wrapper's error filter, which has previously reported a failing build as clean.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/MemoryNaming.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/EphemeralAliasTests.cs
git commit -m "feat(core): one home for the ephemeral alias, and one for the tilde predicate"
```

---

### Task 2: The CLI refuses ephemerals instead of discarding them

> **This task comes before the facade work deliberately.** The CLI calls the internal
> `MemoryTools.Core` methods (`ScriniaCommands.Memory.cs:382`, `:423`, `:471`, `:531`, `:553`,
> `:576`), which still hold the canonicalization literals at this point. Guarding the CLI first
> means the tree is never in a state where `scri store /temp/x` writes a persistent memory.

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.cs` (add after `WriteJsonError`, ends `:32`)
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs` — 6 commands
- Modify: `docs/cli-reference.md` (rows at `:720` and `:735`)
- Modify: `docs/getting-started.md:189`
- Test: `tests/Scrinia.Tests/CliEphemeralRefusalTests.cs` (create)

**Interfaces:**
- Consumes: `MemoryNaming.CanonicalizeEphemeralAlias`, `MemoryNaming.IsEphemeralName` from Task 1.
- Produces: `ScriniaCommands.RefuseEphemeral(string name, bool json, out string canonical) → bool` (private).

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/CliEphemeralRefusalTests.cs`.

Two conventions this repo requires, both learned the hard way:
`Console.SetOut` is process-wide and xUnit runs classes in parallel, so redirect inside a narrow
`try/finally` around the single call (the shape `ConsolidateTests.cs:34-45` uses) — never in a
constructor with a `Dispose` restore, which races. And `Store`/`Append` read stdin when `file` is
null, so any test that must get *past* the guard passes a real file (as `BundleExitCodeTests.cs:107-108`
does).

```csharp
using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// Review finding C1: `scri store /temp/x` reported success and exit 0 while discarding the
/// content — the ephemeral map is an in-memory dictionary and the CLI process exits
/// immediately. Every ephemeral CLI operation is meaningless for the same reason, so all six
/// commands refuse, not only the writes.
/// </summary>
public class CliEphemeralRefusalTests
{
    private static async Task<(int Exit, string Output)> RunAsync(Func<Task<int>> command)
    {
        var captured = new StringWriter();
        TextWriter previous = Console.Out;
        try
        {
            Console.SetOut(captured);
            int exit = await command();
            return (exit, captured.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private static string NewWorkspace()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static TheoryData<string> EphemeralAddresses => new() { "~scratch", "/temp/scratch" };

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Store_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Store(address, file: null, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Append_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Append(address, file: null, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Show_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Show(address, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Forget_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Forget(address, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Compact_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Compact(address, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Link_RefusesAnEphemeralAddressOnEitherEnd(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (fromExit, _) = await RunAsync(() =>
                new ScriniaCommands().Link(address, "/findings/x", workspaceRoot: dir, json: true));
            var (toExit, _) = await RunAsync(() =>
                new ScriniaCommands().Link("/findings/x", address, workspaceRoot: dir, json: true));

            Assert.Equal(1, fromExit);
            Assert.Equal(1, toExit);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The refusal names the canonical form, not the caller's spelling (D-3).</summary>
    [Fact]
    public async Task TheRefusalNamesTheCanonicalForm()
    {
        string dir = NewWorkspace();
        try
        {
            var (_, output) = await RunAsync(() =>
                new ScriniaCommands().Store("/temp/scratch", file: null, workspaceRoot: dir, json: true));

            Assert.Contains("~scratch", output, StringComparison.Ordinal);
            Assert.DoesNotContain("/temp/scratch", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The guard must not over-refuse. Passes a real file because Store reads stdin when
    /// file is null, which would block the test host.
    /// </summary>
    [Fact]
    public async Task APersistentAddressIsNotRefused()
    {
        string dir = NewWorkspace();
        string contentFile = Path.Combine(dir, "content.md");
        await File.WriteAllTextAsync(contentFile, "real content");
        try
        {
            var (exit, _) = await RunAsync(() =>
                new ScriniaCommands().Store("/findings/real", file: contentFile, workspaceRoot: dir, json: true));

            Assert.Equal(0, exit);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CliEphemeralRefusalTests"`

Expected: FAIL. The refusal tests return exit 0 with a success message — C1 reproduced.

- [ ] **Step 3: Add the guard**

In `src/Scrinia/Commands/ScriniaCommands.cs`, after `WriteJsonError` (ends `:32`). `using Scrinia.Core;` (`:10`) and `using Spectre.Console;` (`:17`) are already present.

```csharp
    /// <summary>
    /// Canonicalizes the ephemeral surface alias, then refuses any ephemeral address.
    ///
    /// <para>Every ephemeral operation from the CLI is meaningless, not just the writes: a
    /// fresh CLI process starts with an empty in-memory ephemeral map and exits before
    /// anything can read it back. `scri store ~x` therefore reported success and exit 0 over
    /// content it discarded (review finding C1), and `scri show ~x` could only ever answer
    /// NOT_FOUND. Refusing loudly is the honest outcome; the alternative — inventing a
    /// session-scoped store — is a lifecycle nothing has asked for.</para>
    ///
    /// <para>The message names the CANONICAL form, so a caller who used the alias learns the
    /// real address rather than having their own spelling read back to them.</para>
    /// </summary>
    /// <returns>True when the command must stop and return 1.</returns>
    private static bool RefuseEphemeral(string name, bool json, out string canonical)
    {
        canonical = MemoryNaming.CanonicalizeEphemeralAlias(name);
        if (!MemoryNaming.IsEphemeralName(canonical)) return false;

        string message =
            $"'{canonical}' names an ephemeral memory. Ephemeral memories live only in the "
            + "process that creates them, and the CLI exits immediately. Use a persistent path.";

        if (json) WriteJsonError(message);
        else AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
        return true;
    }
```

- [ ] **Step 4: Wire the guard into all six commands**

In `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, immediately after each command's
`WorkspaceSetup.Configure(workspaceRoot);` — at `:349` (Store), `:420` (Show), `:466` (Forget),
`:503` (Append), `:548` (Compact), `:571` (Link). This is before any content read, plugin load,
or store access.

Five commands (`Store`, `Show`, `Forget`, `Append`, `Compact`) take `name`:
```csharp
        if (RefuseEphemeral(name, json, out string canonicalName)) return 1;
        name = canonicalName;
```

`Link` takes `from` and `to`:
```csharp
        if (RefuseEphemeral(from, json, out string canonicalFrom)) return 1;
        if (RefuseEphemeral(to, json, out string canonicalTo)) return 1;
        from = canonicalFrom;
        to = canonicalTo;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CliEphemeralRefusalTests"`

Expected: PASS.

- [ ] **Step 6: Update the CLI docs — this is a breaking change**

`docs/cli-reference.md` needs **both** rows corrected. The reserved-path table at `:735` lists
`/temp/...`, and the naming table just above at `:720` lists `| ~subject | Ephemeral | In-memory
only (dies with process) |`. D-1 refuses both spellings from the CLI, so fixing only one leaves
the other advertising what this branch just disabled.

Note the section heading at `:726` is "Reserved Path Conventions" and `:738` refers to "the
`list` and `search` actions" — this table documents the MCP tool surface as much as the CLI, so
do **not** delete the rows outright. Mark them CLI-unavailable instead:

- `:720` → `| ~subject | Ephemeral | In-memory only (dies with process). **Not available from the CLI.** |`
- `:735` → `| /temp/... | Ephemeral — an alias for ~subject; both spell the same memory. **Not available from the CLI.** |`

Then add, near the `scri store` section:

```markdown
> **Ephemeral memories are not available from the CLI.** `~name` and its `/temp/...` alias live
> only in the process that creates them, and the CLI exits immediately — so a CLI ephemeral write
> would be discarded. All memory commands refuse an ephemeral address with exit 1. Use an MCP
> client for ephemerals, or a persistent path.
```

`docs/getting-started.md:189` — read the surrounding prose first. If that section documents the
MCP tools rather than the CLI, the row is correct and needs only the alias note; if it documents
the CLI, apply the same correction as above.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia/Commands/ScriniaCommands.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs docs/cli-reference.md docs/getting-started.md tests/Scrinia.Tests/CliEphemeralRefusalTests.cs
git commit -m "fix(cli): an ephemeral write is refused, not reported as stored and discarded"
```

---

### Task 3: Canonicalize at the MCP facade, and delete the two literals

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` — 6 methods at `:109`, `:197`, `:266`, `:335`, `:354`, `:374`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — delete `:139-141` and `:1039-1040`
- Modify: `src/Scrinia.Core/PathRouter.cs:93-94`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:20`
- Modify: `src/Scrinia.Mcp/prompts/guide.md:135`
- **Modify (pre-existing): `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs:1683`** — see Step 5
- Test: `tests/Scrinia.Tests/EphemeralAliasRoundTripTests.cs` (create)

**Interfaces:**
- Consumes: `MemoryNaming.CanonicalizeEphemeralAlias` from Task 1.
- Produces: no new API. After this task both surfaces accept `/temp/x` and `~x` as one address.

**Placement rule:** insert the canonicalization immediately **before** the delegating call.
Not at the top: `MemoryRecall` runs its `/skill/` route check at `:205-206` and `MemoryRemember`
at `:154-155`. The prefixes are disjoint so ordering is functionally free, but "immediately
before delegation" is one rule correct in all six places. Note `MemoryRemember` (`:181`) and
`MemoryAppend` (`:321`) assign to a local rather than `return (await …)` — canonicalize before
that assignment.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/EphemeralAliasRoundTripTests.cs`. Use the repo's `TestHelpers.StoreScope`
(`TestHelpers.cs:96-153`) rather than setting `MemoryStoreContext.Current` by hand — it also
shadows `MemoryEventSinkContext.Current` with `SilentEventSink`, and omitting that leaves the real
embeddings/maintenance pipeline installed, which `TestHelpers.cs:117-122` records as a prior cause
of intermittent failures.

```csharp
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// The regression tests the suite lacked. Every pre-existing ephemeral test writes at "/temp/x"
/// and reads back at "~x", which is why H2 survived 2,015 tests: no test used the SAME spelling
/// twice. These do.
/// </summary>
public class EphemeralAliasRoundTripTests
{
    [Fact]
    public async Task Recall_FindsAMemoryRememberedAtTheSameTempPath()
    {
        using var scope = TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["the secret note"]);

        string recalled = await tools.MemoryRecall("/temp/probe");

        Assert.Contains("the secret note", recalled, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT_FOUND", recalled, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forget_DeletesAMemoryRememberedAtTheSameTempPath()
    {
        using var scope = TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["gone soon"]);

        string forgotten = await tools.MemoryForget("/temp/probe");

        Assert.DoesNotContain("NOT_FOUND", forgotten, StringComparison.Ordinal);
        Assert.Contains("NOT_FOUND", await tools.MemoryRecall("/temp/probe"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_ExtendsAMemoryRememberedAtTheSameTempPath()
    {
        using var scope = TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["first"]);

        string appended = await tools.MemoryAppend("/temp/probe", "second");

        Assert.DoesNotContain("NOT_FOUND", appended, StringComparison.Ordinal);
        string recalled = await tools.MemoryRecall("/temp/probe");
        Assert.Contains("first", recalled, StringComparison.Ordinal);
        Assert.Contains("second", recalled, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compact_MergesAMemoryRememberedAtTheSameTempPath()
    {
        using var scope = TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["one", "two"]);

        string compacted = await tools.MemoryCompact("/temp/probe");

        Assert.DoesNotContain("NOT_FOUND", compacted, StringComparison.Ordinal);
    }

    /// <summary>link carries TWO addresses; both ends must canonicalize.</summary>
    [Fact]
    public async Task Link_ResolvesBothEndsAtTheSameTempPaths()
    {
        using var scope = TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/alpha", ["a"]);
        await tools.MemoryRemember("/temp/beta", ["b"]);

        string linked = await tools.MemoryLink("/temp/alpha", "/temp/beta");

        Assert.DoesNotContain("NOT_FOUND", linked, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing was written", linked, StringComparison.Ordinal);
    }

    /// <summary>D-3: the response teaches the caller the real address.</summary>
    [Fact]
    public async Task Remember_EchoesTheCanonicalFormNotTheCallerSpelling()
    {
        using var scope = TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();

        string response = await tools.MemoryRemember("/temp/probe", ["x"]);

        Assert.Contains("~probe", response, StringComparison.Ordinal);
        Assert.DoesNotContain("/temp/probe", response, StringComparison.Ordinal);
    }

    /// <summary>Both spellings address one memory — not two.</summary>
    [Fact]
    public async Task TheTwoSpellingsAddressTheSameMemory()
    {
        using var scope = TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["written via the alias"]);

        Assert.Contains("written via the alias", await tools.MemoryRecall("~probe"), StringComparison.Ordinal);
    }

    /// <summary>A near-miss must stay persistent, or the prefix check is too greedy.</summary>
    [Fact]
    public async Task ANearMissPathIsStillPersistent()
    {
        using var scope = TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();

        string response = await tools.MemoryRemember("/tempest/probe", ["x"]);

        Assert.DoesNotContain("~", response, StringComparison.Ordinal);
    }
}
```

> If `TestHelpers.StoreScope()` has a different name or arity than assumed, read
> `tests/Scrinia.Tests/TestHelpers.cs:96-153` and use the real one. Do **not** fall back to a
> hand-rolled fixture.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EphemeralAliasRoundTripTests"`

Expected: FAIL. Recall/Forget/Append/Compact/Link at `/temp/x` resolve persistent and answer
NOT_FOUND — H2 reproduced. The two Remember-only tests already pass; that is expected.

- [ ] **Step 3: Canonicalize in the six facade methods**

In `src/Scrinia.Mcp/MemoryTools.cs` (`using Scrinia.Core;` present at `:5`).

`MemoryRemember` (`:109`) and `MemoryAppend` (`:266`) — before the line that assigns the
delegation result to a local:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
```

`MemoryRecall` (`:197`) — before `:215`:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
        return (await Recall(path, chunk, actionLabel: "recalled", cancellationToken: cancellationToken)).ToYaml();
```

`MemoryForget` (`:335`) — before `:345`:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
        return (await Forget(path, cancellationToken)).ToYaml();
```

`MemoryCompact` (`:354`) — before `:365`:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
        return (await Compact(path, keepRecent, cancellationToken)).ToYaml();
```

`MemoryLink` (`:374`) — **both** addresses, before `:391`:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
        destination = MemoryNaming.CanonicalizeEphemeralAlias(destination);
        return (await Link(path, destination, reason, cancellationToken)).ToYaml();
```

- [ ] **Step 4: Delete the two literals**

`src/Scrinia.Mcp/MemoryTools.Core.cs:139-141` — delete all three lines (the comment and the
rewrite). `:137` `var store = CurrentStore;` remains above and the `/agent/` branch below.

`:1039-1040` — delete both lines. `:1042` `var (ephemeral, scope, subject) = Resolve(store, name);`
remains below.

- [ ] **Step 5: Update the one pre-existing test this changes**

`tests/Scrinia.Tests/ScriniaMcpToolsTests.cs:1683`,
`Append_TempPath_IsEphemeral_WhetherOrNotTheMemoryExists`, calls the **internal** `Append`
(`MemoryTools.Core.cs:1002+`), not the facade `MemoryAppend`. Step 4 removes the internal
canonicalization, so `/temp/appendtemp` now resolves persistent and the assertion at `:1687-1689`
(`GetEphemeral("appendtemp")` non-null) fails.

**This is correct and expected, not a design violation.** The test asserts *surface* behavior
through an entry point that is no longer a surface. Change its two calls from the internal
`Append`/`Recall` to the facade `MemoryAppend`/`MemoryRecall`, keeping the assertions as they are.

Preserve the comment block at `:1673-1680`, which records the original half-fix, and add one line
noting the rule now lives in `MemoryNaming` and this test drives the facade because that is where
the alias is resolved.

This is the **only** pre-existing test that changes. Any other one going red means the store layer
moved — stop and report.

- [ ] **Step 6: Correct the two comments that describe the old shape**

`src/Scrinia.Core/PathRouter.cs:93-94` says the rule is a rewrite in `MemoryTools.Core` "at the
remember and forget entry points" — wrong twice (it was remember and *append*, and it is no longer
there). Replace, keeping the alias unquoted so the Step 8 grep stays clean:

```csharp
    //   * the ephemeral-alias rule is MemoryNaming.CanonicalizeEphemeralAlias, applied at the
    //     two surfaces: the MCP facade (MemoryTools.cs) and the CLI commands
    //     (ScriniaCommands.Memory.cs). Nothing below those surfaces sees the alias form.
```

`src/Scrinia.Core/FileMemoryStore.cs:20` documents the alias as the store's own convention.
Correct it to describe `~subject` only, noting the alias is resolved at the surfaces before the
store is reached. Keep it unquoted.

- [ ] **Step 7: Update the agent guide**

`src/Scrinia.Mcp/prompts/guide.md:135`:

```markdown
| `/temp/...` | Ephemeral — dies on process exit. An alias for `~name`; both spell the same memory. |
```

- [ ] **Step 8: Verify no literal survives, and the suite is green**

```bash
grep -rn '"/temp/"' --include=*.cs src/ | grep -v MemoryNaming.cs
```
Expected: no output. (Before this task the hits are `MemoryTools.Core.cs:140,141,1039,1040` and
`PathRouter.cs:94` — all five are removed by Steps 4 and 6.)

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`

Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Core/PathRouter.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Mcp/prompts/guide.md tests/Scrinia.Tests/EphemeralAliasRoundTripTests.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -m "fix(mcp): a memory remembered at the temp alias is reachable at the temp alias"
```

---

### Task 4: The write routes refuse the surface alias

> **Scope correction from the falsification pass.** `NameRefusal` is called by four handlers
> only: `:162` (POST `/memories`), `:578` (Forget), `:620` (Append), `:665`/`:667` (Copy).
> `RecallMemory` (`:250`), `GetEntry` (`:316`), GetChunk and `PatchEntry` do not call it — they
> catch `ArgumentException` and 404 on a null result. Since the alias is a *well-formed* name
> (scope `local:temp`, subject `x`) it throws nothing, so those routes answer **404, not 400**.
> That is acceptable: the harm this closes is *persisting* alias bytes, which only the write
> routes can do. Success criterion 3 is narrowed accordingly — do not add per-route work.

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` — `NameRefusal` at `:988`, doc block `:967-987`
- Test: `tests/Scrinia.Server.Tests/EphemeralAliasRefusalTests.cs` (create)

**Interfaces:**
- Consumes: `MemoryNaming.IsEphemeralAlias` from Task 1.
- Produces: no new API.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Server.Tests/EphemeralAliasRefusalTests.cs`. Use the shared
`ScriniaServerFactory` (`tests/Scrinia.Server.Tests/ScriniaServerFactory.cs`) via
`IClassFixture`, exactly as `MalformedNameRefusalTests.cs:31-44` does. The store is
`factory.PrimaryStore` (`"test-store"`), **not** `"default"` — the test API key is scoped to
`PrimaryStore` and `store-2` only, so a hardcoded store name yields a 403/404 and turns the
negative assertion into a false green.

```csharp
using System.Net;
using System.Net.Http.Json;

namespace Scrinia.Server.Tests;

/// <summary>
/// D-2: the ephemeral alias is a SURFACE form. The REST write routes sit below the surface, so
/// they refuse it rather than guessing between an ephemeral and the topic "temp". Before this,
/// POST /memories with an alias name wrote permanent bytes under memories/temp/, making it a
/// third, different memory from the two the MCP surface could produce.
/// </summary>
public class EphemeralAliasRefusalTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;

    public EphemeralAliasRefusalTests(ScriniaServerFactory factory) => _factory = factory;

    [Fact]
    public async Task PostMemories_RefusesTheAlias()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new { name = "/temp/x", content = new[] { "hello" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ephemeral", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The canonical form is unchanged — only the alias is refused.</summary>
    [Fact]
    public async Task PostMemories_StillAcceptsTheCanonicalEphemeralForm()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new { name = "~x", content = new[] { "hello" } });

        Assert.True(response.IsSuccessStatusCode,
            $"expected success, got {(int)response.StatusCode} — a non-success here would make "
            + "the BadRequest assertion above vacuous");
    }

    /// <summary>A near-miss name must still be storable.</summary>
    [Fact]
    public async Task PostMemories_AcceptsANearMissName()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new { name = "/tempest/x", content = new[] { "hello" } });

        Assert.True(response.IsSuccessStatusCode);
    }
}
```

Note the deliberate absence of a GET test: that route does not call `NameRefusal`, and the
escaped-slash route value does not carry through the shared in-memory server reliably
(`MalformedNameRefusalTests.cs:155-165` documents both the problem and its Kestrel workaround).
Adding one would need a real socket for no benefit the write-route tests do not already give.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~EphemeralAliasRefusalTests"`

Expected: FAIL — the first POST returns success and writes a persistent memory under `temp`.

- [ ] **Step 3: Add the refusal clause**

In `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs`, in `NameRefusal` (signature at `:988`),
**ahead of** the ephemeral skip at `:990`. `using Scrinia.Core;` is present at `:4`.

```csharp
        // The ephemeral alias is a SURFACE form, resolved at the MCP facade and the CLI. The
        // wire sits below the surface, so it refuses rather than guessing between an ephemeral
        // and the topic "temp" — the guess is what silently made memories/temp/ a real,
        // growing scope. Only the four write handlers that call NameRefusal are affected; the
        // read routes answer 404 for a name they cannot find, which is already correct.
        if (MemoryNaming.IsEphemeralAlias(name))
            return Results.BadRequest(new ErrorResponse(
                $"'{name}' names a client-side ephemeral memory and cannot be stored on the "
                + "server. Use '~name', or a persistent path."));
```

Then extend the XML doc block spanning `:967-987` — note the range; `:978` lands mid-sentence
inside the second `<para>`. Add a sentence recording that the alias is refused ahead of the
ephemeral skip, so the existing explanation of why `~name` is skipped is not read as covering
the alias too.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~EphemeralAliasRefusalTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs tests/Scrinia.Server.Tests/EphemeralAliasRefusalTests.cs
git commit -m "fix(server): the write routes refuse a surface alias instead of persisting it"
```

---

### Task 5: Record the breaking change and verify the whole suite

**Files:**
- Modify: `CHANGELOG.md`
- Test: the full solution suite

- [ ] **Step 1: Add the CHANGELOG entry**

At the top of the unreleased section, matching the surrounding style:

```markdown
### Fixed

- A memory remembered at `/temp/x` is now reachable at `/temp/x`. The alias rule was two string
  literals in `MemoryTools.Core.cs` (Remember and Append) and existed nowhere else, so `recall`,
  `forget`, `compact` and `link` answered NOT_FOUND for a path `remember` had just accepted —
  including the literal example in `forget`'s own tool description. The rule is now
  `MemoryNaming.CanonicalizeEphemeralAlias`, applied at the two surfaces (the MCP facade and the
  CLI commands).

### Changed

- The reserved-prefix hint for a case-mismatched `/TEMP/...` path no longer fires, because the
  alias is canonicalized before `ReservedPrefixGuard.HintFor` (`MemoryTools.cs:187`) sees it.
  The path now simply works, which is what the hint was pointing at.

### Breaking

- **Ephemeral addresses are refused by the CLI.** `scri store`, `show`, `forget`, `append`,
  `compact` and `link` exit 1 for a `~name` or `/temp/...` address. Previously
  `scri store /temp/x` reported success and exit 0 while discarding the content: the ephemeral
  map is in-memory and the CLI process exits immediately. Both spellings are marked
  CLI-unavailable in the reserved-path tables.
- **The REST write routes return 400 for a `/temp/...` name** — `POST /memories`, append, forget
  and copy. The alias is resolved at the agent-facing surfaces and never crosses the wire. Read
  routes are unchanged and answer 404 for a name they cannot find. Existing bytes under
  `.scrinia/memories/temp/` are left in place and remain readable as the topic scope
  `local:temp`; they are not moved or deleted, and are no longer reachable by the path
  `/temp/x` from an MCP client.
```

- [ ] **Step 2: Verify the success criteria**

```bash
grep -rn '"/temp/"' --include=*.cs src/ | grep -v MemoryNaming.cs
```
Expected: no output.

```bash
git diff --stat main -- tests/
```
Expected: four new files (`EphemeralAliasTests.cs`, `CliEphemeralRefusalTests.cs`,
`EphemeralAliasRoundTripTests.cs`, `EphemeralAliasRefusalTests.cs`) plus **exactly one** modified
pre-existing file, `ScriniaMcpToolsTests.cs`. Any other modified test file means the store layer
changed — stop and report.

- [ ] **Step 3: Run the full suite**

Run: `dotnet build Scrinia.sln` then `dotnet test Scrinia.sln`

Expected: 0 build errors; all tests pass. Report the actual pass/fail counts — do not claim green
without the output in hand.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): record the ephemeral addressing fix and its two breaking changes"
```

---

## Corrections from falsification

The first draft of this plan was checked against HEAD by an adversarial pass. Eight claims were
wrong; all are fixed above. Recorded so the same errors are not reintroduced:

1. **Task order reversed.** The CLI calls the *internal* `Remember`/`Append`, so deleting the
   internal literals before guarding the CLI would have left `scri store /temp/x` writing a
   persistent memory with exit 0 — worse than HEAD, in a bisectable commit.
2. **One pre-existing test does change.** `ScriniaMcpToolsTests.cs:1683` drives the internal
   `Append`. The original plan's tripwire would have reported this true failure as a false
   "the store layer changed" diagnosis.
3. **`NameRefusal` is not on every route** — four write handlers only. `/temp/x` is well-formed,
   so read routes 404 rather than 400. Criterion 3 narrowed; no per-route work added.
4. **The server store is `factory.PrimaryStore` (`"test-store"`), not `"default"`.** Hardcoding
   it would have made the negative assertion pass vacuously on a 403.
5. **`APersistentAddressIsNotRefused` needed a real file** — `Store` with `file: null` either
   returns 1 or blocks on stdin.
6. **`IsEphemeralName` must be null-safe** (`!string.IsNullOrEmpty`), or the delegation is not
   behavior-preserving.
7. **`PathRouter.cs:94` is a fifth current grep hit**, and the original replacement text
   reintroduced the quoted literal it was deleting.
8. **Four new test files, not three**; doc block is `:967-987`, not `:978-987`; Task 1 is 24
   tests, not 23.

Adopted from the same pass: `TestHelpers.StoreScope` over a hand-rolled fixture, `try/finally`
console redirection over constructor/`Dispose`, `IClassFixture<ScriniaServerFactory>` over copying
a fixture, no redundant `using Xunit;`, the `~subject` row at `cli-reference.md:720`, an `Append`
CLI test, and the `ReservedPrefixGuard` behavior change in the CHANGELOG.
