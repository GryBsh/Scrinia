# Durable-write Conformance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the bounded set of selected durable writes atomically replace one flushed file at a time, refuse unsupported durable formats before their first side effect, and report bundle and filesystem failures with stable MCP/CLI error codes.

**Architecture:** `Scrinia.Core.DurableFile` becomes the one internal synchronous text/byte replacement primitive; its unique sibling temporary file is flushed, moved with retry, and cleaned up best-effort. Artifact sidecars, skill/agent bodies and sidecars, and imported file entities use that primitive. Bundle import first builds and validates both topic and file-entity work, then applies the already-approved writes; multi-file operations intentionally remain prefix-consistent, not transactional.

**Tech Stack:** .NET 10, C#, `System.IO.Compression`, `System.Text.Json` source generation, xUnit, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-24-durable-write-conformance-design.md`

## Global Constraints

- Scope is limited to the artifact sidecar, skill/agent body and sidecar, and bundle file-entity writers. Do not convert incidental config, export, hook, merge-driver, or unrelated writers into a transaction system.
- `DurableFile` has exactly these production APIs: `internal static void AtomicWriteAllText(string path, string content)` and `internal static void AtomicWriteAllBytes(string path, ReadOnlySpan<byte> content)`.
- Both APIs write `{destination}.{pid}.{guid:N}.tmp` with `FileShare.None`, flush the temporary file with `Flush(flushToDisk: true)`, wrap both write and `File.Move(..., overwrite: true)` in `FileRetry.Run`, delete only their own temporary file best-effort on failure, and never create a destination directory.
- Text is UTF-8 without BOM; bytes are copied unchanged. The file is flushed, not its containing directory: a power loss may retain the old file after losing the rename, but must not expose a truncated selected file.
- `FileMemoryStore.AtomicWriteAllText` remains an internal forwarding shim for `ScriniaMcpTools.WriteResolved`; selected new callers use `DurableFile` directly.
- Artifact-sidecar gates remain defence in depth. `WriteSidecars`, `SaveIndexUnsafe`, and `ImportTopicEntries` retain their whole-list/pre-commit admission checks, and `WriteSidecarCore` retains its direct gate.
- Skill/agent preflight (`ReadSidecarMetaOrRefuse`) stays before `ArchiveFileVersion` and body replacement. Existing `SchemaVersion` and copied `Unknown` carry-forward behaviour is unchanged.
- Bundle manifest version remains 2. A v1 bundle with no `fileEntities` remains a no-op for file entities.
- A multi-file skill/agent write and a bundle restore are **not** all-or-nothing. Document the per-file/prefix-consistent limit at every multi-file caller; do not add a journal, replay, rollback set, directory swap, startup recovery, or restore recovery protocol.
- Use source-generated JSON metadata only. Do not introduce reflection serialization or a Core → MCP dependency.
- Failure assertions must use a new `FileMemoryStore` over the same workspace (cold disk), never only `LoadIndex` from a warm cache.
- Add `ErrorCodes.InvalidBundle = "INVALID_BUNDLE"` and `ErrorCodes.DurableWriteFailed = "DURABLE_WRITE_FAILED"`. CLI import must continue to call the MCP/internal import handler and exit 1 for either envelope.
- Add a concise `[Unreleased]` `CHANGELOG.md` entry. No other living documentation is required; dated design/plan artifacts are historical records and must not be rewritten.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/Scrinia.Core/DurableFile.cs` | Shared flush, atomic sibling replacement, cleanup, test-only failure seam. | Create |
| `src/Scrinia.Core/DurableWriteException.cs` | Typed wrapper retaining a filesystem/zip cause plus operation and destination. | Create |
| `src/Scrinia.Core/FileMemoryStore.cs` | Delegate the sidecar writer and legacy reconcile shim to `DurableFile`; retain gates and describe prefix limits. | Modify |
| `src/Scrinia.Core/Bundles/FileEntityMeta.cs` | Core-owned skill/agent file-entity sidecar records and source-generated context. | Create |
| `src/Scrinia.Core/Bundles/BundleFormatService.cs` | Build/preflight file entities before topic writes and atomically apply approved byte writes. | Modify |
| `src/Scrinia.Mcp/MemoryTools.Skills.cs` | Consume the Core sidecar types/context and atomically write skill bodies and sidecars. | Modify |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | Atomically write agent bodies while preserving preflight order. | Modify |
| `src/Scrinia.Mcp/MemoryTools.cs` | Translate direct skill/agent durable-write refusals at the public `remember` facade. | Modify |
| `src/Scrinia.Mcp/MemoryTools.Bundling.cs` | Translate intentional import and durable-write failures to stable response codes. | Modify |
| `src/Scrinia.Mcp/McpResponse.cs` | Declare the two stable response codes. | Modify |
| `tests/Scrinia.Tests/DurableFileTests.cs` | Direct text/byte atomicity, cleanup, and cold-disk failure coverage. | Create |
| `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` | Gate-before-archive/body/sidecar tests for artifact, skill, and agent paths. | Modify |
| `tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs` | Core sidecar context and final sidecar backstop coverage. | Modify |
| `tests/Scrinia.Tests/BundleV2Tests.cs` | File-entity preflight, byte preservation, and cold-disk sentinel tests. | Modify |
| `tests/Scrinia.Tests/Formats/BundleFormatGateTests.cs` | Binding regression coverage that file-entity preflight runs before topic import. | Modify |
| `tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs` | Unknown-category skip and known unsafe filename refusal tests. | Modify |
| `tests/Scrinia.Tests/BundleExitCodeTests.cs` | MCP/CLI structured-error and exit-code coverage. | Modify |
| `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs` | Convert durable restore assertions to cold-disk assertions while preserving prefix semantics. | Modify |
| `CHANGELOG.md` | User-visible durable-write behaviour and explicit limitation. | Modify |

---

### Task 1: Shared durable writer and typed failure

**Files:**
- Create: `src/Scrinia.Core/DurableFile.cs`, `src/Scrinia.Core/DurableWriteException.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1077-1248,2029-2035`
- Create: `tests/Scrinia.Tests/DurableFileTests.cs`

**Interfaces:**
- Produces `internal static class DurableFile` with `AtomicWriteAllText(string, string)` and `AtomicWriteAllBytes(string, ReadOnlySpan<byte>)`.
- Produces `public sealed class DurableWriteException(string operation, string destination, Exception innerException) : IOException` with `Operation` and `Destination` properties. Every failure escaping either `DurableFile` production API is this type, with the caught original failure retained as its `InnerException`; the shared writer never rethrows a raw filesystem or injected failure.
- Produces test-only `internal static Func<DurableWritePhase, string, Exception?>? DurableFile.FailureInjector`, backed by `AsyncLocal`, where `DurableWritePhase` is `BeforeWrite`, `BeforeFlush`, or `BeforeMove`.
- Consumed later by `WriteSidecarCore`, `WriteSidecarMeta`, skill/agent bodies, and `BundleFormatService`.

- [ ] **Step 1: Write failing direct writer tests**

Create `tests/Scrinia.Tests/DurableFileTests.cs` with tests that seed a destination with `old-text` and `new byte[] { 0, 255, 7 }`, set the async-local injector to return `new UnauthorizedAccessException("injected before move")` for `BeforeMove`, and call the corresponding API. Each test must assert `DurableWriteException.Operation == "write text"` or `"write bytes"`, preserve the original exception as `InnerException`, reopen a fresh `FileMemoryStore` over the workspace, and assert the original destination bytes remain. Enumerate `Path.GetDirectoryName(destination)!` and assert no filename starts with `${Path.GetFileName(destination)}.` and ends in `.tmp`.

Also add a passing byte-fidelity test:

```csharp
[Fact]
public void AtomicWriteAllBytes_PreservesArbitraryBytes()
{
    using var scope = new TestHelpers.StoreScope();
    string path = Path.Combine(scope.TempDir, "bytes.meta.json");
    byte[] expected = [0, 255, 1, 0, 128];

    DurableFile.AtomicWriteAllBytes(path, expected);

    File.ReadAllBytes(path).Should().Equal(expected);
}
```

- [ ] **Step 2: Run the focused tests and confirm they fail to compile**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~DurableFileTests`

Expected: build failure because `DurableFile`, `DurableWriteException`, and `DurableWritePhase` do not exist.

- [ ] **Step 3: Implement the primitive and failure wrapper**

Create `DurableWriteException.cs`:

```csharp
namespace Scrinia.Core;

public sealed class DurableWriteException : IOException
{
    public string Operation { get; }
    public string Destination { get; }

    public DurableWriteException(string operation, string destination, Exception innerException)
        : base($"Durable {operation} failed for '{destination}'.", innerException)
    {
        Operation = operation;
        Destination = destination;
    }
}
```

Create `DurableFile.cs` so both public-to-Core methods call one private `AtomicWrite` method. Copy the incoming `ReadOnlySpan<byte>` once to a `byte[]` before passing it to `FileRetry.Run` (a span cannot be captured by its delegate); the copy is byte-for-byte and is not text decoding. It must invoke `FailureInjector` immediately before writing, flushing, and moving; throw the returned exception; write through a `FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)`; and call `Flush(flushToDisk: true)`.

```csharp
private static void AtomicWrite(string path, ReadOnlySpan<byte> content, string operation)
{
    byte[] bytes = content.ToArray();
    string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
    try
    {
        FileRetry.Run(() => WriteTemp(tmp, bytes));
        FileRetry.Run(() =>
        {
            ThrowInjected(DurableWritePhase.BeforeMove, path);
            File.Move(tmp, path, overwrite: true);
        });
    }
    catch (Exception ex)
    {
        try { File.Delete(tmp); } catch { }
        throw new DurableWriteException(operation, path, ex);
    }
}
```

Define `WriteTemp(string path, byte[] bytes)` to invoke `ThrowInjected(BeforeWrite, path)`, write `bytes`, invoke `ThrowInjected(BeforeFlush, path)`, and flush. Define `ThrowInjected` to throw only a non-null result from the async-local delegate. `AtomicWriteAllText` must call the shared method with `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content)`; `AtomicWriteAllBytes` must pass the original span without text conversion. Document the file-only fsync limit in the class summary. Do not create a directory.

Replace `WriteSidecarCore`'s local temp/write/move block with `DurableFile.AtomicWriteAllText(metaPath, json)`. Replace `FileMemoryStore.AtomicWriteAllText` with the one-line forwarding shim `=> DurableFile.AtomicWriteAllText(path, content);`. Replace `AtomicWriteAllTextAsync` with a cancellation check followed by the same primitive, so `WriteArtifactAsync` retains its cancellation boundary but all existing artifact text writes use the one implementation.

- [ ] **Step 4: Run focused tests and the affected store tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~DurableFileTests|FullyQualifiedName~SidecarVersioningTests|FullyQualifiedName~TransactionalRestoreTests"`

Expected: all pass; direct failure tests prove old bytes survive on cold disk and the failure’s temporary sibling is removed.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/DurableFile.cs src/Scrinia.Core/DurableWriteException.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/DurableFileTests.cs
git commit -m "feat(core): centralize flushed atomic file replacement"
```

---

### Task 2: Keep artifact admission durable and cold-disk verified

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1039-1144,2827-2929`
- Modify: `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs`
- Modify: `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs`

**Interfaces:**
- Consumes `DurableFile.AtomicWriteAllText` from Task 1.
- Preserves `FileMemoryStore.InvokeWriteSidecarForTest(ArtifactEntry, string)` and `InvokeWriteSidecarsForTest(List<ArtifactEntry>, string)`.
- Produces no new public API.

- [ ] **Step 1: Add failing cold-disk admission tests**

Append three tests to `SidecarVersioningTests.cs`:

1. Seed an artifact body and sidecar, stamp the sidecar at `DurableFormats.ArtifactSidecar.Current + 1`, record body and sidecar bytes plus `versions/` count, call `StoreAsync` to overwrite, expect `ScopeReadOnlyException`, construct a new `FileMemoryStore(scope.WorkspaceDir)`, and assert all recorded bytes and archive count are unchanged.
2. Call `InvokeWriteSidecarForTest` with an entry whose `SchemaVersion` is current + 1 over an existing sentinel `.meta.json`; assert the sentinel bytes survive.
3. Call `ImportTopicEntries` with one writable and one forward-versioned artifact entry; after its refusal, cold-reopen and assert neither incoming `.nmp2` nor `.meta.json` exists.

Change `TransactionalRestoreTests.Import_ThatFailsPartWayThroughTheCommitLoop_LeavesEarlierEntriesConsistent` so the existing `cold` store is also used to read the raw `alpha.nmp2` and `alpha.meta.json` bytes before semantic assertions. This makes the per-entry prefix assertion evidence of durable disk state, not cache state.

- [ ] **Step 2: Run tests and confirm the new admission tests pass without relaxing a gate**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SidecarVersioningTests|FullyQualifiedName~Import_ThatFailsPartWayThroughTheCommitLoop"`

Expected: all pass. The direct gate tests prove forward versions still refuse before any selected mutation; Task 1’s direct writer tests are the red-green proof for durable I/O wrapping.

- [ ] **Step 3: Preserve gate ordering and add the required limits comment**

Keep the `WriteSidecars` whole-list check before its loop, the `SaveIndexUnsafe` check before its orphan sweep, and `ImportTopicEntries`’ check over `staged.Concat(existingEntries)` before its first move. Do not move any of these into `DurableFile`; the primitive is I/O-only and cannot decide format admission.

Update the `ImportTopicEntries` commit-loop comment to state exactly: each body and each sidecar is individually atomic and flushed; the body-then-sidecar sequence is only prefix-consistent; no journal or recovery is added here. Do not claim an import is transactional.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Formats.SidecarVersioningTests|FullyQualifiedName~Store.TransactionalRestoreTests"`

Expected: all pass, including direct-gate tests and cold reopen assertions.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs
git commit -m "test(store): pin durable sidecar admission on cold disk"
```

---

### Task 3: Atomically write skill and agent body-sidecar pairs after their gates

**Files:**
- Create: `src/Scrinia.Core/Bundles/FileEntityMeta.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs:14-58,195-225,303-345`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:140-198`
- Modify: `src/Scrinia.Mcp/MemoryTools.cs:168-203`
- Modify: `src/Scrinia.Mcp/McpResponse.cs:10-33`
- Modify: `tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs`

**Interfaces:**
- Produces Core-owned `public record SkillFileMeta(...)`, `public record AgentFileMeta(...)`, and `public partial class FileEntityJsonContext : JsonSerializerContext` in `Scrinia.Core.Bundles`.
- `SkillFileMeta` and `AgentFileMeta` retain `[JsonPropertyName("v")] int? SchemaVersion` and mutable `[JsonExtensionData] Dictionary<string, JsonElement>? Unknown`.
- `WriteSidecarMeta<T>` consumes Core record types and `JsonTypeInfo<T>`; it writes through `DurableFile.AtomicWriteAllText`.
- Produces `ErrorCodes.DurableWriteFailed = "DURABLE_WRITE_FAILED"` and a `MemoryRemember` facade that maps `DurableWriteException` and `ScopeReadOnlyException` from direct skill/agent writes to that code before rendering YAML. Internal skill/agent methods continue to throw so this facade remains the sole public-response policy.

- [ ] **Step 1: Write failing skill and agent durable-write tests**

In `RegisteredFormatsTests.cs`, add two async tests. Each must create a skill or agent through `MemoryRemember`, capture body, sidecar, and `versions/` count, set `DurableFile.FailureInjector` to return `new UnauthorizedAccessException("sidecar move denied")` only for `BeforeMove` when the destination ends in `.meta.json`, then rewrite through the same MCP path.

Parse the `MemoryRemember` YAML and assert `ErrorCode == ErrorCodes.DurableWriteFailed`; then cold-reopen the workspace and assert the replacement body is complete new UTF-8 text while the prior sidecar bytes are unchanged. For the successful control, rewrite a current-version skill and agent and assert both body and sidecar exist. The test comment must state that this complete-new-body/old-sidecar result is the accepted two-file prefix window, not a paired rollback claim.

Also expand the existing newer-sidecar refusal tests to parse `MemoryRemember`’s `DURABLE_WRITE_FAILED` envelope and assert raw sidecar bytes as well as body bytes and archive counts; they must no longer expect an exception from the public tool facade.

- [ ] **Step 2: Run tests and confirm they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RegisteredFormatsTests"`

Expected: build errors for `FileEntityJsonContext`, or failure because `File.WriteAllText` does not wrap a durable failure.

- [ ] **Step 3: Move the serializable file-entity records into Core**

Move the two record declarations and their source-generation attributes from `MemoryTools.Skills.cs` to `FileEntityMeta.cs`; register both types in `FileEntityJsonContext`. In `ScriniaMcpJsonContext`, retain `[JsonSerializable(typeof(SkillFileMeta))]` and `[JsonSerializable(typeof(AgentFileMeta))` using the imported Core types, so MCP parameter/response serialization remains trim-safe. Do not create duplicate MCP and Core records.

Change `WriteSidecarMeta`’s final two lines to:

```csharp
string metaPath = Path.ChangeExtension(filePath, ".meta.json");
string json = JsonSerializer.Serialize(meta, typeInfo);
DurableFile.AtomicWriteAllText(metaPath, json);
```

Replace both body writers with the synchronous primitive after their existing preflight and archive calls:

```csharp
cancellationToken.ThrowIfCancellationRequested();
DurableFile.AtomicWriteAllText(filePath, promptContent);
```

and:

```csharp
cancellationToken.ThrowIfCancellationRequested();
DurableFile.AtomicWriteAllText(filePath, agentContent);
```

Add comments at these two call sites stating that preflight prevents unsupported-sidecar mutation before the first side effect, while the `.md` and `.meta.json` replacements remain two individually atomic files and are not a paired transaction.

In `McpResponse.cs`, add:

```csharp
public const string DurableWriteFailed = "DURABLE_WRITE_FAILED";
```

Wrap the two `await` points in `MemoryRemember` (the skill route and direct `Remember` route) in catches for `DurableWriteException` and `ScopeReadOnlyException`, returning:

```csharp
ResponseBuilder.Error(
    ex.Message,
    ErrorCodes.DurableWriteFailed,
    "Check permissions, free disk space if needed, and retry after upgrading scri when the existing sidecar is newer.").ToYaml()
```

Do not catch either error inside `SkillCreate` or the agent branch. This mapping belongs in Task 3 because its tests assert the public `MemoryRemember` response; it must not wait for Task 5.

- [ ] **Step 4: Run skill/agent format tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RegisteredFormatsTests|FullyQualifiedName~SkillPathRoutingTests"`

Expected: all pass. Existing unknown-field and schema-version carry-forward tests remain green; the new tests report the `DURABLE_WRITE_FAILED` public envelope and cold-disk prior bytes. Task 1’s direct writer tests prove that the underlying injected failure is retained as a `DurableWriteException.InnerException`.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Bundles/FileEntityMeta.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.cs src/Scrinia.Mcp/McpResponse.cs tests/Scrinia.Tests/Formats/RegisteredFormatsTests.cs
git commit -m "fix(file-entities): atomically replace gated skill and agent files"
```

---

### Task 4: Preflight bundle file entities before topic mutation

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs:155-481,556-633`
- Modify: `tests/Scrinia.Tests/BundleV2Tests.cs`
- Modify: `tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs`
- Modify: `tests/Scrinia.Tests/Formats/BundleFormatGateTests.cs`

**Interfaces:**
- Consumes `FileEntityJsonContext`, `SkillFileMeta`, `AgentFileMeta`, `DurableFormats`, and `DurableFile.AtomicWriteAllBytes`.
- Produces private immutable `FileEntityWrite(string Category, string FileName, string Destination, byte[] Bytes)`.
- Produces `private static (ImmutableArray<FileEntityWrite> Writes, List<string> SkippedCategories) BuildFileEntityWrites(ZipArchive zip, JsonElement manifestRoot, IMemoryStore store)` and `private static void WriteFileEntities(ImmutableArray<FileEntityWrite> writes)`.

- [ ] **Step 1: Add failing all-before-any-write bundle tests**

In `BundleV2Tests.cs`, build one v2 archive containing a valid topic/body plus a declared skill body and sidecar whose `v` is `DurableFormats.SkillMeta.Current + 1`. Seed the target’s topic artifact, skill `.md`, and `.meta.json` with distinct sentinel bytes. Import must throw `InvalidOperationException`; a fresh store must still load the original topic, and all three raw sentinel files must be byte-identical.

Add a parallel agent case with malformed `.meta.json` (`"{ not json"`) and a case whose manifest declares `planner.md` but omits `files/skills/planner.md`. Each must refuse before topic, `.md`, or `.meta.json` mutation. Add a byte-fidelity test containing `byte[] { 0, 255, 10, 13 }` in a permitted `.md` entry and assert the imported bytes exactly equal the zip bytes.

In `BundleImportCategoryAllowlistTests.cs`, retain the `workflows` skip test, but change the known-category traversal test to expect `InvalidOperationException` and assert a valid sentinel topic/file was not changed. Add assertions that a known category rejects empty names, rooted names, separators, `..`, unsupported extensions, and a destination failing `PathContainment.IsWithin`.

In `Formats/BundleFormatGateTests.cs`, add a binding test that creates a v2 archive with one otherwise-valid topic and a declared skill `.meta.json` whose `v` is `DurableFormats.SkillMeta.Current + 1`. Assert `ImportTopicsFromZip` throws `InvalidOperationException` with the category, filename, and re-export/upgrade recovery text, then assert the topic scope remains empty and no skill body or sidecar exists. The test must describe why it is binding: deleting the `BuildFileEntityWrites` call before `store.ImportTopicEntries` makes the topic import and therefore makes this test fail. This supplements the broader sentinel cases in `BundleV2Tests` by pinning the required preflight-to-topic-write ordering at the `BundleFormatService` gate.

- [ ] **Step 2: Run bundle tests and confirm current behaviour fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleV2Tests|FullyQualifiedName~BundleImportCategoryAllowlistTests|FullyQualifiedName~BundleFormatGateTests"`

Expected: forward/malformed/missing file entities currently import topics first or silently skip malformed records; byte-fidelity cannot be guaranteed by the `File.Create` copy loop.

- [ ] **Step 3: Build and validate the complete file-entity set**

Replace `ImportFileEntities` with a build pass that does not call `Directory.CreateDirectory` or write a destination. For every manifest property:

```csharp
if (!CategoryToKind.ContainsKey(category))
{
    skippedCategories.Add(category);
    continue;
}
if (categoryProp.Value.ValueKind != JsonValueKind.Array)
    throw new InvalidOperationException($"Invalid bundle — file category '{category}' must be an array.");
```

For every known record, reject a null/empty/non-plain name, unsupported extension, missing zip member, or destination outside `CategoryDir(store, category)` with an `InvalidOperationException` naming category and filename. Permit only `.md` and same-stem `.meta.json`; collect entry bytes with `CopyTo` into a `MemoryStream`.

For each `.meta.json`, deserialize its bytes through the category’s typed `FileEntityJsonContext` contract and reject JSON errors or a `FormatVersion.Evaluate(... ) == FormatDisposition.LoadReadOnly` with text instructing the caller to re-export or upgrade. Do not reserialize accepted sidecars: retain the original `byte[]` in `FileEntityWrite`, preserving unknown fields byte-for-byte.

At `ImportTopicsFromZip` line 465, call `BuildFileEntityWrites` after topic build/validation but before the first `store.ImportTopicEntries` call. Only after all topic and file-entity work is approved, execute the existing topic write loop, then call `WriteFileEntities`. That method creates only the directories required by approved writes and uses `DurableFile.AtomicWriteAllBytes(write.Destination, write.Bytes)` once per record.

Add a method comment stating that this is deliberately a prefix-consistent sequence: each selected file is complete, but a crash/failure can apply an approved prefix and leave an untouched suffix; journaled restore recovery is deferred.

- [ ] **Step 4: Run focused bundle tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleV2Tests|FullyQualifiedName~BundleImportCategoryAllowlistTests|FullyQualifiedName~BundleFormatGateTests|FullyQualifiedName~BundleRoundTripTotalityTests"`

Expected: all pass. v1 no-file-entity imports still succeed, unknown categories are returned in `SkippedCategories`, and every invalid known record refuses before any topic or file side effect.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Bundles/BundleFormatService.cs tests/Scrinia.Tests/BundleV2Tests.cs tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs tests/Scrinia.Tests/Formats/BundleFormatGateTests.cs
git commit -m "fix(bundle): preflight and atomically import file entities"
```

---

### Task 5: Return structured import and durable-write failures through MCP and CLI

**Files:**
- Modify: `src/Scrinia.Mcp/McpResponse.cs:10-33`
- Modify: `src/Scrinia.Mcp/MemoryTools.Bundling.cs:103-135`
- Modify: `tests/Scrinia.Tests/BundleExitCodeTests.cs`
- Modify: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`

**Interfaces:**
- Consumes `ErrorCodes.DurableWriteFailed` from Task 3 and produces `ErrorCodes.InvalidBundle`.
- `ScriniaMcpTools.Import` maps `InvalidOperationException` from bundle content to `INVALID_BUNDLE`, `ScopeReadOnlyException` to `DURABLE_WRITE_FAILED` for an existing workspace write and `INVALID_BUNDLE` for bundle-proposed metadata, and `DurableWriteException`/zip I/O failures to `DURABLE_WRITE_FAILED`.
- `ScriniaCommands.Import` remains unchanged in policy: it renders the envelope/JSON error and returns 1 when `response.IsError`.

- [ ] **Step 1: Write failing response and CLI tests**

Add one `ScriniaMcpToolsTests` test that imports a manifest declaring a missing file entity and asserts `status: error`, `ErrorCode == ErrorCodes.InvalidBundle`, and recovery text contains both `re-export` and `upgrade`. Add a test with `DurableFile.FailureInjector` throwing before a file-entity move; assert `ErrorCode == ErrorCodes.DurableWriteFailed`, error text names only the destination path and offers permissions/disk-space/retry guidance, and never returns a success message.

In `BundleExitCodeTests.cs`, capture JSON for each of those two inputs through `new ScriniaCommands().Import(..., json: true)`, assert exit code 1, and parse the embedded YAML in `CliImportOutput.Message` to assert the same error code as MCP. This proves the CLI did not grow divergent import policy.

- [ ] **Step 2: Run response tests and confirm they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleExitCodeTests|FullyQualifiedName~ScriniaMcpToolsTests"`

Expected: malformed bundle content currently returns `INTERNAL`; durable I/O is uncaught or rendered as an unstructured failure.

- [ ] **Step 3: Add the invalid-bundle code and narrow exception mapping**

Add the constants:

```csharp
public const string InvalidBundle = "INVALID_BUNDLE";
```

In `ScriniaMcpTools.Import`, replace the single `catch (InvalidOperationException)` with ordered catches: first `DurableWriteException`, then `ScopeReadOnlyException`, then `InvalidOperationException`, then `UnauthorizedAccessException`, then `IOException` (which also covers `InvalidDataException` from archive reading). Use `ResponseBuilder.Error` with the stated codes and actionable recovery text. Do not catch `Exception` and do not map these expected conditions to `INTERNAL`.

Classify a `ScopeReadOnlyException` as `INVALID_BUNDLE` only when it arises while validating an incoming file-entity sidecar; preserve its workspace-write classification as `DURABLE_WRITE_FAILED`. Make that distinction explicit by having `BuildFileEntityWrites` convert an incoming-forward-version refusal to `InvalidOperationException` with category/file/re-export text, leaving direct workspace preflight as `ScopeReadOnlyException`.

- [ ] **Step 4: Run focused structured-error tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~BundleExitCodeTests|FullyQualifiedName~ScriniaMcpToolsTests"`

Expected: all pass; MCP and CLI report matching stable codes and both CLI paths exit 1.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/McpResponse.cs src/Scrinia.Mcp/MemoryTools.Bundling.cs tests/Scrinia.Tests/BundleExitCodeTests.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -m "fix(bundle): classify durable import failures"
```

---

### Task 6: Document the bounded behaviour and complete verification

**Files:**
- Modify: `CHANGELOG.md`
- Verify: all files in Tasks 1–5

**Interfaces:**
- Consumes the completed writer, preflight, and response contracts.
- Produces a user-facing `[Unreleased]` record; no source interface changes.

- [ ] **Step 1: Add the changelog entry**

Under `## [Unreleased]`, add:

```markdown
### Fixed — durable file replacement and bundle file-entity admission

- Artifact sidecars, skill and agent files, and imported bundle file entities now replace each selected
  file through a flushed sibling temporary file, so readers do not observe a truncated replacement.
- Bundle file entities are validated before topic import begins. Missing, unsafe, malformed, or
  forward-versioned members now fail with `INVALID_BUNDLE`; filesystem write failures report
  `DURABLE_WRITE_FAILED` through both MCP and `scri bundle import`.
- This is per-file durability, not transactional restore: an interruption can leave a complete applied
  prefix of a skill/agent pair or bundle file set. Journaled restore recovery remains future work.
```

- [ ] **Step 2: Run all conformance-focused tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~DurableFileTests|FullyQualifiedName~Formats.SidecarVersioningTests|FullyQualifiedName~Formats.RegisteredFormatsTests|FullyQualifiedName~BundleV2Tests|FullyQualifiedName~BundleFormatGateTests|FullyQualifiedName~BundleImportCategoryAllowlistTests|FullyQualifiedName~BundleExitCodeTests|FullyQualifiedName~Store.TransactionalRestoreTests"`

Expected: 0 failures. Review every cold-disk assertion: it must instantiate a fresh `FileMemoryStore` or directly read raw bytes after the failure.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test Scrinia.sln`

Expected: 0 failures.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): record durable write conformance"
```

---

## Spec Coverage Review

| Design requirement | Plan task |
|---|---|
| Shared UTF-8/byte atomic writer, retry, flush, temp cleanup, no directory creation | 1 |
| File-only fsync limitation and retained reconcile shim | 1 |
| Artifact-sidecar serialization/gates and cold-disk refusal evidence | 2 |
| Skill/agent preflight before archive/body, carry-forward, atomic body/sidecar writes | 3 |
| Core-owned file-entity records/context and no Core → MCP dependency | 3 |
| File-entity build/preflight before topic writes, strict membership/name/version validation | 4 |
| Byte-for-byte accepted file-entity import, unknown-category skips, v1 compatibility | 4 |
| Prefix-consistent multi-file limitation; no journaled recovery | 2, 3, 4, 6 |
| `DURABLE_WRITE_FAILED` for direct skill/agent writes; `INVALID_BUNDLE` / `DURABLE_WRITE_FAILED` for import and common CLI outcome | 3, 5 |
| Text and byte injected failures; cold-disk tests | 1–4, 6 |
| Changelog | 6 |

## Self-Review

- **Spec coverage:** Complete. Every selected writer has a direct `DurableFile` route or retained forwarding shim; every required preflight is before the first named side effect; file-entity validation precedes the topic write pass; and structured outcomes are tested through MCP and the shared CLI handler.
- **Placeholder scan:** Complete. The plan contains no TBD/TODO/fill-in references or “similar to” implementation instructions. Every implementation task names files, interfaces, failing tests, commands, and concrete code or conditions.
- **Type consistency:** Complete. `DurableFile` exposes only the two specified production methods and normalizes every escaping low-level failure to `DurableWriteException`; `DurableWriteException`, `ErrorCodes.DurableWriteFailed`, `FileEntityWrite`, `BuildFileEntityWrites`, and the Core sidecar records are defined before their consuming tasks. Task 3 owns the `MemoryRemember` durable-write response mapping that its tests assert; Task 5 consumes that code for import-only mapping. No journal/recovery interface is introduced.
- **Intentional deferral:** Paired skill/agent transactionality and whole-bundle rollback/recovery are explicitly excluded. The plan preserves per-file atomicity and prefix-consistent ordering only.
