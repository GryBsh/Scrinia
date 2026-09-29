# Attributes P6.3 — Writer Cutover (dual-write) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the write paths populate the attributes carriers (`Fields`) at write time — so every newly-written entry (persistent AND ephemeral) is born `Fields`-complete rather than depending on the read-time forward-fill — while KEEPING the legacy fields written (dual-write), so scoring stays byte-identical this phase.

**Architecture:** `AttributeMigration.Forward` is already the additive, idempotent, tested transform that folds legacy fields → `Fields` (keeping legacy). Applying it at each **persistent** write/mutate site is therefore exactly "dual-write": the entry keeps its legacy fields AND gains `Fields["tag"|"review"|"anchor"|"file"]`. **Ephemeral** entries aren't `ArtifactEntry`, never hit the read-time forward-fill, and their `ListEphemeral` projections currently drop `Fields` — so they get a small inline tag/anchor→`Fields` routing plus a projection/copy `Fields`-carry, making them P6.6-ready. No legacy field stops being written; no scorer change.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions.

## Global Constraints

- **DUAL-WRITE — keep writing the legacy fields.** Do NOT remove or stop populating `Description`/`Tags`/`ReviewAfter`/`ReviewWhen`/`RetrievalAnchors`/`CodeRefs` at any write site. This phase only ADDS write-time `Fields` population. Removal is P6.6.
- **Scoring-neutral.** Because the scorer `Math.Max`-combines legacy + `Fields` branches and legacy stays populated, scoring is unchanged this phase (verified: legacy tag-exact 50 ≥ Fields 40, so `Math.Max` = 50). No scorer file is touched (that is P6.5). No search/scorer/ranking test may change its asserted score.
- **Reuse `AttributeMigration.Forward` for persistent `ArtifactEntry` sites** — do not re-implement the legacy→Fields routing inline for persistent entries. It is idempotent (safe to also run at read time) and additive.
- **`description` input unchanged this phase.** Keep the current `description` param → legacy `Description` behavior; `ContentPreview` stays content-derived. Description's removal as an input is P6.6.
- **`CodeRefs` hash map unchanged.** `Forward` already routes `CodeRefs.Keys` (paths) → `Fields["file"]`; the hash map stays in `CodeRefs`. The rename to `FileHashes` is P6.4 — do not touch it here.
- **No model change** (`ArtifactEntry`/`EphemeralEntry` records unchanged), **no reader change** (P6.2 done), **no wire-DTO shape change**.
- **Branch** off `main` @ current HEAD, named `feature/attributes-writer-cutover`. **Never push.**
- **Product projects build with `TreatWarningsAsErrors`** — warning-clean. Do NOT touch `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, `.superpowers/**`.
- **Commit trailer, exactly** (every commit):
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```

### Note on `Keywords` (expected, benign)
`Forward` folds `RetrievalAnchors` into `Keywords` (anchors→keywords). At the persistent write sites this now happens at WRITE time (previously only at read). This is consistent with the merged read behavior. If an existing write-path test asserts `Keywords` WITHOUT the anchors, update it to reflect the folded set — legitimately (the folded set is the intended state; forward-fill already produced it on read). Flag any such update in your report.

---

## File Structure
- **Modify** `src/Scrinia.Mcp/MemoryTools.Core.cs` — Store/UpdateMeta/Append persistent (Task 1) + ephemeral (Task 2) sites.
- **Modify** `src/Scrinia.Server/Services/MemoryOrchestrator.cs` — Store/Append persistent + ephemeral (Task 3).
- **Modify** `src/Scrinia.Mcp/ScriniaArtifactStore.cs`, `src/Scrinia/HttpMemoryStore.cs`, `src/Scrinia.Core/FileMemoryStore.cs` — `ListEphemeral` projections + `CopyMemory` explicit-ctor paths (Task 4).
- **Tests** across `tests/Scrinia.Tests/` and `tests/Scrinia.Server.Tests/`.

Line references below are from the P6.3 write-path research (`scratchpad/p63-writer-inventory.md`); confirm against the file — Task 1's edits may shift later line numbers slightly.

---

## Task 1: Persistent write/mutate sites (MCP) — apply `Forward` at write

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — `Store` persistent `new ArtifactEntry` (~@361), `UpdateMeta` `entry with {…}` (~@513), `Append` persistent `new ArtifactEntry` (~@1240)
- Test: `tests/Scrinia.Tests/` (new `WriterCutoverTests.cs` or extend an existing MCP store test)

**Interfaces:**
- Consumes: `AttributeMigration.Forward(ArtifactEntry) → ArtifactEntry` (additive, idempotent; folds `Tags`→`Fields["tag"]`, `CodeRefs.Keys`→`Fields["file"]`, `ReviewAfter`/`ReviewWhen`→`Fields["review"]`, `RetrievalAnchors`→`Fields["anchor"]`+`Keywords`, keeping all legacy fields).

**Approach:** at each of the three sites, after the `ArtifactEntry` is fully constructed (legacy fields set, and the existing write-time `Fields` already assembled from `RouteKeywords`/`Extract`/refs/`attributes`), pass it through `Forward` immediately before it is persisted/returned:
```csharp
var entry = new ArtifactEntry(/* … existing construction, unchanged … */);
entry = AttributeMigration.Forward(entry);   // dual-write: adds Fields["tag"|"review"|"anchor"|"file"] from the legacy fields, keeps legacy
// … existing persist call, unchanged …
```
For `UpdateMeta`'s `entry with {…}`: apply `Forward` to the result of the `with` expression:
```csharp
var updated = entry with { /* … existing … */ };
updated = AttributeMigration.Forward(updated);
```
Add `using Scrinia.Core.Migration;` if not already present. Do NOT change any legacy-field assignment, the `Fields` assembly, `Keywords`, or `ContentPreview` construction — `Forward` layers on top additively.

- [ ] **Step 1: Write the failing test**

New `tests/Scrinia.Tests/WriterCutoverTests.cs`. Using the same MCP store/tool harness as `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` (grep it for the `Store` tool invocation + how it reads back a persisted entry), store a persistent memory with `tags`, `reviewAfter`, `anchors`, and a `codeRefs` path, then read the persisted `ArtifactEntry` back and assert BOTH:
```csharp
// dual-write: Fields populated AT WRITE (read back the just-stored entry, no reliance on a load-time upgrade)
persisted.Fields!["tag"].Should().Contain("<the tag>");
persisted.Fields!["review"].Should().NotBeEmpty();
persisted.Fields!["anchor"].Should().Contain("<the anchor>");
persisted.Fields!["file"].Should().Contain("<the path>");
// legacy STILL written (dual-write)
persisted.Tags.Should().Contain("<the tag>");
persisted.ReviewAfter.Should().NotBeNull();     // or ReviewWhen, per what you set
persisted.RetrievalAnchors.Should().Contain("<the anchor>");
persisted.CodeRefs.Should().ContainKey("<the path>");
```
If the harness reads back through a path that itself applies read-time `Forward`, defeat that by asserting on the SIDECAR/store's stored entry directly, or by constructing the entry via the same code path and asserting pre-persist — the point is to prove `Forward` runs at WRITE. If isolating write-time is impractical through the tool, test the smallest write helper directly (make it `internal` if needed; `Scrinia.Mcp` already has `InternalsVisibleTo("Scrinia.Tests")`).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriterCutover"`
Expected: FAIL — `Fields` lacks the param-derived keys at write time (only keyword/ref/attribute-derived keys present).

- [ ] **Step 3: Apply `Forward` at the three sites** (per Approach above).

- [ ] **Step 4: Run to verify it passes** — same filter. Expected: PASS.

- [ ] **Step 5: Full suite regression** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`. Expected: green. If a write-path test asserted `Keywords` without folded anchors, update it legitimately (see the Global Constraints note) and flag it.

- [ ] **Step 6: Build warning-clean** — `dotnet build src/Scrinia.Mcp/Scrinia.Mcp.csproj` → 0 warnings.

- [ ] **Step 7: Commit** — `"feat(mcp): populate Fields at persistent write sites via Forward (dual-write)"`

---

## Task 2: Ephemeral write sites (MCP) — route tag/anchor → `EphemeralEntry.Fields`

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — `Store` ephemeral `new EphemeralEntry` (~@218), `Append` ephemeral (via `BuildAppendMetadata`, ~@1193/1278)
- Test: `tests/Scrinia.Tests/WriterCutoverTests.cs`

**Interfaces:**
- Consumes: `KnownFields.Merge`, `KnownFields.TagKey` (`"tag"`), `KnownFields.AnchorKey` (`"anchor"`).

**Approach:** `EphemeralEntry` carries only `Tags` and `RetrievalAnchors` legacy attribute-fields (no `ReviewAfter`/`ReviewWhen`/`CodeRefs`). It already has a `Fields` member set from keywords/attributes. Route the tag/anchor params into it, mirroring `Forward` for that subset, additively (keep `Tags`/`RetrievalAnchors`):
```csharp
// build the legacy-derived increment, then union into the entry's existing Fields
var ephIncoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
if (tags is { Length: > 0 }) ephIncoming[KnownFields.TagKey] = tags;
var cleanAnchors = (cleanedAnchors ?? [])
    .Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
if (cleanAnchors.Length > 0) ephIncoming[KnownFields.AnchorKey] = cleanAnchors;
var ephFields = ephIncoming.Count > 0 ? KnownFields.Merge(existingFields, ephIncoming) : existingFields;
```
Apply this so the constructed `EphemeralEntry.Fields` includes the tag/anchor keys (use the site's actual variable names for `tags`/`cleanedAnchors`/the pre-assembled `Fields`). For the `Append` ephemeral path, do the same inside/after `BuildAppendMetadata` so `meta.Fields` carries them. Keep `Tags`/`RetrievalAnchors` assignments unchanged (dual-write). Also fold anchors into `Keywords` if the persistent path does (match behavior — check whether the ephemeral path already folds; if not and the persistent Task-1 `Forward` does, add the fold here for parity).

- [ ] **Step 1: Write the failing test** — store an EPHEMERAL memory (name/API for ephemeral in the harness) with `tags` and `anchors`; read the ephemeral entry back (via the ephemeral store/list path) and assert `Fields["tag"]`/`Fields["anchor"]` are populated AND `Tags`/`RetrievalAnchors` still set. (This test may partly depend on Task 4's projection carry to observe Fields through a list path — if so, assert on the `EphemeralEntry` directly here and let Task 4 cover the projection.)

- [ ] **Step 2: Run to verify it fails** — Expected: FAIL (ephemeral `Fields` lacks tag/anchor).

- [ ] **Step 3: Apply the ephemeral routing** at both sites.

- [ ] **Step 4: Run to verify it passes.**

- [ ] **Step 5: Full suite** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` green.

- [ ] **Step 6: Build warning-clean** — `dotnet build src/Scrinia.Mcp/Scrinia.Mcp.csproj`.

- [ ] **Step 7: Commit** — `"feat(mcp): route tags/anchors into ephemeral Fields at write (dual-write)"`

---

## Task 3: Server write sites (`MemoryOrchestrator`)

**Files:**
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs` — `StoreAsync` ephemeral (~@65) + persistent (~@117), `AppendAsync` ephemeral (~@209) + persistent (~@250)
- Test: `tests/Scrinia.Server.Tests/` (extend `MemoryOrchestratorFieldsTests.cs` or `MemoryEndpointTests.cs`)

**Interfaces:**
- Consumes: `AttributeMigration.Forward` (persistent); the same ephemeral routing helper pattern as Task 2.

**Approach:** mirror Tasks 1–2 on the Server twin. Persistent `StoreAsync`/`AppendAsync`: apply `AttributeMigration.Forward(entry)` before persist. Ephemeral `StoreAsync`/`AppendAsync`: route `req.Tags` → `Fields["tag"]` (ephemeral append sets `Tags=null`, so only Store-ephemeral has tags; anchors are not a `MemoryOrchestrator` param — confirm and route only what exists). Keep all legacy assignments. Note the Orchestrator persistent path is thinner than the MCP twin (no count-pattern/review-field fallback) — `Forward` still routes whatever `ReviewAfter`/`ReviewWhen`/`Tags` are set.

- [ ] **Step 1: Write the failing test** — via `ScriniaServerFactory`, POST a store with tags + reviewAfter, then read the stored entry (through the store, or a GET that exposes Fields) and assert `Fields["tag"]`/`Fields["review"]` populated at write + legacy preserved. Follow `MemoryOrchestratorFieldsTests.cs` for the harness.

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Apply `Forward` (persistent) + ephemeral routing.**

- [ ] **Step 4: Run to verify it passes.**

- [ ] **Step 5: Regression** — `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj` green.

- [ ] **Step 6: Build warning-clean** — `dotnet build src/Scrinia.Server/Scrinia.Server.csproj`.

- [ ] **Step 7: Commit** — `"feat(server): populate Fields at orchestrator write sites via Forward (dual-write)"`

---

## Task 4: Carry `Fields` through ephemeral projections + `CopyMemory` (P6.6-prep)

**Files:**
- Modify: `src/Scrinia.Mcp/ScriniaArtifactStore.cs` — `ListEphemeral` (~@82) + `CopyMemory` explicit-ctor paths (~@606/654/699)
- Modify: `src/Scrinia/HttpMemoryStore.cs` — `ListEphemeral` (~@185)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` — `ListEphemeral` (~@330) + `CopyMemory` explicit-ctor paths (~@1377/1424/1469)
- Test: `tests/Scrinia.Tests/WriterCutoverTests.cs`

**Interfaces:** none new — these are field-carry additions to existing `EphemeralEntry→ArtifactEntry` projections and explicit-ctor copies.

**Why:** the 3 `ListEphemeral` projections build an `ArtifactEntry` from an `EphemeralEntry` and currently DROP `Fields`. Ephemeral entries never pass through read-time forward-fill, so without this the `Fields` written in Task 2 are invisible to search/display — and at P6.6 (legacy removed) ephemeral attributes would vanish entirely. Same for the explicit-ctor `CopyMemory` paths (a Fields-only future source would lose attributes on copy). Carry `Fields` (and, where the projection also drops other now-relevant carriers like `RetrievalAnchors`, carry those too for consistency — but `Fields` is the required one).

**Approach:** in each projection/copy, add `Fields: src.Fields` (and `Keywords`/`ContentPreview` if the projection drops them and they exist on the source) to the constructed `ArtifactEntry`. Do not change any other mapped field.

- [ ] **Step 1: Write the failing test** — store an EPHEMERAL memory with `tags`/`anchors` (so Task 2 populates `Fields`), then LIST/search ephemeral through the projection path and assert the projected `ArtifactEntry.Fields["tag"]`/`["anchor"]` are present. Pre-Task-4 this FAILS (projection drops Fields).

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Add `Fields` carry** to the 3 `ListEphemeral` projections + the explicit-ctor `CopyMemory` paths.

- [ ] **Step 4: Run to verify it passes.**

- [ ] **Step 5: Regression** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` AND `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj` green.

- [ ] **Step 6: Build warning-clean** — `dotnet build src/Scrinia.Core/Scrinia.Core.csproj` + `src/Scrinia.Mcp/Scrinia.Mcp.csproj` + `src/Scrinia/Scrinia.csproj`.

- [ ] **Step 7: Commit** — `"fix(core,mcp): carry Fields through ephemeral projections and copy paths"`

---

## Self-Review (author checklist — completed)

- **Spec coverage:** P6.3 = "reroute Store/Append/UpdateMeta + MemoryOrchestrator to write Fields/Keywords/ContentPreview; keep tool/DTO request params, map server-side." Covered: persistent MCP (Task 1) + ephemeral MCP (Task 2) + Server orchestrator (Task 3) all populate `Fields` at write via `Forward`/routing, keeping request params + legacy (dual-write). ContentPreview/Keywords already write-populated (unchanged). Ephemeral projection/copy carry (Task 4) makes the ephemeral `Fields` observable + P6.6-ready.
- **Dual-write discipline:** no legacy assignment removed anywhere; `Forward` and the ephemeral routing are strictly additive; scorer untouched → scoring neutral (Math.Max keeps legacy 50/15).
- **Deferred correctly:** `Description` input removal, `CodeRefs`→`FileHashes`, scorer legacy-block deletion, model-field removal — all later phases, untouched here.
- **Idempotency:** applying `Forward` at write + read is safe (idempotent; `MigratesTo` returns false on already-complete entries → the read-time hook becomes a no-op for born-complete entries, reducing write-back churn).
- **Risk owned:** the ephemeral projection `Fields`-drop (the silent-regression trap) is Task 4 with a dedicated observing test.
