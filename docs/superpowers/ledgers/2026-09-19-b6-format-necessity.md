# B6 Format-Change Necessity Ledger

**Date:** 2026-09-19
**Scope:** B6 format-change necessity finding — a decision gate, not an implementation
**Status:** APPROVE — B6 closes as a no-op; no NMP/2 or sidecar format change is necessary
**Authoring:** Orchestrator-produced acceptance record. This ledger records the B6 necessity
verdict and its evidence; it authorizes no source or test changes outside the B6 test file and
records only evidence already in force on `main`.

Plan: `docs/superpowers/plans/2026-09-18-b6-format-necessity.md`
Roadmap gate: umbrella roadmap §8 (acceptance gate)
Historical gate: umbrella roadmap §7 (new record, no rewrite)

## 1. Base commit and test boundary

| Reference | Commit | Notes |
| --- | --- | --- |
| Base commit | `5a9a316b` | B2 environmental-guard commit; the B6 range sits on top of the B2 guard commits |
| B6 invariant lock | `f64d4302` | `test: lock B6 invariant — sidecar/NMP/2 carrier carries no B1-B5 transaction metadata` |
| B6 test file | `tests/Scrinia.Tests/B6CarrierInvarianceTests.cs` | 4 facts; committed at `f64d4302`, on top of `5a9a316b` |

The B6 charter (roadmap spec 2026-09-10): **B6 is the only Phase B subproject permitted to propose
NMP/2 or sidecar format changes, and it is blocked until "B0–B5 are accepted and a format change is
shown necessary."** Its sole task is to decide whether a format change is necessary at all.

## 2. Necessity verdict

**FORMAT_CHANGE_NOT_NECESSARY.** The B1–B5 control data is neither expressible in nor required inside
the carrier, and every B1–B5 fact is already persisted outside it. The carrier structurally cannot
hold the recovery/transaction/maintenance metadata, and the one memory-level maintenance signal B5
needs is already expressed in the existing sidecar schema.

### 3. Evidence

| # | Finding | Basis |
| --- | --- | --- |
| 1 | NMP/2 is a pure content/integrity carrier with no metadata slot. | `ArtifactMetadata` (`IEncodingStrategy.cs`) exposes exactly `{StrategyId, OriginalBytes, Crc32}`. The `nmp/2` header grammar (`NMP_SPEC.md`) is fixed; adding a field requires a version bump. |
| 2 | The artifact sidecar carries only memory metadata, never control/recovery data. | `ArtifactEntry` fields: address, artifact key, original bytes, chunk count, created/updated timestamps, content preview, keywords, term frequencies, chunk entries, file hashes (`codeRefs`), attributes, agent keywords, schema version — plus a `[JsonExtensionData] Unknown` back-compat bucket. No transaction/recovery/maintenance field exists. |
| 3 | Every B1–B5 new metadata class is persisted OUTSIDE the carrier. | Four separately-versioned `DurableFormats` control-data contracts (AppendJournal, RestoreJournal, RecoveryEnvelope, MultiMemoryTransaction) live in their own scoped files; `B0Result`/`B0Outcome`/`B0Failure`/`B0FailureTaxonomy` are response-envelope-only facades (never persisted). |
| 4 | The single memory-level maintenance signal B5 needs is already expressible. | Orphan / inbound-reference membership is expressed in the sidecar `Attributes` bag (`Attributes["orphan"]`, `Attributes["ref"]`), with `KnownAttributes` defining `OrphanKey="orphan"` and `ReferenceKeys={"file","ref"}`, capacity-capped at `MaxAttributes=32` / `MaxValuesPerKey=256`. No new sidecar field is required. |

### 4. No representation gap

There is no B1–B5 fact that must survive reload-and-search from the memory itself but is not
expressible today. B1–B5 control data has its own versioned homes; the memory-level maintenance
signals already live in the sidecar attributes. The finding was independently reviewed and APPROVED.

## 5. The invariant lock (test-only)

`tests/Scrinia.Tests/B6CarrierInvarianceTests.cs` (4 facts) proves via the **real** store write path
(`InvokeWriteSidecarForTest` → `WriteSidecarCore` → `SerializeSidecar` → `FileStoreJsonContext`)
that:

| Fact | Assertion |
| --- | --- |
| 1 | A fully-attributed sidecar contains none of the 14 B1–B5 keys (`recovery`, `operationId`, `failure`, `b0result`, `coordinator`, `multimemory`, `recoveryenvelope`, `maintenance`, `transactionid`, `participant`, `evidencepath`, `warning`, etc.). |
| 2 | `orphan` + `ref` are present in the serialized sidecar without any schema change (and round-trip through the store's own wire shape). |
| 3 | NMP/2 `ArtifactMetadata` has exactly 3 fields (`StrategyId`, `OriginalBytes`, `Crc32`). |
| 4 | The four control-data formats are registered as distinct contracts. |

A future field added to `ArtifactEntry` would leak into the sidecar and fail this test — the lock is
regression-only, no `src/` file is touched.

## 6. Verification gate (roadmap §8)

| Gate | Command / Result |
| --- | --- |
| B6-focused lane | `--filter FullyQualifiedName~B6CarrierInvarianceTests`: **4 passed, 0 failed** |
| Full solution | `dotnet test Scrinia.sln`: **exit 0** — Scrinia.Retrieval.Benchmarks.Tests 272 passed; Scrinia.Tests 3666 passed / 20 skipped; Scrinia.Plugin.Embeddings.Tests 63 passed |
| The 20 skips | Environmental opt-in: `GlobalToolInstall` detached-PowerShell guard; Model2Vec/Unigram/Bert tokenizer; Model2VecLazyLoad; PublishManifest |
| Release build | `dotnet build Scrinia.sln -c Release`: **0 errors** |
| Scope | No `src/` file changed by B6 — only a test file (`tests/Scrinia.Tests/B6CarrierInvarianceTests.cs`) |

## 7. Dependency gate closed

B6 is the final Phase B subproject. The Phase B roadmap `B0 → {B1, B2} → B3 → {B4, B5} → B6` is
complete. No downstream subproject is authorized by this roadmap; the next roadmap stage (Phase C)
has no plan yet.

## 8. Historical-docs note (roadmap §7)

This ledger is a new record. It does not rewrite any B1–B5 acceptance ledger or the Phase B umbrella
roadmap. The existing acceptance ledgers (`2026-09-13-b3-final-acceptance.md`,
`2026-09-13-b4-plugin-event-boundary.md`, `2026-09-17-b2-gap-closure-acceptance.md`,
`2026-09-17-b5-final-acceptance.md`, `2026-09-19-b2-final-acceptance.md`) are preserved unchanged.

## 9. Phase B roadmap

```
B0 → {B1, B2} → B3 → {B4, B5} → B6   (complete)
```
