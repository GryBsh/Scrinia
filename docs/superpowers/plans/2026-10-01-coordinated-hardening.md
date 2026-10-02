# Coordinated Strict-Safety Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship one breaking strict-safety release that refuses unsafe addresses and incomplete restores before publication, gives CLI and MCP identical reserved-path behavior, bounds retrieval, and exposes only the executable embeddings protocol that exists.

**Architecture:** Establish canonical, durable-safe addresses before any filesystem or vector lookup, then put reserved-path policy behind one internal dispatcher used by both adapters. Bundle import builds a complete immutable plan before one journaled restore operation on `IMemoryStore`; protocol and retrieval work consume canonical, bounded identities rather than rediscovering data from disk.

**Tech Stack:** C#/.NET 10, xUnit, FluentAssertions, `System.Text.Json` source generation, Model Context Protocol, filesystem-backed `FileMemoryStore`, NMP/2, ZIP archives.

**Spec:** `docs/superpowers/specs/2026-10-01-coordinated-hardening-design.md`

## Global Constraints

- This is one coordinated, breaking strict-safety release; do not retain permissive aliases, best-effort import, or a compatibility read mode.
- Do not add an HTTP/REST server, remote CLI mode, generic executable-plugin host, generic executable protocol, or plugin-framework rewrite.
- `MemoryAddress` remains the sole logical-address authority; `/temp/name` canonicalizes to `~name`, while the CLI rejects ephemeral addresses only after parsing.
- Reject malformed, non-portable, or escaping address/scope input before directory creation, reads, writes, vector lookup, or plugin invocation.
- Every `PathRouter.ToStoreDir` mapping, including `local:` and legacy `local-topic:`, must prove `PathContainment.IsWithin(memoriesRoot, candidate)`.
- `/skill/...` and `/agent/...` never fall through to ordinary `FileMemoryStore` handling; unsupported matrix cells return `RESERVED_PATH_OPERATION_FORBIDDEN`.
- Bundle validation failures return `INVALID_BUNDLE` before journal creation or durable mutation; post-journal failures either fully roll back (`BUNDLE_IMPORT_FAILED`) or retain evidence and block imports (`BUNDLE_RECOVERY_REQUIRED`).
- Bundle import must use an `IMemoryStore` restore/import member, not a `FileMemoryStore` cast; a filtered import is atomic only for its selected subset.
- Executable embeddings protocol is exactly version `1` with `status`, `index`, `search`, `remove`, and `status` capabilities; `upsert` is deprecated compatibility-only and never selected after a v1 handshake.
- Changed wire DTOs must use source-generated JSON metadata; reflection serialization is not an escape hatch.
- `Scrinia:Search:CandidateLimit` defaults to `200` and accepts only integer values from `1` through `1000`; an invalid configured value is a configuration error.
- Every downstream scorer, entity-fact derivation, response fact, and vector lookup receives one deterministic lexical top-K candidate snapshot.
- Entity auto-seeding uses complete normalized tokens only; explicit `facts` semantics remain unchanged.
- Living docs and `CHANGELOG.md` change only after contracts and conformance tests are green; do not rewrite dated `docs/superpowers/` artifacts.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Scrinia.Core/Models/MemoryAddress.cs` | Rejects non-portable durable segments and remains the canonical address parser. |
| `src/Scrinia.Core/PathRouter.cs` | Converts only validated scope values to contained memory directories. |
| `src/Scrinia.Mcp/ReservedPathDispatcher.cs` **(create)** | Classifies ordinary, skill, and agent addresses and implements the shared operation matrix. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs`, `MemoryTools.Skills.cs`, `MemoryTools.Agent.cs`, `MemoryTools.Lifecycle.cs` | Delegate MCP operations and file-entity lifecycle work to the dispatcher. |
| `src/Scrinia/Commands/ScriniaCommands.Memory.cs` | Uses the same dispatcher for CLI read/write/delete operations after persistent-only policy. |
| `src/Scrinia.Core/Bundles/BundleFormatService.cs` | Builds a fully validated bundle restore plan and reports verified committed counts. |
| `src/Scrinia.Core/Bundles/BundleRestoreContracts.cs` **(create)** | Defines transport-neutral restore-plan, result, and recovery contracts for `IMemoryStore`. |
| `src/Scrinia.Core/IMemoryStore.cs`, `FileMemoryStore.cs`, `RestoreJournal.cs` | Expose and implement all-or-rollback bundle restore and startup recovery without a concrete-store cast. |
| `src/Scrinia.Plugin.Abstractions/IScriniaPlugin.cs` | Removes generic executable semantics from the public plugin identity contract. |
| `src/Scrinia.Core/Embeddings/EmbeddingsProtocolContracts.cs` **(create)** | Defines version-1 handshake and typed embeddings wire DTOs. |
| `src/Scrinia/Services/McpPluginHost.cs`, `src/Scrinia/Commands/EmbeddingsHost.cs`, `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs` | Negotiate and run only embeddings protocol v1, falling back lexically on refusal. |
| `src/Scrinia.Core/Search/SearchCandidateOptions.cs` **(create)**, `SearchPipeline.cs`, `EntityQuerySeeder.cs` | Validate candidate limit, create the shared snapshot, and seed entities by exact tokens. |
| `src/Scrinia.Core/Embeddings/IVectorStore.cs`, `VectorStore.cs`, `HybridReranker.cs` | Look up vectors only for supplied canonical candidate identities. |
| `src/Scrinia/Commands/WorkspaceSetup.cs`, `WorkspaceConfig.cs`, `CliJsonContext.cs`, `src/Scrinia.Mcp/MemoryTools.Skills.cs` | Bind candidate-limit configuration and register changed DTOs for trimming-safe serialization. |
| `tests/Scrinia.Tests/*`, `tests/Scrinia.Plugin.Embeddings.Tests/*`, `tests/Scrinia.TestDoubles/FakeMemoryStore.cs` | Pin refusal-before-I/O, adapter parity, non-concrete bundle restore, protocol, and bounded retrieval behavior. |
| `docs/getting-started.md`, `docs/cli-reference.md`, `docs/plugin-authoring.md`, `docs/troubleshooting.md`, `CHANGELOG.md` | Publish migration actions, configuration, protocol, and breaking-release guidance. |

### Task 1: Make durable addresses and every scope mapping portable and contained

**Files:**
- Modify: `src/Scrinia.Core/Models/MemoryAddress.cs`, `src/Scrinia.Core/PathRouter.cs`, `src/Scrinia.Core/Embeddings/VectorStore.cs`
- Test: `tests/Scrinia.Tests/MemoryAddressTests.cs`, `tests/Scrinia.Tests/PathRouterTests.cs`, `tests/Scrinia.Tests/VectorStoreTests.cs`

**Interfaces:**
- Consumes: `MemoryAddress.TryParse(string?, out MemoryAddress, out MemoryAddressError)`, `PathContainment.IsWithin(string, string)`.
- Produces: `MemoryAddress.TryParse` rejects rooted, URI-like, traversal, empty, trailing-dot, trailing-space, and non-injective durable segments; `public sealed class ScopeContainmentException : ArgumentException`; `PathRouter.ToStoreDir(string scope, string workspaceRoot)` either returns a contained path or throws `ScopeContainmentException` before I/O.

- [ ] **Step 1: Write failing portability and containment tests.**

```csharp
[Theory]
[InlineData("/notes/design", true)]
[InlineData("/notes/design.", false)]
[InlineData("/notes/design ", false)]
[InlineData("/notes/../secret", false)]
[InlineData("/notes/%2e%2e/secret", false)]
[InlineData("/C:/windows", false)]
[InlineData("/\\server/share", false)]
public void PersistentAddresses_AreDurablyPortable(string raw, bool accepted)
{
    MemoryAddress.TryParse(raw, out var address, out _).Should().Be(accepted);
    if (accepted) address.ToString().Should().Be("/notes/design");
}

[Theory]
[InlineData("local:../../escape")]
[InlineData("local-topic:../../escape")]
public void ToStoreDir_RejectsEscapingScopeBeforeFilesystemAccess(string scope)
{
    Action act = () => PathRouter.ToStoreDir(scope, _workspaceRoot);
    act.Should().Throw<ArgumentException>();
    Directory.Exists(Path.Combine(_workspaceRoot, ".scrinia", "memories", "..", "..", "escape"))
        .Should().BeFalse();
}
```

- [ ] **Step 2: Run the focused tests to verify they fail.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MemoryAddressTests|FullyQualifiedName~PathRouterTests|FullyQualifiedName~VectorStoreTests"`

Expected: FAIL because trailing durable names remain accepted and `local:` returns an unchecked combined path.

- [ ] **Step 3: Implement the single portable-name rule.**

```csharp
private static bool IsLegalDurableSegment(string segment) =>
    IsLegalSegment(segment)
    && !segment.EndsWith('.', StringComparison.Ordinal)
    && !segment.EndsWith(' ', StringComparison.Ordinal)
    && !Path.GetInvalidFileNameChars().Any(segment.Contains);

public static string ToStoreDir(string scope, string workspaceRoot)
{
    string memoriesRoot = Path.Combine(workspaceRoot, ".scrinia", "memories");
    string candidate = ScopeToValidatedRelativePath(scope, memoriesRoot);
    if (!PathContainment.IsWithin(memoriesRoot, candidate))
        throw new ScopeContainmentException(scope);
    return candidate;
}
```

Use the same validated scope constructor in `VectorStore.ScopeToRelativeDir`; preserve its injective hash-based segment encoding, but refuse invalid scope text rather than sanitizing it into a different identity.

- [ ] **Step 4: Run the focused tests to verify they pass.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MemoryAddressTests|FullyQualifiedName~PathRouterTests|FullyQualifiedName~VectorStoreTests"`

Expected: PASS; valid `/notes/design` and `/temp/name`/`~name` still parse, and both `local:` formats refuse escape attempts before any path is returned.

- [ ] **Step 5: Commit.**

```bash
git add src/Scrinia.Core/Models/MemoryAddress.cs src/Scrinia.Core/PathRouter.cs src/Scrinia.Core/Embeddings/VectorStore.cs tests/Scrinia.Tests/MemoryAddressTests.cs tests/Scrinia.Tests/PathRouterTests.cs tests/Scrinia.Tests/VectorStoreTests.cs
git commit -m "fix(core)!: enforce portable contained memory scopes"
```

### Task 2: Route CLI and MCP reserved paths through one dispatcher

**Files:**
- Create: `src/Scrinia.Mcp/ReservedPathDispatcher.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs`, `MemoryTools.Skills.cs`, `MemoryTools.Agent.cs`, `MemoryTools.Lifecycle.cs`, `MemoryTools.cs`, `src/Scrinia/Commands/ScriniaCommands.Memory.cs`
- Test: `tests/Scrinia.Tests/ReservedPathDispatcherTests.cs`, `tests/Scrinia.Tests/CliReservedPathParityTests.cs`, `tests/Scrinia.Tests/AgentFileEntityTests.cs`

**Interfaces:**
- Consumes: `MemoryAddress`, `FileEntityMutation`, `ScriniaMcpTools.SkillLoad`, `SkillDelete`, and `PublishFileEntityAsync`.
- Produces: `internal enum ReservedPathKind { Ordinary, Skill, Agent }`; `internal enum ReservedPathOperation { Read, Write, Append, Delete, List, Search, Copy, Export, Import }`; `internal sealed record ReservedPathRequest(string[]? Content, string? InlineArtifact, bool WithBuiltin, bool Overwrite)`; `internal sealed class ReservedPathOperationException : InvalidOperationException`; `internal Task<McpResponse?> DispatchAsync(ReservedPathOperation operation, MemoryAddress address, ReservedPathRequest request, CancellationToken ct)` where `null` means ordinary-memory handling may proceed.

- [ ] **Step 1: Write the failing matrix and deletion tests.**

```csharp
[Theory]
[MemberData(nameof(OperationMatrix))]
public async Task CliAndMcp_ApplyTheSameReservedPathPolicy(
    ReservedPathOperation operation, string path, string expectedCode)
{
    McpResponse mcp = await InvokeMcp(operation, path);
    CliInvocation cli = await InvokeCli(operation, path);

    mcp.ErrorCode.Should().Be(expectedCode);
    cli.ErrorCode.Should().Be(expectedCode);
}

[Fact]
public async Task DeleteAgent_RemovesBodySidecarAndVersions()
{
    await SeedAgent("reviewer", body: "instructions", sidecar: true, versionCount: 2);
    McpResponse result = await Dispatcher(ReservedPathOperation.Delete, "/agent/reviewer");

    result.IsError.Should().BeFalse();
    AgentBody("reviewer").Should().NotExist();
    AgentSidecar("reviewer").Should().NotExist();
    AgentVersions("reviewer").Should().BeEmpty();
}
```

Cover ordinary, skill, and agent for every matrix operation. Assert skill/agent append, list/search visibility, copy/export/import, and unsupported combinations return `RESERVED_PATH_OPERATION_FORBIDDEN`, with no ordinary-memory artifact created.

- [ ] **Step 2: Run the focused tests to verify they fail.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ReservedPathDispatcherTests|FullyQualifiedName~CliReservedPathParityTests|FullyQualifiedName~AgentFileEntityTests"`

Expected: FAIL because `/agent` special cases are split across MCP helpers, CLI calls internal methods directly, and agent deletion has no shared lifecycle route.

- [ ] **Step 3: Implement the dispatcher before each ordinary-memory branch.**

```csharp
internal async Task<McpResponse?> DispatchAsync(
    ReservedPathOperation operation, MemoryAddress address, ReservedPathRequest request, CancellationToken ct)
{
    return Classify(address) switch
    {
        ReservedPathKind.Ordinary => null,
        ReservedPathKind.Skill => await DispatchSkillAsync(operation, Subject(address), request, ct),
        ReservedPathKind.Agent => await DispatchAgentAsync(operation, Subject(address), request, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(address))
    };
}
```

Make `DispatchAgentAsync(Delete, ...)` publish a deleting `FileEntityMutation` with body, sidecar, and versions directory. Keep built-in skill deletion behavior explicit. Have CLI construct the same `ReservedPathRequest` and call this internal dispatcher after `TryGetPersistentAddress`; do not route CLI through `[McpServerTool]` facade methods.

- [ ] **Step 4: Run the focused tests to verify they pass.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ReservedPathDispatcherTests|FullyQualifiedName~CliReservedPathParityTests|FullyQualifiedName~AgentFileEntityTests"`

Expected: PASS; every unsupported cell is a stable policy refusal and deleting a disk-backed skill or agent leaves no body, sidecar, or version files.

- [ ] **Step 5: Commit.**

```bash
git add src/Scrinia.Mcp/ReservedPathDispatcher.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia.Mcp/MemoryTools.Agent.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs src/Scrinia.Mcp/MemoryTools.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs tests/Scrinia.Tests/ReservedPathDispatcherTests.cs tests/Scrinia.Tests/CliReservedPathParityTests.cs tests/Scrinia.Tests/AgentFileEntityTests.cs
git commit -m "fix: centralize reserved memory path dispatch"
```

### Task 3: Make bundle import a complete-plan, journaled `IMemoryStore` operation

**Files:**
- Create: `src/Scrinia.Core/Bundles/BundleRestoreContracts.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs`, `FileMemoryStore.cs`, `RestoreJournal.cs`, `Bundles/BundleFormatService.cs`, `src/Scrinia.Mcp/MemoryTools.Bundling.cs`, `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`, `tests/Scrinia.TestDoubles/FakeMemoryStore.cs`
- Test: `tests/Scrinia.Tests/BundleStrictValidationTests.cs`, `tests/Scrinia.Tests/BundleAtomicRestoreTests.cs`, `tests/Scrinia.Tests/BundleInterfaceRestoreTests.cs`

**Interfaces:**
- Consumes: Task 1 address/scope refusal and Task 2 file-entity policy.
- Produces: `public sealed record BundleTopicRestore(string Scope, string Topic, IReadOnlyList<ArtifactEntry> Entries, IReadOnlyDictionary<string,byte[]> ArtifactBytes)`; `public sealed record BundleFileEntityRestore(string Kind, string Name, byte[] Body, byte[]? Sidecar)`; `public sealed record BundleRestorePlan(IReadOnlyList<BundleTopicRestore> Topics, IReadOnlyList<BundleFileEntityRestore> FileEntities, bool Overwrite)`; `public enum BundleRestoreOutcome { Committed }`; `public enum BundleImportFailure { InvalidBundle, RolledBack, RecoveryRequired }`; `public sealed class BundleValidationException : InvalidOperationException`; `public sealed class BundleImportException : InvalidOperationException` with `BundleImportFailure Failure`; `Task<BundleRestoreResult> RestoreBundleAsync(BundleRestorePlan plan, CancellationToken ct = default)` on `IMemoryStore`; `public sealed record BundleRestoreResult(int TopicCount, int EntryCount, int FileEntityCount, BundleRestoreOutcome Outcome)`; and `public static Task<BundleRestoreResult> ImportTopicsFromZipAsync(ZipArchive zip, IMemoryStore store, string[]? filterTopics, bool overwrite, CancellationToken ct = default)`.

- [ ] **Step 1: Write failing build-gate and non-concrete-store tests.**

```csharp
[Theory]
[MemberData(nameof(MissingRequiredMembers))]
public void Import_RefusesAnyMissingDeclaredMemberWithoutMutation(string zipMember)
{
    using var archive = BundleFixture.WithMemberRemoved(zipMember);
    Snapshot before = SnapshotWorkspace(_workspaceRoot);

    Action act = () => BundleFormatService.ImportTopicsFromZip(archive, _store, null, overwrite: true);

    act.Should().Throw<InvalidOperationException>().WithMessage("*INVALID_BUNDLE*");
    SnapshotWorkspace(_workspaceRoot).Should().BeEquivalentTo(before);
}

[Fact]
public async Task Import_UsesThePublicStoreRestoreContract()
{
    var store = new RecordingRestoreStore(); // implements IMemoryStore; is not FileMemoryStore
    await BundleFormatService.ImportTopicsFromZipAsync(BundleFixture.Valid(), store, null, true, default);
    store.ReceivedPlan.Should().NotBeNull();
}
```

Include malformed manifest/index shapes, duplicate and unsafe topic names, missing artifact bodies, malformed entry metadata, unknown file-entity categories, missing declared file entities, and manifest-total mismatches. Assert filtered import plans only selected topics and their declared file entities.

- [ ] **Step 2: Run the focused tests to verify they fail.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleStrictValidationTests|FullyQualifiedName~BundleAtomicRestoreTests|FullyQualifiedName~BundleInterfaceRestoreTests"`

Expected: FAIL because missing members are skipped, unknown categories are reported as skipped, and `BundleFormatService` casts `IMemoryStore` to `FileMemoryStore`.

- [ ] **Step 3: Build the immutable restore plan before opening a journal.**

```csharp
internal static BundleRestorePlan BuildRestorePlan(ZipArchive zip, IMemoryStore store,
    IReadOnlyList<string>? selectedTopics, bool overwrite)
{
    BundleManifest manifest = ParseAndValidateManifest(zip);
    var topics = ValidateTopicsAndArtifacts(zip, manifest, selectedTopics, store);
    var files = ValidateFileEntities(zip, manifest, store); // unknown category throws
    ValidateExactTotals(manifest, topics);
    return new BundleRestorePlan(topics, files, overwrite);
}
```

Replace skipped-category output with refusal. Do not decode malformed artifact content as an enrichment-only exception: every declared body is a required restore member and must satisfy the archive/body validation rule before plan construction succeeds.

- [ ] **Step 4: Implement all-or-rollback restore and recovery.**

```csharp
public async Task<BundleRestoreResult> RestoreBundleAsync(BundleRestorePlan plan, CancellationToken ct = default)
{
    ThrowIfBundleRecoveryBlocked();
    BundleRestoreJournal journal = await BundleRestoreJournal.CreateAsync(this, plan, ct);
    await journal.PersistPreparedAsync(ct);
    try
    {
        await ApplyBundlePlanAsync(plan, ct);
        await VerifyBundlePlanAsync(plan, ct);
        await journal.CompleteAndDeleteAsync(ct);
        return BundleRestoreResult.Committed(plan);
    }
    catch (Exception failure)
    {
        if (await journal.TryRollbackAsync(ct)) throw new BundleImportException(BundleImportFailure.RolledBack, failure);
        throw new BundleImportException(BundleImportFailure.RecoveryRequired, failure, journal.Path);
    }
}
```

Journal prior state for every topic mutation and file-entity mutation, including absent prior files. Recover a prepared journal at startup using the same rollback proof; leave recovery evidence in place and reject subsequent imports while proof is unavailable. Return counts from verified plan application, never directly from manifest fields.

- [ ] **Step 5: Run failure-injection and cold-reload tests to verify they pass.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleStrictValidationTests|FullyQualifiedName~BundleAtomicRestoreTests|FullyQualifiedName~BundleInterfaceRestoreTests"`

Expected: PASS. Injected failure at journal write, every topic/file mutation, verification, and rollback leaves either the full selected plan or the exact pre-import snapshot after cold reload; an unprovable rollback returns `BUNDLE_RECOVERY_REQUIRED` and blocks a second import.

- [ ] **Step 6: Commit.**

```bash
git add src/Scrinia.Core/Bundles/BundleRestoreContracts.cs src/Scrinia.Core/Bundles/BundleFormatService.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/RestoreJournal.cs src/Scrinia.Mcp/MemoryTools.Bundling.cs src/Scrinia/Commands/ScriniaCommands.Bundle.cs tests/Scrinia.TestDoubles/FakeMemoryStore.cs tests/Scrinia.Tests/BundleStrictValidationTests.cs tests/Scrinia.Tests/BundleAtomicRestoreTests.cs tests/Scrinia.Tests/BundleInterfaceRestoreTests.cs
git commit -m "fix(core)!: make bundle import all-or-rollback"
```

### Task 4: Publish and enforce embeddings protocol v1 only

**Files:**
- Create: `src/Scrinia.Core/Embeddings/EmbeddingsProtocolContracts.cs`
- Modify: `src/Scrinia.Plugin.Abstractions/IScriniaPlugin.cs`, `ScriniaPluginBase.cs`, `src/Scrinia/Services/PluginClientJsonContext.cs`, `McpPluginHost.cs`, `src/Scrinia/Commands/EmbeddingsHost.cs`, `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs`
- Test: `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsProtocolTests.cs`, `tests/Scrinia.Tests/McpPluginHostProtocolTests.cs`, `tests/Scrinia.Tests/SourceGeneratedProtocolSerializationTests.cs`

**Interfaces:**
- Produces: `public sealed record EmbeddingsStatusResponse(int ProtocolVersion, string[] Capabilities, bool ProviderAvailable, int Dimensions, string VectorStoreSignature)`; `public sealed record EmbeddingsIndexRequest(string Scope, string Name, string[] Content)`; `public sealed record EmbeddingsSearchRequest(string Query, IReadOnlyDictionary<string,string[]> CandidatesByScope)`; `public sealed record EmbeddingsRemoveRequest(string Scope, string Name)`; `public sealed record EmbeddingsOperationResponse(bool Ok, string? Error)`; and matching response DTOs.
- Consumes: source-generated `PluginClientJsonContext`; `ISearchScoreContributor.ComputeScoresAsync` candidate list.
- Also produces: `public sealed class EmbeddingsProtocolUnsupportedException : InvalidOperationException` and `public sealed class EmbeddingsProtocolInvalidException : InvalidOperationException`.

- [ ] **Step 1: Write failing handshake, fallback, and serialization tests.**

```csharp
[Theory]
[MemberData(nameof(InvalidStatuses))]
public async Task Host_DisablesExecutableForSession_WhenStatusIsNotProtocolV1(string statusJson)
{
    await using var host = await StartHost(statusJson);
    host.HasSearchCapability.Should().BeFalse();
    host.ProtocolFailureCode.Should().BeOneOf("EMBEDDINGS_PROTOCOL_INVALID", "EMBEDDINGS_PROTOCOL_UNSUPPORTED");
    await LexicalSearchRemainsUsable();
}

[Fact]
public async Task Search_SendsOnlyCandidateNamesAndRejectsExtraScores()
{
    var scores = await _host.ComputeScoresAsync("query", Candidates("local", "allowed"), _store, default);
    _child.LastSearch!.CandidatesByScope["local"].Should().Equal("allowed");
    scores!.Keys.Should().OnlyContain(key => key.Contains("allowed", StringComparison.OrdinalIgnoreCase));
}
```

Cover absent, malformed, duplicate, unsupported protocol values; missing/duplicate required capabilities; unavailable provider; each required operation; deprecated `upsert`; and every DTO through its production source-generated context.

- [ ] **Step 2: Run the focused tests to verify they fail.**

Run: `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --filter FullyQualifiedName~EmbeddingsProtocolTests; dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~McpPluginHostProtocolTests|FullyQualifiedName~SourceGeneratedProtocolSerializationTests"`

Expected: FAIL because `status` has no version/capability contract and `search` accepts only unconstrained scopes.

- [ ] **Step 3: Replace anonymous JSON and generic claims with typed v1 contracts.**

```csharp
public sealed record EmbeddingsStatusResponse(
    int ProtocolVersion, string[] Capabilities, bool ProviderAvailable,
    int Dimensions, string VectorStoreSignature);

private static bool Supports(EmbeddingsStatusResponse status, string operation) =>
    status.ProtocolVersion == 1 && status.ProviderAvailable &&
    status.Capabilities.Distinct(StringComparer.Ordinal).Count() == status.Capabilities.Length &&
    status.Capabilities.Contains(operation, StringComparer.Ordinal);
```

Make `McpPluginHost.StartAsync` call `status` before registering search or event-sink capability. A failed negotiation disposes/disables the child for the session and installs documented lexical/path fallback. Remove `IScriniaPlugin.Order`, generic executable state-path language, and `EmbeddingsPlugin.Configure` as executable protocol semantics; retain in-process plugin hooks only where their existing contract requires them.

- [ ] **Step 4: Run the protocol suites to verify they pass.**

Run: `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --filter FullyQualifiedName~EmbeddingsProtocolTests; dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~McpPluginHostProtocolTests|FullyQualifiedName~SourceGeneratedProtocolSerializationTests"`

Expected: PASS; only protocol v1 with the required operation capability is used, and invalid executables leave lexical search available.

- [ ] **Step 5: Commit.**

```bash
git add src/Scrinia.Core/Embeddings/EmbeddingsProtocolContracts.cs src/Scrinia.Plugin.Abstractions/IScriniaPlugin.cs src/Scrinia.Plugin.Abstractions/ScriniaPluginBase.cs src/Scrinia/Services/PluginClientJsonContext.cs src/Scrinia/Services/McpPluginHost.cs src/Scrinia/Commands/EmbeddingsHost.cs src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsProtocolTests.cs tests/Scrinia.Tests/McpPluginHostProtocolTests.cs tests/Scrinia.Tests/SourceGeneratedProtocolSerializationTests.cs
git commit -m "breaking: define embeddings executable protocol v1"
```

### Task 5: Bound lexical, vector, and entity work to one candidate snapshot

**Files:**
- Create: `src/Scrinia.Core/Search/SearchCandidateOptions.cs`
- Modify: `src/Scrinia.Core/Search/SearchPipeline.cs`, `SearchQuery.cs`, `EntityQuerySeeder.cs`, `src/Scrinia.Core/Embeddings/IVectorStore.cs`, `VectorStore.cs`, `HybridReranker.cs`, `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`, `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs`, `src/Scrinia/Services/McpPluginHost.cs`, `src/Scrinia/Commands/WorkspaceSetup.cs`, `WorkspaceConfig.cs`
- Test: `tests/Scrinia.Tests/SearchCandidateLimitTests.cs`, `tests/Scrinia.Tests/EntityQuerySeederTests.cs`, `tests/Scrinia.Tests/VectorCandidateLookupTests.cs`, `tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginScoringTests.cs`

**Interfaces:**
- Produces: `public sealed record SearchCandidateOptions(int CandidateLimit)` with `DefaultCandidateLimit = 200`; `IReadOnlyList<VectorEntry> GetCandidateVectors(string canonicalScope, IReadOnlyCollection<string> candidateNames)` on `IVectorStore`.
- Consumes: `SearchPipeline.ExecuteAsync`, `ISearchScoreContributor.ComputeScoresAsync(string, IReadOnlyList<ScopedArtifact>, IMemoryStore, CancellationToken)`.

- [ ] **Step 1: Write failing candidate-boundary and exact-token tests.**

```csharp
[Fact]
public async Task Pipeline_UsesOneTopKSnapshotForContributorsFactsAndResults()
{
    var outcome = await SearchPipeline.ExecuteAsync(_store, new SearchQuery("alpha"), _contributor, _entity,
        surfaceEntityFacts: true);

    _contributor.CandidateAddresses.Should().HaveCount(3);
    _entity.LastFactAddresses.Should().BeEquivalentTo(_contributor.CandidateAddresses);
    _store.LastSearchCandidates.Should().BeEquivalentTo(_contributor.CandidateAddresses);
}

[Theory]
[InlineData("art", "person:martin", false)]
[InlineData("Martin!", "person:martin", true)]
[InlineData("new york architecture", "place:new york", true)]
public void Seed_RequiresCompleteNormalizedTokens(string query, string entity, bool expected) =>
    EntityQuerySeeder.Seed(query, [entity]).Should().HaveCount(expected ? 1 : 0);
```

Also assert absent config means 200; `0`, `1001`, and non-integer values fail configuration loading; vector lookup is case-insensitive but returns no vector whose name is absent from candidates; bounded fixture ordering is deterministic.

- [ ] **Step 2: Run the focused tests to verify they fail.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SearchCandidateLimitTests|FullyQualifiedName~EntityQuerySeederTests|FullyQualifiedName~VectorCandidateLookupTests"; dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --filter FullyQualifiedName~EmbeddingsPluginScoringTests`

Expected: FAIL because the pipeline materializes the full scoped corpus, vector paths call `GetVectors(scope)`, and `art` substring-seeds `martin`.

- [ ] **Step 3: Validate configuration and form the candidate snapshot before contributors run.**

```csharp
public sealed record SearchCandidateOptions(int CandidateLimit)
{
    public const int DefaultCandidateLimit = 200;
    public const int MaximumCandidateLimit = 1000;
    public static SearchCandidateOptions Parse(string? value) => value is null
        ? new(DefaultCandidateLimit)
        : int.TryParse(value, out int limit) && limit is >= 1 and <= MaximumCandidateLimit
            ? new(limit) : throw new ConfigurationException("Scrinia:Search:CandidateLimit must be an integer from 1 through 1000.");
}

var candidates = RankLexically(store.ListScoped(query.Scopes, query.ExcludeTopics), effectiveQuery)
    .Take(options.CandidateLimit).ToArray();
```

Pass this exact array into contributors, entity derivation, `SearchQuery.Candidates`, and response-fact generation. Preserve ranking weights and final result limit; only the corpus eligible for downstream work changes.

- [ ] **Step 4: Replace full-scope vector reads with candidate lookup.**

```csharp
public IReadOnlyList<VectorEntry> GetCandidateVectors(
    string canonicalScope, IReadOnlyCollection<string> candidateNames) =>
    GetVectors(canonicalScope)
        .Where(vector => candidateNames.Contains(vector.Name, StringComparer.OrdinalIgnoreCase))
        .ToArray();
```

Use grouped candidate names in `HybridReranker`, in-process embeddings, and protocol-v1 `search`. The child must score and return only supplied names; host-side projection must discard an unexpected identity as defense in depth. Do not discover vector scopes from disk.

- [ ] **Step 5: Replace substring entity seeding.**

```csharp
var queryTokens = TextAnalysis.Tokenize(query).ToHashSet(StringComparer.OrdinalIgnoreCase);
var entityTokens = TextAnalysis.Tokenize(SurfaceOf(key));
if (entityTokens.Count > 0 && entityTokens.All(queryTokens.Contains))
    seeds.Add(key);
```

Delete the raw `Contains` branch. Keep `facts` parsing and entity-key filtering unchanged.

- [ ] **Step 6: Run the focused tests to verify they pass.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SearchCandidateLimitTests|FullyQualifiedName~EntityQuerySeederTests|FullyQualifiedName~VectorCandidateLookupTests"; dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --filter FullyQualifiedName~EmbeddingsPluginScoringTests`

Expected: PASS; no contributor receives more than K candidates, all consumers observe the same identities, and no substring-only entity is seeded.

- [ ] **Step 7: Commit.**

```bash
git add src/Scrinia.Core/Search/SearchCandidateOptions.cs src/Scrinia.Core/Search/SearchPipeline.cs src/Scrinia.Core/Search/SearchQuery.cs src/Scrinia.Core/Search/EntityQuerySeeder.cs src/Scrinia.Core/Embeddings/IVectorStore.cs src/Scrinia.Core/Embeddings/VectorStore.cs src/Scrinia.Core/Embeddings/HybridReranker.cs src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs src/Scrinia/Services/McpPluginHost.cs src/Scrinia/Commands/WorkspaceSetup.cs src/Scrinia/Commands/WorkspaceConfig.cs tests/Scrinia.Tests/SearchCandidateLimitTests.cs tests/Scrinia.Tests/EntityQuerySeederTests.cs tests/Scrinia.Tests/VectorCandidateLookupTests.cs tests/Scrinia.Plugin.Embeddings.Tests/EmbeddingsPluginScoringTests.cs
git commit -m "fix(search)!: bound retrieval to lexical candidates"
```

### Task 6: Map strict migration errors consistently and update living guidance

**Files:**
- Modify: `src/Scrinia.Core/Models/MemoryError.cs`, `src/Scrinia.Mcp/McpResponse.cs`, `MemoryTools.Bundling.cs`, `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`, `ScriniaCommands.Memory.cs`, `CliJsonContext.cs`
- Modify: `docs/getting-started.md`, `docs/cli-reference.md`, `docs/plugin-authoring.md`, `docs/troubleshooting.md`, `CHANGELOG.md`
- Test: `tests/Scrinia.Tests/StrictSafetyErrorMappingTests.cs`, `tests/Scrinia.Tests/CliMcpMigrationParityTests.cs`, `tests/Scrinia.Tests/PublicGuidanceContractTests.cs`

**Interfaces:**
- Produces stable machine-readable codes: `MALFORMED_ADDRESS`, `SCOPE_CONTAINMENT_FORBIDDEN`, `RESERVED_PATH_OPERATION_FORBIDDEN`, `INVALID_BUNDLE`, `BUNDLE_IMPORT_FAILED`, `BUNDLE_RECOVERY_REQUIRED`, `EMBEDDINGS_PROTOCOL_UNSUPPORTED`, and `EMBEDDINGS_PROTOCOL_INVALID`.
- Consumes: Tasks 1–5 refusal and recovery exceptions.

- [ ] **Step 1: Write failing adapter-parity and documentation contract tests.**

```csharp
[Theory]
[MemberData(nameof(MigrationFailures))]
public async Task CliAndMcp_PreserveTheStrictMachineCode(string operation, Exception failure, string code)
{
    (await InvokeMcpFailure(operation, failure)).ErrorCode.Should().Be(code);
    (await InvokeCliFailure(operation, failure)).ErrorCode.Should().Be(code);
}

[Fact]
public void LivingDocs_DescribeRecoveryInsteadOfPartialSuccess()
{
    Read("docs/troubleshooting.md").Should().Contain("BUNDLE_RECOVERY_REQUIRED")
        .And.NotContain("partial import succeeded");
}
```

- [ ] **Step 2: Run the focused tests to verify they fail.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~StrictSafetyErrorMappingTests|FullyQualifiedName~CliMcpMigrationParityTests|FullyQualifiedName~PublicGuidanceContractTests"`

Expected: FAIL because current adapters classify strict failures as generic invalid parameters or successful skipped categories, and living docs do not describe candidate-limit/protocol migration.

- [ ] **Step 3: Add one mapping table and update the five living documents.**

```csharp
internal static string ToStableCode(Exception error) => error switch
{
    MemoryAddressException => "MALFORMED_ADDRESS",
    ScopeContainmentException => "SCOPE_CONTAINMENT_FORBIDDEN",
    ReservedPathOperationException => "RESERVED_PATH_OPERATION_FORBIDDEN",
    BundleValidationException => "INVALID_BUNDLE",
    BundleImportException { Failure: BundleImportFailure.RolledBack } => "BUNDLE_IMPORT_FAILED",
    BundleImportException { Failure: BundleImportFailure.RecoveryRequired } => "BUNDLE_RECOVERY_REQUIRED",
    EmbeddingsProtocolUnsupportedException => "EMBEDDINGS_PROTOCOL_UNSUPPORTED",
    EmbeddingsProtocolInvalidException => "EMBEDDINGS_PROTOCOL_INVALID",
    _ => throw error
};
```

Document canonical logical addresses, strict legacy-data refusal, reserved skill/agent lifecycle, all-or-rollback bundle retry/recovery action, embeddings v1 handshake/reinstall action, vector quarantine/reindex, and `Scrinia:Search:CandidateLimit` default/range. Add the breaking-release section to `CHANGELOG.md`; do not alter historical planning documents.

- [ ] **Step 4: Run focused tests to verify they pass.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~StrictSafetyErrorMappingTests|FullyQualifiedName~CliMcpMigrationParityTests|FullyQualifiedName~PublicGuidanceContractTests"`

Expected: PASS; every boundary retains its exact stable code and public guidance tells users the explicit recovery or migration action.

- [ ] **Step 5: Commit.**

```bash
git add src/Scrinia.Core/Models/MemoryError.cs src/Scrinia.Mcp/McpResponse.cs src/Scrinia.Mcp/MemoryTools.Bundling.cs src/Scrinia/Commands/ScriniaCommands.Bundle.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia/Commands/CliJsonContext.cs docs/getting-started.md docs/cli-reference.md docs/plugin-authoring.md docs/troubleshooting.md CHANGELOG.md tests/Scrinia.Tests/StrictSafetyErrorMappingTests.cs tests/Scrinia.Tests/CliMcpMigrationParityTests.cs tests/Scrinia.Tests/PublicGuidanceContractTests.cs
git commit -m "docs: publish strict-safety migration guidance"
```

### Task 7: Run the coordinated release acceptance gate

**Files:**
- Modify: only a production file, test, or living document required to correct a demonstrated failure from Tasks 1–6.
- Test: all changed suites plus the complete default solution suite.

**Interfaces:**
- Consumes: all contracts created in Tasks 1–6.
- Produces: release evidence that public CLI, MCP, bundle, plugin, and retrieval surfaces all meet the approved strict-safety contract.

- [ ] **Step 1: Run the address, dispatcher, and bundle acceptance suites.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MemoryAddressTests|FullyQualifiedName~PathRouterTests|FullyQualifiedName~ReservedPathDispatcherTests|FullyQualifiedName~CliReservedPathParityTests|FullyQualifiedName~BundleStrictValidationTests|FullyQualifiedName~BundleAtomicRestoreTests|FullyQualifiedName~BundleInterfaceRestoreTests"`

Expected: PASS. Confirm malformed/rooted/URI/traversal/trailing-dot/trailing-space addresses refuse before I/O; CLI/MCP matrices agree; every bundle build-gate mutation leaves zero changes; and recovery-required blocks a second import.

- [ ] **Step 2: Run protocol, source-generation, and retrieval acceptance suites.**

Run: `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj --filter "FullyQualifiedName~EmbeddingsProtocolTests|FullyQualifiedName~EmbeddingsPluginScoringTests"; dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~McpPluginHostProtocolTests|FullyQualifiedName~SourceGeneratedProtocolSerializationTests|FullyQualifiedName~SearchCandidateLimitTests|FullyQualifiedName~VectorCandidateLookupTests|FullyQualifiedName~EntityQuerySeederTests"`

Expected: PASS. Confirm unsupported executable protocol degrades to lexical search, changed DTOs serialize through generated metadata, and no downstream retrieval path escapes the candidate snapshot.

- [ ] **Step 3: Run build, trim, documentation, and full-suite checks.**

Run: `dotnet build Scrinia.sln; dotnet test Scrinia.sln; pwsh -File publish.ps1 -OutputDir artifacts/strict-safety-trimcheck -Platform win-x64`

Expected: build succeeds with zero errors; default solution tests pass; trimmed publish has no source-generation or trimming warnings. Delete `artifacts/strict-safety-trimcheck` after inspection.

- [ ] **Step 4: Run final public-surface searches and diff checks.**

Run: `git grep -n -E 'Order.*load|generic executable|GetVectors\(scope\)|partial import succeeded|SkippedCategories|\(\(FileMemoryStore\)store\)' -- src tests docs CHANGELOG.md; git diff --check`

Expected: no production or living-document claim retains generic executable behavior, full-scope vector search, partial-success import, skipped file category, or the concrete import cast; `git diff --check` is silent.

- [ ] **Step 5: Commit only verified release-gate corrections.**

```bash
git add src tests docs CHANGELOG.md
git commit -m "fix: close strict-safety release verification gaps"
```

Each correction must first add a focused failing test, then pass that test and the relevant acceptance suite. Do not create a commit if the gate is already green.

## Plan Self-Review

- **Spec coverage:** Task 1 covers portable address validation and `local:`/`local-topic:` containment; Task 2 covers the shared CLI/MCP reserved-path matrix and agent deletion; Task 3 covers complete bundle validation, interface-level restore, journaling, rollback, recovery blocking, and actual counts; Task 4 covers the embeddings-only v1 executable protocol and source generation; Task 5 covers candidate-bounded retrieval/vector lookup and exact entity tokens; Task 6 covers stable migration errors and living documentation; Task 7 is the full release gate.
- **Placeholder scan:** passed. Every task names concrete files, contracts, test classes, test-first code, commands, expected outcomes, and a commit boundary.
- **Type consistency:** passed. `BundleRestorePlan`/`BundleRestoreResult` are introduced before `IMemoryStore.RestoreBundleAsync`; `EmbeddingsSearchRequest` carries grouped candidate names used by host and child; `SearchCandidateOptions` produces the exact candidate snapshot consumed by contributors, facts, `SearchQuery.Candidates`, and vector lookup.
- **Scope:** passed. The plan does not add HTTP, remote mode, a generic executable plugin system, new ranking weights, NMP/2 semantic changes, permissive migration aliases, or historical-document rewrites.
