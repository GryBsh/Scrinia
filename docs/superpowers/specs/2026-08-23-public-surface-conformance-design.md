# Public Surface Conformance — Design

**Date:** 2026-08-23  
**Stream:** Approved H3–H5 public-surface conformance  
**Status:** binding; no implementation item in this stream may be deferred

## Purpose

The local filesystem store is the only memory store. `scri serve` hosts the same
MCP tools over local stdio, and the CLI is a separate caller of their internal
handlers. The optional embeddings plugin is loaded locally by
`WorkspaceSetup.LoadPluginsAsync`.

H3–H5 are one defect shape: a public outcome is computed or validated in one
surface, then another surface invents, omits, or misclassifies it. The remedy is
to make the durable write input and outcome classification authoritative before
either the MCP facade or the CLI renders them.

## Decisions

### H3 — one filesystem-safe write-input boundary

`MemoryWrite` in `src/Scrinia.Core/Models/MemoryWrite.cs` becomes the owner of
the persistent memory-name component limit. It validates in its constructor,
after callers have resolved the address into `Name` and `Scope`, and before
`FileMemoryStore.StoreAsync` or `FileMemoryStore.AppendAsync` can create an
archive, artifact, sidecar, or temporary file.

The limit is **182 characters in the sanitized persistent subject**. The value
is derived from the 255-character filesystem component ceiling and the longest
current durable-write filename, the archive temporary name written by
`FileMemoryStore.ArchiveVersion` at
`src/Scrinia.Core/FileMemoryStore.cs:2621-2645`:

```
{sanitized subject}.{timestamp}.nmp2.{maximum 10-digit process id}.{32-char GUID}.tmp
```

The fixed suffix is 73 characters, leaving 182. This is intentionally stricter
than the current machine's process-id width, so the outcome does not change as
the PID changes. `ArchiveVersion` is the longest current durable write
filename; its timestamp, `.nmp2` extension, PID, GUID, and `.tmp` suffix must
all fit in the same Windows filesystem component.

Ephemeral `~name` writes are exempt: they never create filesystem names. The
check applies to the sanitized leaf subject only, not to a whole display path or
a scope. `MemoryNaming.SanitizeSegment` remains a transformation for names read
from existing disk state; it must not become a rejecting public-input guard.

The constructor reports a dedicated invalid-write exception containing the
stable limit and the offending sanitized length. `ScriniaMcpTools.Remember`
(`MemoryTools.Core.cs:113-231`), `Append` (`MemoryTools.Core.cs:999-1057`), and
`Compact` (`MemoryTools.Lifecycle.cs:1121-1302`) translate that exception to
`ResponseBuilder.Error(..., ErrorCodes.InvalidParameter, ...)`. Those are the
only three `new MemoryWrite(...)` sites in `src/`; their shared translation
makes both public callers receive Scrinia's envelope rather than an uncaught
`IOException`.

The old facade-only `MaxPathLength` check in
`src/Scrinia.Mcp/MemoryTools.cs:74,187-191` is removed. The independent 5 MB
per-content-element guard remains where it is; it is an MCP request-size policy,
not a filesystem-name rule. This stream does not make compact reject an existing
large merged chunk merely because it exceeds a transport limit.

This fixes all six required paths:

| Public operation | MCP facade | CLI entrypoint | Common write construction |
|---|---|---|---|
| remember/store | `MemoryRemember` | `ScriniaCommands.Store` | `Remember` |
| append | `MemoryAppend` | `ScriniaCommands.Append` | `Append` |
| compact | `MemoryCompact` | `ScriniaCommands.Compact` | `Compact` |

The CLI calls the internal methods directly (`ScriniaCommands.Memory.cs:396-420,
551-577`), so moving the guard only to `[McpServerTool]` methods would repeat
the defect. The same 182-character validation therefore applies consistently to
remember, append, and compact in both MCP and CLI. The store is still defence in depth: `StoreAsync` and `AppendAsync`
continue to require a valid `MemoryWrite`; neither gains a second, divergent
path-length calculation.

### H4 — JSON fields must be observed values or absent

`--json` is a public schema. A field is permitted only when the command obtains
that exact value from its operation or calculates it directly from emitted
content. A rendered YAML response may remain in a `message`/`yaml` field for
compatibility, but it is not evidence for unrelated structured fields.

Four current fields are fabricated and are removed, rather than renamed or
retained with a sentinel:

| Current record and fabricated field | Current source | Decision |
|---|---|---|
| `CliReindexOutput.RescuedScopes` | `ScriniaCommands.Setup.cs:675-682` supplies `result.Embedded` | Replace the record with `Embedded`, `Total`, `Skipped`, and `Failed`, populated one-for-one from `ForceReindexAsync`'s result. Retain `Message` only as the human summary already emitted. |
| `CliStoreOutput.ChunkCount` | `ScriniaCommands.Memory.cs:411-417` hard-codes `1` | Remove it. `scri store` currently submits one input chunk, but the JSON contract must not claim a persisted chunk count it did not read from the completed write. Keep the observed canonical `Name`, input `OriginalBytes`, and response message. |
| `CliForgetOutput.Success` | `ScriniaCommands.Memory.cs:500-505` hard-codes `true` | Remove it. Exit status and an error envelope already express success or failure; a constant boolean adds no truthful information. |
| `CliExportOutput.Path` | `ScriniaCommands.Bundle.cs:56-63` supplies `""` | Remove it. Export does not expose the output path as structured data at this call site, so an empty string is a false path, not an optional path. Preserve the response message. |

`CliJsonContext` (`src/Scrinia/Commands/CliJsonContext.cs`) is updated for the
replacement record and the reduced records. No field is added merely to mirror
terminal prose. Existing records whose values are actually computed from the
operation remain unchanged, including `CliShowOutput.Length` and the list/search
arrays. This is a breaking JSON-schema correction and is called out in the
release notes.

### H5 — warnings mean completed-without-the-requested-outcome

`McpResponse.IsWarning` already defines the contract at
`src/Scrinia.Mcp/McpResponse.cs:50-55`: no requested effect or result is not an
error, but it is not success. Every MCP tool uses the following classification.

| Tool | `success` | `warning` | `error` |
|---|---|---|---|
| `guide` | The guide and restored context are returned. | Never; no stored context is information, not a failed guide. | Unexpected failure. |
| `remember` | A memory or file-backed agent/skill is written. | Never. | Invalid input or failed write. |
| `recall` | Requested memory or skill is returned. | Never. | Missing or invalid target. |
| `search` | At least one match is returned. | No matches. | Invalid query or execution failure. |
| `list` | At least one memory is listed. | No memories in the requested scope. | Invalid list request or execution failure. |
| `append` | A chunk is appended or a new memory is created. | Never. | Invalid input or failed write. |
| `forget` | A memory or override is removed. | Never. | Missing or non-removable target. |
| `compact` | At least one chunk is removed and the rewrite completes. | Already one chunk; or `keepRecent >= chunkCount`. | Missing target, invalid input, or failed rewrite. |
| `link` | Both links are persisted. | Never. | Missing target, invalid input, or failed mutation. |
| `restore` | The restore operation completes. | Never; an empty workspace is a valid restored state. | Unexpected failure. |
| `reconcile` | A scan completes or a conflict is resolved. A clean scan is success because the requested diagnostic completed. | Never. | Invalid request, unresolved conflict requiring input, or failed resolution. |
| `facts` | A data result is returned. | Existing disambiguation and not-found cases, through `FactsResponseFormatter`. | Invalid request or execution failure. |

Accordingly, `MemorySearch` changes its zero-match response at
`MemoryTools.Core.cs:897`; `MemoryList` changes its empty response at `:642`;
and `Compact` changes both no-op responses at
`MemoryTools.Lifecycle.cs:1166-1170` to `ResponseBuilder.Warning`, retaining
their current explanatory content and action. `UpdateMeta` and empty bundle
import already return warnings and are not rewritten. `References` is internal,
not one of the twelve MCP tools, so its zero-result status is outside this
stream.

For commands that render an `McpResponse`, exit **0 only for `success`**;
both `warning` and `error` exit **1**. `EmitMcpResult` already implements that
rule (`ScriniaCommands.cs:104-124`) and remains the common CLI/MCP-handler
adapter. `Store`, `Forget`, `Export`, and `Import` keep their equivalent
explicit warning branches. The direct CLI `List` and `Search` commands must
also return 1 for their existing empty outcomes at
`ScriniaCommands.Memory.cs:42-49` and `:257-264`, while preserving their useful
empty JSON arrays and terminal messages. Non-empty results still exit 0.

Warnings do not become errors and do not acquire `errorCode`; scripts can
distinguish an expected no-op from failure through the MCP `status` field or the
CLI's existing structured status where it uses `CliMcpOutput`, while treating
both as a non-success exit for gating.

## Compatibility and release impact

This stream changes public behaviour deliberately:

- Persistent memory subjects longer than 182 sanitized characters fail early
  with `INVALID_PARAMETER`, before any filesystem side effect. Existing readable
  on-disk entries are not renamed, migrated, or rejected merely by listing or
  recalling them.
- Empty search/list and no-op compact outcomes change from `success`/exit 0 to
  `warning`/exit 1. Automation that considered an empty result successful must
  handle the explicit no-result outcome.
- The four fabricated JSON fields are removed; reindex gains four truthful
  counters. Consumers must stop reading `rescuedScopes`, `chunkCount`,
  `success`, and export `path` from the affected command schemas.
- No NMP/2 artifact, sidecar, bundle, plugin protocol, or embedding layout
  changes. The local embeddings plugin observes the same completed store events
  as before; this stream does not alter plugin registration or indexing.

`CHANGELOG.md` receives a `Fixed (breaking)` entry describing the early
filesystem-safe refusal, warning/exit-code correction, and removed fabricated
JSON fields. `docs/cli-reference.md` documents the 182-character persistent
subject limit, warning exit 1, and each revised JSON record. `docs/getting-started.md`
is updated only where it currently promises an affected command's JSON shape or
an empty-result exit code. MCP tool descriptions in `MemoryTools.cs` are updated
only where they promise a successful no-op.

## Test matrix

| Area | Required proof |
|---|---|
| Core write boundary | Construct persistent `MemoryWrite` values at 182 and 183 sanitized characters; 182 survives store and append, 183 is refused before artifact, sidecar, archive, or temporary file creation. Include a long `~name` proving the ephemeral exemption. |
| Three write routes | Drive remember, append-create, and compact through the internal handlers and public MCP facades; each returns `status: error`, `errorCode: INVALID_PARAMETER`, and no uncaught transport exception for the same invalid name. |
| CLI parity | Drive `Store`, `Append`, and `Compact` with `--json` equivalents against the same invalid subject; each emits `CliErrorOutput` and exits 1. This proves the CLI direct-handler route is covered. |
| JSON truthfulness | Serialize each affected command and parse JSON. Assert the removed properties are absent; assert reindex's four counters equal the actual reindex result. Keep a source-generated-context test so a new record cannot be emitted reflectively. |
| MCP warning matrix | Assert the twelve-tool table above: specifically empty `search`, empty `list`, one-chunk `compact`, and `keepRecent >= chunkCount` are warnings; clean `reconcile`, empty `restore`, and `guide` remain successes. |
| CLI exits | Assert `scri search` and `scri list` empty output, and `scri compact` no-op output, preserve their JSON/text payloads but return 1. Assert a non-empty search/list and mutating compact return 0. Assert `EmitMcpResult` returns 1 for warning and error, 0 only for success. |
| Regression guard | Extend `CliSurfaceConformanceTests` rather than adding another unbounded audit: enumerate all command JSON records and require each constructor argument to be sourced from an operation result, input that is explicitly documented as echoed, or emitted content. The four removed fields are named negative assertions. |

## Non-goals

- Changing the 5 MB MCP content-element policy or applying it to CLI stdin.
- Validating, renaming, or repairing legacy sidecars solely because their names
  exceed the new write limit.
- Redesigning CLI JSON into one universal envelope; this stream removes lies,
  not useful command-specific shapes.
- Treating every empty diagnostic as a warning. A clean reconcile scan, an
  empty restore context, and guide delivery are successful requests by the
  classifications above.
- Plugin protocol, vector indexing, durable-format, transactional-restore, or
  H1/H2/H6 work.

## No-deferral constraint

The implementation and review must land every decision and every test row in
this document together. A newly discovered same-class surface is not deferred:
the implementer enumerates the class, either includes the instance with a test
or records a concrete reason it is outside the table. “Pre-existing,” “unlikely,”
or “the CLI does not use the facade” is not a reason to omit it. The stream is
complete only when both MCP and local CLI produce the stated validation, schema,
status, and exit outcomes.
