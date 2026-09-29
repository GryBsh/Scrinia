# Durable-write conformance — design

**Date:** 2026-08-24  
**Stream:** Approved, bounded durable-write conformance  
**Status:** binding

## Purpose

Scrinia is a local filesystem store. `scri serve` exposes its MCP tools over local
stdio, while the CLI calls the same internal handlers; plugins are loaded locally by
`WorkspaceSetup.LoadPluginsAsync`. There is no HTTP, REST, remote-store, or deleted
server surface in this stream.

The store's NMP/2 artifact writer is durable enough to be a useful precedent, but
several adjacent durable files still use bare writes. A process crash or power loss
can therefore expose a truncated file, and a rejected file-entity rewrite can still
produce a partial outcome. The goal is deliberately narrower than transactional
restore: make each selected *single file* atomic and flushed before it replaces its
predecessor; make metadata-version refusal happen before any selected write; and
report a durable-write failure consistently through the existing CLI/MCP boundary.

This stream covers only these live durable-write paths:

| Surface | Current live writer | Defect addressed |
|---|---|---|
| Artifact sidecar | `FileMemoryStore.WriteSidecarCore` in `src/Scrinia.Core/FileMemoryStore.cs` | It has a separate temp-and-move implementation whose temporary bytes are not flushed. |
| Skill and agent bodies/sidecars | `ScriniaMcpTools.SkillCreate` in `src/Scrinia.Mcp/MemoryTools.Skills.cs`; `/agent/` branch of `ScriniaMcpTools.Remember` in `src/Scrinia.Mcp/MemoryTools.Core.cs`; `WriteSidecarMeta` in `MemoryTools.Skills.cs` | Bodies and sidecars use bare `File.WriteAllText[Async]`. |
| Bundle file entities | `BundleFormatService.ImportFileEntities` in `src/Scrinia.Core/Bundles/BundleFormatService.cs` | `File.Create(destPath)` truncates the destination before the zip copy completes and has no sidecar/version preflight. |

It does **not** turn all incidental project configuration, hook-installation, export,
or merge-driver output into a new transaction system. Those writers have different
ownership and are outside the selected class.

## Decision 1 — one shared atomic text/byte primitive

Extract the existing durable mechanics behind `FileMemoryStore.WriteAllTextDurable`,
`AtomicWriteAllText`, and `AtomicWriteAllTextAsync` into the internal Core utility
`src/Scrinia.Core/DurableFile.cs`. Its API is exactly:

```csharp
internal static void AtomicWriteAllText(string path, string content);
internal static void AtomicWriteAllBytes(string path, ReadOnlySpan<byte> content);
```

`AtomicWriteAllText` encodes UTF-8 without a BOM; `AtomicWriteAllBytes` accepts the
original bytes unchanged. Both write a unique sibling temporary path
`{destination}.{pid}.{guid:N}.tmp` with `FileShare.None`, call
`FileStream.Flush(flushToDisk: true)`, then use `FileRetry.Run` around the write and
the `File.Move(..., overwrite: true)`. On every failure they make a best-effort
attempt to delete only their own temporary path and rethrow the original failure.
They create no destination directory: directory ownership remains with the named
caller.

The move is atomic for readers because the temporary file is a sibling on the same
volume. The byte variant is required because a bundle entry is arbitrary file
content; decoding a file entity as text would corrupt supported `.json` or `.md`
bytes if its encoding changes, and would make the primitive unsuitable for the next
binary caller.

`FileMemoryStore.AtomicWriteAllText` remains temporarily as an internal forwarding
shim for `ScriniaMcpTools.WriteResolved` in
`src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`; it delegates to the new Core utility.
The selected writers below call the utility directly. This preserves reconcile's
existing cache invalidation contract while eliminating multiple implementations of
the temp-name, retry, cleanup, and flush rule.

### Explicit durability limit

The primitive flushes the **file**, not the containing directory. As
`FileMemoryStore.WriteAllTextDurable` already documents, .NET has no portable
directory fsync and Windows has no equivalent used here. A power loss may lose the
rename and retain the previous file; it must not expose a partially-written selected
file. This is the accepted single-file guarantee, not a claim of filesystem-wide
durability.

## Decision 2 — artifact-sidecar writing uses the same primitive and retains its gate

`FileMemoryStore.WriteSidecarCore` remains the sole artifact-sidecar serialization
and policy chokepoint. It continues to:

1. refuse `ArtifactSidecarPolicy.IsReadOnly(entry)` before serializing or replacing
   anything;
2. normalize legacy `fields` to `attributes` and sort known and unknown fields;
3. serialize with `FileStoreJsonContext.Default.ArtifactEntry`.

Replace only its local `File.WriteAllText(tmp, json)` / `File.Move` block with the
shared text primitive. `WriteSidecars` and `SaveIndexUnsafe` keep their existing
whole-list gate before any sidecar mutation, and `ImportTopicEntries` keeps its
pre-commit check over both staged and existing entries. The inner
`WriteSidecarCore` check remains the defence-in-depth backstop. This is a
consolidation, not a relaxation of the artifact-sidecar version policy declared by
`DurableFormats.ArtifactSidecar` and evaluated by `FormatVersion.Evaluate` in
`src/Scrinia.Core/Formats`.

The test-only seams `InvokeWriteSidecarForTest` and
`InvokeWriteSidecarsForTest` remain; they are the direct evidence that no caller can
bypass the gate merely by avoiding a higher-level operation.

## Decision 3 — skill and agent writes preflight, then write each file atomically

`SkillCreate` and the `/agent/` branch of `Remember` retain their current ordering:
`ReadSidecarMetaOrRefuse` runs before `ArchiveFileVersion` and before the body
write. That preflight is load-bearing: a newer `v` is a refusal, not a late
sidecar-write error after the old body has been archived or replaced.

Both branches then use `DurableFile.AtomicWriteAllText` for the `.md` body and
`WriteSidecarMeta` uses it for the `.meta.json` body. `WriteSidecarMeta` continues
to select `DurableFormats.SkillMeta` or `DurableFormats.AgentMeta` solely from the
metadata type and to reject an unregistered type. It also remains a backstop:
preflight protects the existing destination, while the final gate protects callers
that construct a newer metadata instance directly.

The existing carry-forward is unchanged and required:

- `SchemaVersion` is copied from the existing sidecar, so a rewrite cannot silently
  downgrade a newer format to an absent version.
- `SkillFileMeta.Unknown` and `AgentFileMeta.Unknown` are copied into a new
  dictionary, not aliased. Consequently a current/older sidecar's unknown JSON
  fields survive a later ordinary skill/agent rewrite.
- Reads remain permissive. `ReadSidecarMeta` may load a newer sidecar; only a write
  is refused. This preserves read/search/recovery access to data the current binary
  must not rewrite.

Archives remain best-effort historical copies and are not upgraded into a paired
transaction by this stream.

## Decision 4 — bundle file-entity import is a built, preflighted file set

`BundleFormatService.ImportTopicsFromZip` already separates topic parsing from the
topic write pass. Extend that pattern to file entities: replace
`ImportFileEntities`'s immediate directory creation and `File.Create` loop with a
build/preflight pass that returns an immutable list of approved file writes, followed
by a write pass using `DurableFile.AtomicWriteAllBytes`.

The preflight runs before **any** `ImportTopicEntries` call. It validates every
selected manifest topic as today, then every selected file-entity record, so an
invalid or forward-versioned file entity cannot import topics first and fail later.
Each record must satisfy all of the following:

1. Its category is present in `CategoryToKind` (`skills` → `skill`, `agent` →
   `agent`). Unknown/deleted categories retain the current non-destructive behavior:
   they are not written and are returned in `SkippedCategories`.
2. Its filename is a plain filename accepted by the current containment rule:
   nonempty, not rooted, contains neither separator nor `..`, and its resolved
   destination remains within `CategoryDir(store, category)`.
3. The named zip entry exists. Missing, unsafe, or malformed records are rejected as
   an invalid bundle rather than silently skipped. A manifest that promises a file
   but cannot supply it is not a successful restore.
4. Only the emitted `.md` body and its same-stem `.meta.json` companion are valid
   file-entity members. Other extensions are rejected rather than copied just
   because current export happens to enumerate `*.json` and `*.md`.
5. Every `.meta.json` is parsed with the appropriate typed sidecar contract before
   a write. `v > DurableFormats.SkillMeta.Current` or
   `v > DurableFormats.AgentMeta.Current` refuses the entire bundle with a clear
   upgrade/re-export instruction. A missing `v` means the respective contract's
   `AbsentMeans` value and remains supported.

To make the Core bundle service own this preflight without introducing a Core → MCP
dependency, move the two file-entity metadata records and their source-generated
JSON context into Core alongside the durable-format contracts, then have
`ScriniaMcpTools` use those same types/context. The MCP serializer registrations
remain available through `ScriniaMcpJsonContext`; no reflection serializer is
introduced.

The importer copies approved zip bytes verbatim. It does not deserialize and
reserialize accepted sidecars, so an unknown field in an imported current/older
sidecar survives byte-for-byte. Later `SkillCreate` or agent `Remember` already
preserves those fields through `[JsonExtensionData]`, as specified above. An imported
newer sidecar is rejected rather than preserved in a state this build might later
mistake as writable.

The current bundle manifest version remains 2. This is stricter validation of its
existing file-entity capability, not a layout change. v1 bundles with no
`fileEntities` remain a no-op for this phase.

## Structured failure semantics

Core code exposes three intentional failure classes. Add the two stable codes
`ErrorCodes.InvalidBundle = "INVALID_BUNDLE"` and
`ErrorCodes.DurableWriteFailed = "DURABLE_WRITE_FAILED"` in
`src/Scrinia.Mcp/McpResponse.cs`; do not overload `INTERNAL` for an expected input
refusal or an actionable filesystem failure.

| Condition | Core outcome | MCP / CLI outcome |
|---|---|---|
| Unsafe, missing, malformed, or unsupported-version bundle content | `InvalidOperationException` whose message identifies the category/file or topic/entry and says whether to re-export or upgrade | `ScriniaMcpTools.Import` converts it to `ResponseBuilder.Error(..., ErrorCodes.InvalidBundle, ...)`; `ScriniaCommands.Import` preserves the envelope/JSON error and exits 1. |
| Existing or proposed skill/agent/artifact sidecar is newer than this build | `ScopeReadOnlyException`, with scope/path, subject, declared version, and supported version | The direct write handlers convert it to `ResponseBuilder.Error(..., ErrorCodes.InvalidBundle, ...)` for bundle input and `ResponseBuilder.Error(..., ErrorCodes.DurableWriteFailed, ...)` for a write to an existing workspace entity. CLI and MCP report the same actionable text and exit 1. |
| I/O, retry exhaustion, flush, move, or zip-read failure | The original `IOException`, `UnauthorizedAccessException`, or zip exception is retained as the inner cause of `DurableWriteException` in new `src/Scrinia.Core/DurableWriteException.cs`, carrying operation and destination | The facade returns `ResponseBuilder.Error(..., ErrorCodes.DurableWriteFailed, ...)` with an operation-safe path, retry/permissions/disk-space instruction, and exit 1. No partial success is rendered. |

`ScriniaMcpTools.Import` currently catches only `InvalidOperationException` and
labels it `ErrorCodes.Internal`. The implementation must map the declared classes to
the codes above; it must not classify caller-correctable input or a forward-version
refusal as an internal defect. The CLI continues to call the same internal `Import` handler in
`src/Scrinia/Commands/ScriniaCommands.Bundle.cs`, so no separate CLI policy is
permitted. Plugins receive no new protocol, event, or remote transport behavior.

## Cold-disk failure tests

Every failure test must dispose/reopen `FileMemoryStore` or construct a new store
over the same workspace before asserting durable state. `LoadIndex` immediately
after a successful operation may be served by `_indexCache` and is not proof that
the disk is correct.

| Test location | Required proof |
|---|---|
| New `tests/Scrinia.Tests/DurableFileTests.cs` | Force a failure before rename for text and byte writes. The previous destination bytes survive; the temporary sibling is removed best-effort; a cold reopen reads the prior valid sidecar/artifact. |
| `tests/Scrinia.Tests/Formats/SidecarVersioningTests.cs` and `RegisteredFormatsTests.cs` | A newer artifact, skill, and agent sidecar refuses before archive/body/sidecar mutation. Assert original bytes and archive count, not merely that an exception was thrown. |
| `tests/Scrinia.Tests/BundleV2Tests.cs` and `BundleFormatGateTests.cs` | A newer skill/agent sidecar, a missing declared zip member, and malformed sidecar JSON reject before any topic or file-entity destination is created or replaced. Reopen cold and assert all sentinel topic, `.md`, and `.meta.json` bytes survive. |
| `tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs` | Unknown categories remain skipped/reported and write nothing; known unsafe file names now fail as invalid bundles before topic/file writes. |
| `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs` | Retain the existing mid-commit fault test and make its assertions cold-disk assertions. It must prove every committed prefix entry has matching body and sidecar. |

The failure injection belongs at the shared primitive's write/flush/move seam and is
test-only. Tests must cover both text and byte callers; a text-only injector proves
neither bundle import nor byte preservation.

## Accepted limitations

This stream promises **atomicity and flush per file**, plus a
**prefix-consistent** sequence when an operation changes several files:

- A skill or agent body and sidecar are still two files. A crash between their
  individual atomic replacements can leave one new file and one old file.
- Bundle file entities commit one approved file at a time. A failure can leave an
  applied prefix and untouched suffix. Each selected file in the prefix is complete;
  the bundle is not all-or-nothing.
- Topic import retains its established per-entry ordering in
  `FileMemoryStore.ImportTopicEntries`: body rename followed immediately by that
  entry's sidecar. It is prefix-consistent per entry, not a whole-bundle transaction.
- Directory-entry persistence is outside the portable .NET capability used here, as
  stated under Decision 1.

These limits are accepted because they are explicit, bounded, and strictly safer
than replacing a live file in place. They must be documented in code comments at the
shared primitive and at each multi-file caller; no response may describe an import
or file-entity rewrite as fully transactional.

## Non-goals and later handoff

This stream does **not** add a journal, replay log, rollback copy set, directory
swap, multi-scope lock protocol, or startup recovery pass. In particular, it does
not repair the residual two-file window for skill/agent pairs or restore a partially
applied bundle after a crash.

The handoff boundary for the later **journaled restore recovery** stream is precise:
it starts after this stream's `DurableFile` primitive and complete preflight are in
place. That stream may add a versioned intent record before the first multi-file
commit, make restoration roll forward or roll back after a cold start, and define
the recovery response. It must not be smuggled into this bounded conformance work.
Its design must reuse `DurableFormats`, declare a new format contract before writing
the first journal byte, and state recovery behavior for a journal written by a newer
build.

## Completion rule

The stream is complete only when every selected live writer routes through the
shared primitive, every listed preflight runs before its first side effect, the
artifact and file-entity version gates remain defence in depth, all failure cases
produce the stated structured outcome through both local CLI and MCP handlers, and
every newly found selected writer is included with a test or explicitly shown to be outside the three-surface table;
"the CLI calls the handler" or "this file is usually text" is not an exemption.
