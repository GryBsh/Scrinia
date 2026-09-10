# Whole-Memory Transactions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the B0 transaction discipline to one persistent memory write, compact, skill/agent file mutations, and arbitrary sidecar mutations while preserving artifact/sidecar pairing, archive behavior, public result contracts, and ephemeral semantics.

**Architecture:** Build on the B0 transaction vocabulary and the existing `FileMemoryStore` per-scope lock. A transaction captures raw before-images, staged after-images, and an archive pair before publication; it publishes the artifact and sidecar under one scope/file-entity lock, verifies the pair, and either returns a committed result, rolls back byte-for-byte, or retains recovery evidence. A shared internal file-entity coordinator applies the same protocol to skill and agent markdown plus `.meta.json`, while facade methods remain the only places that derive user-facing content and response text.

**Tech Stack:** C# on `net10.0`, .NET file I/O and `FileLock`, `ReaderWriterLockSlim`, `System.Text.Json` source generation, xUnit, FluentAssertions, `dotnet test`, and the existing local stdio MCP/CLI surfaces.

**Spec:** `docs/superpowers/specs/2026-09-10-phase-b-roadmap-design.md`, B0 dependency contract and B1 boundary in Sections 3-7 and acceptance gates in Section 8. This plan intentionally narrows B1 to the requested whole-memory and sidecar slice; bundle/import atomicity, one-memory migration, append/restore journal convergence, cross-memory/multi-scope transactions, external plugin/event transactions, general archive/pruning, background recovery reporting, and NMP/2 or sidecar schema changes are excluded.

## Global Constraints

- Preserve `nmp/2`, current encoding/decoding, headers, chunk semantics, and artifact readability; do not add transaction metadata to NMP/2.
- Preserve current sidecar fields, legacy `name`/`uri` handling, forward-fill and `fields` to `attributes` migration, skill/agent lookup, and malformed-sidecar safety.
- Preserve existing version directories, archive names, collision rules, artifact/sidecar pairing, and retention semantics; an archive is never published without its companion.
- Keep `~name` and `/temp/name` in-process and outside persistent archive/journal guarantees; the persistent-only CLI continues to reject ephemeral addresses.
- Keep the Core result contract authoritative; MCP and CLI translate it and do not independently sequence durable writes.
- Use `ReadSidecarMeta`/`WriteSidecarMeta` for skill/agent metadata and source-generated JSON contexts for every newly serialized type.
- Keep `scri serve` over local stdio as the only server surface; do not add HTTP, REST, remote CLI, API keys, web, or Aspire behavior.
- Do not invoke Git or inspect a Git index.
- Do not expose new public MCP tools through `WithTools<T>()`; transaction helpers remain `internal` or `private`.
- A transaction covers exactly one persistent memory scope or one file entity. It never acquires two scope locks and never claims multi-memory atomicity.
- A failed pre-commit operation must leave both live members byte-identical; a post-commit cleanup failure returns success-with-warning and retains inspectable evidence without undoing the committed pair.
- Cancellation before the commit point rolls back with `CancellationToken.None`; cancellation after commit cannot report an uncommitted failure.

---

## Existing and Proposed Contracts

The implementer must start from these existing signatures and preserve them:

```csharp
// src/Scrinia.Core/IMemoryStore.cs
Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default);
Task<AppendStoreResult> AppendAsync(MemoryWrite write, CancellationToken ct = default);
ValueTask<ArtifactEntry> WriteAsync(MemoryAddress address, MemoryWrite write,
    CancellationToken ct = default);
void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
    Dictionary<string, byte[]> artifactBytes, bool overwrite);
```

The B0 implementation must provide the following internal extension points before B1 code is merged. B1 must consume these names and meanings rather than create a second journal model:

```csharp
// src/Scrinia.Core/WholeMemoryTransaction.cs
internal sealed record WholeMemoryTransaction(
    string OperationId,
    string Scope,
    string Target,
    bool IsEphemeral,
    RawMemberSnapshot OldArtifact,
    RawMemberSnapshot OldSidecar,
    RawMemberSnapshot NewArtifact,
    RawMemberSnapshot NewSidecar,
    ArchivePairPlan Archive,
    DateTimeOffset StartedAt);

internal sealed record ArchivePairPlan(
    bool Required,
    string? ArtifactPath,
    string? SidecarPath,
    IReadOnlyList<string> PruneCandidates);

internal enum WholeMemoryOutcome { Created, Replaced, SuccessWithWarning, RecoveryRequired }

internal sealed record WholeMemoryResult(
    WholeMemoryOutcome Outcome,
    ArtifactEntry? Entry,
    string OperationId,
    string? Warning,
    RecoveryInfo? Recovery);

internal sealed class WholeMemoryTransactionException : IOException
{
    internal WholeMemoryTransactionException(
        WholeMemoryErrorCode code,
        string operationId,
        string message,
        Exception? inner = null,
        RecoveryInfo? recovery = null);

    internal WholeMemoryErrorCode Code { get; }
    internal string OperationId { get; }
    internal RecoveryInfo? Recovery { get; }
}

internal enum WholeMemoryErrorCode { RecoveryRequired, Internal }

internal enum WholeMemoryFaultPoint
{
    RawArtifactRead, RawSidecarRead, EvidenceWrite, ArchiveArtifactWrite,
    ArchiveSidecarWrite, LiveArtifactWrite, LiveSidecarWrite, VerificationRead,
    RollbackWrite, EvidenceCleanup
}

internal sealed record RecoveryInfo(
    string EvidencePath,
    string State,
    string Action,
    string Detail);

internal interface IWholeMemoryTransactionCoordinator
{
    Task<WholeMemoryResult> ExecuteAsync(
        WholeMemoryTransaction transaction,
        Func<CancellationToken, Task> stageAndPublish,
        CancellationToken cancellationToken = default);
}

// src/Scrinia.Core/FileMemoryStore.cs test-only seam used by B1 fault tests.
internal partial class FileMemoryStore
{
    internal void FailOnceAt(WholeMemoryFaultPoint point);
}

// File-entity mutation is one entity, with the body and optional metadata as a pair.
internal sealed record FileEntityMutation(
    string EntityKind,
    string Name,
    string BodyPath,
    string MetaPath,
    string VersionsDirectory,
    byte[] NewBody,
    byte[]? NewMeta,
    bool DeleteBody,
    bool DeleteMeta);

internal interface IFileEntityTransactionCoordinator
{
    Task<FileEntityMutationResult> ExecuteAsync(
        FileEntityMutation mutation,
        CancellationToken cancellationToken = default);
}

internal sealed class FileEntityTransactionCoordinator : IFileEntityTransactionCoordinator
{
    internal FileEntityTransactionCoordinator(string workspaceRoot);
    internal void FailOnceAt(FileEntityFaultPoint point);
}

internal enum FileEntityFaultPoint
{
    RawBodyRead, RawMetaRead, ArchiveBodyWrite, ArchiveMetaWrite,
    BodyWrite, MetaWrite, VerificationRead, AfterBodyDelete,
    RollbackWrite, EvidenceCleanup
}

internal sealed record FileEntityMutationResult(
    WholeMemoryOutcome Outcome,
    string OperationId,
    string? Warning,
    RecoveryInfo? Recovery);
```

`IWholeMemoryTransactionCoordinator` and `IFileEntityTransactionCoordinator` are internal seams. The durable evidence format, state names, and JSON registration come from B0; B1 may add operation-specific payload fields only through B0’s stable extension point and must not alter NMP/2 or the existing skill/agent sidecar schema.

## Explicit File Map

**Create:**

- `src/Scrinia.Core/WholeMemoryTransaction.cs` - B1 transaction records, outcomes, recovery exception, and coordinator contracts if B0 places them here.
- `src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs` - persistent artifact/sidecar capture, archive-pair publication, verification, rollback, and per-scope lock integration.
- `src/Scrinia.Core/FileEntityTransactionCoordinator.cs` - one file entity’s body/metadata transaction, archive pairing, delete rollback, and operation-specific fault seams.
- `tests/Scrinia.Tests/WholeMemoryTransactionTests.cs` - record validation, result state transitions, operation IDs, raw-byte evidence, and JSON fixtures.
- `tests/Scrinia.Tests/WholeMemoryStoreTransactionTests.cs` - `StoreAsync` fault, cancellation, restart/recovery, lock, archive, and pairing tests.
- `tests/Scrinia.Tests/FileEntityTransactionTests.cs` - skill/agent/arbitrary-sidecar body/meta transaction tests.
- `tests/Scrinia.Tests/McpWholeMemoryTransactionTests.cs` - MCP result mapping and no-duplicate-write tests.
- `tests/Scrinia.Tests/CliWholeMemoryTransactionTests.cs` - CLI delegation, exit status, and JSON/text contract tests.

**Modify:**

- `src/Scrinia.Core/FileMemoryStore.cs:741-783, 1867-2081, 3179-3220, 3661-3717` - route persistent `StoreAsync` through the coordinator, preserve ephemeral fast path, and move archive/pair operations behind the shared seam.
- `src/Scrinia.Core/AppendContracts.cs` - only if B0’s common recovery/result types require a typed adapter; retain append-specific compatibility aliases and behavior.
- `src/Scrinia.Core/AssemblyInfo.cs` - no new friend assembly; retain `Scrinia.Mcp` and test access if the coordinator remains internal.
- `src/Scrinia.Mcp/MemoryTools.Core.cs:164-227, 288-301` - route `/agent/*` through the file-entity coordinator and map whole-memory results without sequencing body and sidecar writes in MCP.
- `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:71-286` - keep compact’s derivation/candidate preparation but call the transactional store write and map warnings/recovery.
- `src/Scrinia.Mcp/MemoryTools.Skills.cs:94-222, 225-422` - make `ArchiveFileVersion`, `WriteSidecarMeta`, `SkillCreate`, and `SkillDelete` use one file-entity transaction; retain source-generated metadata helpers.
- `src/Scrinia.Mcp/MemoryTools.cs:197-320, 590-618` - preserve existing public tool signatures and add typed catches for whole-memory recovery/write outcomes.
- `src/Scrinia.Mcp/McpResponse.cs:10-51, 57-220` - add only stable operation/recovery fields required by the B0 contract; keep `DURABLE_WRITE_FAILED`, `RECOVERY_REQUIRED`, and `RECOVERY_BLOCKED` meanings stable.
- `src/Scrinia.Mcp/MemoryTools.Skills.cs:16-54` - register every new result/recovery type used in MCP serialization in `ScriniaMcpJsonContext`.
- `src/Scrinia/Commands/ScriniaCommands.Memory.cs:411-455, 619-641` - continue calling internal `Remember`/`Compact`; map warning/recovery result status consistently in text and JSON.
- `src/Scrinia/Commands/CliJsonContext.cs` - register any new CLI result DTO, without reflection serialization.
- `src/Scrinia.Core/FileMemoryStore.cs` source-generated `FileStoreJsonContext` - register any durable transaction DTO only if FileMemoryStore serializes it directly; otherwise keep journal types in B0’s context.

**Do not modify:** bundle/import code, restore journal code, append journal convergence, migration code, plugin/event contracts, NMP/2 encoders, archive-pruning service, shared-base code, or unrelated living/historical documentation.

## Task 1: Lock the B0 Adapter and Transaction Result Contract

**Files:**
- Create: `src/Scrinia.Core/WholeMemoryTransaction.cs`
- Create: `tests/Scrinia.Tests/WholeMemoryTransactionTests.cs`
- Modify: the B0 transaction contract file identified by the accepted B0 plan, only when its final location differs from `WholeMemoryTransaction.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs` and `src/Scrinia.Core/FileMemoryStore.cs` source-generated contexts as required by the final serialized types

**Interfaces:**
- Consumes: B0’s journal state/evidence codec and `RawMemberSnapshot`.
- Produces: `WholeMemoryTransaction`, `ArchivePairPlan`, `WholeMemoryResult`, `WholeMemoryTransactionException`, `IWholeMemoryTransactionCoordinator`, and `IFileEntityTransactionCoordinator` with the exact signatures above.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void TransactionSnapshot_DefensivelyCopiesBytesAndRejectsHashMismatch()
{
    byte[] bytes = [1, 2, 3];
    var snapshot = RawMemberSnapshot.Capture("body", true, bytes);
    bytes[0] = 9;
    snapshot.Bytes.Should().Equal([1, 2, 3]);
    FluentActions.Invoking(() => new RawMemberSnapshot("body", true, [1], snapshot.Sha256))
        .Should().Throw<ArgumentException>();
}

[Fact]
public void WholeMemoryResult_RequiresOperationIdAndRecoveryEvidenceForRecovery()
{
    var result = new WholeMemoryResult(
        WholeMemoryOutcome.RecoveryRequired, null, "op-1", null,
        new RecoveryInfo("scope/.journal", "recoveryRequired", "upgrade or repair", "safe detail"));
    result.OperationId.Should().Be("op-1");
    result.Recovery!.EvidencePath.Should().Be("scope/.journal");
}

[Fact]
public void FileEntityMutation_CarriesBothMembersAndExplicitDeleteIntent()
{
    var mutation = new FileEntityMutation(
        "skill", "qa", "qa.md", "qa.meta.json", "versions", "new"u8.ToArray(), null, true, true);
    mutation.DeleteBody.Should().BeTrue();
    mutation.DeleteMeta.Should().BeTrue();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WholeMemoryTransactionTests"`

Expected: FAIL because the B1 records/contracts and their validation do not yet exist.

- [ ] **Step 3: Write the minimal implementation**

Implement immutable records with null/empty argument guards, defensive byte-array copies through `RawMemberSnapshot`, enum values `Created`, `Replaced`, `SuccessWithWarning`, and `RecoveryRequired`, and the exact exception/result signatures above. Implement the listed fault-point enums and one-shot fault seams as internal test-only members. Do not add a new JSON file or alter any existing sidecar property.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WholeMemoryTransactionTests"`

Expected: PASS with no warnings attributable to the new types.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/WholeMemoryTransaction.cs tests/Scrinia.Tests/WholeMemoryTransactionTests.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia.Core/FileMemoryStore.cs
git commit -m "feat: define whole-memory transaction contract"
```

## Task 2: Implement Persistent Whole-Memory Publication and Recovery

**Files:**
- Create: `src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs`
- Create: `tests/Scrinia.Tests/WholeMemoryStoreTransactionTests.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:741-783, 1867-2081, 3179-3220, 3661-3717`
- Modify: `src/Scrinia.Core/AppendContracts.cs` only for a typed compatibility adapter, not for append behavior

**Interfaces:**
- Consumes: Task 1 records and B0 journal codec; existing `WithExclusiveLock`, `FileLock`, `DurableFile`, `BuildDerivedEntry`, `Upsert`, `WriteArtifactAsync`, `ArchiveVersion`, and `ReadArtifactAsync`.
- Produces: `FileMemoryStore.StoreAsync` remains `Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default)`; internal coordinator entry point is `Task<WholeMemoryResult> ExecuteAsync(WholeMemoryTransaction transaction, Func<CancellationToken, Task> stageAndPublish, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write the failing tests**

Add tests that seed a memory, capture exact `.nmp2` and `.meta.json` bytes, inject failure at each B1 boundary (`before capture`, archive artifact, archive sidecar, live artifact, live sidecar, verification, rollback, evidence cleanup), and assert one of: both live members remain byte-identical; a typed recovery exception carries the same operation ID and retained evidence; or the new pair is fully committed. Add tests for archive body/meta pairing, collision-safe archive names, cancellation before commit, cancellation after commit, a second `FileMemoryStore` blocked by the same scope lock, and a fresh store recovering retained evidence without touching another scope.

```csharp
[Fact]
public async Task StoreAsync_LiveSidecarFailureRestoresBothMembersByteForByte()
{
    using var workspace = new TestHelpers.StoreScope();
    await workspace.Store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
    byte[] oldBody = File.ReadAllBytes(workspace.Store.ArtifactPath("target"));
    byte[] oldMeta = File.ReadAllBytes(workspace.Store.MetaPath("target"));
    workspace.Store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

    await FluentActions.Invoking(() => workspace.Store.StoreAsync(
        MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"])))
        .Should().ThrowAsync<WholeMemoryTransactionException>();

    File.ReadAllBytes(workspace.Store.ArtifactPath("target")).Should().Equal(oldBody);
    File.ReadAllBytes(workspace.Store.MetaPath("target")).Should().Equal(oldMeta);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WholeMemoryStoreTransactionTests"`

Expected: FAIL because normal `StoreAsync` archives and rewrites without a durable paired transaction and the fault seam/result type is absent.

- [ ] **Step 3: Write the minimal implementation**

Refactor only the persistent branch of `StoreAsyncCore`: derive and validate before mutation; resolve the canonical existing entry under `WithExclusiveLock`; capture raw old artifact and sidecar bytes; freeze an archive pair destination using existing timestamp/collision conventions; write transaction evidence before destructive publication; publish archive artifact and sidecar as a pair; publish live artifact then normalized sidecar under the same lock; verify both hashes; mark commit; and perform cleanup after commit. On pre-commit failure restore exact old bytes or delete both newly-created members, retain evidence when rollback cannot be proved, and throw `WholeMemoryTransactionException` with `RecoveryRequired`. Keep the current ephemeral branch as an in-memory dictionary assignment with no journal, archive, file lock, or persistent recovery evidence. Preserve `CreatedAt`, `UpdatedAt`, derivation, cache invalidation, case-insensitive canonical names, read-only sidecar refusal, and `ArtifactEntry` index publication.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WholeMemoryStoreTransactionTests|FullyQualifiedName~Append"`

Expected: PASS for all new whole-memory durability tests and all existing append tests; append behavior remains unchanged except for consuming shared private file primitives.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/WholeMemoryTransactionCoordinator.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/AppendContracts.cs tests/Scrinia.Tests/WholeMemoryStoreTransactionTests.cs
git commit -m "feat: transact persistent whole-memory writes"
```

## Task 3: Make Compact Use the Whole-Memory Transaction Boundary

**Files:**
- Create: additional tests in `tests/Scrinia.Tests/WholeMemoryStoreTransactionTests.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:71-286`
- Modify: `src/Scrinia.Mcp/MemoryTools.cs:590-618`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:619-641`

**Interfaces:**
- Consumes: Task 2 `StoreAsync`, `WholeMemoryResult`, existing `MemoryDerivation.CuratedKeywords`, and the compact preparation logic.
- Produces: existing `internal Task<McpResponse> Compact(MemoryAddress name, int keepRecent = 0, CancellationToken cancellationToken = default)` and existing `scri compact` command signature; no new MCP/CLI verb.

- [ ] **Step 1: Write the failing tests**

Pin that compact failure after archive/body/meta publication leaves the prior pair consistent or returns structured recovery, that successful compact archives both prior members exactly once, that `keepRecent` and curated metadata remain unchanged, that ephemeral compact has no journal/archive, and that compact no-op does not create a transaction. Add a test that a fault during the live sidecar write cannot produce a success response or an artifact whose sidecar describes a different chunk count.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Compact"`

Expected: FAIL at the injected publication failure because the current compact path delegates to the old non-transactional `StoreAsync` behavior and does not map the new result.

- [ ] **Step 3: Write the minimal implementation**

Leave chunk decoding, `keepRecent` validation, metadata carry-forward, curated-keyword fallback, no-op messages, and event invocation in their current order. Replace only the durable write call with Task 2’s transactional `StoreAsync`; do not call `ArchiveVersion` separately. Map `Created`/`Replaced` to the existing success text, `SuccessWithWarning` to `ResponseBuilder.SuccessWithWarning(...).WithAction("compacted")`, and `RecoveryRequired` to `ErrorCodes.RecoveryRequired` with `WithOperationId` and `WithRecovery`. Do not fire the event sink after a result that did not commit.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Compact"`

Expected: PASS with existing compact response text preserved except for explicit warning/recovery fields on failure paths.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Lifecycle.cs src/Scrinia.Mcp/MemoryTools.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs tests/Scrinia.Tests/WholeMemoryStoreTransactionTests.cs
git commit -m "feat: transact compact writes"
```

## Task 4: Add the Shared File-Entity Transaction Coordinator

**Files:**
- Create: `src/Scrinia.Core/FileEntityTransactionCoordinator.cs`
- Create: `tests/Scrinia.Tests/FileEntityTransactionTests.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs:94-222`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:164-227`

**Interfaces:**
- Consumes: Task 1 `FileEntityMutation`, `FileEntityMutationResult`, B0 evidence codec, `FileLock`, `DurableFile`, `PathContainment`, `ReadSidecarMetaOrRefuse`, and source-generated `JsonTypeInfo<T>`.
- Produces: `Task<FileEntityMutationResult> ExecuteAsync(FileEntityMutation mutation, CancellationToken cancellationToken = default)`; `SkillCreate`, agent remember, and `SkillDelete` retain their existing internal signatures and response shapes.

- [ ] **Step 1: Write the failing tests**

Cover skill and agent create/rewrite/delete, sidecar-only update, body-only update with an existing sidecar, both-member delete, missing sidecar, missing body, malformed sidecar refusal behavior, newer sidecar read-only refusal before archive, archive body/meta pairing, injected failures after each member publication, cancellation before commit, retained recovery evidence, scope lock blocking, and path containment. Assert no operation can report success while leaving a live body and metadata from different versions.

```csharp
[Fact]
public async Task DeleteEntity_FailureAfterBodyDeleteRestoresBodyAndSidecar()
{
    using var scope = new TestHelpers.StoreScope();
    string bodyPath = Path.Combine(scope.TempDir, "qa.md");
    string metaPath = Path.Combine(scope.TempDir, "qa.meta.json");
    string versions = Path.Combine(scope.TempDir, "versions");
    File.WriteAllText(bodyPath, "old body");
    File.WriteAllText(metaPath, "{\"role\":\"reviewer\"}");
    byte[] body = File.ReadAllBytes(bodyPath);
    byte[] meta = File.ReadAllBytes(metaPath);
    var coordinator = new FileEntityTransactionCoordinator(scope.WorkspaceDir);
    coordinator.FailOnceAt(FileEntityFaultPoint.AfterBodyDelete);

    await FluentActions.Invoking(() => coordinator.ExecuteAsync(new FileEntityMutation(
            "skill", "qa", bodyPath, metaPath, versions, [], null, true, true)))
        .Should().ThrowAsync<WholeMemoryTransactionException>();

    File.ReadAllBytes(bodyPath).Should().Equal(body);
    File.ReadAllBytes(metaPath).Should().Equal(meta);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FileEntityTransactionTests"`

Expected: FAIL because skill/agent helpers currently archive, replace, and delete members independently and have no paired rollback.

- [ ] **Step 3: Write the minimal implementation**

Implement one entity transaction under the entity directory’s `.lock`, using the existing `versions` directory and archive naming rules. Capture raw body and metadata before-images; refuse unsupported sidecar versions before the first side effect; stage new bytes; archive body and metadata as a pair when replacing; publish body and metadata as a pair; verify both; and on failure restore or delete both members exactly. For deletion, archive the pair before deleting and restore both on a pre-commit failure. Treat a missing optional sidecar as a non-existent member, never synthesize one during delete, and retain an existing orphan sidecar when creating a new body unless the operation explicitly replaces that sidecar. Return warning only for post-commit archive cleanup/evidence cleanup failures. Never use `JsonSerializer` without the caller-provided `JsonTypeInfo<T>`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FileEntityTransactionTests|FullyQualifiedName~AgentFileEntityTests|FullyQualifiedName~SkillPath"`

Expected: PASS, including existing skill/agent lookup, built-in shadowing, metadata preservation, and malformed-sidecar tests.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/FileEntityTransactionCoordinator.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/FileEntityTransactionTests.cs
git commit -m "feat: transact skill and agent file entities"
```

## Task 5: Route Arbitrary Sidecar Mutations Through the Coordinator

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs:110-222`
- Modify: `src/Scrinia.Core/FileEntityTransactionCoordinator.cs`
- Modify: `tests/Scrinia.Tests/FileEntityTransactionTests.cs`

**Interfaces:**
- Consumes: Task 4 `IFileEntityTransactionCoordinator.ExecuteAsync(FileEntityMutation, CancellationToken)`.
- Produces: `ReadSidecarMeta<T>`, `ReadSidecarMetaOrRefuse<T>`, and `WriteSidecarMeta<T>` continue to accept the existing `JsonTypeInfo<T>` signatures; all arbitrary sidecar writes use `FileEntityMutation.NewMeta` and do not add a second writer.

- [ ] **Step 1: Write the failing tests**

Add a test using a sidecar type already supported by the current helpers and a test for an unknown-field-preserving rewrite. Inject a failure between body and metadata publication and assert the old raw sidecar survives. Add a malformed sidecar read test proving reads remain null/best-effort while a mutation that would overwrite it refuses or recovers before changing the body, matching the existing helper contract.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FileEntityTransactionTests"`

Expected: FAIL because `WriteSidecarMeta` still writes a single file after an already-published body and has no transaction context.

- [ ] **Step 3: Write the minimal implementation**

Split helper responsibilities into pure serialization/preflight and coordinator publication: `ReadSidecarMeta*` remains read-only; `WriteSidecarMeta<T>` serializes with the supplied `JsonTypeInfo<T>` and delegates the pair mutation when called from a body mutation; direct sidecar-only callers use `BodyPath == MetaPath` semantics that capture and replace only the metadata member under the same entity lock. Preserve `ContractAndVersion`, unknown-field copying, `FormatVersion` refusal, UTF-8/no-BOM behavior, and legacy migration. Do not change the serialized JSON fields or add a transaction field to `.meta.json`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FileEntityTransactionTests|FullyQualifiedName~Formats"`

Expected: PASS with exact existing sidecar JSON compatibility and no reflection warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/FileEntityTransactionCoordinator.cs src/Scrinia.Mcp/MemoryTools.Skills.cs tests/Scrinia.Tests/FileEntityTransactionTests.cs
git commit -m "feat: protect arbitrary sidecar mutations"
```

## Task 6: Convert SkillCreate, Agent Remember, and SkillDelete

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs:225-422`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:164-227`
- Modify: `tests/Scrinia.Tests/FileEntityTransactionTests.cs`
- Modify: `tests/Scrinia.Tests/Hooks/SkillWriterTests.cs` only when existing assertions need the new typed warning/recovery path; do not weaken compatibility assertions

**Interfaces:**
- Consumes: Task 5 coordinator and existing `SkillFileMeta`/`AgentFileMeta` constructors.
- Produces: unchanged `internal static Task<McpResponse> SkillCreate(...)`, `internal static Task<McpResponse> SkillDelete(...)`, and `internal Task<McpResponse> Remember(...)` signatures.

- [ ] **Step 1: Write the failing tests**

Test that skill and agent rewrites preserve `CreatedAt`, `SchemaVersion`, and `Unknown`; body/meta archives are both present and paired; a newer sidecar refuses before creating `versions`; delete archives both members and restores both on failure; a built-in-only skill remains non-deletable; a project override reveals the built-in after successful delete; and an injected write failure returns a structured durable/recovery response without claiming the operation completed.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Skill|FullyQualifiedName~AgentFileEntity|FullyQualifiedName~Hooks.SkillWriter"`

Expected: FAIL at the new fault and pairing assertions against the current independent file operations.

- [ ] **Step 3: Write the minimal implementation**

Build prompt/content and metadata exactly as today, including scaffold selection, built-in hash, capabilities, carried-forward schema/version/unknown fields, and cancellation preflight. Pass serialized body/meta bytes to the coordinator. For delete, pass explicit delete flags and retain current built-in/not-found validation. Catch `WholeMemoryTransactionException` at this internal boundary only to return `DURABLE_WRITE_FAILED` for safe rollback failures and `RECOVERY_REQUIRED` with operation ID/evidence for retained recovery; do not catch and convert validation, read-only, or not-found outcomes into success.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Skill|FullyQualifiedName~AgentFileEntity|FullyQualifiedName~Hooks.SkillWriter"`

Expected: PASS with existing user-facing success/error text retained and new failures carrying structured fields.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/FileEntityTransactionTests.cs tests/Scrinia.Tests/Hooks/SkillWriterTests.cs
git commit -m "feat: route skill and agent mutations through transactions"
```

## Task 7: Stabilize MCP and CLI Result Mapping

**Files:**
- Create: `tests/Scrinia.Tests/McpWholeMemoryTransactionTests.cs`
- Create: `tests/Scrinia.Tests/CliWholeMemoryTransactionTests.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.cs:197-320, 590-618`
- Modify: `src/Scrinia.Mcp/McpResponse.cs:10-51, 57-220`
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs:16-54`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:411-455, 619-641`
- Modify: `src/Scrinia/Commands/CliJsonContext.cs`

**Interfaces:**
- Consumes: Task 2/3/6 Core outcomes and existing `McpResponse` fluent methods.
- Produces: unchanged public MCP tool signatures and CLI command signatures; stable response fields are `status`, `errorCode`, `operationId`, `scope`, `journalPath`, `actionNeeded`, and `info`.

- [ ] **Step 1: Write the failing tests**

Drive internal MCP handlers and CLI commands, not only rendered YAML. Assert committed writes return `status: success`; post-commit cleanup returns `success_with_warning` and CLI exit 1 as existing warning policy dictates; rollback-safe failure returns `DURABLE_WRITE_FAILED`; retained evidence returns `RECOVERY_REQUIRED`, operation ID, scope/evidence path, and no memory bytes; no event sink fires when commit did not occur; and CLI calls the same internal handler rather than a second write implementation. Assert JSON output deserializes through `CliJsonContext` and MCP serializer options under trimming-compatible source generation.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~McpWholeMemoryTransactionTests|FullyQualifiedName~CliWholeMemoryTransactionTests"`

Expected: FAIL because whole-memory exceptions are not yet mapped consistently and new result/recovery types are not registered in all source-generated contexts.

- [ ] **Step 3: Write the minimal implementation**

Centralize the catch-to-response mapping beside `DurableWriteFailureResponse` and `RecoveryBlockedResponse`; include operation ID only when present, never include raw content or absolute internal temp paths, and use `WithRecovery` for retained evidence. Keep CLI delegation through `ScriniaMcpTools.Remember`/`Compact`; update only status/exit mapping. Register every serialized record with `ScriniaMcpJsonContext` or `CliJsonContext` and use the generated `JsonTypeInfo<T>` at every serialization call.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~McpWholeMemoryTransactionTests|FullyQualifiedName~CliWholeMemoryTransactionTests|FullyQualifiedName~McpToolSerializerOptionsTests"`

Expected: PASS, including source-generation and public-surface contract assertions.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.cs src/Scrinia.Mcp/McpResponse.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia/Commands/CliJsonContext.cs tests/Scrinia.Tests/McpWholeMemoryTransactionTests.cs tests/Scrinia.Tests/CliWholeMemoryTransactionTests.cs
git commit -m "feat: map whole-memory transaction outcomes"
```

## Dependencies and Interfaces Between Tasks

- Task 1 depends on accepted B0 vocabulary and produces the only B1 transaction/result contracts. Tasks 2 and 4 cannot start implementation until Task 1’s signatures and serialization ownership are accepted.
- Task 2 owns persistent memory commit/rollback and is the only task allowed to change `FileMemoryStore.StoreAsync` publication. Task 3 consumes it; compact must not create its own archive or transaction.
- Task 4 owns file-entity publication. Task 5 extends the same coordinator for sidecar-only and arbitrary body/meta mutations; Task 6 consumes it for skill/agent behavior.
- Task 7 consumes all previous result contracts and owns facade translation. It must not add a second transaction or bypass the Core/coordinator boundary.
- No task acquires more than one persistent scope/entity lock. A skill or agent entity is locked by its own entity directory; a memory is locked by its resolved memory scope. No operation in this plan touches two memories.
- Existing append and restore journals remain distinguishable and behaviorally unchanged. B1’s coordinator may call B0 shared primitives but must not migrate either journal.

## Compatibility, Error, and Recovery Behavior

- Existing memories remain readable before and after the change. Existing NMP/2 bytes are decoded and re-encoded only when the operation semantics already require a rewrite; rollback restores captured bytes rather than reconstructing content.
- Existing sidecars are read through current forward-fill/migration helpers. Unknown fields are copied into newly created `SkillFileMeta`/`AgentFileMeta`; no new JSON member is introduced.
- A newer sidecar version is refused before archive, staging, body replacement, or deletion. The refusal remains a durable-write/read-only error, not recovery evidence.
- Invalid input, invalid names, missing targets, no-op compact, and built-in-only skill deletion remain ordinary `INVALID_PARAMETER`, `NOT_FOUND`, or warning results and create no transaction evidence.
- A pre-commit I/O or cancellation failure attempts exact rollback with cancellation suppressed. If both live members are proven restored, return/throw `DURABLE_WRITE_FAILED` with the operation ID where the surface supports it. If restoration or evidence cleanup cannot be proven, retain the journal/evidence and return/throw `RECOVERY_REQUIRED` with operation ID, safe scope/evidence path, state, and operator action.
- After both live members and the index are verified, the commit is final. Archive cleanup, old-version pruning, journal deletion, and event delivery are post-commit concerns; failure yields `success_with_warning`, never rollback and never a false error.
- Recovery evidence contains hashes, paths, state, and safe diagnostics only. It must not contain memory content in MCP/CLI responses.
- A fresh `FileMemoryStore` must discover and recover B1 evidence at startup or first access for that scope. A damaged/unsupported evidence record blocks only its scope; another scope remains writable.
- In-process ephemeral memories have no files, archive, journal, recovery evidence, or cross-process lock. Their existing compare/swap and dictionary semantics remain intact.

## CLI/MCP Behavior and Result Contracts

- MCP public methods remain `MemoryRemember(string? path, string[]? content, Dictionary<string,string[]>? attributes = null, string[]? keywords = null, CancellationToken cancellationToken = default)`, `MemoryCompact(string? path, int keepRecent = 0, CancellationToken cancellationToken = default)`, and existing skill routing through `remember`/`forget`.
- CLI `store`, `compact`, and skill/agent paths continue to delegate to the internal MCP handlers. No CLI command writes transaction files directly and no MCP facade calls a second Core write after the store returns.
- Committed memory/skill/agent writes preserve existing success prose, `action`, `path`/`persistedPath`, and `fileChanges` behavior. Ephemeral success continues to omit file changes and archive claims.
- Safe rollback failure maps to `DURABLE_WRITE_FAILED`; retained evidence maps to `RECOVERY_REQUIRED` with `operationId`, `scope`, `journalPath`/evidence path, and `actionNeeded`. The existing `RECOVERY_BLOCKED` code remains reserved for restore-journal blocking and is not repurposed.
- CLI text renders the same response envelope as today. CLI JSON uses existing DTOs plus source-generated metadata; warning results preserve the established nonzero warning exit behavior, while committed success remains exit 0.
- Public descriptions must state that a committed write is paired and that recovery-required results retain evidence; they must not promise bundle/import, cross-memory, plugin, or external-system atomicity.

## Archive, Sidecar Pairing, and Ephemeral Rules

- Replacement archives use the existing `versions` directory, millisecond timestamp, collision suffix, and ten-version retention conventions. The artifact archive and `.meta.json` archive share the same base name and are committed as one archive pair.
- A missing sidecar is represented as a missing member; the coordinator never archives a fabricated empty sidecar and never reports a complete archive pair when only the body exists.
- A sidecar-only mutation archives the sidecar only when the operation is explicitly sidecar-only; a body rewrite archives both members so restoring an old body cannot leave newer metadata attached.
- Delete archives the current pair before deleting live members and restores both on pre-commit failure. Successful deletion removes both live members and leaves the archived pair.
- Ephemeral compact and remember never enter archive or transaction code. Persistent-only CLI rejection remains unchanged.

## Concurrency and Locking Boundaries

- Persistent memory writes acquire `FileMemoryStore.WithExclusiveLock(scope, ...)`, which combines the cross-process `.lock` and the instance’s `ReaderWriterLockSlim`; recovery runs inside that same exclusive boundary before new publication.
- File entities acquire their entity-directory `.lock` and an in-process lock keyed by the canonical entity directory. The lock covers capture, archive, publication, verification, rollback, and evidence cleanup.
- Reads retain the existing recovery barrier and may observe either the old pair or the fully committed new pair, never an artifact/sidecar mismatch. No read lock is held across a second scope acquisition.
- Operation IDs are generated at the Core transaction admission boundary and remain stable through fault, rollback, recovery, and facade mapping.
- Lock timeout is a typed durable-write failure with no partial success. The plan does not introduce lock ordering because no transaction spans multiple scopes/entities.

## Full Verification Commands

Run from the repository root after the final task and record exit code, elapsed time, pass/skipped/fail/total counts, and no-hang evidence:

```bash
dotnet format Scrinia.sln --verify-no-changes
git diff --check HEAD~7..HEAD
dotnet build Scrinia.sln --no-restore
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-build
dotnet test Scrinia.sln --no-build
dotnet test Scrinia.sln --no-build --filter "Category=LongRunningBenchmark" --blame-hang --blame-hang-timeout 15m
dotnet publish src/Scrinia/Scrinia.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true
git status --short
git diff --stat HEAD~7..HEAD
```

Focused verification before the full suite:

```bash
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WholeMemoryTransactionTests|FullyQualifiedName~WholeMemoryStoreTransactionTests|FullyQualifiedName~FileEntityTransactionTests|FullyQualifiedName~McpWholeMemoryTransactionTests|FullyQualifiedName~CliWholeMemoryTransactionTests"
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append|FullyQualifiedName~TransactionalRestore|FullyQualifiedName~AgentFileEntity|FullyQualifiedName~SkillPath|FullyQualifiedName~Formats"
```

The final implementation commit must contain only the scoped source/test/context changes listed above. Bundle/import, migration, append/restore convergence, cross-memory, plugin/event, pruning-service, shared-base, format, generated-evidence, and historical-document diffs fail the scope gate.

## Spec Coverage and Self-Review

| Roadmap/spec requirement | Plan coverage |
|---|---|
| B1 single-memory transaction discipline | Tasks 1-3; exact Core coordinator and per-scope lock boundary |
| `remember` whole-memory writes | Task 2; existing `StoreAsync` signature retained |
| `compact` archive/commit/recovery behavior | Task 3; no duplicate archive or derivation path |
| Skill/agent mutations | Tasks 4-6; body/meta pair and built-in behavior retained |
| Arbitrary sidecar mutations | Task 5; generic `FileEntityMutation` coordinator |
| Commit, rollback, recovery, warnings | Tasks 1-2, 4-7; typed outcomes and evidence rules |
| Archive/sidecar pairing | Task 2, Task 4, and dedicated archive rules |
| Ephemeral exclusion | Task 2, Task 3, and explicit compatibility rules |
| Concurrency/per-scope locking | Task 2, Task 4, and locking section |
| CLI/MCP single result contract | Task 7 and CLI/MCP section |
| Source-generated JSON | Tasks 1 and 7 plus global constraint and context map |
| Existing migrations and malformed sidecars | Tasks 4-6; read/refusal behavior preserved without schema changes |
| B0 dependency and stable extension points | Existing/proposed contract section and Task 1 |
| No bundle/import atomicity | Spec line, file map, global exclusion, final scope gate |
| No journal convergence, multi-memory, plugin, pruning, or format work | Spec line, global constraints, dependencies, and final scope gate |
| Deterministic fault, cancellation, restart, malformed, privacy tests | Tasks 2, 4, 5, 7 and full verification commands |

Self-review completed:

- Placeholder scan: no unfinished marker, deferred implementation instruction, or unspecified error-handling step remains.
- Interface scan: all proposed types and method signatures are named once and reused consistently by later tasks.
- Scope scan: the plan is one coherent requested slice around single-member/pair publication; explicitly excluded roadmap lanes are not assigned implementation tasks.
- Compatibility scan: NMP/2 and sidecar schemas remain unchanged; current archive names, migrations, built-in fallback, CLI delegation, MCP tool names, and ephemeral behavior are preserved.
- JSON scan: every newly serialized contract is assigned to a named source-generated context; reflection serialization is prohibited.
- Verification scan: each task has failing test, failure command/expectation, minimal implementation, passing command/expectation, and independent commit; the final commands include build, ordinary full test, opt-in hang-bounded benchmark, trimmed publish, and scoped diff checks.
