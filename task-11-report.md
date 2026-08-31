# Task 11 Root-Cause Investigation Report

## Failure group 3 follow-up (2026-08-31)

The sidecar/schema, ArtifactKey, display-identity, and MetaMerge representatives were re-run at
HEAD `8538bc3`. Production fixes were limited to the Core/MCP/Merge surfaces; `src/Scrinia.Server/`
remains untouched.

- Legacy sidecar deserialization used a reduced DTO that discarded extension data. Unknown members
  now transfer into `ArtifactEntry.Unknown` during the legacy URI migration path.
- Reconcile/import validation could either parse legacy URI-shaped resolved content as an internal
  error or miss a newer sidecar already resident in a warm scope. Resolved sidecars now normalize
  `name`/`uri` into `address`/`artifactKey`, and import preflights the cold scope snapshot before
  constructing candidates.
- Both merge implementations previously merged the physical `uri` field. They now normalize
  legacy URI input, merge the portable `artifactKey`, and never emit `uri`.
- Direct serialization and bundle assertions that required `uri` or preservation of the source
  workspace address were stale against the approved portable ArtifactKey contract. The affected
  sidecar serialization fixture and scoped import setup were updated; bundle import correctly
  rebinds the logical address to the destination scope.

Verification for this group: Core representatives **64 passed, 0 failed**; Merge representatives
**85 passed, 0 failed**; durable-format representatives **6 passed, 0 failed**. The remaining
`BundleRoundTripTotalityTests.EveryArtifactEntryMember_SurvivesABundleRoundTrip` failure is a stale
assertion that expects the source `/probe` address after import instead of the destination-scoped
`/totality-source/probe`; no production change is warranted.

## Final Group 3 verification (2026-08-31)

The remaining serialization failure was a stale scope assertion: `/context/note` resolves to the
`local:context` index, not the root `local` index. The test now loads that scoped index while still
asserting the cold-store address, artifact key, and content-preview round trip.

- Sidecar, bundle, and display-identity representatives: **58 passed, 0 failed, 0 skipped**.
- Reconcile durable-format and MetaMerge parity representatives: **27 passed, 0 failed, 0 skipped**.
- Standalone MetaMerge sidecar representatives: **85 passed, 0 failed, 0 skipped**.
- Source-generated address serialization representatives: **7 passed, 0 failed, 0 skipped**.
- Solution build: **15 projects, 0 errors, 2,584 warnings** (existing analyzer warnings).

## Post-commit verification (2026-08-31)

- The 13 requested files are already committed in existing HEAD `c9b291b` (`Unify memory addressing across migration paths`); no pending tracked migration/report files were present to stage.
- `dotnet build Scrinia.sln --no-restore`: **PASS**, 15 projects, 0 errors, 2,584 warnings.
- Focused Task 11 acceptance filter: **271 passed, 0 failed, 0 skipped**.
- Core suite: **2,055 passed, 176 failed, 20 skipped**.
- Full solution: **2,471 passed, 187 failed, 20 skipped** across 4 projects.
- Bundle: **119 passed**; serialization/contract: **33 passed**; consolidation: **3 passed**; CLI: **32 passed**; MCP address: **32 passed**; embeddings plugin: **32 passed**; merge: **127 passed**. Each reported 0 failures and 0 skips.
- Trimmed solution build: **FAIL**, 12 IL2026 errors, all in unchanged `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs`.
- Native AOT publish (`dotnet publish src/Scrinia/Scrinia.csproj -p:PublishAot=true -r win-x64 --no-restore`): **PASS**.
- Only pre-existing untracked `src/Scrinia.Server/` entries remain outside HEAD; `.claude/agent-memory` was not staged.

## Scope and evidence

- Worktree: `C:\SynologyDrive\Source\repos\workspace-scrinia\Scrinia\.claude\worktrees\unified-memory-addressing-task-1`
- Requested HEAD: `850ca1e8ffd7c1bb5668e2001296cc2848576c3e`
- Baseline: `90827cd4c65d1bb803a0d6d56a5208956ac39d4a`
- Read: this report, the Task 11 brief, the SDD ledger at
  `.superpowers/sdd/2026-08-29-unified-memory-addressing/progress.md`, and the plan/spec named by that ledger.

The worktree is **not clean**. HEAD is correct, but there are 12 modified tracked files and an
untracked `src/Scrinia.Server/` directory. The modified files include production contracts and
`FileMemoryStore`; they are not part of HEAD. Therefore the reported 177/188 results cannot be
attributed to HEAD without qualification.

## Reproduction matrix

| Tree | Command | Result |
|---|---|---|
| Baseline archive at `90827cd` | Core test suite, restored and built in an isolated copy | **2,075 passed, 0 failed, 20 skipped** |
| Clean HEAD archive at `850ca1e` | Core test build | **FAIL: 7 compile errors** in `MemoryTools.Core.cs` and `MemoryTools.Lifecycle.cs` |
| Dirty requested worktree, after a fresh solution build | Core tests | **2,055 passed, 176 failed, 20 skipped** |
| Same dirty worktree | Task 11 focused filter | **271 passed, 0 failed, 0 skipped** |
| Same dirty worktree | Solution tests | Reproduces the Core failures; the prior report's 188 count was not reproduced in this run |

The clean-HEAD compile errors are concrete: HEAD's `IMemoryStore` has typed members, while
`MemoryTools.Core.cs`/`MemoryTools.Lifecycle.cs` still call the removed string overloads. The
pending worktree edits add the compatibility/typed forwarding changes that make the solution
compile. This is an uncommitted integration gap, not a legacy failure.

## Exact failure groups in the dirty 176-failure run

The baseline proves these are introduced after `90827cd` (the baseline suite is green), but the
groups below distinguish stale expectations from production behavior regressions:

| Count | Root-cause group | Evidence / introducing surface |
|---:|---|---|
| 27 | Migration/cache tests receive no matching entry | `FileMemoryStoreTests`, `AttributeMigrationTests`, layout tests; repeated `Sequence contains no matching element` or empty indexes. The branch's typed-address/store-layout cutover is implicated; these are real migration behavior failures until the legacy fixtures are either supported or deliberately retired. |
| 27 | Legacy bare-name/URI callers now fail address parsing | 14 `MemoryAddressException: Address is malformed` plus 13 invalid path/URI exceptions. `MemoryStoreAddressExtensions.ParseQualifiedName` (introduced by `ec4af54`) now calls `MemoryAddress.Parse`; old bare-name test/setup paths therefore fail before store access. This is an intentional contract change only where every caller was migrated; remaining callers are not harmless legacy evidence. |
| 14 | Sidecar/format/schema expectations disagree with typed artifacts | `SidecarVersioningTests`, `ArtifactKeyBoundaryTests`, and display tests expect `Uri`, old field order, or old artifact identity; observed `artifacts/*.nmp2` and `address` output. The model/schema cutover was introduced across Tasks 3/4/8/9, not by Task 11. Tests/fixtures require an explicit compatibility decision. |
| 17 | Merge-driver parity and durable-format tests return `error`/`INTERNAL` | 8 merge-success-vs-error results plus durable-format error-code failures. The test harness invokes the current typed/portable sidecar path while asserting the old merge admission/format contract. `MetaMergeSymmetricParityTests` itself predates this branch (blame `3f0f03c6`), but baseline green versus branch red means the changed sidecar/schema input path must be investigated; do not mark it legacy. |
| 22 | Reference/entity/maintenance propagation | `ReferenceTests`, `MaintenanceEventSinkTests`, and `EntityTypingEventHandlerTests`; failures include missing entries, `TryUpdateEntry == false`, missing `mention`/`ref`, and ephemeral canonicalization. These are address/scope propagation regressions or incomplete test migration, not retrieval noise. |
| 13 | Journal/transactional restore and index-cache behavior | `JournaledRestoreRecoveryTests`, `TransactionalRestoreTests`, and `IndexCacheWritePathTests`; failures show `notes/seed`/`other/other`, missing sidecars, and lost entries. This is a scope/canonicalization boundary regression exposed by the new address-to-scope mapping. |
| 8 | Response envelope/advice and CLI/MCP contract expectations | Golden, truncation, missing-parameter, skill-path, and poisoned-reason tests expect prior error/action text or codes. These are contract fixtures, not proof of a production data-loss bug. |
| 48 | Remaining isolated schema, ephemeral, file-reference, and writer assertions | Exact failures include file-reference hashes, ephemeral review/drift projection, sanitized-name limits, write rollback, and portable artifact-key assertions. They require per-test triage; the focused acceptance lane passing does not prove these old/default-suite contracts are correct. |

The groups intentionally overlap only at the diagnostic level; the exact run has 176 failed test
cases and the dominant exact-message signatures are: 27 `Sequence contains no matching element`,
14 malformed-address exceptions, 13 invalid path/URI exceptions, 8 merge status mismatches, 5
null-reference exceptions, 4 not-found results, 4 artifact-key argument failures, and 3 durable
write-code mismatches. The remaining 98 cases have distinct assertion messages.

## Artifacts and status checks

- `git status --short --branch`: HEAD is `850ca1e`; 12 tracked modifications plus untracked
  `src/Scrinia.Server/`.
- `git ls-files src/Scrinia.Server`: no tracked files; its existing untracked files were not changed.
- Clean HEAD archive contains the source files required by the typed contracts, but does **not**
  contain the pending `MemoryStoreAddressExtensions`/contract-forwarding edits needed to compile
  all MCP callers. No missing generated artifact explains the compile errors; the issue is source
  integration.
- The focused 271-test lane passes only in the dirty, compiled state. It is not evidence that clean
  HEAD or the full default suite passes.

## Conclusion

The prior report's “inherited baseline” conclusion is disproved: the true pre-work baseline is
green, while clean HEAD does not compile and the dirty integrated state has 176 Core failures.
There is no remaining focused failure in the rerun (271/271 pass), but fixes are required before
acceptance: first reconcile the uncommitted contract changes with HEAD, then triage the migration,
scope/canonicalization, sidecar/schema, reference/entity, restore, and response-contract groups.
Do not dismiss the 177/188 counts as legacy, and do not modify the untracked server surface as part
of this task.

## Failure group 4 resolution (2026-08-31)

The requested group was reproduced at HEAD `0df5d63` against the green baseline `90827cd`.
The initial focused run had **98 passed and 2 failed**; the failures were stale test assumptions at
the typed-address boundary, not independent production regressions:

- `ReferenceTests.Link_WithShortNames_StoresTheNameVerbatimOnBothSides` still supplied bare
  persistent names (`bare-a`/`bare-b`) to `MemoryAddress.Parse`. Bare names are no longer valid
  public addresses; persistent addresses are canonical `/name` paths. The invalid case was removed
  while the path and ephemeral cases continue to assert verbatim reference values.
- `CoreEmbeddingEventHandlerEphemeralAppendTests` used legacy `ParseQualifiedName("~note")` to
  choose the vector scope. Typed `ResolveAddress(MemoryAddress.Parse("~note"))` is the shared
  address authority and returns `("ephemeral", "note")`; both store and append callbacks use that
  pair. The test now derives its expected identity through the typed resolver and asserts that no
  second scope receives vectors.

These changes preserve `/temp/x == ~x`, typed event contracts, and the prohibition on raw/URI public
APIs. No production or server files were changed for this group because the shared production
address propagation was already correct at HEAD; the remaining failures were incomplete test
migration.

### Group 4 verification

- Focused Core group (`ReferenceTests`, `MaintenanceEventSinkTests`,
  `EntityTypingEventHandlerTests`, `EphemeralLifecycleTests`, and
  `CoreEmbeddingEventHandlerEphemeralAppendTests`): **99 passed, 0 failed, 0 skipped**.
- Plugin embeddings lane: **32 passed, 0 failed, 0 skipped**.
- Solution build (`dotnet build Scrinia.sln --no-restore`): **15 projects, 0 errors**
  (**342 warnings**, existing analyzer/preview-SDK warnings).

## Failure group 5 resolution (2026-08-31)

The MCP/CLI response and writer group was reproduced at HEAD `1133f87` against baseline `90827cd`.
The 37 classified cases were **2 production regressions** plus **35 stale expectations** caused by
the approved typed-address and categorized-error schema changes.

- `MemoryWrite` now rejects an overlong sanitized persistent subject at construction, before disk
  access, while retaining structured limit/actual values for callers.
- `compact()` now rebuilds its typed write address from the existing entry's canonical casing,
  preventing a case-sensitive sibling write.
- Tests now use the MCP string facade for inline artifacts and malformed/URI inputs, assert the
  categorized malformed-address golden response, and expect canonical typed identities in writer
  and event projections. No URI/raw API or server file was added.

Verification: group 5 filter **228 passed, 0 failed, 0 skipped**; MCP/CLI lanes **88 passed, 0 failed,
0 skipped**; solution build **15 projects, 0 errors** (existing warnings only).

## Retrieval regression resolution (2026-08-31)

The confirmed retrieval regression was at the benchmark adapter boundary. `ScriniaRetriever`
derived `RetrievalHit.Id` from the compatibility `Uri`; after the portable-address migration that
view aliases `ArtifactKey`, so correct ranked results exposed values such as
`artifacts/d2.nmp2` instead of the logical ingested document ID. Ranking, entity PPR, MMR, recency,
and hybrid scoring were not changed.

- Added an adapter-side logical-ID map keyed by sanitized entry name, populated for whole documents
  and chunks at ingest. Both `EntryResult` and `ChunkEntryResult` resolve through it before public
  `RetrievalHit` construction; missing mappings fail fast.
- Added whole-document and chunked regressions using IDs that require sanitization. They assert the
  original logical ID and reject sanitized names, portable `artifacts/` keys, and chunk suffixes.
- Exact 11-test retrieval filter: **10 passed, 1 failed, 0 skipped**. The sole remaining failure is
  the pre-classified agentic invalid-path tool-error counter, not retrieval identity.
- Full retrieval test project: **269 passed, 1 failed, 0 skipped**. Focused retrieval lanes:
  **37 passed, 1 failed, 0 skipped**; the same agentic invalid-path counter is the only failure.
- Solution build (`dotnet build Scrinia.sln --no-restore -v:q`): **15 projects, 0 errors, 2,310
  warnings** (existing analyzer/preview-SDK warnings).

## Agentic structured tool-error resolution (2026-08-31)

The remaining retrieval failure was an adapter contract mismatch: address validation returned a
structured MCP YAML error, but the dispatcher marked the response as successful because no exception
was thrown. `ExecuteCallAsync` now centrally recognizes `status: error` responses as tool errors,
without changing successful responses or the exception boundary. A parameterized harness regression
covers `UNSUPPORTED_INPUT_FORM` and `MALFORMED_ADDRESS` responses.

Verification after the fix:

- Exact 11-test retrieval filter: **11 passed, 0 failed, 0 skipped**.
- Full retrieval test project: **272 passed, 0 failed, 0 skipped**.
- Focused agentic acceptance lanes (`AgenticIngestHarnessTests`, `AgenticScriniaRetrieverTests`,
  `AgenticIngestEndToEndTests`): **35 passed, 0 failed, 0 skipped**.
- Solution build (`dotnet build Scrinia.sln --no-restore -v:q`): **15 projects, 0 errors, 2,310
  warnings** (existing analyzer/preview-SDK warnings).
