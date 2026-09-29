# P6.6b Merge 1: `Fields` → `Attributes` rename + on-disk key migration

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the `ArtifactEntry`/`EphemeralEntry` `Fields` property to `Attributes` so the C# name matches the domain term used everywhere else (`attributes` tool param, `AttributeReaders`, `AttributeMigration`), and migrate the persisted sidecar JSON key `"fields"` → `"attributes"` with dual-read tolerance so already-written sidecars still load and `scri migrate` rewrites them.

**Architecture:** Pure additive rename + serialization-contract change. The property is renamed and pinned to JSON key `"attributes"`; a read-only ingest member captures the legacy `"fields"` key so old sidecars deserialize losslessly; a coalesce at every deserialization seam moves the legacy value into `Attributes`; `AttributeMigration.MigratesTo` flags a legacy-key entry so `scri migrate` (and the read-time forward-fill hook) rewrite the sidecar under the new key. No member is removed and no write-path semantics change — this merge is safe to ship on its own and independently validatable by rebuilding and running `migrate`.

**Tech Stack:** .NET 10, C# records with primary constructors, System.Text.Json source-gen (`FileStoreJsonContext`, `StoreJsonContext`), xUnit + FluentAssertions.

## Global Constraints

- **TreatWarningsAsErrors is on for all product projects.** Zero warnings, or the build fails. (Stale-LSP CS8019/CS8933 on global usings are IDE-only, never emitted by `dotnet build` — ignore those.)
- **Do NOT touch:** `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, `.superpowers/**` (product code), `.git/**`.
- **Commit trailer — exact, both lines, on every commit:**
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```
- **Never push.** Commit only.
- **Staging discipline — NEVER `git add -A` / `git add .`.** The working tree carries pre-existing out-of-scope modifications (`benchmarks/**`, `.gitignore`, `src/Scrinia.Mcp/plugins/opencode/*`) and many untracked docs that must NOT be committed. Stage with `git add -- src tests ':!src/Scrinia.Mcp/plugins'`, then run `git status` and confirm no `benchmarks/`, `plugins/`, `.gitignore`, or stray untracked doc is staged before committing. (The plan doc itself stays untracked — matching the repo convention that `docs/superpowers/plans/*` are not committed.)
- **Benchmarks build clean under THIS merge** (they consume `ArtifactEntry` but never `.Fields`/`Fields:`), so `dotnet build Scrinia.sln` stays green. Do NOT edit `benchmarks/**`.
- **Rename scope discipline:** only `ArtifactEntry.Fields` and `EphemeralEntry.Fields` become `Attributes`. Unrelated `.Fields` members on OTHER types (e.g. `GeminiChatProvider`, `SafeTensorsReader`, tensor/provider types) must NOT be touched — the compiler disambiguates because the rename only breaks calls against the two record types.
- **Back-compat is mandatory (user decision):** a sidecar written by a pre-rename binary (key `"fields"`) MUST load with `Attributes` populated. New writes MUST emit only `"attributes"`, never `"fields"`. This must hold under `PublishTrimmed` (source-gen only — no reflection fallback; the `scri serve` trimmed publish is the proof surface).
- **`EphemeralEntry` is never persisted** (no `[JsonSerializable(typeof(EphemeralEntry))]` anywhere) — its rename is code-only; it gets NO legacy-key ingest member.
- **This is a rename + serialization change, NOT a scoring change** (ranker weights, `Forward`'s folding semantics untouched) → no ranking-measurement gate.

---

### Task 1: Rename the property + establish the serialization contract on `ArtifactEntry`

**Files:**
- Modify: `src/Scrinia.Core/Models/ArtifactEntry.cs`
- Modify: `src/Scrinia.Core/Models/EphemeralEntry.cs`
- Test: `tests/Scrinia.Tests/ArtifactEntrySerializationTests.cs` (create)

**Interfaces:**
- Produces: `ArtifactEntry.Attributes` (was `Fields`) — `Dictionary<string,string[]>?`, JSON key `"attributes"`. A read-only ingest member `LegacyFields` (JSON key `"fields"`, never serialized) plus a `NormalizeAttributesKey()` instance method returning a copy with the legacy value coalesced into `Attributes`. `EphemeralEntry.Attributes` (was `Fields`), no legacy member.
- Consumes: nothing from prior tasks (first task).

**Context:** The `Attributes` dict currently serializes under camelCase key `"fields"` (property `Fields`, `PropertyNamingPolicy = CamelCase` in both `FileStoreJsonContext` at `FileMemoryStore.cs:164-171` and `StoreJsonContext` at `ScriniaArtifactStore.cs:178-180`). Renaming the property to `Attributes` alone would flip the key to `"attributes"` and silently orphan every existing sidecar's `"fields"` data. The ingest member + coalesce (wired at the load seams in Task 2) is what preserves that data.

- [ ] **Step 1: Write the failing serialization contract tests**

Create `tests/Scrinia.Tests/ArtifactEntrySerializationTests.cs`. Use the same source-gen context the store uses so the test is trim-faithful. Because `FileStoreJsonContext` is `private` inside `FileMemoryStore`, serialize through a local test context declared in the test file:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class ArtifactEntrySerializationTests
{
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ArtifactEntry))]
    private partial class TestCtx : JsonSerializerContext;

    private static ArtifactEntry Sample(Dictionary<string, string[]>? attrs) => new(
        Name: "n", Uri: "mem://n", OriginalBytes: 10, ChunkCount: 1,
        CreatedAt: DateTimeOffset.UnixEpoch, Description: "d", Attributes: attrs);

    [Fact]
    public void Serializes_attributes_under_new_key_and_never_writes_fields()
    {
        var json = JsonSerializer.Serialize(
            Sample(new() { ["review"] = ["cond"] }), TestCtx.Default.ArtifactEntry);
        json.Should().Contain("\"attributes\":");
        json.Should().NotContain("\"fields\":");
    }

    [Fact]
    public void Reads_new_attributes_key()
    {
        const string json = """{"name":"n","uri":"mem://n","originalBytes":10,"chunkCount":1,"createdAt":"1970-01-01T00:00:00+00:00","description":"d","attributes":{"tag":["x"]}}""";
        var e = JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry)!.NormalizeAttributesKey();
        e.Attributes.Should().ContainKey("tag");
    }

    [Fact]
    public void Reads_legacy_fields_key_into_attributes()
    {
        const string json = """{"name":"n","uri":"mem://n","originalBytes":10,"chunkCount":1,"createdAt":"1970-01-01T00:00:00+00:00","description":"d","fields":{"tag":["x"]}}""";
        var e = JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry)!.NormalizeAttributesKey();
        e.Attributes.Should().ContainKey("tag");
    }

    [Fact]
    public void New_key_wins_when_both_present()
    {
        const string json = """{"name":"n","uri":"mem://n","originalBytes":10,"chunkCount":1,"createdAt":"1970-01-01T00:00:00+00:00","description":"d","attributes":{"tag":["new"]},"fields":{"tag":["old"]}}""";
        var e = JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry)!.NormalizeAttributesKey();
        e.Attributes!["tag"].Should().Equal("new");
    }

    [Fact]
    public void Normalized_entry_reserializes_without_fields_key()
    {
        const string json = """{"name":"n","uri":"mem://n","originalBytes":10,"chunkCount":1,"createdAt":"1970-01-01T00:00:00+00:00","description":"d","fields":{"tag":["x"]}}""";
        var e = JsonSerializer.Deserialize(json, TestCtx.Default.ArtifactEntry)!.NormalizeAttributesKey();
        var round = JsonSerializer.Serialize(e, TestCtx.Default.ArtifactEntry);
        round.Should().Contain("\"attributes\":");
        round.Should().NotContain("\"fields\":");
    }
}
```

- [ ] **Step 2: Run the tests to confirm they fail to compile** (`Attributes`/`NormalizeAttributesKey` don't exist yet)

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter ArtifactEntrySerializationTests`
Expected: compile error — `ArtifactEntry` has no `Attributes`/`NormalizeAttributesKey`.

- [ ] **Step 3: Rename the property + add the legacy ingest member on `ArtifactEntry`**

In `src/Scrinia.Core/Models/ArtifactEntry.cs`:
1. Rename positional param `Fields` → `Attributes` and pin its key:
   ```csharp
   [property: System.Text.Json.Serialization.JsonPropertyName("attributes")]
   Dictionary<string, string[]>? Attributes = null)
   ```
   Update the `<summary>` above it to say "Unified multi-valued **attributes**…".
2. Add a body member + coalesce method inside the record (after the primary constructor, `{ … }`):
   ```csharp
   {
       // Back-compat ingest: sidecars written before the Fields→Attributes rename stored the
       // dict under JSON key "fields". Deserialization routes that key here so old sidecars load
       // losslessly. The getter stays observable (so AttributeMigration.MigratesTo can detect a
       // not-yet-rewritten legacy entry); NormalizeAttributesKey() folds it into Attributes AND
       // nulls it, so a normalized entry re-serializes WITHOUT a "fields" key (WhenWritingNull).
       // Freshly-constructed entries never set it → never emit "fields". Remove once all
       // workspaces have migrated (Merge 3 cleanup candidate).
       private readonly Dictionary<string, string[]>? _legacyFields;

       [System.Text.Json.Serialization.JsonInclude]
       [System.Text.Json.Serialization.JsonPropertyName("fields")]
       public Dictionary<string, string[]>? LegacyFields
       {
           get => _legacyFields;
           init => _legacyFields = value;
       }

       /// <summary>Coalesces a legacy "fields"-key value into <see cref="Attributes"/> (new key
       /// wins) and clears the legacy holder so re-serialization emits only "attributes". Call
       /// once immediately after deserializing a persisted entry. No-op when no legacy value.</summary>
       public ArtifactEntry NormalizeAttributesKey()
           => _legacyFields is not null
               ? this with { Attributes = Attributes ?? _legacyFields, LegacyFields = null }
               : this;
   }
   ```
   Note on the `with`: the record's generated copy-constructor first copies `_legacyFields`, then the `LegacyFields = null` init-setter clears it on the copy — so `Attributes` takes the original legacy value while the copy serializes clean. Verify the `Serializes_…never_writes_fields` and `Normalized_entry_reserializes_without_fields_key` tests both pass, which pins this behavior.

- [ ] **Step 4: Rename the property on `EphemeralEntry`** (no legacy member — never persisted)

In `src/Scrinia.Core/Models/EphemeralEntry.cs`: rename positional `Fields` → `Attributes`, add `[property: JsonPropertyName("attributes")]` for consistency, update the `<summary>` prose. No `LegacyFields`, no `NormalizeAttributesKey`.

- [ ] **Step 5: Get the solution compiling — rename every reference (compiler-driven)**

The property rename breaks the build ONLY at `ArtifactEntry`/`EphemeralEntry` `.Fields` sites. Build, and fix each error by renaming `.Fields` → `.Attributes`. Also rename the `fields` PARAMETER name → `attributes` on the four `AttributeReaders` methods (`GetReviewStatus`, `PreviewText` unaffected, `DisplayTags`, `DisplayAnchors`) and their `<param>`/`<c>Fields</c>` doc references to `Attributes`. Do NOT touch `.Fields` on unrelated types.

Run repeatedly until clean: `dotnet build Scrinia.sln`
Expected: 0 errors, 0 warnings. (Existing tests that construct entries with `Fields:` named arg will also fail to compile — rename those named args `Fields:` → `Attributes:` in the same pass. Do not change test assertions yet; only what's needed to compile.)

- [ ] **Step 6: Run the new tests + the full core/tests suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Expected: PASS, including all 5 `ArtifactEntrySerializationTests`. If any pre-existing test asserted serialization contained `"fields"`, that is a genuine contract change — update it to `"attributes"` and note it in the report.

- [ ] **Step 7: Commit**

```bash
git add -- src tests ':!src/Scrinia.Mcp/plugins'
git status   # confirm no benchmarks/, plugins/, .gitignore, or stray docs staged
git commit -m "refactor(core): rename ArtifactEntry/EphemeralEntry Fields -> Attributes; persist under 'attributes' key with legacy 'fields' read tolerance"
```

---

### Task 2: Coalesce the legacy key at every load seam + make `migrate` rewrite it

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (deserialize seams at `LoadEntriesFromSidecars` ~813, `LoadEntriesFromLegacyIndex` ~830)
- Modify: `src/Scrinia/Mcp/ScriniaArtifactStore.cs` (deserialize seams at `LoadEntriesFromSidecars` ~427, `LoadEntriesFromLegacyIndex` ~444)
- Modify: `src/Scrinia.Core/Migration/AttributeMigration.cs` (`MigratesTo` must return true when a legacy-key entry needs its key rewritten)
- Test: `tests/Scrinia.Tests/AttributesKeyMigrationTests.cs` (create)

**Interfaces:**
- Consumes: `ArtifactEntry.NormalizeAttributesKey()`, `ArtifactEntry.LegacyFields` (Task 1).
- Produces: every deserialization of a persisted `ArtifactEntry` returns an entry with `Attributes` already coalesced (callers never see a stray `LegacyFields`). `scri migrate` and the read-time forward-fill hook rewrite a legacy-key sidecar to the `"attributes"` key.

**Context:** Deserialization populates `LegacyFields` but leaves `Attributes` null for old sidecars; consumers read `.Attributes`, so the coalesce must run right after each `JsonSerializer.Deserialize(...)`. `IndexFile` (legacy index) holds `ArtifactEntry[]` — normalize each element. The read-time forward-fill hook (`FileMemoryStore.LoadIndexUnsafe` ~757-765) already calls `AttributeMigration.MigratesTo` and rewrites when true; making `MigratesTo` true for a legacy-key entry piggybacks the key rewrite onto machinery that already exists (and onto `scri migrate`'s `MigrateStore`).

- [ ] **Step 1: Write the failing key-migration tests**

Create `tests/Scrinia.Tests/AttributesKeyMigrationTests.cs`. Follow the **cold-cache contract** established in the migrate-fidelity work: seed the sidecar file directly (or via one store instance), then load/migrate through a FRESH `FileMemoryStore` so the read-time hook fires. Cover:
1. `LegacyFieldsSidecar_LoadsWithAttributesPopulated` — write a sidecar `.meta.json` by hand containing a `"fields"` key (no `"attributes"`), load via a fresh `FileMemoryStore` (list/get), assert the returned entry's `Attributes` carries the value.
2. `LegacyFieldsSidecar_RewrittenToAttributesKey_ByMigrate` — after `AttributeMigration.MigrateStore(store)`, read the raw sidecar text off disk and assert it contains `"attributes"` and not `"fields"`.
3. `MigratesTo_true_for_legacy_key_entry` — construct an entry as if just deserialized from a `"fields"` sidecar (i.e. `LegacyFields` set, `Attributes` null via the ingest init member) and assert `AttributeMigration.MigratesTo(entry)` is true; assert it is false for an already-`Attributes` entry with no legacy value.
4. `Migrate_idempotent_after_key_rewrite` — running `MigrateStore` a second time reports 0 migrated and leaves the sidecar byte-identical.
5. `ScriniaArtifactStore_reads_legacy_fields` — mirror test 1 through the MCP local store path (`ScriniaArtifactStore`) to prove both deserialization contexts coalesce.

Use the existing `TempWorkspace` helper (has a `Root` accessor) for on-disk seeding.

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter AttributesKeyMigrationTests`
Expected: FAIL — legacy sidecars load with `Attributes` null (no coalesce yet) and `migrate` does not rewrite the key.

- [ ] **Step 3: Coalesce at the `FileMemoryStore` seams**

In `LoadEntriesFromSidecars` (~813) after `var entry = JsonSerializer.Deserialize(json, FileStoreJsonContext.Default.ArtifactEntry);`, apply `entry = entry?.NormalizeAttributesKey();` before adding to the list. In `LoadEntriesFromLegacyIndex` (~830), after deserializing `IndexFile`, map `idx.Entries` (or equivalent property) through `NormalizeAttributesKey()`. Match whatever null-guarding the surrounding code already uses.

- [ ] **Step 4: Coalesce at the `ScriniaArtifactStore` seams**

Same treatment at `ScriniaArtifactStore.cs:427` and `:444`.

- [ ] **Step 5: Make `MigratesTo` flag a legacy-key rewrite**

In `AttributeMigration.MigratesTo`, add a clause returning true when `entry.LegacyFields is not null` — a not-yet-rewritten legacy sidecar. The getter is observable (Task 1), so this works on the entry as deserialized.

**Ordering subtlety — decide deliberately:** if Step 3/4's load-seam coalesce calls `NormalizeAttributesKey()` eagerly, `LegacyFields` is already cleared by the time the read-time forward-fill hook (`LoadIndexUnsafe`) runs `MigratesTo` → the rewrite would never trigger and old sidecars would linger with the `"fields"` key until an unrelated change. Resolve by NOT coalescing inside `LoadIndexUnsafe`'s own path before its `MigratesTo` check: let `LoadIndexUnsafe` observe the raw-deserialized entry (legacy holder intact), so `MigratesTo` returns true and the existing hook rewrites the sidecar (whose write goes through `Forward` → the persisted entry ends up under `"attributes"`). The seams that feed DISPLAY reads (list/get/search, and the `ScriniaArtifactStore` paths) should coalesce eagerly so callers never see a null `Attributes`. Concretely: coalesce in `LoadEntriesFromSidecars`/`LoadEntriesFromLegacyIndex` return values, but confirm `Forward` itself also coalesces (so the rewrite persists the value even if the hook path reaches `Forward` with the legacy holder still set). Tests 2/3/4 are the gate — test 2 proves the on-disk rewrite, test 4 proves idempotence (no infinite rewrite loop). If the two-audiences split proves awkward, an acceptable alternative is: coalesce everywhere eagerly AND have `Forward`/`MigratesTo` treat "a legacy sidecar was seen" via a value the coalesce records — but the simplest correct version is to make `Forward` coalesce `LegacyFields` and `MigratesTo` report it, then apply `NormalizeAttributesKey()` at the display seams only. Verify the actual `LoadIndexUnsafe` ↔ `Forward` ↔ `MigratesTo` flow in source before choosing.

- [ ] **Step 6: Run the suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Expected: PASS — all `AttributesKeyMigrationTests` green, no regressions in the existing migrate-fidelity suite (`AttributeMigrationTests`, `MigrationTests`).

- [ ] **Step 7: Commit**

```bash
git add -- src tests ':!src/Scrinia.Mcp/plugins'
git status   # confirm only in-scope files staged
git commit -m "feat(core): coalesce legacy 'fields' sidecar key into Attributes at load seams; migrate rewrites to 'attributes'"
```

---

### Task 3: Trimming-safety guard + full-solution rename sweep verification

**Files:**
- Test: `tests/Scrinia.Tests/ArtifactEntrySerializationTests.cs` (extend — Task 1 file)
- Modify: any remaining product/string references surfaced below.

**Interfaces:**
- Consumes: Tasks 1-2.
- Produces: a source-gen resolvability guard for `ArtifactEntry` so a future trimmed `serve`/CLI publish can't silently drop the type; a clean full-solution build across ALL projects.

**Context:** The `scri serve` trimmed-publish crash (side-fix `1c42d0f`) proved that source-gen metadata gaps only surface under `PublishTrimmed`, not in Debug. The `Attributes`/`LegacyFields` members are `Dictionary<string,string[]>` — already registered in `ScriniaMcpJsonContext` (the serve fix), but the store contexts (`FileStoreJsonContext`, `StoreJsonContext`) must resolve the renamed `ArtifactEntry` cleanly. A cheap unit guard catches a resolver regression without a full publish.

- [ ] **Step 1: Write the trim-faithful resolver guard test**

Add to `ArtifactEntrySerializationTests.cs` a fact asserting the source-gen context resolves the type and its dictionary member metadata:

```csharp
[Fact]
public void SourceGen_resolves_ArtifactEntry_and_attributes_dictionary()
{
    TestCtx.Default.GetTypeInfo(typeof(ArtifactEntry)).Should().NotBeNull();
    TestCtx.Default.GetTypeInfo(typeof(Dictionary<string, string[]>)).Should().NotBeNull(
        "attributes/legacy dict needs source-gen metadata for trimmed publishes");
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter ArtifactEntrySerializationTests`
Expected: PASS.

- [ ] **Step 3: Grep-sweep for any residual `Fields` references the compiler couldn't catch**

Search (case-sensitive) for lingering references to the OLD name that live in strings/comments/help text tied to the attributes dict — NOT unrelated `.Fields`:
- `grep -rn '"fields"' src/` — every remaining literal must be either the intentional `LegacyFields` ingest key (Task 1) or a genuinely unrelated use. The `--summary` view's `topFields`/`CliFieldEntry` (aggregate key tally) is a DISPLAY label, not the dict key — leave it. Confirm `BundleFormatService` and any manual JSON that names `"fields"` for the attributes dict is updated to emit/read `"attributes"` (with legacy read tolerance) or explicitly deferred with a code comment if it belongs to Merge 2/3.
- Confirm no XML-doc `<see cref="Fields"/>` or `<c>Fields</c>` referring to the renamed member remains (would not break the build, but is now wrong).

- [ ] **Step 4: Full-solution build + full test run across all in-scope projects**

Run:
```bash
dotnet build Scrinia.sln
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj
dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj
dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj
```
Expected: 0 warnings, all green. (Excluded: `tests/Scrinia.Retrieval.Benchmarks.Tests` — do not build/run.)

- [ ] **Step 5: Commit**

```bash
git add -- src tests ':!src/Scrinia.Mcp/plugins'
git status   # confirm only in-scope files staged
git commit -m "test(core): guard source-gen resolvability of renamed ArtifactEntry; finish Fields->Attributes sweep"
```

---

## Post-merge validation (user action, before Merge 2)

After this branch merges to `main`, the user rebuilds `scri` (`publish.ps1 -OutputDir c:\mcptools\scrinia -Platform win-x64`) and runs `scri migrate` on each workspace. Expected: sidecars that still carried the `"fields"` key are rewritten to `"attributes"`; a dry-run re-run reports `0` needing migration; a still-old published binary continues to read the new sidecars (unknown-key tolerance) — and the new binary reads any not-yet-rewritten `"fields"` sidecars (legacy ingest). This green, rebuildable checkpoint is the gate for Merge 2 (attributes-native write paths).

## Self-Review notes (author)

- **Coverage:** rename (Task 1) + dual-read across BOTH store contexts (Task 2) + migrate rewrite (Task 2) + trimming guard (Task 3) covers the user's decision ("migrate key to attributes; read tolerates old fields; migrate rewrites").
- **Ordering risk resolved:** Task 1 leaves the build green by renaming all references in the same task (mechanical, compiler-driven); back-compat need only hold at MERGE, so Task 1's intermediate "no coalesce yet" state is fine (nothing ships mid-plan).
- **The subtle bit** is Step 5 of Task 2 — detecting the legacy-key condition before the coalesce erases it. Flagged explicitly with tests 2/3/4 as the gate rather than prescribing a fragile mechanism.
- **EphemeralEntry** correctly gets rename-only (never persisted).
