# Journaled Restore Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an interrupted bundle restore converge each affected persistent topic scope to its durable incoming body and sidecar bytes before that scope can be read or written again.

**Architecture:** `FileMemoryStore` owns one versioned, source-generated `.restore-journal.json` per persistent topic scope. Import constructs final normalized entries and immutable body/sidecar byte payloads, durably commits `prepared`, then idempotently replays, verifies, marks `completed`, invalidates derived state, and deletes the journal. A shared-check/exclusive-escalation access barrier invokes that same recovery state machine before every persistent-scope read or write; no bundle-wide transaction, rollback, background sweep, or file-entity transaction is introduced.

**Tech Stack:** .NET 10, C#, `System.Text.Json` source generation, SHA-256, `DurableFile`, xUnit, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-24-journaled-restore-recovery-design.md`

## Global Constraints

- A transaction owns exactly one canonical persistent topic scope; selected bundle topics remain independent and may complete independently.
- Recovery is roll-forward only. A `prepared` journal owns final incoming bytes and hashes; recovery never opens the source ZIP, restores prior bytes, or rolls back.
- Do not journal no-op entries: omit entries skipped by `overwrite: false` and entries without a bundle body.
- The journal is `.restore-journal.json` beside `.lock`; it is private control data and must be ignored by discovery, sidecar enumeration, export, and bundle import.
- Journal JSON is UTF-8 without BOM and all journal writes, state transitions, and replayed destination replacements use `DurableFile.AtomicWriteAllBytes`.
- Version 1 has only `prepared` and `completed`. `prepared` is replayed in full; `completed` verifies destinations then only cleans up. There is no cursor or `applying` state.
- `RestoreJournalCodec.MaxRawNmp2BodyBytes` bounds a decoded body at 96 MiB. `MaxJournalJsonBytes` separately bounds the entire UTF-8 journal before deserialization or Base64 decoding.
- Reject ZIP `.nmp2` members exceeding `MaxRawNmp2BodyBytes` before materializing them (and stop a streaming read that crosses it) as `INVALID_BUNDLE`. Unsupported, malformed, wrong-scope, noncanonical, duplicate, undecodable, oversized, or hash-mismatched retained journals remain byte-for-byte in place and block only their scope. Never quarantine, delete, repair, or overwrite them.
- Journal admission also permits at most 1,000 entries, 96 MiB decoded body bytes per entry, 16 MiB decoded sidecar bytes per entry, and 128 MiB aggregate decoded payload. The full journal has the checked 181,009,068-byte ceiling, and validation proves encoded and derived decoded lengths with checked aggregate accounting before any Base64 decode.
- Every persistent read and write is gated; ephemeral memories remain ungated and other persistent scopes remain available. A lock timeout is a loud contention failure, never a recovery bypass.
- Preserve the existing additive import and stale-`.staging` cleanup rules. Do not create whole-bundle/file-entity transactions, rollback, migration/compact/ordinary-write journals, or a background recovery sweep.
- Use source-generated JSON contexts only; do not introduce reflection serialization or a Core → MCP dependency.
- Crash/recovery assertions must use a fresh `FileMemoryStore` over the same workspace or raw disk bytes, never a warm cache alone.
- Do not commit while executing this plan. Add one concise `[Unreleased]` changelog entry. Keep the approved design and this implementation plan synchronized when an approved correction changes their future-work contract; do not rewrite unrelated historical artifacts.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/Scrinia.Core/Formats/DurableFormats.cs` | Registers the restore-journal durable-format contract. | Modify |
| `src/Scrinia.Core/RestoreJournal.cs` | Immutable journal DTOs, source-generated JSON context, checked size limits, validation, hashes, and recovery-blocked exception. | Create |
| `src/Scrinia.Core/FileMemoryStore.cs` | Builds journals, applies/recover journals under the existing scope locks, gates reads/writes, invalidates caches/facts, and retains `.staging` cleanup. | Modify |
| `src/Scrinia.Core/IMemoryStore.cs` | Carries raw bundle artifact bytes into the scope-local restore transaction. | Modify |
| `tests/Scrinia.TestDoubles/FakeMemoryStore.cs` | Keeps the only test-double implementation conformant with the raw-byte import signature. | Modify |
| `src/Scrinia.Core/Bundles/BundleFormatService.cs` | Retains exact incoming NMP/2 body bytes through the validated topic build pass. | Modify |
| `src/Scrinia.Mcp/McpResponse.cs` | Declares the stable recovery-blocked error code. | Modify |
| `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/MemoryTools.Facts.cs`, `src/Scrinia.Mcp/MemoryTools.Bundling.cs` | Map journal recovery failures through one narrow helper on every persistent MCP route. | Modify |
| `tests/Scrinia.Tests/Formats/FormatVersionTests.cs` | Pins journal registry membership and version dispositions. | Modify |
| `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs` | Cold-disk state-machine, validation, cache/facts, concurrency, residue, and crash-transition coverage. | Create |
| `tests/Scrinia.Tests/BundleExitCodeTests.cs` | Pins import MCP/CLI recovery-blocked envelope and exit code. | Modify |
| `tests/Scrinia.Tests/McpRecoveryBlockedRouteTests.cs` | Pins `RECOVERY_BLOCKED` coverage for every non-import persistent MCP route and preserves unrelated error classifications. | Create |
| `CHANGELOG.md` | Records per-scope roll-forward recovery and its limits. | Modify |

---

### Task 1: Define and register the durable journal contract

**Files:**
- Create: `src/Scrinia.Core/RestoreJournal.cs`
- Modify: `src/Scrinia.Core/Formats/DurableFormats.cs:15-91`, `src/Scrinia.Core/FileMemoryStore.cs:200-212`
- Modify: `tests/Scrinia.Tests/Formats/FormatVersionTests.cs`

**Interfaces:**
- Produces `internal enum RestoreJournalState { Prepared, Completed }` serialized as `prepared` / `completed`.
- Produces `internal sealed record RestoreJournal(string Format, int Version, RestoreJournalState State, string TransactionId, string Scope, IReadOnlyList<RestoreJournalEntry> Entries)` and `internal sealed record RestoreJournalEntry(string Name, string BodyFile, string BodyBase64, string BodySha256, string SidecarFile, string SidecarBase64, string SidecarSha256)`.
- Produces `internal static class RestoreJournalCodec` with `CreatePrepared(string scope, IReadOnlyList<RestoreJournalPayload> entries)`, `ParseAndValidate(ReadOnlySpan<byte> json, string expectedScope, string journalPath)`, `Serialize(RestoreJournal journal)`, and `WithCompleted(RestoreJournal journal)`.
- Produces `internal sealed class RestoreRecoveryException : IOException` with `Scope`, `JournalPath`, and `RestoreRecoveryFailure Failure`, where `Failure` is `UnsupportedVersion`, `MalformedOrIntegrityFailed`, or `ReplayIoFailed`.
- Produces runtime-evaluated `internal static long MaxRawNmp2BodyBytes` and `MaxJournalJsonBytes` accessors (not values frozen during type initialization). Compute the first with checked arithmetic from the live `Nmp2Strategy.MaxDecodedBytes`, `BrotliEncoder.GetMaxCompressedLength`, unpadded Base64Url characters, 76-character wrapping newlines, and fixed NMP/2 header/footer overhead; compute and validate the complete-document limit with checked arithmetic too.
- Adds `DurableFormats.RestoreJournal = new("restore-journal", Current: 1, AbsentMeans: null, FormatDisposition.Refuse, FormatDisposition.Refuse)` and includes it exactly once in `DurableFormats.All`.

- [ ] **Step 1: Write the failing durable-format and codec tests**

Create tests that assert `DurableFormats.All` contains `RestoreJournal` once, has `Current == 1`, `AbsentMeans == null`, and refuses both version `0`/absent and version `2`. Add source-generation serialization coverage that creates a one-entry prepared journal, serializes it, parses it through `RestoreJournalCodec`, and asserts exact decoded body and sidecar bytes plus lowercase 64-character SHA-256 values.

Add table-driven invalid cases: non-object JSON, absent/foreign `format`, version 0/2, unknown state, empty entries, wrong scope, `../x.nmp2`, rooted paths, `x/y.nmp2`, noncanonical filenames, duplicate names/files ignoring case, invalid base64, uppercase/non-hex digest, payload-hash mismatch, oversized encoded body/sidecar, aggregate decoded payload over 128 MiB, checked arithmetic overflow, and a document exceeding `MaxJournalJsonBytes`. Each must throw `RestoreRecoveryException` with `Failure == MalformedOrIntegrityFailed` except version 2, which must be `UnsupportedVersion`. Assert every unsupported, malformed, oversized, or overflowed journal remains byte-for-byte unchanged and a different scope remains accessible; an oversized document must be rejected before JSON deserialization or Base64 decode.

- [ ] **Step 2: Run the focused tests and confirm they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FormatVersionTests|FullyQualifiedName~JournaledRestoreRecoveryTests"`

Expected: build failure because the journal contract, DTOs, codec, and exception do not exist.

- [ ] **Step 3: Implement the exact journal model and validation boundary**

In `RestoreJournal.cs`, use `[JsonPropertyName]` for the lower-camel properties shown in the design and register `RestoreJournal`, `RestoreJournalEntry`, and `RestoreJournalState` in a new `[JsonSerializable] internal partial class RestoreJournalJsonContext : JsonSerializerContext`. `Serialize` must use `RestoreJournalJsonContext.Default.RestoreJournal`, UTF-8 without BOM, and must reject any object that `ParseAndValidate` would reject before it reaches disk.

Define the payload input consumed by import as:

```csharp
internal sealed record RestoreJournalPayload(
    ArtifactEntry Entry, byte[] BodyBytes, byte[] SidecarBytes);
```

`CreatePrepared` must generate `transactionId` with `Guid.NewGuid().ToString("N")`, set `Format` to `"scrinia.restore-journal"`, set `Version` from `DurableFormats.RestoreJournal.Current`, and derive each file name exclusively as `SanitizeName(name) + ".nmp2"` and `SanitizeName(name) + ".meta.json"`. It must compute `Convert.ToHexStringLower(SHA256.HashData(bytes))` from the exact in-memory arrays and Base64-encode those same arrays. Before creating a journal, reject more than 1,000 payloads, a body over 96 MiB, a UTF-8 serialized sidecar over 16 MiB, or checked aggregate decoded bytes over 128 MiB. `ParseAndValidate` must first reject JSON larger than `MaxJournalJsonBytes`, then enforce the inverse rules before decoding any payload into a destination write. It must reject a raw body whose decoded Base64 length exceeds `MaxRawNmp2BodyBytes`, bound sidecars, and compare hashes with ordinal, case-sensitive equality after requiring lowercase digest grammar.

It must validate the journal file length against this checked `long` ceiling before reading or parsing it:

```csharp
checked(4L * 1024L + checked(1_000L * (2L * 1024L)) +
        checked((((128L * 1024L * 1024L) + 2L) / 3L) * 4L))
// 181,009,068 bytes
```

After structural validation, it must validate each encoded length and Base64 grammar/padding, derive each exact decoded length without decoding, and use checked `long` per-entry and aggregate accounting before any Base64 decode. It must then enforce the 96 MiB body, 16 MiB sidecar, and 128 MiB aggregate limits, decode, defensively recheck lengths, and compare hashes.

Add the `DurableFormats.RestoreJournal` declaration and registry entry. Do not put journal DTOs in `FileStoreJsonContext`: journal serialization has its own source-generated context and no artifact sidecar behavior.

- [ ] **Step 4: Run focused codec/format tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FormatVersionTests|FullyQualifiedName~JournaledRestoreRecoveryTests"`

Expected: PASS; every invalid input is classified without touching a live artifact.

---

### Task 2: Admit bounded raw bundle bytes, preserve transaction-private input, and create `prepared` before live writes

**Depends on:** Task 1 (the codec owns the checked raw-body and complete-journal limits used at ZIP admission and journal construction).

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs:231-481`
- Modify: `src/Scrinia.Core/IMemoryStore.cs:352-356`, `src/Scrinia.Core/FileMemoryStore.cs:2707-2855`
- Modify: `tests/Scrinia.TestDoubles/FakeMemoryStore.cs` (`ImportTopicEntries` implementation)
- Modify: `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs`

**Interfaces:**
- Changes `IMemoryStore.ImportTopicEntries` and `FileMemoryStore.ImportTopicEntries` body input from `Dictionary<string, string>` to `Dictionary<string, byte[]>`; all callers retain raw ZIP bytes.
- Produces `private List<RestoreJournalPayload> BuildRestorePayloadsUnsafe(string topicScope, List<ArtifactEntry> incoming, Dictionary<string, byte[]> bodies, bool overwrite)`.
- Produces `private const string RestoreJournalFileName = ".restore-journal.json"` and `private static string GetRestoreJournalPath(string storeDir)`.

- [ ] **Step 1: Write failing transaction-construction tests**

Add a test importing a topic with: an existing entry skipped under `overwrite: false`, an entry missing its body, and one overwritten entry. Inject a failure immediately after the prepared journal write. Assert the journal contains only the overwritten entry; its decoded body equals the ZIP member bytes, its decoded sidecar equals the one serialized final normalized `ArtifactEntry`, and neither live destination changed.

Add a second test whose ZIP body contains `new byte[] { 0, 255, 10, 13 }`. Interrupt after prepared, delete the ZIP, create a fresh store, and trigger recovery. Assert the restored `.nmp2` bytes exactly equal the journal-decoded byte array. This pins that recovery is independent of text decoding and the source archive.

Add ZIP-admission tests for a member declared larger than `RestoreJournalCodec.MaxRawNmp2BodyBytes` and for a stream that crosses the bound despite unusable/misleading length metadata. Both must return the existing `INVALID_BUNDLE` classification before full member allocation, NMP/2 decoding, journal creation, or destination mutation.

- [ ] **Step 2: Run the focused tests and confirm they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~JournaledRestoreRecoveryTests`

Expected: FAIL because bundle import retains strings, no journal exists, and a failure after staging can still leave only a prefix.

- [ ] **Step 3: Build private immutable transaction input, then durably write `prepared`**

In `BundleFormatService.ImportTopicsFromZip`, replace `Dictionary<string, string> ArtifactContents` with `Dictionary<string, byte[]> ArtifactBytes`; there is no existing bounded raw-body helper, so change `ReadZipEntryBytes` (or add a specifically named sibling) to accept `RestoreJournalCodec.MaxRawNmp2BodyBytes`, preflight `ZipArchiveEntry.Length`, and count bytes while copying. Decode a temporary UTF-8 string only for the current optional keyword-derivation branch; never substitute its encoded bytes for the retained raw member array. Pass the byte dictionary to `ImportTopicEntries`.

Change the `IMemoryStore.ImportTopicEntries` declaration and `FakeMemoryStore` implementation to the same `Dictionary<string, byte[]>` parameter. The fake must retain arrays without converting them through text so Core bundle tests exercise the production byte contract.

Inside `ImportTopicEntriesCore`, after the existing exclusive lock, stale-staging sweep, cold index load, overwrite/no-body filtering, URI/CreatedAt normalization, and whole-list read-only-sidecar preflight, call `BuildRestorePayloadsUnsafe`. Serialize each sidecar once with `FileStoreJsonContext.Default.ArtifactEntry`; do not route through `WriteSidecarCore`, and do not regenerate it during recovery. If the payload list is empty, return without creating a journal or publishing an index.

Write the prepared journal before the first live body or sidecar replacement:

```csharp
var journal = RestoreJournalCodec.CreatePrepared(topicScope, payloads);
byte[] journalBytes = RestoreJournalCodec.Serialize(journal);
DurableFile.AtomicWriteAllBytes(GetRestoreJournalPath(storeDir), journalBytes);
ApplyPreparedJournalUnsafe(journal, storeDir);
```

Delete the old `.staging` body write/rename loop from this import path. Keep `SweepStaleStagingUnsafe` unchanged for pre-existing residue; it is not transaction data and is never interpreted as a journal.

- [ ] **Step 4: Run focused construction tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~BundleRoundTripTotalityTests|FullyQualifiedName~BundleV2Tests"`

Expected: PASS; oversized ZIP members are refused before allocation/decoding, and a prepared journal is complete, owns exact incoming bytes, excludes no-ops, and precedes every live destination mutation.

---

### Task 3: Implement the locked idempotent recovery state machine and crash seam

**Depends on:** Tasks 1–2 (validated, bounded journal payloads and the prepared-journal import boundary).

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:637-775,2707-2880`
- Modify: `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs`

**Interfaces:**
- Produces test-only `internal enum RestoreJournalTransition { BeforePreparedWrite, AfterPreparedWrite, BeforeReplayBody, BeforeReplaySidecar, AfterReplayDestinations, BeforeCompletedWrite, AfterCompletedWrite, BeforeCleanupDelete }` and `internal static AsyncLocal<Func<RestoreJournalTransition, string, Exception?>?>` seam on `FileMemoryStore`; it is not public production API. The seam surrounds calls to `DurableFile.AtomicWriteAllBytes` and `File.Delete`; it does not change `DurableFile`'s production API.
- Produces `private void EnsureScopeRecovered(string scope)`, `private void RecoverScopeUnsafe(string scope, string storeDir)`, `private void ApplyPreparedJournalUnsafe(RestoreJournal journal, string storeDir)`, `private void VerifyJournalDestinationsUnsafe(RestoreJournal journal, string storeDir)`, and `private void InvalidateRecoveredScopeUnsafe(string scope)`.
- `RecoverScopeUnsafe` requires the caller already owns the existing exclusive file lock and `ReaderWriterLockSlim` write lock; it never calls `WithExclusiveLock`, `LoadIndex`, or another lock-taking method.

- [ ] **Step 1: Write failing crash-transition tests**

For a two-entry import, inject one failure at every transition. Assert the following on a fresh store:

| Injection | Required cold-disk result |
|---|---|
| `BeforePreparedWrite` | no journal and no destination change |
| `AfterPreparedWrite`, `BeforeReplayBody` | later read replays both entries after the ZIP has been deleted |
| `BeforeReplaySidecar` for entry 1 | later read replaces that entry’s body and sidecar with matching journal hashes |
| `BeforeReplayBody` for entry 2 | later read safely replays entry 1 and applies entry 2 |
| `BeforeCompletedWrite` | later access replays/verifies and removes the journal |
| `AfterCompletedWrite`, `BeforeCleanupDelete` | later access verifies hashes, deletes the journal, and records no destination replacement |

Use a test observer counting `DurableFile.AtomicWriteAllBytes` destinations or the injection seam to prove the completed-cleanup case does not rewrite a body or sidecar. In every prepared case assert a source ZIP is not reopened.

- [ ] **Step 2: Run the crash tests and confirm they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~JournaledRestoreRecoveryTests`

Expected: FAIL because no state machine distinguishes prepared replay from completed verification/cleanup.

- [ ] **Step 3: Implement apply, verify, complete, and cleanup exactly once by state**

`ApplyPreparedJournalUnsafe` must validate every embedded payload/hash before its first destination write, then for each entry write body first and sidecar second with `DurableFile.AtomicWriteAllBytes`. Afterwards it must reread every destination, hash it, and require equality with the journal. Only then rewrite the journal with `State = Completed` via `DurableFile.AtomicWriteAllBytes`.

`RecoverScopeUnsafe` must parse the complete journal before changing any destination. For `Prepared`, call apply, verify, write completed, invalidate unpublished stale state, then delete the journal. For `Completed`, call only verify then delete. A delete failure after verified completion returns success and leaves the completed journal for the next verification/cleanup attempt. An I/O failure during replay wraps in `RestoreRecoveryException(... ReplayIoFailed ...)` and retains the journal.

Use this state table verbatim in the method XML documentation:

| State | Destination action | Verification | Next journal action |
|---|---|---|---|
| absent | none | none | continue |
| prepared | replay every body then sidecar | hash every destination | atomically write `completed`, invalidate, delete |
| completed | none | hash every destination | invalidate, delete |
| invalid/newer | none | none | retain and throw recovery-blocked |

Do not publish `PublishIndexUnsafe` inside replay: it would regenerate all sidecars and violate journal-private byte ownership. After successful recovery, invalidate the scope cache; the original operation subsequently cold-loads/current-writes through its normal path.

- [ ] **Step 4: Run crash and idempotence tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~JournaledRestoreRecoveryTests`

Expected: PASS; every durable boundary converges by roll-forward, and a completed journal performs verify/cleanup without rewriting destinations.

---

### Task 4: Gate every persistent read and write without recursive locks or stale caches

**Depends on:** Task 3 (the barrier invokes the completed recovery state machine).

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:653-775,1302-1835,1953-2077,2099-2201,2463-2505`
- Modify: `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs`

**Interfaces:**
- `WithExclusiveLock` calls `RecoverScopeUnsafe` after it acquires both existing locks and before its supplied body.
- `EnsureScopeRecovered(scope)` first acquires `FileLock.AcquireShared(GetLockPath(scope))` only if the scope directory exists; it releases immediately if no journal exists, otherwise releases and obtains `WithExclusiveLock(scope, ...)` and rechecks.
- `LoadIndex`, `ReadArtifactAsync`, `ResolveArtifactAsync`, `ListScoped`, `AnyScoped`, `SearchAll`, and `GetEntityFactStore` cannot return a persistent cached value until their relevant scope gates have completed.

- [ ] **Step 1: Write failing cache, facts, direct-read, and concurrency tests**

Add these tests, each hand-writing a valid prepared journal after warming the named cache:

1. Warm `LoadIndex(Scope)`, then call it again and assert returned entries reflect journal metadata/body after recovery rather than the cached index.
2. Warm `ReadArtifactAsync("note", Scope)`, then read again and assert the returned text is the journal body, never the artifact LRU value.
3. Call `ResolveArtifactAsync("notes:note")` and the `file://` artifact route against a journalled scope; both must recover before direct file text is returned.
4. Warm `GetEntityFactStore`, create a journal changing `person:old` to `person:new`, query facts, and assert `person:new` is present, `person:old` absent, and no pre-recovery queued fact write is later published.
5. Hold recovery in one store at `BeforeReplaySidecar`; from another store attempt same-scope read and write. Assert each blocks until release or throws `FileLockTimeoutException`; neither can observe body-new/sidecar-old state. Assert a different topic and an ephemeral memory remain readable.
6. Create a pending journal in a never-written scope directory and assert recovery does not materialize unrelated absent scopes; create no journal and assert ordinary reads still leave a never-written scope unmaterialized.

- [ ] **Step 2: Run tests and confirm current fast paths fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~JournaledRestoreRecoveryTests`

Expected: FAIL because `LoadIndex`, artifact LRU, and facts cache can return before checking a journal.

- [ ] **Step 3: Add the barrier at each actual access route**

At the start of `LoadIndex`, call `EnsureScopeRecovered(scope)` before the in-memory cache check. At the start of `ReadArtifactAsync`, recover before `File.Exists`, tick calculation, and `_artifactCache.TryGet`. In `ResolveArtifactAsync`, parse/resolve the qualified scope and recover before `File.Exists`/`ReadAllTextAsync`; for a `file://` URI, map its contained artifact path to its persistent scope and recover it before reading, while leaving arbitrary non-artifact `.scrinia` files unchanged.

Route every scope write through the existing `WithExclusiveLock` wrapper: `Upsert`, `Remove`, `TryUpdateEntry`, `StoreAsync`, `AppendAsync`, `WriteArtifactAsync`, `DeleteArtifact`, and `ImportTopicEntries` must not add a second recovery call or acquire recursively. Move `WriteArtifactAsync` directory creation and replacement inside `WithExclusiveLock(scope, ...)` so it is a scope write rather than a bypass.

Before returning `_factStore`, `GetEntityFactStore` must call `EnsureScopeRecovered` for every persistent scope in `ResolveReadScopes(null)`. On recovery completion, `InvalidateRecoveredScopeUnsafe` must remove `_indexCache[scope]`, invalidate `_artifactCache` by `$"{scope}|"`, clear `_cachedTopics` for `local:` scopes, and call `InvalidateFactStore(persistentMutation: true)` so generation advances and queued stale facts are discarded. `ListScoped`, `AnyScoped`, and `SearchAll` inherit barriers through `LoadIndex`; retain explicit tests for them rather than assuming that inheritance.

- [ ] **Step 4: Run focused access-barrier tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~IndexCacheWritePathTests|FullyQualifiedName~FactsCache"`

Expected: PASS; cached index, artifact, and facts results are never published before recovery, and same-scope concurrent access blocks or fails loudly.

---

### Task 5: Preserve blocked journals and expose actionable structured errors

**Depends on:** Tasks 3–4 (route tests must exercise real blocked recovery from every persistent access path).

**Files:**
- Modify: `src/Scrinia.Mcp/McpResponse.cs:10-39`
- Modify: `src/Scrinia.Mcp/MemoryTools.cs:126-450`, `src/Scrinia.Mcp/MemoryTools.Facts.cs:18-75`, `src/Scrinia.Mcp/MemoryTools.Bundling.cs:104-171`
- Modify: `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs`, `tests/Scrinia.Tests/BundleExitCodeTests.cs`
- Create: `tests/Scrinia.Tests/McpRecoveryBlockedRouteTests.cs`

**Interfaces:**
- Produces `ErrorCodes.RecoveryBlocked = "RECOVERY_BLOCKED"`.
- Produces one private `RecoveryBlockedResponse(RestoreRecoveryException exception)` helper on `ScriniaMcpTools`. It preserves `INVALID_BUNDLE` for bundle parse/version failures and returns `RECOVERY_BLOCKED` for a durable journal obligation.

- [ ] **Step 1: Write failing blocked-journal tests**

Handcraft journals for newer version, malformed JSON, wrong scope, noncanonical target, duplicate target, base64/hash mismatch, oversized full JSON or encoded payload, aggregate decoded payload over 128 MiB, arithmetic overflow, and completed destination hash mismatch. Exercise `LoadIndex`, `ReadArtifactAsync`, `StoreAsync`, `AppendAsync`, `WriteArtifactAsync`, `ListScoped`, `SearchAll`, and `GetEntityFactStore` for the journalled scope. Assert no destination bytes change, the original journal byte array remains byte-for-byte identical, and another scope remains usable.

Add a route-level matrix using a persistent path/scope that is blocked by each journal class. Assert `RECOVERY_BLOCKED`, the exception's canonical scope/path/copy, and retained journal bytes for **each** public MCP route: `remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `facts`, and `import`. Cover `import` in `BundleExitCodeTests`; cover the other nine in `McpRecoveryBlockedRouteTests`. Add control assertions that a malformed bundle remains `INVALID_BUNDLE`, a `DurableWriteException` remains `DURABLE_WRITE_FAILED`, and an unrelated exception still follows the route's existing classification.

Through `ScriniaMcpTools.Import` and `ScriniaCommands.Import(..., json: true)`, assert exit code `1`, `RECOVERY_BLOCKED`, canonical scope, exact journal path, and the required copy: newer versions say upgrade and retry; malformed/integrity failures say no files changed and journal retained; replay I/O says permissions/free disk space/retry. Do not label any of these `INVALID_BUNDLE`.

- [ ] **Step 2: Run the focused tests and confirm they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~BundleExitCodeTests"`

Expected: FAIL because journal failures escape as generic I/O/internal failures or are classified as bundle errors.

- [ ] **Step 3: Add narrow recovery mapping and immutable-residue behavior**

Add `RecoveryBlocked` to `ErrorCodes`. Add exactly one private `RecoveryBlockedResponse(RestoreRecoveryException exception)` helper to the `ScriniaMcpTools` partial class. It renders `Scope` and `JournalPath` exactly once and is the only recovery-error-to-response conversion. Use these messages, replacing only the scope/path/version values:

```text
Recovery is blocked for topic scope '{scope}': restore journal version {version} is newer than this build supports (1). The journal was left unchanged at '{path}'. Upgrade scri, then retry the operation.
Recovery is blocked for topic scope '{scope}': restore journal '{path}' is malformed or failed integrity verification. No files were changed and the journal was retained. Inspect or restore the journal with a compatible scri build, then retry.
Recovery could not complete for topic scope '{scope}'; the journal remains at '{path}' and access is blocked until retry succeeds. Check permissions and free disk space, then retry.
```

Keep the exception mapping narrow: do not catch `Exception`, do not relabel normal lock contention as recovery success, and do not delete/rename a blocked journal. At the store-facing invocation in each public MCP route, catch only `RestoreRecoveryException` and return `RecoveryBlockedResponse(exception)`: `MemoryRemember`, `MemoryRecall`, `MemorySearch`, `MemoryList`, `MemoryAppend`, `MemoryForget`, `MemoryCompact`, `MemoryLink`, `Facts`, and `Import`. Preserve existing catch ordering by placing this catch before any broader `IOException`/durable-write handling. Do not wrap inline-NMP/2 recall decoding, skill/agent file-entity routing, request validation, or response rendering; those errors retain their current behavior. This makes automatic recovery failure on every persistent read or write structured rather than stale or `INTERNAL`.

- [ ] **Step 4: Run focused structured-error tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~BundleExitCodeTests|FullyQualifiedName~McpRecoveryBlockedRouteTests"`

Expected: PASS; all ten persistent MCP routes use the one recovery helper, failed recovery blocks only its scope and preserves evidence, and unrelated bundle/durable/internal errors retain their prior classifications.

---

### Task 6: Verify residue handling, import boundaries, documentation, and the complete suite

**Depends on:** Tasks 1–5 (final verification covers size admission, recovery, barriers, and all response routes together).

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:2857-2880`
- Modify: `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs`, `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Preserves `ImportStagingSuffix`, `TryDeleteStagingFile`, and `SweepStaleStagingUnsafe` for legacy staging residue only.
- Produces no new runtime interface.

- [ ] **Step 1: Write final boundary and residue tests**

Add tests that verify: a current `.staging` residue is swept only by the existing import cleanup; `.restore-journal.json` is not enumerated as a sidecar, memory, topic, export member, or bundle artifact; multi-topic import can leave topic A completed while a prepared journal in topic B recovers only when B is accessed; and skill/agent file entities preserve their current prefix-consistent import behavior without a journal.

Add a completed-journal delete-failure test: first recovery verifies destinations and leaves the journal when deletion fails; a later access retries only verification/cleanup and removes it. Add a prepared replay I/O failure test that leaves the journal and blocks its scope while a second scope reads successfully.

- [ ] **Step 2: Run the focused boundary tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~TransactionalRestoreTests|FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~BundleV2Tests"`

Expected: PASS; legacy staging cleanup remains intact, journal files are invisible control data, and no test claims whole-bundle/file-entity atomicity or rollback.

- [ ] **Step 3: Add the changelog entry**

Under `## [Unreleased]` in `CHANGELOG.md`, add:

```markdown
### Fixed — interrupted bundle restores recover per topic scope

- Bundle topic restore now records a durable, versioned journal containing the final artifact body and
  sidecar bytes before changing that topic. After an interruption, the next access rolls the affected
  topic forward to those verified bytes before returning data or accepting a write.
- Recovery is scoped to one persistent topic and is demand-driven. Other topics and ephemeral memories
  remain available; imports across multiple topics and skill/agent file entities are not one transaction.
- A malformed, integrity-failed, or newer restore journal is retained and blocks only its topic with
  `RECOVERY_BLOCKED` guidance. Recovery never guesses, rolls back, or rereads the source bundle.
```

- [ ] **Step 4: Run all focused recovery tests and the full suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~JournaledRestoreRecoveryTests|FullyQualifiedName~TransactionalRestoreTests|FullyQualifiedName~FormatVersionTests|FullyQualifiedName~BundleV2Tests|FullyQualifiedName~BundleExitCodeTests|FullyQualifiedName~McpRecoveryBlockedRouteTests"`

Expected: 0 failures; every crash assertion cold-reopens or reads disk and every completed-journal assertion proves no destination rewrite.

Run: `dotnet test Scrinia.sln`

Expected: 0 failures.

- [ ] **Step 5: Review the diff; do not commit**

Confirm the change set contains only the files in this plan plus any necessary generated source artifacts, contains no source ZIP dependency in recovery, and does not alter skill/agent transactions, migration, compaction, ordinary transaction semantics, or rollback behavior. Do not run `git commit`.

---

## Spec Coverage Review

| Design requirement | Plan task |
|---|---|
| Versioned/source-generated durable journal registry | 1 |
| Exact private body/sidecar bytes and hashes, no-op exclusion | 2 |
| `prepared`/`completed` roll-forward state machine and idempotence | 3 |
| Crash injection at every durable transition | 3 |
| Shared-check/exclusive-escalation access barrier | 4 |
| Index/artifact/facts caches and concurrent access | 4 |
| Checked raw NMP/2 body bound, ZIP admission, and full-journal pre-deserialization bound | 1, 2 |
| Newer/malformed/hash-mismatch/oversized blocking residue and all persistent MCP/CLI route copy | 5 |
| Staging residue, journal invisibility, multi-topic/file-entity limits | 6 |
| Changelog and focused/full verification | 6 |
| No whole-bundle/file-entity transaction or rollback | Global Constraints, 2, 6 |

## Self-Review

- **Spec coverage:** Complete. The plan names the registry, checked raw NMP/2 and complete-journal bounds, ZIP admission, journal byte contract, state machine, automatic read/write barrier, cache/facts invalidation, error surface, every crash boundary, concurrency, residue, changelog, and the deliberately limited per-scope roll-forward semantics.
- **Placeholder scan:** Complete. No TBD/TODO, deferred implementation, or “similar to” instruction remains. Each task has exact file paths, named methods/types, red-green commands, and concrete assertions or implementation rules.
- **Type consistency:** Complete. `RestoreJournalPayload` is produced after bounded ZIP admission and before import consumes it; codec, states, checked limits, exception, and format registry are established before replay; recovery helpers are defined before access routes call them; `RECOVERY_BLOCKED` and `RecoveryBlockedResponse(RestoreRecoveryException)` are declared before all ten MCP-route mapping tests consume them.
- **Scope check:** Complete. The plan explicitly excludes rollback, whole-bundle/file-entity atomicity, background sweeping, and journals for ordinary writes, migration, compaction, and ephemeral memory.
- **Blockers:** None. Task 5 enumerates remember, recall, search, list, append, forget, compact, link, facts, and import, so no persistent MCP route can omit the narrow `RestoreRecoveryException` mapping or degrade a blocked recovery to `INTERNAL`.
- **Approved bounds:** Complete. The plan records the exact 181,009,068-byte checked-`long` journal formula, all entry and aggregate payload ceilings, validation order, byte-for-byte blocked-residue rule, and boundary/overflow coverage.
