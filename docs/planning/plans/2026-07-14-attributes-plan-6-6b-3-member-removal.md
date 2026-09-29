# P6.6b Merge 3: Destructive legacy-member removal

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Remove the now-redundant legacy carrier members from `ArtifactEntry` (`Description`, `Tags`, `ReviewAfter`, `ReviewWhen`, `RetrievalAnchors`) and `EphemeralEntry` (`Description`, `Tags`, `RetrievalAnchors`); trim `AttributeMigration.Forward`'s member-sourced folds; reshape `scri list --json` to surface `attributes` and drop the redundant fields; collapse `AttributeReaders` legacy-fallback signatures; and repurpose the tests that asserted the removed members.

**Architecture:** Destructive. Members' data has already been folded into `Attributes`/`ContentPreview` (Merge 2 pre-fold + the completed `scri migrate`), so removal is safe. The read-time forward-fill hook + `scri migrate` still need `Forward`/`MigratesTo` (for the `"fields"`→`"attributes"` key rewrite, the `key:value`-Keywords fold, and the `file`-from-`FileHashes` fold) — so those functions SURVIVE, only their member-sourced bodies trim. `Description` is a positional-**required** param, so its constructor args cannot be dropped until the member is removed → the removal + Description-arg sweep is one coupled final task; every other member's args (optional) and all assertions are cleaned up first, so the final task's compiler errors are just "drop Description args + benchmark".

**Task ordering (green-preserving, deliberate):** readers collapse → re-source stragglers → **repurpose test assertions (before members stop being written)** → **stop setting optional members + trim Forward** → list reshape → re-home copies/bundles → **destructive removal**. Repurposing assertions to read `Attributes` MUST precede nulling the members, or the purpose-tests break.

**Design source (exhaustive, verified line numbers):** `scratchpad/merge3-removal-design.md`. Every task cites its section; implementers MUST read the cited section for the full site list.

**Tech Stack:** .NET 10, C# records, STJ source-gen, xUnit + FluentAssertions.

## Global Constraints

- **TreatWarningsAsErrors on all product projects; 0 warnings.** Ignore IDE-only stale CS8019/CS8933 and phantom `.Attributes`/`.Fields`/`.Description` LSP diagnostics — trust `dotnet build`/`dotnet test` ONLY.
- **`dotnet build Scrinia.sln` fails on a PRE-EXISTING unrelated `Scrinia.AppHost` NuGet-audit restore error.** Verify via per-project builds with `-p:NuGetAudit=false`: `src/Scrinia/Scrinia.csproj` (Core+Mcp+CLI), `src/Scrinia.Server/Scrinia.Server.csproj`, and the three test projects (`tests/Scrinia.Tests`, `tests/Scrinia.Server.Tests`, `tests/Scrinia.Plugin.Embeddings.Tests`). Do NOT build/test `tests/Scrinia.Retrieval.Benchmarks.Tests`.
- **Commit trailer — exact, both lines, every commit:**
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```
- **Never push.** **Staging: NEVER `git add -A`.** Default: `git add -- src tests ':!src/Scrinia.Mcp/plugins'`, then `git status` and confirm no `benchmarks/`, `plugins/`, `.gitignore`, or stray docs staged. **EXCEPTION (Task 7 benchmark only):** the benchmark compile-fix stages exactly `benchmarks/Scrinia.Retrieval.Benchmarks/Core/ScriniaRetriever.cs` — and ONLY after the controller confirms the user's uncommitted benchmark WIP has been committed/stashed (do NOT sweep unrelated WIP; the controller coordinates this at execution time).
- **Do NOT touch (except the one benchmark file in Task 7):** `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, `.superpowers/**`, `.git/**`.
- **KEEP the Merge-1 back-compat on `ArtifactEntry`:** `_legacyFields`, `LegacyFields`, `NormalizeAttributesKey()` — these load old `"fields"`-key sidecars and are UNRELATED to the removed carriers. Do NOT remove them.
- **Server REST DTOs keep their shape** (`MemoryListItem`/`SearchResultItem` retain `Description`/`Tags`), re-sourced from `Attributes`/`ContentPreview` — no REST wire break. ONLY the CLI `CliMemoryEntry` reshapes (user decision).
- **Removed data is already safe** in `Attributes`/`ContentPreview` (Merge 2 + completed migrate). This merge changes behavior only where noted (list --json shape). Post-removal, old sidecars/bundles still load (STJ drops unmapped keys).
- **Forward STAYS on the write paths** (keyword-fold safety net) — do NOT remove the `Forward(entry)` calls; only trim Forward's body.

---

### Task 1: `AttributeReaders` — add `TryReadReview`, collapse legacy-fallback signatures, update callers

**Files:** Modify `src/Scrinia.Core/Search/AttributeReaders.cs`; ~30 caller sites (design §4); Test `tests/Scrinia.Tests/AttributeReadersTests.cs`.
**Design:** §4 + §6.2. **Read design §4 for the complete caller list.**

**Interfaces produced:**
- `TryReadReview(IReadOnlyDictionary<string,string[]>? attributes, out DateTimeOffset? date, out string? condition)` — reuses the exact positional parse in `GetReviewStatus` (round-trip `"o"` date element = date; other non-blank = condition). Returns true if any review data found.
- `GetReviewStatus(attributes, now)` (drop `legacyReviewAfter`/`legacyReviewWhen`), `PreviewText(contentPreview)` → `contentPreview ?? ""`, `DisplayTags(attributes)`, `DisplayAnchors(attributes)`.

- [ ] **Step 1:** Update `AttributeReadersTests.cs` FIRST: delete the legacy-fallback test cases (design §7b: L54-56, L80; `PreviewText(null,"desc")` → `PreviewText(null)` returns `""`; `DisplayTags(...,legacy)` → drop the arg); add a `TryReadReview` test (attributes `["review"]=[date "o", condition]` → out date+condition; empty/missing → false + nulls). Run — fails to compile.
- [ ] **Step 2:** Add `TryReadReview`; collapse the 4 signatures (delete `GetReviewStatus`'s `else { date = legacyReviewAfter; … }` branch; `PreviewText` → 1-arg; drop `legacyTags`/`legacyAnchors`).
- [ ] **Step 3:** Update EVERY caller (design §4 list: `MemoryEndpoints.cs`, `AgentLoop.cs`, `MemoryTools.Core.cs`, `ScriniaCommands.Memory.cs`, `ScriniaCommands.Migration.cs`) — drop the legacy arg at each. Members still exist (harmlessly unpassed).
- [ ] **Step 4:** `dotnet test tests/Scrinia.Tests` + `dotnet build src/Scrinia` + `src/Scrinia.Server` (`-p:NuGetAudit=false`) → 0 warnings, green.
- [ ] **Step 5:** Commit — `refactor(core): collapse AttributeReaders legacy-fallback params; add TryReadReview`.

---

### Task 2: Re-source the straggler reads (UpdateMeta / append carry-forward)

**Files:** Modify `src/Scrinia.Mcp/MemoryTools.Core.cs`, `src/Scrinia.Server/Services/MemoryOrchestrator.cs`. Tests: `ScriniaMcpToolsTests.cs`, `MemoryOrchestratorFieldsTests.cs`.
**Design:** §6 (items 1-5). **Read design §6.**

**Context:** These reads consume a removed member's PRIOR value; re-source from `Attributes`/`ContentPreview` via Task 1's helpers:
- `MemoryTools.Core.cs:468` `updatedDescription = entry.Description` → `entry.ContentPreview` (nullable-adapt).
- `MemoryTools.Core.cs:476/491` `parsedReviewAfter/updatedReviewWhen = entry.ReviewAfter/.ReviewWhen` (UpdateMeta carry-forward) → `AttributeReaders.TryReadReview(entry.Attributes, out …)`.
- `MemoryTools.Core.cs:1228/1266` `BuildAppendMetadata(existing?.Description, existing?.Tags, …, existing?.RetrievalAnchors, …)` → Description→`ContentPreview`, Tags→`DisplayTags(Attributes)`, Anchors→`DisplayAnchors(Attributes)` (adapt param types via `.ToArray()`).
- `MemoryTools.Core.cs:1289` + `MemoryOrchestrator.cs:269/285-286` review carry-forward from `existingEntry?.ReviewAfter/.ReviewWhen` → `TryReadReview(existingEntry.Attributes, …)`.

- [ ] **Step 1:** Ensure a raw-persisted-read test asserts review-carry-forward on `update`/`append` (seed review in `Attributes["review"]` only, update/append without touching review, assert persisted `Attributes["review"]` preserved). Add if missing. Run.
- [ ] **Step 2:** Re-source each read per §6. Members still exist; you're switching the SOURCE. Behavior identical (values come from Attributes, which the members were dual-written into).
- [ ] **Step 3:** `dotnet test tests/Scrinia.Tests` + `tests/Scrinia.Server.Tests` + builds → green, 0 warnings.
- [ ] **Step 4:** Commit — `refactor(mcp,server): re-source UpdateMeta/append carry-forward from Attributes/ContentPreview`.

---

### Task 3: Repurpose purpose-test assertions; drop optional member args from test ctors

**Files:** the §7b test files: `AttributeMigrationTests.cs`, `WriterCutoverTests.cs`, `MemoryOrchestratorFieldsTests.cs`, `ScriniaMcpToolsTests.cs`, `AutoReviewWhenTests.cs`, `ScriniaArtifactStoreTests.cs`, `FileMemoryStoreTests.cs`, `MigrationTests.cs`, `ScorerCleanupTests.cs`, `MemoryEndpointTests.cs`. **Read design §7b for exact lines.**

**Context (WHY this precedes Task 4):** these assert a removed member or the dual-write-to-legacy invariant. Repurposing the ASSERTIONS to read `Attributes["tag"/"review"/"anchor"]`/`ContentPreview`/`GetReviewStatus` works NOW (green — `Attributes` is dual-written while members still exist). This MUST land before Task 4 stops writing the members, or these tests break. Also drop the OPTIONAL member args (`Tags:`/`ReviewAfter:`/`ReviewWhen:`/`RetrievalAnchors:`) from test constructions here. Keep `Description:` (required until Task 7); do NOT do the Description-positional sweep yet.

- [ ] **Step 1:** Per design §7b, repurpose each purpose-test's assertions: `WriterCutoverTests` `.Tags`→`Attributes["tag"]`, `.ReviewAfter/.ReviewWhen`→`GetReviewStatus(Attributes,now)`/`TryReadReview`; `AutoReviewWhenTests` `.ReviewWhen`→`Attributes["review"]` contains condition; `AttributeMigrationTests` fold tests→`Attributes["tag"/"anchor"]`+`ContentPreview`; `MemoryOrchestratorFieldsTests`/`ScriniaArtifactStoreTests`/`FileMemoryStoreTests`/`MigrationTests`/`ScorerCleanupTests`/`ScriniaMcpToolsTests` per the §7b table.
- [ ] **Step 2:** Drop optional member args from those files' constructions (keep `Description:`).
- [ ] **Step 3:** `dotnet test` all three test projects → green, 0 warnings.
- [ ] **Step 4:** Commit — `test: repurpose legacy-member assertions to Attributes/ContentPreview`.

---

### Task 4: Stop setting the optional members on write; trim `Forward`

**Files:** Modify `src/Scrinia.Mcp/MemoryTools.Core.cs`, `src/Scrinia.Server/Services/MemoryOrchestrator.cs`, `src/Scrinia.Core/Migration/AttributeMigration.cs`.
**Design:** §2 (Forward trim), §3 (#1-7, #12-13, #16 SAFE sites). **Read design §2 + §3.**

**Context:** Stop passing the OPTIONAL removed members (`Tags`/`ReviewAfter`/`ReviewWhen`/`RetrievalAnchors`, default null) to `new ArtifactEntry`/`new EphemeralEntry` at the write/projection sites (values already in `Attributes`). KEEP the `Description:` arg (required — removed in Task 7). Trim `Forward` (design §2): change its `BuildLegacyCarrierFields` call → `(tags: null, entry.FileHashes?.Keys, reviewAfter: null, reviewWhen: null, anchors: null)`; DELETE the `ContentPreview ?? Description` backfill (drop `ContentPreview =` from the `with`). KEEP `BuildLegacyCarrierFields`, `NormalizeAttributesKey`, the `key:value`-Keywords fold, and the `Forward(entry)` calls on the write paths.

- [ ] **Step 1:** Trim `Forward` per §2 → existing `AttributeMigrationTests`/`AttributesKeyMigrationTests` must stay green (Forward still folds file + keywords + normalizes).
- [ ] **Step 2:** At each SAFE write/projection site (§3 #1-7, #12-13, #16), drop the `Tags:`/`ReviewAfter:`/`ReviewWhen:`/`RetrievalAnchors:` args. Keep `Description:`.
- [ ] **Step 3:** builds + `dotnet test tests/Scrinia.Tests` + `tests/Scrinia.Server.Tests` → green, 0 warnings. (Task 3 already repurposed the assertions that read those members, so nulling them here is safe.)
- [ ] **Step 4:** Commit — `refactor(core,mcp,server): stop dual-writing optional legacy members; trim Forward member folds`.

---

### Task 5: `list --json` reshape (CLI) + server DTO re-source + HttpMemoryStore

**Files:** Modify `src/Scrinia/Commands/CliJsonContext.cs` (`CliMemoryEntry`), `src/Scrinia/Commands/ScriniaCommands.Memory.cs` (`BuildCliMemoryEntry`), `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` (population), `src/Scrinia/HttpMemoryStore.cs` (#17/#18). Test: `tests/Scrinia.Tests/CliDisplayCutoverTests.cs`.
**Design:** §5 + §3 #17/#18. **Read design §5.**

- [ ] **Step 1:** Update `CliDisplayCutoverTests.cs`: `cli.Tags`→`cli.Attributes["tag"]`; `cli.Description` stays. Run — fails (new shape).
- [ ] **Step 2:** New `CliMemoryEntry` (design §5): drop `Tags`/`ReviewAfter`/`ReviewWhen`; add `Attributes` (`Dictionary<string,string[]>?`); keep `Description`, `IsStale`, `NeedsReview`. Update `BuildCliMemoryEntry`: `Description=PreviewText(e.ContentPreview)`, `Attributes=e.Attributes`, `IsStale/NeedsReview` from `GetReviewStatus(e.Attributes, now)`.
- [ ] **Step 3:** Server: KEEP `MemoryListItem`/`SearchResultItem` SHAPE (Description/Tags); re-source population — `Description←PreviewText(entry.ContentPreview)`, `Tags←DisplayTags(entry.Attributes).ToArray()`. HttpMemoryStore #17/#18: route wire `item.Description→ContentPreview`, `item.Tags→Attributes["tag"]` when reconstructing the entry.
- [ ] **Step 4:** builds + `dotnet test tests/Scrinia.Tests` + `tests/Scrinia.Server.Tests` → green. Confirm `list --json` emits `attributes`, not `tags`/`reviewAfter`/`reviewWhen`.
- [ ] **Step 5:** Commit — `feat(cli): list --json surfaces attributes, drops redundant tags/review fields`.

---

### Task 6: Re-home the remaining construction sites (synthetic-desc copies, bundle import/export)

**Files:** Modify `src/Scrinia.Core/FileMemoryStore.cs` (#10,11), `src/Scrinia/Mcp/ScriniaArtifactStore.cs` (#14,15), `src/Scrinia.Core/Bundles/BundleFormatService.cs` (#19 import), `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` (#20 export), `src/Scrinia/HttpMemoryStore.cs` (#18 if not done in Task 5). Tests: `BundleV2Tests.cs`.
**Design:** §3 #10,11,14,15,18,19,20 + §9. **Read design §3 re-home rows + §9.**

**Context:** These sites' removed-member values are NOT otherwise in `Attributes`/`ContentPreview` — re-route to avoid data loss:
- Synthetic-desc copies (#11 `FileMemoryStore.cs:1555`, #15 `ScriniaArtifactStore.cs:712`): `Description: $"Copied from {src}"` → set `ContentPreview: srcPreview ?? $"Copied from {src}"`. #10/#14 eph-copy fallback → `ContentPreview: srcEntry?.ContentPreview ?? "Loaded from …"`.
- Bundle IMPORT (#19 `BundleFormatService.cs:172/207`): keep reading `"description"`/`"tags"` (back-compat for old bundles) but route → `ContentPreview` (`contentPreview ?? description`) and `Attributes["tag"]`.
- Bundle EXPORT (#20 `ScriniaCommands.Bundle.cs:184`): put desc → `ContentPreview`, tags → `Attributes["tag"]`.
- #18 `HttpMemoryStore.cs:363` → `ContentPreview: item.Description ?? ""`.

Keep the `Description:` arg (required) at each site until Task 7 — pass the same value you route to `ContentPreview` (harmless dual until Task 7 drops it). Drop the optional `Tags:` args.

- [ ] **Step 1:** Add/confirm a bundle round-trip test: export a memory with desc+tags, re-import, assert `ContentPreview`/`Attributes["tag"]` survive; AND an OLD-bundle import test (JSON with `"description"`/`"tags"`, no `"attributes"`) → routes into ContentPreview/Attributes. Run.
- [ ] **Step 2:** Re-home each site per §3/§9.
- [ ] **Step 3:** builds + full test → green.
- [ ] **Step 4:** Commit — `refactor(core,cli): re-home copy/bundle description+tags to ContentPreview/Attributes`.

---

### Task 7 (DESTRUCTIVE): Remove the members; sweep Description args; benchmark fix; verify back-compat

**Files:** `src/Scrinia.Core/Models/ArtifactEntry.cs`, `EphemeralEntry.cs`; every remaining `Description:` site (compiler-surfaced); `benchmarks/Scrinia.Retrieval.Benchmarks/Core/ScriniaRetriever.cs` (the EXCEPTION file).
**Design:** §1, §3 (Description sweep), §8 (benchmark), §10 step 10.

**Controller pre-step (NOT the implementer):** before dispatching, confirm the user's uncommitted `benchmarks/**` WIP is committed or stashed, so the benchmark compile-fix stages cleanly.

- [ ] **Step 1:** Remove the 5 members from `ArtifactEntry` and 3 from `EphemeralEntry` (§1). KEEP `FileHashes`, `Attributes`, `ContentPreview`, `Keywords`, `TermFrequencies`, `UpdatedAt`, `ChunkEntries`, and the `_legacyFields`/`LegacyFields`/`NormalizeAttributesKey` trio.
- [ ] **Step 2:** Build all product + test projects (`-p:NuGetAudit=false`). For each compiler error, DROP the `Description:` arg (product ctors, test ctors, `with` expressions) — mechanical (re-home sites already set `ContentPreview`). Repeat until product + tests are 0 errors / 0 warnings.
- [ ] **Step 3:** Benchmark compile-fix (§8, EXCEPTION): in `benchmarks/…/ScriniaRetriever.cs` drop `Description: string.Empty` (~L238); change `RetrievalAnchors: anchorSource` (~L242) → fold anchors into `Attributes["anchor"]` before construction (or drop if Attributes already carries them); fix the `<see cref="ArtifactEntry.RetrievalAnchors"/>` cref (~L151) → `Attributes`. Compile fix only.
- [ ] **Step 4:** `dotnet build Scrinia.sln -p:NuGetAudit=false` — benchmark now compiles; solution builds clean (AppHost bypassed). Full test run: all three product test projects green, 0 warnings.
- [ ] **Step 5: Back-compat round-trip.** Test: (a) an OLD sidecar JSON carrying `"description"/"tags"/"reviewAfter"/"reviewWhen"/"retrievalAnchors"` (+`"attributes"`) still deserializes cleanly (unmapped keys dropped, `Attributes`/`ContentPreview` intact); (b) an OLD bundle imports (Task 6 test). No NRE / data loss.
- [ ] **Step 6:** Stage `src tests ':!src/Scrinia.Mcp/plugins'` AND `benchmarks/Scrinia.Retrieval.Benchmarks/Core/ScriniaRetriever.cs` (the single exception); `git status` to confirm no OTHER benchmark/plugin file staged; commit — `feat(core)!: remove legacy carrier members (Description/Tags/ReviewAfter/ReviewWhen/RetrievalAnchors)`.

---

## Post-merge validation (user, before considering P6.6b done)
Rebuild `scri` (`publish.ps1`); confirm `remember`/`append`/`hint`/`consolidate`/`list --json`/`bundle` work; `scri list --json` shows `attributes` (no `tags`/`reviewAfter`/`reviewWhen`); an OLD sidecar + OLD bundle still load. `migrate --dry-run` = 0.

## Self-Review notes (author)
- **Order rationale:** removal (Task 7) is last because `Description` is required — its args can't drop until the member is gone. Everything else is cleaned first, so Task 7 is a focused Description-sweep + benchmark. **Repurpose (Task 3) precedes stop-setting (Task 4)** — assertions must read `Attributes` before the members stop being written, else purpose-tests break.
- **The Merge-2 value-lock tests become discriminating here** (Task 4 trims Forward; Task 3 repurposes): when Forward's folds go, the pre-folds alone must produce the persisted values.
- **Trickiest (design §10):** UpdateMeta/append review carry-forward via `TryReadReview` (Task 2); synthetic-desc/bundle re-homes (Task 6); benchmark in-solution break (Task 7 same-change); Forward STAYS on writes.
- **Coverage vs design:** Task1←§4/§6.2; Task2←§6; Task3←§7b; Task4←§2/§3-safe; Task5←§5; Task6←§3-rehome/§9; Task7←§1/§3-Description/§8/§10.
