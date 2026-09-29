# Attributes Model — Plan 6.1: Migration completeness (key:value keywords → Fields + read-time forward-fill) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the two remaining gaps that must exist *before* any legacy read/scoring path can be retired, both **additively** (no removals, no observable behavior change):
1. Teach `AttributeMigration.Forward` to route **recognized-key `key:value`-shaped legacy keywords** (`person:maria`, `ref:x`, …) into `Fields`, so the scorer's `key:value`-in-`Keywords` dual-read block (`WeightedFieldScorer.cs:417-435`) has an attributes backing and can be deleted in P6.5 without regressing pre-unification entries.
2. Hook `AttributeMigration.Forward` into the store's load seam (`FileMemoryStore.LoadIndexUnsafe`) so **any un-migrated sidecar upgrades transparently on read** — readers see the attributes shape without a prior `scri migrate`, and the upgrade is persisted on first load (mirroring the existing `index.json`→sidecar auto-upgrade) so STJ's silent unmapped-member drop can never lose legacy data once P6.6 removes the members. No user action required.

**Architecture:** Both changes ride the existing pure `AttributeMigration.Forward` transform. Task 1 extends `Forward` (still pure, deterministic, idempotent, additive) to additionally parse `key:value` keywords whose key is a recognized attribute key (`KnownFields.RecommendedKeys`) and merge them into `Fields` via `KnownFields.Merge`, leaving the keyword in `Keywords` (additive). Task 2 wires `Forward` (via `MigratesTo`) into `FileMemoryStore.LoadIndexUnsafe`: every loaded entry is forward-filled in memory so all readers see `Fields`; entries loaded from the **primary scope sidecars** whose shape actually changes are written back once (durable upgrade), following the precedent already in that method for the legacy `index.json`→sidecar upgrade.

**Tech Stack:** C# / .NET 10, System.Text.Json source-gen sidecars, xUnit + FluentAssertions.

## Global Constraints

- **Additive only — no removals, no behavior change.** `Forward` still leaves every legacy field (`Tags`/`RetrievalAnchors`/`CodeRefs`/`ReviewAfter`/`ReviewWhen`/`Description`) and every legacy keyword populated; it only *adds* to `Fields`. The scorer's legacy blocks (tags/anchors/`key:value`-keywords/description) stay in place — deleting them is P6.5. Because scoring uses `Math.Max` across the field block and the `key:value`-keyword block, an entry that now has both `Fields["person"]=["maria"]` **and** the `person:maria` keyword scores identically to today. **Search results and ranking must not change.** This plan enables P6.5; it does not perform it.
- **Routing scope = recognized attribute keys only.** Route a `key:value` keyword into `Fields` **iff** its key ∈ `KnownFields.RecommendedKeys` (`person`/`place`/`org`/`thing`/`topic`/`file`/`ref`/`review`). This captures the real entity/reference keywords (entity `type:value` from `EntityGraph.cs:163`; `ref:x` — already write-routed to `Fields["ref"]` at `MemoryTools.Core.cs:1426`, so old keyword-shaped ones get consolidated the same way) and **excludes provenance/non-attribute colon keywords** such as `basedOn:…` (`MemoryTools.Skills.cs:285`) and accidental colons (URLs, `12:30`), which stay plain keywords. Rationale recorded in `Forward`'s doc-comment: recommended keys are real attributes; provenance keywords are not searchable facets and must not pollute the attribute namespace (they surface in P6.2 displays/filters).
- **Idempotent + non-destructive.** `MigratesTo` (set-equality on `Fields`/`Keywords`) still gates every write: re-running `Forward`, re-loading a store, or re-running `scri migrate` produces zero additional writes once an entry is upgraded (SSD-wear + clean-diff discipline). The read-time write-back must write each upgraded sidecar **at most once** (guarded by `MigratesTo`), never on every read.
- **Read-time persistence follows existing precedent, primary scope only.** Persist the forward-fill by writing back only entries loaded from the **primary `storeDir` sidecars** (mirroring the `index.json`→sidecar `WriteSidecars` call already at `FileMemoryStore.cs:726-732`). Entries pulled in via the legacy v1-layout **fallback merge** (`ResolveLegacyFallbackDir`, `FileMemoryStore.cs:734-752`) are forward-filled **in memory only** — persisting them into `storeDir` would silently promote their scope. They persist naturally when next written or via `scri migrate`.
- **Use the instance store.** The read-time hook lives on `FileMemoryStore` (the v1+v2 instance store, `MemoryStoreContext.Current`), not the static `ScriniaArtifactStore` facade.
- **Do NOT touch** bench WIP (`benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`), `src/Scrinia.Mcp/plugins/opencode/*`, or `.superpowers/**`.
- Commit trailer, exactly:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
  then `Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g`
- Never push. `TreatWarningsAsErrors` is on for `Scrinia.Core`/`Scrinia` — build clean. Branch off `main` (`76cae2c`) before Task 1.

## File Structure

- `src/Scrinia.Core/Migration/AttributeMigration.cs` — MODIFY: extend `Forward` to route recognized-key `key:value` keywords into `Fields` (Task 1).
- `src/Scrinia.Core/FileMemoryStore.cs` — MODIFY: forward-fill (+ persist primary-scope) inside `LoadIndexUnsafe` (Task 2).
- `tests/Scrinia.Tests/AttributeMigrationTests.cs` — MODIFY: add `Forward` key:value-routing unit tests (Task 1); update any existing assertion whose `Forward` output legitimately expands.
- `tests/Scrinia.Tests/FileMemoryStoreTests.cs` — MODIFY: add read-time forward-fill + persistence integration tests (Task 2).

Read `src/Scrinia.Core/Migration/AttributeMigration.cs` (current `Forward`, `MigratesTo`), `src/Scrinia.Core/Search/KnownFields.cs` (`RecommendedKeys`, `IsMultiValued`, `Merge`, `ReviewKey`), `src/Scrinia.Core/Search/WeightedFieldScorer.cs:417-435` (the `key:value`-keyword parse this mirrors — same `sep>0 && sep<len-1` predicate), and `src/Scrinia.Core/FileMemoryStore.cs:717-758` (`LoadIndexUnsafe`, incl. the `index.json`→sidecar write-back precedent at 726-732 and the legacy-merge at 734-752) before implementing.

---

### Task 1: Route recognized-key `key:value` keywords into `Fields` in `AttributeMigration.Forward`

Extend the pure transform so a `key:value`-shaped legacy keyword whose key is a recognized attribute key is additionally merged into `Fields[key]`, leaving the keyword in place. Pure, deterministic, idempotent, additive.

**Files:**
- Modify: `src/Scrinia.Core/Migration/AttributeMigration.cs`
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs`

**Interfaces:**
- Consumes: `KnownFields.RecommendedKeys`, `KnownFields.Merge`, `KnownFields.ReviewKey` (`src/Scrinia.Core/Search/KnownFields.cs`); `ArtifactEntry.Keywords`.
- Produces: `AttributeMigration.Forward(ArtifactEntry)` — unchanged signature and existing behavior (tags→`tag`, codeRefs→`file`, review→`review`, anchors→keywords), **plus**: for each `kw` in `Keywords` with a colon at `sep` where `sep>0 && sep<kw.Length-1`, if `KnownFields.RecommendedKeys.Contains(kw[..sep])`, collect `kw[(sep+1)..]` under that key; the collected `key→values[]` are merged into `Fields` via `KnownFields.Merge` (so multi-valued keys union-dedupe, scalar keys newer-win) alongside the existing tag/file/review incoming map. Keywords are **not** modified. `MigratesTo` continues to gate writes unchanged.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Scrinia.Tests/AttributeMigrationTests.cs` (reuse its existing `MakeEntry` helper; extend it with a `keywords:`/`fields:` arg if not already present — it is, per current file):

```csharp
[Fact]
public void Forward_RoutesRecognizedKeyValueKeywords_IntoFields_KeepingKeyword()
{
    var e = MakeEntry(keywords: ["person:maria", "topic:auth", "loose-keyword"]);
    var m = AttributeMigration.Forward(e);
    m.Fields!["person"].Should().BeEquivalentTo("maria");
    m.Fields!["topic"].Should().BeEquivalentTo("auth");
    // additive: keywords are untouched (still available to the legacy scorer block + graph)
    m.Keywords.Should().Contain(new[] { "person:maria", "topic:auth", "loose-keyword" });
}

[Fact]
public void Forward_UnionsKeywordEntities_IntoExistingMultiValuedFields()
{
    var e = MakeEntry(keywords: ["person:bob"], fields: new() { ["person"] = ["alice"] });
    var m = AttributeMigration.Forward(e);
    m.Fields!["person"].Should().BeEquivalentTo("alice", "bob");   // union-dedupe, existing order kept
}

[Fact]
public void Forward_IgnoresUnrecognizedAndMalformedColonKeywords()
{
    var e = MakeEntry(keywords: ["basedOn:skill-x", "http://example.com", "trailingcolon:", ":leading", "12:30"]);
    var m = AttributeMigration.Forward(e);
    // none of these keys are recognized attributes (or the colon is edge-positioned) -> no Fields created
    (m.Fields is null || m.Fields.Count == 0).Should().BeTrue();
    m.Keywords.Should().Contain("basedOn:skill-x");   // provenance stays a plain keyword
}

[Fact]
public void Forward_KeyValueRouting_IsIdempotent()
{
    var e = MakeEntry(keywords: ["ref:other-note"]);
    AttributeMigration.MigratesTo(e, out var once).Should().BeTrue();
    once.Fields!["ref"].Should().BeEquivalentTo("other-note");
    AttributeMigration.MigratesTo(once, out _).Should().BeFalse();   // second pass: no change
}
```

Note the recognized-key predicate: `person`/`place`/`org`/`thing`/`topic`/`file`/`ref`/`review`. `basedOn` is intentionally NOT recognized. Malformed colons (leading `:`, trailing `:`, i.e. `sep<=0 || sep>=len-1`) are skipped, matching the scorer's own guard at `WeightedFieldScorer.cs:422`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~AttributeMigrationTests`
Expected: the four new tests FAIL (routing not implemented); existing `AttributeMigrationTests` still pass.

- [ ] **Step 3: Implement the routing in `Forward`**

In `AttributeMigration.Forward`, before the `KnownFields.Merge` call, fold recognized-key `key:value` keywords into the `incoming` map. Sketch (adapt to the existing method body — the existing tag/file/review population and the `incoming` dictionary already exist):

```csharp
// Route recognized-key "key:value" legacy keywords into Fields (additive; keyword is kept).
// Mirrors the scorer's key:value parse (WeightedFieldScorer.cs:417-435) so P6.5 can delete that
// dual-read block without regressing pre-unification entries. Only RecommendedKeys are real
// attributes; provenance/malformed colon keywords (basedOn:, URLs, 12:30) stay plain keywords.
if (entry.Keywords is { Length: > 0 })
{
    Dictionary<string, List<string>>? kwFields = null;
    foreach (string kw in entry.Keywords)
    {
        int sep = kw.IndexOf(':');
        if (sep <= 0 || sep >= kw.Length - 1) continue;
        string key = kw[..sep];
        if (!KnownFields.RecommendedKeys.Contains(key)) continue;
        (kwFields ??= new(StringComparer.OrdinalIgnoreCase))
            .TryGetValue(key, out var list);
        if (list is null) kwFields[key] = list = new List<string>();
        string value = kw[(sep + 1)..];
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
    }
    if (kwFields is not null)
        foreach (var (k, v) in kwFields)
            incoming[k] = incoming.TryGetValue(k, out var existing)
                ? existing.Union(v, StringComparer.OrdinalIgnoreCase).ToArray()
                : v.ToArray();
}
```

Keep the existing `KnownFields.Merge(entry.Fields, incoming)` call as the single merge point — do not merge twice. Update the class/`Forward` doc-comment to state the new routing and the RecommendedKeys-only rule + why. **Do not** remove the keyword from `Keywords` (additive).

- [ ] **Step 4: Run to verify green**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~AttributeMigrationTests` → all green (new + existing). If any pre-existing `AttributeMigration` test asserted a `Forward` output that now legitimately includes newly-routed `Fields` (e.g. a fixture that happened to carry a `person:` keyword), update that expectation and note it in the report — the transform's contract has expanded by design. Full `Scrinia.Tests` suite green (the scorer/store tests must not move — `Math.Max` parity).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Migration/AttributeMigration.cs tests/Scrinia.Tests/AttributeMigrationTests.cs
git commit -F- <<'EOF'
feat(core): route recognized-key key:value keywords into Fields in Forward

AttributeMigration.Forward now folds `person:maria`/`ref:x`-shaped legacy keywords
(key in KnownFields.RecommendedKeys) into Fields via KnownFields.Merge, mirroring the
scorer's key:value dual-read parse so that block can be deleted in P6.5 without
regressing pre-unification entries. Additive: the keyword is kept; provenance/malformed
colon keywords (basedOn:, URLs, 12:30) stay plain keywords. Idempotent via MigratesTo.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 2: Read-time auto forward-fill in `FileMemoryStore.LoadIndexUnsafe`

Every entry returned from the load seam is forward-filled in memory (readers see `Fields` without a prior `scri migrate`); entries loaded from the primary scope sidecars whose shape actually changes are written back once, so the upgrade is durable before P6.6 removes the legacy members.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (`LoadIndexUnsafe`)
- Test: `tests/Scrinia.Tests/FileMemoryStoreTests.cs`

**Interfaces:**
- Consumes: `AttributeMigration.MigratesTo` / `AttributeMigration.Forward` (Task 1 semantics), the existing private `WriteSidecar(ArtifactEntry, string storeDir)` (`FileMemoryStore.cs:802`), `LoadEntriesFromSidecars` (`:760`).
- Produces: `LoadIndexUnsafe(scope)` returns forward-filled entries; the in-memory `_indexCache` holds the forward-filled form; primary-scope sidecars whose entry `MigratesTo` a new shape are rewritten once with the upgraded `Fields`. Legacy-merged entries (`ResolveLegacyFallbackDir`) are forward-filled in memory but NOT written back (no scope promotion). Behavior contract for callers (scorer/list/drift/search) is unchanged today because scoring is `Math.Max` over legacy + attribute reads.

- [ ] **Step 1: Write the failing tests**

Read `tests/Scrinia.Tests/FileMemoryStoreTests.cs` first for its temp-workspace construction and raw `.meta.json` seeding pattern (~L478-497). Add tests that seed a **legacy-shaped** sidecar (via raw `.meta.json` with `tags`/`retrievalAnchors`/a `person:` keyword and NO `fields`) so the forward-fill has something to do — do not seed via `Upsert` alone if that would pre-populate `Fields`. Then:

```csharp
[Fact]
public void LoadIndex_ForwardFillsLegacySidecar_InMemory()
{
    using var tmp = new TempWorkspace();                 // mirror this file's existing helper
    var store = tmp.NewStore();
    // seed a legacy-shaped sidecar directly on disk (tags + person: keyword, no fields)
    tmp.WriteRawSidecar("local", "note", /* legacy json */ ...);   // mirror existing raw-seed helper

    var loaded = store.LoadIndex("local").Single(e => e.Name == "note");
    loaded.Fields!["tag"].Should().Contain("security");
    loaded.Fields!["person"].Should().Contain("maria");
    loaded.Tags.Should().Contain("security");            // additive: legacy field still present
}

[Fact]
public void LoadIndex_PersistsForwardFill_ToPrimarySidecar_Once()
{
    using var tmp = new TempWorkspace();
    var store = tmp.NewStore();
    tmp.WriteRawSidecar("local", "note", /* legacy json, no fields */ ...);

    store.LoadIndex("local");                            // first load upgrades + writes back
    tmp.InvalidateCache(store, "local");                 // force a fresh disk read (mirror how the file drops _indexCache)

    // the on-disk sidecar now carries fields
    var reread = store.LoadIndex("local").Single(e => e.Name == "note");
    reread.Fields!["tag"].Should().Contain("security");

    // idempotent: capturing the sidecar's last-write time, a second load does NOT rewrite it
    var mtimeBefore = tmp.SidecarWriteTime("local", "note");
    tmp.InvalidateCache(store, "local");
    store.LoadIndex("local");
    tmp.SidecarWriteTime("local", "note").Should().Be(mtimeBefore);   // no second write
}
```

If the test file has no `WriteRawSidecar`/`InvalidateCache`/`SidecarWriteTime` helpers, replicate the raw-seed + cache-drop patterns it already uses inline (read the file; do not invent a broad abstraction). If forcing a fresh disk read is impractical because `_indexCache` isn't test-reachable, construct a **second** `FileMemoryStore` over the same temp root for the re-read (a fresh instance has a cold cache) and assert the on-disk upgrade that way; drop the mtime sub-assertion if no write-time helper is feasible and instead assert the second store reads `Fields` from disk (proving persistence) — record whichever approach you took in the report. Do NOT weaken the "no second write" intent silently.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~FileMemoryStoreTests`
Expected: the new tests FAIL (load returns `Fields == null` for legacy sidecars; no write-back).

- [ ] **Step 3: Implement the hook in `LoadIndexUnsafe`**

Forward-fill after entries are assembled, before caching. Persist only the primary-`storeDir` entries. Sketch (integrate into the existing method at `FileMemoryStore.cs:717-758` — note the primary load is `LoadEntriesFromSidecars(storeDir)` at :724 and the legacy `index.json` upgrade already writes back at :726-732):

```csharp
// Read-time forward-fill: upgrade legacy-shaped entries to the attributes model on load so all
// readers see Fields without a prior `scri migrate`, and persist the upgrade for primary-scope
// sidecars (one-time, guarded by MigratesTo) so STJ's unmapped-member drop can't lose legacy
// data once P6.6 removes the members. Mirrors the index.json->sidecar write-back above.
for (int i = 0; i < entries.Count; i++)
{
    if (AttributeMigration.MigratesTo(entries[i], out var upgraded))
    {
        bool fromPrimary = /* entry i was loaded from storeDir sidecars, not the legacy-merge */;
        entries[i] = upgraded;
        if (fromPrimary) WriteSidecar(upgraded, storeDir);   // durable upgrade, primary scope only
    }
}
```

Determine `fromPrimary` cleanly: apply the forward-fill+write-back to the primary `entries` list **immediately after `LoadEntriesFromSidecars(storeDir)` (/legacy `index.json` fallback) and BEFORE the legacy-merge block appends fallback entries** — then every entry processed in that first pass is primary (write-back), and legacy-merged entries appended afterward are forward-filled in memory only (a second, no-write pass, or fold their forward-fill into the merge loop). Structure it so the write-back set is unambiguous; add a comment. Keep all of this inside the existing lock scope (the method already writes sidecars under the same context at :726-732). Cache the forward-filled `entries` (the existing `_indexCache[scope] = new CachedIndex(entries)` at :754 then holds the upgraded form).

- [ ] **Step 4: Run to verify green + no regressions**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~FileMemoryStoreTests` → green. Then the **full** `Scrinia.Tests` suite: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` → green. Watch specifically for search/scorer/list tests that load an entry then assert `Fields is null` — such an assertion is now legitimately false (the load forward-fills); update it to reflect the upgraded shape and confirm the *score/ranking* it checks is unchanged (`Math.Max` parity). Record any such updates in the report. `dotnet build` across `Scrinia.Core`/`Scrinia` clean (TreatWarningsAsErrors).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/FileMemoryStoreTests.cs
git commit -F- <<'EOF'
feat(core): read-time auto forward-fill in FileMemoryStore.LoadIndexUnsafe

Loaded entries are forward-filled to the attributes model in memory so all readers see
Fields without a prior `scri migrate`; primary-scope sidecars whose shape changes are
written back once (guarded by MigratesTo), mirroring the existing index.json->sidecar
upgrade, so STJ's unmapped-member drop can't lose legacy data once P6.6 removes the
members. Legacy-merge fallback entries are upgraded in memory only (no scope promotion).
Additive: scoring is unchanged (Math.Max over legacy + attribute reads).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

## Verification

1. `dotnet build` clean across `Scrinia.Core`/`Scrinia` (TreatWarningsAsErrors on).
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` fully green. **No search/scorer/ranking test changes its asserted score** — this plan is additive and scoring is `Math.Max` over legacy + attribute reads. Test edits are limited to assertions about *entry shape* (`Fields` now populated on load / after `Forward`), never about ranking.
3. `Forward` routing: recognized-key `key:value` keywords (`person:`/`place:`/`org:`/`thing:`/`topic:`/`file:`/`ref:`/`review:`) land in `Fields`; `basedOn:` and malformed colons do not; keywords are retained; idempotent.
4. Read-time hook: a legacy-shaped sidecar (tags/anchors/`person:` keyword, no `fields`) loads with `Fields` populated; the primary sidecar is rewritten once with `Fields` (durable) and not again on re-load; legacy-merge entries are upgraded in memory only.
5. Idempotency end-to-end: after a first `LoadIndex`, `scri migrate --json` on the same store reports `Migrated: 0` for the now-upgraded entries (the read already persisted them).

## Self-review (done during authoring)

- **Spec coverage:** implements P6.1 of the locked cleanup sub-sequence — the two additive prerequisites (key:value-keyword routing for the P6.5 scorer-block deletion; read-time forward-fill for the P6.2 reader cutover and P6.6 member removal). No removals here.
- **Sequencing discipline:** strictly additive. Legacy fields and legacy scorer blocks remain; `Math.Max` guarantees identical ranking. This is what makes P6.5 (delete scorer blocks) and P6.6 (remove members) safe later.
- **Routing-scope decision (recorded for the merge review):** recognized attribute keys only (`RecommendedKeys`), NOT every colon keyword. Evidence: real key:value keywords are entity `type:value` (`EntityGraph.cs:163`) and `ref:` (already write-routed, `MemoryTools.Core.cs:1426`); `basedOn:` (`MemoryTools.Skills.cs:285`) is provenance, not a searchable facet. Consequence at P6.5: non-recognized colon keywords lose their (incidental) split-scoring when the block is deleted; this is correct — provenance/time/URL colons were never intended as `key:value` facets. Alternative (route every colon keyword for mechanical parity) rejected: pollutes the attribute namespace surfaced in P6.2 displays/filters. Flagged so the user can veto at merge.
- **Data-safety (decision #2, faithful):** the read-time hook persists the upgrade for primary-scope sidecars on first load, so a store that never runs `scri migrate` is still durably upgraded before P6.6 removes the members — no user action required. Legacy-merge entries are the one exception (in-memory only, to avoid scope promotion); they persist on next write or via `scri migrate`. This is the honest boundary and is called out in-code.
- **SSD-wear discipline:** write-back is one-time per entry (guarded by `MigratesTo`), never per-read; the cache holds the upgraded form. No read churn.
- **Type consistency:** `Forward`'s signature is unchanged; `MigratesTo(entry, out upgraded)` is the same write-or-skip gate reused by both the store driver (Plan 5) and the new load hook.
- **Risk flagged for the runbook:** if `scri serve` is running, its in-process cache won't see another process's read-time write-backs until invalidation — same caveat as `scri migrate` (Plan 5). No new risk.
