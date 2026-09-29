# Attributes P6.6a — Description→ContentPreview Fold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `AttributeMigration.Forward` backfill `ContentPreview` from `Description` (only when `ContentPreview` is null) so that both the read-time forward-fill hook and `scri migrate` rescue the preview text of pre-`ContentPreview` sidecars **before** `Description` is removed in P6.6b.

**Architecture:** `ContentPreview` is the successor display/preview carrier for the removed `Description` field. New writes already populate `ContentPreview` directly (P6.3), but sidecars created before `ContentPreview` existed carry only `Description`. Today nothing backfills `ContentPreview` from `Description` — so removing `Description` would silently lose their preview text permanently. This plan closes that gap additively: one fold in `Forward`, one matching change-detection clause in `MigratesTo`, and tests proving both the in-memory transform and the on-disk `MigrateStore`/cold-load paths populate `ContentPreview`. **No member is removed here** — this is the safe additive unit that must ship (and the user must run `scri migrate`) before the destructive P6.6b removal.

**Tech Stack:** .NET 10, C#, xUnit + FluentAssertions. `AttributeMigration` (Scrinia.Core.Migration), `FileMemoryStore` (Scrinia.Core), source-gen STJ sidecars.

## Global Constraints

- **This is a SCORING-RELEVANT change** — backfilling `ContentPreview` gives old description-only entries a `ContentPreview` (5-pt) scoring signal they previously lacked. Do NOT tune weights. The controller runs an opus A/B ranking measurement before merge (gate: top-3 head stability), same as P6.1.2/P6.5.
- **Additive only.** Do NOT remove `Description`, `Tags`, `RetrievalAnchors`, `ReviewAfter`, `ReviewWhen`, or any member. Do NOT touch `AttributeReaders` signatures. Those are P6.6b.
- **Idempotent.** The fold must set `ContentPreview` only when it is null (`??` short-circuit), so re-running `Forward`/`MigrateStore` produces no further change.
- **TWAE** (treat warnings as errors) on product projects. `dotnet build`/`dotnet test` from the command line are the source of truth for warnings — CS8019/CS8933-class IDE-only diagnostics are stale-LSP noise, not build failures.
- Do NOT touch: `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, `.superpowers/**`.
- Commit trailer EXACT on every commit:
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```
- Never push. Local `--no-ff` merge only, on explicit user confirmation (controller step).
- Branch: `feature/attributes-desc-preview-fold` off `main` (base = current `main` HEAD).

---

## File Structure

- **Modify** `src/Scrinia.Core/Migration/AttributeMigration.cs` — add the `Description→ContentPreview` fold to `Forward`; add the `ContentPreview` clause to `MigratesTo`; extend the class XML doc.
- **Modify** `tests/Scrinia.Tests/AttributeMigrationTests.cs` — default `MakeEntry`'s `ContentPreview` non-null (isolates the new behavior); add unit tests for the fold; add a `MigrateStore` integration test.

No other product file changes. The read-time forward-fill hook (`FileMemoryStore.cs:757-765`) and `MigrateStore` both call `MigratesTo`/`Forward` and inherit the new behavior with **no edit**.

---

### Task 1: `Forward` backfills `ContentPreview`; `MigratesTo` detects it

**Files:**
- Modify: `src/Scrinia.Core/Migration/AttributeMigration.cs` (Forward ~84-88, MigratesTo ~92-97, class doc ~7-22)
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs`

**Interfaces:**
- Consumes: `ArtifactEntry` (positional record; `Description` is required positional #6, `ContentPreview` is a later positional defaulting null).
- Produces: `AttributeMigration.Forward(ArtifactEntry) -> ArtifactEntry` now also sets `ContentPreview = entry.ContentPreview ?? (non-blank Description)`. `MigratesTo(entry, out migrated)` returns true when the backfill changes `ContentPreview` (in addition to the existing Fields/Keywords checks). Both remain deterministic and idempotent.

- [ ] **Step 1: Default `MakeEntry`'s `ContentPreview` non-null (isolates the new behavior)**

The existing `MakeEntry` helper builds entries with `Description: "desc"` and a null `ContentPreview`. Once the fold lands, that combination becomes a migration (it backfills `ContentPreview`), which would flip existing no-op assertions (`MigratesTo_NoLegacyData_ReportsNoChange`, and the `Skipped`/`Migrated` counts in `MigrateStore_ForwardFillsLegacyEntries_AndIsIdempotent`). Give `MakeEntry` a non-null `ContentPreview` so the fold is a deliberate no-op for every existing caller, and let the NEW tests opt into `contentPreview: null` to exercise the backfill.

In `tests/Scrinia.Tests/AttributeMigrationTests.cs`, change the helper signature and body (currently lines ~33-41):

```csharp
    private static ArtifactEntry MakeEntry(
        string[]? tags = null, string[]? anchors = null,
        Dictionary<string, string>? codeRefs = null,
        DateTimeOffset? reviewAfter = null, string? reviewWhen = null,
        string[]? keywords = null, Dictionary<string, string[]>? fields = null,
        string? contentPreview = "preview") =>
        new(Name: "n", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, Description: "desc",
            Tags: tags, ContentPreview: contentPreview, Keywords: keywords, RetrievalAnchors: anchors,
            ReviewAfter: reviewAfter, ReviewWhen: reviewWhen, FileHashes: codeRefs, Fields: fields);
```

(Confirm `ContentPreview` is the correct named position in the `ArtifactEntry` primary ctor; it sits between `Tags` and `Keywords`. Named args make order immaterial — keep them named.)

- [ ] **Step 2: Write the failing unit tests**

Add to `tests/Scrinia.Tests/AttributeMigrationTests.cs`:

```csharp
    [Fact]
    public void Forward_BackfillsContentPreviewFromDescription_WhenPreviewNull()
    {
        var e = MakeEntry(contentPreview: null);   // Description "desc", ContentPreview null
        var m = AttributeMigration.Forward(e);
        m.ContentPreview.Should().Be("desc");       // rescued from Description
        m.Description.Should().Be("desc");          // additive: legacy field untouched
    }

    [Fact]
    public void Forward_LeavesExistingContentPreview_WhenAlreadySet()
    {
        var e = MakeEntry(contentPreview: "explicit preview");
        var m = AttributeMigration.Forward(e);
        m.ContentPreview.Should().Be("explicit preview");   // ?? short-circuits, no overwrite
    }

    [Fact]
    public void Forward_LeavesContentPreviewNull_WhenDescriptionBlank()
    {
        var e = new ArtifactEntry(Name: "n", Uri: "u", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch, Description: "   ", ContentPreview: null);
        var m = AttributeMigration.Forward(e);
        m.ContentPreview.Should().BeNull();   // blank Description is not preview-worthy
    }

    [Fact]
    public void MigratesTo_DetectsContentPreviewBackfill_AndIsIdempotent()
    {
        var e = MakeEntry(contentPreview: null);           // only the preview backfill is pending
        AttributeMigration.MigratesTo(e, out var once).Should().BeTrue();
        once.ContentPreview.Should().Be("desc");
        AttributeMigration.MigratesTo(once, out _).Should().BeFalse();   // second pass writes nothing
    }
```

- [ ] **Step 3: Run the new tests to verify they FAIL**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AttributeMigrationTests" -v minimal`
Expected: the 4 new tests FAIL (`ContentPreview` is null / `MigratesTo` returns false) — the fold does not exist yet. Existing `AttributeMigrationTests` still PASS (the `MakeEntry` default now supplies a non-null `ContentPreview`, so no existing assertion is perturbed).

- [ ] **Step 4: Add the fold to `Forward`**

In `src/Scrinia.Core/Migration/AttributeMigration.cs`, replace the tail of `Forward` (currently lines ~84-88):

```csharp
        var mergedFields = incoming.Count > 0 ? KnownFields.Merge(entry.Fields, incoming) : entry.Fields;

        string[]? mergedKeywords = entry.Keywords;
        return entry with { Fields = mergedFields, Keywords = mergedKeywords };
```

with:

```csharp
        var mergedFields = incoming.Count > 0 ? KnownFields.Merge(entry.Fields, incoming) : entry.Fields;

        string[]? mergedKeywords = entry.Keywords;

        // Description → ContentPreview: ContentPreview is the successor preview/display carrier
        // (Description is removed in P6.6b). New writes populate ContentPreview directly, but
        // sidecars created before ContentPreview existed carry only Description — backfill it here,
        // and ONLY when ContentPreview is null, so both the read-time forward-fill and `scri migrate`
        // rescue their preview text before Description is deleted. Idempotent: once set, `??`
        // short-circuits on re-run. A blank Description is not preview-worthy and stays null.
        string? mergedContentPreview = entry.ContentPreview
            ?? (string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description);

        return entry with
        {
            Fields = mergedFields,
            Keywords = mergedKeywords,
            ContentPreview = mergedContentPreview,
        };
```

- [ ] **Step 5: Add the `ContentPreview` clause to `MigratesTo`**

In the same file, `MigratesTo` (currently lines ~92-97) must recognise a preview-only backfill as a change, or `MigrateStore`/forward-fill would skip entries whose ONLY pending migration is the preview:

```csharp
    public static bool MigratesTo(ArtifactEntry entry, out ArtifactEntry migrated)
    {
        migrated = Forward(entry);
        return !FieldsSetEqual(entry.Fields, migrated.Fields)
            || !SetEqual(entry.Keywords, migrated.Keywords)
            || !string.Equals(entry.ContentPreview, migrated.ContentPreview, StringComparison.Ordinal);
    }
```

- [ ] **Step 6: Extend the class XML doc**

Update the `<summary>` on `AttributeMigration` (lines ~7-22) to record the new fold. Append a sentence before the "Deterministic and idempotent" line, e.g.:

```csharp
/// Also backfills <c>ContentPreview</c> from <c>Description</c> when <c>ContentPreview</c> is null,
/// so pre-<c>ContentPreview</c> sidecars keep their preview text once <c>Description</c> is removed.
```

- [ ] **Step 7: Run the new + full migration tests to verify PASS**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AttributeMigrationTests" -v minimal`
Expected: ALL `AttributeMigrationTests` PASS (4 new + all existing). If any pre-existing test now fails, it is because it relied on a `Description`+null-`ContentPreview` entry being a migration no-op — confirm that is the cause, fix it by giving that specific entry a non-null `ContentPreview` (or updating the expectation to reflect the legitimate backfill), and note it. Do NOT mask a failure by weakening an unrelated assertion.

- [ ] **Step 8: Build the product project (TWAE)**

Run: `dotnet build src/Scrinia.Core/Scrinia.Core.csproj -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add src/Scrinia.Core/Migration/AttributeMigration.cs tests/Scrinia.Tests/AttributeMigrationTests.cs
git commit -m "feat(core): backfill ContentPreview from Description in AttributeMigration.Forward"
```

---

### Task 2: `MigrateStore` and cold-load forward-fill rescue on-disk preview text

**Files:**
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs`

**Interfaces:**
- Consumes: `AttributeMigration.MigrateStore(IMemoryStore, dryRun, onProgress) -> MigrateSummary`; `FileMemoryStore.Upsert`/`LoadIndex` via the `TempWorkspace` helper already in the test file; the Task 1 `Forward`/`MigratesTo` backfill.
- Produces: proof that a persisted entry with `Description` and null `ContentPreview` gains `ContentPreview` after `MigrateStore`, that the count is right, and that it is idempotent — this is the on-disk guarantee the whole P6.6a→migrate→P6.6b sequence depends on.

- [ ] **Step 1: Write the failing integration test**

Add to `tests/Scrinia.Tests/AttributeMigrationTests.cs` (uses the existing `TempWorkspace` + `MakeEntry`):

```csharp
    [Fact]
    public void MigrateStore_BackfillsContentPreviewFromDescription_ForPreviewOnlyEntry()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.NewStore();
        // A pre-ContentPreview sidecar: Description present, ContentPreview null, no other legacy data.
        store.Upsert(MakeEntry(contentPreview: null) with { Name = "old" }, "local");
        // A control that already has ContentPreview and no legacy fields -> must be skipped.
        store.Upsert(MakeEntry(contentPreview: "already", fields: new() { ["topic"] = ["auth"] }) with { Name = "ok" }, "local");

        var first = AttributeMigration.MigrateStore(store);
        first.Total.Should().Be(2);
        first.Migrated.Should().Be(1);   // only "old" (preview backfill) migrates
        first.Skipped.Should().Be(1);    // "ok" already has ContentPreview and attributes

        store.LoadIndex("local").First(e => e.Name == "old").ContentPreview.Should().Be("desc");

        // idempotent: re-running writes nothing
        var second = AttributeMigration.MigrateStore(store);
        second.Migrated.Should().Be(0);
        second.Skipped.Should().Be(2);
    }
```

- [ ] **Step 2: Run it to verify PASS**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MigrateStore_BackfillsContentPreview" -v minimal`
Expected: PASS (Task 1 already implemented the fold). This test is red-verifiable by temporarily reverting the Task 1 `MigratesTo` clause (the entry would report `Skipped`, `ContentPreview` null) — confirm non-vacuity if the reviewer asks, then restore.

- [ ] **Step 3: Run the full Scrinia.Tests suite (regression + ripple check)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -v minimal`
Expected: green. The backfill also flows through the read-time forward-fill hook (`FileMemoryStore.cs:757-765`, which calls `MigratesTo`), so any test that cold-loads an old-style entry may now see `ContentPreview` populated. If a test outside `AttributeMigrationTests` flips, verify it is the legitimate backfill (a `Description`+null-`ContentPreview` entry now migrating) and fix the expectation honestly; report each such flip. Do NOT weaken assertions to hide a real regression.

- [ ] **Step 4: Commit**

```bash
git add tests/Scrinia.Tests/AttributeMigrationTests.cs
git commit -m "test(core): MigrateStore backfills ContentPreview for preview-only sidecars"
```

---

## Measurement gate (controller step — before merge, do NOT skip)

The fold gives pre-`ContentPreview` entries a 5-pt `ContentPreview` scoring signal they previously lacked. Per the established cadence (P6.1.2, P6.5) and the "measure, don't theorize" constraint, run an **opus A/B ranking measurement** before proposing merge:

- Build a small corpus (~20-30 docs) including several **description-only, null-`ContentPreview`** entries whose description text overlaps a query term, plus controls (entries with existing `ContentPreview`, field-signal entries, BM25 hubs).
- A/B: score each query through the real scorer **before** the fold (preview null → 0 preview pts) vs **after** (preview = description → up to 5 pts on preview-term matches).
- **Gate:** top-3 head stability. The expected effect is a bounded uplift for description-only entries on preview-term queries; confirm no genuine field/BM25 head result is displaced and no unintended reordering elsewhere.
- Write the report to `scratchpad/p66a-preview-fold-ranking-measurement.md` and present the verdict. Merge only on user confirmation (never revert/merge decisions without explicit go-ahead).

## After merge — the manual `scri migrate` gate (user action)

P6.6a shipping does NOT complete the prerequisite. The user must then run `scri migrate` on every live workspace so on-disk sidecars gain `ContentPreview` (and the other `Fields`) **before** P6.6b removes the members. Confirm an idempotent re-run reports `Migrated: 0`. Only after that does P6.6b (the destructive removal) get planned/executed.

---

## Self-Review

- **Spec coverage:** Closes the §8 "Description has no forward-fill target" data-loss gap identified in the P6.6 removal inventory; honors design decision 3 (ContentPreview is Description's successor carrier) by making it true for legacy entries too. ✅
- **Placeholder scan:** every code step shows complete code; no TODO/TBD. ✅
- **Type consistency:** `Forward`/`MigratesTo` signatures unchanged (same params/returns); `ContentPreview` is `string?`; `MakeEntry` gains a defaulted `string? contentPreview = "preview"`; `MigrateStore` summary fields (`Total`/`Migrated`/`Skipped`) match the existing record. ✅
- **Additive guarantee:** no member removed, no `AttributeReaders` change, no weight change. ✅
