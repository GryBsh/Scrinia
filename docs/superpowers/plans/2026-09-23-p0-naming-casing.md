# P0 Naming/Casing Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Consolidate all naming/casing logic into `MemoryNaming` as the single home with zero disk/wire/JSON changes.

**Architecture:** Move the bodies of `FileMemoryStore.ResolveAddress` / `FormatQualifiedName` / `SanitizeNameCore` verbatim into `MemoryNaming`, add `Comparer` + `NameEquals` + `ResolveExistingName` (2 overloads) + `IsUriLike`, then turn `FileMemoryStore`, `PathParser`, `MemoryAddress`, and all read matchers into one-line delegates/callers. Pure static methods, no new abstractions, no state.

**Tech Stack:** net10.0 / C# / ConsoleAppFramework (CLI host); xUnit + FluentAssertions for tests; source-generated JSON only.

**Spec:** docs/superpowers/specs/2026-09-23-p0-naming-casing-design.md

## Global Constraints

- NMP/2 artifact format unchanged (`NMP_SPEC.md`).
- `.scrinia/` layout unchanged (`memories/`, `skills/`, `agent/`, sidecars).
- CLI stays single-file surface (`src/Scrinia/Commands/ScriniaCommands.cs`, `--workspace-root` pattern; no remote mode).
- MCP 12+11 disjoint: 12 tools (`guide` + remember/recall/search/list/append/forget/compact/link/restore/facts) stay disjoint from the 11 CLI command forms.
- Source-gen JSON only (`ScriniaMcpJsonContext`, `CliJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`); no reflection serialization.
- Every new test uses `TestHelpers.StoreScope` isolation (AsyncLocal workspace/store/ephemeral/`SessionBudget` redirect).
- Build stays green after every task (`dotnet build Scrinia.sln`).
- DRY: one comparer, one char rule, one URI-clause list. YAGNI: no new service/abstraction. TDD: failing test first for every behavior.

---

## File Structure

- `src/Scrinia.Core/MemoryNaming.cs` (modify, currently 398 lines) — SINGLE HOME after change. Owns: `Comparer` (static readonly `StringComparer.OrdinalIgnoreCase`), `NameEquals(a, b)`, `IsUriLike(value)` (6 clauses), `ResolveAddress(MemoryAddress)`, `FormatQualifiedName(scope, subject)`, `SanitizeName(name)` + `SanitizeNameCore(name)` (moved bodies), `ResolveExistingName(IReadOnlyList<ArtifactEntry>, MemoryAddress)` and `ResolveExistingName(IReadOnlyList<ArtifactEntry>, string subject)` overloads. Keeps existing `TrySanitizeSegment`/`SanitizeSegment`/`TryBuild`/`BuildScopedTopicScope`/`FormatScopeLabel`/`ParseScopeLabel` untouched.
- `src/Scrinia.Core/FileMemoryStore.cs:393-448` (modify) — `ParseQualifiedName` (keep, uses new home for leaf sanitize), `FormatQualifiedName` → one-line delegate, `ResolveAddress` → one-line delegate, `SanitizeName` → one-line delegate, `SanitizeNameCore` (`:512`) → one-line delegate to `MemoryNaming.SanitizeSegment`. Matchers at `:2135`, `:2206`, `:2335`, read barrier `:467-468`, append/store matchers `:2448`, `:2614`, `:2818` become `NameEquals`/`ResolveExistingName` callers (Task 6).
- `src/Scrinia.Core/MemoryStoreAddressExtensions.cs` (modify, 67 lines) — `ParseQualifiedName` extension at `:43-66` keeps shape but routes leaf sanitize through `MemoryNaming` (no behavior change; removes second fork).
- `src/Scrinia.Core/Search/EntityFact.cs` (read-only reference) — `ArtifactEntry.Name` carrier type; no change. Referenced so implementers know `ResolveExistingName` returns the stored `ArtifactEntry` (with its `Name` casing preserved) and never mutates it.
- `src/Scrinia.Core/PathParser.cs:51-91` (modify) — `Parse` copies caller `entityTypes` set once into `HashSet<string>(OrdinalIgnoreCase)` and delegates URI/colon/backslash rejection to `MemoryNaming.IsUriLike` where overlapping; tags/leaf keep original casing.
- `src/Scrinia.Core/Models/MemoryAddress.cs:112-120` (modify) — `IsLegalSegment` keeps positional guards; `ContainsPhysicalOrUriSyntax` becomes one-line delegate to `MemoryNaming.IsUriLike`. JSON converter, `MaxUtf8Bytes`, ephemeral canonicalization untouched.
- `src/Scrinia.Core/IMemoryStore.cs:132-134` (modify) — default `TryUpdateEntry` matcher becomes `MemoryNaming.NameEquals`.
- `src/Scrinia.Core/EntityTypingEventHandler.cs:52-53` (modify) — promotion pre-lookup becomes `MemoryNaming.NameEquals`.
- `tests/Scrinia.Tests/P0NamingCasingTests.cs` (create) — all new P0 pins in one file, `TestHelpers.StoreScope` per test.
- `tests/Scrinia.Tests/MemoryNamingTests.cs` (read-only reference, 104 lines) — existing pins must stay green; do not edit.

---

### Task 1: `Comparer` + `NameEquals` + `IsUriLike` single rules

**Files:**
- Modify: `src/Scrinia.Core/MemoryNaming.cs:1-10`
- Test: `tests/Scrinia.Tests/P0NamingCasingTests.cs`

**Interfaces:**
- Consumes: `MemoryNaming.TrySanitizeSegment(string, out string)` (existing, `:175`).
- Produces: `public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;`, `public static bool NameEquals(string? a, string? b)`, `public static bool IsUriLike(string value)` — exact names/signatures neighbors use.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class P0NamingCasingTests
{
    [Fact]
    public void NameEquals_MixedCaseAndSanitizedChars_MatchesOldInlineRule()
    {
        MemoryNaming.NameEquals("Notes", "notes").Should().BeTrue();
        MemoryNaming.NameEquals("a/b", "a_b").Should().BeTrue();
        MemoryNaming.NameEquals("notes", "other").Should().BeFalse();
    }

    [Fact]
    public void Comparer_IsOrdinalIgnoreCase()
    {
        MemoryNaming.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\\", true)]
    [InlineData(":", true)]
    [InlineData("a?b", true)]
    [InlineData("a#b", true)]
    [InlineData("a%b", true)]
    [InlineData("//server/x", true)]
    [InlineData("C:/x", true)]
    [InlineData("plain-name", false)]
    [InlineData("/topic/subject", false)]
    public void IsUriLike_SixClauseTable(string value, bool expected)
    {
        MemoryNaming.IsUriLike(value).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests" -v`
Expected: FAIL with "MemoryNaming does not contain a definition for 'NameEquals'" (and Comparer/IsUriLike).

- [ ] **Step 3: Write minimal implementation**

In `src/Scrinia.Core/MemoryNaming.cs`, insert after the `SanitizeSegment` method (`:192-195`):

```csharp
    /// <summary>
    /// THE ONE casing rule. Sanitizes both sides via <see cref="TrySanitizeSegment"/>
    /// then compares with <see cref="Comparer"/>. Replaces every inline
    /// <c>SanitizeName(a).Equals(SanitizeName(b), OrdinalIgnoreCase)</c> call site.
    /// Null-safe: two nulls are equal, one null is not.
    /// </summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static bool NameEquals(string? a, string? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (!TrySanitizeSegment(a, out string sa)) return false;
        if (!TrySanitizeSegment(b, out string sb)) return false;
        return Comparer.Equals(sa, sb);
    }

    /// <summary>
    /// THE ONE URI/physical-syntax predicate. Six clauses: backslash, colon
    /// (subsumes drive-letter <c>X:</c>), <c>?</c>, <c>#</c>, <c>%</c>, leading
    /// <c>//</c>. Consumed by <c>PathParser.Parse</c> and
    /// <c>MemoryAddress.ContainsPhysicalOrUriSyntax</c>. One clause list, no drift.
    /// </summary>
    public static bool IsUriLike(string value) =>
        value.Contains('\\') || value.Contains(':') || value.Contains('?') ||
        value.Contains('#') || value.Contains('%') ||
        value.StartsWith("//", StringComparison.Ordinal);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests" -v`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/MemoryNaming.cs tests/Scrinia.Tests/P0NamingCasingTests.cs
git commit -m "feat: add MemoryNaming Comparer NameEquals IsUriLike single rules"
```

---

### Task 2: Move `ResolveAddress` + `FormatQualifiedName` + `SanitizeName` bodies into `MemoryNaming`, add `ResolveExistingName` overloads

**Files:**
- Modify: `src/Scrinia.Core/MemoryNaming.cs:355-398`
- Test: `tests/Scrinia.Tests/P0NamingCasingTests.cs`

**Interfaces:**
- Consumes: `Comparer`, `NameEquals` (Task 1), `Models.MemoryAddress`, `ArtifactEntry`, `BuildScopedTopicScope`.
- Produces: `public static (string Scope, string Subject) ResolveAddress(Models.MemoryAddress address)`, `public static string FormatQualifiedName(string scope, string subject)`, `public static string SanitizeName(string name)`, `public static ArtifactEntry? ResolveExistingName(IReadOnlyList<ArtifactEntry> entries, Models.MemoryAddress address)`, `public static ArtifactEntry? ResolveExistingName(IReadOnlyList<ArtifactEntry> entries, string subject)` — exact signatures `FileMemoryStore` delegates to in Task 3.

- [ ] **Step 1: Write the failing test**

Append to `tests/Scrinia.Tests/P0NamingCasingTests.cs`:

```csharp
    [Fact]
    public void ResolveAddress_LocalAndTopicAndEphemeral()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryNaming.ResolveAddress(Scrinia.Core.Models.MemoryAddress.Parse("/notes"))
            .Should().Be(("local", "notes"));
        MemoryNaming.ResolveAddress(Scrinia.Core.Models.MemoryAddress.Parse("/api/notes"))
            .Should().Be(("local:api", "notes"));
        MemoryNaming.ResolveAddress(Scrinia.Core.Models.MemoryAddress.Parse("~scratch"))
            .Should().Be(("ephemeral", "scratch"));
        MemoryNaming.ResolveAddress(Scrinia.Core.Models.MemoryAddress.Parse("/a/b/c"))
            .Should().Be(("local:a/b", "c"));
    }

    [Fact]
    public void FormatQualifiedName_RoundTrips()
    {
        MemoryNaming.FormatQualifiedName("local", "notes").Should().Be("notes");
        MemoryNaming.FormatQualifiedName("local:api", "notes").Should().Be("/api/notes");
        MemoryNaming.FormatQualifiedName("ephemeral", "scratch").Should().Be("scratch");
    }

    [Fact]
    public void ResolveExistingName_FirstMatchWins_MissIsNull()
    {
        using var scope = new TestHelpers.StoreScope();
        var a = new ArtifactEntry(Name: "Notes", Address: Scrinia.Core.Models.MemoryAddress.Parse("/api/notes"));
        var b = new ArtifactEntry(Name: "NOTES", Address: Scrinia.Core.Models.MemoryAddress.Parse("/api/notes"));
        var entries = new List<ArtifactEntry> { a, b };
        MemoryNaming.ResolveExistingName(entries, "notes").Should().Be(a);
        MemoryNaming.ResolveExistingName(entries, "missing").Should().BeNull();
        MemoryNaming.ResolveExistingName(entries, Scrinia.Core.Models.MemoryAddress.Parse("/api/NOTES"))
            .Should().Be(a);
    }
```

Note: if `ArtifactEntry` requires additional constructor args in this repo snapshot, use the minimal record `with` form from an existing fixture (e.g. copy the construction line from `TryUpdateEntryTests`) — do not invent a new shape.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests.ResolveAddress|FullyQualifiedName~P0NamingCasingTests.FormatQualifiedName|FullyQualifiedName~P0NamingCasingTests.ResolveExistingName" -v`
Expected: FAIL with "does not contain a definition for 'ResolveAddress'".

- [ ] **Step 3: Write minimal implementation**

Append to `src/Scrinia.Core/MemoryNaming.cs` after `ParseScopeLabel` (`:392-397`), adding `using Scrinia.Core.Models;` at the top of the file:

```csharp
    /// <summary>
    /// THE ONE address-to-(scope, subject) resolver. Body moved verbatim from
    /// <c>FileMemoryStore.ResolveAddress</c> (src/Scrinia.Core/FileMemoryStore.cs:436-448).
    /// Single-address resolve throws <see cref="ArgumentException"/> on an
    /// unconstructible topic; the never-throws filter path stays on
    /// <see cref="TryBuildScopedTopicScope"/>.
    /// </summary>
    public static (string Scope, string Subject) ResolveAddress(MemoryAddress address)
    {
        if (!address.IsValid)
            throw new ArgumentException("The memory address must be valid.", nameof(address));
        if (address.Kind == MemoryAddressKind.Ephemeral)
            return ("ephemeral", address.Value);

        var segments = address.Value[1..].Split('/');
        if (segments.Length == 1)
            return ("local", SanitizeName(segments[0]));

        return (BuildScopedTopicScope(segments[..^1]), SanitizeName(segments[^1]));
    }

    /// <summary>
    /// THE ONE display formatter. Body moved verbatim from
    /// <c>FileMemoryStore.FormatQualifiedName</c> (:428-431):
    /// <c>local</c> and <c>ephemeral</c> render as the bare subject (facade
    /// prepends <c>~</c> for ephemeral); <c>local:{topic}</c> renders as
    /// <c>/{topic}/{subject}</c>.
    /// </summary>
    public static string FormatQualifiedName(string scope, string subject) =>
        scope.StartsWith("local:", StringComparison.Ordinal)
            ? $"/{scope["local:".Length..]}/{subject}"
            : subject;

    /// <summary>
    /// THE ONE leaf sanitizer (throw-only-on-empty, via <see cref="SanitizeSegment"/>).
    /// Body moved from <c>FileMemoryStore.SanitizeNameCore</c> (:512). No
    /// trailing-dot/NUL rejection here: artifact paths append <c>.nmp2</c> after
    /// sanitizing, and this runs against names read back from disk where throwing
    /// would poison a whole scope read.
    /// </summary>
    public static string SanitizeName(string name) => SanitizeSegment(name);

    /// <summary>
    /// THE ONE read-back matcher. First entry in iteration order where
    /// <see cref="NameEquals(entry.Name, subject)"/> holds; null when none.
    /// No sorting, no last-match. Stored casing travels forward untouched.
    /// </summary>
    public static ArtifactEntry? ResolveExistingName(IReadOnlyList<ArtifactEntry> entries, string subject)
    {
        foreach (var entry in entries)
        {
            if (NameEquals(entry.Name, subject))
                return entry;
        }
        return null;
    }

    /// <summary>
    /// Address overload: resolves via <see cref="ResolveAddress"/> then matches
    /// by subject. Exact-address shortcut first (covers entries whose stored
    /// <c>Address</c> already equals), then case-insensitive name fallback —
    /// the same two-clause rule the read barrier used.
    /// </summary>
    public static ArtifactEntry? ResolveExistingName(IReadOnlyList<ArtifactEntry> entries, MemoryAddress address)
    {
        foreach (var entry in entries)
        {
            if (entry.Address == address)
                return entry;
        }
        var (_, subject) = ResolveAddress(address);
        return ResolveExistingName(entries, subject);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests" -v`
Expected: PASS (all 6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/MemoryNaming.cs tests/Scrinia.Tests/P0NamingCasingTests.cs
git commit -m "feat: move ResolveAddress FormatQualifiedName SanitizeName into MemoryNaming"
```

---

### Task 3: `FileMemoryStore` + `MemoryStoreAddressExtensions` become thin delegates

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:428-450` (`FormatQualifiedName`, `ResolveAddress`, `SanitizeName`) and `:512` (`SanitizeNameCore`)
- Modify: `src/Scrinia.Core/MemoryStoreAddressExtensions.cs:43-66`
- Test: `tests/Scrinia.Tests/P0NamingCasingTests.cs` (parity test, no new file)

**Interfaces:**
- Consumes: `MemoryNaming.ResolveAddress`, `MemoryNaming.FormatQualifiedName`, `MemoryNaming.SanitizeName` (Task 2).
- Produces: unchanged public shapes — `FileMemoryStore.ResolveAddress`, `FormatQualifiedName`, `SanitizeName` keep signatures for `IMemoryStore`/`IPluginStore` compat.

- [ ] **Step 1: Write the failing test** (parity: store delegates equal the home)

Append to `tests/Scrinia.Tests/P0NamingCasingTests.cs`:

```csharp
    [Fact]
    public void FileMemoryStore_Delegates_MatchMemoryNaming()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (Scrinia.Core.FileMemoryStore)Scrinia.Core.MemoryStoreContext.Current!;
        var addr = Scrinia.Core.Models.MemoryAddress.Parse("/api/Notes");
        store.ResolveAddress(addr).Should().Be(MemoryNaming.ResolveAddress(addr));
        store.FormatQualifiedName("local:api", "notes")
            .Should().Be(MemoryNaming.FormatQualifiedName("local:api", "notes"));
        store.SanitizeName("a/b").Should().Be(MemoryNaming.SanitizeName("a/b"));
    }
```

- [ ] **Step 2: Run test to verify it passes already (parity holds pre-delegate)** — this is a characterization pin, not a red test; record PASS as baseline, then refactor.

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests.FileMemoryStore_Delegates" -v`
Expected: PASS (bodies still verbatim-equal).

- [ ] **Step 3: Write minimal implementation** (replace bodies with delegates)

In `src/Scrinia.Core/FileMemoryStore.cs:428-450`, replace with:

```csharp
    public string FormatQualifiedName(string scope, string subject) =>
        MemoryNaming.FormatQualifiedName(scope, subject);

    internal bool IsEphemeral(string name) =>
        MemoryNaming.IsEphemeralName(name);

    public (string Scope, string Subject) ResolveAddress(MemoryAddress address) =>
        MemoryNaming.ResolveAddress(address);

    public string SanitizeName(string name) => MemoryNaming.SanitizeName(name);
```

In `src/Scrinia.Core/FileMemoryStore.cs:512`, replace with:

```csharp
    private static string SanitizeNameCore(string name) => MemoryNaming.SanitizeName(name);
```

In `src/Scrinia.Core/MemoryStoreAddressExtensions.cs:61-65`, replace the two `store.SanitizeName(...)` leaf calls with `MemoryNaming.SanitizeName(...)` (scope construction already calls `MemoryNaming.BuildScopedTopicScope`; signatures unchanged).

- [ ] **Step 4: Run tests to verify nothing broke**

Run: `dotnet build Scrinia.sln`
Expected: build green.

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests|FullyQualifiedName~MemoryNamingTests" -v`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/MemoryStoreAddressExtensions.cs tests/Scrinia.Tests/P0NamingCasingTests.cs
git commit -m "refactor: FileMemoryStore naming members delegate to MemoryNaming"
```

---

### Task 4: `PathParser` case-insensitive entity wrap (intentional behavior change)

**Files:**
- Modify: `src/Scrinia.Core/PathParser.cs:51-91`
- Test: `tests/Scrinia.Tests/P0NamingCasingTests.cs`

**Interfaces:**
- Consumes: `MemoryNaming.Comparer` (Task 1), `MemoryNaming.IsUriLike` (Task 1, for the `\`/`:` clauses).
- Produces: same `ParsedPath Parse(string, IReadOnlySet<string>)` signature; entity matching now case-insensitive regardless of caller set; tags/leaf keep original casing.

- [ ] **Step 1: Write the failing test**

Append to `tests/Scrinia.Tests/P0NamingCasingTests.cs`:

```csharp
    [Fact]
    public void PathParser_EntityMatch_IsCaseInsensitive_DespiteCaseSensitiveCallerSet()
    {
        var parsed = Scrinia.Core.PathParser.Parse(
            "/goal/G-5", new HashSet<string>(new[] { "goal" }, StringComparer.Ordinal));
        parsed.IsEntityPath.Should().BeTrue();
        parsed.EntityPairs.Should().ContainSingle().Which.Should().Be(
            new Scrinia.Core.EntityIdPair("goal", "G-5"));
    }

    [Fact]
    public void PathParser_EntityMatch_MixedCasePair()
    {
        var parsed = Scrinia.Core.PathParser.Parse(
            "/GOAL/g-5", new HashSet<string>(new[] { "goal" }, StringComparer.Ordinal));
        parsed.IsEntityPath.Should().BeTrue();
        parsed.Tags.Should().BeEmpty();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests.PathParser" -v`
Expected: FAIL — second test fails (`/GOAL/g-5` with case-sensitive caller set yields no entity pair today).

- [ ] **Step 3: Write minimal implementation**

In `src/Scrinia.Core/PathParser.cs:51`, after normalization and before the walk, copy the caller set once:

```csharp
        // P0: entity matching is case-insensitive regardless of what the caller
        // passed. Copy once (caller set untouched) so the pairwise walk below
        // uses OrdinalIgnoreCase. Tags/leaf keep original casing.
        var entityLookup = new HashSet<string>(entityTypes, StringComparer.OrdinalIgnoreCase);
```

Then at `:91` replace `entityTypes.Contains(current)` with `entityLookup.Contains(current)`. Do not change `:`/`\`/`..`/control-char guards in this task (colon/backslash overlap with `IsUriLike` is covered by Task 5's shared predicate; keep positional messages unchanged).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests.PathParser|FullyQualifiedName~DualModeParseTests" -v`
Expected: PASS (new pins + no traversal-rule regression).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/PathParser.cs tests/Scrinia.Tests/P0NamingCasingTests.cs
git commit -m "feat: PathParser entity matching is OrdinalIgnoreCase"
```

---

### Task 5: `MemoryAddress` URI-guard delegates to `IsUriLike`

**Files:**
- Modify: `src/Scrinia.Core/Models/MemoryAddress.cs:117-120`
- Test: `tests/Scrinia.Tests/P0NamingCasingTests.cs`

**Interfaces:**
- Consumes: `MemoryNaming.IsUriLike` (Task 1).
- Produces: same `TryParse`/`Parse`/`IsValid`/`Kind` behavior; `ContainsPhysicalOrUriSyntax` is a one-line delegate (private, no signature change).

- [ ] **Step 1: Write the failing test** (parity table through both surfaces)

Append to `tests/Scrinia.Tests/P0NamingCasingTests.cs`:

```csharp
    [Theory]
    [InlineData("/api/no?tes")]
    [InlineData("/api/no#tes")]
    [InlineData("/api/no%tes")]
    [InlineData("/api\\back")]
    [InlineData("topic:subject")]
    public void MemoryAddress_And_PathParser_RejectUriLike(string raw)
    {
        Scrinia.Core.Models.MemoryAddress.TryParse(
            raw, out _, out _).Should().BeFalse();
        if (raw.StartsWith('/'))
            Assert.Throws<System.ArgumentException>(() =>
                Scrinia.Core.PathParser.Parse(raw, new HashSet<string>()));
    }

    [Fact]
    public void MemoryAddress_LegacyColon_GateMessageSuggestsPath()
    {
        var ex = Assert.Throws<Scrinia.Core.Models.MemoryAddressException>(() =>
            Scrinia.Core.Models.MemoryAddress.Parse("topic:subject"));
        ex.Message.Should().Contain("Address");
    }
```

- [ ] **Step 2: Run test to verify it passes pre-refactor (characterization)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests.MemoryAddress" -v`
Expected: PASS (guards already reject; this pins the unified table before dedup).

- [ ] **Step 3: Write minimal implementation**

In `src/Scrinia.Core/Models/MemoryAddress.cs:117-120`, replace with:

```csharp
    private static bool ContainsPhysicalOrUriSyntax(string value) =>
        MemoryNaming.IsUriLike(value);
```

Keep the drive-letter subcase covered (`X:` subsumed by `:` — no extra clause). Keep `IsLegalSegment` (`:112-115`), JSON converter, `MaxUtf8Bytes`, ephemeral canonicalization untouched per frozen surfaces.

- [ ] **Step 4: Run test to verify it still passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests" -v`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Models/MemoryAddress.cs tests/Scrinia.Tests/P0NamingCasingTests.cs
git commit -m "refactor: MemoryAddress URI guard delegates to MemoryNaming.IsUriLike"
```

---

### Task 6: Swap all read matchers to `NameEquals` / `ResolveExistingName`

**Files:**
- Modify: `src/Scrinia.Core/IMemoryStore.cs:132-134`
- Modify: `src/Scrinia.Core/EntityTypingEventHandler.cs:51-53`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:467-468` (read barrier), `:2135` (Upsert cache), `:2206` (Remove cache), `:2335` (TryUpdate cache), `:2448` (Append lookup), `:2614` (StoreAsyncCore lookup), `:2818` (AppendAsyncCore lookup)
- Test: `tests/Scrinia.Tests/P0NamingCasingTests.cs`

**Interfaces:**
- Consumes: `MemoryNaming.NameEquals`, `MemoryNaming.ResolveExistingName` (Tasks 1–2).
- Produces: identical read semantics (first-match, stored casing forward); no signature changes.

- [ ] **Step 1: Write the failing test** (case-insensitive round-trip through store)

Append to `tests/Scrinia.Tests/P0NamingCasingTests.cs`:

```csharp
    [Fact]
    public async Task Store_CaseInsensitiveRead_PreservesStoredCasing()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (Scrinia.Core.FileMemoryStore)Scrinia.Core.MemoryStoreContext.Current!;
        var writeAddr = Scrinia.Core.Models.MemoryAddress.Parse("/api/Notes");
        await store.WriteAsync(writeAddr,
            new Scrinia.Core.MemoryWrite(writeAddr, new[] { "hello" }));
        var readAddr = Scrinia.Core.Models.MemoryAddress.Parse("/api/NOTES");
        var entry = await store.ReadAsync(readAddr);
        entry.Should().NotBeNull();
        entry!.Name.Should().Be("Notes");
    }
```

If `MemoryWrite` construction differs in this snapshot, copy the exact construction from an existing `WriteAsync` test — do not invent fields.

- [ ] **Step 2: Run test to verify it passes pre-swap (characterization)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests.Store_CaseInsensitiveRead" -v`
Expected: PASS (old inline rules already case-insensitive; this pins parity through the swap).

- [ ] **Step 3: Write minimal implementation** (mechanical, one site at a time)

1. `src/Scrinia.Core/IMemoryStore.cs:132-134`:
```csharp
        var current = LoadIndex(scope).FirstOrDefault(
            e => MemoryNaming.NameEquals(e.Name, subject));
```
2. `src/Scrinia.Core/EntityTypingEventHandler.cs:52-53`:
```csharp
        var entry = store.LoadIndex(scope)
            .FirstOrDefault(e => MemoryNaming.NameEquals(e.Name, subject));
```
3. `src/Scrinia.Core/FileMemoryStore.cs:467-468`:
```csharp
            return LoadIndexUnderRecoveryBarrier(scope).FirstOrDefault(entry =>
                MemoryNaming.ResolveExistingName(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(
                        new List<ArtifactEntry> { entry }), address) is not null);
```
Simpler preferred form (same semantics, no allocation) — replace the two-clause lambda with:
```csharp
            return MemoryNaming.ResolveExistingName(
                LoadIndexUnderRecoveryBarrier(scope), address);
```
4. Cache matchers — `FileMemoryStore.cs:2135`, `:2206`, `:2335`: replace `SanitizeName(existing.Name).Equals(target, StringComparison.OrdinalIgnoreCase)` with `MemoryNaming.NameEquals(existing.Name, target)` (where `target` is the already-sanitized string; `NameEquals` re-sanitizes idempotently — no behavior delta).
5. Lookup matchers — `:2448`, `:2614`, `:2818`: replace `e.Name.Equals(subject, OrdinalIgnoreCase)` / `entry.Name.Equals(subject, OrdinalIgnoreCase)` with `MemoryNaming.NameEquals(e.Name, subject)` / `MemoryNaming.NameEquals(entry.Name, subject)`.

- [ ] **Step 4: Run tests to verify**

Run: `dotnet build Scrinia.sln`
Expected: green.

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests|FullyQualifiedName~CaseSensitive|FullyQualifiedName~TryUpdateEntryTests" -v`
Expected: PASS — including `CaseSensitiveFileStoreTests` and `CaseSensitiveMcpAppendTests` pins (no casing regression on case-sensitive FS).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/EntityTypingEventHandler.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/P0NamingCasingTests.cs
git commit -m "refactor: route all read matchers through NameEquals ResolveExistingName"
```

---

### Task 7: Full matrix + frozen-surface verification

**Files:**
- Test: `tests/Scrinia.Tests/P0NamingCasingTests.cs` (append round-trip + legacy-colon pins)
- Verify: `src/Scrinia.Core/MemoryNaming.cs`, `src/Scrinia.Core/FileMemoryStore.cs:4512` (`DiscoverMemoriesRecursive` untouched), `src/Scrinia.Core/MemoryNaming.cs:392` (`ParseScopeLabel` untouched)

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces: green suite + explicit recovery-bypass pins.

- [ ] **Step 1: Write the failing tests** (round-trip + legacy colon + recovery bypass)

Append to `tests/Scrinia.Tests/P0NamingCasingTests.cs`:

```csharp
    [Theory]
    [InlineData("/notes", "local", "notes")]
    [InlineData("/api/notes", "local:api", "notes")]
    [InlineData("/a/b/c", "local:a/b", "c")]
    public void RoundTrip_Resolve_Format_Parse_Resolve(string raw, string scope, string subject)
    {
        using var _ = new TestHelpers.StoreScope();
        var addr = Scrinia.Core.Models.MemoryAddress.Parse(raw);
        var resolved = MemoryNaming.ResolveAddress(addr);
        resolved.Should().Be((scope, subject));
        var display = MemoryNaming.FormatQualifiedName(resolved.Scope, resolved.Subject);
        var reparsed = Scrinia.Core.Models.MemoryAddress.Parse(
            display.StartsWith('/') ? display : "/" + display);
        MemoryNaming.ResolveAddress(reparsed).Should().Be(resolved);
    }

    [Fact]
    public void LegacyColon_Rejected_WithPathSuggestion()
    {
        using var _ = new TestHelpers.StoreScope();
        var store = (Scrinia.Core.FileMemoryStore)Scrinia.Core.MemoryStoreContext.Current!;
        var ex = Assert.Throws<System.ArgumentException>(() =>
            store.ParseQualifiedName("topic:subject"));
        ex.Message.Should().Contain("/topic/subject");
    }

    [Fact]
    public void RecoveryBypass_ParseScopeLabel_Unvalidated()
    {
        MemoryNaming.ParseScopeLabel("local").Should().Be("local");
        MemoryNaming.ParseScopeLabel("ephemeral").Should().Be("ephemeral");
        MemoryNaming.ParseScopeLabel("api").Should().Be("local:api");
    }
```

- [ ] **Step 2: Run new tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~P0NamingCasingTests.RoundTrip|FullyQualifiedName~P0NamingCasingTests.LegacyColon|FullyQualifiedName~P0NamingCasingTests.RecoveryBypass" -v`
Expected: PASS (ephemeral display case `~x`/`/temp/x` canonicalization is covered by existing ephemeral tests; this task pins the persistent round-trip + gate + bypass).

- [ ] **Step 3: Full verification (no code change — confirm frozen surfaces)**

Confirm by inspection (no edits): `DiscoverMemoriesRecursive` (`FileMemoryStore.cs:4512`) and `ParseScopeLabel` (`MemoryNaming.cs:392`) are NOT routed through `TryBuild`; `MemoryAddressJsonConverter`, `MaxUtf8Bytes`, `IsEphemeralAlias`/`CanonicalizeEphemeralAlias` prefix-only folding, `FormatScopeLabel`/`ParseScopeLabel` round-trip (incl. `local:local` collision), `SplitCommaSeparated`/`NormalizeFilterList` null-shape — all untouched. Source-gen contexts untouched.

- [ ] **Step 4: Run full suite**

Run: `dotnet build Scrinia.sln`
Expected: green.

Run: `dotnet test Scrinia.sln`
Expected: green (default suite; excludes opt-in `Category=LongRunningBenchmark`).

- [ ] **Step 5: Commit**

```bash
git add tests/Scrinia.Tests/P0NamingCasingTests.cs
git commit -m "test: P0 naming casing round-trip colon-gate recovery pins"
```

---

## Self-Review

1. **Spec coverage (§1–§4 mapped):**
   - §1 `ResolveAddress`/`FormatQualifiedName` move → Task 2 + Task 3 delegates. ✅
   - §1 `NameEquals`+`Comparer` single case rule → Task 1, consumed Task 6. ✅
   - §1 `ResolveExistingName` 2 overloads (first-match, null-on-miss, no sorting) → Task 2, consumed Task 6 read barrier. ✅
   - §1 SanitizeName 3 categories (shared char rule / scope-only rejections / leaf leniency) → Task 2 `SanitizeName` + untouched `TryBuild`; Task 3 `SanitizeNameCore` delegate. ✅
   - §1 call-site rewrites (`IMemoryStore:132-134`, `EntityTypingEventHandler:52-53`, `:2135/:2206/:2335`, `:467-468`) → Task 6. ✅
   - §2 write/read/display data flow + leniency boundary (single-address throws, filter-listing matches-nothing) → preserved by verbatim move (Task 2) + round-trip test (Task 7); no `TryBuild` signature change. ✅
   - §2 recovery bypass (`DiscoverMemoriesRecursive`, `ParseScopeLabel` stay outside `TryBuild`) → Task 7 Step 3 inspection + bypass test. ✅
   - §3 `PathParser` OrdinalIgnoreCase wrap (caller set untouched, tags/leaf casing kept) → Task 4. ✅
   - §3 `IsUriLike` 6-clause unify (both `Parse` and `TryParse`) → Tasks 1+5 + parity table. ✅
   - §3 legacy colon gate + frozen surfaces → Tasks 5+7. ✅
   - §4 matrix (round-trip, comparer, first-match, GOAL/g-5, 6-clause, colon gate, CaseSensitive pins, DualModeParse) → Tasks 4–7 filters. ✅
   - §4 rollout (move-body → delegate → swap → test, no JSON changes, StoreScope) → Tasks 2→3→6→7. ✅
2. **Placeholder scan:** no TBD/TODO/"similar to"/"appropriate handling" — every code step has literal code, every run step has the exact `dotnet` command and expected outcome. ✅
3. **Type consistency:** `Comparer` is `StringComparer`; `NameEquals(string?, string?) → bool`; `IsUriLike(string) → bool`; `ResolveAddress(MemoryAddress) → (string Scope, string Subject)` tuple names match `FileMemoryStore`/`IPluginStore` call sites; `ResolveExistingName` returns `ArtifactEntry?` in both overloads; `FormatQualifiedName(string, string) → string`. Task 6 call sites use these exact names. ✅
