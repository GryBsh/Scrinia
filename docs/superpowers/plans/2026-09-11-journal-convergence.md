# B2 Journal Convergence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Converge append, restore, and file-entity recovery evidence on one versioned, scope-qualified hybrid envelope without moving their files, changing normal operation behavior, or weakening B1 contamination protections.

**Architecture:** Add a source-generated JSON envelope and typed candidate/result contracts in `Scrinia.Core`, then give each operation a private adapter that normalizes legacy evidence into the envelope in memory while retaining operation-specific bodies, validation, transitions, and replay logic. A scope-local discovery coordinator runs under the existing lock/barrier, qualifies exact locations before dispatch, admits candidates in restore, append, file-entity order, and returns typed blocking or post-commit-cleanup outcomes to the existing `FileMemoryStore`, MCP, and CLI facades.

**Tech Stack:** C# on `net10.0`, `System.Text.Json` source generation, xUnit 2.9.3, FluentAssertions 6.12.2, filesystem atomic writes and existing `FileLock`/`DurableFile` seams, Scrinia Core/MCP/CLI projects.

**Spec:** `docs/superpowers/specs/2026-09-11-journal-convergence-design.md`

> **Scope status (2026-09-17):** This remains an implementation plan. The current B2
> reconciliation is documentation-only: it neither changes source or tests nor establishes new
> verification results. Fixture completeness, deterministic reparse-point coverage, envelope
> hygiene, and the implementation/test work described below remain planned for a later
> implementation phase.

## Global Constraints

- Append evidence remains `.append-journal.json`; restore evidence remains `.restore-journal.json`; file-entity evidence remains in `.file-entity-*.recovery` files in the existing scope directory.
- The common envelope owns identity, version, scope, lifecycle state, and recovery metadata; operation bodies retain operation-specific data and invariants.
- Discovery qualifies every candidate to exactly one store scope before it can affect that scope; ambiguous or unsafe evidence blocks only the affected scope.
- Deterministic precedence is restore, append, then file entity, followed by ordinal canonical subject and `operationId` ordering.
- Recovery is fail-closed for malformed, unsafe, newer-than-supported, ambiguous, and internally inconsistent evidence; retained evidence is not rewritten merely because it was read.
- A verified committed result is never rolled back for cleanup failure; cleanup failure is a typed, retained, retryable post-commit warning.
- The B1 ruling remains in force: an unrelated or unqualified recovery file must never promote an ordinary failure into a recovery-blocked result for another scope.
- Common envelope version `1` and every supported operation body version are rejected before mutation when newer than the running build.
- All durable JSON uses source-generated metadata; reflection-based serialization and exception-message parsing are forbidden.
- Existing command/tool names, successful-response semantics, normal append/restore/file-entity behavior, physical locations, and operation correlation semantics remain unchanged.
- Do not modify NMP/2, ordinary artifacts, index files, metadata, ordinary sidecars, cross-scope transactions, workspace-wide barriers, background garbage collection, fleet reporting, daemons, server/HTTP surfaces, or unrelated schemas.
- Every filesystem path read from evidence is canonicalized and contained by the expected scope/entity layout before any filesystem use.
- All mutation and recovery admission stays inside the existing target-scope coordination boundary and never creates a cross-scope transaction.
- Use the existing B1 operation IDs from validated evidence in recovery results; caller-generated IDs may identify the attempted request but cannot replace a safely read journal identity.

---

## File Responsibility Map

The implementation should add focused files rather than turning the existing operation codecs into a shared mutable state machine.

| File | Responsibility |
|---|---|
| `src/Scrinia.Core/RecoveryEnvelope.cs` | Historical B2 envelope wire-model location. Its former `RecoveryEnvelopeDocument`/`JsonElement Body` parser boundary is superseded by the B5 verified-envelope boundary described below. |
| `src/Scrinia.Core/RecoveryContracts.cs` | Typed candidate, classification, blocking, cleanup-warning, retry, and adapter interfaces/results shared by store operations. |
| `src/Scrinia.Core/RecoveryDiscovery.cs` | Exact-location enumeration, filename/operation checks, candidate qualification, deterministic sorting, ambiguity detection, and scope-local admission orchestration. |
| `src/Scrinia.Core/AppendJournal.cs` | Existing append body codec and typed verified-envelope consumer; preserve append-specific state and payload/path/hash validation. |
| `src/Scrinia.Core/RestoreJournal.cs` | Existing restore body codec plus restore adapter, staging/snapshot protection, replay verification, and legacy-to-envelope normalization. |
| `src/Scrinia.Core/FileEntityTransactionCoordinator.cs` | File-entity adapter and JSON envelope evidence publication/recovery while retaining `.file-entity-*.recovery` locations and pair semantics. |
| `src/Scrinia.Core/WholeMemoryTransaction.cs` | Shared internal result/error contracts and interface declarations where existing whole-memory callers already depend on them; add only typed recovery fields required by the common coordinator. |
| `src/Scrinia.Core/FileMemoryStore.cs` | Wire one scope-local discovery/admission call into existing read/write/recovery boundaries, preserving lock order, cache invalidation timing, and operation-specific business flows. |
| `src/Scrinia.Core/Formats/DurableFormats.cs` | Register the common recovery envelope and file-entity recovery contract without changing NMP/2 or ordinary sidecar contracts. |
| `src/Scrinia.Mcp/MemoryTools.cs` | Consume typed Core blocked/recovery outcomes and retain exact `RECOVERY_BLOCKED`, `RECOVERY_REQUIRED`, and post-commit-warning mappings. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | Route append and file-entity outcomes using validated evidence identity and scope; remove any remaining operation-specific filesystem inference. |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | Route restore/import outcomes through the typed coordinator result without changing public tool names or successful payloads. |
| `src/Scrinia.Mcp/MemoryTools.Skills.cs` | Route skill/agent file-entity publication warnings and blocked recovery details without changing ordinary sidecar format or built-in precedence. |
| `src/Scrinia.Mcp/McpResponse.cs` | Add only typed fields needed to render common operation, reason category, evidence path, and validated operation ID consistently. |
| `src/Scrinia.Mcp/ScriniaMcpJsonContext` declaration in `src/Scrinia.Mcp/MemoryTools.Skills.cs` | Register any newly public MCP result DTOs/enums used by the response contract. |
| `src/Scrinia/Commands/ScriniaCommands.cs` | Preserve CLI JSON/YAML wrapper and exit classes while rendering typed Core recovery results; never classify from file existence or exception text. |
| `src/Scrinia/Commands/CliJsonContext.cs` | Register any new CLI wrapper DTOs, keeping all output source-generated. |
| `tests/Scrinia.Tests/RecoveryTestFixtures.cs` | Shared deterministic envelope/body bytes, scope layouts, operation IDs, legacy documents, path fixtures, and byte snapshots. |
| `tests/Scrinia.Tests/RecoveryEnvelopeTests.cs` | Contract, converter, version, structural validation, and source-generation tests. |
| `tests/Scrinia.Tests/RecoveryDiscoveryTests.cs` | Deterministic precedence, qualification, ambiguity, shuffled enumeration, and B1 contamination tests. |
| `tests/Scrinia.Tests/AppendJournalTests.cs` and `AppendRecoveryTests.cs` | Append adapter normalization, envelope round trips, rollback, cleanup, and retained-evidence behavior. |
| `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs` and `Mcp/RestoreEnvelopeTests.cs` | Restore adapter, staging, scope barrier, mixed-version, cache, and cleanup retry behavior. |
| `tests/Scrinia.Tests/FileEntityTransactionTests.cs` | File-entity envelope publication, containment, pair verification, rollback, committed cleanup, and retry behavior. |
| `tests/Scrinia.Tests/RecoveryFacadeMappingTests.cs` | MCP and CLI typed mapping for all blocked, required-recovery, and post-commit-warning classes. |
| `tests/Scrinia.Tests/RecoverySerializationTests.cs` | Round trips, invalid wire values/types, unsupported versions, unknown members, and trimming-safe metadata use. |

## Exact Shared Interfaces — B5 Reconciliation

> **Historical-plan amendment (2026-09-17).** The original interface listing in this section described a B2-owned `RecoveryEnvelopeDocument` / `JsonElement Body` parser boundary and `IRecoveryAdapter.ParseAndNormalize` / `ParseAndValidate` shapes that do not represent HEAD; it must not be used to reintroduce those interfaces. The section was again reconciled to HEAD on 2026-09-17 — see [Interface reconciliation to HEAD (2026-09-17, B2 gap-closure)](#interface-reconciliation-to-head-2026-09-17-b2-gap-closure).

B5 owns the sole parse-and-validate boundary. `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` takes exactly six inputs: verified content, expected scope, evidence path, expected evidence directory, evidence kind, and verified metadata. It performs the bounded byte/JSON parse and validates scope, location, kind, metadata, and envelope, returning `RecoveryVerifiedEnvelope`. Legacy append/restore/file-entity evidence is normalized into the envelope in memory inside `ParseVerifiedEnvelope`'s legacy parse paths; there is no separate B2 `NormalizeLegacy` step.

`RecoveryVerifiedEnvelope` exposes the parsed document and its typed body via the `TypedBody` accessor (`RecoveryEnvelopeDocument.TypedBody`), which the consumers borrow. `RecoveryEnvelopeDocument` retains a live, intentional `JsonElement Body` (the raw wire body) alongside the parsed `TypedBody`; the raw body is preserved for containment and re-serialization checks and is not a historical artifact.

B2 consumes that returned value through I/O-, parse-, and validation-free consumers that borrow the already-parsed envelope:

- `AppendJournalCodec.ConsumeParsed(in RecoveryVerifiedEnvelope)` and `RestoreJournalCodec.ConsumeParsed(in RecoveryVerifiedEnvelope)` reconstruct the typed append/restore journal from the parsed body.
- `AppendRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope)`, `RestoreRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope)`, and `FileEntityRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope)` implement the `IRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope)` boundary; each borrows the parsed body and performs no I/O, JSON parsing, or duplicate validation.
- There is no `GetOperationId` method at HEAD; operation identity flows through the validated `RecoveryEnvelopeDocument.OperationId`. `MultiMemoryTransactionRecordCodec` is a record codec, not a `ConsumeParsed` consumer.

The `IRecoveryAdapter` boundary at HEAD is:

```csharp
RecoveryEnvelopeDocument ConsumeParsed(in RecoveryVerifiedEnvelope envelope);
RecoveryEnvelopeDocument ValidateForMutation(
    RecoveryEnvelopeDocument envelope,
    string expectedScope,
    string evidencePath,
    string expectedSubject,
    string expectedEvidenceDirectory);
RecoveryOutcome RecoverOrCleanup(
    RecoveryEnvelopeDocument envelope,
    string evidencePath,
    CancellationToken cancellationToken);
RecoveryOutcome Admit(RecoveryEnvelopeDocument envelope, string evidencePath);
```

`ValidateForMutation` takes five arguments (the fourth is `expectedSubject`, the fifth is `expectedEvidenceDirectory`), not the four-argument shape listed in the original plan. `RecoverOrCleanup` and `Admit` are unchanged. `RecoveryEnvelopeCodec.Parse` (a contextless legacy entry point) is removed and `ParseTypedBody` was renamed to the private `ParseTypedBodyDocument`; `Serialize`, `FromAppend`, `FromRestore`, and `FromFileEntity` remain the envelope's construction/serialization seams, not parse entry points.

The operation-specific payload, containment, state-transition, rollback, and cleanup invariants in the remainder of this plan remain frozen. This reconciliation changes only the documented interface signatures; it does not change B2 behavior, evidence locations, precedence, or public MCP/CLI contracts. See B5 design §§0.4, 0.6, 0.7, and 2.1–2.2.

### Interface reconciliation to HEAD (2026-09-17, B2 gap-closure)

This subsection closes the gap between the 2026-09-11 interface listing and HEAD. It is documentation-only; it changes no source or test and establishes no new verification result.

- Sole parser: `RecoveryEnvelopeCodec.ParseVerifiedEnvelope(in RecoveryVerificationContent content, in RecoveryScopeBinding expectedScope, string evidencePath, string expectedEvidenceDirectory, RecoveryEvidenceKind evidenceKind, in RecoveryVerifiedFileMetadata metadata)` — six arguments, in `src/Scrinia.Core/RecoveryVerification.cs` (~line 299). Replaces the original B2-owned `RecoveryEnvelopeDocument` / `JsonElement Body` parse boundary and the removed `RecoveryEnvelopeCodec.Parse`.
- Typed consumers (all I/O/parse/validation-free, borrow the parsed envelope): `AppendJournalCodec.ConsumeParsed`, `RestoreJournalCodec.ConsumeParsed`, `FileEntityRecoveryAdapter.ConsumeParsed`, `AppendRecoveryAdapter.ConsumeParsed`, `RestoreRecoveryAdapter.ConsumeParsed`, and the `RecoveryVerifiedEnvelope.TypedBody` accessor. The original plan's `MultiMemoryTransactionRecordCodec.ConsumeParsed`, `FileMemoryStore.GetOperationId`, and `RecoveryDiscoveryCoordinator.GetOperationId` entries are removed — there is no `GetOperationId` at HEAD, and `MultiMemoryTransactionRecordCodec` is a record codec, not a `ConsumeParsed` consumer.
- `IRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope)` replaces the original `ParseAndNormalize`. `ValidateForMutation(envelope, expectedScope, evidencePath, expectedSubject, expectedEvidenceDirectory)` gains the fifth `expectedEvidenceDirectory` argument over the original four-argument listing. `RecoverOrCleanup(envelope, evidencePath, ct)` and `Admit(envelope, evidencePath)` are unchanged.
- `RecoveryEnvelopeDocument` retains a live intentional `JsonElement Body` (raw wire body) alongside the parsed `TypedBody`; the original plan's treatment of that boundary as historical is corrected.
- Legacy entry points `RecoveryEnvelopeCodec.Parse` and `ParseTypedBody` are removed; `ParseTypedBody` is now the private `ParseTypedBodyDocument`. `NormalizeLegacy` is gone — legacy normalization lives inside `ParseVerifiedEnvelope`'s legacy parse paths.

Behavior, deterministic precedence (restore → append → file entity), scope-local blocking, the B1 contamination ruling, legacy/mixed-version compatibility, committed-cleanup retry, and public MCP/CLI mappings in the remainder of this plan are unchanged. See B5 design §§0.4, 0.6, 0.7, and 2.1–2.2.

## Task 1: Baseline Fixtures and Contracts

**Files:**
- Create: `src/Scrinia.Core/RecoveryContracts.cs`
- Create: `tests/Scrinia.Tests/RecoveryTestFixtures.cs`
- Create: `tests/Scrinia.Tests/RecoveryEnvelopeTests.cs`
- Modify: `src/Scrinia.Core/Formats/DurableFormats.cs`
- Modify: `src/Scrinia.Core/WholeMemoryTransaction.cs`

**Interfaces:**
- Consumes: existing `RecoveryInfo`, `WholeMemoryOutcome`, `AppendStoreResult`, `RestoreRecoveryException`, `FileEntityMutationResult`, `MemoryAddress`, `PathContainment`, and `DurableFormats` contracts.
- Produces: `RecoveryOperation`, `RecoveryLifecycleState`, `RecoveryBlockReason`, `RecoveryBlock`, `RecoveryCandidate`, `RecoveryDiscoveryResult`, `RecoveryOutcome`, and the typed adapter interface used by Tasks 2-7.

- [ ] **Step 1: Add deterministic fixtures for each preserved location and each legacy state.**

  Create fixture helpers returning `local:notes`, `local:other`, canonical `/notes/note`, `operationId = "01JOURNAL000000000000000001"`, and fully specified byte arrays for old/new artifact, sidecar, archive, file-entity body, and metadata. Include builders for `.restore-journal.json`, `.append-journal.json`, and `.file-entity-{id}.recovery` at their existing locations, plus a scope snapshot helper that records sorted relative paths and bytes.

- [ ] **Step 2: Write failing contract tests for the common discriminators and typed result shape.**

  Assert that the operation enum contains exactly `Restore`, `Append`, and `FileEntity`; lifecycle enum contains exactly the seven lowercase-wire states from the spec; a blocked result requires scope/evidence path/reason; and a recovery result cannot use a blank operation ID. Assert that `DurableFormats.All` includes exactly one new common recovery contract and does not change the current NMP/2, artifact-sidecar, or ordinary metadata entries.

- [ ] **Step 3: Run the focused tests and verify the new contracts fail to compile or assert.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RecoveryEnvelopeTests" --no-restore`

  Expected: FAIL because the new contract types and registry entry do not exist yet.

- [ ] **Step 4: Implement the minimal internal contracts and registry entry.**

  Keep common results immutable records, require non-empty scope/evidence path/operation ID, make `RecoveryOperation` parsing separate from the lowercase JSON converter, and add `DurableFormats.RecoveryEnvelope = new("recovery-envelope", 1, null, FormatDisposition.Refuse, FormatDisposition.Refuse)` to `All`. Do not add a migration writer or alter existing format versions.

- [ ] **Step 5: Run the focused tests and the existing contract lane.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RecoveryEnvelopeTests|FullyQualifiedName~FormatVersionTests" --no-restore`

  Expected: PASS for the new baseline tests and all existing format contract tests.

- [ ] **Step 6: Commit the baseline and stop for review.**

  ```bash
  git add src/Scrinia.Core/RecoveryContracts.cs src/Scrinia.Core/Formats/DurableFormats.cs src/Scrinia.Core/WholeMemoryTransaction.cs tests/Scrinia.Tests/RecoveryTestFixtures.cs tests/Scrinia.Tests/RecoveryEnvelopeTests.cs
  git commit -m "feat: define b2 recovery contracts"
  ```

  Review gate: reject the task if any public MCP/CLI name changes, if `RecoveryEnvelope.Body` becomes a reflection-serialized `object`, or if the registry changes any ordinary format contract.

## Task 2: Common Envelope and Operation Adapters

**Files:**
- Create: `src/Scrinia.Core/RecoveryEnvelope.cs`
- Modify: `src/Scrinia.Core/AppendJournal.cs`
- Modify: `src/Scrinia.Core/RestoreJournal.cs`
- Modify: `src/Scrinia.Core/FileEntityTransactionCoordinator.cs`
- Modify: `src/Scrinia.Core/Formats/DurableFormats.cs`
- Modify: `tests/Scrinia.Tests/RecoveryEnvelopeTests.cs`
- Modify: `tests/Scrinia.Tests/AppendJournalTests.cs`
- Modify: `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs`
- Modify: `tests/Scrinia.Tests/FileEntityTransactionTests.cs`

**Interfaces:**
- Consumes: existing operation codecs/transaction snapshots and the B5-owned verified-envelope handoff in [Exact Shared Interfaces — B5 Reconciliation](#exact-shared-interfaces--b5-reconciliation).
- Produces: typed operation bodies and consumers only. The former B2 `RecoveryEnvelopeCodec.Parse` and adapter parser signatures are superseded; parsing and common validation occur once in `ParseVerifiedEnvelope`, then typed `ConsumeParsed` consumers receive the resulting `RecoveryVerifiedEnvelope`.

- [ ] **Step 1: Write failing wire-shape tests before changing the codecs.**

  Serialize one typed document for each operation and assert exact members `format`, `version`, `operation`, `bodyVersion`, `operationId`, `scope`, `state`, `createdAt`, `updatedAt`, and `body`; assert append carries its canonical `address` and restore/file-entity omit it because their bodies supply entity identity. Assert the common format is `scrinia.recovery`, common version is `1`, body versions are positive, timestamps are valid with `updatedAt >= createdAt`, and all state spellings are exactly lowercase. Assert missing body, wrong member types, duplicate identity members, invalid operation/state strings, and newer common/body versions throw typed malformed/unsupported failures before body decode.

- [ ] **Step 2: Run the wire tests to confirm they fail.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RecoveryEnvelopeTests" --no-restore`

  Expected: FAIL because the common document, converters, and generated metadata are not present.

- [ ] **Step 3: Implement generated envelope and body DTOs.**

   Use source-generated metadata for durable DTOs and exact lowercase wire spellings. The B5-owned `ParseVerifiedEnvelope` performs the sole bounded parse and common validation; typed consumers receive its already parsed result and never call a contextless `JsonSerializer` overload.

- [ ] **Step 4: Implement common validation without absorbing operation invariants.**

  Validate format/version/operation/bodyVersion/identity/scope/address/timestamps and reject unsupported versions before operation payload decode. Validate canonical `MemoryAddress` only when `address` exists; require file-entity identity in its body. Leave payload hashes, archive paths, staging names, pair rules, and operation-specific state transitions to the adapters.

- [ ] **Step 5: Add adapter normalization and preserve legacy bytes.**

  Add operation-specific adapter methods that parse current legacy documents, map their current states and fields into typed body DTOs plus common lifecycle state, and return an in-memory envelope marked `IsLegacy`. Reading must not rewrite the source. Preserve append archive/prune/hash fields, restore entries and payload hashes, and file-entity prior/committed snapshots and archive paths.

- [ ] **Step 6: Run round-trip and legacy normalization tests.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RecoveryEnvelopeTests|FullyQualifiedName~AppendJournalTests|FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~FileEntityTransactionTests" --no-restore`

  Expected: PASS for common round trips, each legacy normalization, exact lowercase wire values, invalid structure, unsupported versions, and unchanged legacy location/bytes.

- [ ] **Step 7: Commit and review the adapter boundary.**

  ```bash
  git add src/Scrinia.Core/RecoveryEnvelope.cs src/Scrinia.Core/AppendJournal.cs src/Scrinia.Core/RestoreJournal.cs src/Scrinia.Core/FileEntityTransactionCoordinator.cs src/Scrinia.Core/Formats/DurableFormats.cs tests/Scrinia.Tests/RecoveryEnvelopeTests.cs tests/Scrinia.Tests/AppendJournalTests.cs tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs tests/Scrinia.Tests/FileEntityTransactionTests.cs
  git commit -m "feat: add common recovery envelope adapters"
  ```

  Review gate: verify the envelope contains no append/restore/file-entity business transition logic and no legacy file is rewritten during parse or normalization.

## Task 3: Discovery and Deterministic Precedence

**Files:**
- Create: `src/Scrinia.Core/RecoveryDiscovery.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `tests/Scrinia.Tests/RecoveryDiscoveryTests.cs`
- Modify: `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs`
- Modify: `tests/Scrinia.Tests/CliRecoveryBlockedRouteTests.cs`

**Interfaces:**
- Consumes: Task 2 adapters; existing `GetStoreDirForScope`, `WithExclusiveLock`, `FileLock`, `ReadBoundedJournalStream`, `PathContainment`, and cache invalidation seams.
- Produces:

  ```csharp
  internal sealed class RecoveryDiscoveryCoordinator
  {
      internal RecoveryDiscoveryResult Discover(string scope, string storeDirectory);
      internal RecoveryOutcome AdmitNext(string scope, string storeDirectory,
          RecoveryDiscoveryResult discovery, CancellationToken cancellationToken);
  }
  ```

- [ ] **Step 1: Write failing discovery tests for exact locations and precedence.**

  Create restore, append, and file-entity candidates in one scope, shuffle the order returned by a test enumeration seam, and assert admission is always restore, append, file entity, then ordinal canonical subject and operation ID. Assert a filename/operation mismatch, duplicate subject claim, malformed JSON, unsafe path, wrong scope, ambiguous scope, or newer version returns a typed `RecoveryBlock` with no live mutation.

- [ ] **Step 2: Add B1 contamination regressions before implementation.**

  Put malformed and newer evidence in `local:notes`, trigger an ordinary failure in `local:other`, and assert the latter remains its ordinary failure with no recovery fields. Put an unqualified evidence filename outside the selected scope and assert it is not promoted. Put valid evidence in `local:notes` and assert `local:other` remains readable and writable.

- [ ] **Step 3: Run the discovery tests to confirm they fail.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RecoveryDiscoveryTests" --no-restore`

  Expected: FAIL because the coordinator and typed candidate classification do not exist.

- [ ] **Step 4: Implement exact-location discovery under the existing scope boundary.**

  Resolve the already-selected scope and inspect only `Path.Combine(storeDirectory, ".restore-journal.json")`, `Path.Combine(storeDirectory, ".append-journal.json")`, and `Directory.EnumerateFiles(storeDirectory, ".file-entity-*.recovery")`. Read each file with bounded input before deserializing; parse through the common codec and operation adapter; validate scope, canonical subject/entity identity, operation ID, filename, and every contained path. Do not inspect sibling scope directories.

- [ ] **Step 5: Implement deterministic sorting and blocking.**

  Sort by numeric operation precedence restore `0`, append `1`, file entity `2`, then ordinal canonical subject and ordinal operation ID. Treat multiple claims of the same operation/subject as ambiguous unless the adapter proves a deterministic legacy/B2 normalization relationship; retain all bytes and block the scope when no safe winner exists. Unknown safe identity fields are rendered as `unknown`, never copied into a path.

- [ ] **Step 6: Integrate admission into `FileMemoryStore` without changing lock order.**

  Invoke discovery/admission from the existing scope recovery boundary before persistent mutation and before cache-backed reads can return inconsistent state. Reuse the current exclusive recovery lock and shared read barrier. A blocking result stops only the selected scope; no global cache or barrier is added. Invalidate index, artifact, topic, and entity-fact caches only after verified replay, rollback, or publication.

- [ ] **Step 7: Run focused discovery and existing B1 lanes.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RecoveryDiscoveryTests|FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~CliRecoveryBlockedRouteTests" --no-restore`

  Expected: PASS, including shuffled enumeration determinism, restore-before-append-before-file entity, no mutation on blocked evidence, and all existing scope-contamination tests.

- [ ] **Step 8: Commit and hold the discovery review gate.**

  ```bash
  git add src/Scrinia.Core/RecoveryDiscovery.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/RecoveryDiscoveryTests.cs tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs tests/Scrinia.Tests/CliRecoveryBlockedRouteTests.cs
  git commit -m "feat: coordinate scope-local recovery discovery"
  ```

  Review gate: reject any directory-wide scan, workspace-wide barrier, filesystem-order precedence, or response that turns an unrelated failure into `RECOVERY_BLOCKED`.

## Task 4: Append Adapter

**Files:**
- Modify: `src/Scrinia.Core/AppendJournal.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `tests/Scrinia.Tests/AppendJournalTests.cs`
- Modify: `tests/Scrinia.Tests/AppendRecoveryTests.cs`
- Modify: `tests/Scrinia.Tests/AppendCleanupTests.cs`
- Modify: `tests/Scrinia.Tests/AppendDurabilityTests.cs`

**Interfaces:**
- Consumes: Task 2 envelope codec and Task 3 qualified candidate admission; existing `AppendTransaction`, `AppendJournalCodec`, append fault injector, append lock, and `RecoverAppendAsync` seam.
- Produces: append-specific typed consumption and replay behavior. The historical envelope-conversion signatures are superseded by B5's parse-once handoff: `AppendJournalCodec.ConsumeParsed(in RecoveryVerifiedEnvelope)` consumes an already parsed envelope and performs no I/O, JSON parsing, or duplicate validation.

- [ ] **Step 1: Add failing tests for append envelope state mapping.**

  For prepared, archive-in-progress, archived, new-pair-in-progress, committed, cleanup-pending, recovery-required, and rolled-back append bodies, assert the common state and operation-specific state preserve enough information to resume or roll back. Assert archive paths, prune candidates, old/new snapshots, hashes, address, scope, create flag, and operation ID survive a B2 round trip.

- [ ] **Step 2: Add failing append preflight and retained-byte tests.**

  Corrupt each member payload/hash/path/address, inject an unsupported version, and place a journal in the preserved `versions/.append-journal.json` location. Assert `AppendAsync` blocks before mutation, leaves all live members and journal bytes unchanged, and reports the validated journal operation ID. Assert a journal in another scope does not alter an ordinary append failure.

- [ ] **Step 3: Run the append tests to verify the new cases fail.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendJournalTests|FullyQualifiedName~AppendRecoveryTests|FullyQualifiedName~AppendCleanupTests|FullyQualifiedName~AppendDurabilityTests" --no-restore`

  Expected: FAIL only in the new envelope/discovery cases; existing B1 append cases must remain green before proceeding.

- [ ] **Step 4: Adapt append admission and replay to the envelope.**

  Replace append’s direct journal interpretation at the recovery boundary with the qualified adapter. Keep all current payload, SHA-256, path containment, prune-candidate, create-vs-append, and canonical address checks. Map operation-specific progress to common states without accepting illegal transitions; a committed or cleanup-pending append cannot be rolled back by a cleanup retry.

- [ ] **Step 5: Preserve B1 append lock and operation semantics.**

  Keep admission, recovery, and append mutation under the target scope’s exclusive append lock. Keep missing-target append on `StoreAsync` creation semantics with its journaled before-images. Keep `RecoverAppendAsync`’s locked public wrapper and unsafe already-locked body. Keep pre-commit rollback member-by-member and retain evidence until both raw states are verified; keep post-commit cleanup warning distinct from pre-admission `RECOVERY_BLOCKED`.

- [ ] **Step 6: Run the full append durability lane.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append" --no-restore`

  Expected: PASS for normal append/create, all rollback fault points, malformed/newer/path evidence, archive/prune recovery, journal-delete warning, explicit retry, and byte-for-byte retention.

- [ ] **Step 7: Commit and review append invariants.**

  ```bash
  git add src/Scrinia.Core/AppendJournal.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/AppendJournalTests.cs tests/Scrinia.Tests/AppendRecoveryTests.cs tests/Scrinia.Tests/AppendCleanupTests.cs tests/Scrinia.Tests/AppendDurabilityTests.cs
  git commit -m "feat: adapt append recovery to common envelope"
  ```

  Review gate: compare every existing append fault point and public outcome against B1; no adapter helper may omit append-specific validation.

## Task 5: Restore Adapter

**Files:**
- Modify: `src/Scrinia.Core/RestoreJournal.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs`
- Modify: `tests/Scrinia.Tests/Mcp/RestoreEnvelopeTests.cs`

**Interfaces:**
- Consumes: Task 2 restore body DTO/adapter and Task 3 scope-local coordinator; existing bundle import staging, `RestoreJournalTransition`, `ReadBoundedJournalStream`, cache invalidation, and snapshot logic.
- Produces: restore-specific typed consumption, replay, and cleanup behavior. The historical envelope-conversion signatures are superseded by B5's parse-once handoff: `RestoreJournalCodec.ConsumeParsed(in RecoveryVerifiedEnvelope)` consumes an already parsed envelope and performs no I/O, JSON parsing, or duplicate validation.

- [ ] **Step 1: Add failing restore adapter tests for legacy and B2 documents.**

  Assert current prepared/completed restore journals normalize and replay through the common envelope; assert a B2 body preserves every entry name, body/sidecar path, Base64 payload, hash, and transaction identity. Assert reading either format leaves original bytes untouched until verified cleanup.

- [ ] **Step 2: Add failing crash-boundary and staging tests.**

  Cover prepared-journal creation failure, first body write failure, sidecar write failure, verification failure, completed-journal cleanup failure, stale staging sweep, overlong names, traversal-producing names, wrong-scope entries, and cache invalidation after verified replay. Assert no live mutation on blocked preflight and no deletion of data created after the import snapshot.

- [ ] **Step 3: Run the restore lane to establish failures.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~RestoreEnvelopeTests" --no-restore`

  Expected: existing tests PASS and new B2 adapter cases FAIL until the adapter is wired.

- [ ] **Step 4: Adapt restore replay and cleanup.**

  Make the restore adapter own bundle/topic import staging, snapshot protection, live pair publication, destination verification, cache invalidation, and staging cleanup. It must validate names and paths before staging, write the common envelope only after all rollback/replay evidence is admitted, transition prepared to applying to committed, and retain cleanup-pending evidence after verified completion.

- [ ] **Step 5: Preserve the current recovery barrier and re-entry rule.**

  Keep readers and writers scoped to the selected topic scope, keep unrelated scopes available, and allow a completed-journal cleanup retry to verify destinations and proceed without recursively re-entering an active barrier. Invalidate caches after verified publication/replay and before returning a cache-backed read.

- [ ] **Step 6: Run restore, cache, and scope gates.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~RestoreEnvelopeTests|FullyQualifiedName~CliRecoveryBlockedRouteTests" --no-restore`

  Expected: PASS for legacy/B2 replay, crash boundaries, cleanup retry, cache correctness, scope blocking, byte retention, and MCP restore envelope tests.

- [ ] **Step 7: Commit and review restore isolation.**

  ```bash
  git add src/Scrinia.Core/RestoreJournal.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs tests/Scrinia.Tests/Mcp/RestoreEnvelopeTests.cs
  git commit -m "feat: adapt restore recovery to common envelope"
  ```

  Review gate: reject changes that rewrite legacy restore evidence during read, broaden the barrier beyond one scope, or allow staging names to reach the filesystem before containment validation.

## Task 6: File-Entity Adapter

**Files:**
- Modify: `src/Scrinia.Core/FileEntityTransactionCoordinator.cs`
- Modify: `src/Scrinia.Core/WholeMemoryTransaction.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `tests/Scrinia.Tests/FileEntityTransactionTests.cs`
- Modify: `tests/Scrinia.Tests/AgentFileEntityTests.cs`

**Interfaces:**
- Consumes: Task 2 typed file-entity body and Task 3 qualified discovery; existing `FileEntityMutation`, `IFileEntityTransactionCoordinator`, `DurableFile`, `PathContainment`, and cache invalidation.
- Produces: file-entity-specific typed consumption, pair recovery, and cleanup behavior. The historical envelope-conversion signatures are superseded by B5's parse-once handoff: `FileEntityRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope)` consumes an already parsed envelope and performs no I/O, JSON parsing, or duplicate validation.

- [ ] **Step 1: Add failing file-entity envelope and path tests.**

  Assert the body records body/meta paths, prior snapshots, hashes, publication status, archive/cleanup paths, entity kind/name, and operation ID. Assert traversal, absolute escape, wrong-scope, Windows reparse-point escape, Unix symlink escape, malformed identity, and mismatched filename/identity evidence blocks before touching either live member.

- [ ] **Step 2: Add failing pair publication and committed-cleanup tests.**

  Inject body write, metadata write, verification, rollback, archive, and evidence cleanup failures. Assert partial publication restores both members byte-for-byte or retains typed recovery evidence; verified publication followed by cleanup failure returns a warning and leaves evidence; explicit retry removes only validated evidence without republishing or reverting the live pair.

- [ ] **Step 3: Run the file-entity tests to verify the new cases fail.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FileEntityTransactionTests|FullyQualifiedName~AgentFileEntityTests" --no-restore`

  Expected: existing behavior remains green and new common-envelope/retry assertions fail before implementation.

- [ ] **Step 4: Replace binary evidence publication with the common JSON envelope body at the same path.**

  Keep the exact `.file-entity-{operationId}.recovery` name and entity directory. Serialize the typed body through generated metadata, use `DurableFile.AtomicWriteAllBytes`, and retain prior/committed snapshots and archive paths. Do not change ordinary skill/agent sidecar JSON or body formats.

- [ ] **Step 5: Implement scope/entity containment and pair verification.**

  Canonicalize every deserialized body/meta/archive path and require it to remain within the expected entity directory and known versions directory. Verify both members as one logical result, delete only operation-owned validated paths, and invalidate affected caches only after verified rollback/publication.

- [ ] **Step 6: Run file-entity and sidecar compatibility tests.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FileEntityTransactionTests|FullyQualifiedName~AgentFileEntityTests|FullyQualifiedName~Skill" --no-restore`

  Expected: PASS for body/meta pair publication, metadata-only publication, archive behavior, unknown sidecar field preservation, malformed/newer sidecars, rollback, retained evidence, and cleanup retry.

- [ ] **Step 7: Commit and review the no-sidecar-change boundary.**

  ```bash
  git add src/Scrinia.Core/FileEntityTransactionCoordinator.cs src/Scrinia.Core/WholeMemoryTransaction.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/FileEntityTransactionTests.cs tests/Scrinia.Tests/AgentFileEntityTests.cs
  git commit -m "feat: adapt file entity recovery to common envelope"
  ```

  Review gate: reject any change to ordinary `.meta.json` serialization, any evidence path outside the entity operation’s owned set, or any rollback after a verified committed publication.

## Task 7: Mixed Versions, Cleanup Retry, and Recovery Errors

**Files:**
- Modify: `src/Scrinia.Core/RecoveryDiscovery.cs`
- Modify: `src/Scrinia.Core/RecoveryEnvelope.cs`
- Modify: `src/Scrinia.Core/AppendJournal.cs`
- Modify: `src/Scrinia.Core/RestoreJournal.cs`
- Modify: `src/Scrinia.Core/FileEntityTransactionCoordinator.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Create: `tests/Scrinia.Tests/MixedVersionRecoveryTests.cs`
- Modify: `tests/Scrinia.Tests/RecoveryDiscoveryTests.cs`

**Interfaces:**
- Consumes: Tasks 2-6 adapters and typed discovery outcomes.
- Produces: explicit adapter retry behavior through `RecoveryOutcome`, with no new public command; `RecoveryDiscoveryCoordinator.AdmitNext` must be idempotent for committed cleanup evidence.

- [ ] **Step 1: Write failing mixed-version tests.**

  Put a supported legacy candidate and supported B2 candidate in one scope, assert deterministic precedence and conflict detection. Put newer common and newer body versions in separate scopes, assert only the qualified scope blocks, bytes remain unchanged, and the result exposes the safe operation ID only when the scope/identity is validated. Put a legacy candidate that cannot map a unique subject and assert blocking rather than silent ignore.

- [ ] **Step 2: Write failing cleanup retry tests for all operations.**

  Force evidence deletion to fail after a verified append, restore, and file-entity commit. Assert the current operation continues with a typed warning, evidence remains byte-for-byte, and a later explicit retry verifies live state, removes evidence, does not reapply business mutation, and converges to no recovery file. Force one cleanup member to fail and assert all other operation-owned members retain safe retry evidence.

- [ ] **Step 3: Run the mixed-version tests to confirm failures.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MixedVersionRecoveryTests" --no-restore`

  Expected: FAIL in the new mixed-version and three-operation retry cases.

- [ ] **Step 4: Implement per-candidate normalization and conservative conflict handling.**

  Parse each candidate independently, retain legacy source bytes, apply the fixed operation precedence, and block the affected scope on an unsupported/unsafe/ambiguous candidate. Do not convert legacy evidence in place. Ensure a supported B2 candidate cannot hide a conflicting legacy candidate for the same operation/subject.

- [ ] **Step 5: Implement idempotent post-commit retry.**

  Each adapter must verify its committed live result before cleanup, delete only validated operation-owned members, retain evidence on any deletion failure, and return `CleanupPending` warning data. Retry must not enter pre-commit replay or rollback and must not invalidate caches before verified state.

- [ ] **Step 6: Run mixed-version, retry, and all existing recovery tests.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MixedVersionRecoveryTests|FullyQualifiedName~Recovery|FullyQualifiedName~Append|FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~FileEntityTransactionTests" --no-restore`

  Expected: PASS with no regressions in B1 append locking, restore scope barriers, file-entity pair recovery, or cleanup warnings.

- [ ] **Step 7: Commit and review retry classification.**

  ```bash
  git add src/Scrinia.Core/RecoveryDiscovery.cs src/Scrinia.Core/RecoveryEnvelope.cs src/Scrinia.Core/AppendJournal.cs src/Scrinia.Core/RestoreJournal.cs src/Scrinia.Core/FileEntityTransactionCoordinator.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/MixedVersionRecoveryTests.cs tests/Scrinia.Tests/RecoveryDiscoveryTests.cs
  git commit -m "feat: harden mixed-version recovery retry"
  ```

  Review gate: verify `RECOVERY_BLOCKED`, `RECOVERY_REQUIRED`, and post-commit warning remain distinct and no retained evidence is discarded merely because a newer or malformed candidate was encountered.

## Task 8: MCP and CLI Facades and Operation Wiring

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs`
- Modify: `src/Scrinia.Mcp/McpResponse.cs`
- Modify: `src/Scrinia/Commands/ScriniaCommands.cs`
- Modify: `src/Scrinia/Commands/CliJsonContext.cs`
- Create: `tests/Scrinia.Tests/RecoveryFacadeMappingTests.cs`
- Modify: `tests/Scrinia.Tests/McpRecoveryBlockedRouteTests.cs`
- Modify: `tests/Scrinia.Tests/CliRecoveryBlockedRouteTests.cs`
- Modify: `tests/Scrinia.Tests/AppendFacadeMappingTests.cs`

**Interfaces:**
- Consumes: typed Core `RecoveryBlock`, `RecoveryOutcome`, `AppendStoreResult`, `WholeMemoryResult`, and existing operation IDs/scopes.
- Produces: unchanged public `append`, restore/import, file-entity, CLI memory command names and response wrappers; all recovery mapping must consume typed properties rather than `Exception.Message`.

- [ ] **Step 1: Write failing MCP/CLI contract tests for every response class.**

  Assert exact YAML/JSON fields for malformed, unsafe, newer, ambiguous, and internally inconsistent blocked evidence: status `error`, code `RECOVERY_BLOCKED`, scope, evidence path, safe operation ID or `unknown`, reason/action guidance, and retained-evidence information. Assert pre-admission append recovery maps to `RECOVERY_BLOCKED`, ordinary append recovery maps to `RECOVERY_REQUIRED`, and verified commit cleanup failure stays `success_with_warning` with recovery details. Assert restore and file-entity use the same typed shape.

- [ ] **Step 2: Run facade tests to confirm failures.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RecoveryFacadeMappingTests|FullyQualifiedName~McpRecoveryBlockedRouteTests|FullyQualifiedName~CliRecoveryBlockedRouteTests|FullyQualifiedName~AppendFacadeMappingTests" --no-restore`

  Expected: FAIL for new common operation/reason fields or incorrect exception-derived mapping cases.

- [ ] **Step 3: Map typed Core outcomes in MCP.**

  Update append, restore/import, facts/list/recall, and skill/agent file-entity paths to handle typed recovery outcomes. Use the validated evidence operation ID, scope, and safe evidence path. Do not scan arbitrary directories, promote `INTERNAL`, or parse exception messages. Preserve existing successful payload and warning behavior.

- [ ] **Step 4: Map typed Core outcomes in CLI.**

  Keep `EmitMcpResult`, `EmitRecoveryBlocked`, `CliMcpOutput`, JSON/YAML output, and existing exit-class behavior. Render the typed Core response through the source-generated CLI context; do not infer recovery from a file’s existence, expose an untrusted path as a destination, or change command names.

- [ ] **Step 5: Register response metadata in source-generated contexts.**

  Add every new serialized response/result DTO and enum to `ScriniaMcpJsonContext` and `CliJsonContext`. Use the existing `CreateToolSerializerOptions` resolver chain for MCP parameter marshalling and verify no contextless serialization is introduced.

- [ ] **Step 6: Run facade, trimming, and surface tests.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RecoveryFacadeMappingTests|FullyQualifiedName~McpRecoveryBlockedRouteTests|FullyQualifiedName~CliRecoveryBlockedRouteTests|FullyQualifiedName~AppendFacadeMappingTests|FullyQualifiedName~CliSurfaceConformanceTests" --no-restore`

  Expected: PASS with exact typed fields, unchanged public names, correct exit behavior, and no reflection fallback in serialization.

- [ ] **Step 7: Commit and review the public compatibility gate.**

  ```bash
  git add src/Scrinia.Mcp/MemoryTools.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia.Mcp/McpResponse.cs src/Scrinia/Commands/ScriniaCommands.cs src/Scrinia/Commands/CliJsonContext.cs tests/Scrinia.Tests/RecoveryFacadeMappingTests.cs tests/Scrinia.Tests/McpRecoveryBlockedRouteTests.cs tests/Scrinia.Tests/CliRecoveryBlockedRouteTests.cs tests/Scrinia.Tests/AppendFacadeMappingTests.cs
  git commit -m "feat: map converged recovery through public facades"
  ```

  Review gate: compare MCP and CLI output against pre-B2 golden responses; reject any mapping that trusts unvalidated path, scope, operation ID, or exception text.

## Task 9: Final Compatibility and Acceptance Gate

**Files:**
- Create: `tests/Scrinia.Tests/RecoverySerializationTests.cs`
- Modify: `tests/Scrinia.Tests/RecoveryEnvelopeTests.cs`
- Modify: `tests/Scrinia.Tests/RecoveryDiscoveryTests.cs`
- Modify: `tests/Scrinia.Tests/MixedVersionRecoveryTests.cs`
- Modify: `tests/Scrinia.Tests/RecoveryFacadeMappingTests.cs`
- No production changes are permitted unless a failing acceptance test identifies a concrete inconsistency with the preceding tasks.

**Interfaces:**
- Consumes: all Task 1-8 contracts, adapters, discovery, store integration, MCP/CLI mappings, and existing test helpers.
- Produces: named acceptance evidence for all ten spec acceptance gates and a clean compatibility result.

- [ ] **Step 1: Add serialization and trimming-safe tests.**

  Round-trip every operation body and common envelope through its generated `JsonTypeInfo`; reject missing/extra required structure, malformed types, invalid lowercase values, duplicate identity members, common version `2`, and each body version above support. Assert a reflection-disabled test path still serializes and parses all durable recovery DTOs.

- [ ] **Step 2: Add the full acceptance matrix as explicit tests.**

  Name tests for: every operation and supported legacy round trip; shuffled enumeration; restore-before-append-before-file entity; unrelated/malformed/unsafe/newer/ambiguous scope isolation; no mutation and byte retention on blocked preflight; verified commit plus failed cleanup plus successful retry for all three operations; partial publication rollback and cache invalidation for all three operations; exact MCP/CLI blocked/required/warning envelopes; and the B1 contamination ruling.

- [ ] **Step 3: Run focused B2 verification.**

  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Recovery|FullyQualifiedName~Append|FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~FileEntityTransactionTests|FullyQualifiedName~McpRecoveryBlockedRouteTests|FullyQualifiedName~CliRecoveryBlockedRouteTests" --no-restore`

  Expected: PASS with zero failed tests and no test-created recovery evidence left in the fixture workspace.

- [ ] **Step 4: Run source-generation, build, and complete regression verification.**

  Run: `dotnet build Scrinia.sln --no-restore`

  Expected: PASS with zero errors and no source-generation warnings indicating a missing DTO registration.

  Run: `dotnet test Scrinia.sln --no-restore`

  Expected: PASS with zero failed tests; the default filter continues to exclude only the existing opt-in long-running benchmark category.

  Run: `git diff --check 4dd315e..HEAD`

  Expected: no whitespace errors across the B2 implementation commits that follow the approved design commit `4dd315e`.

- [ ] **Step 5: Inspect compatibility evidence and changed paths.**

  Verify with `git status --short` and `git diff --name-only 4dd315e..HEAD` that only the planned Core, MCP, CLI, and test files changed; no NMP/2, ordinary sidecar, unrelated index, planning, or documentation file changed. Re-read the spec’s compatibility gates at lines 229-257 and match each one to a passing named test or command result.

- [ ] **Step 6: Commit only the final test additions or concrete gate fix.**

  ```bash
  git add tests/Scrinia.Tests/RecoverySerializationTests.cs tests/Scrinia.Tests/RecoveryEnvelopeTests.cs tests/Scrinia.Tests/RecoveryDiscoveryTests.cs tests/Scrinia.Tests/MixedVersionRecoveryTests.cs tests/Scrinia.Tests/RecoveryFacadeMappingTests.cs
  git commit -m "test: add b2 recovery compatibility gate"
  ```

  Review gate: the implementation is not ready for merge unless all ten acceptance requirements have named passing evidence, all three legacy locations remain byte-preserved until verified cleanup, no unrelated scope is blocked, and the full solution suite/build/whitespace checks pass.

## Compatibility, Migration, Error, and Recovery Behavior

- Legacy append, restore, and file-entity evidence is read and normalized in memory at its preserved location. No read path rewrites it merely to obtain the common shape.
- A supported B2 and supported legacy candidate may coexist. Discovery parses each independently, applies restore/append/file-entity precedence, and blocks same-subject conflicts rather than selecting by timestamp or enumeration order.
- A newer common or body version blocks only the candidate’s safely qualified scope and retains original bytes. If scope or identity cannot be safely established, response fields are conservative and the file is never promoted into another scope.
- Malformed, unsafe, ambiguous, or internally inconsistent evidence blocks before live mutation. Existing live members and retained evidence are compared byte-for-byte in tests.
- Pre-commit failures use operation-specific rollback evidence and retain a typed `RECOVERY_REQUIRED` result when automatic rollback cannot be verified.
- Verified committed operations can return a post-commit cleanup warning. Cleanup retry verifies the live result and removes only validated operation-owned evidence; it never replays business mutation or rolls back a verified commit.
- Recovery results carry the validated evidence operation ID, scope, path, reason category, and action-needed instruction. A caller ID may remain as request context only when it does not replace safe evidence identity.
- All recovery coordination is scope-local. No cross-scope transaction, workspace-wide barrier, background GC, fleet report, or daemon is introduced.
- Cache invalidation occurs only after verified replay, rollback, or publication and uses canonical scope plus subject/entity identity. B1’s existing append lock and restore barrier ordering remain unchanged.

## Explicit Exclusions

The implementation must not modify NMP/2 encoding or decoding, ordinary artifact/index/metadata/sidecar formats, sidecar migration, physical recovery filenames or directories, normal operation semantics, MCP/CLI names, cross-scope transactions, workspace-wide transaction barriers, background garbage collection, fleet reporting, daemon behavior, HTTP/server surfaces, or unrelated schemas. It must not make legacy evidence writable in place solely for normalization and must not worsen B1 scope contamination.

## Plan Self-Review

- **Spec coverage:** Tasks 1-2 cover the common envelope, versions, exact wire strings, source generation, typed contracts, and all three operation bodies. Task 3 covers exact locations, qualification, deterministic precedence, ambiguity, scope-local blocking, and B1 contamination. Tasks 4-6 cover append, restore, and file-entity invariants, rollback, cache invalidation, and preserved locations. Task 7 covers legacy/mixed-version parsing, newer evidence, cleanup retry, and retained bytes. Task 8 covers MCP/CLI mappings and public compatibility. Task 9 covers all ten acceptance gates, build, full tests, source generation, and whitespace checks.
- **Placeholder scan:** No step delegates unspecified behavior to a future decision; every error class, path, state, command, expected result, and review gate is specified here.
- **Type consistency:** `RecoveryEnvelopeDocument` and its live `JsonElement Body` are retained at HEAD, not historical; only the original adapter `ParseAndNormalize` parser signature is historical only. HEAD uses B5's single six-argument `ParseVerifiedEnvelope` and I/O-, parse-, validation-free `ConsumeParsed` consumers (the typed codec `ConsumeParsed` and the `IRecoveryAdapter.ConsumeParsed` adapter boundary); there is no `GetOperationId` at HEAD, and no later task depends on untyped serialization or a second parser.
- **Boundary check:** The common layer owns discovery and coarse lifecycle only; append, restore, and file-entity adapters retain their own payload, path, snapshot, state, staging, archive, and pair invariants. No ordinary sidecar or NMP/2 migration is hidden in an adapter.
- **Location check:** `.append-journal.json`, `.restore-journal.json`, and `.file-entity-*.recovery` are preserved exactly, including the append `versions/` location already used by B1.
