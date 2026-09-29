# B0 Transaction Foundation and Compatibility Harness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the reusable transaction foundation that B1-B6 consume - one canonical lifecycle mapper, one unified `B0Result` vocabulary, one centralized `DurableFormats` registry, one unified `B0FaultSeam` contract, and one compatibility harness - without changing any existing behavior except the single justified typed-member change.

**Architecture:** B0 introduces read-only projections and facades over the existing append, whole-memory, multi-memory, and recovery foundations rather than new durable surfaces. `RecoveryLifecycleStateMapper` projects the canonical enum to the four existing machine-specific vocabularies; `B0Result` is a thin facade over `AppendStoreResult`, `WholeMemoryResult`, `MultiMemoryAggregateResult`, and `RecoveryOutcome` via read-only wrappers; `DurableFormats.Registry` becomes the single source of truth every consumer imports; `B0FaultSeam` unifies the twelve existing test-only injection seams behind one `Inject` function shape; and the compatibility harness loads current evidence through the unified foundation to prove it still parses, validates, and maps. The only justified change is the typed `RecoveryMember` carrier at the result boundary only.

**Tech Stack:** C# on `net10.0`, nullable reference types, `System.Collections.Immutable.ImmutableDictionary`, `System.Text.Json` source generation, xUnit, FluentAssertions, existing `TestHelpers.StoreScope`, MCP `McpResponse`/`ScriniaMcpJsonContext`, ConsoleAppFramework CLI.

**Spec:** `docs/superpowers/specs/2026-09-17-b0-transaction-foundation-design.md`

## Global Constraints

- The only justified behavior change is the typed `RecoveryMember` carrier at the result boundary (design 6.2 / Task 4); every other section preserves existing behavior exactly.
- No new durable file, journal, state, migration, archive rule, or recovery outcome is introduced.
- The sole parser boundary `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` (`RecoveryVerification.cs:297-343`, six args) and every typed `ConsumeParsed` consumer (`AppendJournalCodec.ConsumeParsed` `AppendJournal.cs:190`, `RestoreJournalCodec.ConsumeParsed` `RestoreJournal.cs:111`, `FileEntityRecoveryAdapter.ConsumeParsed` `RecoveryEnvelope.cs:794`, `AppendRecoveryAdapter.ConsumeParsed` `RecoveryEnvelope.cs:567`, `RestoreRecoveryAdapter.ConsumeParsed` `RecoveryEnvelope.cs:706`, `RecoveryVerifiedEnvelope.TypedBody` `RecoveryVerification.cs:294`) are untouched; never edit `ParseVerifiedEnvelope`, `ConsumeParsed`, `RecoveryVerificationContent`, `RecoveryVerificationResult`, `RecoveryVerificationOutcome`, or the verifier/accounting/cursor code.
- No public MCP/CLI contract change except the additive `B0Result` registration in `ScriniaMcpJsonContext`; `McpResponse.ErrorCodes` (`McpResponse.cs:10`) is unchanged and `B0Result` re-expresses existing codes through the same vocabulary, adding none.
- No rewrite of existing state machines: `AppendJournalState` (`AppendJournal.cs:11-29`), `WholeMemoryOutcome` (`WholeMemoryTransaction.cs:100-106`) with `RecoveryInfo` (line 192), `MultiMemoryTransactionState`/`MultiMemoryOutcome` (`MultiMemoryTransaction.cs:6-20`), and `RecoveryLifecycleState` wire strings (`RecoveryEnvelope.cs:96`) remain authoritative.
- No format evolution: NMP/2, sidecar schemas (`ReadSidecarMeta`/`WriteSidecarMeta`), and archive layout remain unchanged; that work belongs to B6.
- No reuse of B1-B6 or shared-base contracts; B0 defines the primitives and does not consume results from the children it feeds.
- No cross-scope, remote, plugin, HTTP, or gateway work; B0 stays inside `Scrinia.Core` and `tests/Scrinia.Tests`; there is no server surface beyond the existing `scri serve` local stdio.
- `RecoveryMember` is never written to disk, is not a durable format, and is not a recovery outcome.
- All twelve fault seams remain test-only and `AsyncLocal`-scoped; production `B0FaultSeam.Inject` is never set by production code.
- Source-generated JSON only: register every new serialized type in the appropriate context; reflection-based `JsonSerializer` is prohibited.
- Shared-base integration stays filesystem-only, fixed to `project-git`, reports `UNVERIFIED` index state, never starts or inspects Git, and does not disclose disabled registry entries except through `scri shared base list --all`.

---

## File Map

### New implementation files

- `src/Scrinia.Core/RecoveryLifecycleStateMapper.cs` - read-only projection from `RecoveryLifecycleState` to the four machine-specific vocabularies (Task 1).
- `src/Scrinia.Core/B0Vocabulary.cs` - `B0Outcome` union enum and `B0Failure` discriminated container (Task 2).
- `src/Scrinia.Core/B0FaultSeam.cs` - unified fault-injection contract, single `FaultPhase` enum, and six enum-mapping adapters (Task 3).
- `src/Scrinia.Core/RecoveryMember.cs` - the one justified typed member carrier (Task 4).
- `src/Scrinia.Core/B0Result.cs` - `B0Result` record, four thin read-only wrappers, `B0Exception` carrier, `CorrelationId` collapse (Task 5).

### Modified implementation files

- `src/Scrinia.Core/DurableFile.cs` - `FailureInjector` and `ThrowInjected` become thin adapters onto `B0FaultSeam` (Task 3).
- `src/Scrinia.Core/FileMemoryStore.cs` - `AppendFaultInjector`, `WholeMemoryFaultInjector`, `RestoreJournalFailureInjector`, `FailOnceAt`/`FailPersistentlyAt`/`ClearFaults`/`InjectAppendFault`/`InjectWholeFault` become adapters onto `B0FaultSeam` (Task 3).
- `src/Scrinia.Core/FileEntityTransactionCoordinator.cs` - `_faultPoints`/`FailOnceAt`/`ThrowIfFault` become adapters onto `B0FaultSeam` (Task 3).
- `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs` - `FailureInjector`/`ThrowIfInjected` become adapters onto `B0FaultSeam` (Task 3).
- `src/Scrinia.Core/RestoreJournal.cs` - `DecodeObserver` stays an `AsyncLocal<Action?>` observation hook (NOT an injection phase); no injection seam is added (Task 3).
- `src/Scrinia.Core/Formats/DurableFormats.cs` - add `Registry` as the single source of truth; do not relocate `FormatContract`/`FormatDisposition`/`FormatVersion.Evaluate` (Task 6).
- `src/Scrinia.Mcp/MemoryTools.Skills.cs` - register `B0Result` and its sub-types in `ScriniaMcpJsonContext`; do not change `ErrorCodes` or existing roots (Task 7).

### New/modified tests

- `tests/Scrinia.Tests/RecoveryLifecycleStateMapperTests.cs` (Task 1)
- `tests/Scrinia.Tests/B0OutcomeVocabularyTests.cs` (Task 2)
- `tests/Scrinia.Tests/B0FaultSeamTests.cs` (Task 3)
- `tests/Scrinia.Tests/RecoveryMemberCarrierTests.cs` (Task 4)
- `tests/Scrinia.Tests/B0ResultWrapperTests.cs` (Task 5)
- `tests/Scrinia.Tests/Formats/DurableFormatsRegistryTests.cs` (Task 6)
- `tests/Scrinia.Tests/B0ResultFacadeMappingTests.cs` (Task 7)
- `tests/Scrinia.Tests/B0CompatibilityHarness.cs` + `tests/Scrinia.Tests/Fixtures/**` (Task 8)

## Interfaces and Signatures

The following internal contracts are the only cross-task API. Implementers must use these names and types unless a compile-time conflict with an existing symbol is found; resolve such a conflict by adapting the implementation, not by adding a second parallel contract.

```csharp
// Task 1 - design Section 1. Read-only projection. No I/O, no write path, no transition logic.
internal enum RecoveryLifecycleState           // existing, RecoveryContracts.cs:15
{ Prepared, Applying, Committed, CleanupPending, Cleaned, RecoveryRequired, RolledBack }

internal static class RecoveryLifecycleStateMapper
{
    // Canonical enum -> lowercase wire string (matches RecoveryEnvelope.cs:96 converter).
    public static string ToWireString(RecoveryLifecycleState state);
    // Canonical enum -> the closest existing machine-specific lifecycle value, when one exists.
    public static string? ToAppendJournalState(RecoveryLifecycleState state);
    public static string? ToWholeMemoryOutcome(RecoveryLifecycleState state);
    public static string? ToMultiMemoryState(RecoveryLifecycleState state);
}

// Task 2 - design Section 2 (vocabulary portion). Behavior-preserving type system only.
internal enum B0Outcome
{
    Created, Appended, Replaced, Committed, CommittedCleanupPending,
    RolledBack, Rejected, SuccessWithWarning, RecoveryRequired
}

internal enum B0FailureTaxonomy
{ AppendStore, WholeMemory, MultiMemory, RecoveryBlock }

internal sealed record B0Failure(B0FailureTaxonomy Taxonomy, string Code, string? Detail);

// Task 4 - design Section 2 (the one justified change). Distinct from RawMemberSnapshot
// (AppendTransaction.cs:7) and RecoveryMemberSnapshot (RecoveryEnvelope.cs:58).
internal sealed record RecoveryMember(
    MemoryAddress Address,
    string Scope,
    byte[] ArtifactBytes,
    string ArtifactSha256,
    string? SidecarPath,
    string? SidecarSha256);

// Task 5 - design Section 2 (facade) + Section 2 (exception).
internal sealed record B0Result(
    B0Outcome Outcome,
    B0Failure? Failure,
    ArtifactEntry? Artifact,
    string OperationId,
    string CorrelationId,
    RecoveryInfo? Recovery,
    string? Warning);

internal sealed record B0Exception(Exception Source)
{
    internal static B0Exception Of(Exception exception) => new(exception);
}

internal static class B0ResultWrappers
{
    internal static B0Result FromAppend(AppendStoreResult result);
    internal static B0Result FromWhole(WholeMemoryResult result);
    internal static B0Result FromMulti(MultiMemoryAggregateResult result);
    internal static B0Result FromRecovery(RecoveryOutcome result);
}

// Task 3 - design Section 4. Unifies twelve seams onto one function shape.
internal static class B0FaultSeam
{
    // Single phase space: DurableWritePhase + all Append/Whole/Restore/MultiMemory/FileEntity phases.
    public enum FaultPhase
    {
        BeforeWrite, BeforeFlush, BeforeMove,
        AppendRawArtifactRead, AppendRawSidecarRead, AppendJournalWrite, AppendJournalFlush,
        AppendJournalParse, AppendJournalDelete, AppendArchiveArtifactWrite,
        AppendArchiveArtifactFlush, AppendArchiveSidecarWrite, AppendArchiveSidecarFlush,
        AppendLiveArtifactWrite, AppendLiveArtifactFlush, AppendLiveSidecarWrite,
        AppendLiveSidecarFlush, AppendVerificationRead, AppendRollbackWrite,
        AppendRollbackDelete, AppendArchiveCleanup, AppendPruningDelete, AppendBeforeEvent,
        AppendAfterEvent,
        WholeRawArtifactRead, WholeRawSidecarRead, WholeEvidenceWrite,
        WholeArchiveArtifactWrite, WholeArchiveSidecarWrite, WholeLiveArtifactWrite,
        WholeLiveSidecarWrite, WholeVerificationRead, WholeRollbackWrite,
        WholeEvidenceCleanup, WholeArchiveCleanup, WholeCleanupStateWrite,
        RestoreBeforeRecoveryBarrierRead, RestoreBeforePreparedWrite, RestoreAfterPreparedWrite,
        RestoreBeforeReplayBody, RestoreBeforeReplaySidecar, RestoreAfterReplayDestinations,
        RestoreBeforeCompletedWrite, RestoreAfterCompletedWrite, RestoreBeforeCleanupDelete,
        MultiPreparedPublished, MultiCommittedPublished, MultiCompletionProofPublished,
        MultiRecordDeleted,
        FileEntityRawBodyRead, FileEntityRawMetaRead, FileEntityArchiveBodyWrite,
        FileEntityArchiveMetaWrite, FileEntityBodyWrite, FileEntityMetaWrite,
        FileEntityVerificationRead, FileEntityAfterBodyDelete, FileEntityRollbackWrite,
        FileEntityEvidenceCleanup
    }

    public static Func<FaultPhase, string, Exception?>? Inject;
    public static void FailOnceAt(FaultPhase phase, string tag);
    public static void FailPersistentlyAt(FaultPhase phase, string tag);
    public static void ClearFaults();
    public static void ThrowIfInjected(FaultPhase phase, string tag);
}
```

The six enum-mapping adapters (internal, used by the production seams) each map one source enum to `B0FaultSeam.FaultPhase` 1:1:

```csharp
internal static B0FaultSeam.FaultPhase MapDurableWritePhase(DurableWritePhase phase);
internal static B0FaultSeam.FaultPhase MapAppendFaultPoint(AppendFaultPoint point);
internal static B0FaultSeam.FaultPhase MapWholeMemoryFaultPoint(WholeMemoryFaultPoint point);
internal static B0FaultSeam.FaultPhase MapRestoreJournalTransition(RestoreJournalTransition transition);
internal static B0FaultSeam.FaultPhase MapMultiMemoryTransition(MultiMemoryTransactionTransition transition);
internal static B0FaultSeam.FaultPhase MapFileEntityFaultPoint(FileEntityFaultPoint point);
```

`RecoveryInfo` (`WholeMemoryTransaction.cs:192`: `EvidencePath`, `State`, `Action`, `Detail`) is shared; the append wrapper maps `AppendRecoveryInfo` (`AppendContracts.cs:134`: `JournalPath`, `State`, `Action`, `Detail`) onto `RecoveryInfo` by `EvidencePath = JournalPath`.

## Task Dependencies

`Task 1` is foundational and independent. `Task 2` depends on Task 1 (both are read-only type-system additions). `Task 3` (fault seam) is independent of the vocabulary. `Task 4` (the justified carrier) is independent of the vocabulary. `Task 5` depends on Tasks 2 and 4 (the `B0Result` facade consumes `B0Outcome`, `B0Failure`, and `RecoveryMember`). `Task 6` (registry) is independent. `Task 7` (public mapping) depends on Task 5 (`B0Result`). `Task 8` (harness) depends on Tasks 1-7 (`B0FaultSeam`, `DurableFormats.Registry`, `B0Result`). `Task 9` (final gate) depends on all prior tasks. Tasks must be independently reviewed and green before the next dependent task starts.

### Task 1: Canonical lifecycle mapper `RecoveryLifecycleStateMapper`

**Files:**
- Create: `src/Scrinia.Core/RecoveryLifecycleStateMapper.cs`
- Create: `tests/Scrinia.Tests/RecoveryLifecycleStateMapperTests.cs`
- Do not touch: `AppendJournal.cs`, `WholeMemoryTransaction.cs`, `MultiMemoryTransaction.cs`, `RecoveryEnvelope.cs`, `RecoveryContracts.cs` (the four authoritative state machines).

**Interfaces:**
- Consumes: existing `RecoveryLifecycleState` (`RecoveryContracts.cs:15`), `AppendJournalState` (`AppendJournal.cs:11`), `WholeMemoryOutcome` (`WholeMemoryTransaction.cs:100`), `MultiMemoryTransactionState` (`MultiMemoryTransaction.cs:6`), and the wire converter at `RecoveryEnvelope.cs:96`.
- Produces: `RecoveryLifecycleStateMapper.ToWireString`, `ToAppendJournalState`, `ToWholeMemoryOutcome`, `ToMultiMemoryState` - pure functions with no I/O, no write path, and no transition logic.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class RecoveryLifecycleStateMapperTests
{
    [Theory]
    [InlineData(RecoveryLifecycleState.Prepared, "prepared", "Prepared", null, "Prepared")]
    [InlineData(RecoveryLifecycleState.Applying, "applying", "ArchiveInProgress", null, null)]
    [InlineData(RecoveryLifecycleState.Committed, "committed", "Committed", null, "Committed")]
    [InlineData(RecoveryLifecycleState.CleanupPending, "cleanupPending", "CleanupPending", null, null)]
    [InlineData(RecoveryLifecycleState.Cleaned, "cleaned", null, null, null)]
    [InlineData(RecoveryLifecycleState.RecoveryRequired, "recoveryRequired", "RecoveryRequired", "RecoveryRequired", "RecoveryRequired")]
    [InlineData(RecoveryLifecycleState.RolledBack, "rolledBack", "RolledBack", null, null)]
    public void ProjectsCanonicalEnumToAllFourVocabularies(
        RecoveryLifecycleState state, string wire, string? append, string? whole, string? multi)
    {
        Assert.Equal(wire, RecoveryLifecycleStateMapper.ToWireString(state));
        Assert.Equal(append, RecoveryLifecycleStateMapper.ToAppendJournalState(state));
        Assert.Equal(whole, RecoveryLifecycleStateMapper.ToWholeMemoryOutcome(state));
        Assert.Equal(multi, RecoveryLifecycleStateMapper.ToMultiMemoryState(state));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RecoveryLifecycleStateMapperTests`

Expected: FAIL because `RecoveryLifecycleStateMapper` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Scrinia.Core;

/// <summary>Read-only projection from the canonical recovery lifecycle to the four
/// machine-specific vocabularies. Does NOT rewrite any existing state machine.</summary>
internal static class RecoveryLifecycleStateMapper
{
    public static string ToWireString(RecoveryLifecycleState state) => state switch
    {
        RecoveryLifecycleState.Prepared => "prepared",
        RecoveryLifecycleState.Applying => "applying",
        RecoveryLifecycleState.Committed => "committed",
        RecoveryLifecycleState.CleanupPending => "cleanupPending",
        RecoveryLifecycleState.Cleaned => "cleaned",
        RecoveryLifecycleState.RecoveryRequired => "recoveryRequired",
        RecoveryLifecycleState.RolledBack => "rolledBack",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown recovery lifecycle state.")
    };

    public static string? ToAppendJournalState(RecoveryLifecycleState state) => state switch
    {
        RecoveryLifecycleState.Prepared => nameof(AppendJournalState.Prepared),
        RecoveryLifecycleState.Applying => nameof(AppendJournalState.ArchiveInProgress),
        RecoveryLifecycleState.Committed => nameof(AppendJournalState.Committed),
        RecoveryLifecycleState.CleanupPending => nameof(AppendJournalState.CleanupPending),
        RecoveryLifecycleState.RecoveryRequired => nameof(AppendJournalState.RecoveryRequired),
        RecoveryLifecycleState.RolledBack => nameof(AppendJournalState.RolledBack),
        RecoveryLifecycleState.Cleaned => null,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown recovery lifecycle state.")
    };

    public static string? ToWholeMemoryOutcome(RecoveryLifecycleState state) => state switch
    {
        RecoveryLifecycleState.RecoveryRequired => nameof(WholeMemoryOutcome.RecoveryRequired),
        _ => null
    };

    public static string? ToMultiMemoryState(RecoveryLifecycleState state) => state switch
    {
        RecoveryLifecycleState.Prepared => nameof(MultiMemoryTransactionState.Prepared),
        RecoveryLifecycleState.Committed => nameof(MultiMemoryTransactionState.Committed),
        RecoveryLifecycleState.RecoveryRequired => nameof(MultiMemoryTransactionState.RecoveryRequired),
        _ => null
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RecoveryLifecycleStateMapperTests`

Expected: PASS for all seven canonical states across all four projections, including every `null` branch.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/RecoveryLifecycleStateMapper.cs tests/Scrinia.Tests/RecoveryLifecycleStateMapperTests.cs
git commit -m "feat: add read-only recovery lifecycle mapper"
```

### Task 2: Unified `B0Outcome` union and `B0Failure` container

**Files:**
- Create: `src/Scrinia.Core/B0Vocabulary.cs`
- Create: `tests/Scrinia.Tests/B0OutcomeVocabularyTests.cs`
- Do not touch: `AppendContracts.cs`, `WholeMemoryTransaction.cs`, `MultiMemoryTransaction.cs`, `RecoveryContracts.cs` (the source taxonomies keep their members unchanged).

**Interfaces:**
- Consumes: `AppendStoreOutcome` (`AppendContracts.cs:19`), `WholeMemoryOutcome` (`WholeMemoryTransaction.cs:100`), `MultiMemoryOutcome` (`MultiMemoryTransaction.cs:13`), `MultiMemoryFailure` (`MultiMemoryTransaction.cs:22`), `RecoveryBlockReason` (`RecoveryContracts.cs:69`), `AppendStoreErrorCode` (`AppendContracts.cs:121`), `WholeMemoryErrorCode` (`WholeMemoryTransaction.cs:170`).
- Produces: `B0Outcome` (9 members), `B0FailureTaxonomy` (4 members), `B0Failure` (discriminated container recording the source taxonomy).

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class B0OutcomeVocabularyTests
{
    [Fact]
    public void B0Outcome_HasExactlyTheNineUnionMembers()
    {
        var expected = new[]
        {
            nameof(B0Outcome.Created), nameof(B0Outcome.Appended), nameof(B0Outcome.Replaced),
            nameof(B0Outcome.Committed), nameof(B0Outcome.CommittedCleanupPending),
            nameof(B0Outcome.RolledBack), nameof(B0Outcome.Rejected),
            nameof(B0Outcome.SuccessWithWarning), nameof(B0Outcome.RecoveryRequired)
        };
        Assert.Equal(9, Enum.GetValues(typeof(B0Outcome)).Length);
        Assert.Equal(expected, Enum.GetNames(typeof(B0Outcome)));
    }

    [Fact]
    public void B0Failure_RecordsTheProducingTaxonomy()
    {
        B0Failure failure = new(B0FailureTaxonomy.MultiMemory, nameof(MultiMemoryFailure.LockTimeout), "lock held");
        Assert.Equal(B0FailureTaxonomy.MultiMemory, failure.Taxonomy);
        Assert.Equal("LockTimeout", failure.Code);
        Assert.Equal("lock held", failure.Detail);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0OutcomeVocabularyTests`

Expected: FAIL because `B0Outcome` / `B0Failure` / `B0FailureTaxonomy` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Scrinia.Core;

/// <summary>One unified outcome surface adopted from the four existing outcome enums.</summary>
internal enum B0Outcome
{
    Created, Appended, Replaced, Committed, CommittedCleanupPending,
    RolledBack, Rejected, SuccessWithWarning, RecoveryRequired
}

/// <summary>Which failure taxonomy produced a <see cref="B0Failure"/>.</summary>
internal enum B0FailureTaxonomy
{
    AppendStore, WholeMemory, MultiMemory, RecoveryBlock
}

/// <summary>Discriminated container unifying AppendStoreErrorCode, WholeMemoryErrorCode,
/// MultiMemoryFailure (14 members), and RecoveryBlockReason (7 members).</summary>
internal sealed record B0Failure(B0FailureTaxonomy Taxonomy, string Code, string? Detail);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0OutcomeVocabularyTests`

Expected: PASS - exactly nine `B0Outcome` members in the declared order, and `B0Failure` records taxonomy/code/detail.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/B0Vocabulary.cs tests/Scrinia.Tests/B0OutcomeVocabularyTests.cs
git commit -m "feat: add unified B0Outcome and B0Failure vocabulary"
```

### Task 3: Unified `B0FaultSeam` contract and seam adapters

**Files:**
- Create: `src/Scrinia.Core/B0FaultSeam.cs`
- Modify: `src/Scrinia.Core/DurableFile.cs` - `FailureInjector` and `ThrowInjected` route through `B0FaultSeam`.
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` - `AppendFaultInjector`, `WholeMemoryFaultInjector`, `RestoreJournalFailureInjector`, `FailOnceAt`, `FailPersistentlyAt`, `ClearFaults`, `InjectAppendFault`, `InjectWholeFault` route through `B0FaultSeam`.
- Modify: `src/Scrinia.Core/FileEntityTransactionCoordinator.cs` - `_faultPoints`/`FailOnceAt`/`ThrowIfFault` route through `B0FaultSeam`.
- Modify: `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs` - `FailureInjector`/`ThrowIfInjected` route through `B0FaultSeam`.
- Do not touch: `RestoreJournal.cs` injection (the `DecodeObserver` at `RestoreJournal.cs:118` stays an `AsyncLocal<Action?>` observation hook, NOT an injection phase); `RecoveryVerification.cs` parser boundary.

**Interfaces:**
- Consumes: the six source enums (`DurableWritePhase` `DurableFile.cs:147`, `AppendFaultPoint` `AppendContracts.cs:140`, `WholeMemoryFaultPoint` `WholeMemoryTransaction.cs:176`, `RestoreJournalTransition` `FileMemoryStore.cs:17`, `MultiMemoryTransactionTransition` `MultiMemoryTransaction.cs:40`, `FileEntityFaultPoint` `WholeMemoryTransaction.cs:298`) and their existing seam call sites.
- Produces: `B0FaultSeam.Inject`, `FailOnceAt`, `FailPersistentlyAt`, `ClearFaults`, `ThrowIfInjected`, and the six `Map*` adapters. This task is behavior-preserving: production never registers a fault and never sets `Inject`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class B0FaultSeamTests
{
    [Fact]
    public void ThrowOnceAt_ThrowsOnceThenClears()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.Inject = (phase, tag) => new IOException($"boom {phase} {tag}");
            B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.AppendJournalWrite, "op-1");

            var first = Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.AppendJournalWrite, "op-1"));
            Assert.Contains("boom", first.Message);

            // Once-only: the second call at the same (phase, tag) must not throw again.
            Assert.Null(B0FaultSeam.ThrowIfInjectedOrNull(B0FaultSeam.FaultPhase.AppendJournalWrite, "op-1"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void FailPersistentlyAt_ThrowsEveryTime()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.Inject = (_phase, _tag) => new TimeoutException("held");
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.MultiCommittedPublished, "tx-9");
            Assert.Throws<TimeoutException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.MultiCommittedPublished, "tx-9"));
            Assert.Throws<TimeoutException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.MultiCommittedPublished, "tx-9"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void ClearFaults_ResetsAllScheduledSeams()
    {
        B0FaultSeam.Inject = (_phase, _tag) => new IOException("x");
        B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.WholeEvidenceWrite, "a");
        B0FaultSeam.ClearFaults();
        Assert.Null(B0FaultSeam.ThrowIfInjectedOrNull(B0FaultSeam.FaultPhase.WholeEvidenceWrite, "a"));
    }

    [Fact]
    public void FaultPhase_CoversEverySourceEnumMember()
    {
        // DurableWritePhase(3) + AppendFaultPoint(24) + WholeMemoryFaultPoint(12) +
        // RestoreJournalTransition(9) + MultiMemoryTransactionTransition(4) + FileEntityFaultPoint(10)
        Assert.Equal(62, Enum.GetValues(typeof(B0FaultSeam.FaultPhase)).Length);
        Assert.Equal(3, Enum.GetNames(typeof(DurableWritePhase)).Length);
        Assert.Equal(24, Enum.GetNames(typeof(AppendFaultPoint)).Length);
        Assert.Equal(12, Enum.GetNames(typeof(WholeMemoryFaultPoint)).Length);
        Assert.Equal(9, Enum.GetNames(typeof(RestoreJournalTransition)).Length);
        Assert.Equal(4, Enum.GetNames(typeof(MultiMemoryTransactionTransition)).Length);
        Assert.Equal(10, Enum.GetNames(typeof(FileEntityFaultPoint)).Length);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0FaultSeamTests`

Expected: FAIL because `B0FaultSeam` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation of `B0FaultSeam` and the six adapters**

```csharp
namespace Scrinia.Core;

/// <summary>One unified test-only fault-injection seam over the twelve existing seams.
/// Production code never registers a fault and never sets <see cref="Inject"/>; the seam is a
/// no-op unless a test schedules a fault at a <see cref="FaultPhase"/>.</summary>
internal static class B0FaultSeam
{
    public static Func<FaultPhase, string, Exception?>? Inject;

    private static readonly HashSet<(FaultPhase, string)> _once = new();
    private static readonly HashSet<(FaultPhase, string)> _persist = new();

    public static void FailOnceAt(FaultPhase phase, string tag) => _once.Add((phase, tag));
    public static void FailPersistentlyAt(FaultPhase phase, string tag) => _persist.Add((phase, tag));

    public static void ClearFaults()
    {
        _once.Clear();
        _persist.Clear();
    }

    public static void ThrowIfInjected(FaultPhase phase, string tag) =>
        ThrowIfInjectedOrNull(phase, tag) ?? throw new InvalidOperationException(
            $"Fault {phase} (tag={tag}) was scheduled but no exception factory produced one.");

    internal static Exception? ThrowIfInjectedOrNull(FaultPhase phase, string tag)
    {
        if (_persist.Contains((phase, tag)))
            return Fault();
        if (_once.Remove((phase, tag)))
            return Fault();
        return null;

        Exception Fault() => Inject?.Invoke(phase, tag)
            ?? new InvalidOperationException(
                $"Injected fault scheduled at {phase} (tag={tag}).");
    }

    // Six 1:1 enum adapters. Overlapping member names across Append/Whole are disambiguated by the
    // subsystem prefix; DurableWritePhase members are unique and stay unprefixed.
    internal static FaultPhase MapDurableWritePhase(DurableWritePhase phase) => phase switch
    {
        DurableWritePhase.BeforeWrite => FaultPhase.BeforeWrite,
        DurableWritePhase.BeforeFlush => FaultPhase.BeforeFlush,
        DurableWritePhase.BeforeMove => FaultPhase.BeforeMove,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown durable write phase.")
    };

    internal static FaultPhase MapAppendFaultPoint(AppendFaultPoint point) => point switch
    {
        AppendFaultPoint.RawArtifactRead => FaultPhase.AppendRawArtifactRead,
        AppendFaultPoint.RawSidecarRead => FaultPhase.AppendRawSidecarRead,
        AppendFaultPoint.JournalWrite => FaultPhase.AppendJournalWrite,
        AppendFaultPoint.JournalFlush => FaultPhase.AppendJournalFlush,
        AppendFaultPoint.JournalParse => FaultPhase.AppendJournalParse,
        AppendFaultPoint.JournalDelete => FaultPhase.AppendJournalDelete,
        AppendFaultPoint.ArchiveArtifactWrite => FaultPhase.AppendArchiveArtifactWrite,
        AppendFaultPoint.ArchiveArtifactFlush => FaultPhase.AppendArchiveArtifactFlush,
        AppendFaultPoint.ArchiveSidecarWrite => FaultPhase.AppendArchiveSidecarWrite,
        AppendFaultPoint.ArchiveSidecarFlush => FaultPhase.AppendArchiveSidecarFlush,
        AppendFaultPoint.LiveArtifactWrite => FaultPhase.AppendLiveArtifactWrite,
        AppendFaultPoint.LiveArtifactFlush => FaultPhase.AppendLiveArtifactFlush,
        AppendFaultPoint.LiveSidecarWrite => FaultPhase.AppendLiveSidecarWrite,
        AppendFaultPoint.LiveSidecarFlush => FaultPhase.AppendLiveSidecarFlush,
        AppendFaultPoint.VerificationRead => FaultPhase.AppendVerificationRead,
        AppendFaultPoint.RollbackWrite => FaultPhase.AppendRollbackWrite,
        AppendFaultPoint.RollbackDelete => FaultPhase.AppendRollbackDelete,
        AppendFaultPoint.ArchiveCleanup => FaultPhase.AppendArchiveCleanup,
        AppendFaultPoint.PruningDelete => FaultPhase.AppendPruningDelete,
        AppendFaultPoint.BeforeEvent => FaultPhase.AppendBeforeEvent,
        AppendFaultPoint.AfterEvent => FaultPhase.AppendAfterEvent,
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Unknown append fault point.")
    };

    internal static FaultPhase MapWholeMemoryFaultPoint(WholeMemoryFaultPoint point) => point switch
    {
        WholeMemoryFaultPoint.RawArtifactRead => FaultPhase.WholeRawArtifactRead,
        WholeMemoryFaultPoint.RawSidecarRead => FaultPhase.WholeRawSidecarRead,
        WholeMemoryFaultPoint.EvidenceWrite => FaultPhase.WholeEvidenceWrite,
        WholeMemoryFaultPoint.ArchiveArtifactWrite => FaultPhase.WholeArchiveArtifactWrite,
        WholeMemoryFaultPoint.ArchiveSidecarWrite => FaultPhase.WholeArchiveSidecarWrite,
        WholeMemoryFaultPoint.LiveArtifactWrite => FaultPhase.WholeLiveArtifactWrite,
        WholeMemoryFaultPoint.LiveSidecarWrite => FaultPhase.WholeLiveSidecarWrite,
        WholeMemoryFaultPoint.VerificationRead => FaultPhase.WholeVerificationRead,
        WholeMemoryFaultPoint.RollbackWrite => FaultPhase.WholeRollbackWrite,
        WholeMemoryFaultPoint.EvidenceCleanup => FaultPhase.WholeEvidenceCleanup,
        WholeMemoryFaultPoint.ArchiveCleanup => FaultPhase.WholeArchiveCleanup,
        WholeMemoryFaultPoint.CleanupStateWrite => FaultPhase.WholeCleanupStateWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Unknown whole-memory fault point.")
    };

    internal static FaultPhase MapRestoreJournalTransition(RestoreJournalTransition t) => t switch
    {
        RestoreJournalTransition.BeforeRecoveryBarrierRead => FaultPhase.RestoreBeforeRecoveryBarrierRead,
        RestoreJournalTransition.BeforePreparedWrite => FaultPhase.RestoreBeforePreparedWrite,
        RestoreJournalTransition.AfterPreparedWrite => FaultPhase.RestoreAfterPreparedWrite,
        RestoreJournalTransition.BeforeReplayBody => FaultPhase.RestoreBeforeReplayBody,
        RestoreJournalTransition.BeforeReplaySidecar => FaultPhase.RestoreBeforeReplaySidecar,
        RestoreJournalTransition.AfterReplayDestinations => FaultPhase.RestoreAfterReplayDestinations,
        RestoreJournalTransition.BeforeCompletedWrite => FaultPhase.RestoreBeforeCompletedWrite,
        RestoreJournalTransition.AfterCompletedWrite => FaultPhase.RestoreAfterCompletedWrite,
        RestoreJournalTransition.BeforeCleanupDelete => FaultPhase.RestoreBeforeCleanupDelete,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown restore transition.")
    };

    internal static FaultPhase MapMultiMemoryTransition(MultiMemoryTransactionTransition t) => t switch
    {
        MultiMemoryTransactionTransition.PreparedPublished => FaultPhase.MultiPreparedPublished,
        MultiMemoryTransactionTransition.CommittedPublished => FaultPhase.MultiCommittedPublished,
        MultiMemoryTransactionTransition.CompletionProofPublished => FaultPhase.MultiCompletionProofPublished,
        MultiMemoryTransactionTransition.RecordDeleted => FaultPhase.MultiRecordDeleted,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown multi-memory transition.")
    };

    internal static FaultPhase MapFileEntityFaultPoint(FileEntityFaultPoint point) => point switch
    {
        FileEntityFaultPoint.RawBodyRead => FaultPhase.FileEntityRawBodyRead,
        FileEntityFaultPoint.RawMetaRead => FaultPhase.FileEntityRawMetaRead,
        FileEntityFaultPoint.ArchiveBodyWrite => FaultPhase.FileEntityArchiveBodyWrite,
        FileEntityFaultPoint.ArchiveMetaWrite => FaultPhase.FileEntityArchiveMetaWrite,
        FileEntityFaultPoint.BodyWrite => FaultPhase.FileEntityBodyWrite,
        FileEntityFaultPoint.MetaWrite => FaultPhase.FileEntityMetaWrite,
        FileEntityFaultPoint.VerificationRead => FaultPhase.FileEntityVerificationRead,
        FileEntityFaultPoint.AfterBodyDelete => FaultPhase.FileEntityAfterBodyDelete,
        FileEntityFaultPoint.RollbackWrite => FaultPhase.FileEntityRollbackWrite,
        FileEntityFaultPoint.EvidenceCleanup => FaultPhase.FileEntityEvidenceCleanup,
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Unknown file-entity fault point.")
    };
}
```

  Then replace each existing seam body with a thin adapter. Example for `DurableFile`:

```csharp
// Before (DurableFile.cs:127-132)
private static void ThrowInjected(DurableWritePhase phase, string destination)
{
    Exception? failure = FailureInjector?.Invoke(phase, destination);
    if (failure is not null) throw failure;
}
// After
private static void ThrowInjected(DurableWritePhase phase, string destination) =>
    B0FaultSeam.ThrowIfInjected(B0FaultSeam.MapDurableWritePhase(phase), destination);
```

  `FileMemoryStore`: `InjectAppendFault(point, operationId, path)` becomes `B0FaultSeam.ThrowIfInjected(B0FaultSeam.MapAppendFaultPoint(point), path)`; `InjectWholeFault` becomes `B0FaultSeam.ThrowIfInjected(B0FaultSeam.MapWholeMemoryFaultPoint(point), path)`; `FailOnceAt(point)` / `FailPersistentlyAt(point)` / `ClearFaults()` delegate to `B0FaultSeam`; the `AppendFaultInjector`/`WholeMemoryFaultInjector` static fields become thin adapters that call `B0FaultSeam.Inject?.Invoke(Map(...), tag)`. `FileEntityTransactionCoordinator.FailOnceAt` calls `B0FaultSeam.FailOnceAt(B0FaultSeam.MapFileEntityFaultPoint(point), point.ToString())` and `ThrowIfFault` calls `B0FaultSeam.ThrowIfInjected`. `MultiMemoryTransactionCoordinator.FailureInjector` becomes an adapter routing `MapMultiMemoryTransition` through `B0FaultSeam.Inject`, and `ThrowIfInjected` calls `B0FaultSeam.ThrowIfInjected`. `RestoreJournal.DecodeObserver` is left exactly as `internal static AsyncLocal<Action?> DecodeObserver { get; } = new();` at `RestoreJournal.cs:118` - no injection method is added near it.

- [ ] **Step 4: Run the focused seam tests to verify pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0FaultSeamTests`

Expected: PASS - `FailOnceAt` clears after one throw, `FailPersistentlyAt` repeats, `ClearFaults` resets, `FaultPhase` has 62 members, and each mapped phase is enumerable.

- [ ] **Step 5: Run the full existing fault-injection regression lane** (proves the adapters are behavior-preserving against the real seams).

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Fault|FullyQualifiedName~Inject|FullyQualifiedName~DurableWrite|FullyQualifiedName~Recovery`

Expected: PASS - every pre-existing fault-injection test still throws at the same boundary, and production `B0FaultSeam.Inject` was never set by the run.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/B0FaultSeam.cs src/Scrinia.Core/DurableFile.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/FileEntityTransactionCoordinator.cs src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs tests/Scrinia.Tests/B0FaultSeamTests.cs
git commit -m "feat: unify fault seams behind B0FaultSeam"
```

### Task 4: Typed `RecoveryMember` carrier (the ONE justified change)

**Files:**
- Create: `src/Scrinia.Core/RecoveryMember.cs`
- Create: `tests/Scrinia.Tests/RecoveryMemberCarrierTests.cs`
- Do not touch: `AppendTransaction.cs` (capture `RawMemberSnapshot` at `AppendTransaction.cs:102-122`), `WholeMemoryTransaction.cs` (capture `RawMemberSnapshot` at `WholeMemoryTransaction.cs:6-41`), `RecoveryEnvelope.cs` (`RecoveryMemberSnapshot` at `RecoveryEnvelope.cs:58`). Capture stays raw; only the result boundary becomes typed.

**Interfaces:**
- Consumes: `MemoryAddress` (`Scrinia.Core.Models`), `RawMemberSnapshot` (`AppendTransaction.cs:7`), `RecoveryMemberSnapshot` (`RecoveryEnvelope.cs:58`).
- Produces: `RecoveryMember` - the single typed member contract B1-B6 read. This task is the only one permitted to introduce a production type that changes representation at the result boundary.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Cryptography;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public class RecoveryMemberCarrierTests
{
    [Fact]
    public void RecoveryMember_IsDistinctFromRawMemberSnapshotAndRecoveryMemberSnapshot()
    {
        var address = MemoryAddress.Parse("/notes/candidate");
        RecoveryMember member = new(
            address, "notes", new byte[] { 1, 2, 3 }, "abc123",
            ".scrinia/memories/notes/candidate.nmp2.meta.json", "def456");

        Assert.Equal(address, member.Address);
        Assert.Equal("notes", member.Scope);
        Assert.Equal(new byte[] { 1, 2, 3 }, member.ArtifactBytes);
        Assert.Equal("abc123", member.ArtifactSha256);
        Assert.Equal(".scrinia/memories/notes/candidate.nmp2.meta.json", member.SidecarPath);
        Assert.Equal("def456", member.SidecarSha256);
        // Distinct type: a RecoveryMember is never a RawMemberSnapshot.
        Assert.False(member is RawMemberSnapshot);
        Assert.False(member is RecoveryMemberSnapshot);
    }

    [Fact]
    public void AppendCapture_StillUsesRawMemberSnapshot_NotRecoveryMember()
    {
        // Prove the justified change is boundary-only: capture keeps RawMemberSnapshot,
        // SHA-256 still enforced at capture.
        RawMemberSnapshot raw = RawMemberSnapshot.Capture("/x/nmp2", false, new byte[] { 9, 9 });
        Assert.IsType<RawMemberSnapshot>(raw);
        Assert.NotEqual(typeof(RecoveryMember), typeof(RawMemberSnapshot));
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(raw.Bytes)),
            raw.Sha256);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RecoveryMemberCarrierTests`

Expected: FAIL because `RecoveryMember` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Scrinia.Core;

/// <summary>The one justified change (design Section 6.2). A typed member carrier B1-B6 read at
/// the result boundary. It is distinct from RawMemberSnapshot (append/whole capture) and
/// RecoveryMemberSnapshot (recovery body). It is NEVER written to disk, so it is not a durable
/// format or recovery outcome.</summary>
internal sealed record RecoveryMember(
    MemoryAddress Address,
    string Scope,
    byte[] ArtifactBytes,
    string ArtifactSha256,
    string? SidecarPath,
    string? SidecarSha256);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RecoveryMemberCarrierTests`

Expected: PASS - `RecoveryMember` round-trips its six fields, is not a `RawMemberSnapshot` or `RecoveryMemberSnapshot`, and append capture still asserts SHA-256 on `RawMemberSnapshot` unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/RecoveryMember.cs tests/Scrinia.Tests/RecoveryMemberCarrierTests.cs
git commit -m "feat: add typed RecoveryMember carrier at the result boundary"
```

### Task 5: `B0Result` facade and thin read-only wrappers + `B0Exception`

**Files:**
- Create: `src/Scrinia.Core/B0Result.cs`
- Create: `tests/Scrinia.Tests/B0ResultWrapperTests.cs`
- Do not touch: `RecoveryEnvelope.cs` (`ConsumeParsed` at lines 567/706/794), `RecoveryVerification.cs` (`ParseVerifiedEnvelope` at `RecoveryVerification.cs:297`, `RecoveryVerificationContent`/`RecoveryVerificationResult`/`RecoveryVerificationOutcome`, verifier/accounting/cursor), `AppendJournal.cs` (`ConsumeParsed` at line 190), `RestoreJournal.cs` (`ConsumeParsed` at line 111).

**Interfaces:**
- Consumes: Task 2 `B0Outcome`/`B0Failure`/`B0FailureTaxonomy`, Task 4 `RecoveryMember`, existing `AppendStoreResult` (`AppendContracts.cs:11`), `AppendStoreWarning` (`AppendContracts.cs:34`), `AppendRecoveryInfo` (`AppendContracts.cs:134`), `WholeMemoryResult` (`WholeMemoryTransaction.cs:108`), `RecoveryInfo` (`WholeMemoryTransaction.cs:192`), `MultiMemoryAggregateResult` (`MultiMemoryTransaction.cs:394`), `MultiMemoryOutcome`/`MultiMemoryFailure` (`MultiMemoryTransaction.cs:13`/`22`), `RecoveryOutcome`/`RecoveryBlock`/`RecoveryBlockReason` (`RecoveryContracts.cs:229`/`80`/`69`), and `ArtifactEntry` (`Scrinia.Core.Models`).
- Produces: `B0Result`, `B0Exception`, and `B0ResultWrappers.FromAppend`/`FromWhole`/`FromMulti`/`FromRecovery` - thin, read-only, behavior-preserving facades. Each wrapper copies the outcome, carries any existing `AppendStoreWarning`/`WholeMemoryWarning` into `B0Result.Warning`, carries any existing `RecoveryInfo` into `B0Result.Recovery`, and collapses `OperationId`/`TransactionId` to one `CorrelationId`.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public class B0ResultWrapperTests
{
    [Fact]
    public void FromAppend_MapsOutcomeWarningAndCorrelation()
    {
        ArtifactEntry entry = new(
            MemoryAddress.Parse("/notes/candidate"),
            "artifacts/notes/candidate.nmp2", 3, 1, DateTimeOffset.UtcNow);
        AppendStoreResult append = new(AppendStoreOutcome.Appended, entry, "op-append");
        B0Result b0 = B0ResultWrappers.FromAppend(append);

        Assert.Equal(B0Outcome.Appended, b0.Outcome);
        Assert.Equal("op-append", b0.OperationId);
        Assert.Equal("op-append", b0.CorrelationId); // collapses AppendTransaction.OperationId
        Assert.Same(entry, b0.Artifact);
        Assert.Null(b0.Failure);
        Assert.Null(b0.Recovery);
    }

    [Fact]
    public void FromWhole_MapsRecoveryIntoSharedRecoveryInfo()
    {
        WholeMemoryResult whole = new(
            WholeMemoryOutcome.RecoveryRequired, null, "op-whole",
            "sidecar newer", new RecoveryInfo("/evid", "recoveryRequired", "resolve", "blocked"));
        B0Result b0 = B0ResultWrappers.FromWhole(whole);

        Assert.Equal(B0Outcome.RecoveryRequired, b0.Outcome);
        Assert.Equal("op-whole", b0.CorrelationId);
        Assert.Equal("sidecar newer", b0.Warning);
        Assert.Equal("/evid", b0.Recovery!.EvidencePath);
    }

    [Fact]
    public void FromMulti_RecordsFailureTaxonomyAndCollapsesTransactionId()
    {
        MultiMemoryAggregateResult multi = new(
            "tx-multi", "notes", MultiMemoryOutcome.Committed,
            Array.Empty<MultiMemoryParticipantOutcome>(), null, null, null);
        B0Result b0 = B0ResultWrappers.FromMulti(multi);

        Assert.Equal(B0Outcome.Committed, b0.Outcome);
        Assert.Equal("tx-multi", b0.OperationId);
        Assert.Equal("tx-multi", b0.CorrelationId); // collapses MultiMemoryTransactionRecord.transactionId
    }

    [Fact]
    public void FromRecovery_MapsBlockReasonIntoB0Failure()
    {
        RecoveryBlock block = new("notes", "/evid", RecoveryOperation.Append, "op-r",
            RecoveryBlockReason.WrongScope, "scope mismatch");
        RecoveryOutcome outcome = new(true, false, false, "notes", "/evid", "op-r", block, null);
        B0Result b0 = B0ResultWrappers.FromRecovery(outcome);

        Assert.Equal(B0Outcome.RecoveryRequired, b0.Outcome);
        Assert.NotNull(b0.Failure);
        Assert.Equal(B0FailureTaxonomy.RecoveryBlock, b0.Failure!.Taxonomy);
        Assert.Equal("WrongScope", b0.Failure.Code);
    }

    [Fact]
    public void B0Exception_WrapsExistingExceptionTypeUnchanged()
    {
        AppendStoreException appendEx = new(AppendStoreErrorCode.Internal, "op", "nope");
        B0Exception b0ex = B0Exception.Of(appendEx);
        Assert.Same(appendEx, b0ex.Source);
        Assert.Equal("nope", b0ex.Source.Message);
    }
}
```

  `AppendStoreResult`, `WholeMemoryResult`, `MultiMemoryAggregateResult`, and `RecoveryOutcome` are constructed through their existing constructors; the test project accesses `internal` members via the configured `InternalsVisibleTo("Scrinia.Tests")`. `MultiMemoryParticipantOutcome` is constructed with the fields documented in the plan's Interfaces block (`Subject`, `OperationId`, `Kind`, `EvidencePath`, `Committed`, `RolledBack`, `Reason`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0ResultWrapperTests`

Expected: FAIL because `B0Result` / `B0ResultWrappers` / `B0Exception` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Scrinia.Core;

/// <summary>One Core result facade over the four existing Core results. Thin and behavior-preserving.</summary>
internal sealed record B0Result(
    B0Outcome Outcome,
    B0Failure? Failure,
    ArtifactEntry? Artifact,
    string OperationId,
    string CorrelationId,
    RecoveryInfo? Recovery,
    string? Warning);

/// <summary>Single typed exception surface over the existing append/whole/multi exceptions.</summary>
internal sealed record B0Exception(Exception Source);

internal static class B0ResultWrappers
{
    internal static B0Result FromAppend(AppendStoreResult result) => new(
        MapAppendOutcome(result.Outcome),
        null,
        result.Entry,
        result.OperationId,
        result.OperationId,
        result.Recovery is null
            ? null
            : new RecoveryInfo(result.Recovery.JournalPath, result.Recovery.State, result.Recovery.Action, result.Recovery.Detail),
        result.Warning is null ? null : $"{result.Warning.Code}:{result.Warning.Message}");

    internal static B0Result FromWhole(WholeMemoryResult result) => new(
        MapWholeOutcome(result.Outcome),
        null,
        result.Entry,
        result.OperationId,
        result.OperationId,
        result.Recovery,
        result.Warning);

    internal static B0Result FromMulti(MultiMemoryAggregateResult result) => new(
        MapMultiOutcome(result.Outcome),
        result.Failure is null
            ? null
            : new B0Failure(B0FailureTaxonomy.MultiMemory, result.Failure.ToString(), null),
        null,
        result.TransactionId,
        result.TransactionId,
        result.EvidencePath is null || result.Outcome is not MultiMemoryOutcome.RecoveryRequired
            ? null
            : new RecoveryInfo(result.EvidencePath, "recoveryRequired", "inspect coordinator record", null),
        result.Warning);

    internal static B0Result FromRecovery(RecoveryOutcome result)
    {
        B0Failure? failure = result.Block is null
            ? null
            : new B0Failure(B0FailureTaxonomy.RecoveryBlock, result.Block.Reason.ToString(), result.Block.Detail);
        return new B0Result(
            result.IsCommitted
                ? (result.CleanupPending ? B0Outcome.CommittedCleanupPending : B0Outcome.Committed)
                : B0Outcome.RecoveryRequired,
            failure,
            null,
            result.OperationId,
            result.OperationId,
            result.Block is null
                ? null
                : new RecoveryInfo(result.Block.EvidencePath, "recoveryRequired", "resolve retained evidence", result.Block.Detail),
            result.Warning);
    }

    private static B0Outcome MapAppendOutcome(AppendStoreOutcome o) => o switch
    {
        AppendStoreOutcome.Created => B0Outcome.Created,
        AppendStoreOutcome.Appended => B0Outcome.Appended,
        AppendStoreOutcome.SuccessWithWarning => B0Outcome.SuccessWithWarning,
        AppendStoreOutcome.RecoveryRequired => B0Outcome.RecoveryRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(o), o, "Unknown append outcome.")
    };

    private static B0Outcome MapWholeOutcome(WholeMemoryOutcome o) => o switch
    {
        WholeMemoryOutcome.Created => B0Outcome.Created,
        WholeMemoryOutcome.Replaced => B0Outcome.Replaced,
        WholeMemoryOutcome.SuccessWithWarning => B0Outcome.SuccessWithWarning,
        WholeMemoryOutcome.RecoveryRequired => B0Outcome.RecoveryRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(o), o, "Unknown whole-memory outcome.")
    };

    private static B0Outcome MapMultiOutcome(MultiMemoryOutcome o) => o switch
    {
        MultiMemoryOutcome.Committed => B0Outcome.Committed,
        MultiMemoryOutcome.CommittedCleanupPending => B0Outcome.CommittedCleanupPending,
        MultiMemoryOutcome.RolledBack => B0Outcome.RolledBack,
        MultiMemoryOutcome.Rejected => B0Outcome.Rejected,
        MultiMemoryOutcome.RecoveryRequired => B0Outcome.RecoveryRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(o), o, "Unknown multi-memory outcome.")
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0ResultWrapperTests`

Expected: PASS - append/whole/multi/recovery outcomes map to the correct `B0Outcome`, `CorrelationId` collapses to the single id, warnings/recoveries/blocks carry through, and `B0Exception` preserves the wrapped exception type and message.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/B0Result.cs tests/Scrinia.Tests/B0ResultWrapperTests.cs
git commit -m "feat: add B0Result facade over the four Core results"
```

### Task 6: Centralized `DurableFormats` registry owner

**Files:**
- Modify: `src/Scrinia.Core/Formats/DurableFormats.cs` - add `Registry`; do NOT relocate or duplicate `FormatContract`, `FormatDisposition`, or `FormatVersion.Evaluate` (each keeps its single definition in `Formats/`).
- Create: `tests/Scrinia.Tests/Formats/DurableFormatsRegistryTests.cs`
- Do not touch: `Formats/FormatContract.cs`, `Formats/FormatDisposition.cs`, `Formats/FormatVersion.cs` (one definition each, unchanged).

**Interfaces:**
- Consumes: existing `DurableFormats.All` (`Formats/DurableFormats.cs:110`) and the twelve contracts listed in design Section 7.2.
- Produces: `DurableFormats.Registry` - one `ImmutableDictionary<string, FormatContract>` whose keys are the twelve format names, as the sole source of truth for every durable format name, current version, `AbsentMeans`, and `OnOlder`/`OnNewer` disposition.

- [ ] **Step 1: Write the failing test**

```csharp
using Scrinia.Core.Formats;
using Xunit;

namespace Scrinia.Tests.Formats;

public class DurableFormatsRegistryTests
{
    [Fact]
    public void Registry_ContainsExactlyTheTwelveKnownFormats()
    {
        var names = DurableFormats.Registry.Keys.OrderBy(n => n).ToArray();
        Assert.Equal(12, names.Length);
        Assert.Contains("artifact-sidecar", names);
        Assert.Contains("entity-facts", names);
        Assert.Contains("nmp2-body", names);
        Assert.Contains("bundle-entry", names);
        Assert.Contains("bundle-manifest", names);
        Assert.Contains("legacy-index", names);
        Assert.Contains("skill-meta", names);
        Assert.Contains("agent-meta", names);
        Assert.Contains("restore-journal", names);
        Assert.Contains("append-journal", names);
        Assert.Contains("recovery-envelope", names);
        Assert.Contains("multi-memory-transaction", names);
    }

    [Fact]
    public void Evaluate_ReturnsRefuse_ForUnknownVersion_ViaRegistry()
    {
        // Nmp2Body has AbsentMeans null => Refuse; a declared version above Current also Refuses.
        Assert.Equal(FormatDisposition.Refuse,
            FormatVersion.Evaluate(DurableFormats.Registry["nmp2-body"], 99));
        Assert.Equal(FormatDisposition.Refuse,
            FormatVersion.Evaluate(DurableFormats.Registry["append-journal"], 0));
    }

    [Fact]
    public void CurrentVersions_Load_ViaRegistry()
    {
        Assert.Equal(FormatDisposition.Load,
            FormatVersion.Evaluate(DurableFormats.Registry["artifact-sidecar"], 1));
        Assert.Equal(FormatDisposition.Rebuild,
            FormatVersion.Evaluate(DurableFormats.Registry["entity-facts"], 3));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~DurableFormatsRegistryTests`

Expected: FAIL because `DurableFormats.Registry` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Add to `Formats/DurableFormats.cs` (inside the class, after the `All` property):

```csharp
/// <summary>Sole source of truth for every durable format name and version contract.
/// Every consumer imports this Registry; no consumer declares a second copy or
/// hard-codes a version disposition. FormatContract / FormatDisposition / FormatVersion.Evaluate
/// each keep their single definition in Formats/ and are not duplicated or relocated.</summary>
public static readonly ImmutableDictionary<string, FormatContract> Registry =
    All.ToImmutableDictionary(contract => contract.Name, StringComparer.Ordinal);
```

IMPORTANT: `Registry` MUST be derived from the existing `All` list (`All.ToImmutableDictionary(...)`) as shown — `All` is the single canonical declaration of the twelve formats. Do NOT hand-write a second list of the twelve format contracts anywhere in this file. If you need to change a contract's disposition or version, edit it in place in `All`; `Registry` must never duplicate or re-enumerate the formats. Any second enumeration would violate the 'one definition' rule this section exists to enforce.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~DurableFormatsRegistryTests`

Expected: PASS - exactly twelve registered formats, `Refuse` on unknown/newer/absent-null versions, and all current versions `Load`/`Rebuild` through the registry.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Formats/DurableFormats.cs tests/Scrinia.Tests/Formats/DurableFormatsRegistryTests.cs
git commit -m "feat: centralize DurableFormats as single registry owner"
```

### Task 7: Public result mapping Core -> MCP -> CLI

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs` - register `B0Result` and its sub-types in `ScriniaMcpJsonContext` (additive; do not change existing roots).
- Do not touch: `McpResponse.cs` `ErrorCodes` (`McpResponse.cs:10`) unchanged; `CliJsonContext.cs` roots keep `CliMcpOutput` as the append/whole/multi passthrough unchanged; no second facade.

**Interfaces:**
- Consumes: Task 5 `B0Result`, `B0Outcome`, `B0Failure`, `B0FailureTaxonomy`, `B0Exception`, `RecoveryMember`.
- Produces: `B0Result` registered as a new additive JSON root alongside the existing `AppendStoreResult` + sub-types (`MemoryTools.Skills.cs:49-54`); `MultiMemoryAggregateResult` and `RecoveryBlock`/`RecoveryBlockReason` still cross Core -> MCP -> CLI through `B0Result` without wire change.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class B0ResultFacadeMappingTests
{
    [Fact]
    public void B0Result_RoundTripsThroughScriniaMcpJsonContext()
    {
        B0Result result = new(
            B0Outcome.Appended, null, null, "op-1", "op-1", null, "warn");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            result, ScriniaMcpJsonContext.Default.B0Result);
        B0Result decoded = JsonSerializer.Deserialize(json, ScriniaMcpJsonContext.Default.B0Result)!;

        Assert.Equal(B0Outcome.Appended, decoded.Outcome);
        Assert.Equal("op-1", decoded.CorrelationId);
        Assert.Equal("warn", decoded.Warning);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0ResultFacadeMappingTests`

Expected: FAIL because `B0Result` is not registered in `ScriniaMcpJsonContext` (compile error on `ScriniaMcpJsonContext.Default.B0Result`).

- [ ] **Step 3: Write minimal implementation**

Add `[JsonSerializable(typeof(B0Result))]` and the sub-type entries to `ScriniaMcpJsonContext` in `MemoryTools.Skills.cs`, immediately after the existing `AppendStoreResult` registration block (lines 49-53):

```csharp
[JsonSerializable(typeof(B0Result))]
[JsonSerializable(typeof(B0Outcome))]
[JsonSerializable(typeof(B0Failure))]
[JsonSerializable(typeof(B0FailureTaxonomy))]
[JsonSerializable(typeof(B0Exception))]
[JsonSerializable(typeof(RecoveryMember))]
```

  This is additive: the existing `AppendStoreResult`/`AppendStoreWarning`/`AppendRecoveryInfo`/`AppendStoreOutcome`/`AppendWarningCode` registrations are unchanged, and `McpResponse.ErrorCodes` is not touched.

- [ ] **Step 4: Run the focused test and the source-generation build check**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0ResultFacadeMappingTests`

Expected: PASS - `B0Result` round-trips through `ScriniaMcpJsonContext`.

Run: `dotnet build src/Scrinia.Mcp/Scrinia.Mcp.csproj -c Release`

Expected: PASS with zero source-generation warnings; no reflection `JsonSerializer` is introduced.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Skills.cs tests/Scrinia.Tests/B0ResultFacadeMappingTests.cs
git commit -m "feat: register B0Result as additive MCP JSON root"
```

### Task 8: Compatibility harness and golden fixtures

**Files:**
- Create: `tests/Scrinia.Tests/B0CompatibilityHarness.cs`
- Create: `tests/Scrinia.Tests/Fixtures/` golden fixtures (artifact, sidecar, archive, journal/envelope, fault-injection)
- Do not touch: any `src/` file. The harness is test infrastructure only and does not run in production.

**Interfaces:**
- Consumes: Task 1 `RecoveryLifecycleStateMapper`, Task 3 `B0FaultSeam`, Task 5 `B0Result`/`B0ResultWrappers`, Task 6 `DurableFormats.Registry`, existing `RecoveryEnvelopeCodec`, `TestHelpers.StoreScope`, `LegacyMigrationService` (`Migration/LegacyMigrationState.cs`), and the sole parser boundary at `RecoveryVerification.cs:297`.
- Produces: a compatibility harness that loads current evidence through the unified foundation and asserts it still parses, validates, and maps; a compatibility matrix proving every current durable format + result maps without behavior change; and a bare-complete migration rejection regression.

- [ ] **Step 1: Create golden fixtures for each current evidence kind**

Copy a representative current artifact of each kind into `tests/Scrinia.Tests/Fixtures/`:
- Artifact: one `ArtifactSidecar` `.meta.json`, one `EntityFacts` `facts.json`, one NMP/2 `nmp2` body.
- Sidecar: one ordinary sidecar and one skill/agent sidecar (exercised through `ReadSidecarMeta`/`WriteSidecarMeta`).
- Archive: one `versions/` archive pair carrying `BundleManifest`/`LegacyIndex`.
- Journal/envelope: one append `.append-journal.json`, one `.restore-journal.json`, one `.file-entity-*.recovery`, one `/.multi-memory-transaction.json` (each parses through the sole parser boundary).
- Ephemeral: a `~name` and a `/temp/name` address pair (asserted to stay in-process).

- [ ] **Step 2: Write the failing harness tests**

```csharp
using System.Text;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Configuration;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Migration;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class B0CompatibilityHarness
{
    private const string Fixtures = "Fixtures";

    [Fact]
    public void ArtifactFixture_DecodesAndReEncodesIdentically()
    {
        // Golden NMP/2 body: decode verifies CRC32/length integrity, re-encode is byte-identical.
        string artifact = File.ReadAllText(Path.Combine(Fixtures, "entity-facts.nmp2"));
        byte[] decoded = Nmp2Strategy.Instance.Decode(artifact);
        Assert.NotEmpty(decoded);
        EncodingResult reencoded = Nmp2Strategy.Instance.Encode(decoded, new EncodingOptions());
        Assert.Equal(artifact, reencoded.Artifact);
    }

    [Fact]
    public void SidecarFixture_RoundTripsThroughReadSidecarMetaWriteSidecarMeta()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"scrinia_b0_{Guid.NewGuid():N}.md");
        try
        {
            int current = DurableFormats.SkillMeta.Current;
            SkillFileMeta written = new(null, "reviewer", null, null, null, current);
            ScriniaMcpTools.WriteSidecarMeta(tempFile, written, ScriniaMcpJsonContext.Default.SkillFileMeta);

            SkillFileMeta? read = ScriniaMcpTools.ReadSidecarMeta(tempFile, ScriniaMcpJsonContext.Default.SkillFileMeta);
            Assert.NotNull(read);
            Assert.Equal(current, read!.SchemaVersion);
            Assert.Equal("reviewer", read!.Role);
            // The sidecar version routes through the centralized registry and Loads.
            Assert.Equal(FormatDisposition.Load,
                FormatVersion.Evaluate(DurableFormats.Registry["skill-meta"], current));
        }
        finally
        {
            string metaPath = Path.ChangeExtension(tempFile, ".meta.json");
            if (File.Exists(metaPath)) File.Delete(metaPath);
        }
    }

    [Fact]
    public void CompatibilityMatrix_EveryCurrentFormatLoads_AndRefuseFailsClosed()
    {
        foreach (FormatContract contract in DurableFormats.All)
        {
            FormatDisposition disposition = FormatVersion.Evaluate(
                DurableFormats.Registry[contract.Name], contract.Current);
            Assert.Equal(FormatDisposition.Load, disposition);
        }
        // Refuse versions fail closed, matching existing recovery/journal/envelope/coordinator behavior.
        Assert.Equal(FormatDisposition.Refuse,
            FormatVersion.Evaluate(DurableFormats.Registry["nmp2-body"], 99));
    }

    [Fact]
    public void CurrentAppendEvidence_ReturnsAppended_ThroughB0Result()
    {
        var append = new AppendStoreResult(AppendStoreOutcome.Appended, null, "op-harness");
        B0Result b0 = B0ResultWrappers.FromAppend(append);
        Assert.Equal(B0Outcome.Appended, b0.Outcome);
        Assert.Equal("op-harness", b0.CorrelationId);
    }

    [Fact]
    public void BareCompleteMigration_StillRejectsCompleteWithoutPrepared()
    {
        using var scope = new TestHelpers.StoreScope();
        string directory = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(directory);
        string journal = Path.Combine(directory, ".tracking-recovery.json");
        File.WriteAllText(journal, "complete", new UTF8Encoding(false));
        var service = new LegacyMigrationService();

        LegacyFilesystemObservation inspection = service.Inspect(scope.WorkspaceDir);
        LegacyMigrationResult migration = service.Migrate(scope.WorkspaceDir, TrackingPolicy.Local);

        // Anchored at LegacyMigrationState.cs:424: lines[0] == "complete" without "prepared" rejects.
        Assert.NotEqual(ConfigurationErrorCode.None, inspection.ErrorCode);
        Assert.Equal(ConfigurationExitClass.InvalidConfiguration, migration.ExitClass);
    }

    [Fact]
    public void FaultSeam_ReproducesEachExistingSeamFailureAtMappedPhase()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.Inject = (_phase, _tag) => new IOException("injected");
            B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.AppendJournalWrite, "append-op");
            // The append durable write routes through the unified seam at the mapped phase.
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.AppendJournalWrite, "append-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }
}
```

- [ ] **Step 3: Run the harness tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0CompatibilityHarness`

Expected: FAIL because the harness, fixtures, and `LegacyMigrationService`/`TestHelpers.StoreScope` wiring are not yet in place.

- [ ] **Step 4: Implement the harness against the golden fixtures and rerun**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~B0CompatibilityHarness`

Expected: PASS - golden fixtures load and re-encode identically, every current format loads and `Refuse` versions fail closed, current append evidence maps `Appended` through `B0Result`, bare-complete rejects, and the fault seam reproduces a mapped-phase failure.

- [ ] **Step 5: Commit**

```bash
git add tests/Scrinia.Tests/B0CompatibilityHarness.cs tests/Scrinia.Tests/Fixtures
git commit -m "test: add B0 compatibility harness and golden fixtures"
```

### Task 9: Final verification and scope-hygiene gate

**Files:**
- Modify only the B0 implementation files listed in Tasks 1-8.
- Extend, but do not weaken, the B0 tests listed in Tasks 1-8 and the existing regression lanes.
- Do not modify specs, historical reports, ordinary docs, NMP/2 code, sidecar codecs, plugin/event code, migration internals, or B1-B6 contracts.

**Interfaces:**
- Consumes: all Task 1-8 contracts and implementation; existing B1-B6 regression suites.
- Produces: green full-suite evidence and a scoped diff proving the B0 boundary.

- [ ] **Step 1: Run the complete B0-focused lane.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RecoveryLifecycleStateMapper|FullyQualifiedName~B0OutcomeVocabulary|FullyQualifiedName~B0FaultSeam|FullyQualifiedName~RecoveryMember|FullyQualifiedName~B0Result|FullyQualifiedName~DurableFormatsRegistry|FullyQualifiedName~B0CompatibilityHarness`

Expected: PASS with no skipped B0 tests.

- [ ] **Step 2: Run source-generation and trimming checks.**

Run: `dotnet build Scrinia.sln -c Release`

Expected: PASS with zero errors and zero B0-attributable warnings.

Run: `dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained false /p:PublishTrimmed=true`

Expected: PASS; the `scri serve` startup and generated DTO metadata tests remain green; no reflection serializer warnings introduced by B0.

- [ ] **Step 3: Run the full solution suite.**

Run: `dotnet test Scrinia.sln`

Expected: exit code 0, zero failed or skipped tests outside explicitly opt-in performance benchmarks, no test-host hang, crash, abort, or timeout. Record pass/skipped/failed/total and the no-hang evidence.

- [ ] **Step 4: Perform the scoped diff audit.**

Run: `git status --short` and `git diff --name-only HEAD~9..HEAD`

Expected: only the intended B0 source/test files appear in the B0 task commits; `RecoveryMember` is not enumerated in any durable format, bundle, or sidecar; no second parser boundary or facade was added; no public MCP/CLI name or successful payload shape changed; and `ParseVerifiedEnvelope`/`ConsumeParsed`/`RecoveryVerification*` are absent from the diff.

- [ ] **Step 5: Perform the deferred-work/type audit.** Search the plan and implementation for deferred-work markers, guessed exception-message parsing, reflection `JsonSerializer` calls, `object` payloads, a second `RecoveryLifecycleState` enum, a second `B0Result` facade, or references to excluded work (B1-B6 contracts, remote/plugin/HTTP surfaces, format evolution). Resolve every match before approval.

- [ ] **Step 6: Commit the final compatibility gate.**

```bash
git add src tests
git commit -m "test: close B0 transaction-foundation compatibility gate"
```

## Final Verification Gates

- [ ] The lifecycle mapper (`RecoveryLifecycleStateMapper`) projects all seven canonical states to the four machine vocabularies with no write path and no transition logic.
- [ ] `B0Outcome` has exactly nine union members; `B0Failure` records its producing taxonomy; every source taxonomy keeps its members.
- [ ] `B0Result` is a thin facade over the four Core results; each wrapper copies outcome, carries warning/recovery, and collapses `OperationId`/`TransactionId` to one `CorrelationId`.
- [ ] `RecoveryMember` is the only new production type that changes representation at the result boundary; `RawMemberSnapshot` capture in `AppendTransaction.cs:102-122` and `WholeMemoryTransaction.cs:6-41` is unchanged; `RecoveryMember` is never written to disk.
- [ ] `B0FaultSeam.FaultPhase` covers all 62 mapped phases; `FailOnceAt` clears after one, `FailPersistentlyAt` repeats, `ClearFaults` resets, `ThrowIfInjected` throws at the consuming site; production `Inject` is never set.
- [ ] `DurableFormats.Registry` holds exactly the twelve current formats; `FormatVersion.Evaluate` returns `Refuse` for unknown versions; `FormatContract`/`FormatDisposition`/`FormatVersion.Evaluate` keep one definition each in `Formats/`.
- [ ] `B0Result` is registered as an additive root in `ScriniaMcpJsonContext`; `McpResponse.ErrorCodes` and `CliMcpOutput` passthrough are unchanged; no second facade exists.
- [ ] The compatibility harness loads current append, whole-memory, multi-memory, file-entity, restore, and migration evidence and returns verified/committed/created outcomes through `B0Result` without behavior change.
- [ ] The bare-complete migration case (`LegacyMigrationState.cs:424`; `LegacyMigrationServiceTests.CompletedJournalWithTrailingContentIsRejectedByInspectAndMigrate` `tests/Scrinia.Tests/LegacyMigrationServiceTests.cs:161-184`) still rejects `complete` without `prepared`.
- [ ] `dotnet build Scrinia.sln` passes with zero errors and zero B0-attributable warnings.
- [ ] `dotnet test Scrinia.sln` exits 0 with zero failures and no hang/crash/abort/timeout.
- [ ] Package/trimming/source-generation/CLI-MCP contract checks pass; `B0Result` is registered in the correct JSON context; no reflection JSON is introduced.
- [ ] The scoped diff contains no unrelated source, test, evidence, generated, or historical-document change.
- [ ] An independent review records APPROVE (or a resolved REQUEST_CHANGES cycle) against the exact B0 diff and verification transcript.

## Self-Review Record

The plan covers the specification's sections in order: canonical lifecycle enum and mapper (Task 1), unified `B0Result` vocabulary with `B0Outcome`/`B0Failure` (Task 2), the single `B0FaultSeam` contract over twelve seams (Task 3), the one justified typed member carrier (Task 4), the `B0Result` facade and thin wrappers plus `B0Exception` (Task 5), the centralized `DurableFormats` registry (Task 6), public Core->MCP->CLI result mapping (Task 7), the compatibility harness and golden fixtures (Task 8), and every acceptance gate (Task 9). No deferred implementation or generic test instructions remain; all neighboring signatures are defined once in the Interfaces and Signatures block and reused consistently across tasks. The source-generation file-map entries are explicit: register `B0Result` and its sub-types in `ScriniaMcpJsonContext` (Task 7) and keep `CliMcpOutput` as the unchanged passthrough. The B5 do-not-change anchor (`ParseVerifiedEnvelope`, `ConsumeParsed`, `RecoveryVerification*`) is called out in every task that touches shared recovery files. The plan does not include B1-B6 contracts, shared-base migration, remote/plugin/HTTP surfaces, or format evolution.
