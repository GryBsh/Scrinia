# B3 Multi-Memory Transactions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an atomic, recoverable coordinator for multiple persistent memory participants in exactly one topic scope while preserving B2 participant journals and all existing single-memory surfaces.

**Architecture:** `MultiMemoryTransactionCoordinator` owns admission, deterministic ordering, the one scope lock/barrier, the private `.multi-memory-transaction.json` decision record, and the aggregate lifecycle. Typed `IMultiMemoryParticipantAdapter` handles are produced by the existing B2 discovery/admission path and remain the only authority for participant payload preparation, commit, verification, rollback, and cleanup. `FileMemoryStore` integrates the coordinator at its scope lock/recovery boundary; MCP and CLI continue to render existing response envelopes and do not infer state from files or exception text.

**Tech Stack:** C# on `net10.0`, nullable reference types, `FileLock`, `DurableFile.AtomicWriteAllBytes`, `System.Text.Json` source generation, xUnit, FluentAssertions, existing `TestHelpers.StoreScope`, MCP `McpResponse`, ConsoleAppFramework CLI.

**Spec:** `docs/superpowers/specs/2026-09-12-multi-memory-transaction-design.md`

## Global Constraints

- The scope boundary is one persistent topic scope; every participant must resolve to the same scope before admission.
- Reject an empty participant set, duplicate canonical subjects, ephemeral addresses, file-entity paths, and cross-scope participants.
- The coordinator record is exactly `<scope>/.multi-memory-transaction.json`, is private control data, and is excluded from enumeration, bundle export, sidecar enumeration, facts, and ordinary search.
- Ship only coordinator record version 1; do not migrate, rewrite, or silently default older or unknown records.
- `state` is exactly `prepared`, `committed`, or `recoveryRequired` in the coordinator record.
- `transactionId` and participant `operationId` values are opaque, non-empty strings compared ordinally; never parse them as timestamps or filesystem names.
- Sort participants by ordinal canonical subject, then ordinal operation ID; never acquire locks or invoke participants in caller-provided order.
- Acquire the scope lock once; participants must not acquire another scope lock.
- Run the existing scope recovery barrier before admission and hold it across prepare, decision publication, commit, verification, and coordinator cleanup.
- Before `committed`, failures are non-committed and use best-effort B2 rollback; after `committed`, rollback is forbidden and recovery only rolls forward.
- A missing, malformed, inconsistent, unsafe, ambiguous, unsupported, or unverifiable record/evidence fails closed and blocks only its scope.
- Invalidate scope-local index/cache and facts/entity projections once, after all participant final bytes are verified; other scopes are not invalidated.
- Replaying a decision is idempotent and must not issue a second payload mutation after verified commit.
- B2 physical locations and wire formats remain unchanged: restore `.restore-journal.json`, append `<memory>/versions/.append-journal.json`, and file-entity `.file-entity-{id}.recovery`.
- All B3 records and typed result DTOs use source-generated `System.Text.Json`; reflection, `object` payloads, untyped reserialization, and exception-message parsing are forbidden.
- Existing MCP names, CLI names, successful payload shapes, wrappers, correlation semantics, exit classes, and single-memory behavior remain unchanged.
- Do not add multi-scope or workspace-wide transactions, plugins, hooks, events, archive policy/GC, NMP/2 changes, ordinary sidecar changes, participant journal duplication, HTTP/REST, remote CLI, daemon, or fleet coordination.
- Do not include B1 contamination cleanup or reinterpret unrelated recovery evidence.
- Do not modify existing source, tests, docs, or specs outside the implementation changes enumerated by this plan; the plan itself is the only file changed in this planning task.

---

## File Map

### New implementation files

- `src/Scrinia.Core/MultiMemoryTransaction.cs` — immutable request, canonical participant manifest, typed participant/aggregate outcomes, lifecycle/error enums, and coordinator/adaptor interfaces.
- `src/Scrinia.Core/MultiMemoryTransactionRecord.cs` — exact version-1 record DTO, state converter, validation, path/identity checks, and source-generated JSON context registration.
- `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs` — admission, lock ownership, prepare/decision/commit/recovery orchestration, and idempotent coordinator cleanup.

### Modified implementation files

- `src/Scrinia.Core/FileMemoryStore.cs` — expose narrow internal coordinator seams, discover the coordinator record before cache/read/write admission, hold the existing scope barrier/lock around the coordinator, and perform one post-commit invalidation.
- `src/Scrinia.Core/RecoveryDiscovery.cs` — recognize the coordinator record as a scope-local control candidate before B2 candidates and delegate typed parsing/blocking without treating it as a participant journal.
- `src/Scrinia.Core/RecoveryContracts.cs` — add the coordinator recovery operation/block metadata only if the existing typed recovery boundary needs it; keep B2 operation contracts intact.
- `src/Scrinia.Core/Formats/DurableFormats.cs` — register `multi-memory-transaction` version 1 in `All` and preserve refuse-on-newer semantics.
- `src/Scrinia.Mcp/MemoryTools.Core.cs` — map aggregate Core outcomes through existing `McpResponse` fields; never parse journal names/messages.
- `src/Scrinia.Mcp/MemoryTools.cs` — add no new MCP tool name; adjust only the already-supported multi-memory entry point, if present, to catch the typed aggregate/coordinator failures.
- `src/Scrinia/Commands/ScriniaCommands.Memory.cs` — map the same typed aggregate through the CLI's direct internal-tool calls; do not assume MCP facade changes reach CLI.
- `src/Scrinia/Commands/CliJsonContext.cs` — register a new CLI aggregate DTO when the already-supported multi-memory command directly serializes one; otherwise add the explicit test proving that the existing `CliMcpOutput` wrapper remains sufficient.
- `src/Scrinia.Mcp/MemoryTools.Skills.cs` — register aggregate DTOs when MCP transport directly serializes them; normal tool output remains `string`/YAML.

### New/modified tests

- `tests/Scrinia.Tests/MultiMemoryTransactionContractTests.cs` — DTO invariants, exact JSON, source-generated serialization, and record validation.
- `tests/Scrinia.Tests/MultiMemoryTransactionAdmissionTests.cs` — one-scope admission, identity, path containment, ordering, and lock contention.
- `tests/Scrinia.Tests/MultiMemoryTransactionLifecycleTests.cs` — prepare/decision/commit, all failure classes, rollback, ambiguity, cleanup retry, and idempotent replay.
- `tests/Scrinia.Tests/MultiMemoryTransactionRecoveryTests.cs` — restart/read/write discovery, prepared abort, committed roll-forward, malformed/newer records, and scope isolation.
- `tests/Scrinia.Tests/MultiMemoryTransactionVisibilityTests.cs` — barrier visibility, cache/index/facts invalidation timing and scope locality.
- `tests/Scrinia.Tests/MultiMemoryTransactionFacadeTests.cs` — MCP aggregation and unchanged single-memory mapping.
- `tests/Scrinia.Tests/CliMultiMemoryTransactionTests.cs` — CLI direct-call aggregation, JSON/YAML wrapper, exit class, and correlation preservation.
- `tests/Scrinia.Tests/FormatVersionTests.cs` — update the existing registry-count/contract assertions for the one new durable format.
- Existing B2 regression suites named in Task 7 — run unchanged unless a test must be extended to prove B3 did not alter B2 wire locations or behavior.

## Interfaces and Signatures

The following internal contracts are the only cross-task API. Implementers must use these names and types unless a compile-time conflict with an existing symbol is found; resolve such a conflict by adapting the implementation, not by adding a second parallel contract.

```csharp
internal enum MultiMemoryTransactionState { Prepared, Committed, RecoveryRequired }

internal enum MultiMemoryOutcome {
    Committed, CommittedCleanupPending, RolledBack, RecoveryRequired, Rejected
}

internal enum MultiMemoryFailure {
    Admission, Prepare, DecisionPublication, ParticipantCommit,
    Verification, Rollback, Cleanup, Malformed, UnsupportedVersion,
    Unsafe, Inconsistent, Ambiguous, ReplayIoFailed, LockTimeout
}

internal sealed record MultiMemoryParticipantRequest(
    MemoryAddress Address,
    string OperationId,
    string Kind);

internal sealed record MultiMemoryTransactionRequest(
    string Scope,
    IReadOnlyList<MultiMemoryParticipantRequest> Participants);

internal sealed record MultiMemoryParticipantManifest(
    string Subject,
    string OperationId,
    string Kind);

internal sealed record MultiMemoryParticipantOutcome(
    string Subject,
    string OperationId,
    string Kind,
    string? EvidencePath,
    bool Committed,
    bool RolledBack,
    string? Reason);

internal sealed record MultiMemoryAggregateResult(
    string TransactionId,
    string Scope,
    MultiMemoryOutcome Outcome,
    IReadOnlyList<MultiMemoryParticipantOutcome> Participants,
    MultiMemoryFailure? Failure,
    string? Warning,
    string? EvidencePath);

internal interface IMultiMemoryParticipantAdapter {
    string Kind { get; }
    string Subject { get; }
    string OperationId { get; }
    string EvidencePath { get; }
    Task PrepareAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task VerifyCommittedAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
    Task CleanupAsync(CancellationToken cancellationToken);
}

internal interface IMultiMemoryTransactionCoordinator {
    Task<MultiMemoryAggregateResult> ExecuteAsync(
        MultiMemoryTransactionRequest request,
        Func<MultiMemoryParticipantRequest, CancellationToken,
            Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken = default);
    Task<MultiMemoryAggregateResult> RecoverAsync(
        string scope,
        CancellationToken cancellationToken = default);
}
```

The adapter factory is deliberately typed and asynchronous. It must return an existing B2 adapter handle/result, not raw JSON, exception text, or guessed filesystem paths. If the current B2 code cannot expose a handle without leaking payload data, add an internal adapter wrapper that delegates to `AppendRecoveryAdapter`, `RestoreRecoveryAdapter`, or `FileEntityRecoveryAdapter`; do not copy or replace their journals.

## Task Dependencies

`Task 1` is foundational. `Task 2` depends on Task 1. `Task 3` depends on Tasks 1 and 2. `Task 4` depends on Tasks 1 through 3. `Task 5` depends on Tasks 3 and 4. `Task 6` depends on Tasks 1 through 5. `Task 7` depends on all prior tasks and is the final compatibility gate. Tasks must be independently reviewed and green before the next dependent task starts.

### Task 1: Coordinator Contracts and Participant Model

**Files:**
- Create: `src/Scrinia.Core/MultiMemoryTransaction.cs`
- Modify: `src/Scrinia.Core/RecoveryContracts.cs` only if a typed coordinator block is required by the existing recovery boundary
- Create: `tests/Scrinia.Tests/MultiMemoryTransactionContractTests.cs`

**Interfaces:**
- Consumes: `MemoryAddress`, `FileMemoryStore.ResolveAddress`, `RecoveryBlock`, `RecoveryInfo`, and existing `AppendStoreOutcome`/`WholeMemoryOutcome` semantics.
- Produces: `MultiMemoryTransactionRequest`, `MultiMemoryParticipantRequest`, `MultiMemoryParticipantManifest`, `MultiMemoryParticipantOutcome`, `MultiMemoryAggregateResult`, `IMultiMemoryParticipantAdapter`, and `IMultiMemoryTransactionCoordinator` exactly as listed above.

- [ ] **Step 1: Write failing contract tests.** Add tests that construct valid and invalid requests and assert: null/empty participant lists throw; blank operation IDs/kinds throw; two equal canonical subjects throw; participant outcome order is immutable; aggregate participant order cannot be mutated through the input list; `CommittedCleanupPending` is distinct from `Committed`; and a `RecoveryRequired` aggregate requires a safe evidence path or typed block.
- [ ] **Step 2: Run the focused tests to verify failure.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionContractTests`

Expected: FAIL because the B3 contracts do not exist.
- [ ] **Step 3: Implement the immutable contracts.** Use sealed records with constructor guards, defensive `ToArray()`/`Array.AsReadOnly()` copies, ordinal identity comparisons, and typed enums. Keep all contracts `internal` unless an existing public facade requires a DTO, and do not add payload/body/hash fields to the participant manifest or aggregate outcome.
- [ ] **Step 4: Run the focused tests to verify the contract.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionContractTests`

Expected: PASS with all invalid identity, immutability, and classification assertions green.
- [ ] **Step 5: Commit the task.**

```bash
git add src/Scrinia.Core/MultiMemoryTransaction.cs src/Scrinia.Core/RecoveryContracts.cs tests/Scrinia.Tests/MultiMemoryTransactionContractTests.cs
git commit -m "feat: add multi-memory transaction contracts"
```

### Task 2: Durable Decision Record and Source-Generated Format

**Files:**
- Create: `src/Scrinia.Core/MultiMemoryTransactionRecord.cs`
- Modify: `src/Scrinia.Core/Formats/DurableFormats.cs`
- Modify: `src/Scrinia.Core/RecoveryDiscovery.cs` only for typed coordinator-record discovery
- Create: `tests/Scrinia.Tests/MultiMemoryTransactionContractTests.cs`
- Modify: `tests/Scrinia.Tests/FormatVersionTests.cs`

**Interfaces:**
- Consumes: Task 1 manifest/state types, `DurableFile.AtomicWriteAllBytes`, `PathContainment.ResolveCanonical`, `MemoryAddress.Parse`, `DurableFormats`, and the existing recovery block taxonomy.
- Produces: `MultiMemoryTransactionRecord`, `MultiMemoryTransactionRecordCodec.Serialize`, `MultiMemoryTransactionRecordCodec.ParseAndValidate`, `MultiMemoryTransactionRecordCodec.WithState`, `MultiMemoryTransactionRecordCodec.GetPath`, and `MultiMemoryTransactionJsonContext.Default.MultiMemoryTransactionRecord`.

- [ ] **Step 1: Write exact-format tests.** Serialize a record with one participant and assert the UTF-8 JSON contains exactly the camel-case members `format`, `version`, `transactionId`, `scope`, `state`, `participants`, `createdAt`, and `updatedAt`; participant members are exactly `subject`, `operationId`, and `kind`; no payload, artifact, sidecar, hash, rollback, or unknown extension is emitted. Assert state strings are lower camel case and timestamps round-trip.
- [ ] **Step 2: Write validation and version tests.** Assert rejection for wrong format, version 0, version 2, blank IDs, scope mismatch, duplicate subjects/operation IDs, blank participant fields, unsorted manifests, invalid timestamps, unsafe record path, and participant path/evidence claims outside the expected scope. Assert a newer record is retained and classified `UnsupportedVersion`; parsing it does not rewrite bytes.
- [ ] **Step 3: Run the focused tests to verify failure.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionContractTests|FullyQualifiedName~FormatVersionTests`

Expected: FAIL because the record codec and durable-format registration do not exist.
- [ ] **Step 4: Implement the source-generated record codec.** Add the exact DTO and a custom `JsonConverter<MultiMemoryTransactionState>`; annotate the Core context with `[JsonSerializable(typeof(MultiMemoryTransactionRecord))]`, `[JsonSerializable(typeof(MultiMemoryParticipantManifest))]`, `[JsonSerializable(typeof(MultiMemoryTransactionState))]`, and any concrete immutable list/array types needed by the compiler. Use `JsonSerializer.SerializeToUtf8Bytes(value, MultiMemoryTransactionJsonContext.Default.MultiMemoryTransactionRecord)` and the matching generated metadata for deserialization. Never call reflection-based `JsonSerializer` overloads.
- [ ] **Step 5: Register the durable format.** Add `DurableFormats.MultiMemoryTransaction = new("multi-memory-transaction", Current: 1, AbsentMeans: null, FormatDisposition.Refuse, FormatDisposition.Refuse)` and include it in `DurableFormats.All`; keep format-registry tests exact rather than changing a broad count without asserting the new contract.
- [ ] **Step 6: Add typed discovery.** Make discovery inspect `<scope>/.multi-memory-transaction.json` before participant evidence, validate canonical containment and scope, return a typed coordinator block for malformed/unsupported/unsafe/inconsistent records, and never add the record as an `Append`, `Restore`, or `FileEntity` candidate.
- [ ] **Step 7: Run focused tests and trimming/build checks.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionContractTests|FullyQualifiedName~FormatVersionTests`

Expected: PASS, including exact JSON and “newer record left byte-for-byte unchanged.”

Run: `dotnet build src/Scrinia.Core/Scrinia.Core.csproj -c Release`

Expected: PASS with no source-generation warnings/errors.
- [ ] **Step 8: Commit the task.**

```bash
git add src/Scrinia.Core/MultiMemoryTransactionRecord.cs src/Scrinia.Core/Formats/DurableFormats.cs src/Scrinia.Core/RecoveryDiscovery.cs tests/Scrinia.Tests/MultiMemoryTransactionContractTests.cs tests/Scrinia.Tests/FormatVersionTests.cs
git commit -m "feat: add durable multi-memory decision record"
```

### Task 3: Deterministic Admission, Locking, and B2 Participant Adapters

**Files:**
- Create: `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `src/Scrinia.Core/RecoveryDiscovery.cs`
- Create: `tests/Scrinia.Tests/MultiMemoryTransactionAdmissionTests.cs`

**Interfaces:**
- Consumes: Task 1 coordinator/adaptor interfaces, Task 2 record codec, `FileMemoryStore.ResolveAddress`, `GetStoreDirForScope`, `GetLockPath`, `WithExclusiveLock`, `WithRecoveryReadBarrier`, `RecoveryDiscoveryCoordinator.Discover`, and typed B2 adapters.
- Produces: `MultiMemoryTransactionCoordinator.NormalizeAndOrder`, `AdmitAsync`, and a single scope-owned execution entry from `FileMemoryStore` that accepts a `MultiMemoryTransactionRequest` and typed adapter factory.

- [ ] **Step 1: Write admission tests.** Cover empty input, `/temp`/`~` input, `/skill` and `/agent` file-entity input, duplicate canonical subject with differing casing, duplicate operation ID, blank operation ID, cross-topic `/notes/a` plus `/other/b`, unsafe subject/path traversal, and a valid shuffled set. Assert rejected requests do not create the coordinator record, acquire a mutation lock, or call any adapter.
- [ ] **Step 2: Write deterministic ordering tests.** Supply the same participants in at least five permutations and assert the adapter invocation manifest is identical: ordinal `Subject`, then ordinal `OperationId`. Assert the aggregate participant outcomes use that same order.
- [ ] **Step 3: Write concurrency/lock tests.** Hold the scope `.lock` with `FileLock.AcquireExclusive`, invoke the coordinator, and assert a typed `LockTimeout`/rejected result with no participant call. Run two callers against one scope and assert only one enters admission/mutation while a different topic proceeds independently.
- [ ] **Step 4: Run admission tests to verify failure.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionAdmissionTests`

Expected: FAIL because B3 admission and coordinator entry points do not exist.
- [ ] **Step 5: Implement normalization and admission.** Resolve every `MemoryAddress` before locking; reject forbidden kinds and file entities; compare resolved scope ordinally; canonicalize subjects through the store resolver; reject duplicate subjects and operation IDs; sort by the required two-key ordinal order; run `RecoveryDiscoveryCoordinator.Discover`/B2 admission before mutation; and construct typed adapter handles only after all validation passes.
- [ ] **Step 6: Integrate one lock and the existing barrier.** Ensure the coordinator receives an already admitted scope context or owns exactly one `FileLock.AcquireExclusive(GetLockPath(scope))`; do not let an adapter acquire a scope lock. The read barrier must be acquired before discovery and remain held until coordinator cleanup. Use existing lock timeout behavior and map `FileLockTimeoutException` without string inspection.
- [ ] **Step 7: Run focused admission/concurrency tests.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionAdmissionTests`

Expected: PASS, including shuffled-input determinism, no-side-effect rejection, same-scope serialization, and cross-scope independence.
- [ ] **Step 8: Commit the task.**

```bash
git add src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/RecoveryDiscovery.cs tests/Scrinia.Tests/MultiMemoryTransactionAdmissionTests.cs
git commit -m "feat: add deterministic multi-memory admission"
```

### Task 4: Prepare, Decision Publication, Commit, and Recovery

**Files:**
- Modify: `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs`
- Modify: `src/Scrinia.Core/MultiMemoryTransactionRecord.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Create: `tests/Scrinia.Tests/MultiMemoryTransactionLifecycleTests.cs`
- Create: `tests/Scrinia.Tests/MultiMemoryTransactionRecoveryTests.cs`

**Interfaces:**
- Consumes: Task 3 ordered adapter handles and scope lock; Task 2 `Serialize`, `ParseAndValidate`, `WithState`; B2 adapter methods for prepare/commit/verify/rollback/cleanup.
- Produces: `ExecuteAsync` and `RecoverAsync` behavior with the five aggregate classifications and typed participant outcomes; an internal test fault seam for coordinator record write interruption and each lifecycle boundary.

- [ ] **Step 1: Write the happy-path lifecycle test.** Use two fake adapters that record calls and expose deterministic state. Assert call order is `Prepare(a), Prepare(b), publish(prepared), publish(committed), Commit(a), Verify(a), Commit(b), Verify(b), invalidate, Cleanup(a/b), delete(record)`; assert one record exists only between publication and cleanup; assert result is `Committed` and both participants are committed.
- [ ] **Step 2: Write pre-decision failure tests.** Inject failure from admission, each prepare, prepared-record publication, and the first participant rollback/cleanup. Assert no committed decision is published; successful rollback marks `RolledBack`; unresolved rollback/cleanup retains the coordinator record as `recoveryRequired`; no participant is committed after the result claims `RolledBack`.
- [ ] **Step 3: Write post-decision failure tests.** Fail committed-record publication, participant commit, verification, and cleanup. Assert publication failure remains non-committed; every failure after committed is `CommittedCleanupPending` or `RecoveryRequired`, rollback is never called after the decision, evidence is retained when proof is incomplete, and a later recovery retries only commit/verification/cleanup.
- [ ] **Step 4: Write idempotent replay and ambiguity tests.** Replay a committed record with already verified participants and assert no second `CommitAsync`; remove/mismatch evidence, operation ID, subject, scope, or expected path and assert `RecoveryRequired` with a safe evidence path. Assert ambiguous participants never guess success.
- [ ] **Step 5: Write restart tests.** Place prepared and committed version-1 records on disk, create a fresh store, enter by read and write, and assert prepared records abort safely while committed records roll forward before any index/cache load. Assert malformed/incomplete/unknown/newer records remain unchanged and block only the selected topic.
- [ ] **Step 6: Run lifecycle/recovery tests to verify failure.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionLifecycleTests|FullyQualifiedName~MultiMemoryTransactionRecoveryTests`

Expected: FAIL until orchestration and recovery are implemented.
- [ ] **Step 7: Implement the lifecycle.** Persist the complete sorted manifest as `prepared`, call every B2 `PrepareAsync`, atomically replace the record with `committed` as the sole linearization point, then call commit and verification in order. Before that point, compensate through B2 rollback; after it, forbid rollback. Retain the record whenever the decision or completion cannot be proved.
- [ ] **Step 8: Implement recovery without the source request.** Parse and validate only the record and exact adapter evidence locations. For `prepared`, invoke safe abort/rollback and verify no B3 participant remains committed. For `committed`, invoke commit/verification in sorted order and then cleanup. Preserve malformed bytes and classify unsupported versions without rewriting them.
- [ ] **Step 9: Run focused lifecycle/recovery tests.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionLifecycleTests|FullyQualifiedName~MultiMemoryTransactionRecoveryTests`

Expected: PASS for all pre/post-decision failure classes, coordinator-write interruption, ambiguity, restart, and idempotent replay assertions.
- [ ] **Step 10: Commit the task.**

```bash
git add src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs src/Scrinia.Core/MultiMemoryTransactionRecord.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/MultiMemoryTransactionLifecycleTests.cs tests/Scrinia.Tests/MultiMemoryTransactionRecoveryTests.cs
git commit -m "feat: implement multi-memory commit and recovery"
```

### Task 5: Read Visibility, Cache, and Facts Invalidation

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs`
- Create: `tests/Scrinia.Tests/MultiMemoryTransactionVisibilityTests.cs`

**Interfaces:**
- Consumes: Task 4 recovery/commit result; `WithRecoveryReadBarrier`, `LoadIndexUnderRecoveryBarrier`, `InvalidateScopeCacheAfterRollback`, `_indexCache`, `InvalidateEntityFacts`, and `EntityFactMaterializer`/`GetEntityFactStore` behavior.
- Produces: one coordinator-owned `InvalidateCommittedScope(string scope)` action and read/write entry integration that resolves coordinator evidence before cache/index/facts access.

- [ ] **Step 1: Write visibility tests.** Warm an index and facts cache, execute a two-participant transaction, and assert a read cannot observe one updated participant plus one old participant. Assert cached entries remain unchanged until all final bytes verify, then one invalidation causes the next read to observe both updates.
- [ ] **Step 2: Write invalidation timing tests.** Fail participant commit or verification and assert neither the index nor facts cache is invalidated as a committed aggregate. Execute a successful commit and assert exactly the affected scope's cache/facts generation changes; another topic's warm cache remains valid. For pre-decision rollback, assert invalidation occurs only when an observable participant write happened.
- [ ] **Step 3: Write recovery/read-barrier tests.** Enter a scope with prepared or committed coordinator evidence through `ReadAsync`, `LoadIndex`, `SearchAll`, `ListScoped`, and `GetEntityFactStore`; assert recovery is completed or fails closed before data is returned. Enter another scope concurrently and assert it is not blocked by the retained record.
- [ ] **Step 4: Run visibility tests to verify failure.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionVisibilityTests`

Expected: FAIL until read paths and post-commit invalidation are coordinator-aware.
- [ ] **Step 5: Integrate the barrier at the common store boundary.** Route every persistent read/write path through the existing scope recovery barrier, ensuring coordinator discovery happens before `LoadIndexUnsafe`, cache hit, facts materialization, or mutation acceptance. Do not add a workspace-wide lock or a second cache format.
- [ ] **Step 6: Implement post-commit scope invalidation.** After every participant's final bytes and indexes verify, call `InvalidateScopeCacheAfterRollback`-style scope-local index invalidation plus the existing entity-facts invalidation once. Make the action idempotent and retryable; do not invalidate unrelated scopes and do not roll back a verified decision because cleanup failed.
- [ ] **Step 7: Run focused visibility tests.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionVisibilityTests`

Expected: PASS for no mixed snapshots, timing, facts/cache locality, and fail-closed recovery behavior.
- [ ] **Step 8: Commit the task.**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs tests/Scrinia.Tests/MultiMemoryTransactionVisibilityTests.cs
git commit -m "feat: enforce multi-memory visibility and invalidation"
```

### Task 6: MCP and CLI Aggregate Mapping

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.cs`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs` only if a typed DTO is directly marshalled
- Modify: `src/Scrinia/Commands/CliJsonContext.cs` only if a typed DTO is directly marshalled
- Create: `tests/Scrinia.Tests/MultiMemoryTransactionFacadeTests.cs`
- Create: `tests/Scrinia.Tests/CliMultiMemoryTransactionTests.cs`

**Interfaces:**
- Consumes: Task 5 aggregate result and typed failures; existing `WholeMemoryFailureResponse`, `AppendRecoveryBlockedResponse`, `RecoveryBlockedResponse`, `ResponseBuilder`, `CliMcpOutput`, `EmitMcpResult`, and the CLI's direct internal calls (`tools.Remember`, `tools.Append`, `tools.Compact`, `tools.Link`).
- Produces: no new MCP or CLI command name; one internal `MapMultiMemoryResult(MultiMemoryAggregateResult, string action)` mapping helper per surface if needed, preserving existing response fields and exit classes.

- [ ] **Step 1: Inventory the supported entry point.** Confirm from the current route/command table which already-supported operation can submit multiple participants. Do not create a new remote/HTTP surface or infer support from journal files. Record the exact internal method and its MCP and CLI call sites in the implementation PR.
- [ ] **Step 2: Write MCP mapping tests.** For each aggregate classification assert existing success, `success_with_warning`, recovery-required, blocked, and invalid-parameter fields are populated from typed result properties. Assert transaction/participant IDs and safe evidence paths survive where existing fields allow them; raw payloads, sidecar contents, foreign roots, and exception text do not appear. Assert single-memory remember/append/compact/link responses are byte/field compatible with their B2 baselines.
- [ ] **Step 3: Write CLI mapping tests.** Invoke the actual CLI command against a `TestHelpers.StoreScope`, deserialize through `CliJsonContext.Default.CliMcpOutput` where that is the existing wrapper, and assert exit `0` for committed, exit `1` for warnings/recovery, existing YAML status/error/recovery fields, and preserved operation correlation. Prove the CLI path receives the same mapping even though it bypasses `[McpServerTool]` methods.
- [ ] **Step 4: Run facade tests to verify failure.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionFacadeTests|FullyQualifiedName~CliMultiMemoryTransactionTests`

Expected: FAIL until both direct surfaces map the typed aggregate.
- [ ] **Step 5: Implement MCP mapping.** Branch on `MultiMemoryOutcome`/`MultiMemoryFailure` fields, not journal existence or `Exception.Message`. Reuse `McpResponse.WithOperationId`, `WithRecovery`, `WithReason`, and existing success/warning status conventions. Keep all public tool names and parameter schemas unchanged.
- [ ] **Step 6: Implement CLI mapping independently.** Update the internal method result handling in `ScriniaCommands.Memory.cs`; do not rely on changes to `MemoryTools.cs` facade handlers. Keep `CliMcpOutput`/existing JSON output types unless the already-supported entry point genuinely has a typed aggregate shape, and register every new directly serialized type in the appropriate source-generated context.
- [ ] **Step 7: Run focused facade and CLI tests.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransactionFacadeTests|FullyQualifiedName~CliMultiMemoryTransactionTests`

Expected: PASS with unchanged single-memory contracts and identical typed aggregate semantics on MCP and CLI.
- [ ] **Step 8: Commit the task.**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia/Commands/CliJsonContext.cs tests/Scrinia.Tests/MultiMemoryTransactionFacadeTests.cs tests/Scrinia.Tests/CliMultiMemoryTransactionTests.cs
git commit -m "feat: map multi-memory outcomes across facades"
```

### Task 7: Final Compatibility and Scope Hygiene Gate

**Files:**
- Modify only the B3 implementation files listed in Tasks 1–6.
- Extend, but do not weaken, the B3 tests listed in Tasks 1–6 and the existing B2 compatibility suites.
- Do not modify specs, historical reports, ordinary docs, NMP/2 code, sidecar codecs, plugin/event code, or B1 cleanup code.

**Interfaces:**
- Consumes: all Task 1–6 contracts and implementation; existing B2 journal fixtures and response assertions.
- Produces: green full-suite evidence and a reviewed diff proving the B3 scope boundary.

- [ ] **Step 1: Run the complete B3-focused lane.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemoryTransaction`

Expected: PASS with no skipped B3 tests.
- [ ] **Step 2: Run source-generation and trimming checks.**

Run: `dotnet build Scrinia.sln -c Release`

Expected: PASS with zero errors and no reflection serializer warnings introduced by B3.

Run: `dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained false /p:PublishTrimmed=true`

Expected: PASS; MCP/CLI startup and generated DTO metadata tests remain green.
- [ ] **Step 3: Run B2 and single-memory regression suites.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Append|FullyQualifiedName~WholeMemory|FullyQualifiedName~FileEntity|FullyQualifiedName~Recovery|FullyQualifiedName~Nmp2|FullyQualifiedName~Sidecar|FullyQualifiedName~Cli|FullyQualifiedName~ScriniaMcp`

Expected: PASS; restore, append, file-entity, whole-memory, NMP/2, ordinary sidecar, MCP, and CLI behavior is unchanged.
- [ ] **Step 4: Run the full solution suite.**

Run: `dotnet test Scrinia.sln`

Expected: PASS with zero failed or skipped tests outside explicitly opt-in performance benchmarks.
- [ ] **Step 5: Perform the diff/scope audit.**

Run: `git status --short` and `git diff --name-only HEAD~6..HEAD`

Expected: only the intended B3 source/test files appear in the B3 task commits; no `.multi-memory-transaction.json` is enumerated/exported/indexed; no participant payload journal is duplicated; no cross-scope lock/transaction exists; no public MCP/CLI name or successful payload shape changed.
- [ ] **Step 6: Perform the deferred-work/type audit.** Search the plan and implementation for deferred-work markers, guessed exception-message parsing, reflection `JsonSerializer` calls, `object` payloads, caller-order lock acquisition, rollback after committed verification, and references to excluded work. Resolve every match before approval.
- [ ] **Step 7: Commit the final compatibility gate.**

```bash
git add src tests
git commit -m "test: close multi-memory transaction compatibility gate"
```

## Final Verification Gates

- [ ] Exact version-1 coordinator JSON is tested byte/field-for-field and is serialized/deserialized exclusively through source-generated metadata.
- [ ] The record is private control data and is absent from memory listing, search, facts, sidecar enumeration, and bundle export.
- [ ] Admission rejects empty, duplicate, ephemeral, file-entity, unsafe, and cross-scope participants before mutation or coordinator-record publication.
- [ ] Participants are deterministically sorted and the coordinator owns one scope lock/barrier; no participant acquires another scope lock.
- [ ] B2 adapters remain the sole owners of payload journals, validation, rollback, commit, verification, and cleanup evidence.
- [ ] The `prepared` record is the non-decision state; `committed` is the sole linearization point; post-decision rollback is impossible.
- [ ] Prepared recovery aborts safely; committed recovery rolls forward; malformed/newer/ambiguous evidence remains retained and fails closed only for its scope.
- [ ] Readers cannot observe participant-by-participant state; cache/facts invalidation happens once after aggregate verification and remains scope-local.
- [ ] Cleanup retry is idempotent and never replays verified business mutation.
- [ ] MCP and CLI map typed outcomes independently while retaining existing names, wrappers, correlation fields, successful shapes, and exit classes.
- [ ] B2 journals, NMP/2, ordinary sidecars, plugins/events, archive policy/GC, multi-scope behavior, HTTP/remote surfaces, and B1 contamination remain untouched.
- [ ] Focused B3 tests, B2/single-memory regressions, source-generation/trimming build, and `dotnet test Scrinia.sln` pass.

## Self-Review Record

The plan covers the specification's sections in order: one-scope boundary and identity (Tasks 1 and 3), exact durable record/version policy (Task 2), deterministic lock/admission (Task 3), prepare/decision/commit and all failure classes (Task 4), read visibility/restart/ambiguity (Tasks 4 and 5), cache/facts invalidation (Task 5), MCP/CLI compatibility (Task 6), serialization/security/path safety (Tasks 2–4), and every acceptance/exclusion gate (Task 7). No deferred implementation or generic test instructions remain; all neighboring signatures are defined once above and reused consistently. The source-generation file-map entries are explicit: use the existing YAML wrapper when it remains sufficient, or register and test a directly serialized aggregate DTO when the supported command actually requires one. The plan does not include multi-scope, plugins/events, archive policy/GC, NMP/2/ordinary sidecar changes, or B1 contamination cleanup.
