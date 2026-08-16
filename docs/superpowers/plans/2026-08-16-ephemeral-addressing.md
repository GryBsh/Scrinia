# Ephemeral Addressing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `/temp/x` and `~x` the same address on every agent-facing surface, and stop the CLI reporting success over an ephemeral write it discards.

**Architecture:** `/temp/` is a reserved-path alias on the *surface*, exactly like `/skill/`. It is canonicalized to `~` at the two surfaces (the MCP facade in `MemoryTools.cs`, and the CLI commands), and never travels below them. The store, the wire, and every layer beneath speak one spelling. `IsEphemeral` and `StripEphemeralPrefix` stay `~`-only — teaching them the alias would push a surface concern into the store and contradict the wire's 400.

**Tech Stack:** C# / .NET, xunit 2.9.3 (+ Xunit.SkippableFact), Spectre.Console for CLI rendering, ASP.NET Core minimal APIs for the server.

**Spec:** `docs/superpowers/specs/2026-08-16-ephemeral-addressing-design.md` (committed at `5da9417`)

**Branch:** `fix/ephemeral-addressing` (already created, branched from `main`)

## Global Constraints

- The literal string `/temp/` must appear in `src/` **only** inside `MemoryNaming.cs` when this plan is complete. That is success criterion 4 and the whole point of the branch.
- Prefix matching is `StringComparison.OrdinalIgnoreCase`, preserving the behavior of the two literals being deleted.
- `IsEphemeral` and `StripEphemeralPrefix` remain `~`-only. Do not teach either the `/temp/` alias.
- Responses and error messages echo the **canonical** `~x`, never the caller's `/temp/x` spelling.
- Existing ephemeral test suites must pass **unmodified**. If one needs editing, stop — that means the store layer changed and the design was violated.
- Build with `dotnet build Scrinia.sln`. Do not add `-p:NuGetAudit=false`; that workaround was retired on 2026-07-30 and restore is clean without it.
- CLI commands take the memory address as a **positional** `[Argument]`, e.g. `scri store /temp/x`, not `--name`.

---

### Task 1: The alias rule, with one home

**Files:**
- Modify: `src/Scrinia.Core/MemoryNaming.cs` (add after `StripEphemeralPrefix`, which ends at `:37`)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:273-274` (delegate the existing predicate)
- Test: `tests/Scrinia.Tests/EphemeralAliasTests.cs` (create)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `MemoryNaming.IsEphemeralAlias(string) → bool`, `MemoryNaming.CanonicalizeEphemeralAlias(string) → string`, `MemoryNaming.IsEphemeralName(string) → bool`. Tasks 2, 3 and 4 all call these.

**Why `IsEphemeralName` is here although the spec did not name it:** the CLI guard in Task 3 needs to ask "is this ephemeral?" without holding an `IMemoryStore` instance, and `IsEphemeral` is an instance member. Rather than let Task 3 hand-roll `name[0] == '~'` — a fourth copy of the predicate — add the static authority here and have `FileMemoryStore.IsEphemeral` delegate to it. This is exactly how `StripEphemeralPrefix` already works: `FileMemoryStore.cs:299-301` is `[Obsolete]` and delegates to `MemoryNaming`. Behavior is unchanged; the rule gains one home instead of two.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/EphemeralAliasTests.cs`:

```csharp
using Scrinia.Core;
using Xunit;

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
    [InlineData("/TEMP/x", "~x")]          // case-insensitive, matching the deleted literals
    [InlineData("/Temp/Probe", "~Probe")]  // prefix folds case; the subject does NOT
    public void CanonicalizeEphemeralAlias_RewritesTempPrefix(string input, string expected)
        => Assert.Equal(expected, MemoryNaming.CanonicalizeEphemeralAlias(input));

    [Theory]
    [InlineData("~x")]                 // already canonical
    [InlineData("/temp")]              // no trailing slash — an ordinary persistent path
    [InlineData("/tempest/x")]         // longer word starting with "temp"
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
    /// D-4: the store predicate stays "~"-only. If this ever goes red, a surface alias has
    /// leaked into the store layer and the design has been violated.
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

Expected: FAIL to compile — `CS0117: 'MemoryNaming' does not contain a definition for 'IsEphemeralAlias'` (and the same for `CanonicalizeEphemeralAlias`, `IsEphemeralName`).

- [ ] **Step 3: Add the three members to MemoryNaming**

In `src/Scrinia.Core/MemoryNaming.cs`, immediately after `StripEphemeralPrefix` (which ends at `:37`):

```csharp
    /// <summary>
    /// True for an ephemeral name in its canonical form (<c>~subject</c>).
    /// <para>THE static authority for the predicate. <see cref="FileMemoryStore.IsEphemeral"/>
    /// delegates here, the same way <see cref="StripEphemeralPrefix"/> already owns the strip.</para>
    /// </summary>
    public static bool IsEphemeralName(string name) =>
        name.Length > 0 && name[0] == '~';

    /// <summary>
    /// True for the <c>/temp/...</c> SURFACE alias.
    /// <para>The alias is a surface concern: it is canonicalized at the MCP facade
    /// (<c>MemoryTools.cs</c>) and at the CLI commands, and never reaches the store or the
    /// wire. Note this is false for <c>~x</c> — an alias is not yet an ephemeral name.</para>
    /// </summary>
    public static bool IsEphemeralAlias(string name) =>
        name.StartsWith("/temp/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// THE ONE PLACE <c>/temp/x</c> becomes <c>~x</c>; returns anything else unchanged.
    ///
    /// <para>This rule used to be two verbatim string literals in <c>MemoryTools.Core.cs</c>
    /// (Remember and Append) and existed nowhere else, so <c>recall</c>, <c>forget</c>,
    /// <c>compact</c> and <c>link</c> answered NOT_FOUND for a path <c>remember</c> had just
    /// accepted — including the exact example in <c>forget</c>'s own tool description. It lives
    /// here because the aliasing rule only holds if it is intrinsic rather than layered over
    /// each site, which is the same reasoning that put scope construction in this file.</para>
    ///
    /// <para>Case folding applies to the PREFIX only: <c>/TEMP/Probe</c> → <c>~Probe</c>. The
    /// subject keeps its casing, matching the literals this replaces.</para>
    /// </summary>
    public static string CanonicalizeEphemeralAlias(string name) =>
        IsEphemeralAlias(name) ? "~" + name["/temp/".Length..] : name;
```

- [ ] **Step 4: Delegate the store predicate**

In `src/Scrinia.Core/FileMemoryStore.cs`, replace the body at `:273-274`:

```csharp
    public bool IsEphemeral(string name) =>
        MemoryNaming.IsEphemeralName(name);
```

Behavior is identical (`name.Length > 0 && name[0] == '~'`); the rule now has one home. `HttpMemoryStore.IsEphemeral:290-291` already delegates to `_localHelper`, so it inherits this with no edit.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EphemeralAliasTests"`

Expected: PASS, 24 tests.

- [ ] **Step 6: Verify nothing else regressed**

Run: `dotnet build Scrinia.sln`

Expected: 0 errors. `dotnet build`'s own summary is the gate — do not substitute a wrapper's error filter, which has previously reported a failing build as clean.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/MemoryNaming.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/EphemeralAliasTests.cs
git commit -m "feat(core): one home for the /temp/ alias, and one for the ~ predicate"
```

---

### Task 2: Canonicalize at the MCP facade, and delete the two literals

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` — 6 methods at `:109`, `:197`, `:266`, `:335`, `:354`, `:374`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — delete `:140-141` and `:1039-1040`
- Modify: `src/Scrinia.Core/PathRouter.cs:93-94` (comment is wrong)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:20` (comment is wrong)
- Modify: `src/Scrinia.Mcp/prompts/guide.md:135`
- Test: `tests/Scrinia.Tests/EphemeralAliasRoundTripTests.cs` (create)

**Interfaces:**
- Consumes: `MemoryNaming.CanonicalizeEphemeralAlias(string) → string` from Task 1.
- Produces: no new API. After this task every MCP tool accepts `/temp/x` and `~x` as the same address.

**Placement rule — apply it uniformly:** insert the canonicalization on the line immediately **before** the delegating `return (await ...)` call. Not at the top of the method: `MemoryRecall` runs its `/skill/` route check first (`:204-206`), and `MemoryRemember` runs one at `:155`. `/temp/` and `/skill/` are disjoint prefixes so ordering is functionally free, but "immediately before delegation" is one rule that is correct in all six places and cannot be misapplied.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/EphemeralAliasRoundTripTests.cs`:

```csharp
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// The regression tests the suite lacked. Every pre-existing ephemeral test writes at "/temp/x"
/// and reads back at "~x", which is exactly why H2 survived 2,015 tests: no test ever used the
/// SAME spelling twice. These do.
/// </summary>
public class EphemeralAliasRoundTripTests : IDisposable
{
    private readonly string _dir;

    public EphemeralAliasRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scrinia-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        MemoryStoreContext.Current = new FileMemoryStore(_dir);
    }

    public void Dispose()
    {
        MemoryStoreContext.Current = null;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Recall_FindsAMemoryRememberedAtTheSameTempPath()
    {
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["the secret note"]);

        string recalled = await tools.MemoryRecall("/temp/probe");

        Assert.Contains("the secret note", recalled, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT_FOUND", recalled, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forget_DeletesAMemoryRememberedAtTheSameTempPath()
    {
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["gone soon"]);

        string forgotten = await tools.MemoryForget("/temp/probe");

        Assert.DoesNotContain("NOT_FOUND", forgotten, StringComparison.Ordinal);
        Assert.Contains("NOT_FOUND", await tools.MemoryRecall("/temp/probe"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_ExtendsAMemoryRememberedAtTheSameTempPath()
    {
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
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["one", "two"]);

        string compacted = await tools.MemoryCompact("/temp/probe");

        Assert.DoesNotContain("NOT_FOUND", compacted, StringComparison.Ordinal);
    }

    /// <summary>link carries TWO addresses; both ends must canonicalize.</summary>
    [Fact]
    public async Task Link_ResolvesBothEndsAtTheSameTempPaths()
    {
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
        var tools = new ScriniaMcpTools();

        string response = await tools.MemoryRemember("/temp/probe", ["x"]);

        Assert.Contains("~probe", response, StringComparison.Ordinal);
        Assert.DoesNotContain("/temp/probe", response, StringComparison.Ordinal);
    }

    /// <summary>Both spellings address one memory — not two.</summary>
    [Fact]
    public async Task TheTwoSpellingsAddressTheSameMemory()
    {
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["written via the alias"]);

        Assert.Contains("written via the alias", await tools.MemoryRecall("~probe"), StringComparison.Ordinal);
    }

    /// <summary>A near-miss must stay persistent, or the prefix check is too greedy.</summary>
    [Fact]
    public async Task ANearMissPathIsStillPersistent()
    {
        var tools = new ScriniaMcpTools();

        string response = await tools.MemoryRemember("/tempest/probe", ["x"]);

        Assert.DoesNotContain("~", response, StringComparison.Ordinal);
        Assert.DoesNotContain("ephemeral", response, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EphemeralAliasRoundTripTests"`

Expected: FAIL. `Recall`, `Forget`, `Append`, `Compact` and `Link` tests fail with NOT_FOUND present in the response — this is H2 reproduced. `Remember_EchoesTheCanonicalForm` and `TheTwoSpellingsAddressTheSameMemory` may already pass (Remember has the literal today); that is expected and fine.

- [ ] **Step 3: Canonicalize in the six facade methods**

In `src/Scrinia.Mcp/MemoryTools.cs`. Confirm `using Scrinia.Core;` is present at the top; add it if not.

`MemoryRemember` (`:109`) — before its delegating call:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
```

`MemoryRecall` (`:197`) — immediately before `return (await Recall(path, chunk, ...))`:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
        return (await Recall(path, chunk, actionLabel: "recalled", cancellationToken: cancellationToken)).ToYaml();
```

`MemoryAppend` (`:266`) — before its delegating call:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
```

`MemoryForget` (`:335`) — immediately before `return (await Forget(path, cancellationToken))`:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
        return (await Forget(path, cancellationToken)).ToYaml();
```

`MemoryCompact` (`:354`) — immediately before `return (await Compact(path, keepRecent, cancellationToken))`:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
        return (await Compact(path, keepRecent, cancellationToken)).ToYaml();
```

`MemoryLink` (`:374`) — **both** addresses, immediately before `return (await Link(path, destination, reason, cancellationToken))`:
```csharp
        path = MemoryNaming.CanonicalizeEphemeralAlias(path);
        destination = MemoryNaming.CanonicalizeEphemeralAlias(destination);
        return (await Link(path, destination, reason, cancellationToken)).ToYaml();
```

- [ ] **Step 4: Delete the two literals**

In `src/Scrinia.Mcp/MemoryTools.Core.cs`, delete these three lines at `:139-141`:

```csharp
        // ── Ephemeral naming (/temp/... is spelled ~... below this line) ────
        if (name.StartsWith("/temp/", StringComparison.OrdinalIgnoreCase))
            name = "~" + name["/temp/".Length..];
```

and the matching two lines at `:1039-1040`:

```csharp
        if (name.StartsWith("/temp/", StringComparison.OrdinalIgnoreCase))
            name = "~" + name["/temp/".Length..];
```

- [ ] **Step 5: Correct the two comments that describe the old shape**

`src/Scrinia.Core/PathRouter.cs:93-94` currently says the rule is a string-prefix rewrite in `MemoryTools.Core` "at the remember and forget entry points" — wrong twice over (it was remember and *append*, and it is no longer there at all). Replace with:

```csharp
    //   * the "/temp/ means ephemeral" rule is MemoryNaming.CanonicalizeEphemeralAlias, applied
    //     at the two surfaces (the MCP facade in MemoryTools.cs, and the CLI commands). It is a
    //     SURFACE alias: nothing below those surfaces ever sees "/temp/".
```

`src/Scrinia.Core/FileMemoryStore.cs:20` documents `/temp/` as the store's own naming convention. The store no longer sees the alias — correct that line to describe `~subject` only, and note that `/temp/` is a surface alias resolved before the store is reached.

- [ ] **Step 6: Update the agent guide**

`src/Scrinia.Mcp/prompts/guide.md:135` — keep the row, make the relationship explicit:

```markdown
| `/temp/...` | Ephemeral — dies on process exit. An alias for `~name`; both spell the same memory. |
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~EphemeralAlias"`

Expected: PASS, all tests from Task 1 and Task 2.

- [ ] **Step 8: Verify no literal survives, and the suite is green**

```bash
grep -rn '"/temp/"' --include=*.cs src/ | grep -v MemoryNaming.cs
```
Expected: no output.

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`

Expected: PASS. Existing ephemeral suites must pass **unmodified** — if any needs editing, stop and report, because that means the store layer changed.

- [ ] **Step 9: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Core/PathRouter.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Mcp/prompts/guide.md tests/Scrinia.Tests/EphemeralAliasRoundTripTests.cs
git commit -m "fix(mcp): a memory remembered at /temp/x is reachable at /temp/x"
```

---

### Task 3: The CLI refuses ephemerals instead of discarding them

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.cs` (add the guard near `WriteJsonError` at `:29`)
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs` — 6 commands at `:337`, `:413`, `:460`, `:496`, `:541`, `:563`
- Modify: `docs/cli-reference.md:735`
- Modify: `docs/getting-started.md:189`
- Test: `tests/Scrinia.Tests/CliEphemeralRefusalTests.cs` (create)

**Interfaces:**
- Consumes: `MemoryNaming.CanonicalizeEphemeralAlias(string)`, `MemoryNaming.IsEphemeralName(string)` from Task 1.
- Produces: `ScriniaCommands.RefuseEphemeral(string name, bool json, out string canonical) → bool` — private, used only within `ScriniaCommands`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/CliEphemeralRefusalTests.cs`:

```csharp
using Scrinia.Commands;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Review finding C1: `scri store /temp/x` reported success and exit 0 while discarding the
/// content entirely — the ephemeral map is a plain in-memory dictionary and the CLI process
/// exits immediately. Every ephemeral CLI operation is meaningless for the same reason, so all
/// six commands refuse rather than only the writes.
/// </summary>
public class CliEphemeralRefusalTests : IDisposable
{
    private readonly string _dir;
    private readonly StringWriter _out = new();
    private readonly TextWriter _prev;

    public CliEphemeralRefusalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scrinia-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _prev = Console.Out;
        Console.SetOut(_out);
    }

    public void Dispose()
    {
        Console.SetOut(_prev);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string> EphemeralAddresses => new() { "~scratch", "/temp/scratch" };

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Store_RefusesAnEphemeralAddress(string address)
    {
        var cmd = new ScriniaCommands();
        int exit = await cmd.Store(address, file: null, workspaceRoot: _dir, json: true);

        Assert.Equal(1, exit);
        Assert.Contains("ephemeral", _out.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Show_RefusesAnEphemeralAddress(string address)
    {
        var cmd = new ScriniaCommands();
        int exit = await cmd.Show(address, workspaceRoot: _dir, json: true);

        Assert.Equal(1, exit);
        Assert.Contains("ephemeral", _out.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Forget_RefusesAnEphemeralAddress(string address)
    {
        var cmd = new ScriniaCommands();
        int exit = await cmd.Forget(address, workspaceRoot: _dir, json: true);

        Assert.Equal(1, exit);
        Assert.Contains("ephemeral", _out.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Compact_RefusesAnEphemeralAddress(string address)
    {
        var cmd = new ScriniaCommands();
        int exit = await cmd.Compact(address, workspaceRoot: _dir, json: true);

        Assert.Equal(1, exit);
        Assert.Contains("ephemeral", _out.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Link_RefusesAnEphemeralAddressOnEitherEnd(string address)
    {
        var cmd = new ScriniaCommands();

        Assert.Equal(1, await cmd.Link(address, "/findings/x", workspaceRoot: _dir, json: true));
        Assert.Equal(1, await cmd.Link("/findings/x", address, workspaceRoot: _dir, json: true));
    }

    /// <summary>The refusal names the canonical form, not the caller's spelling (D-3).</summary>
    [Fact]
    public async Task TheRefusalNamesTheCanonicalForm()
    {
        var cmd = new ScriniaCommands();
        await cmd.Store("/temp/scratch", file: null, workspaceRoot: _dir, json: true);

        string output = _out.ToString();
        Assert.Contains("~scratch", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/temp/scratch", output, StringComparison.Ordinal);
    }

    /// <summary>A persistent address is unaffected — the guard must not over-refuse.</summary>
    [Fact]
    public async Task APersistentAddressIsNotRefused()
    {
        var cmd = new ScriniaCommands();
        int exit = await cmd.Store("/findings/real", file: null, workspaceRoot: _dir, json: true);

        Assert.Equal(0, exit);
    }
}
```

> **Note on `Store`:** it reads content from stdin when `file` is null. The refusal guard runs
> *before* that read, so these tests do not need redirected stdin. If a test hangs, the guard was
> placed after the content read — move it to the top of the method body, right after
> `WorkspaceSetup.Configure(workspaceRoot)`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CliEphemeralRefusalTests"`

Expected: FAIL. The refusal tests return exit 0 and print a success message — this is C1 reproduced.

- [ ] **Step 3: Add the guard**

In `src/Scrinia/Commands/ScriniaCommands.cs`, after `WriteJsonError` (ends `:32`):

```csharp
    /// <summary>
    /// Canonicalizes the "/temp/" surface alias, then refuses any ephemeral address.
    ///
    /// <para>Every ephemeral operation from the CLI is meaningless, not just the writes: a fresh
    /// CLI process starts with an empty in-memory ephemeral map and exits before anything can
    /// read it back. `scri store ~x` therefore reported success and exit 0 over content it
    /// discarded (review finding C1), and `scri show ~x` could only ever answer NOT_FOUND.
    /// Refusing loudly is the honest outcome; the alternative — inventing a session-scoped
    /// store — is a lifecycle nothing has asked for.</para>
    ///
    /// <para>The message names the CANONICAL form, so a caller who wrote "/temp/x" learns the
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

Confirm `using Scrinia.Core;` and `using Spectre.Console;` are present in this file; add if missing.

- [ ] **Step 4: Wire the guard into all six commands**

In `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, immediately after each command's
`WorkspaceSetup.Configure(workspaceRoot);` line — before any content read, plugin load, or
store access.

`Store` (`:337`), `Show` (`:413`), `Forget` (`:460`), `Append` (`:496`), `Compact` (`:541`):
```csharp
        if (RefuseEphemeral(name, json, out string canonicalName)) return 1;
        name = canonicalName;
```

`Link` (`:563`) takes two addresses, `from` and `to`:
```csharp
        if (RefuseEphemeral(from, json, out string canonicalFrom)) return 1;
        if (RefuseEphemeral(to, json, out string canonicalTo)) return 1;
        from = canonicalFrom;
        to = canonicalTo;
```

The reassignment is deliberate even though every ephemeral address is refused: it keeps the
canonicalization in one place should a future command choose to accept one.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CliEphemeralRefusalTests"`

Expected: PASS.

- [ ] **Step 6: Update the CLI docs — this is a breaking change**

`docs/cli-reference.md:735` currently lists `/temp/...` in the reserved-path table as available. Remove that row and add, near the `scri store` section:

```markdown
> **Ephemeral memories are not available from the CLI.** `~name` and its `/temp/...` alias live
> only in the process that creates them, and the CLI exits immediately — so a CLI ephemeral write
> would be discarded. All memory commands refuse an ephemeral address with exit 1. Use an MCP
> client for ephemerals, or a persistent path.
```

`docs/getting-started.md:189` — make the same correction, scoped to whatever surface the surrounding text describes. If that section documents the MCP tools rather than the CLI, the row stays correct and needs only the alias note from Task 2 Step 6; read the surrounding prose before editing.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia/Commands/ScriniaCommands.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs docs/cli-reference.md docs/getting-started.md tests/Scrinia.Tests/CliEphemeralRefusalTests.cs
git commit -m "fix(cli): an ephemeral write is refused, not reported as stored and discarded"
```

---

### Task 4: The wire refuses the surface alias

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:990` (`NameRefusal`)
- Test: `tests/Scrinia.Server.Tests/EphemeralAliasRefusalTests.cs` (create)

**Interfaces:**
- Consumes: `MemoryNaming.IsEphemeralAlias(string)` from Task 1.
- Produces: no new API.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Server.Tests/EphemeralAliasRefusalTests.cs`. Follow the harness already used by the sibling endpoint suites in this project — construct the same `WebApplicationFactory`/`TestServer` fixture `PatchEndpointTests.cs` uses, including its auth setup, rather than inventing a new one:

```csharp
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// D-2: "/temp/" is a SURFACE alias. The REST wire sits below the surface, so it refuses the
/// alias rather than guessing whether the caller meant an ephemeral or the topic "temp".
/// Before this, POST /memories {"name":"/temp/x"} wrote permanent bytes to memories/temp/,
/// making "/temp/x" a third, different memory from the two the MCP surface could produce.
/// </summary>
public class EphemeralAliasRefusalTests
{
    [Fact]
    public async Task PostMemories_RefusesTheTempAlias()
    {
        using var client = /* the same authenticated client the sibling endpoint suites build */;

        var response = await client.PostAsJsonAsync(
            "/api/v1/stores/default/memories",
            new { name = "/temp/x", content = new[] { "hello" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ephemeral", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Commit 8bf748a made name refusal uniform; pin that this rides on it.</summary>
    [Fact]
    public async Task GetMemory_RefusesTheTempAliasToo()
    {
        using var client = /* same fixture */;

        var response = await client.GetAsync("/api/v1/stores/default/memories/%2Ftemp%2Fx");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The canonical ~name form is unchanged — only the alias is refused.</summary>
    [Fact]
    public async Task PostMemories_StillAcceptsTheCanonicalEphemeralForm()
    {
        using var client = /* same fixture */;

        var response = await client.PostAsJsonAsync(
            "/api/v1/stores/default/memories",
            new { name = "~x", content = new[] { "hello" } });

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

> The `/* same fixture */` comments are the one place this plan defers to the codebase: the
> server test projects share an authenticated-client fixture whose exact construction differs by
> suite. Copy it from `PatchEndpointTests.cs` verbatim rather than writing a new one — do not
> invent a fixture, and do not skip these tests if the fixture is awkward.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~EphemeralAliasRefusalTests"`

Expected: FAIL — the POST returns 200/201 and writes a persistent memory under the `temp` topic.

- [ ] **Step 3: Add the refusal clause**

In `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs`, in `NameRefusal` (`:990`), **ahead of** the existing ephemeral skip:

```csharp
    private static IResult? NameRefusal(IMemoryStore store, string name)
    {
        // "/temp/" is a SURFACE alias, resolved at the MCP facade and the CLI. The wire sits
        // below the surface, so it refuses rather than guessing between an ephemeral and the
        // topic "temp" — which is what silently made memories/temp/ a real, growing scope.
        if (MemoryNaming.IsEphemeralAlias(name))
            return Results.BadRequest(new ErrorResponse(
                $"'{name}' names a client-side ephemeral memory and cannot be stored on the "
                + "server. Use '~name', or a persistent path."));

        if (store.IsEphemeral(name))
            return null;
        return TryResolveName(store, name, out _, out _, out IResult? refusal) ? null : refusal;
    }
```

Confirm `using Scrinia.Core;` is present in the file.

Update the XML doc above `NameRefusal` (`:978-987`) to mention the new first clause, so the
existing explanation of why `~name` is skipped is not read as covering the alias too.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~EphemeralAliasRefusalTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs tests/Scrinia.Server.Tests/EphemeralAliasRefusalTests.cs
git commit -m "fix(server): the wire refuses a surface alias instead of persisting it"
```

---

### Task 5: Record the breaking change and verify the whole suite

**Files:**
- Modify: `CHANGELOG.md`
- Test: the full solution suite

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: nothing.

- [ ] **Step 1: Add the CHANGELOG entry**

At the top of the unreleased section in `CHANGELOG.md`, matching the surrounding entry style:

```markdown
### Fixed

- A memory remembered at `/temp/x` is now reachable at `/temp/x`. The `/temp/` → `~` rule was
  two string literals in `MemoryTools.Core.cs` (Remember and Append) and existed nowhere else,
  so `recall`, `forget`, `compact` and `link` answered NOT_FOUND for a path `remember` had just
  accepted — including the literal example in `forget`'s own tool description. The rule is now
  `MemoryNaming.CanonicalizeEphemeralAlias`, applied at the two surfaces.

### Breaking

- **Ephemeral addresses are refused by the CLI.** `scri store`, `show`, `forget`, `append`,
  `compact` and `link` now exit 1 for a `~name` or `/temp/...` address. Previously
  `scri store /temp/x` reported success and exit 0 while discarding the content: the ephemeral
  map is in-memory and the CLI process exits immediately. `/temp/...` has been removed from the
  CLI reserved-path table.
- **`POST /memories` with a `/temp/...` name returns 400.** The alias is resolved at the
  agent-facing surfaces and never crosses the wire. Existing bytes under
  `.scrinia/memories/temp/` are left in place and remain readable as the topic scope
  `local:temp`; they are not moved or deleted, and are no longer reachable by the path
  `/temp/x` from an MCP client.
```

- [ ] **Step 2: Verify the success criteria**

```bash
grep -rn '"/temp/"' --include=*.cs src/ | grep -v MemoryNaming.cs
```
Expected: no output. (Criterion 4.)

```bash
git diff --stat main -- tests/
```
Expected: only the three new test files appear. No pre-existing test file is modified — that is
the evidence D-4 held and the store layer was untouched. If any existing test was edited, stop
and report.

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

## Self-review notes

**Spec coverage.** D-1 → Task 3. D-2 → Task 4. D-3 → Task 2 Step 1 (`Remember_EchoesTheCanonicalForm`) and Task 3 Step 1 (`TheRefusalNamesTheCanonicalForm`). D-4 → Task 1 Step 1 (`FileMemoryStore_IsEphemeral_DoesNotKnowTheAlias`) and the Task 5 Step 2 diff check. D-5 (no migration) → Task 5 CHANGELOG text; no code. All six doc rows → Task 2 Steps 5–6 and Task 3 Step 6. All seven spec test cases → Tasks 1–4.

**One addition beyond the spec:** `MemoryNaming.IsEphemeralName` plus the `FileMemoryStore.IsEphemeral` delegation (Task 1). The spec did not call for it; without it, Task 3's CLI guard would hand-roll `name[0] == '~'` and create a second home for the predicate — the exact defect this branch exists to remove. Behavior is unchanged and D-4 still holds. Flagged rather than folded in silently.

**One deferral to the codebase:** the server test fixture in Task 4 Step 1. The authenticated-client construction differs across the server suites, so the plan directs copying it from `PatchEndpointTests.cs` rather than specifying bytes that would likely be wrong.

**Type consistency.** `CanonicalizeEphemeralAlias`, `IsEphemeralAlias`, `IsEphemeralName` and `RefuseEphemeral` are spelled identically in every task that uses them.
