# B1 Single-Memory Write Coverage — Design Specification

**Status:** DRAFT
**Date:** 2026-09-18
**Predecessor:** `docs/superpowers/specs/2026-09-17-b0-transaction-foundation-design.md`
**Parent:** `docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md`
**Scope authority:** Phase B roadmap §4 (B1 boundary, exclusions, and required decisions), §5 (dependencies), §7 (compatibility constraints), §8 (acceptance gates), §11 (Phase B blocker checklist).

This is the B1 child spec under the Phase B roadmap. It applies the B0 transaction discipline to
every single-memory write operation that B0 does not itself change: `remember`, `compact`, import of
one memory, migration of one memory, bundle writes that publish one memory, and arbitrary sidecar
mutations. B1 adds the transaction-discipline *contract* and the Core→MCP→CLI result *mapping* for
each operation. It does not change the behavior of any of them. The roadmap §4 B1 boundary is the
authority for what B1 may and may not do, and the roadmap §5 dependency graph places B1 after B0: B1
consumes the B0 vocabulary, fault seam, and compatibility harness and may not redefine them.

## 1. Goal, authority, and scope

B1 extends the append transaction protocol (Phase A) from append to the remaining single-memory
write boundaries. Each of the six operations already carries its own coordinator, journal, archive
plan, and recovery path; B1's job is to make those paths *explicitly declarable* — one commit
boundary per operation, one refuse-before-any-side-effect ordering for import and bundle, one
archive-and-prune path for compact, one artifact↔sidecar pairing gate for sidecar writes, one
warning-vs-recovery-required classification for every Core outcome and exception, and one B0Result
mapping for each — without rewriting any of them. The roadmap §4 B1 deliverable is exactly this:
"each operation must retain its existing semantic contract while gaining explicit commit, rollback,
recovery, and warning behavior."

Scope authority for this spec is the Phase B roadmap. Every non-goal in Section 2, every compatibility
constraint in Section 12, every invariant in Section 13, and every acceptance gate in Section 15
restates a roadmap clause. B1 does not extend the roadmap. It makes the roadmap's B1 boundary
executable against the six operations.

## 2. Goal and non-goals

### Goal

Define the single-memory transaction discipline that B0's foundation feeds into B3–B6, and prove it
against `remember`, `compact`, import of one memory, migration of one memory, bundle writes that
publish one memory, and arbitrary sidecar mutations. B1 locks:

- one commit boundary per operation, adopted from the existing coordinators (Section 5);
- one refuse-before-any-side-effect ordering for import and bundle export, plus one transactional
  staging discipline for bundle publish (Section 6);
- the preserved compact archive-and-prune path and its `SuccessWithWarning` warning (Section 7);
- the preserved artifact↔sidecar pairing gate and its warning-vs-recovery classification
  (Section 8);
- one precise B1 classification rule mapping every Core outcome and exception to a `B0Outcome` and
  `B0Failure` through the existing enum members only (Section 9);
- the preserved layout-migration quarantine/resume, attribute-migration forward-fill, and
  tracking-policy config rollback, plus one `B0Result` mapping for the layout-migration result
  (Section 10).

B1 adds internal result contracts and result-mapping facades only. It changes no public MCP or CLI
contract, adds no durable file, journal, state, migration, archive rule, or recovery outcome, and
introduces no new outcome into any existing enum.

### Non-goals

- **Zero behavior change except three justified changes.** The only permitted behavior changes are the
  minimal per-memory import result type and its `B0Result` mapping in Section 6/11 (justified in
  Section 13), the aggregate layout-migration `B0Result` mapping in Section 10 (justified in Section
  13), and the bundle-export refuse-before-write + cleanup-on-failure discipline in Section 6.3
  (justified in Section 13). Every other section preserves existing behavior exactly.
- **No public MCP/CLI contract change.** `McpResponse`, its `ErrorCodes`, the CLI facade, and all
  public result shapes remain unchanged. B1 maps through the existing `StoreWithTransactionResultAsync`
  and `FileEntityMutationResult` facades; it adds no error code and changes no public result shape.
- **No new durable file, journal, state, migration, archive rule, or recovery outcome.** The sole
  parser boundary in Section 4 (B5-owned) is untouched. B1's new records are in-memory Core result
  types carried only at the result boundary, like `B0Result` and `RecoveryMember`.
- **No cross-memory atomicity.** B1 stays inside single-memory operations. It consumes no
  `MultiMemoryTransaction` contract and introduces no multi-scope journal. That work belongs to B3.
- **No shared-journal migration.** B1 does not unify append and restore journals. That work belongs
  to B2. B1 treats the two journals as distinct durable formats and maps each through its own
  coordinator.
- **No external-plugin guarantee.** B1 does not make plugin hooks, embedding/index providers, or
  event sinks transactional. It observes the existing event-sink fire that follows `remember` and
  `compact` and maps its observable result; it does not advertise a plugin guarantee. That work
  belongs to B4.
- **No NMP/2 or sidecar schema change.** B1 preserves NMP/2, sidecar schemas, and archive layout
  byte-for-byte. That work belongs to B6.
- **No reuse of B3–B6 or shared-base contracts.** B1 stays within single-memory operations and the
  B0 vocabulary. It does not consume multi-memory, retention, or shared-base results.

## 3. Locked decisions

The following six decisions are locked and user-approved. B1 implements them exactly. Any deviation
requires a new approved decision. They restate the roadmap §4 "Required decisions" verbatim.

1. **Commit boundary is the existing coordinator, not a new one.** B1 adopts the four existing
   coordinators as the single commit boundary for the six operations (Section 5). It does not
   introduce a new coordinator or durable file.
2. **Import and bundle publish refuse before any side effect; one memory = one journal scope.** B1
   preserves the Build-pass→Write-pass ordering for import and adds a transactional staging +
   per-topic commit + cleanup-on-failure discipline for bundle export (Section 6).
3. **Compact preserves its existing ArchivePlan archive and prune path; `SuccessWithWarning` is the
   explicit warning.** B1 does not invent an archive or prune rule (Section 7).
4. **Sidecar pairing preserves `IsReadOnly` + whole-memory artifact↔sidecar pairing.** B1 adds only
   the warning-vs-recovery-required classification for sidecar-only writes (Section 8).
5. **Warning vs recovery-required classification uses the existing enum members only.** B1 maps each
   Core outcome and exception to a `B0Outcome` and `B0Failure` through `Created`/`Replaced`/
   `Appended`/`Committed`/`SuccessWithWarning`/`RecoveryRequired`; it does not invent new outcomes
   (Section 9).
6. **Migration preserves quarantine/resume, forward-fill, and config rollback; layout-migration result
   gains a `B0Result` mapping.** B1 adds only the transaction boundary and result mapping for the
   layout-migration result (Section 10).

## 4. Current foundation map

The table below is the authoritative evidence inventory for B1's base. Line numbers are review
anchors; symbol names and call purpose are authoritative. All coordinators, journals, and migrations
live in the `Scrinia.Core` namespace unless noted.

### 4.1 Remember (whole-memory publish)

| Symbol | Location | Evidence |
|---|---|---|
| `WholeMemoryTransactionCoordinator` | `src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs` | `ExecuteAsync` lines 16–119: stage `Prepared` journal → `ArchiveInProgress` → `Archived` → `NewPairInProgress` → `Committed` (SHA-256 verify) → prune; `TryRollback` line 142 restores old artifact+sidecar, or `RecoveryRequired` line 61 if rollback unprovable; `SuccessWithWarning` lines 80 and 105 when commit succeeded but cleanup pending. |
| `WholeMemoryResult` | `src/Scrinia.Core/WholeMemoryTransaction.cs:108` | `(WholeMemoryOutcome, ArtifactEntry?, OperationId, Warning?, RecoveryInfo?)`. |
| `WholeMemoryOutcome` | `WholeMemoryTransaction.cs:100` | `Created`, `Replaced`, `SuccessWithWarning`, `RecoveryRequired`. |
| `RecoveryInfo` | `WholeMemoryTransaction.cs:192` | `(EvidencePath, State, Action, Detail)`. |
| `WholeMemoryTransactionException` | `WholeMemoryTransaction.cs:145` | `: IOException`; `WholeMemoryErrorCode` (line 170: `RecoveryRequired`, `Internal`). |
| `StoreWithTransactionResultAsync` | `src/Scrinia.Core/FileMemoryStore.cs:2374` | Returns `(ArtifactEntry, WholeMemoryResult?)`; the typed-result path the remember facade reads. |
| `StorePersistentAsync` | `FileMemoryStore.cs:2602` | Builds the `WholeMemoryTransaction` + `ArchivePlan` and drives the coordinator through `ExecuteAsync`. |
| `Remember` (facade) | `src/Scrinia.Mcp/MemoryTools.Core.cs:245` | Routes `/agent/*` through `PublishFileEntityAsync` (file-entity) and every other persistent path through `StoreWithTransactionResultAsync`; promotes `SuccessWithWarning` to a `success_with_warning` response (lines 383–393). |

### 4.2 Compact (whole-memory rewrite)

| Symbol | Location | Evidence |
|---|---|---|
| `Compact` | `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:74` | Re-encodes merged chunks and re-stores via the SAME `StoreWithTransactionResultAsync` path as remember (lines 276–284). |
| Compact warning promotion | `MemoryTools.Lifecycle.cs:300` | Promotes `SuccessWithWarning` to a `success_with_warning` response (lines 300–314). |
| Archive behavior | `MemoryTools.Lifecycle.cs:226–229` | Compaction is a whole-memory rewrite; the store archives the previous version itself — the explicit `ArchiveVersion` the method used to call first is gone, so calling it again would archive twice. |

Compact returns `Task<McpResponse>` (not a typed Core result). No per-memory typed result flows up
from compact today; the `WholeMemoryResult?` it receives from `StoreWithTransactionResultAsync`
drives only the `success_with_warning` branch.

### 4.3 Import of one topic (restore-journal publish)

| Symbol | Location | Evidence |
|---|---|---|
| `ImportTopicsFromZip` | `src/Scrinia.Core/Bundles/BundleFormatService.cs:166` | Build pass (validate ALL entries before any write) then Write pass. The Build-pass comment sits at line 232; the Write-pass loop at lines 487–498 calls `store.ImportTopicEntries`. |
| `ImportTopicEntries` | `src/Scrinia.Core/FileMemoryStore.cs:4600` | `void`; throws typed exceptions. |
| `ImportTopicEntriesCore` | `FileMemoryStore.cs:4618` | Pre-validates subjects (refuse before any side effect), takes `WithExclusiveLock`, cold-reads index, refuses read-only scope, builds candidates, stages a `Prepared` `RestoreJournal` (`RestoreJournal.cs:17–32`), `ApplyPreparedJournalUnsafe` (line 4711) writes bodies+sidecars, verifies SHA256, transitions `Completed`, then invalidates cache + deletes completed journal. |
| `RestoreJournal` | `src/Scrinia.Core/RestoreJournal.cs:17` | `(Format, Version, State, TransactionId, Scope, Entries[])`; `State` is `Prepared`/`Completed` (`RestoreJournalState`). |
| `RecoverScopeUnsafe` | `FileMemoryStore.cs:4727` | Replays `Prepared`/`Completed` journals on next access when write pass did not complete. |
| Failure surface | `FileMemoryStore.cs` via `RestoreRecoveryException` | Surfaces as `RestoreRecoveryException` → `RecoveryBlockedResponse`; no per-memory result type exists — import is `void` at the store boundary. |

### 4.4 Bundle export / publish

| Symbol | Location | Evidence |
|---|---|---|
| `ExportTopicsToZip` | `src/Scrinia.Core/Bundles/BundleFormatService.cs:59` | Per topic `WriteTopic` (line 110) writes topic index + `.nmp2` bodies; `WriteManifest` (line 137) sets `BundleManifest.Version = hasFileEntities ? 2 : 1`. |
| `WriteFileEntities` | `BundleFormatService.cs:732` | Writes file entities with NO journaled recovery (the comment at line 730 so states it). |
| `Export` (CLI) | `src/Scrinia.Mcp/MemoryTools.Bundling.cs:60` | `scri export` deletes a half-created bundle on empty export, with no rollback. |

### 4.5 File-entity sidecar writes

| Symbol | Location | Evidence |
|---|---|---|
| `WriteSidecarMeta<T>` | `src/Scrinia.Mcp/MemoryTools.Skills.cs:212` | → `FileEntityTransactionCoordinator.ExecuteAsync` (line 19) publishes body+meta as a recoverable pair, returns `FileEntityMutationResult`. |
| `FileEntityMutationResult` | `src/Scrinia.Core/WholeMemoryTransaction.cs:292` | `(WholeMemoryOutcome, OperationId, Warning?, RecoveryInfo?)`. |
| `FileEntityTransactionCoordinator` | `src/Scrinia.Core/FileEntityTransactionCoordinator.cs:19` | `ExecuteAsync` lines 19–128: capture → write-prepared-evidence → archive → publish → verify → write-committed-evidence → delete-evidence; `RecoverPendingEvidence` first (line 34) returns retained `RecoveryRequired` before any mutation. |
| `ArtifactSidecarPolicy.IsReadOnly` | `src/Scrinia.Core/Formats/FormatVersion.cs:43` | The sole "cannot rewrite" version gate. |

### 4.6 Migration (per-memory)

| Symbol | Location | Evidence |
|---|---|---|
| `StoreLayoutMigration` | `src/Scrinia.Core/StoreLayoutMigration.cs` | `MigrateIfNeeded` line 173 auto-on-first-access, `Migrate` line 177 backs `scri migrate`; returns `Result(Moved, Conflicts, Ran, BackupPath?, BackupSkipped?)`. |
| Single-memory move | `StoreLayoutMigration.cs:276` | `foreach (var (source, target, _) in plan)`; `MoveWithRetry` (line 526) retries transient sharing violations; conflicts quarantined to `.scrinia-conflicts-*/`; `index.json` expanded then deleted; NO rollback, resume-on-interruption. |
| `AttributeMigration` | `src/Scrinia.Core/Migration/AttributeMigration.cs` | Forward-fills `fields`→`attributes`, idempotent; `MigrateStore` (line 157) best-effort write-back swallows I/O. |
| Migration write-back swallow | `src/Scrinia.Core/FileMemoryStore.cs:1506` | `try { WriteSidecar(upgraded, storeDir); } catch (...) { /* best-effort */ }`. |
| `LegacyMigrationService` (tracking policy) | `src/Scrinia.Core/Migration/LegacyMigrationState.cs` | Migrates the TRACKING POLICY (config.json/.gitattributes/merge.config), NOT per-memory — do NOT conflate with memory migration. Its `Migrate` (line 227) IS a locked config transaction with `TryRestore` (line 509) rollback of 4 config files. |

### 4.7 B0 foundation consumed (committed; see project memory `/findings/b0-transaction-foundation-complete`)

| Symbol | Location | Evidence |
|---|---|---|
| `B0Result` | `src/Scrinia.Core/B0Result.cs:6` | `(B0Outcome, B0Failure?, ArtifactEntry?, OperationId, CorrelationId, RecoveryInfo?, Warning?)`. |
| `B0ResultWrappers` | `B0Result.cs:22` | `FromAppend`, `FromWhole`, `FromMulti`, `FromRecovery`. |
| `B0Outcome` | `src/Scrinia.Core/B0Vocabulary.cs` | `Created`, `Appended`, `Replaced`, `Committed`, `CommittedCleanupPending`, `RolledBack`, `Rejected`, `SuccessWithWarning`, `RecoveryRequired`. |
| `B0Failure` | `B0Vocabulary.cs` | `(B0FailureTaxonomy, Code, Detail)`. |
| `RecoveryMember` | `src/Scrinia.Core/RecoveryMember.cs` | Typed carrier (the one justified change in B0), carried only at result boundary, never written to disk. |
| `RecoveryLifecycleStateMapper` | `src/Scrinia.Core/RecoveryLifecycleStateMapper.cs` | Read-only projection. |
| `B0FaultSeam` | `src/Scrinia.Core/B0FaultSeam.cs` | Unified fault-injection contract over the twelve seams. |
| `DurableFormats.Registry` | `src/Scrinia.Core/Formats/DurableFormats.cs:15` | Single source of truth. |

### 4.8 B0 do-not-touch anchors (B5-owned, NEVER modify)

The sole parser boundary is `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` in
`src/Scrinia.Core/RecoveryVerification.cs:299` (six args); all typed `ConsumeParsed` consumers;
`JsonElement Body`. Also the four authoritative state machines `AppendJournal.cs`,
`WholeMemoryTransaction.cs`, `MultiMemoryTransaction.cs`, `RecoveryEnvelope.cs`, `RecoveryContracts.cs`,
and `AppendContracts.cs`; `McpResponse.cs:10` `ErrorCodes`; `CliJsonContext`; and
`FormatContract.cs`, `FormatDisposition.cs`, `FormatVersion.cs`. B1 maps through these; it does not
rewrite any of them.

### 4.9 Existing outcome/recovery classification (before B1's rule)

| Operation | Core result | `Created` | `Replaced`/`Committed` | `SuccessWithWarning` | `RecoveryRequired` |
|---|---|---|---|---|---|
| remember | `WholeMemoryResult` | new memory | overwrite of existing | commit succeeded, archive/prune cleanup pending | rollback unprovable |
| compact | `WholeMemoryResult` | new single-chunk memory | overwrite of existing | commit succeeded, cleanup pending | rollback unprovable |
| sidecar (skill/agent) | `FileEntityMutationResult` | new file entity | overwrite of existing | publish committed, evidence cleanup pending | rollback unprovable |
| import (one topic) | *(none — `void`)* | new memory in scope | overwrite of existing | *(none today)* | retained restore journal blocks scope |

### 4.10 Public result vocabulary divergence (Core → MCP → CLI)

The result vocabulary is currently split: `remember`/`compact` surface `WholeMemoryResult` through
`McpResponse`; sidecar writes surface `FileEntityMutationResult` through `McpResponse`; import surfaces
`void` through `McpResponse` with no typed Core result; migration surfaces `StoreLayoutMigration.Result`
through the CLI `scri migrate` command. B1 unifies the Core surface behind `B0Result` for these
operations without changing the MCP or CLI passthrough.

| Layer | Whole-memory (remember/compact) | File-entity (sidecar) | Restore (import) | Layout migration |
|---|---|---|---|---|
| Core | `WholeMemoryResult` (`WholeMemoryTransaction.cs:108`) | `FileEntityMutationResult` (`WholeMemoryTransaction.cs:292`) | none (`void`) | `StoreLayoutMigration.Result` (`StoreLayoutMigration.cs:77`) |
| Outcome enum | `WholeMemoryOutcome` (`:100`) | `WholeMemoryOutcome` (`:100`) | — | `bool Ran` / `int Moved` / `int Conflicts` |
| Envelope failures | `WholeMemoryTransactionException` (`:145`) | `WholeMemoryTransactionException` (`:145`) | `RestoreRecoveryException` (`RestoreJournal.cs:47`) | — |
| MCP | `McpResponse.ErrorCodes` (`McpResponse.cs:10`) | `McpResponse.ErrorCodes` | `RecoveryBlocked` / `RecoveryRequired` | — |
| CLI | `CliMcpOutput` passthrough raw YAML (`CliJsonContext.cs:99`) | `CliMcpOutput` passthrough raw YAML | `CliMcpOutput` passthrough raw YAML | `CliMigrateOutput` / `CliStoreMigrateOutput` (`CliJsonContext.cs:26–27`) |

## 5. Design — Section 1: operation-specific commit boundary

B1 adopts the existing coordinators as the single commit boundary for each operation. It does **not**
introduce a new coordinator, a new durable file, or a new durable journal. The four boundaries are:

- **remember / compact** → `WholeMemoryTransactionCoordinator.ExecuteAsync`
  (`WholeMemoryTransactionCoordinator.cs:16`) is the commit boundary. It stages the `Prepared`
  journal, archives the old pair, publishes the new pair, verifies SHA-256, records `Committed`, then
  prunes. Rollback runs through `TryRollback` (`:142`); unprovable rollback raises `RecoveryRequired`
  (`:61`); pending cleanup raises `SuccessWithWarning` (`:80`, `:105`). Compact shares this boundary
  because it re-stores through the same `StoreWithTransactionResultAsync` path (`FileMemoryStore.cs:2374`).
- **arbitrary sidecar mutations** → `FileEntityTransactionCoordinator.ExecuteAsync`
  (`FileEntityTransactionCoordinator.cs:19`) is the commit boundary. It captures old body+meta, writes
  a `Prepared` file-entity envelope, archives, publishes, verifies, writes the `Committed` envelope,
  and deletes evidence, returning `FileEntityMutationResult`. `IsReadOnly` (`FormatVersion.cs:43`) is
  the pre-commit version gate; `RecoverPendingEvidence` (`:34`) returns retained evidence before any
  mutation.
- **import of one topic** → the per-topic `RestoreJournal` (`RestoreJournal.cs:17–32`) staged inside
  `FileMemoryStore.ImportTopicEntriesCore` (`FileMemoryStore.cs:4618`) is the commit boundary. One
  memory = one scope = one journal. `ApplyPreparedJournalUnsafe` (`:4711`) writes bodies+sidecars and
  verifies SHA256; `RecoverScopeUnsafe` (`:4727`) replays `Prepared`/`Completed` journals on next
  access if the write pass did not complete.
- **migration of one memory** → the single-memory move inside `StoreLayoutMigration.RunMigration`
  (`StoreLayoutMigration.cs:276`) via `MoveWithRetry` (`:526`) is the commit boundary. It moves one
  plan entry, quarantines collision losers to `.scrinia-conflicts-*/`, and on resume re-checks
  `File.Exists(source)`. There is no rollback; interruption resumes on next access.

B1 records each boundary as a testable invariant and maps its Core result into `B0Result` (Section 11).
No coordinator, journal, or durable file is added or relocated.

## 6. Design — Section 2: import and bundle publication discipline

### 6.1 Import: preserve Build-pass → Write-pass, one memory = one journal scope

B1 preserves the `ImportTopicsFromZip` ordering (`BundleFormatService.cs:166`): the Build pass
(comment at line 232) parses and validates every manifest topic and every entry *before any write*,
refusing a malformed scope, unreadable index, missing/invalid address, unsafe subject, or
over-versioned bundle entry up front; the Write pass (lines 487–498) then calls
`ImportTopicEntries` once per topic. B1 adds nothing to this ordering. It records the invariant that a
single topic import is a single `RestoreJournal` scope and that the "refuse before any side effect"
rule is the operation's commit discipline.

### 6.2 Import per-memory result type (the first justified change)

Import is `void` at the store boundary (`FileMemoryStore.cs:4600`), so no per-memory typed result
flows up today. B1 proposes the minimal per-memory import result so import can map through `B0Result`
the same way remember and compact do. The proposal reuses the existing `WholeMemoryOutcome` vocabulary
(`WholeMemoryTransaction.cs:100`) rather than inventing a new outcome enum:

```csharp
// Internal Core result only — carried at the result boundary, never written to disk.
internal sealed record ImportMemoryResult(
    WholeMemoryOutcome Outcome,      // Created | Replaced | SuccessWithWarning | RecoveryRequired
    ArtifactEntry? Entry,
    string OperationId,              // the scoped RestoreJournal.TransactionId
    string? Warning,
    RecoveryInfo? Recovery);
```

`Created` maps to a memory that did not exist in the scope before; `Replaced` maps to an overwrite of
an existing memory; `SuccessWithWarning` maps to a completed publish whose completed-journal cleanup
(`TryDeleteCompletedJournalUnsafe`) was deferred; `RecoveryRequired` maps to a retained `Prepared`
restore journal that blocks the scope until `RecoverScopeUnsafe` replays it. The shape is identical to
`WholeMemoryResult` (`WholeMemoryTransaction.cs:108`); the distinct name records import semantics
(scope identity, journal transaction id) at the result boundary. `B0ResultWrappers` gains a thin
`FromImport` that is structurally identical to `FromWhole` (`B0Result.cs:35`): it maps
`WholeMemoryOutcome` through the existing `MapWholeOutcome` and carries `OperationId`, `Warning`, and
`Recovery` unchanged. `FromImport` is internal Core; it registers through the existing
`ScriniaMcpJsonContext` alongside `B0Result` (there is no serialization of `ImportMemoryResult` on
disk). This is the first justified change; the second is the aggregate layout-migration mapping in
Section 10.

### 6.3 Bundle export: refuse-before-write + publish-per-topic + cleanup-on-failure

Bundle export currently has no refuse-before-write or cleanup discipline. `ExportTopicsToZip`
(`BundleFormatService.cs:59`) writes topics into an open `ZipArchive` as it goes, `WriteFileEntities`
(`:732`) writes file entities with no journaled recovery (comment at `:730`), and the CLI `Export`
(`MemoryTools.Bundling.cs:53–66`) opens the bundle stream, calls `ExportTopicsToZip`, and on an empty
export (`exportedTopics.Count == 0`) deletes the half-created bundle at line 60 with no rollback.

For the "bundle writes that publish one memory" boundary, B1 requires export to apply the SAME
refuse-before-any-side-effect discipline import enforces, in reverse. Export first BUILDs every
topic's index+body+manifest in a staging area (in memory or a private staging directory) BEFORE
opening or creating the bundle zip; a malformed topic, oversized body, or unsafe subject fails the
build and no bundle is created (no side effect). Only after the full build succeeds does export
PUBLISH per-topic into the zip (one topic = one commit); if publish fails partway, export DELETES the
bundle and any staging tree (cleanup-on-failure), so a failed export leaves no half-created bundle. The
existing empty-export delete at `MemoryTools.Bundling.cs:60` is preserved as the no-op cleanup path
(zero exported topics → nothing staged → no bundle created).

The discipline is scoped to *publish ordering and failure cleanup* — it does not add a durable journal,
does not change `WriteTopic`/`WriteManifest`/`WriteFileEntities` wire formats, does not change the
v1/v2 manifest version rule (`WriteManifest`, `BundleFormatService.cs:137`), and does not add a typed
per-topic result. B1 does not add journaled recovery to bundle export; the atomic `DurableFile` write
per topic is the publish boundary (journaled recovery for external writes is a B4 concern, not B1).
This discipline is a justified behavior change (export gains refuse-before-write + cleanup-on-failure),
documented in Section 13.2 as the third justified change, and covered by a compatibility test proving:
(a) a malformed topic produces no bundle and no side effect; (b) a successful export produces the
byte-identical bundle it produced before B1; (c) a failed publish leaves no bundle.

## 7. Design — Section 3: compact archive and prune preservation

B1 preserves the compact archive-and-prune path exactly. Compact re-encodes merged chunks and
re-stores through `StoreWithTransactionResultAsync` (`MemoryTools.Lifecycle.cs:276`), which drives
`StorePersistentAsync` (`FileMemoryStore.cs:2602`) → `WholeMemoryTransaction` + `ArchivePlan` →
`WholeMemoryTransactionCoordinator.ExecuteAsync` (`WholeMemoryTransactionCoordinator.cs:16`). The
`ArchivePlan` archives the previous version and prunes older versions through
`PruneWholeArchives` (`FileMemoryStore.cs:2701`). The warning path is `SuccessWithWarning`
(`WholeMemoryTransactionCoordinator.cs:80`, `:105`): commit succeeded but archive/prune cleanup is
pending, which is the explicit warning behavior B1 records. B1 does not change `ArchivePlan`, the
prune candidate set (`BuildWholePruneCandidates`, `FileMemoryStore.cs:2728`), the skip-9 retention
rule, or the `SuccessWithWarning` condition. Compact's `hadIndexedEntry` archive claim
(`MemoryTools.Lifecycle.cs:297`) is preserved.

## 8. Design — Section 4: sidecar-only pairing protection

B1 preserves the whole-memory artifact↔sidecar pairing for file-entity writes and the sole
`IsReadOnly` version gate. `WriteSidecarMeta<T>` (`MemoryTools.Skills.cs:212`) builds a
`FileEntityMutation` with `BodyPath == MetaPath` (sidecar-only) and routes through
`FileEntityTransactionCoordinator.ExecuteAsync` (`FileEntityTransactionCoordinator.cs:19`), which
publishes body+meta as a recoverable pair. `ArtifactSidecarPolicy.IsReadOnly`
(`Formats/FormatVersion.cs:43`) is the only "cannot rewrite" guard; `ReadSidecarMetaOrRefuse`
(`MemoryTools.Skills.cs:172`) applies the same gate *before* any side effect, mirroring the
import/store refuse-before-write rule. B1 adds only the explicit warning-vs-recovery-required
classification for `FileEntityMutationResult`: `SuccessWithWarning` (publish committed, evidence
cleanup pending, `FileEntityTransactionCoordinator.cs:87`) versus `RecoveryRequired` (publish failed
and rollback unprovable, `:112`). The pairing and the gate are unchanged.

## 9. Design — Section 5: warning vs recovery-required classification

B1 defines the precise rule that maps each Core outcome and exception to a `B0Outcome`, a `B0Failure`,
and a classification of *typed warning* versus *recovery-required*, using the existing enum members
only (`Created`/`Replaced`/`Appended`/`Committed`/`SuccessWithWarning`/`RecoveryRequired`). No new
outcome member is introduced into `WholeMemoryOutcome`, `B0Outcome`, or any other enum.

The mapping is read through the existing `B0ResultWrappers.MapWholeOutcome` (`B0Result.cs:85`) for the
whole-memory and file-entity operations, and through the new `FromImport`/`FromMigration` wrappers for
import and migration. The classification rule is:

| Core signal | B1 classification | `B0Outcome` | `B0Failure` | Why |
|---|---|---|---|---|
| `WholeMemoryOutcome.Created` | success | `Created` | none | new memory published, pair verified, evidence deleted |
| `WholeMemoryOutcome.Replaced` | success | `Replaced` | none | existing memory overwritten, pair verified |
| `WholeMemoryOutcome.SuccessWithWarning` | **typed warning** | `SuccessWithWarning` | none | commit succeeded; archive/prune/evidence cleanup pending |
| `WholeMemoryOutcome.RecoveryRequired` | **recovery-required** | `RecoveryRequired` | `WholeMemory` (`Internal`/`RecoveryRequired`) | publication failed and byte-for-byte rollback unprovable |
| `WholeMemoryErrorCode.Internal` (exception) | recovery-required | `RecoveryRequired` | `WholeMemory` | `WholeMemoryTransactionException` from `TryRollback` failure (`WholeMemoryTransactionCoordinator.cs:54`) |
| `FileEntityMutationResult.SuccessWithWarning` | **typed warning** | `SuccessWithWarning` | none | publish committed; evidence cleanup pending (`FileEntityTransactionCoordinator.cs:87`) |
| `FileEntityMutationResult.RecoveryRequired` | **recovery-required** | `RecoveryRequired` | `WholeMemory` | publish failed; rollback unprovable (`:112`) |
| `ImportMemoryResult.Created` | success | `Created` | none | new memory published; completed-journal verified |
| `ImportMemoryResult.Replaced` | success | `Replaced` | none | existing memory overwritten; completed-journal verified |
| `ImportMemoryResult.RecoveryRequired` | **recovery-required** | `RecoveryRequired` | `Restore` | retained `Prepared` restore journal blocks scope (`RecoverScopeUnsafe`, `FileMemoryStore.cs:4727`) |
| `StoreLayoutMigration` per-memory move = Moved | success | `Committed` | none | single memory relocated to `memories/` |
| `StoreLayoutMigration` per-memory move = Conflict | **typed warning** | `SuccessWithWarning` | `Migration` (`Conflict`) | loser quarantined to `.scrinia-conflicts-*/`; operator must resolve |

`B0FailureTaxonomy` gains no new member for B1's import/layout-migration cases; the layout-migration
conflict case routes through the existing `WholeMemory` taxonomy via the thin wrapper, or — if the
plan adds a `Migration` taxonomy — that addition is a separate B0-style vocabulary change and is out of
scope for B1. B1's classification records the rule; it does not add a new `B0FailureTaxonomy` member.

## 10. Design — Section 6: migration state and legacy data preservation

B1 preserves three migration behaviors exactly and adds only the layout-migration result mapping.

- **Layout migration quarantine/resume.** `StoreLayoutMigration` (`StoreLayoutMigration.cs`) keeps
  `MigrateIfNeeded` auto-on-first-access (`:173`) and `Migrate` backing `scri migrate` (`:177`). The
  single-memory move (`:276`) with `MoveWithRetry` (`:526`) keeps retry-on-sharing-violation,
  conflict quarantine to `.scrinia-conflicts-*/`, `index.json` expansion-then-delete, and
  resume-on-interruption (`if (!File.Exists(source)) continue`). B1 adds no rollback — the existing
  "no rollback, resume on next access" behavior is the invariant.
- **Attribute migration forward-fill.** `AttributeMigration` (`Migration/AttributeMigration.cs`)
  keeps its idempotent forward-fill of `fields`→`attributes` and `key:value`-keyword folding, and
  `MigrateStore` (`:157`) keeps its best-effort write-back that swallows I/O
  (`FileMemoryStore.cs:1506–1507`). B1 maps the per-entry `MigratesTo` decision
  (`AttributeMigration.cs:115`) into a success (`Committed`) or a skip (`Committed`, no change) at the
  result boundary.
- **Tracking-policy migration is NOT memory migration.** `LegacyMigrationService`
  (`Migration/LegacyMigrationState.cs`) migrates the tracking policy across `config.json`,
   `.gitattributes`, `.gitignore`, and `merge.config`. Its `Migrate` (`:227`) is a locked config
   transaction with `TryRestore` (`:509`) rollback of the four config files. Tracking-policy migration
   (`LegacyMigrationService`) is a config transaction, not a single-memory write, and is OUT OF B1'S
   SCOPE: B1 covers migration of one memory only. B1 preserves its existing locked-transaction behavior
   and its `TryRestore` rollback untouched; it does not map the config transaction through `B0Result`
   and does not change `LegacyMigrationState`.

`StoreLayoutMigration` returns an **aggregate** `Result(Moved, Conflicts, Ran, BackupPath?,
BackupSkipped?)` (`StoreLayoutMigration.cs:77`), not a per-memory result. B1 maps that aggregate
result through `B0Result` rather than threading a per-memory result out of the bulk `RunMigration`
pass (which would be a behavior change to the durable migration). The mapping: `Ran == false` →
`Committed` (nothing to migrate); `Ran == true && Conflicts == 0` → `Committed` (all moves succeeded,
evidence deleted); `Ran == true && Conflicts > 0` → `SuccessWithWarning` with a `Migration`/`Conflict`
failure detail and a `RecoveryInfo` pointing at the `.scrinia-conflicts-*/` quarantine directory
(operator must resolve the losers). B1 adds no per-memory layout-migration result record; the aggregate
`StoreLayoutMigration.Result` is the result boundary. `FromMigration` maps the aggregate `Result` to
`B0Result`; it does not add a `MemoryMigrationResult` type.

## 11. Public result mapping

B1 maps the six operations through `B0Result` across Core → MCP → CLI. The mapping is thin and
behavior-preserving.

```text
Core:  B0Result  ──B0ResultWrappers──▶  MCP: McpResponse (Status/IsError/IsWarning/Action/OperationId/Scope/...)
                                         MCP: McpResponse.ErrorCodes (unchanged)
                                       CLI: CliMcpOutput (passthrough raw YAML) — JSON roots unchanged except B0Result registration
```

Rules:

- **ErrorCodes stay stable.** `McpResponse.ErrorCodes` (McpResponse.cs:10) is unchanged. B1 does not
  add an error code; it re-expresses existing codes (`RecoveryRequired`, `RecoveryBlocked`,
  `DurableWriteFailed`, `InvalidBundle`) through the same vocabulary.
- **New wrappers, no new facade.** `B0ResultWrappers` gains `FromImport` (Section 6.2) and
  `FromMigration` (Section 10) alongside the existing `FromAppend`/`FromWhole`/`FromMulti`/
  `FromRecovery`. `B0Result` is still the single Core result contract; neither MCP nor CLI derives or
  sequences writes independently. The roadmap §6 rule (no duplicate facade transaction or derivation)
  holds.
- **`ImportMemoryResult` is an internal Core record.** It is carried at the result boundary only; it
  is not a durable format, not a recovery outcome, and not registered as a durable JSON root; it
  reuses `WholeMemoryOutcome`. The layout-migration mapping adds no Core result record — it maps the
  existing aggregate `StoreLayoutMigration.Result` through `FromMigration` at the result boundary, so
  nothing new is carried there.
- **CLI passthrough unchanged.** `CliMcpOutput` (`CliJsonContext.cs:99`) continues to carry raw YAML
  from MCP. `scri store`/`scri migrate`/`scri import`/`scri export` render the same YAML they render
  today; B1's `B0Result` mapping is read by tests and by any future typed consumer, not by the current
  YAML passthrough.
- **No second facade.** `B0Result` remains the single Core result contract for the six operations.

## 12. Compatibility constraints

These restate roadmap §7 for B1. B1 preserves every constraint below.

- **NMP/2:** Preserve `nmp/2`, current encoding/decoding, headers, chunk semantics, and artifact
  readability. B1 changes nothing in `Encoding/`.
- **Sidecars and migrations:** Preserve current sidecar fields, legacy `name`/`uri` handling,
  forward-fill and `fields`→`attributes` migration, skill/agent sidecar lookup, and malformed sidecar
  safety. Use `ReadSidecarMeta`/`WriteSidecarMeta` and source-generated contexts. Preserve the
  layout-migration quarantine/resume, the attribute-migration best-effort write-back, and the
  tracking-policy config rollback.
- **Archive layout:** Preserve existing version directories, names, collision rules, artifact/sidecar
  pairing, and retention semantics. B1 preserves `ArchivePlan`, `PruneWholeArchives`, and the
  skip-9 prune rule unchanged.
- **Ephemeral memories:** Keep `~name` and `/temp/name` in-process and outside persistent
  archive/journal guarantees. Compact and remember already route ephemerals in-process; B1 preserves
  that.
- **CLI/MCP:** Facades translate one Core result contract (`B0Result`). Public error codes, operation
  IDs, warnings, recovery details, and content privacy remain stable. `McpResponse.ErrorCodes` and
  `CliMcpOutput` are unchanged.
- **Source-generated JSON:** Register every new serialized type in the appropriate JSON context.
  Reflection-based `JsonSerializer` use is incompatible with trimming and is prohibited. `ImportMemoryResult`
  is not serialized to disk, so it needs no durable root; the layout-migration mapping adds no new
  serialized type at all. Only `B0Result` and its vocabulary (already registered by B0) cross the
  boundary.
- **Deployment surface:** `scri serve` over local stdio is the only server surface. No HTTP/REST,
  remote CLI, API-key, web, or Aspire guidance.
- **Shared privacy and no Git:** Shared-base integration is filesystem-only, fixed to `project-git`,
  reports `UNVERIFIED` index state, never starts or inspects Git, and does not disclose disabled
  registry entries except through explicit `shared base list --all`.
- **Historical docs:** `docs/planning/`, `docs/superpowers/`, dated reviews, and released changelog
  entries are historical records. This spec does not rewrite them.

## 13. Behavior lock and justified changes

### 13.1 The behavior lock

Sections 5, 6.1, 6.3, 7, 8, and 12 are **behavior-preserving**: they adopt, record, and map existing
boundaries. They add no durable file, journal, state, migration, archive rule, or recovery outcome,
and they rewrite no existing state machine.

### 13.2 The three justified changes

1. **Minimal per-memory import result (Section 6.2).** Import is `void` at the store boundary
   (`FileMemoryStore.cs:4600`), so it cannot map to `B0Result` today. `ImportMemoryResult` reuses the
   existing `WholeMemoryOutcome` vocabulary and is carried only at the result boundary; it is never
   written to disk. This gives import one typed result to map through `FromImport` without changing
   import's Build→Write ordering, its `RestoreJournal` scope, its SHA256 verification, or its
   `RecoverScopeUnsafe` recovery. It is representation at the result boundary, not behavior.
2. **Aggregate layout-migration mapping through `B0Result` (Section 10).** `StoreLayoutMigration`
   already returns a typed aggregate `Result` (`:77`); B1 maps that aggregate through `FromMigration`
   into `B0Result` rather than threading a per-memory result out of the bulk `RunMigration` pass. It
   changes no move, no quarantine, no resume, no forward-fill, and no durable migration behavior. It
   maps `Ran == false` → `Committed`, `Ran == true && Conflicts == 0` → `Committed`, and
   `Ran == true && Conflicts > 0` → `SuccessWithWarning` at the result boundary only — representation,
   not behavior.
3. **Bundle-export refuse-before-write + cleanup-on-failure discipline (Section 6.3).** Export gains
   build-every-topic-first-then-publish-per-topic with cleanup-on-failure, mirroring import's
   refuse-before-any-side-effect rule in reverse. It changes only the ordering and failure cleanup of
   the existing `ExportTopicsToZip`/`Export` path; it adds no durable journal, no recovery outcome, and
   no change to `WriteTopic`/`WriteManifest`/`WriteFileEntities` wire format.

All three are result-boundary only. Item 1 is an internal Core result record, exactly like `B0Result`
and `RecoveryMember` (B0's single justified change) and is never a durable format. Items 2 and 3 map
existing results and the existing publish/cleanup behavior at the result boundary; they add no durable
record, journal, or recovery outcome.

### 13.3 Any other change needs separate justification

No other change is authorized by this spec. If implementation reveals a need beyond the three justified
changes, that change requires a new user-approved decision recorded in Section 16 before it is made.
Section 13 enforces the roadmap §10 split rule and the behavior lock in locked decisions 2 and 6.

## 14. Invariants

B1 holds every invariant below. None is a proposal to change; each is a guard the implementation must
satisfy.

- **No new durable file, journal, state, migration, archive rule, or recovery outcome.** The single
  justified result record (Section 6.2) is an in-memory Core type carried only at the result boundary;
  the other two justified changes map existing results and existing publish/cleanup behavior at the
  boundary and add no durable record.
- **Sole parser boundary `ParseVerifiedEnvelope` (RecoveryVerification.cs:299) untouched.** B1 maps
  through it; it does not rewrite it, its six arguments, or any `ConsumeParsed` consumer.
- **No rewrite of existing state machines.** `AppendJournalState`, `WholeMemoryOutcome`,
  `RestoreJournalState`, `MultiMemoryTransactionState`, `RecoveryLifecycleState`, and
  `LegacyMigrationState` remain authoritative and unchanged.
- **No public MCP/CLI contract change except additive `B0Result` mapping through the existing facades.**
  `McpResponse.ErrorCodes` and `CliMcpOutput` are unchanged.
- **No reuse of B3/B4/B5/B6 contracts.** B1 stays within single-memory operations and the B0
  vocabulary.

## 15. Test lanes and acceptance

Acceptance restates roadmap §8 gates. B1 passes only when all of the following hold.

1. **An approved child specification names goals, non-goals, invariants, ownership, compatibility
    impact, public result mapping, migration/recovery behavior, tests, and explicit decisions.** This
    document is that specification.
2. **A scoped implementation plan names exact files, call sites, test lanes, and exclusions.**
3. **Focused per-operation tests pass.** They cover:
    - remember/compact `B0Result` mapping preserves outcome, entry, warning, and recovery through
      `FromWhole`; the `SuccessWithWarning` branch maps to `success_with_warning`; the
      `RecoveryRequired` branch maps to `RecoveryRequired`.
    - import per-memory result maps `Created`/`Replaced`/`SuccessWithWarning`/`RecoveryRequired`
      through `FromImport`; the retained-journal case maps to `RecoveryRequired`.
    - migration maps the aggregate `StoreLayoutMigration.Result` through `FromMigration`: `Ran ==
      false` → `Committed`, `Ran == true && Conflicts == 0` → `Committed`, and `Ran == true &&
      Conflicts > 0` → `SuccessWithWarning` (Conflict → quarantine path).
    - bundle export applies refuse-before-write (a malformed topic produces no bundle and no side
      effect), publishes per-topic and cleans up on failure (a failed publish deletes the bundle and
      staging tree, leaving no half-created bundle), and the empty-export delete at
      `MemoryTools.Bundling.cs:60` is the explicit no-op cleanup (zero topics → nothing staged → no
      bundle); a successful export produces the byte-identical bundle it produced before B1.
    - sidecar-write pairing maps `SuccessWithWarning` (evidence cleanup pending) versus
      `RecoveryRequired` (rollback unprovable) through `FromWhole`.
    - the rollback path returns the correct `B0Outcome`: `WholeMemoryTransactionCoordinator.TryRollback`
      (`:142`) success maps to `Replaced`/`Created`; unprovable rollback (`:61`) maps to
      `RecoveryRequired`.
    - `B0FaultSeam` mapped phases reproduce each operation's failure: `WholeMemoryFaultPoint` for
      remember/compact, `FileEntityFaultPoint` for sidecar, `RestoreJournalTransition` for import,
      `DurableWritePhase` for migration moves and bundle writes.
4. **Compatibility/harness tests pass.** Current evidence for remember, compact, import, migration,
    bundle, and sidecar writes flows through the unified `B0Result` discipline and maps without
    behavior change. The bare-complete migration regression (Section 4.6, the `scri migrate` config
    transaction with `TryRestore`) is preserved; the layout-migration resumption (`File.Exists`
    guard) and attribute-migration idempotency (`MigratesTo`, `AttributeMigration.cs:115`) are
    preserved.
5. **`dotnet build Scrinia.sln` passes with zero errors and zero warnings attributable to B1.**
6. **`dotnet test Scrinia.sln` exits 0 with zero failures.** No test-host hang, crash, abort, or
    timeout is accepted. The final run records pass, skipped, failed, total, exit status, and
    no-hang evidence.
7. **Package, trimming/source-generation, CLI/MCP contract, and historical-doc checks pass.** No new
    durable JSON root is introduced; `B0Result` and its vocabulary remain the only registered Core
    result roots.
8. **A scoped diff check passes.** The B1 commit contains no unrelated source, test, evidence,
    generated, or historical-document change. B1's evidence lives under the roadmap §9 dated evidence
    directory.
9. **An independent review records APPROVE (or a resolved REQUEST_CHANGES cycle) against the exact
    B1 diff and verification transcript.**

## 16. Decision record

The roadmap §4 requires six explicit decisions. The user approved all six (locked decisions in
Section 3).

| # | Decision | Alternative considered | Rationale | Approved |
|---|---|---|---|---|
| 1 | Operation-specific commit boundary | Adopt the existing coordinators (`WholeMemoryTransactionCoordinator`, `FileEntityTransactionCoordinator`, `RestoreJournal`, `StoreLayoutMigration`) **vs.** introduce a new coordinator or durable file. | The roadmap §10 split rule forbids a new durable file/journal/state; the existing coordinators already carry commit, rollback, recovery, and warning. Adopting them is behavior-preserving and reuses B0's fault seam. | **Approved.** Section 5 adopts the four existing coordinators as the single commit boundary. |
| 2 | Import and bundle per-entry discipline | Preserve Build-pass→Write-pass "refuse before any side effect", one memory = one journal scope **vs.** one-entry-per-transaction. | The roadmap §7 refuse-before-side-effect rule and the existing `ImportTopicEntriesCore` ordering already refuse before any side effect; one memory = one `RestoreJournal` scope keeps import inside single-memory. | **Approved.** Section 6.1 preserves the ordering; Section 6.3 adds transactional staging + per-topic commit for export. |
| 3 | Compact archive and prune | Preserve the existing `ArchivePlan` archive + prune path with `SuccessWithWarning` as the explicit warning **vs.** a new archive/prune service. | The archive/prune service is roadmap deferred area 5 (B5). B1 preserves the existing path and its warning condition. | **Approved.** Section 7 preserves `ArchivePlan`, `PruneWholeArchives`, and the skip-9 rule. |
| 4 | Sidecar pairing | Preserve `IsReadOnly` version gate + whole-memory artifact↔sidecar pairing, add only warning-vs-recovery classification **vs.** a new pairing model. | The pairing and the single `IsReadOnly` gate are already correct; B1's contribution is the classification, not the pairing. | **Approved.** Section 8 preserves the gate and pairing. |
| 5 | Warning vs recovery-required classification | Map each Core outcome/exception to `B0Outcome`/`B0Failure` through the existing enum members only **vs.** invent new outcomes. | The roadmap §10 split rule and the invariant forbid new durable recovery outcomes; the existing `WholeMemoryOutcome` members already express every case. | **Approved.** Section 9 defines the mapping; no new outcome member is introduced. |
| 6 | Migration preservation | Preserve layout-migration quarantine/resume, attribute-migration forward-fill, and tracking-policy config rollback, add only the layout-migration `B0Result` mapping **vs.** rework migration behavior. | Migration behavior is committed and tested; B1's contribution is the layout-migration result mapping, not a migration rewrite. | **Approved.** Section 10 preserves all three behaviors and maps the aggregate layout-migration result through `FromMigration`. |

## 17. Self-review

This is a DRAFT. The following placeholders must be filled before B1 is presented for review.

- **Contradictions:** none identified in draft. Confirm Section 6.2 (`ImportMemoryResult` reusing
  `WholeMemoryOutcome`) does not contradict Section 9 (which lists `Replaced` as an import outcome):
  import overwrite maps through the same `MapWholeOutcome` as remember, so `Replaced` is the correct
  Core outcome and `Replaced`/`Created` are the correct `B0Outcome` for import. State this explicitly
  in the final review.
- **Ambiguity:** `ImportMemoryResult.OperationId` carries the scoped `RestoreJournal.TransactionId`
  (`RestoreJournal.cs:21`), which is a `Guid` `N` format; confirm it is the right correlation field
  against `B0Result.OperationId` (a string) and that the import write pass's journal transaction id
  is the value `FromImport` should carry.
- **Scope:** confirm Section 6.3's export discipline does not cross a scope or lock boundary (build
  all topics first, then publish per topic — one topic = one publish) and that the empty-export delete
  at `MemoryTools.Bundling.cs:60` is the no-op cleanup path (zero topics → nothing staged → no bundle)
  alongside the cleanup-on-failure delete.
- **Justified changes:** confirm the three justified changes (Section 13.2) — the import result record
  (item 1), the aggregate layout-migration mapping (item 2), and the bundle-export discipline (item 3)
  — are all result-boundary only and none introduces a durable format, journal, or recovery outcome.
- **Placeholders:** acceptance gates in Section 15 and the decision record in Section 16 are present
  but not yet evidenced. No code is written; no transcript is attached.

## 18. Citations

- Phase B umbrella roadmap: `docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md` §4 (B1
  boundary, exclusions, required decisions), §5 (dependencies), §7 (compatibility constraints), §8
  (acceptance gates), §10 (split criteria), §11 (blocker checklist).
- Predecessor B0 spec: `docs/superpowers/specs/2026-09-17-b0-transaction-foundation-design.md`
  (Section 6.2 typed member carrier justification, Section 10 public result mapping, Section 12
  behavior lock).
- `src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs:16`, `:61`, `:80`, `:105`, `:142` —
  remember/compact commit boundary, rollback, recovery, and warning.
- `src/Scrinia.Core/WholeMemoryTransaction.cs:100`, `:108`, `:145`, `:192`, `:292` —
  `WholeMemoryOutcome`, `WholeMemoryResult`, `WholeMemoryTransactionException`, `RecoveryInfo`,
  `FileEntityMutationResult`.
- `src/Scrinia.Core/FileMemoryStore.cs:1506`, `:2374`, `:2602`, `:2701`, `:4600`, `:4618`, `:4711`,
  `:4727` — `StorePersistentAsync`, `StoreWithTransactionResultAsync`, `PruneWholeArchives`,
  `ImportTopicEntries`, `ImportTopicEntriesCore`, `ApplyPreparedJournalUnsafe`, `RecoverScopeUnsafe`.
- `src/Scrinia.Mcp/MemoryTools.Core.cs:245` — remember facade.
- `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:74`, `:300` — compact path and warning promotion.
- `src/Scrinia.Mcp/MemoryTools.Skills.cs:212`, `:172` — `WriteSidecarMeta` and refuse-before-write
  sidecar gate.
- `src/Scrinia.Core/FileEntityTransactionCoordinator.cs:19` — sidecar commit boundary.
- `src/Scrinia.Core/StoreLayoutMigration.cs:77`, `:173`, `:177`, `:276`, `:526` — migration result,
  auto/manual triggers, single-memory move, `MoveWithRetry`.
- `src/Scrinia.Core/Migration/AttributeMigration.cs:115`, `:157` — `MigratesTo` idempotency and
  `MigrateStore` best-effort write-back.
- `src/Scrinia.Core/Migration/LegacyMigrationState.cs:227`, `:509` — tracking-policy config
  transaction and `TryRestore` rollback.
- `src/Scrinia.Core/Bundles/BundleFormatService.cs:59`, `:110`, `:137`, `:166`, `:232`, `:732` —
  bundle export, `WriteTopic`, `WriteManifest`, `ImportTopicsFromZip`, Build-pass, `WriteFileEntities`.
- `src/Scrinia.Mcp/MemoryTools.Bundling.cs:60` — empty-export bundle cleanup.
- `src/Scrinia.Core/RestoreJournal.cs:17`, `:21`, `:47` — `RestoreJournal` record, `TransactionId`,
  `RestoreRecoveryException`.
- `src/Scrinia.Core/B0Result.cs:22`, `:35`, `:85` — `B0ResultWrappers`, `FromWhole`, `MapWholeOutcome`.
- `src/Scrinia.Core/B0Vocabulary.cs` — `B0Outcome`, `B0Failure`.
- `src/Scrinia.Core/B0FaultSeam.cs` — unified fault seam over the twelve seams.
- `src/Scrinia.Core/RecoveryMember.cs` — typed member carrier (B0's justified change).
- `src/Scrinia.Core/RecoveryLifecycleStateMapper.cs` — read-only lifecycle projection.
- `src/Scrinia.Core/Formats/DurableFormats.cs:15` — durable-format registry.
- `src/Scrinia.Core/Formats/FormatVersion.cs:43` — `ArtifactSidecarPolicy.IsReadOnly` gate.
- `src/Scrinia.Core/RecoveryVerification.cs:299` — sole parser boundary (B5-owned, do-not-touch).
- `src/Scrinia.Mcp/McpResponse.cs:10` — `ErrorCodes` (unchanged).
- `src/Scrinia/Commands/CliJsonContext.cs:99` — `CliMcpOutput` passthrough (unchanged).
- `src/Scrinia/Commands/ScriniaCommands.Memory.cs:519` — CLI `remember` facade through
  `ScriniaMcpTools.Remember`.

(End of draft. No source or test changes are authorized by this file.)
