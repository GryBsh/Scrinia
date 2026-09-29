# Attributes Model — Plan 5: `scri migrate` (attributes forward-fill) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `scri migrate` — a one-time, idempotent, progress-reported command that rewrites every stored memory's sidecar into the new attributes model by **forward-filling** the legacy carriers into `Fields`/`Keywords` (`tags`→`tag`, `codeRefs` paths→`file`, `reviewAfter`/`reviewWhen`→`review`, `anchors`→`keywords`) **without clearing the legacy fields**, so every sidecar carries the new schema and Plan 6 can safely retire the legacy read/scoring paths and remove the legacy model fields.

**Architecture:** The transform is a pure `ArtifactEntry → ArtifactEntry` function (`AttributeMigration.Forward`) reusing `KnownFields.Merge`; the store driver (`AttributeMigration.MigrateStore`) enumerates every persistent entry across all scopes via `IMemoryStore.ListScoped("all")` on the v2-complete `FileMemoryStore` instance (`MemoryStoreContext.Current`), applies the transform, and `Upsert`s only entries that actually change (data-shape idempotency — there is no version marker). The `scri migrate` CLI command is a thin wrapper (`WorkspaceSetup.Configure` → optional backup → `MigrateStore` → progress/`--json` summary), mirroring the existing `Consolidate` (rewrite-all) and `Reindex` (progress) commands.

**Tech Stack:** C# / .NET 10, ConsoleAppFramework (source-generated CLI), System.Text.Json source-gen sidecars, xUnit + FluentAssertions.

## Global Constraints

- **Additive only — do NOT clear legacy fields.** `Forward` merges legacy data *into* `Fields`/`Keywords` and leaves `Tags`/`RetrievalAnchors`/`CodeRefs`/`ReviewAfter`/`ReviewWhen`/`Description` populated. Clearing them, cutting readers over to `Fields`, and removing the model fields is Plan 6 — doing any of it here would break `list`'s `[stale]`/`[review?]`/`[drift]` markers, the description column, drift detection, and `Consolidate`'s stale count, which all still read the legacy fields directly. No existing test may change its expected value.
- **Idempotent + non-destructive.** Running `migrate` twice must produce zero writes the second time (skip entries whose `Forward` output is set-equal to their current `Fields`/`Keywords`). Never overwrite an entry that doesn't change (SSD-wear + clean-diff discipline).
- **Use the instance store, not the static facade.** Enumerate via `MemoryStoreContext.Current` (a `FileMemoryStore`, v1+v2 topic discovery) with `ListScoped("all")` (the `"all"` selector includes entity-classified scopes that a `null` selector excludes). The static `ScriniaArtifactStore` facade's topic discovery is v1-only and would silently skip v2 `memories/` entries — do not use it for enumeration.
- **Ephemeral is out of scope.** `EphemeralEntry` is in-memory-only and never persisted; a fresh `scri migrate` process has an empty ephemeral store, so there is nothing on disk to migrate.
- **Do NOT touch** bench WIP (`benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`), `src/Scrinia.Mcp/plugins/opencode/*`, or `.superpowers/**`.
- Commit trailer, exactly:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
  then `Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g`
- Never push. `TreatWarningsAsErrors` is on for `Scrinia.Core`/`Scrinia` — build clean. Branch off `main` (`8656f90`) before Task 1.

## File Structure

- `src/Scrinia/Commands/ScriniaCommands.Migration.cs` — MODIFY: rename the existing hidden `Migrate` (v1→v2 layout) command to `migrate_v1` (Task 1); add the new `Migrate` (attributes) command (Task 4).
- `src/Scrinia.Core/Migration/AttributeMigration.cs` — CREATE: `Forward(ArtifactEntry)` transform + set-equality helpers (Task 2) and `MigrateStore(IMemoryStore, bool dryRun)` driver + `MigrateSummary` (Task 3).
- `tests/Scrinia.Tests/AttributeMigrationTests.cs` — CREATE: pure-transform unit tests (Task 2) + store-driver integration tests (Task 3), mirroring `FileMemoryStoreTests` seed/round-trip patterns.

Read `src/Scrinia/Commands/ScriniaCommands.Migration.cs` (the existing `Migrate`/`Consolidate` commands), `src/Scrinia/Commands/ScriniaCommands.Setup.cs` (`Reindex`), and `tests/Scrinia.Tests/FileMemoryStoreTests.cs` (raw `.meta.json` seeding, ~lines 478–497, and round-trip asserts) before implementing, and mirror their patterns.

---

### Task 1: Rename the existing hidden `Migrate` (v1→v2 layout) to `migrate_v1`

Free the `migrate` command name for the new attributes migration by renaming the existing hidden layout-migration command to `migrate_v1`.

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Migration.cs` (the `[Hidden] public Task<int> Migrate(...)` at ~L29, its `<summary>`, and any internal name references)
- Modify: any test/doc that invokes the command by the name `migrate` (grep first)

**Interfaces:**
- Produces: the existing v1→v2 layout migration is invoked as `scri migrate_v1`; the `scri migrate` name is now free.

- [ ] **Step 1: Find every reference to the existing command name**

Run: `git grep -nE '\bmigrate\b' -- src/Scrinia tests docs | grep -iv 'migrate-attributes\|migration\|migrated\|migrating'` and read the hits. Identify: the `Migrate` method itself; any `[Command(...)]`/help text; any test invoking it; any doc naming `scri migrate`. (The command is `[Hidden]`, so references are expected to be few.)

- [ ] **Step 2: Rename the command to `migrate_v1`**

Rename the method so its invocation name becomes exactly `migrate_v1`. ConsoleAppFramework derives the command name from the method name (kebab-cased). To get the exact underscore form the user asked for, set an explicit command-name override if the framework supports it, else use the closest supported form and record which you used:
- Preferred: rename the method to `MigrateV1` and add ConsoleAppFramework's command-name attribute to pin the invocation string to `migrate_v1` (verify the attribute name the framework uses — check how other commands override names, or the ConsoleAppFramework docs).
- Keep it `[Hidden]`. Update its `<summary>` to say "v1→v2 directory-layout migration (legacy)". Update any test/doc reference from `migrate` to `migrate_v1`.

- [ ] **Step 3: Verify the rename**

Run: `dotnet build src/Scrinia/Scrinia.csproj` clean, then verify the command surface: `dotnet run --project src/Scrinia -- --help` shows no top-level `migrate` (it's hidden anyway) and `dotnet run --project src/Scrinia -- migrate_v1 --help` resolves. Confirm `git grep -n 'scri migrate\b' -- docs tests` has no stale references to the old name (all now `migrate_v1`).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` → green (any test that invoked the old `migrate` now invokes `migrate_v1`).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia/Commands/ScriniaCommands.Migration.cs
git commit -F- <<'EOF'
refactor(cli): rename hidden v1->v2 layout migration to `migrate_v1`

Frees the `migrate` command name for the new attributes model migration.
The existing (hidden) directory-layout migration is now invoked as
`scri migrate_v1`; behavior is unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 2: The pure `AttributeMigration.Forward` transform

A pure, deterministic, idempotent `ArtifactEntry → ArtifactEntry` that forward-fills legacy carriers into `Fields`/`Keywords`, leaving legacy fields untouched.

**Files:**
- Create: `src/Scrinia.Core/Migration/AttributeMigration.cs`
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs`

**Interfaces:**
- Consumes: `KnownFields.Merge` (`src/Scrinia.Core/Search/KnownFields.cs:54`), `KnownFields.ReviewKey`, `ArtifactEntry` (`src/Scrinia.Core/Models/ArtifactEntry.cs`).
- Produces:
  - `public static ArtifactEntry AttributeMigration.Forward(ArtifactEntry entry)` — merges `Tags`→`Fields["tag"]`, `CodeRefs.Keys`→`Fields["file"]`, `ReviewAfter`(ISO `o`)+`ReviewWhen`→`Fields["review"]`, and `RetrievalAnchors`→`Keywords` (deduped, `OrdinalIgnoreCase`), via `KnownFields.Merge`; legacy fields, `Description`, and all other members are preserved unchanged.
  - `public static bool AttributeMigration.MigratesTo(ArtifactEntry entry, out ArtifactEntry migrated)` — returns `true` iff `Forward(entry)` differs from `entry` in `Fields` or `Keywords` (set-equality, order-insensitive, `OrdinalIgnoreCase`), with the transformed entry as the out-param. This is the write-or-skip decision that makes the driver idempotent.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Scrinia.Tests/AttributeMigrationTests.cs
using System.Globalization;
using FluentAssertions;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class AttributeMigrationTests
{
    private static ArtifactEntry MakeEntry(
        string[]? tags = null, string[]? anchors = null,
        Dictionary<string, string>? codeRefs = null,
        DateTimeOffset? reviewAfter = null, string? reviewWhen = null,
        string[]? keywords = null, Dictionary<string, string[]>? fields = null) =>
        new(Name: "n", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, Description: "desc",
            Tags: tags, Keywords: keywords, RetrievalAnchors: anchors,
            ReviewAfter: reviewAfter, ReviewWhen: reviewWhen, CodeRefs: codeRefs, Fields: fields);

    [Fact]
    public void Forward_MapsTagsToTagAttribute_AnchorsToKeywords()
    {
        var e = MakeEntry(tags: ["security", "auth"], anchors: ["how do I log in"]);
        var m = AttributeMigration.Forward(e);
        m.Fields!["tag"].Should().BeEquivalentTo("security", "auth");
        m.Keywords.Should().Contain("how do I log in");
        // additive: legacy fields preserved
        m.Tags.Should().BeEquivalentTo("security", "auth");
        m.RetrievalAnchors.Should().BeEquivalentTo("how do I log in");
        m.Description.Should().Be("desc");
    }

    [Fact]
    public void Forward_MapsCodeRefPathsToFile_AndReviewToReview()
    {
        var e = MakeEntry(
            codeRefs: new() { ["src/A.cs"] = "hashA", ["src/B.cs"] = "hashB" },
            reviewAfter: DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture),
            reviewWhen: "when auth changes");
        var m = AttributeMigration.Forward(e);
        m.Fields!["file"].Should().BeEquivalentTo("src/A.cs", "src/B.cs");   // paths, not hashes
        m.Fields!["review"].Should().Contain(v => v.StartsWith("2026-08-01"))
            .And.Contain("when auth changes");
    }

    [Fact]
    public void Forward_UnionsIntoExistingFields_WithoutDuplicating()
    {
        var e = MakeEntry(codeRefs: new() { ["src/A.cs"] = "h" },
            fields: new() { ["file"] = ["src/A.cs"], ["person"] = ["maria"] });
        var m = AttributeMigration.Forward(e);
        m.Fields!["file"].Should().BeEquivalentTo("src/A.cs");   // union dedupes
        m.Fields!["person"].Should().BeEquivalentTo("maria");    // untouched
    }

    [Fact]
    public void MigratesTo_IsIdempotent_SecondPassReportsNoChange()
    {
        var e = MakeEntry(tags: ["x"], anchors: ["y"]);
        AttributeMigration.MigratesTo(e, out var once).Should().BeTrue();
        AttributeMigration.MigratesTo(once, out _).Should().BeFalse();   // already forward-filled
    }

    [Fact]
    public void MigratesTo_NoLegacyData_ReportsNoChange()
    {
        var e = MakeEntry(fields: new() { ["topic"] = ["auth"] });
        AttributeMigration.MigratesTo(e, out _).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~AttributeMigrationTests`
Expected: FAIL to compile — `Scrinia.Core.Migration.AttributeMigration` does not exist.

- [ ] **Step 3: Implement the transform**

```csharp
// src/Scrinia.Core/Migration/AttributeMigration.cs
using System.Globalization;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core.Migration;

/// <summary>
/// Forward-fills the legacy memory carriers into the attributes model: tags→`tag`,
/// codeRefs paths→`file`, reviewAfter/reviewWhen→`review` (ISO date + condition text),
/// anchors→keywords. ADDITIVE — legacy fields are left populated so the legacy read/scoring
/// paths keep working; clearing them and removing the model fields is the cleanup plan.
/// Deterministic and idempotent: re-running produces no change once an entry is forward-filled.
/// </summary>
public static class AttributeMigration
{
    /// <summary>The attribute key legacy free-form tags migrate to.</summary>
    public const string TagKey = "tag";

    public static ArtifactEntry Forward(ArtifactEntry entry)
    {
        var incoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (entry.Tags is { Length: > 0 })
            incoming[TagKey] = entry.Tags;
        if (entry.CodeRefs is { Count: > 0 })
            incoming["file"] = entry.CodeRefs.Keys.ToArray();
        var review = new List<string>(2);
        if (entry.ReviewAfter is { } ra)
            review.Add(ra.ToString("o", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(entry.ReviewWhen))
            review.Add(entry.ReviewWhen!);
        if (review.Count > 0)
            incoming[KnownFields.ReviewKey] = review.ToArray();

        var mergedFields = incoming.Count > 0 ? KnownFields.Merge(entry.Fields, incoming) : entry.Fields;

        string[]? mergedKeywords = entry.Keywords;
        if (entry.RetrievalAnchors is { Length: > 0 })
            mergedKeywords = (entry.Keywords ?? [])
                .Concat(entry.RetrievalAnchors)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return entry with { Fields = mergedFields, Keywords = mergedKeywords };
    }

    /// <summary>True iff Forward(entry) changes the entry's Fields or Keywords (the write-or-skip
    /// decision). Set-equal comparison (order-insensitive) keeps re-runs write-free.</summary>
    public static bool MigratesTo(ArtifactEntry entry, out ArtifactEntry migrated)
    {
        migrated = Forward(entry);
        return !FieldsSetEqual(entry.Fields, migrated.Fields)
            || !SetEqual(entry.Keywords, migrated.Keywords);
    }

    private static bool SetEqual(string[]? a, string[]? b)
    {
        var sa = a is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                           : new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var sb = b is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                           : new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        return sa.SetEquals(sb);
    }

    private static bool FieldsSetEqual(
        Dictionary<string, string[]>? a, Dictionary<string, string[]>? b)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca != cb) return false;
        if (a is null || b is null) return ca == 0 && cb == 0;
        foreach (var (k, va) in a)
        {
            if (!b.TryGetValue(k, out var vb)) return false;
            if (!SetEqual(va, vb)) return false;
        }
        return true;
    }
}
```

- [ ] **Step 4: Run to verify green**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~AttributeMigrationTests` → PASS (5/5). Full `Scrinia.Tests` suite still green.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Migration/AttributeMigration.cs tests/Scrinia.Tests/AttributeMigrationTests.cs
git commit -F- <<'EOF'
feat(core): add AttributeMigration.Forward (legacy -> attributes forward-fill)

Pure, idempotent ArtifactEntry transform: tags->tag, codeRefs paths->file,
reviewAfter/reviewWhen->review, anchors->keywords, via KnownFields.Merge.
Additive — legacy fields are preserved (removal is the cleanup plan).
MigratesTo() is the set-equal write-or-skip decision that keeps re-runs write-free.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 3: The `AttributeMigration.MigrateStore` driver

Enumerate every persistent entry across all scopes, apply `Forward`, and `Upsert` only the entries that change; return a summary. Integration-tested over a real `FileMemoryStore` on a temp dir.

**Files:**
- Modify: `src/Scrinia.Core/Migration/AttributeMigration.cs` (add `MigrateStore` + `MigrateSummary`)
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs` (add the driver tests)

**Interfaces:**
- Consumes: `IMemoryStore.ListScoped(string?)` and `IMemoryStore.Upsert(ArtifactEntry, string)` (`src/Scrinia.Core/IMemoryStore.cs:26,22`).
- Produces:
  - `public readonly record struct MigrateSummary(int Total, int Migrated, int Skipped);`
  - `public static MigrateSummary MigrateStore(IMemoryStore store, bool dryRun = false, Action<int,int>? onProgress = null)` — iterates `store.ListScoped("all")`; for each `ScopedArtifact`, if `MigratesTo` is true, `Upsert`s the transformed entry to its scope (unless `dryRun`) and counts it Migrated, else Skipped; invokes `onProgress(done, total)` per entry.

- [ ] **Step 1: Write the failing tests**

Read `tests/Scrinia.Tests/FileMemoryStoreTests.cs` first (its temp-workspace `FileMemoryStore` construction and how it seeds entries — prefer constructing entries via `store.Upsert(entry, scope)` in the test, which is simpler than hand-writing `.meta.json`). Then:

```csharp
// add to AttributeMigrationTests.cs
[Fact]
public void MigrateStore_ForwardFillsLegacyEntries_AndIsIdempotent()
{
    using var tmp = new TempWorkspace();                       // mirror FileMemoryStoreTests' temp-dir helper
    var store = tmp.NewStore();                                // a FileMemoryStore over the temp root
    store.Upsert(MakeEntry(tags: ["security"], anchors: ["how to log in"]) with { Name = "a" }, "local");
    store.Upsert(MakeEntry(fields: new() { ["topic"] = ["auth"] }) with { Name = "b" }, "local");

    var first = AttributeMigration.MigrateStore(store);
    first.Total.Should().Be(2);
    first.Migrated.Should().Be(1);    // only "a" had legacy data
    first.Skipped.Should().Be(1);     // "b" already attributes-only

    var reread = store.LoadIndex("local").First(e => e.Name == "a");
    reread.Fields!["tag"].Should().BeEquivalentTo("security");
    reread.Keywords.Should().Contain("how to log in");

    // idempotent: second run writes nothing
    var second = AttributeMigration.MigrateStore(store);
    second.Migrated.Should().Be(0);
    second.Skipped.Should().Be(2);
}

[Fact]
public void MigrateStore_DryRun_ReportsButDoesNotWrite()
{
    using var tmp = new TempWorkspace();
    var store = tmp.NewStore();
    store.Upsert(MakeEntry(tags: ["x"]) with { Name = "a" }, "local");

    var summary = AttributeMigration.MigrateStore(store, dryRun: true);
    summary.Migrated.Should().Be(1);
    store.LoadIndex("local").First(e => e.Name == "a").Fields.Should().BeNull();   // unwritten
}

[Fact]
public void MigrateStore_CoversTopicScopes()
{
    using var tmp = new TempWorkspace();
    var store = tmp.NewStore();
    store.Upsert(MakeEntry(tags: ["t"]) with { Name = "note" }, "local-topic:findings");

    AttributeMigration.MigrateStore(store).Migrated.Should().Be(1);
    store.LoadIndex("local-topic:findings").First().Fields!["tag"].Should().BeEquivalentTo("t");
}
```

If `FileMemoryStoreTests` has no reusable `TempWorkspace`/`NewStore` helper, replicate its exact temp-dir + `new FileMemoryStore(root, ...)` setup inline (read that file and mirror it — do not invent a new abstraction). Confirm the scope string for topics it uses (`local-topic:<name>`) matches that file's usage.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~AttributeMigrationTests.MigrateStore`
Expected: FAIL to compile — `MigrateStore`/`MigrateSummary` do not exist.

- [ ] **Step 3: Implement the driver**

```csharp
// add to AttributeMigration
public readonly record struct MigrateSummary(int Total, int Migrated, int Skipped);

/// <summary>Forward-fills every persistent entry across all scopes. Idempotent: only entries
/// whose Fields/Keywords actually change are written. `ListScoped("all")` reaches entity-classified
/// scopes that the default selector excludes; ephemeral entries are in-memory-only, so a fresh
/// migrate process sees none. Pass the FileMemoryStore instance (MemoryStoreContext.Current) — the
/// static facade's topic discovery is v1-only.</summary>
public static MigrateSummary MigrateStore(IMemoryStore store, bool dryRun = false, Action<int, int>? onProgress = null)
{
    var all = store.ListScoped("all");
    int total = all.Count, migrated = 0, done = 0;
    foreach (var item in all)
    {
        if (MigratesTo(item.Entry, out var next))
        {
            if (!dryRun) store.Upsert(next, item.Scope);
            migrated++;
        }
        onProgress?.Invoke(++done, total);
    }
    return new MigrateSummary(total, migrated, total - migrated);
}
```

- [ ] **Step 4: Run to verify green**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~AttributeMigrationTests` → all green (transform + driver). Full `Scrinia.Tests` suite green.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Migration/AttributeMigration.cs tests/Scrinia.Tests/AttributeMigrationTests.cs
git commit -F- <<'EOF'
feat(core): add AttributeMigration.MigrateStore driver (idempotent, all scopes)

Enumerates every persistent entry via ListScoped("all") and Upserts only entries
whose Fields/Keywords change, returning a (Total, Migrated, Skipped) summary with a
progress callback. Idempotent (re-run writes nothing), dry-run aware, topic scopes
covered. Integration-tested over a temp FileMemoryStore.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 4: The `scri migrate` CLI command

A thin ConsoleAppFramework command wrapping `MigrateStore`: configure the workspace, optionally back up `.scrinia/`, run, report progress to stderr and an optional `--json` summary. Mirrors `Consolidate` (`--dry-run`/`--json`/backup) and `Reindex` (progress idiom).

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Migration.cs` (add the new `Migrate` method + its `--json` output record, alongside the renamed `migrate_v1`)

**Interfaces:**
- Consumes: `WorkspaceSetup.Configure(string?)` (`src/Scrinia/Commands/WorkspaceSetup.cs:16`), `MemoryStoreContext.Current`, `AttributeMigration.MigrateStore` (Task 3). Mirror the backup helper `CopyDirectoryRecursive` and the `CliJsonContext` `--json` pattern already in `ScriniaCommands.Migration.cs`.
- Produces: `scri migrate [--workspace-root <path>] [--dry-run] [--no-backup] [--json]` → exit 0 on success, prints a `(total, migrated, skipped)` summary.

- [ ] **Step 1: Write the command (mirror `Consolidate`)**

Read the existing `Consolidate` (`ScriniaCommands.Migration.cs:328`) and the renamed `migrate_v1` backup helper. Add:

```csharp
/// <summary>Migrate stored memories to the attributes model (forward-fill: tags, codeRefs,
/// review, and anchors become attributes/keywords). Additive and idempotent — safe to re-run.</summary>
/// <param name="workspaceRoot">Workspace root. Defaults to the nearest ancestor with a .scrinia/ dir.</param>
/// <param name="dryRun">Report what would change without writing.</param>
/// <param name="backup">Copy .scrinia/ to a timestamped backup first (default true).</param>
/// <param name="json">Emit a machine-readable summary.</param>
public Task<int> Migrate(
    string? workspaceRoot = null, bool dryRun = false, bool backup = true, bool json = false,
    CancellationToken cancellationToken = default)
{
    WorkspaceSetup.Configure(workspaceRoot);
    var store = MemoryStoreContext.Current
        ?? throw new InvalidOperationException("No workspace configured.");

    if (backup && !dryRun)
        BackupScriniaDir(store);   // reuse the existing timestamped-backup helper the layout migration uses

    int lastPct = -1;
    var summary = AttributeMigration.MigrateStore(store, dryRun, (done, total) =>
    {
        int pct = total == 0 ? 100 : done * 100 / total;
        if (pct != lastPct) { Console.Error.Write($"\r[scrinia] migrating {done}/{total}…"); lastPct = pct; }
    });
    Console.Error.WriteLine();

    if (json)
        Console.WriteLine(JsonSerializer.Serialize(
            new CliMigrateOutput(summary.Total, summary.Migrated, summary.Skipped, dryRun),
            CliJsonContext.Default.CliMigrateOutput));
    else
        Console.WriteLine($"{(dryRun ? "[dry-run] " : "")}Migrated {summary.Migrated} of {summary.Total} memories ({summary.Skipped} already current).");

    return Task.FromResult(0);
}
```

Add the output record + register it on the existing `CliJsonContext`:

```csharp
public sealed record CliMigrateOutput(int Total, int Migrated, int Skipped, bool DryRun);
// and: [JsonSerializable(typeof(CliMigrateOutput))] on the existing CliJsonContext partial.
```

If a reusable timestamped-backup helper (`BackupScriniaDir`/`CopyDirectoryRecursive`) already exists from `migrate_v1`, call it; otherwise factor the existing `migrate_v1` backup block into a shared private helper rather than duplicating it. The `--backup` flag surfaces as `--no-backup` (ConsoleAppFramework negates default-true bools) — verify the actual flag name via `--help`.

- [ ] **Step 2: Build + smoke-test the command surface**

Run: `dotnet build src/Scrinia/Scrinia.csproj` clean. Then `dotnet run --project src/Scrinia -- migrate --help` resolves and shows the flags. If `ScriniaCommands` has an existing command-level test harness (grep the test project for `new ScriniaCommands()` invocations), add a smoke test that constructs a temp workspace, seeds one legacy entry, calls `.Migrate(workspaceRoot: tmp, json: true)`, and asserts exit 0 + the entry gained `Fields["tag"]`. If commands are not unit-tested in this repo, rely on the Task 3 driver tests (which cover the logic) plus this build/`--help` smoke check, and note that in the report.

- [ ] **Step 3: Verify idempotency end-to-end (manual, recorded in report)**

On the repo's own `.scrinia/` (or a temp copy), run `dotnet run --project src/Scrinia -- migrate --dry-run --json`, then `... migrate --json`, then `... migrate --json` again; confirm the third run reports `Migrated: 0`. Record the three summaries in the task report.

- [ ] **Step 4: Full suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` → green. `dotnet build` across `Scrinia.Core`/`Scrinia` clean (TreatWarningsAsErrors).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia/Commands/ScriniaCommands.Migration.cs
git commit -F- <<'EOF'
feat(cli): add `scri migrate` — attributes forward-fill command

Thin wrapper over AttributeMigration.MigrateStore: configures the workspace, backs
up .scrinia/ (default, skipped on --dry-run/--no-backup), forward-fills every memory
to the attributes model with stderr progress, and prints a (total, migrated, skipped)
summary (--json optional). Additive and idempotent — safe to re-run.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

## Verification

1. `dotnet build` clean across `Scrinia.Core`/`Scrinia` (TreatWarningsAsErrors on).
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` fully green — **no pre-existing test changes its expected value** (this plan is additive; it adds a new command + a new Core helper and touches no read/scoring path).
3. `scri migrate --help` and `scri migrate_v1 --help` both resolve; top-level `scri migrate` maps to the attributes migration.
4. Idempotency: three consecutive `scri migrate --json` runs on a populated store → first migrates N, second and third report `Migrated: 0`.
5. Additivity: a memory written the old way still lists/searches/shows identically after migrate (legacy fields untouched; `Fields` gained the forward-filled data).

## Self-review (done during authoring)

- **Spec coverage:** implements Unit 6 (`scri migrate` — one-time, idempotent, progress-reported). The command name is `migrate` (spec-verbatim); the pre-existing hidden layout `migrate` is renamed to `migrate_v1` (Task 1) to free it.
- **Sequencing discipline:** migrate is **additive forward-fill only** — it does not clear legacy fields or touch any read/scoring path, so no `list`/drift/stale/`Consolidate`/scorer behavior changes and no existing test moves. Clearing the legacy fields, cutting the readers over to `Fields`, and removing the legacy model members is Plan 6 — safe there precisely because this plan guarantees every sidecar carries the data in `Fields`.
- **Correctness levers:** idempotency is data-shape based (`MigratesTo` set-equality) since there is no version marker; enumeration uses the v2-complete `FileMemoryStore` instance with `ListScoped("all")` to avoid the static facade's v1-only topic gap and the default selector's entity-scope exclusion; ephemeral entries are correctly ignored (in-memory-only).
- **Type consistency:** `Forward: ArtifactEntry→ArtifactEntry` and `MigratesTo(entry, out migrated): bool` (Task 2) are consumed unchanged by `MigrateStore` (Task 3), which the CLI command (Task 4) calls with a progress lambda; `MigrateSummary(Total, Migrated, Skipped)` flows to `CliMigrateOutput`.
- **Risk flagged for the runbook (not a code change):** if the `scri serve` daemon is running, its in-process index cache won't see migrate's out-of-process writes until its next invalidation — recommend running `migrate` with the daemon stopped, or restarting `serve` afterward.
