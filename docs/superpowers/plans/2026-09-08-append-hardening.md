# Append Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden persistent append into a journaled, byte-preserving transaction with typed outcomes, deterministic recovery, and stable MCP/CLI mappings while preserving existing create-via-append and ephemeral behavior.

**Architecture:** `FileMemoryStore.AppendAsync` becomes the sole owner of validation, canonical resolution, recovery gating, full-content derivation, raw-byte capture, archive planning, journal transitions, durable publication, rollback, and post-commit cleanup. Small Core contracts/codecs model immutable transaction data and journal state; the existing MCP append facade maps `AppendStoreResult` and append exceptions into `McpResponse`, and the CLI continues to render that structured response. Fault injection remains internal and operation/path scoped, following the existing `DurableFile.FailureInjector` and restore-journal seams.

**Tech Stack:** C# on `net10.0`, xUnit 2.9.3, FluentAssertions 6.12.2, source-generated `System.Text.Json`, filesystem-backed `FileMemoryStore`, `DurableFile` atomic replacement, MCP `McpResponse`, ConsoleAppFramework CLI.

**Spec:** `docs/superpowers/specs/2026-09-08-append-hardening-design.md`

## Global Constraints

- Phase A only; do not convert `remember`, `compact`, import, migration, bundle writes, or arbitrary sidecar mutations to the append transaction mechanism.
- Do not change NMP/2, sidecar schema, index schema, or archive naming rules except for `<memory-versions-directory>/.append-journal.json`.
- `AppendAsync` owns the complete append transaction; MCP and CLI facades must not independently derive content, archive the old version, or write the artifact/sidecar.
- Persistent rollback writes exact captured raw bytes and restores missing members as missing; recovery must never decode, deserialize, derive, or format rollback data.
- A journal is durable before archive creation or live publication; malformed, zero-length, unsupported-version, or hash-inconsistent journals are `RECOVERY_REQUIRED` and are never silently deleted.
- `Committed` means both new members were written, flushed, and verified; cleanup and event failures never roll back a committed pair.
- The only append exception codes are `RecoveryRequired` and `Internal`; all other conditions use typed results or the existing facade error contract.
- No append failure may escape as the MCP framework's generic `An error occurred invoking 'append'` response.
- Deterministic tests must use operation-ID/path-scoped seams, not sleeps, wall-clock ordering, process races, or real disk-fault dependence.
- Phase B is explicitly deferred: no workspace-wide transaction gate, shared append/restore journal, cross-memory transaction, general archive/pruning service, background journal GC, or NMP/2/sidecar redesign.

---

### Task 1: Define Typed Append Contracts And Fault Seams

**Files:**
- Create: `src/Scrinia.Core/AppendContracts.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs:49-63`
- Modify: `src/Scrinia.Core/DurableFile.cs:10-98`
- Modify: `src/Scrinia.Core/Scrinia.Core.csproj` only if the new file requires an explicit compile/include change
- Test: `tests/Scrinia.Tests/AppendContractTests.cs`
- Test: `tests/Scrinia.Tests/DurableFileFaultSeamTests.cs`
- Test: `tests/Scrinia.TestDoubles/FakeMemoryStore.cs:43-46`

**Interfaces:**
- Consumes: `ArtifactEntry`, `MemoryAddress`, `DurableFile`, and existing `IMemoryStore.AppendAsync(MemoryWrite, CancellationToken)`.
- Produces: `AppendStoreResult`, `AppendStoreOutcome`, `AppendStoreWarning`, `AppendWarningCode`, `AppendRecoveryInfo`, `AppendStoreException`, `AppendStoreErrorCode`, `AppendFaultPoint`, and an internal fault-seam contract that later tasks can invoke by operation ID and path.

- [ ] **Step 1: Write the failing contract tests**

```csharp
[Fact]
public void AppendStoreResult_ExposesTheSpecifiedOutcomeShape()
{
    var result = new AppendStoreResult(AppendStoreOutcome.Appended, null, "op-1");

    result.Outcome.Should().Be(AppendStoreOutcome.Appended);
    result.Entry.Should().BeNull();
    result.OperationId.Should().Be("op-1");
    result.Warning.Should().BeNull();
    result.Recovery.Should().BeNull();
}

[Fact]
public void AppendStoreException_AllowsOnlyRecoveryRequiredAndInternalCodes()
{
    var exception = new AppendStoreException(
        AppendStoreErrorCode.RecoveryRequired, "op-1", "recovery required");

    typeof(AppendStoreException).IsSealed.Should().BeTrue();
    exception.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
    exception.OperationId.Should().Be("op-1");
}
```

- [ ] **Step 2: Run the focused contract tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendContractTests"`

Expected: FAIL because the typed append contract types do not yet exist.

- [ ] **Step 3: Add the exact public contracts from spec Sections 4 and 4.1**

Implement the following signatures without extra append exception subclasses or error codes:

```csharp
public sealed record AppendStoreResult(
    AppendStoreOutcome Outcome,
    ArtifactEntry? Entry,
    string OperationId,
    AppendStoreWarning? Warning = null,
    AppendRecoveryInfo? Recovery = null);

public enum AppendStoreOutcome { Created, Appended, SuccessWithWarning, RecoveryRequired }

public sealed record AppendStoreWarning(AppendWarningCode Code, string Message);
public enum AppendWarningCode { PostCommitCleanupFailed, EventFailed }

public sealed class AppendStoreException : Exception
{
    public AppendStoreException(AppendStoreErrorCode code, string operationId,
        string message, Exception? innerException = null) : base(message, innerException) { }
    public AppendStoreErrorCode Code { get; }
    public string OperationId { get; }
}

public enum AppendStoreErrorCode { RecoveryRequired, Internal }
```

Define `AppendRecoveryInfo` as the structured operation ID, journal path, durable state, and recovery action/detail needed by MCP and CLI; keep raw memory bytes out of its public fields.

Use this concrete shape so later mappings have stable names:

```csharp
public sealed record AppendRecoveryInfo(
    string JournalPath,
    string State,
    string Action,
    string Detail);
```

Define the deterministic seam as:

```csharp
internal enum AppendFaultPoint
{
    RawArtifactRead, RawSidecarRead,
    JournalWrite, JournalFlush, JournalParse, JournalDelete,
    ArchiveArtifactWrite, ArchiveArtifactFlush,
    ArchiveSidecarWrite, ArchiveSidecarFlush,
    LiveArtifactWrite, LiveArtifactFlush,
    LiveSidecarWrite, LiveSidecarFlush,
    VerificationRead, RollbackWrite, RollbackDelete,
    ArchiveCleanup, PruningDelete, BeforeEvent, AfterEvent
}

internal static Func<AppendFaultPoint, string, string, Exception?>? AppendFaultInjector { get; set; }
```

The implementation must expose internal test helpers with these signatures: `void FailOnceAt(AppendFaultPoint point)`, `void FailPersistentlyAt(AppendFaultPoint point)`, `void ClearFaults()`, and `static IReadOnlyList<AppendFaultPoint> PreCommitFaultPoints { get; }` on `FileMemoryStore`.

- [ ] **Step 4: Change the store contract and test double to return the typed result**

Change both declarations to:

```csharp
Task<AppendStoreResult> AppendAsync(MemoryWrite write, CancellationToken ct = default);
```

Update `FakeMemoryStore.AppendAsync` to return `new AppendStoreResult(AppendStoreOutcome.Appended, entry, "fake-operation")` so existing facade tests remain a useful double.

- [ ] **Step 5: Add deterministic append fault injection alongside the durable-file seam**

Use an internal `AsyncLocal<Func<AppendFaultPoint, string, string, Exception?>>` or equivalent with arguments `(faultPoint, operationId, path)`, one-shot and persistent injection behavior, and explicit points for raw reads, journal write/flush/parse/delete, archive body/sidecar write/flush, live artifact/sidecar write/flush, verification reads, rollback write/delete, cleanup, pruning, before-event, and after-event. Do not make `DurableFile.FailureInjector` depend on timing; route append-specific failures through the append seam while retaining existing non-append tests.

- [ ] **Step 6: Run the focused contract and seam tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendContractTests|FullyQualifiedName~DurableFileFaultSeamTests"`

Expected: PASS, including assertions that a one-shot fault is consumed once, a persistent fault remains active, and every callback receives the operation ID and exact destination path.

- [ ] **Step 7: Commit the contract-only slice**

```bash
git add src/Scrinia.Core/AppendContracts.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/DurableFile.cs tests/Scrinia.Tests/AppendContractTests.cs tests/Scrinia.Tests/DurableFileFaultSeamTests.cs tests/Scrinia.TestDoubles/FakeMemoryStore.cs
git commit -m "feat: define typed append contracts and fault seams"
```

### Task 2: Capture Pre-Write State And Derive An Immutable Transaction

**Files:**
- Create: `src/Scrinia.Core/AppendTransaction.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1907-2019`
- Test: `tests/Scrinia.Tests/AppendTransactionTests.cs`
- Test: `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs:336-424`

**Interfaces:**
- Consumes: `AppendStoreResult` contracts from Task 1, existing address validation, `MemoryDerivation.ForAppend`, `Nmp2ChunkedEncoder`, `BuildDerivedEntry`, `ReadArtifactAsync`, and sidecar read/write helpers.
- Produces: immutable `AppendTransaction`, `RawMemberSnapshot`, and `ArchivePlan` values whose byte arrays, paths, existence flags, and SHA-256 values remain unchanged after construction.

- [ ] **Step 1: Add failing tests for immutable raw capture and pre-write derivation**

```csharp
[Fact]
public async Task AppendAsync_CapturesOldArtifactAndSidecarBytesBeforeAnyMutation()
{
    using var tmp = new TempWorkspace();
    await tmp.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
    byte[] oldArtifact = await File.ReadAllBytesAsync(tmp.Store.ArtifactPath("target"));
    byte[] oldSidecar = await File.ReadAllBytesAsync(tmp.Store.MetaPath("target"));

    AppendStoreResult result = await tmp.Store.AppendAsync(
        MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

    result.Outcome.Should().Be(AppendStoreOutcome.Appended);
    tmp.Store.LastCapturedOldArtifactBytesForTest.Should().Equal(oldArtifact);
    tmp.Store.LastCapturedOldSidecarBytesForTest.Should().Equal(oldSidecar);
}
```

Expose only the internal test observation needed to prove ordering; do not expose rollback bytes publicly.

- [ ] **Step 2: Run the new transaction test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendTransactionTests"`

Expected: FAIL because append currently returns `ArtifactEntry`, derives after reading only the artifact, and does not capture raw sidecar bytes.

- [ ] **Step 3: Implement `RawMemberSnapshot`, `ArchivePlan`, and `AppendTransaction` exactly**

Use these required signatures:

```csharp
public sealed record AppendTransaction(
    string OperationId, MemoryAddress Address, string Scope, bool IsCreate,
    RawMemberSnapshot OldArtifact, RawMemberSnapshot OldSidecar,
    RawMemberSnapshot NewArtifact, RawMemberSnapshot NewSidecar,
    ArchivePlan Archive, DateTimeOffset StartedAt);

public sealed record RawMemberSnapshot(
    string Path, bool Existed, byte[] Bytes, string Sha256);

public sealed record ArchivePlan(
    bool Required, string? ArtifactArchivePath, string? SidecarArchivePath,
    ArchiveMemberStatus ArtifactStatus, ArchiveMemberStatus SidecarStatus,
    IReadOnlyList<string> PruneCandidates);

public enum ArchiveMemberStatus { NotRequired, Planned, Created, AlreadyPresentVerified, Failed }
```

Copy input byte arrays during construction, hash exact write bytes with SHA-256, and prevent mutation through read-only plan collections. Record missing old members with `Existed == false` and empty bytes.

- [ ] **Step 4: Move validation, canonical resolution, recovery preflight, raw reads, full derivation, and sidecar serialization into `AppendAsync`**

The operation order must be: validate address/content/scope; resolve canonical index identity before reading; create a fresh operation ID; run pending scope recovery; capture artifact and sidecar raw bytes plus missing flags; decode only for new-content derivation; build the complete new artifact and sidecar in memory; then construct `AppendTransaction` and its immutable archive plan before the first destructive operation. Preserve `AppendAsync`'s one-content-element validation and case-mismatch behavior.

- [ ] **Step 5: Verify existing regression behavior with the changed return type**

Update assertions in `FileMemoryStoreWriteSurfaceTests` and related tests from `ArtifactEntry entry = await AppendAsync(...)` to `AppendStoreResult result = ...; result.Entry.Should().NotBeNull(); var entry = result.Entry!`. Preserve concrete assertions for canonical casing, metadata carry-forward, create-via-append `Created`, one-chunk rejection, and ephemeral in-process append.

- [ ] **Step 6: Run the focused append surface tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FileMemoryStoreWriteSurfaceTests|FullyQualifiedName~AppendTransactionTests|FullyQualifiedName~CaseSensitiveMcpAppendTests"`

Expected: PASS; existing-memory append still has two chunks and preserved metadata, missing target follows `StoreAsync` semantics, and ephemeral append remains archive/journal-free.

- [ ] **Step 7: Commit the derivation/capture slice**

```bash
git add src/Scrinia.Core/AppendTransaction.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/AppendTransactionTests.cs tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs
git commit -m "feat: build immutable append transactions before mutation"
```

### Task 3: Implement Version-1 Append Journal And Recovery Gate

**Files:**
- Create: `src/Scrinia.Core/AppendJournal.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:700-790, 1907-2019, 2790-3020`
- Modify: `src/Scrinia.Core/DurableFile.cs:22-71` only to reuse durable replacement/flush semantics for journal records
- Test: `tests/Scrinia.Tests/AppendJournalTests.cs`
- Test: `tests/Scrinia.Tests/AppendRecoveryTests.cs`

**Interfaces:**
- Consumes: `AppendTransaction`, `ArchivePlan`, `DurableFile`, existing `WithExclusiveLock`, scope directory resolution, and restore journal admission patterns.
- Produces: internal `RecoverAppendAsync` and journal codec/state operations for `Prepared`, `ArchiveInProgress`, `Archived`, `NewPairInProgress`, `Committed`, `CleanupPending`, `RecoveryRequired`, and `RolledBack`.

Expose these internal test seams from `FileMemoryStore`: `Task WriteAppendRecoveryRequiredJournalForTest(string operationId)`, and `Task RecoverAppendAsync(string scope, CancellationToken cancellationToken = default)`. The production recovery call uses the same method under the existing exclusive scope lock.

- [ ] **Step 1: Write failing codec and recovery tests**

```csharp
[Fact]
public void AppendJournalCodec_RejectsMalformedUnsupportedAndHashInconsistentRecords()
{
    Action malformed = () => AppendJournalCodec.ParseAndValidate(
        "{}"u8.ToArray(), "local", ".../.append-journal.json");
    malformed.Should().Throw<AppendStoreException>()
        .Which.Code.Should().Be(AppendStoreErrorCode.RecoveryRequired);
}

[Fact]
public async Task AppendAsync_BlocksTheScopeWhenRecoveryJournalIsUnresolved()
{
    using var tmp = new TempWorkspace();
    await tmp.Store.WriteAppendRecoveryRequiredJournalForTest("op-1");

    Func<Task> append = () => tmp.Store.AppendAsync(
        MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["body"]));

    await append.Should().ThrowAsync<AppendStoreException>()
        .Where(e => e.Code == AppendStoreErrorCode.RecoveryRequired);
}
```

- [ ] **Step 2: Run the journal tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendJournalTests|FullyQualifiedName~AppendRecoveryTests"`

Expected: FAIL because `.append-journal.json`, its codec, and append recovery do not exist.

- [ ] **Step 3: Implement the journal schema and durable replacement**

Use a separate format name and version `1`; include operation ID, canonical address/scope, state, old/new paths and existence flags, Base64 raw old/new member bytes, SHA-256 hashes, archive paths/statuses, prune candidates and boundary, timestamps, committed hash, create flag, and append-only last error/recovery detail. Register new JSON types in the appropriate source-generated context. Write the complete record with durable replacement before archive/live writes; advance state by replacing the complete record rather than mutating a partial file.

- [ ] **Step 4: Implement `RecoverAppendAsync` and startup/store-boundary invocation**

Parse and validate bounded JSON, version, state monotonicity, paths, hashes, and payloads. Invoke recovery during store initialization and before every new append under the scope lock. Treat zero-length, malformed, unsupported-version, and hash-inconsistent journals as blocking `RecoveryRequired`; never delete or ignore them. Make successful recovery idempotent and make a retry after rollback allocate a new operation ID.

- [ ] **Step 5: Add crash-boundary transition tests**

Inject one failure after each durable transition and assert the exact Section 10.1 behavior: no authoritative transaction before `Prepared`; archive completion or retained evidence during archive; old-pair restore or new-pair completion after `Archived`; raw-pair reconciliation after one live member; commit only after both new hashes verify; and no rollback after `Committed`. Assert recovery can be run twice without changing the result.

- [ ] **Step 6: Run journal and recovery tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendJournalTests|FullyQualifiedName~AppendRecoveryTests"`

Expected: PASS with journal state and evidence retained for unresolved recovery, automatic scope blocking, idempotent successful recovery, and a new operation ID on a post-rollback retry.

- [ ] **Step 7: Commit the journal/recovery slice**

```bash
git add src/Scrinia.Core/AppendJournal.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/DurableFile.cs tests/Scrinia.Tests/AppendJournalTests.cs tests/Scrinia.Tests/AppendRecoveryTests.cs
git commit -m "feat: journal and recover append transactions"
```

### Task 4: Publish Archive And Live Pair With Raw-Byte Rollback

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1907-2019, 2624-2675`
- Modify: `src/Scrinia.Core/AppendJournal.cs`
- Test: `tests/Scrinia.Tests/AppendDurabilityTests.cs`
- Test: `tests/Scrinia.Tests/AppendRollbackTests.cs`

**Interfaces:**
- Consumes: prepared journal/recovery from Task 3, immutable snapshots/plans from Task 2, `DurableFile.AtomicWriteAllBytes`, sidecar serialization bytes, and append fault points from Task 1.
- Produces: an append path that archives and verifies both old members before changing live files, publishes artifact then sidecar with journal updates, verifies raw new hashes, and returns `Created`, `Appended`, or a typed failure/recovery result.

For ordering tests, retain internal copies as `byte[]? LastCapturedOldArtifactBytesForTest` and `byte[]? LastCapturedOldSidecarBytesForTest`; these are test observations only and are not part of the public result or journal response.

- [ ] **Step 1: Write failing byte-for-byte rollback tests**

```csharp
[Fact]
public async Task AppendAsync_RestoresBothMembersByteForByteAfterEveryPreCommitFault()
{
    using var tmp = new TempWorkspace();
    await tmp.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
    byte[] oldArtifact = await File.ReadAllBytesAsync(tmp.Store.ArtifactPath("target"));
    byte[] oldSidecar = await File.ReadAllBytesAsync(tmp.Store.MetaPath("target"));

    foreach (AppendFaultPoint point in FileMemoryStore.PreCommitFaultPoints)
    {
        tmp.Store.FailOnceAt(point);
        AppendStoreResult result = await tmp.Store.AppendAsync(
            MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));
        result.Outcome.Should().NotBe(AppendStoreOutcome.Appended);
        (await File.ReadAllBytesAsync(tmp.Store.ArtifactPath("target"))).Should().Equal(oldArtifact);
        (await File.ReadAllBytesAsync(tmp.Store.MetaPath("target"))).Should().Equal(oldSidecar);
    }
}
```

- [ ] **Step 2: Run the rollback tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendDurabilityTests|FullyQualifiedName~AppendRollbackTests"`

Expected: FAIL because the current rollback restores only decoded artifact text and does not restore sidecar bytes.

- [ ] **Step 3: Implement archive pair creation and independent statuses**

Before any live write, persist `Prepared`, create artifact and sidecar archive members independently, verify each exact old hash, and persist status after each member. If an archive destination already exists, accept it only when its raw bytes match the corresponding old hash; mismatch yields `RecoveryRequired` and never overwrites. Persist `Archived` only after both required members are verified.

- [ ] **Step 4: Implement artifact-first live publication and verification**

Write/flush the new artifact, persist `NewPairInProgress` with its status/hash, write/flush the new sidecar, raw-read both live members, compare exact new hashes, then persist `Committed` with the verified pair hash. Return only the entry representing the committed new state.

- [ ] **Step 5: Implement independent raw rollback**

For existing members, replace exact captured bytes only when the destination currently matches the operation's new hash or old hash; an unrelated hash is ambiguous and remains `RecoveryRequired`. For missing old members, delete only the operation-created path and verify absence. Record artifact rollback status before sidecar rollback; return public cancellation/durable-write results only after both old existence/hash conditions are proven. If proof fails, retain the journal and throw `AppendStoreException(RecoveryRequired, ...)`.

- [ ] **Step 6: Cover create-via-append failure and compatibility**

Test that a missing target delegates to `StoreAsync`, returns `Created`, creates no archive, and on injected creation failure deletes only newly created members and restores missing-file state. Seed an unrelated same-directory file and assert it survives recovery. Preserve existing sidecar/index schema and archive naming.

- [ ] **Step 7: Run durability, rollback, and compatibility tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendDurabilityTests|FullyQualifiedName~AppendRollbackTests|FullyQualifiedName~FileMemoryStoreWriteSurfaceTests"`

Expected: PASS; every injected pre-commit failure restores both old members byte-for-byte, archive mismatch is non-destructive, and create-via-append retains `StoreAsync` behavior.

- [ ] **Step 8: Commit the durable publication slice**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/AppendJournal.cs tests/Scrinia.Tests/AppendDurabilityTests.cs tests/Scrinia.Tests/AppendRollbackTests.cs
git commit -m "feat: publish append pairs with raw-byte rollback"
```

### Task 5: Add Post-Commit Cleanup And Safe Best-Effort Pruning

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:2624-2675` and append transaction flow
- Modify: `src/Scrinia.Core/AppendJournal.cs`
- Test: `tests/Scrinia.Tests/AppendCleanupTests.cs`

**Interfaces:**
- Consumes: committed journal and immutable `ArchivePlan` from Tasks 2-4 plus cleanup/pruning fault seams from Task 1.
- Produces: `SuccessWithWarning` with `PostCommitCleanupFailed` when cleanup/pruning fails or is cancelled after commit; no path can delete live state, the active journal, or recovery-required archive members.

- [ ] **Step 1: Write failing post-commit tests**

```csharp
[Fact]
public async Task AppendAsync_CleanupFailureKeepsNewPairAndReturnsDistinctWarning()
{
    using var tmp = new TempWorkspace();
    await tmp.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
    tmp.Store.FailOnceAt(AppendFaultPoint.ArchiveCleanup);

    AppendStoreResult result = await tmp.Store.AppendAsync(
        MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]));

    result.Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);
    result.Warning!.Code.Should().Be(AppendWarningCode.PostCommitCleanupFailed);
    (await tmp.Store.ReadAsync(MemoryAddress.Parse("/target")))!.ChunkCount.Should().Be(2);
}
```

- [ ] **Step 2: Run the cleanup tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendCleanupTests"`

Expected: FAIL because current append has no journaled cleanup boundary or warning result.

- [ ] **Step 3: Implement immutable plan-bounded pruning after durable commit**

Select and freeze exact candidates before mutation. Start deletion only after verified `Committed`; delete only candidates still in the plan and never the current artifact, current sidecar, active append journal, or archive member needed for recovery. Stop safely after any candidate. Create-via-append has no pruning boundary.

- [ ] **Step 4: Implement cleanup journal retention and warning mapping**

Remove the journal only after the final state and retained evidence are proven. If archive cleanup, journal deletion, or pruning fails, persist `CleanupPending`, retain evidence, keep the new pair, and return `AppendStoreResult(SuccessWithWarning, ...)` with `PostCommitCleanupFailed`.

- [ ] **Step 5: Run cleanup/pruning tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendCleanupTests"`

Expected: PASS; new state survives cleanup faults/cancellation, planned candidates are the only deletion targets, and current/recovery evidence is never pruned.

- [ ] **Step 6: Commit the cleanup slice**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/AppendJournal.cs tests/Scrinia.Tests/AppendCleanupTests.cs
git commit -m "feat: make append cleanup best effort after commit"
```

#### Task 5 verification follow-up — cancellation classification

The Task 5 verification reproducer initially returned `AppendStoreException(RecoveryRequired)` for
`CancellationDuringArchiveRollsBackAndPreservesOperationId`: an injected `OperationCanceledException`
was wrapped before the archive catch inspected it. A symmetric live-publication reproducer also
returned `DurableWriteException`. The pre-commit catches now use the caller token's actual
`IsCancellationRequested` state, so caller cancellation rolls back both live members and maps to
`AppendOperationCanceledException` with the operation ID preserved. Non-caller injected OCEs retain
their deterministic fault classifications. Focused evidence: `AppendRecoveryTests` cancellation and
injected-OCE cases pass (18/18 recovery tests); archive and live tests assert exact exception/code,
operation ID presence, and byte-for-byte pair preservation.

### Task 6: Map MCP And CLI Results And Isolate Event Failures

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:1002-1075, 1318-1326`
- Modify: `src/Scrinia.Mcp/McpResponse.cs:1-35`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:559-610`
- Modify: `src/Scrinia/Commands/ScriniaCommands.cs:127-167` only if append warning/error fields require renderer support
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs:20-49` only if new response DTOs need source-generation registration
- Test: `tests/Scrinia.Tests/AppendFacadeMappingTests.cs`
- Test: `tests/Scrinia.Tests/AppendEventBoundaryTests.cs`
- Test: `tests/Scrinia.Tests/CliAppendMappingTests.cs`

**Interfaces:**
- Consumes: `AppendStoreResult`, `AppendStoreException`, committed `ArtifactEntry`, and existing event sink contexts.
- Produces: MCP/CLI responses preserving outcome, operation ID, entry, warning, and recovery information; stable public codes `APPEND_CANCELLED`, existing validation/read-only/durable-write codes, `RECOVERY_REQUIRED`, and `INTERNAL`.

The test file defines `AppendResultStoreDouble : FakeMemoryStore` overriding `AppendAsync` to return the supplied result, `TestEntry()` returning the smallest valid `ArtifactEntry`, and `CancellingAfterAppendSink : IMemoryEventSink` whose `OnAppendedAsync` throws `OperationCanceledException`; install the double with `MemoryStoreContext.Current` and restore the prior context in `finally`, matching the existing `StoreScope` cleanup pattern.

- [ ] **Step 1: Write failing facade and event tests**

```csharp
[Fact]
public async Task MemoryAppend_MapsSuccessWithWarningWithoutGenericInvocationError()
{
    var store = new AppendResultStoreDouble(
        new AppendStoreResult(AppendStoreOutcome.SuccessWithWarning,
            Entry: TestEntry(), OperationId: "op-1",
            Warning: new(AppendWarningCode.EventFailed, "sink failed")));
    IMemoryStore? prior = MemoryStoreContext.Current;
    MemoryStoreContext.Current = store;
    try
    {

        McpResponse response = await new ScriniaMcpTools().MemoryAppend("/target", "new");

        response.IsError.Should().BeFalse();
        response.Status.Should().Be("success_with_warning");
        response.ToYaml().Should().Contain("op-1").And.Contain("event_failed");
        response.ToYaml().Should().NotContain("An error occurred invoking 'append'");
    }
    finally { MemoryStoreContext.Current = prior; }
}

[Fact]
public async Task MemoryAppend_AfterEventCancellationDoesNotUndoCommittedState()
{
    using var sink = new CancellingAfterAppendSink();
    IMemoryEventSink? priorSink = MemoryEventSinkContext.Current;
    MemoryEventSinkContext.Current = sink;
    using var tmp = new TempWorkspace();
    await tmp.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));

    try
    {
        McpResponse response = await new ScriniaMcpTools().MemoryAppend("/target", "new");

        response.Status.Should().Be("success_with_warning");
        response.Error.Should().BeNull();
        (await tmp.Store.ReadAsync(MemoryAddress.Parse("/target")))!.ChunkCount.Should().Be(2);
    }
    finally { MemoryEventSinkContext.Current = priorSink; }
}
```

- [ ] **Step 2: Run facade tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendFacadeMappingTests|FullyQualifiedName~AppendEventBoundaryTests|FullyQualifiedName~CliAppendMappingTests"`

Expected: FAIL because MCP currently expects `ArtifactEntry`, event exceptions escape, and CLI only catches restore recovery.

- [ ] **Step 3: Map each store outcome in `MemoryTools.Core.Append`**

Consume the typed result directly. Map `Created` to the existing stored/created response, `Appended` to appended action, `SuccessWithWarning` to a success-with-warning response preserving warning code/message and operation ID, and `RecoveryRequired` to structured recovery response. Map pre-commit cancellation to `APPEND_CANCELLED`, validation/read-only/input failures to existing typed codes, durable failures after successful rollback to the existing durable-write code, and `AppendStoreException` codes to `RECOVERY_REQUIRED` or `INTERNAL`. Include operation ID and recovery action without raw memory content.

- [ ] **Step 4: Move append event delivery into the post-commit boundary**

Ensure before-event runs before journal creation/live mutation and its failure/cancellation leaves no journal or live change. Deliver after-event the committed entry and operation ID through the chosen event interface. Catch after-event failure/cancellation, never roll back, and return `SuccessWithWarning(EventFailed, ...)`. A retry must retry event delivery rather than issue another append.

- [ ] **Step 5: Update CLI mapping and JSON/YAML assertions**

Keep `ScriniaCommands.Append` calling only `tools.Append`; map structured append warnings/errors through `EmitMcpResult` and existing JSON source-generation. Assert JSON contains operation ID, outcome/status, warning/recovery fields as applicable; error JSON uses stable public code and exits nonzero. Assert human output contains recovery action and never the generic MCP invocation error.

- [ ] **Step 6: Run facade and CLI tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendFacadeMappingTests|FullyQualifiedName~AppendEventBoundaryTests|FullyQualifiedName~CliAppendMappingTests|FullyQualifiedName~Golden.ResponseGoldenTests"`

Expected: PASS; all append outcomes are structured, after-event failures/cancellation leave committed state intact, before-event failures leave no mutation, and CLI/MCP mappings agree.

- [ ] **Step 7: Commit the facade slice**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/McpResponse.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia/Commands/ScriniaCommands.cs tests/Scrinia.Tests/AppendFacadeMappingTests.cs tests/Scrinia.Tests/AppendEventBoundaryTests.cs tests/Scrinia.Tests/CliAppendMappingTests.cs
git commit -m "feat: expose typed append outcomes through MCP and CLI"
```

### Task 7: Complete Fault Matrix, Regression Coverage, And Phase A Verification

**Files:**
- Modify: `tests/Scrinia.Tests/AppendDurabilityTests.cs`
- Modify: `tests/Scrinia.Tests/AppendRecoveryTests.cs`
- Modify: `tests/Scrinia.Tests/AppendFacadeMappingTests.cs`
- Modify: `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs`
- Modify: `tests/Scrinia.Tests/CaseSensitiveMcpAppendTests.cs` only for typed-result assertions
- Modify: `tests/Scrinia.TestDoubles/FakeMemoryStore.cs` for final result/event test seams

**Interfaces:**
- Consumes: all Phase A contracts and implementations from Tasks 1-6.
- Produces: regression evidence for every acceptance criterion, with no source/test changes outside the Phase A implementation and its tests.

- [ ] **Step 1: Add the complete deterministic fault matrix**

Cover raw artifact/sidecar reads; journal write, flush, parse, delete; archive body/sidecar write and flush; live artifact/sidecar write and flush; raw verification read; rollback write/delete; archive cleanup; pruning delete; before-event and after-event delivery. For each seam assert operation ID/path attribution and the correct pre-commit rollback, post-commit warning, or `RECOVERY_REQUIRED` result.

- [ ] **Step 2: Add committed-hash mismatch and ambiguity tests**

Inject a live hash mismatch after `Committed`, an unexpected unrelated live hash during rollback, missing archive member, and mismatched existing archive destination. Assert recovery verifies before acting, never blindly overwrites, restores only a proven old pair, and otherwise retains the journal/evidence with `AppendStoreException(RecoveryRequired, ...)`.

- [ ] **Step 3: Add explicit compatibility regressions**

Assert case-insensitive canonical resolution, metadata carry-forward, ephemeral append without persistent journal/archive, append-to-missing delegation, multi-element rejection, read-only refusal before side effects, and existing golden response shapes for ordinary created/appended success.

- [ ] **Step 4: Run the complete required verification commands**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`

Expected: PASS for the core/MCP/CLI append suite, including all existing tests.

Run: `dotnet test Scrinia.sln`

Expected: PASS for the full default solution suite with no opt-in benchmark category required.

Run: `dotnet build Scrinia.sln`

Expected: PASS with no source-generation, nullable, or interface implementation errors.

- [ ] **Step 5: Self-review the implementation against every Phase A acceptance criterion**

Check the 14 numbered criteria in spec Section 17 one by one, record the corresponding test name in the review notes, scan the changed files for any generic append invocation error path, and verify no Phase B workspace-wide gating or convenience refactor was introduced.

- [ ] **Step 6: Commit the regression and verification slice**

```bash
git add tests/Scrinia.Tests/AppendDurabilityTests.cs tests/Scrinia.Tests/AppendRecoveryTests.cs tests/Scrinia.Tests/AppendFacadeMappingTests.cs tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs tests/Scrinia.Tests/CaseSensitiveMcpAppendTests.cs tests/Scrinia.TestDoubles/FakeMemoryStore.cs
git commit -m "test: cover append hardening recovery and mappings"
```

## Phase B Deferral

Phase B is not implemented or planned as source work in this document. Specifically deferred are applying the protocol to `remember`, `compact`, import, migration, bundle writes, or arbitrary sidecar mutations; unifying append and restore journals; cross-memory or multi-scope transactions; transactional external event systems; a general archive/pruning service; background journal garbage collection and fleet-wide recovery reporting; and NMP/2 or sidecar changes for richer transaction metadata. No workspace-wide append gate is introduced in Phase A; only the target scope's unresolved append journal blocks its normal writes, exactly as required by the approved spec.

## Plan Self-Review

- **Spec coverage:** Tasks 1-2 cover typed contracts, pre-write derivation, raw capture, and deterministic seams; Task 3 covers journal ordering, all states, crash recovery, malformed evidence, scope blocking, idempotence, and new retry IDs; Task 4 covers archive immutability/statuses, paired publication, byte rollback, create-via-append, and cancellation-safe durability; Task 5 covers committed-only pruning and best-effort cleanup; Task 6 covers before/after events and MCP/CLI mappings; Task 7 covers the complete fault matrix and regression acceptance criteria.
- **Placeholder scan:** No `TBD`, `TODO`, “implement later”, “add appropriate error handling”, or unbounded “write tests for the above” instruction remains. Every task names files, interfaces, test names/assertions, commands, expected results, and a focused commit.
- **Type consistency:** `IMemoryStore.AppendAsync` returns `Task<AppendStoreResult>` from Task 1 onward; all later tasks consume `AppendStoreResult.Entry` and its outcome rather than `ArtifactEntry` directly. `AppendStoreWarning`, `AppendWarningCode`, `AppendStoreException`, and `AppendStoreErrorCode` names match the spec. `AppendTransaction`, `RawMemberSnapshot`, and `ArchivePlan` signatures match spec Sections 7 and 8.
- **Scope check:** The plan keeps the transaction append-specific, preserves existing restore-journal behavior, limits recovery blocking to the affected scope, and explicitly defers Phase B workspace-wide and cross-operation work.
