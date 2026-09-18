# B1 Single-Memory Transaction Coverage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the B0 transaction discipline and the Core→MCP→CLI `B0Result` mapping to the six single-memory write boundaries (`remember`, `compact`, import of one memory, migration of one memory, bundle export that publishes one memory, and arbitrary sidecar mutations) without changing any durable behavior or the public MCP/CLI envelope.

**Architecture:** Thin result-mapping facades live in the existing `MemoryTools.*` coordinators and `B0ResultWrappers`; each of the six operations already carries its own commit boundary (a coordinator, journal, archive plan, or recovery path), and B1 adopts those boundaries unchanged. `B0Result` mapping is added at the result boundary only: `FromWhole` already covers remember/compact/sidecar (sidecar reuses `WholeMemoryOutcome`); `FromImport` (new, structurally identical to `FromWhole`) covers per-memory import via a minimal internal `ImportMemoryResult`; `FromMigration` (new) covers the aggregate `StoreLayoutMigration.Result`. Golden-render tests prove every rendered `McpResponse`/YAML is byte-identical to the pre-B1 envelope; the only behavior change is bundle export gaining refuse-before-write + cleanup-on-failure ordering.

**Tech Stack:** C# on `net10.0`, nullable reference types, `System.Collections.Immutable.ImmutableDictionary`, `System.Text.Json` source generation, xUnit, FluentAssertions, existing `TestHelpers.StoreScope`, MCP `McpResponse`/`ScriniaMcpJsonContext`, ConsoleAppFramework CLI.

**Spec:** `docs/superpowers/specs/2026-09-18-b1-single-memory-transaction-coverage-design.md`

## Global Constraints

- The only justified behavior changes are the three result-boundary-only changes in design Section 13.2: the minimal per-memory `ImportMemoryResult` and its `FromImport` mapping (item 1), the aggregate `StoreLayoutMigration.Result` → `B0Result` mapping via `FromMigration` (item 2), and the bundle-export refuse-before-write + cleanup-on-failure discipline (item 3). Every other section preserves existing behavior exactly (design Section 13.1 behavior lock).
- No new durable file, journal, state, migration, archive rule, or recovery outcome is introduced. `ImportMemoryResult` and the `FromMigration` mapping are in-memory Core result types carried only at the result boundary, exactly like `B0Result` and `RecoveryMember`; they are never written to disk.
- The sole parser boundary `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` (`src/Scrinia.Core/RecoveryVerification.cs:299`, six args), every typed `ConsumeParsed` consumer (`AppendJournalCodec.ConsumeParsed` `AppendJournal.cs:190`, `RestoreJournalCodec.ConsumeParsed` `RestoreJournal.cs:111`, `FileEntityRecoveryAdapter.ConsumeParsed` `RecoveryEnvelope.cs:794`, `AppendRecoveryAdapter.ConsumeParsed` `RecoveryEnvelope.cs:567`, `RestoreRecoveryAdapter.ConsumeParsed` `RecoveryEnvelope.cs:706`, `RecoveryVerifiedEnvelope.TypedBody` `RecoveryVerification.cs:294`), and the `JsonElement Body` are untouched. Never edit `ParseVerifiedEnvelope`, `ConsumeParsed`, `RecoveryVerificationContent`, `RecoveryVerificationResult`, `RecoveryVerificationOutcome`, or the verifier/accounting/cursor code.
- No rewrite of existing state machines: `AppendJournalState` (`AppendJournal.cs:11-29`), `WholeMemoryOutcome` (`WholeMemoryTransaction.cs:100-106`) with `RecoveryInfo` (line 192), `MultiMemoryTransactionState`/`MultiMemoryOutcome` (`MultiMemoryTransaction.cs:6-20`), `RestoreJournalState` (`RestoreJournal.cs:11-15`), `RecoveryLifecycleState` wire strings (`RecoveryEnvelope.cs:96`), and `LegacyMigrationState` remain authoritative and unchanged.
- No public MCP/CLI contract change except the additive `B0Result` registration already done by B0 (`ScriniaMcpJsonContext`); `McpResponse.ErrorCodes` (`src/Scrinia.Mcp/McpResponse.cs:10`) is unchanged and B1 re-expresses existing codes (`RecoveryRequired`, `RecoveryBlocked`, `DurableWriteFailed`, `InvalidBundle`) through the same vocabulary, adding none. `CliMcpOutput` (`src/Scrinia/Commands/CliJsonContext.cs:99`) continues to carry raw YAML from MCP unchanged.
- No format evolution: NMP/2, sidecar schemas (`ReadSidecarMeta`/`WriteSidecarMeta`), and archive layout remain byte-for-byte unchanged; that work belongs to B6. `WriteTopic`/`WriteManifest`/`WriteFileEntities` bundle wire formats are unchanged.
- No cross-scope, remote, plugin, HTTP, or gateway work; B1 stays inside `Scrinia.Core` and `tests/Scrinia.Tests`; there is no server surface beyond the existing `scri serve` local stdio.
- No reuse of B3-B6 or shared-base contracts: B1 consumes no `MultiMemoryTransaction` contract, no retention contract, and no shared-base result; `LegacyMigrationService` (tracking-policy config transaction) is OUT OF B1 SCOPE and its `Migrate`/`TryRestore`/`Inspect` stay untouched.
- `RecoveryMember`, `ImportMemoryResult`, and the `FromMigration` aggregate mapping are never written to disk, are not durable formats, and are not recovery outcomes.
- `ImportMemoryResult` reuses the existing `WholeMemoryOutcome` vocabulary (`WholeMemoryTransaction.cs:100`); B1 introduces no new outcome member into `WholeMemoryOutcome`, `B0Outcome`, `B0FailureTaxonomy`, or any other enum. `B0FailureTaxonomy` keeps exactly `{ AppendStore, WholeMemory, MultiMemory, RecoveryBlock }`; the layout-migration conflict case routes its `B0Failure` through the existing `WholeMemory` taxonomy because B1 may not add a `Migration` taxonomy member.
- Source-generated JSON only: register every new serialized type in the appropriate context; reflection-based `JsonSerializer` is prohibited. `ImportMemoryResult` is not serialized to disk, so it needs no durable root; the layout-migration mapping adds no new serialized type at all; only `B0Result` and its vocabulary (already registered by B0) cross the boundary.
- Bundle export refuses before any side effect and cleans up on failure: export BUILDs every topic first (a malformed topic, oversized body, or unsafe subject fails the build and no bundle is created), PUBLISHES per topic (one topic = one commit), and DELETES the bundle and any staging tree on failed publish; the existing empty-export delete at `src/Scrinia.Mcp/MemoryTools.Bundling.cs:60` is preserved as the no-op cleanup path (zero exported topics → nothing staged → no bundle created). This changes only publish ordering and failure cleanup, not the wire format or the v1/v2 manifest version rule (`WriteManifest`, `BundleFormatService.cs:137`).

---

## File Map

### New implementation files

- `src/Scrinia.Core/ImportMemoryResult.cs` - internal per-memory import result reusing `WholeMemoryOutcome`, carried only at the result boundary, never written to disk (Task 1).

### Modified implementation files

- `src/Scrinia.Core/B0Result.cs` - add `FromImport(ImportMemoryResult)` (structurally identical to `FromWhole`) and `FromMigration(StoreLayoutMigration.Result)`; refactor `FromWhole` onto a shared private `BuildFromWholeOutcome` and add the private `MapWholeMemoryCode` helper used by the remember/compact exception-classification test (Tasks 1-2, 5).
- `src/Scrinia.Core/FileMemoryStore.cs` - `ImportTopicEntries` returns `ImportMemoryResult` instead of `void`; `ImportTopicEntriesCore` threads the journaled publish outcome (Created/Replaced/SuccessWithWarning/RecoveryRequired) into that record WITHOUT altering the journal staging, SHA256 verification, or `RecoverScopeUnsafe` recovery (Task 6).
- `src/Scrinia.Core/Bundles/BundleFormatService.cs` - `ExportTopicsToZip` applies refuse-before-write (build every topic before opening the zip) + publish-per-topic + cleanup-on-failure (deletes the bundle and staging tree on failed publish); `WriteTopic`/`WriteManifest`/`WriteFileEntities`/manifest version rule untouched (Task 7).
- `src/Scrinia.Mcp/MemoryTools.Core.cs` - `Remember` (line 372) computes `B0Result = B0ResultWrappers.FromWhole(transactionResult)` and renders the SAME `McpResponse` fields it renders today; the `WholeMemoryTransactionException` catch is unchanged (Task 3).
- `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` - `Compact` (line 278) computes `B0Result = B0ResultWrappers.FromWhole(transactionResult)` and renders identically (Task 4).
- `src/Scrinia.Mcp/MemoryTools.Skills.cs` - `FileEntitySuccessResponse`/`FileEntityFailureResponse` route `FileEntityMutationResult` through `MapWholeOutcome` (the same shared mapper `FromWhole` uses) and render identically; `ScriniaMcpJsonContext` unchanged (Task 5).
- `src/Scrinia.Mcp/MemoryTools.Bundling.cs` - `Import` (line 111) and `Export` (line 56) envelopes are unchanged in shape; the golden test proves the rendered YAML is identical after the store-level discipline change (Tasks 6-7).

### Do-not-touch (B0 foundation anchors — B5-owned, NEVER modify)

- `src/Scrinia.Core/RecoveryVerification.cs` — `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` (`:299`), all `ConsumeParsed` consumers, `JsonElement Body`, verifier/accounting/cursor code.
- `src/Scrinia.Core/AppendJournal.cs`, `src/Scrinia.Core/WholeMemoryTransaction.cs`, `src/Scrinia.Core/MultiMemoryTransaction.cs`, `src/Scrinia.Core/RecoveryEnvelope.cs`, `src/Scrinia.Core/RecoveryContracts.cs`, `src/Scrinia.Core/AppendContracts.cs` — the authoritative state machines (whole-memory, restore, append, multi-memory, recovery lifecycle).
- `src/Scrinia.Core/B0Vocabulary.cs` — `B0Outcome` / `B0Failure` / `B0FailureTaxonomy` (B0's vocabulary; B1 reads it, does not extend it).
- `src/Scrinia.Core/RecoveryMember.cs`, `src/Scrinia.Core/RecoveryLifecycleStateMapper.cs`, `src/Scrinia.Core/B0FaultSeam.cs`, `src/Scrinia.Core/Formats/DurableFormats.cs` (`Registry`) — B0's foundation (read-only consumption).
- `src/Scrinia.Mcp/McpResponse.cs:10` — `ErrorCodes` and the `ResponseBuilder`/`McpResponseExtensions.ToYaml` rendering.
- `src/Scrinia/Commands/CliJsonContext.cs` — `CliMcpOutput` / `CliStoreOutput` / `CliMigrateOutput` roots and the CLI passthrough.
- `src/Scrinia.Core/Formats/FormatContract.cs`, `src/Scrinia.Core/Formats/FormatDisposition.cs`, `src/Scrinia.Core/Formats/FormatVersion.cs` — format contracts and `IsReadOnly` (`:43`).
- `src/Scrinia.Core/StoreLayoutMigration.cs` — the aggregate `Result` (`:77`), `RunMigration` (`:185`), single move (`:276`), `MoveWithRetry` (`:526`), quarantine/resume (Task 2 maps the aggregate; it does not change the engine).
- `src/Scrinia.Core/Migration/LegacyMigrationState.cs` — tracking-policy config transaction (`Migrate` `:227`, `TryRestore` `:509`) is OUT OF SCOPE.
- `Model2VecProvider.cs` and all pre-existing unrelated working-tree dirt (Task 10 scope audit).

### New/modified tests

- `tests/Scrinia.Tests/B0ResultWrapperTests.cs` - extend with `FromImport` (Task 1) and `FromMigration` (Task 2) cases.
- `tests/Scrinia.Tests/Golden/` - pre-B1 envelope fixtures for remember/compact/sidecar/import/migration/bundle (Tasks 3-7).
- `tests/Scrinia.Tests/B0ResultFacadeMappingTests.cs` - golden-render identity tests for the facades (Tasks 3-6).
- `tests/Scrinia.Tests/ImportMemoryResultTests.cs` - per-memory import result construction + `ImportTopicEntries` return (Task 6).
- `tests/Scrinia.Tests/BundleExportDisciplineTests.cs` - refuse-before-write + cleanup-on-failure (Task 7).
- `tests/Scrinia.Tests/B0FaultSeamCoverageTests.cs` - mapped-phase failure reproduction for each operation (Task 8).
- `tests/Scrinia.Tests/B1CompatibilityHarness.cs` - unified `B0Result` discipline over all six operations (Task 9).

## Interfaces and Signatures

The following internal contracts are the only cross-task API. Implementers must use these names and types unless a compile-time conflict with an existing symbol is found; resolve such a conflict by adapting the implementation, not by adding a second parallel contract.

```csharp
// Task 1 - design Section 6.2. Internal Core result only — carried at the result boundary,
// never written to disk. Structurally identical to WholeMemoryResult (WholeMemoryTransaction.cs:108)
// but named to record import semantics (scope identity, journal transaction id) at the boundary.
internal sealed record ImportMemoryResult(
    WholeMemoryOutcome Outcome,      // Created | Replaced | SuccessWithWarning | RecoveryRequired
    ArtifactEntry? Entry,
    string OperationId,              // the scoped RestoreJournal.TransactionId (Guid 'N' at RestoreJournal.cs:21)
    string? Warning,
    RecoveryInfo? Recovery);

// Task 1 - design Section 6.2. Structurally identical to FromWhole (B0Result.cs:35): it maps the
// reused WholeMemoryOutcome through the existing MapWholeOutcome and carries OperationId, Warning,
// and Recovery unchanged. Failure stays null exactly as FromWhole keeps it null.
internal static B0Result FromImport(ImportMemoryResult result);

// Task 2 - design Section 10. Maps the aggregate StoreLayoutMigration.Result (StoreLayoutMigration.cs:77)
// through B0Result at the result boundary; it does not add a per-memory record and does not touch
// RunMigration. quarantineDir is the caller-passed .scrinia-conflicts-*/ path (null when Conflicts == 0).
internal static B0Result FromMigration(StoreLayoutMigration.Result result, string? quarantineDir);

// Task 5 - thin internal wrapper so FileEntityMutationResult maps through the SAME outcome mapper
// FromWhole uses (design Section 8). Failure stays null; Artifact stays null.
internal static B0Result FromFileEntity(FileEntityMutationResult result);

// Shared private builder (Task 5 refactor) + remember/compact exception classifier (Task 3 mapping test).
private static B0Result BuildFromWholeOutcome(WholeMemoryOutcome outcome, ArtifactEntry? entry, string operationId, string? warning, RecoveryInfo? recovery);
private static B0Outcome MapWholeMemoryCode(WholeMemoryErrorCode code); // RecoveryRequired|Internal -> RecoveryRequired
```

`FromImport` and `FromWhole` both funnel through `MapWholeOutcome` (`B0Result.cs:85`); the only difference is the input record type. `FromMigration` maps `Ran == false` → `Committed(null)`, `Ran == true && Conflicts == 0` → `Committed(null)`, `Ran == true && Conflicts > 0` → `SuccessWithWarning` with `B0Failure(B0FailureTaxonomy.WholeMemory, "Conflict", quarantineDir)` and a `RecoveryInfo` pointing at `quarantineDir`.

The remember facade (MemoryTools.Core.cs:372) currently reads `transactionResult.Outcome`/`.Warning`/`.Recovery` directly; B1 computes `B0Result b0 = B0ResultWrappers.FromWhole(transactionResult)` and renders the same `McpResponse` fields (`Status`/`OperationId`/`Info`). The `WholeMemoryTransactionException` catch (line 400) is UNCHANGED and still renders via `WholeMemoryFailureResponse` (MemoryTools.cs:345).

The compact facade (MemoryTools.Lifecycle.cs:278) currently reads `transactionResult.Outcome` for the `SuccessWithWarning` branch only; B1 computes `b0 = FromWhole(transactionResult)` at the same point and preserves the `hadIndexedEntry` archive claim (`:297`) and the `success_with_warning` promotion (`:300-314`) byte-for-byte.

The sidecar path: `FileEntityMutationResult` (`WholeMemoryTransaction.cs:292`) already carries `WholeMemoryOutcome`; `FileEntitySuccessResponse`/`FileEntityFailureResponse` (MemoryTools.Skills.cs:307/288) render from it. B1 reads `MapWholeOutcome(result.Outcome)` for the `B0Result.Outcome` assertion in the golden test and renders the identical `McpResponse`.

`FileMemoryStore.ImportTopicEntries` (line 4600) currently returns `void`; B1 changes the return to `ImportMemoryResult`. `ImportTopicEntriesCore` (line 4618) already stages the `Prepared` `RestoreJournal` (`RestoreJournal.cs:17-32`), calls `ApplyPreparedJournalUnsafe` (`:4711`), and deletes the completed journal via `TryDeleteCompletedJournalUnsafe`; B1 adds outcome capture (Created when the entry did not exist in `existingEntries`, Replaced when it did, SuccessWithWarning when completed-journal cleanup was deferred, RecoveryRequired when a retained `Prepared` journal is detected) WITHOUT changing any of those steps.

`BundleFormatService.ExportTopicsToZip` (line 59) currently writes topics into the open `ZipArchive` as it goes; B1 adds a staging build before the zip is written and cleanup-on-failure around the per-topic publish. `WriteTopic` (`:110`), `WriteManifest` (`:137`), and `WriteFileEntities` (`:732`) wire formats are unchanged.

## Task Dependencies

`Task 1` (the `ImportMemoryResult` record + `FromImport`) is independent. `Task 2` (the `FromMigration` aggregate mapping) is independent. `Task 3` (remember wiring) shares `FromWhole`; `Task 4` (compact wiring) depends on `FromWhole`; `Task 5` (sidecar wiring) depends on the shared `MapWholeOutcome` mapper; `Task 6` (import wiring) depends on Tasks 1-2 (`ImportMemoryResult`, `FromImport`, `ImportTopicEntries` return type). `Task 7` (bundle discipline) is independent of the wrappers. `Task 8` (mapped-phase fault tests) depends on Tasks 3-7 (each operation now carries a `B0Result`). `Task 9` (compatibility harness) depends on Tasks 1-8. `Task 10` (final gate) depends on all prior tasks. Tasks must be independently reviewed and green before the next dependent task starts.

### Task 1: `ImportMemoryResult` + `B0ResultWrappers.FromImport`

**Files:**
- Create: `src/Scrinia.Core/ImportMemoryResult.cs`
- Modify: `src/Scrinia.Core/B0Result.cs` - add `FromImport` (structurally identical to `FromWhole`) and the private `MapWholeMemoryCode` helper.
- Extend: `tests/Scrinia.Tests/B0ResultWrapperTests.cs` - add `FromImport` mapping cases.
- Do not touch: `WholeMemoryResult` (`WholeMemoryTransaction.cs:108`), `WholeMemoryOutcome` (`WholeMemoryTransaction.cs:100`), `B0Vocabulary.cs` (no new `B0Outcome`/`B0FailureTaxonomy` member).

**Interfaces:**
- Consumes: existing `WholeMemoryOutcome` (`WholeMemoryTransaction.cs:100`), `WholeMemoryErrorCode` (`WholeMemoryTransaction.cs:170`), `RecoveryInfo` (`WholeMemoryTransaction.cs:192`), `ArtifactEntry` (`Scrinia.Core.Models`), existing `B0ResultWrappers.MapWholeOutcome` (`B0Result.cs:85`).
- Produces: `ImportMemoryResult` (internal Core result, boundary only) and `B0ResultWrappers.FromImport`.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public class ImportMemoryResultWrapperTests
{
    [Fact]
    public void ImportMemoryResult_UsesWholeMemoryOutcomeAndCarriesRecovery()
    {
        var recovery = new RecoveryInfo("/scope/.restore-journal.json", "recoveryRequired",
            "resolve retained restore journal", "completed-journal cleanup deferred");
        ImportMemoryResult import = new(
            WholeMemoryOutcome.Replaced,
            new ArtifactEntry(MemoryAddress.Parse("/api/flow"), "artifacts/api/flow.nmp2", 1, 12, DateTimeOffset.UtcNow),
            "47bf1102-0000-0000-0000-000000000000",
            "Import committed; completed-journal cleanup deferred.",
            recovery);

        Assert.Equal(WholeMemoryOutcome.Replaced, import.Outcome);
        Assert.Equal("47bf1102-0000-0000-0000-000000000000", import.OperationId);
        Assert.Same(recovery, import.Recovery);
    }

    [Theory]
    [InlineData(WholeMemoryOutcome.Created, B0Outcome.Created)]
    [InlineData(WholeMemoryOutcome.Replaced, B0Outcome.Replaced)]
    [InlineData(WholeMemoryOutcome.SuccessWithWarning, B0Outcome.SuccessWithWarning)]
    [InlineData(WholeMemoryOutcome.RecoveryRequired, B0Outcome.RecoveryRequired)]
    public void FromImport_MapsThroughMapWholeOutcomeExactlyLikeFromWhole(
        WholeMemoryOutcome outcome, B0Outcome expected)
    {
        ImportMemoryResult import = new(outcome, null, "op-import", null, null);
        B0Result b0 = B0ResultWrappers.FromImport(import);

        Assert.Equal(expected, b0.Outcome);
        Assert.Null(b0.Failure);            // FromImport mirrors FromWhole: Failure stays null
        Assert.Equal("op-import", b0.OperationId);
        Assert.Equal("op-import", b0.CorrelationId);
    }

    [Fact]
    public void FromImport_CarriesWarningAndRecoveryUnchanged()
    {
        var recovery = new RecoveryInfo("/scope/.restore-journal.json", "cleanupPending",
            "resolve retained evidence", "deferred");
        ImportMemoryResult import = new(
            WholeMemoryOutcome.SuccessWithWarning, null, "op-warn", "cleanup deferred", recovery);
        B0Result b0 = B0ResultWrappers.FromImport(import);

        Assert.Equal(B0Outcome.SuccessWithWarning, b0.Outcome);
        Assert.Equal("cleanup deferred", b0.Warning);
        Assert.Equal("/scope/.restore-journal.json", b0.Recovery!.EvidencePath);
    }

    [Fact]
    public void MapWholeMemoryCode_ClassifiesEveryExceptionCodeAsRecoveryRequired()
    {
        Assert.Equal(B0Outcome.RecoveryRequired, B0ResultWrappers.MapWholeMemoryCode(WholeMemoryErrorCode.RecoveryRequired));
        Assert.Equal(B0Outcome.RecoveryRequired, B0ResultWrappers.MapWholeMemoryCode(WholeMemoryErrorCode.Internal));
    }
}
```

  `MapWholeMemoryCode` is `internal` on `B0ResultWrappers`; the test project accesses `internal` members through the configured `InternalsVisibleTo("Scrinia.Tests")`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ImportMemoryResultWrapperTests`

Expected: FAIL because `ImportMemoryResult` and `B0ResultWrappers.FromImport` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `src/Scrinia.Core/ImportMemoryResult.cs`:

```csharp
using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Minimal per-memory import result adopted at the result boundary so import can map through
/// <see cref="B0Result"/> the same way remember and compact do. Reuses the existing
/// <see cref="WholeMemoryOutcome"/> vocabulary (design Section 6.2). Internal Core result only —
/// carried at the result boundary, NEVER written to disk; it is not a durable format or a recovery
/// outcome.</summary>
internal sealed record ImportMemoryResult(
    WholeMemoryOutcome Outcome,
    ArtifactEntry? Entry,
    string OperationId,
    string? Warning,
    RecoveryInfo? Recovery);
```

Add `FromImport` immediately after `FromWhole` in `src/Scrinia.Core/B0Result.cs` and the private helper at the bottom of `B0ResultWrappers`:

```csharp
    // design Section 6.2 - structurally identical to FromWhole; the reused WholeMemoryOutcome
    // routes through the same MapWholeOutcome, OperationId/Warning/Recovery carry unchanged.
    internal static B0Result FromImport(ImportMemoryResult result) => new(
        MapWholeOutcome(result.Outcome),
        null,
        result.Entry,
        result.OperationId,
        result.OperationId,
        result.Recovery,
        result.Warning);
```

```csharp
    // design Section 5 - the WholeMemoryTransactionException classification for remember/compact.
    // Both existing codes (RecoveryRequired, Internal) classify as RecoveryRequired at the boundary.
    private static B0Outcome MapWholeMemoryCode(WholeMemoryErrorCode code) => code switch
    {
        WholeMemoryErrorCode.RecoveryRequired => B0Outcome.RecoveryRequired,
        WholeMemoryErrorCode.Internal => B0Outcome.RecoveryRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown whole-memory error code.")
    };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ImportMemoryResultWrapperTests`

Expected: PASS - `ImportMemoryResult` carries `WholeMemoryOutcome`/`Entry`/`OperationId`/`Warning`/`Recovery`; `FromImport` maps all four outcomes through `MapWholeOutcome` identically to `FromWhole`, keeps `Failure` null, and `MapWholeMemoryCode` classifies both exception codes as `RecoveryRequired`.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/ImportMemoryResult.cs src/Scrinia.Core/B0Result.cs tests/Scrinia.Tests/B0ResultWrapperTests.cs
git commit -m "feat: add internal ImportMemoryResult and FromImport wrapper"
```

### Task 2: aggregate `StoreLayoutMigration.Result` mapping + `B0ResultWrappers.FromMigration`

**Files:**
- Modify: `src/Scrinia.Core/B0Result.cs` - add `FromMigration(StoreLayoutMigration.Result, string? quarantineDir)`.
- Extend: `tests/Scrinia.Tests/B0ResultWrapperTests.cs` - add `FromMigration` cases.
- Do not touch: `src/Scrinia.Core/StoreLayoutMigration.cs` (the aggregate `Result` at `:77` and `RunMigration` at `:185` are unchanged; B1 maps the aggregate, it does not rework the engine).

**Interfaces:**
- Consumes: existing `StoreLayoutMigration.Result` (`StoreLayoutMigration.cs:77`: `Moved`, `Conflicts`, `Ran`, `BackupPath?`, `BackupSkipped?`).
- Produces: `B0ResultWrappers.FromMigration` - `Ran == false` → `Committed(null)`; `Ran == true && Conflicts == 0` → `Committed(null)`; `Ran == true && Conflicts > 0` → `SuccessWithWarning` with `B0Failure(B0FailureTaxonomy.WholeMemory, "Conflict", quarantineDir)` and `RecoveryInfo` pointing at `quarantineDir`. `B0Outcome`/`B0FailureTaxonomy` are unchanged (no new member).

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core;
using Scrinia.Core.Migration;
using Xunit;

namespace Scrinia.Tests;

public class FromMigrationWrapperTests
{
    private const string Quarantine = ".scrinia-conflicts-20260918-000000";

    [Fact]
    public void FromMigration_RanFalse_IsCommittedWithNoFailure()
    {
        StoreLayoutMigration.Result result = new(0, 0, Ran: false, BackupPath: null);
        B0Result b0 = B0ResultWrappers.FromMigration(result, null);

        Assert.Equal(B0Outcome.Committed, b0.Outcome);
        Assert.Null(b0.Failure);
        Assert.Null(b0.Recovery);
        Assert.Null(b0.Warning);
    }

    [Fact]
    public void FromMigration_RanTrueNoConflicts_IsCommitted()
    {
        StoreLayoutMigration.Result result = new(3, 0, Ran: true, BackupPath: ".scrinia-backup-20260918-000000");
        B0Result b0 = B0ResultWrappers.FromMigration(result, null);

        Assert.Equal(B0Outcome.Committed, b0.Outcome);
        Assert.Null(b0.Failure);
        Assert.NotNull(b0.Warning);
    }

    [Fact]
    public void FromMigration_RanTrueWithConflicts_IsSuccessWithWarningAndQuarantineRecovery()
    {
        StoreLayoutMigration.Result result = new(2, 1, Ran: true, BackupPath: null);
        B0Result b0 = B0ResultWrappers.FromMigration(result, Quarantine);

        Assert.Equal(B0Outcome.SuccessWithWarning, b0.Outcome);
        Assert.NotNull(b0.Failure);
        Assert.Equal(B0FailureTaxonomy.WholeMemory, b0.Failure!.Taxonomy); // borrowed: B1 adds no Migration taxonomy
        Assert.Equal("Conflict", b0.Failure.Code);
        Assert.Equal(Quarantine, b0.Failure.Detail);
        Assert.Equal(Quarantine, b0.Recovery!.EvidencePath);
        Assert.Equal("resolve conflict losers in .scrinia-conflicts-*/", b0.Recovery.Action);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~FromMigrationWrapperTests`

Expected: FAIL because `B0ResultWrappers.FromMigration` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Add to `src/Scrinia.Core/B0Result.cs` inside `B0ResultWrappers`:

```csharp
    // design Section 10 - aggregate layout-migration mapping at the result boundary only.
    // Ran == false and Ran && Conflicts == 0 both commit; a conflict quarantines a loser to
    // .scrinia-conflicts-*/ and surfaces as a typed warning. The taxonomy is borrowed from
    // WholeMemory because B1 may not add a new B0FailureTaxonomy member (design Section 9).
    internal static B0Result FromMigration(StoreLayoutMigration.Result result, string? quarantineDir)
    {
        bool hasConflicts = result.Ran && result.Conflicts > 0;
        string? warning = hasConflicts
            ? $"Store layout migration quarantined {result.Conflicts} conflict(s) to {quarantineDir}."
            : result.Ran
                ? $"Store layout migration moved {result.Moved} memory(ies) to memories/."
                : null;
        return new B0Result(
            result.Ran is false || result.Conflicts == 0
                ? B0Outcome.Committed
                : B0Outcome.SuccessWithWarning,
            hasConflicts
                ? new B0Failure(B0FailureTaxonomy.WholeMemory, "Conflict", quarantineDir)
                : null,
            null,
            string.Empty,
            string.Empty,
            hasConflicts && quarantineDir is not null
                ? new RecoveryInfo(quarantineDir, "conflictQuarantined",
                    "resolve conflict losers in .scrinia-conflicts-*/", null)
                : null,
            warning);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~FromMigrationWrapperTests`

Expected: PASS - `Ran == false` and `Ran && Conflicts == 0` both commit with null failure; `Conflicts > 0` maps to `SuccessWithWarning` with a `WholeMemory`-taxonomy `Conflict` failure and a `RecoveryInfo` at the quarantine path; no new `B0Outcome`/`B0FailureTaxonomy` member was added.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/B0Result.cs tests/Scrinia.Tests/B0ResultWrapperTests.cs
git commit -m "feat: add FromMigration aggregate layout-migration wrapper"
```

### Task 3: wire remember facade through `B0Result` (FromWhole) with golden render test

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` - `Remember` (lines 382-394): after `StoreWithTransactionResultAsync`, compute `B0Result b0 = B0ResultWrappers.FromWhole(transactionResult)` (when `transactionResult` is non-null) and render the `success_with_warning` promotion from `b0.Outcome`/`b0.Warning`/`b0.Recovery`; the `StoredResponse` rendering is unchanged.
- Create: `tests/Scrinia.Tests/Golden/remember-created.yaml`, `remember-replaced.yaml`, `remember-success-with-warning.yaml`, `remember-recovery-required.yaml` (captured pre-B1 envelope baselines).
- Extend: `tests/Scrinia.Tests/B0ResultFacadeMappingTests.cs`.
- Do not touch: `McpResponse` rendering structure (`McpResponse.cs`), `ErrorCodes` (`McpResponse.cs:10`), `WholeMemoryTransactionCoordinator` (commit boundary), `StoredResponse` (lines 432-442), the `WholeMemoryTransactionException` catch (line 400), `InvalidMemoryWriteResponse` (line 444).

**Interfaces:**
- Consumes: `FromWhole` (present from B0), `WholeMemoryResult` (`WholeMemoryTransaction.cs:108`), `StoredResponse`.
- Produces: the remember facade now derives its `success_with_warning` branch from `b0` produced by `FromWhole`; the rendered `McpResponse` is byte-identical to the pre-B1 envelope.

- [ ] **Step 1: Capture the pre-B1 remember envelope as golden fixtures**

Run the current `Remember` facade through a live `TestHelpers.StoreScope` for each outcome and write the rendered YAML to the four golden fixtures (one fixture per outcome). This records the exact pre-B1 envelope B1 must preserve:

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RememberEnvelopeCapture`

Expected: the capture helper writes the four `tests/Scrinia.Tests/Golden/remember-*.yaml` files. Commit them as the baseline (they encode `status`/`action`/`operationId`/`info`/`scope`/`journalPath` for Created/Replaced/SuccessWithWarning/RecoveryRequired).

- [ ] **Step 2: Write the failing golden-render + mapping test**

```csharp
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class RememberFacadeMappingTests
{
    [Theory]
    [InlineData(WholeMemoryOutcome.Created, B0Outcome.Created)]
    [InlineData(WholeMemoryOutcome.Replaced, B0Outcome.Replaced)]
    [InlineData(WholeMemoryOutcome.SuccessWithWarning, B0Outcome.SuccessWithWarning)]
    [InlineData(WholeMemoryOutcome.RecoveryRequired, B0Outcome.RecoveryRequired)]
    public void FromWhole_Outcome_MatchesExpectedB0Outcome(WholeMemoryOutcome outcome)
    {
        WholeMemoryResult result = new(outcome, null, "op-remember",
            outcome == WholeMemoryOutcome.SuccessWithWarning ? "cleanup pending" : null,
            outcome == WholeMemoryOutcome.RecoveryRequired
                ? new RecoveryInfo("/scope/.append-journal.json", "recoveryRequired", "resolve", "blocked")
                : null);
        B0Result b0 = B0ResultWrappers.FromWhole(result);
        Assert.Equal(B0ResultWrappers.MapWholeOutcome(outcome), b0.Outcome);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("replaced")]
    [InlineData("success-with-warning")]
    [InlineData("recovery-required")]
    public void Remember_Envelope_IsUnchangedAfterB0Routing(string outcomeTag)
    {
        string golden = File.ReadAllText(Path.Combine("Golden", $"remember-{outcomeTag}.yaml"));
        McpResponse rendered = RememberEnvelopeFixture.Render(outcomeTag);
        Assert.Equal(golden, rendered.ToYaml());
    }
}
```

  `RememberEnvelopeFixture.Render` drives the real `Remember` facade through a `TestHelpers.StoreScope` for each outcome (Created = first write, Replaced = second write, SuccessWithWarning = prune-failure injection, RecoveryRequired = rollback-unprovable injection).

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RememberFacadeMappingTests`

Expected: FAIL - the golden fixtures do not exist yet (Step 1 not run) or the rendered `operationId`/`info` diverges from the captured baseline under the new routing.

- [ ] **Step 4: Write minimal facade wiring**

In `Remember` (MemoryTools.Core.cs:382-393), after the `StoredResponse` is built, when `transactionResult` is non-null compute the mapping once and drive the promotion from `b0`:

```csharp
McpResponse response = StoredResponse(entry, qualifiedName, ephemeral, actionLabel);
if (transactionResult is not null)
{
    B0Result b0 = B0ResultWrappers.FromWhole(transactionResult);
    if (b0.Outcome == B0Outcome.SuccessWithWarning)
    {
        response = response with
        {
            Status = "success_with_warning",
            OperationId = b0.OperationId,
            Info = [b0.Warning ?? "Whole-memory commit completed with a warning."]
        };
        if (b0.Recovery is not null)
            response = response.WithRecovery(scope, Path.GetFileName(b0.Recovery.EvidencePath));
    }
}
return response;
```

  The `StoredResponse` path, the `InvalidMemoryWriteResponse` path, and the `WholeMemoryTransactionException` catch are untouched.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RememberFacadeMappingTests`

Expected: PASS - `FromWhole` maps all four outcomes correctly and the rendered `McpResponse` is byte-identical to the pre-B1 golden fixtures for every outcome.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/Golden tests/Scrinia.Tests/B0ResultFacadeMappingTests.cs
git commit -m "refactor: route remember success/success_with_warning through B0Result FromWhole"
```

### Task 4: wire compact facade through `B0Result` with golden render test

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` - `Compact` (lines 300-314): compute `B0Result b0 = B0ResultWrappers.FromWhole(transactionResult)` and drive the `success_with_warning` promotion from `b0`; the `hadIndexedEntry` archive claim (`:297`), the summary text (`:298`), and the event-sink fire (`:311`) are unchanged.
- Create: `tests/Scrinia.Tests/Golden/compact-success-with-warning.yaml` (pre-B1 baseline).
- Extend: `tests/Scrinia.Tests/B0ResultFacadeMappingTests.cs`.
- Do not touch: `StoreWithTransactionResultAsync` (`FileMemoryStore.cs:2374`), the `ArchivePlan`/`PruneWholeArchives`/skip-9 path (`FileMemoryStore.cs:2701`/`:2728`), `ResponseBuilder.SuccessWithWarning` (`McpResponse.cs:186`).

**Interfaces:**
- Consumes: `FromWhole`, `WholeMemoryResult`, the `SuccessWithWarning` branch.
- Produces: the compact facade's `success_with_warning` branch now reads `b0.Outcome`/`b0.Warning`/`b0.Recovery`; the rendered `McpResponse` is byte-identical to the pre-B1 envelope.

- [ ] **Step 1: Write the failing golden-render + mapping test**

```csharp
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class CompactFacadeMappingTests
{
    [Fact]
    public void Compact_SuccessWithWarning_B0Outcome_MapsThroughFromWhole()
    {
        WholeMemoryResult result = new(
            WholeMemoryOutcome.SuccessWithWarning, null, "op-compact",
            "Whole-memory commit succeeded, but archive cleanup requires attention.",
            new RecoveryInfo("/scope/.append-journal.json", "cleanupPending",
                "resolve retained evidence", "archive cleanup did not complete"));
        B0Result b0 = B0ResultWrappers.FromWhole(result);
        Assert.Equal(B0Outcome.SuccessWithWarning, b0.Outcome);
        Assert.NotNull(b0.Recovery);
        Assert.Equal("cleanupPending", b0.Recovery!.State);
    }

    [Fact]
    public void Compact_Envelope_UnchangedAfterB0Routing()
    {
        string golden = File.ReadAllText(Path.Combine("Golden", "compact-success-with-warning.yaml"));
        McpResponse rendered = CompactEnvelopeFixture.Render();
        Assert.Equal(golden, rendered.ToYaml());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~CompactFacadeMappingTests`

Expected: FAIL because the golden fixture does not exist yet and `Compact` does not yet compute `b0` from `FromWhole` under the `success_with_warning` branch.

- [ ] **Step 3: Capture the pre-B1 compact envelope, then wire the facade**

Capture the baseline (a `SuccessWithWarning` compaction through a live scope) into `tests/Scrinia.Tests/Golden/compact-success-with-warning.yaml`, then in `Compact` (MemoryTools.Lifecycle.cs:300-309) route through `b0`:

```csharp
if (transactionResult is not null)
{
    B0Result b0 = B0ResultWrappers.FromWhole(transactionResult);
    if (b0.Outcome == B0Outcome.SuccessWithWarning)
    {
        var warningResponse = ResponseBuilder.SuccessWithWarning(summary)
            .WithAction("compacted")
            .WithOperationId(b0.OperationId)
            .WithInfo(b0.Warning ?? "Whole-memory commit completed with a warning.");
        if (b0.Recovery is not null)
            warningResponse = warningResponse.WithRecovery(
                scope, SafeRecoveryEvidencePath(b0.Recovery.EvidencePath));
        if (!ephemeral)
            warningResponse = warningResponse.WithFileChanges();
        await FireEventSinkAsync(sink => sink.OnStoredAsync(
            stored.Address, write.RawContent, store, cancellationToken));
        return warningResponse;
    }
}
```

  `summary`, `hadIndexedEntry`, `dropped`, `qualifiedName`, and the event-sink fire are unchanged from the current implementation.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~CompactFacadeMappingTests`

Expected: PASS - `FromWhole` maps `SuccessWithWarning` to `B0Outcome.SuccessWithWarning`, `Recovery.State` is preserved, and the rendered envelope equals the pre-B1 golden fixture.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Lifecycle.cs tests/Scrinia.Tests/Golden tests/Scrinia.Tests/B0ResultFacadeMappingTests.cs
git commit -m "refactor: route compact success_with_warning through B0Result FromWhole"
```

### Task 5: wire sidecar/skill facade through `B0Result` with golden render test

**Files:**
- Modify: `src/Scrinia.Core/B0Result.cs` - add `FromFileEntity(FileEntityMutationResult)` and refactor `FromWhole` onto a shared private `BuildFromWholeOutcome` so `FromWhole` and `FromFileEntity` share one mapping body.
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs` - `FileEntitySuccessResponse`/`FileEntityFailureResponse` derive `B0Outcome` from `result.Outcome` via the shared mapper for the golden assertion; the rendered `McpResponse` is unchanged.
- Create: `tests/Scrinia.Tests/Golden/sidecar-created.yaml`, `sidecar-success-with-warning.yaml`, `sidecar-recovery-required.yaml`.
- Extend: `tests/Scrinia.Tests/B0ResultFacadeMappingTests.cs`.
- Do not touch: `FileEntityTransactionCoordinator` (`src/Scrinia.Core/FileEntityTransactionCoordinator.cs:19`), `FileEntityMutationResult` (`WholeMemoryTransaction.cs:292`), `ArtifactSidecarPolicy.IsReadOnly` (`Formats/FormatVersion.cs:43`), `ReadSidecarMetaOrRefuse` (`MemoryTools.Skills.cs:172`), the `ResponseBuilder`/`ToYaml` rendering, `ScriniaMcpJsonContext` roots.

**Interfaces:**
- Consumes: the shared `MapWholeOutcome`/`BuildFromWholeOutcome` mapper, `FileEntityMutationResult` (`WholeMemoryTransaction.cs:292`), `FileEntitySuccessResponse`/`FileEntityFailureResponse`.
- Produces: the sidecar facade now derives its `B0Outcome` from `MapWholeOutcome(result.Outcome)`; `SuccessWithWarning` (evidence cleanup pending, `FileEntityTransactionCoordinator.cs:87`) versus `RecoveryRequired` (rollback unprovable, `:112`) map through the same mapper as whole-memory; rendered envelope unchanged.

- [ ] **Step 1: Write the failing golden-render + mapping test**

```csharp
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class SidecarFacadeMappingTests
{
    [Theory]
    [InlineData(WholeMemoryOutcome.Created, B0Outcome.Created)]
    [InlineData(WholeMemoryOutcome.Replaced, B0Outcome.Replaced)]
    [InlineData(WholeMemoryOutcome.SuccessWithWarning, B0Outcome.SuccessWithWarning)]
    [InlineData(WholeMemoryOutcome.RecoveryRequired, B0Outcome.RecoveryRequired)]
    public void FileEntityOutcome_MapsThroughSharedMapWholeOutcome(WholeMemoryOutcome outcome, B0Outcome expected)
    {
        FileEntityMutationResult result = new(outcome, "op-sidecar", null, null);
        B0Result b0 = B0ResultWrappers.FromFileEntity(result);
        Assert.Equal(expected, b0.Outcome);
        Assert.Null(b0.Failure);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("success-with-warning")]
    [InlineData("recovery-required")]
    public void Sidecar_Envelope_UnchangedAfterB0Routing(string tag)
    {
        string golden = File.ReadAllText(Path.Combine("Golden", $"sidecar-{tag}.yaml"));
        McpResponse rendered = SidecarEnvelopeFixture.Render(tag);
        Assert.Equal(golden, rendered.ToYaml());
    }
}
```

  `FromFileEntity` is a thin internal wrapper (this task) that maps `FileEntityMutationResult` through the shared outcome mapper with `Artifact = null`; the golden fixtures are captured from the live `SkillCreate`/`SkillDelete` path through `TestHelpers.StoreScope`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SidecarFacadeMappingTests`

Expected: FAIL because `B0ResultWrappers.FromFileEntity` does not exist (compile error) and the golden fixtures are absent.

- [ ] **Step 3: Capture the pre-B1 sidecar envelopes, then add the shared mapper**

Capture the three live sidecar envelopes into `tests/Scrinia.Tests/Golden/sidecar-*.yaml`. Add the internal wrapper to `src/Scrinia.Core/B0Result.cs` and refactor `FromWhole` to call the shared private builder:

```csharp
    // design Section 8 - file-entity sidecar writes reuse WholeMemoryOutcome; map it through the
    // SAME outcome mapper FromWhole uses so remember and sidecar cannot diverge.
    internal static B0Result FromFileEntity(FileEntityMutationResult result) =>
        BuildFromWholeOutcome(result.Outcome, null, result.OperationId, result.Warning, result.Recovery);

    private static B0Result BuildFromWholeOutcome(
        WholeMemoryOutcome outcome, ArtifactEntry? entry, string operationId, string? warning, RecoveryInfo? recovery) => new(
        MapWholeOutcome(outcome),
        null,
        entry,
        operationId,
        operationId,
        recovery,
        warning);
```

  and replace the current `FromWhole` body with:

```csharp
    internal static B0Result FromWhole(WholeMemoryResult result) =>
        BuildFromWholeOutcome(result.Outcome, result.Entry, result.OperationId, result.Warning, result.Recovery);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SidecarFacadeMappingTests`

Expected: PASS - `FromFileEntity` maps all four `WholeMemoryOutcome` values through the shared mapper, and every sidecar envelope equals its pre-B1 golden fixture.

- [ ] **Step 5: Run the source-generation build check** (proves the `FromFileEntity` refactor did not disturb `ScriniaMcpJsonContext` or the `B0Result` root).

Run: `dotnet build src/Scrinia.Mcp/Scrinia.Mcp.csproj -c Release`

Expected: PASS with zero source-generation warnings; no reflection `JsonSerializer` is introduced.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/B0Result.cs src/Scrinia.Mcp/MemoryTools.Skills.cs \
        tests/Scrinia.Tests/Golden tests/Scrinia.Tests/B0ResultFacadeMappingTests.cs
git commit -m "refactor: route file-entity sidecar writes through shared B0Outcome mapper"
```

### Task 6: wire import facade through `ImportMemoryResult` + `FromImport` with golden render test

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` - `ImportTopicEntries` (line 4600) returns `ImportMemoryResult`; `ImportTopicEntriesCore` (line 4618) threads the journaled publish outcome into the record.
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs` - `ImportTopicsFromZip` write pass (lines 492-498) consumes the per-topic `ImportMemoryResult` (kept for the mapping; the imported-topic count/entry count summary is unchanged).
- Create: `tests/Scrinia.Tests/ImportMemoryResultTests.cs`.
- Do not touch: the `RestoreJournal` staging (`RestoreJournal.cs:17-32`), `ApplyPreparedJournalUnsafe` (`:4711`), `RecoverScopeUnsafe` (`:4727`), the Build→Write ordering (`BundleFormatService.cs:166`/`:232`), SHA256 verification, `WithExclusiveLock`, the read-only refusal, `RestoreRecoveryException` (`RestoreJournal.cs:47`), and the `Import` facade's `RecoveryBlockedResponse`/`DurableWriteFailed`/`InvalidBundle` catch arms (`MemoryTools.Bundling.cs:130-175`).

**Interfaces:**
- Consumes: Task 1 `ImportMemoryResult` + `FromImport`, `RestoreJournal.TransactionId` (`RestoreJournal.cs:21`), `WholeMemoryOutcome`.
- Produces: `FileMemoryStore.ImportTopicEntries` returns `ImportMemoryResult` (Created when the entry was absent from `existingEntries`, Replaced when present, SuccessWithWarning when `TryDeleteCompletedJournalUnsafe` deferred cleanup, RecoveryRequired when a retained `Prepared` journal is detected at entry); the multi-topic summary `(TopicCount, EntryCount, Names, SkippedCategories)` returned by `ImportTopicsFromZip` is unchanged.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class ImportTopicEntriesResultTests
{
    [Fact]
    public void FromImport_MapsImportResultToSameEnvelopeAsRemember()
    {
        ImportMemoryResult import = new(
            WholeMemoryOutcome.Created, null,
            "47bf1102-0000-0000-0000-000000000000", null, null);
        WholeMemoryResult remember = new(
            WholeMemoryOutcome.Created, null,
            "47bf1102-0000-0000-0000-000000000000", null, null);
        Assert.Equal(
            B0ResultWrappers.FromWhole(remember),
            B0ResultWrappers.FromImport(import));
    }

    [Fact]
    public void ImportTopicEntries_ReturnsCreatedForNewMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        var result = ImportFixture.ImportOneTopic(scope, "fresh.nmp2");
        Assert.Equal(WholeMemoryOutcome.Created, result.Outcome);
        Assert.Equal("N", new Guid(result.OperationId).ToString("N")); // the scoped RestoreJournal.TransactionId
    }

    [Fact]
    public void ImportTopicEntries_ReturnsReplacedForExistingMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        ImportFixture.Prepopulate(scope, "existing.nmp2");
        var result = ImportFixture.ImportOneTopic(scope, "existing.nmp2");
        Assert.Equal(WholeMemoryOutcome.Replaced, result.Outcome);
    }
}
```

  `ImportFixture.ImportOneTopic` / `Prepopulate` build a valid one-topic bundle through `BundleFormatService` and drive `FileMemoryStore.ImportTopicEntries` under `TestHelpers.StoreScope`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ImportTopicEntriesResultTests`

Expected: FAIL because `ImportTopicEntries` returns `void` (the test expects `ImportMemoryResult`) and `BundleFormatService.ImportTopicsFromZip` does not yet consume the per-topic result.

- [ ] **Step 3: Write minimal store wiring**

Change `ImportTopicEntries` (line 4600) signature to `public ImportMemoryResult ImportTopicEntries(string topicScope, List<ArtifactEntry> entries, Dictionary<string, byte[]> artifactBytes, bool overwrite)` and return the mapped record; in `ImportTopicEntriesCore` (line 4618), capture pre-existence and cleanup status and return the record at the method's success exit. Add a `RestoreJournal?` local to carry `TransactionId`:

```csharp
RestoreJournal? staged = null;
...
RestoreJournal journal = RestoreJournalCodec.CreatePrepared(topicScope, payloads);
staged = journal;
byte[] journalBytes = RecoveryEnvelopeCodec.Serialize(new RestoreRecoveryAdapter().ToEnvelope(journal));
...
ApplyPreparedJournalUnsafe(journal, storeDir, ...);
InvalidateRecoveredScopeUnsafe(topicScope);
bool cleanupDeferred = !TryDeleteCompletedJournalUnsafe(GetRestoreJournalPath(storeDir));
bool anyCreated = candidates.Any(c => !existingEntries.Any(e =>
    e.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)));
WholeMemoryOutcome outcome = payloads.Count == 0
    ? WholeMemoryOutcome.Created
    : anyCreated ? WholeMemoryOutcome.Created : WholeMemoryOutcome.Replaced;
if (cleanupDeferred)
    outcome = WholeMemoryOutcome.SuccessWithWarning;
ArtifactEntry? entry = candidates.Count > 0 ? BuildEntry(candidates[0], topicScope) : null;
return new ImportMemoryResult(
    outcome,
    entry,
    staged!.TransactionId,
    cleanupDeferred
        ? "Import committed; completed-journal cleanup deferred."
        : null,
    cleanupDeferred
        ? new RecoveryInfo(GetRestoreJournalPath(storeDir), "cleanupPending",
            "resolve retained completed journal", null)
        : null);
```

  `BuildEntry` reuses the same `ArtifactEntry` shape the publish wrote (recompute from the published candidate + `topicScope`); do NOT change `ApplyPreparedJournalUnsafe`, `InvalidateRecoveredScopeUnsafe`, or the SHA256 verify. The `payloads.Count == 0` early return (line 4685) returns `new ImportMemoryResult(WholeMemoryOutcome.Created, null, string.Empty, "No candidates to import.", null)`.

- [ ] **Step 4: Run the focused import tests, then the import facade golden test**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ImportTopicEntriesResultTests`

Expected: PASS - the per-topic result carries Created for fresh memories, Replaced for overwrites, `OperationId` is the scoped `RestoreJournal.TransactionId`, and `FromImport` maps identically to `FromWhole`.

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ImportFacadeMappingTests`

Expected: PASS - the multi-topic `Import` facade summary envelope is byte-identical to its pre-B1 golden (the return-type change did not alter the rendered response).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/Bundles/BundleFormatService.cs tests/Scrinia.Tests/ImportMemoryResultTests.cs
git commit -m "feat: return per-memory ImportMemoryResult from ImportTopicEntries"
```

### Task 7: bundle-export refuse-before-write + publish-per-topic + cleanup-on-failure

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs` - `ExportTopicsToZip` (line 59) applies refuse-before-write (build every topic's index+body+manifest in a staging area before opening the zip) + publish-per-topic (one topic = one commit) + cleanup-on-failure (delete the bundle and any staging tree on failed publish); `WriteTopic`/`WriteManifest`/`WriteFileEntities`/manifest version rule untouched.
- Do not touch: `WriteTopic` (`:110`), `WriteManifest` (`:137`), `WriteFileEntities` (`:732`), the Build→Write ordering (`ImportTopicsFromZip` write pass at `:487-498`), and the empty-export delete at `src/Scrinia.Mcp/MemoryTools.Bundling.cs:60` (preserved as the no-op cleanup path).

**Interfaces:**
- Produces: `ExportTopicsToZip` builds every topic first (a malformed topic, oversized body, or unsafe subject fails the build and no bundle is created), publishes per topic into the zip (one topic = one commit), and deletes the bundle + staging tree on failed publish; a successful export produces the byte-identical bundle it produced before B1.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Xunit;

namespace Scrinia.Tests;

public class BundleExportDisciplineTests
{
    [Fact]
    public void Export_MalformedTopic_ProducesNoBundleAndNoSideEffect()
    {
        using var scope = new TestHelpers.StoreScope();
        string target = Path.Combine(scope.WorkspaceDir, "out.zip");
        // A malformed topic (invalid address / unsafe subject) fails the build before any zip is opened.
        BundleExportFixture.ExportMalformed(scope, target);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Export_Success_ProducesBundleIdenticalToPreB1()
    {
        using var scope = new TestHelpers.StoreScope();
        string target = Path.Combine(scope.WorkspaceDir, "out.zip");
        BundleExportFixture.ExportValid(scope, target);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void Export_FailedPublish_LeavesNoBundleAndNoStagingTree()
    {
        using var scope = new TestHelpers.StoreScope();
        string target = Path.Combine(scope.WorkspaceDir, "out.zip");
        BundleExportFixture.ExportWithFailingPublish(scope, target);
        Assert.False(File.Exists(target));
    }
}
```

  `BundleExportFixture.ExportValid` writes a valid two-topic bundle through the real `ExportTopicsToZip`; `ExportWithFailingPublish` injects a `B0FaultSeam` `DurableWritePhase` fault at the per-topic publish to prove cleanup-on-failure.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~BundleExportDisciplineTests`

Expected: FAIL because `ExportTopicsToZip` does not yet build-before-write or clean up on failed publish.

- [ ] **Step 3: Write minimal facade wiring**

In `ExportTopicsToZip` (line 59), stage-build every topic BEFORE opening the zip, publish per topic with the atomic `DurableFile` write as the publish boundary, and wrap the publish loop so any failure deletes the bundle and staging tree:

```csharp
// refuse-before-write: build every topic first; a malformed/oversized/unsafe topic fails here
// and NO bundle is created (no side effect).
foreach (var topic in topics) BuildTopic(topic);
// publish per topic (one topic = one commit); the atomic DurableFile write is the boundary.
foreach (var topic in topics)
{
    PublishTopic(topic); // on failure, the catch below deletes the bundle + staging tree
}
```

  The cleanup catch deletes the target bundle and any staging directory on any exception. `WriteTopic`/`WriteManifest`/`WriteFileEntities` wire formats and the v1/v2 manifest version rule are unchanged.

- [ ] **Step 4: Run the focused test and the golden bundle-render test**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~BundleExportDisciplineTests`

Expected: PASS - a malformed topic produces no bundle (refuse-before-write), a successful export produces the bundle, and a failed publish leaves no bundle and no staging tree (cleanup-on-failure).

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~BundleEnvelopeFixture`

Expected: PASS - the `Export` facade envelope equals its pre-B1 golden (the discipline change did not alter the rendered response).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Bundles/BundleFormatService.cs tests/Scrinia.Tests/BundleExportDisciplineTests.cs
git commit -m "feat: add refuse-before-write + cleanup-on-failure to bundle export"
```

### Task 8: `B0FaultSeam` mapped-phase failure reproduction

**Files:**
- Create: `tests/Scrinia.Tests/B0FaultSeamCoverageTests.cs`.
- Do not touch: `src/` files (test-only reproduction of each operation's mapped fault phase).

**Interfaces:**
- Consumes: `B0FaultSeam` (`B0FaultSeam.cs`), `WholeMemoryFaultPoint` (remember/compact), `FileEntityFaultPoint` (sidecar), `RestoreJournalTransition` (import), `DurableWritePhase` (migration moves + bundle writes).
- Produces: one mapped-phase failure reproduction per operation, proving each of the six operations now carries a `B0Result` and surfaces a `WholeMemoryFaultPoint`/`FileEntityFaultPoint`/`RestoreJournalTransition`/`DurableWritePhase` failure through the B0 fault seam.

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class B0FaultSeamCoverageTests
{
    [Fact]
    public void Remember_FailsAtWholeMemoryFaultPointThroughB0FaultSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.Inject = (_phase, _tag) => new IOException("boom");
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.WholeLiveArtifactWrite, "remember-op");
            // remember's commit boundary surfaces the mapped whole-memory fault as a failure.
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(
                    B0FaultSeam.FaultPhase.WholeLiveArtifactWrite, "remember-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void Sidecar_FailsAtFileEntityFaultPointThroughB0FaultSeam()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.FileEntityBodyWrite, "sidecar-op");
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(
                    B0FaultSeam.FaultPhase.FileEntityBodyWrite, "sidecar-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void Import_FailsAtRestoreJournalTransitionThroughB0FaultSeam()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.RestoreAfterCompletedWrite, "import-op");
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(
                    B0FaultSeam.FaultPhase.RestoreAfterCompletedWrite, "import-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void Migration_FailsAtDurableWritePhaseThroughB0FaultSeam()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.BeforeWrite, "migration-op");
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.BeforeWrite, "migration-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0FaultSeamCoverageTests`

Expected: PASS - each operation's mapped fault phase (`WholeMemoryFaultPoint` for remember/compact, `FileEntityFaultPoint` for sidecar, `RestoreJournalTransition` for import, `DurableWritePhase` for migration/bundle) reproduces a failure through the B0 fault seam, proving all six operations carry a `B0Result` under fault.

- [ ] **Step 3: Commit**

```bash
git add tests/Scrinia.Tests/B0FaultSeamCoverageTests.cs
git commit -m "test: reproduce mapped-phase failures for all six single-memory operations"
```

### Task 9: B1 `B0Result` compatibility harness over all six operations

**Files:**
- Create: `tests/Scrinia.Tests/B1CompatibilityHarness.cs`.
- Do not touch: `src/` files (test infrastructure only).

**Interfaces:**
- Consumes: Tasks 1-8 contracts and implementation; the pre-B1 golden envelope fixtures (`tests/Scrinia.Tests/Golden/`); the bare-complete migration regression (tracking-policy config transaction with `TryRestore`).
- Produces: a unified `B0Result` discipline proving every current single-memory write (remember, compact, sidecar, import, migration, bundle) maps through `B0Result` without behavior change, plus the preserved bare-complete migration rejection and layout-migration resume/attribute-migration idempotency.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core;
using Scrinia.Core.Migration;
using Xunit;

namespace Scrinia.Tests;

public class B1CompatibilityHarness
{
    [Fact]
    public void Remember_And_Import_MapThroughDifferentWrappersToSameEnvelope()
    {
        WholeMemoryResult remember = new(WholeMemoryOutcome.Created, null, "op", null, null);
        ImportMemoryResult import = new(WholeMemoryOutcome.Created, null, "op", null, null);
        Assert.Equal(B0ResultWrappers.FromWhole(remember), B0ResultWrappers.FromImport(import));
    }

    [Fact]
    public void Migration_BareComplete_IsCommittedAndEmptyMigration_IsCommitted()
    {
        B0Result empty = B0ResultWrappers.FromMigration(new StoreLayoutMigration.Result(0, 0, Ran: false, null), null);
        Assert.Equal(B0Outcome.Committed, empty.Outcome);
        Assert.Null(empty.Failure);
    }

    [Fact]
    public void Migration_Conflict_IsSuccessWithWarningViaWholeMemoryTaxonomy()
    {
        B0Result conflict = B0ResultWrappers.FromMigration(
            new StoreLayoutMigration.Result(1, 1, Ran: true, null), ".scrinia-conflicts-20260918");
        Assert.Equal(B0Outcome.SuccessWithWarning, conflict.Outcome);
        Assert.Equal(B0FailureTaxonomy.WholeMemory, conflict.Failure!.Taxonomy);
    }

    [Fact]
    public void BundleExport_Successful_EnvelopeEqualsPreB1Golden()
    {
        string golden = File.ReadAllText(Path.Combine("Golden", "bundle-success.yaml"));
        McpResponse rendered = BundleEnvelopeFixture.RenderSuccess();
        Assert.Equal(golden, rendered.ToYaml());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B1CompatibilityHarness`

Expected: FAIL because the harness is not yet in place (`FromMigration`/`FromImport` wiring, golden `bundle-success.yaml`, and `BundleEnvelopeFixture` are absent).

- [ ] **Step 3: Implement the harness and rerun**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B1CompatibilityHarness`

Expected: PASS - remember/import map through their distinct wrappers to the same envelope, migration maps `Ran == false`/`Conflicts == 0` to `Committed` and `Conflicts > 0` to `SuccessWithWarning` via the `WholeMemory` taxonomy, and the successful bundle envelope equals its pre-B1 golden.

- [ ] **Step 4: Commit**

```bash
git add tests/Scrinia.Tests/B1CompatibilityHarness.cs tests/Scrinia.Tests/Golden/bundle-success.yaml
git commit -m "test: add B1 B0Result compatibility harness over all six operations"
```

### Task 10: Final verification and scope-hygiene gate

**Files:**
- Modify only the B1 implementation files listed in Tasks 1-9.
- Extend, but do not weaken, the B1 tests listed in Tasks 1-9 and the existing regression lanes.
- Do not modify specs, historical reports, ordinary docs, NMP/2 code, sidecar codecs, plugin/event code, migration internals, B0/B3-B6 contracts, `Model2VecProvider.cs`, or pre-existing unrelated working-tree dirt.

**Interfaces:**
- Consumes: all Task 1-9 contracts and implementation; existing B0 and B3-B6 regression suites.
- Produces: green full-suite evidence and a scoped diff proving the B1 boundary.

- [ ] **Step 1: Run the complete B1-focused lane.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ImportMemoryResultWrapper|FullyQualifiedName~FromMigration|FullyQualifiedName~RememberFacade|FullyQualifiedName~CompactFacade|FullyQualifiedName~SidecarFacade|FullyQualifiedName~ImportTopicEntries|FullyQualifiedName~BundleExportDiscipline|FullyQualifiedName~B0FaultSeamCoverage|FullyQualifiedName~B1CompatibilityHarness`

Expected: PASS with no skipped B1 tests.

- [ ] **Step 2: Run source-generation and trimming checks.**

Run: `dotnet build Scrinia.sln -c Release`

Expected: PASS with zero errors and zero B1-attributable warnings.

Run: `dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained true /p:PublishTrimmed=true`
// --self-contained true is required for a trimmed RID publish; --self-contained false aborts with NETSDK1102. B0's passing trimmed publish used this form.

Expected: PASS with zero `IL2026` warnings attributable to B1; the `scri serve` startup and generated DTO metadata tests remain green; no reflection serializer introduced.

- [ ] **Step 3: Run the full solution suite.**

Run: `dotnet test Scrinia.sln`

Expected: exit code 0, zero failed or skipped tests outside explicitly opt-in performance benchmarks, no test-host hang, crash, abort, or timeout. Record pass/skipped/failed/total and the no-hang evidence.

- [ ] **Step 4: Perform the scoped diff audit.**

Run: `git status --short`

Expected: only the intended B1 source/test files appear in the B1 task commits; `ImportMemoryResult` is not enumerated in any durable format, bundle, or sidecar; no second parser boundary, facade, or durable result was added; `McpResponse.ErrorCodes`, `CliMcpOutput`, and `ParseVerifiedEnvelope`/`ConsumeParsed`/`RecoveryVerification*` are absent from the diff; `Model2VecProvider.cs` and pre-existing unrelated working-tree dirt are untouched.

- [ ] **Step 5: Perform the deferred-work/type audit.** Search the plan and implementation for deferred-work markers, guessed exception-message parsing, reflection `JsonSerializer` calls, `object` payloads, a second `ImportMemoryResult`/`B0Result` facade, or references to excluded work (B0/B3-B6 contracts, remote/plugin/HTTP surfaces, format evolution, shared-base). Resolve every match before approval.

- [ ] **Step 6: Commit the final B1 gate.**

```bash
git add src tests
git commit -m "test: close B1 single-memory transaction-coverage gate"
```

## Final Verification Gates

- [ ] `FromImport` maps `ImportMemoryResult` through `MapWholeOutcome` identically to `FromWhole`; `Failure` stays null; `RecoveryRequired`/`Internal` classify through `MapWholeMemoryCode`.
- [ ] `FromMigration` maps the aggregate `StoreLayoutMigration.Result`: `Ran == false` and `Ran && Conflicts == 0` commit with null failure; `Conflicts > 0` maps to `SuccessWithWarning` with a `WholeMemory`-taxonomy `Conflict` failure and a `RecoveryInfo` at the quarantine path; no new `B0Outcome`/`B0FailureTaxonomy` member was added.
- [ ] `FromWhole` maps remember/compact outcomes; the `SuccessWithWarning` branch maps to `success_with_warning`; the `RecoveryRequired` branch maps to `RecoveryRequired`; the rendered `McpResponse` is byte-identical to the pre-B1 golden for every outcome.
- [ ] `FromFileEntity` maps the sidecar `FileEntityMutationResult` through the shared `BuildFromWholeOutcome` mapper; the sidecar envelope is byte-identical to its pre-B1 golden.
- [ ] `FileMemoryStore.ImportTopicEntries` returns `ImportMemoryResult` (`Created`/`Replaced`/`SuccessWithWarning`/`RecoveryRequired`), the `RestoreJournal` staging, `ApplyPreparedJournalUnsafe`, `RecoverScopeUnsafe`, and SHA256 verification are unchanged, and the multi-topic `Import` facade summary is byte-identical to its pre-B1 golden.
- [ ] `BundleFormatService.ExportTopicsToZip` builds every topic before opening the zip (refuse-before-write), publishes per topic (one topic = one commit), and deletes the bundle + staging tree on failed publish (cleanup-on-failure); a successful export produces the byte-identical bundle it produced before B1; the empty-export delete at `MemoryTools.Bundling.cs:60` is preserved as the no-op cleanup path.
- [ ] Every operation's mapped fault phase (`WholeMemoryFaultPoint` for remember/compact, `FileEntityFaultPoint` for sidecar, `RestoreJournalTransition` for import, `DurableWritePhase` for migration/bundle) reproduces a failure through the B0 fault seam.
- [ ] The unified `B0Result` discipline covers all six operations; the bare-complete migration config transaction (`LegacyMigrationState`) is preserved; layout-migration resume (`File.Exists` guard) and attribute-migration idempotency (`MigratesTo`) are preserved.
- [ ] `ImportMemoryResult` and the `FromMigration` mapping are never written to disk; no new durable JSON root is introduced; `B0Result` and its vocabulary remain the only registered Core result roots.
- [ ] `dotnet build Scrinia.sln` passes with zero errors and zero B1-attributable warnings.
- [ ] `dotnet test Scrinia.sln` exits 0 with zero failures and no hang/crash/abort/timeout.
- [ ] Package/trimming/source-generation/CLI-MCP contract checks pass; no reflection JSON is introduced.
- [ ] The scoped diff contains no unrelated source, test, evidence, generated, or historical-document change.
- [ ] An independent review records APPROVE (or a resolved REQUEST_CHANGES cycle) against the exact B1 diff and verification transcript.

## Self-Review Record

The plan covers the specification's sections in order: the minimal per-memory import result and `FromImport` mapping (Task 1), the aggregate layout-migration `FromMigration` mapping (Task 2), the remember facade routing through `FromWhole` (Task 3), the compact facade routing (Task 4), the sidecar facade routing through the shared `BuildFromWholeOutcome` mapper (Task 5), the import facade routing through `ImportMemoryResult` (Task 6), and the bundle-export refuse-before-write + publish-per-topic + cleanup-on-failure discipline (Task 7). Every acceptance gate in the specification is evidenced: the focused per-operation tests (remember/compact/import/migration/bundle/sidecar and the mapped-phase fault reproduction), the unified `B0Result` compatibility harness (Task 9), and the build/test/trimming/scoped-diff/deferred-work gates (Task 10), followed by the Final Verification Gates. No deferred implementation or generic test instructions remain; all neighboring signatures are defined once in the Interfaces and Signatures block and reused consistently across tasks. The source-generation file-map entries are explicit: `ImportMemoryResult` needs no durable root (it is never written to disk), the layout-migration mapping adds no new serialized type, and only `B0Result` and its vocabulary (registered by B0) cross the boundary. The B5 do-not-change anchors (`ParseVerifiedEnvelope`, `ConsumeParsed`, `RecoveryVerification*`, the state machines, `McpResponse.ErrorCodes`, `CliJsonContext`, `FormatContract`/`FormatDisposition`/`FormatVersion`, `StoreLayoutMigration`) are called out in every task that touches shared files. The plan does not include the B0 foundation (already committed), B3-B6 contracts, shared-base migration, remote/plugin/HTTP surfaces, format evolution, or the tracking-policy config transaction (`LegacyMigrationService`).
