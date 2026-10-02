# Coordinated Strict-Safety Hardening — Design

**Status:** Approved architectural design  
**Date:** 2026-10-01  
**Release:** One coordinated strict-safety release

## Context and goals

The current Core boundary is sound, but several public paths still rely on duplicated or late
validation. A `local:` scope can be combined without the containment check used by the legacy
branch; reserved `/skill` and `/agent` paths do not have one dispatcher; bundle import can report
success after an incomplete, prefix-consistent restore; the only executable plugin is embeddings
specific although plugin-facing expectations imply a generic host; and hybrid search materializes
and scans unbounded scoped data.

This release makes those boundaries strict, explicit, and testable. Its goals are to reject
unsafe or incomplete input before publication, make CLI and MCP reserved-path behavior identical,
make restore all-or-rollback, limit retrieval work to declared candidate sets, and publish only the
executable protocol that actually exists.

## Non-goals

- No HTTP/REST server, remote CLI mode, or generic executable-plugin host.
- No compatibility aliases for rejected addresses, incomplete bundles, or an unversioned plugin
  protocol.
- No change to NMP/2 content semantics, embedding model selection, ranking weights, or entity-graph
  scoring beyond bounded inputs and exact entity recognition.
- No best-effort bundle import, partial-success result, silent category skip, or manual repair as a
  successful outcome.
- No broad store, transaction, or plugin-framework rewrite outside the contracts below.

## Approved boundary-first architecture

### 1. `MemoryAddress` and scope containment

`MemoryAddress` becomes the portable durable-name authority, not merely a syntactic parser. Its
construction must reject every address whose persistent segments cannot be represented safely and
injectively by the durable store. The existing `/temp/name` ↔ `~name` canonicalization remains;
CLI persistent-only policy remains an adapter policy after parsing.

Every scope-to-directory conversion must derive from the validated address/scope constructor and
must prove `PathContainment.IsWithin(memoriesRoot, candidate)` before returning a filesystem path.
`PathRouter.ToStoreDir` must apply this rule to both `local:` and legacy `local-topic:` paths; no
public helper may return an unchecked joined path. Rejection happens before any directory creation,
read, write, or vector lookup.

### 2. Shared reserved-path dispatcher

Introduce one internal reserved-path dispatcher consumed by both CLI commands and MCP tools before
ordinary memory handling. It classifies a parsed persistent address as ordinary memory, skill, or
agent and owns the complete operation matrix: read, write, append where supported, list/search
visibility, copy/export/import behavior, and delete including body, metadata sidecar, and versions.

The dispatcher routes `/skill/...` through the established markdown-plus-sidecar lifecycle and
`/agent/...` through the agent-file lifecycle. Unsupported operation/kind combinations fail with a
stable policy error; neither facade may fall through to `FileMemoryStore` as an ordinary memory.
The dispatcher is not a second store and does not alter the typed `IMemoryStore` contract.

### 3. Strict all-or-rollback bundle import

Bundle import has two gates and one commit boundary:

1. **Build gate:** validate manifest version and shape, selected-topic names, every topic index,
   each declared artifact body, all entry metadata, all declared file entities, and exact manifest
   totals before durable state changes. A declaration without its required archive member is invalid.
2. **Restore plan:** construct one complete, deterministic plan of topic mutations and file-entity
   mutations. Unknown file-entity categories are invalid, not reported as skipped.
3. **Journaled commit:** persist a versioned restore journal containing enough prior state to undo
   every planned mutation; apply the plan; verify the committed state against the plan; then clear
   the journal. A failure or startup recovery rolls back the whole plan. If rollback cannot be
   proven, return `BUNDLE_RECOVERY_REQUIRED` and block further bundle imports until recovery
   completes.

The bundle service calls an `IMemoryStore` import/restore operation; it must not cast the public
store interface to `FileMemoryStore`. The import result is emitted only after verification and
contains actual committed counts, not manifest counts. A valid filtered import is atomic for its
selected subset; unselected topics and file entities are untouched.

### 4. Versioned embeddings-only executable protocol

The only supported child executable is the embeddings plugin. Replace implied generic-plugin
behavior with an explicit embeddings protocol, negotiated before indexing or searching:

```text
scrinia.embeddings.protocol = 1
capabilities = ["index", "search", "remove", "status"]
```

The executable exposes `status` as the handshake. Its response includes `protocolVersion` (integer),
the exact capability names, provider availability, dimensions, and vector-store signature. The host
accepts only protocol version 1 and the required capability set for the requested operation. Missing,
malformed, duplicate, or unsupported values disable the executable for that session and leave the
host on its documented lexical fallback; they are never interpreted as a generic plugin contract.

`index`, `search`, and `remove` use typed request/response DTOs serialized through source-generated
JSON metadata. `search` accepts a bounded candidate identity set and returns scores only for those
identities. `upsert` is retained only as an explicitly deprecated protocol-1 compatibility tool for
installed embeddings executables; the host never selects it after a successful version-1 handshake.
`IScriniaPlugin.Order`, generic executable state paths, and `EmbeddingsPlugin.Configure` are not
advertised as executable-protocol semantics.

### 5. Bounded retrieval and exact entity tokens

Lexical search produces a deterministic top-**K** candidate window after scope and topic-exclusion
filtering. **K** is `Scrinia:Search:CandidateLimit`, defaults to **200**, and accepts only integers
from **1** through **1,000**; an absent value uses 200 and an invalid value is a configuration
error, not an unbounded fallback. All downstream lexical refinements, entity-fact derivation, supplemental
scorers, and response facts consume that same snapshot. The vector contributor receives only the
candidate identities grouped by scope and performs candidate-based vector lookup, not per-scope
`GetVectors(scope)` followed by a full scan and sort.

The vector-store contract gains a lookup/search operation whose inputs are canonical scope plus
candidate names and whose output contains only matching candidate vectors/scores. It must preserve
the existing case-insensitive memory-name identity rule and avoid discovering scopes from disk.

Entity auto-seeding normalizes with `TextAnalysis.Tokenize` and matches an entity only when every
normalized entity token is present as a complete normalized query token. Raw substring matching is
removed: `art` must not seed `person:martin`. Empty token surfaces never seed. Explicit `facts`
queries retain their existing entity-key semantics.

## Strict migration and error policy

This is a breaking release. New strict rules apply on first use; there is no permissive read mode.

| Boundary | Refusal / recovery behavior | Consumer action |
|---|---|---|
| Address or scope | `MALFORMED_ADDRESS` or `SCOPE_CONTAINMENT_FORBIDDEN`; no filesystem access | Use a canonical logical address; re-export/recreate invalid legacy data. |
| Reserved path | `RESERVED_PATH_OPERATION_FORBIDDEN`; no ordinary-memory fallback | Use the supported skill/agent operation or migrate the data intentionally. |
| Bundle validation | `INVALID_BUNDLE`; no journal and no mutation | Obtain a complete, supported bundle and retry. |
| Bundle commit/recovery | `BUNDLE_IMPORT_FAILED` after successful rollback, or `BUNDLE_RECOVERY_REQUIRED` when rollback proof fails | Retry only after automatic recovery succeeds; otherwise preserve the journal and repair through the recovery command. |
| Embeddings executable | `EMBEDDINGS_PROTOCOL_UNSUPPORTED` or `EMBEDDINGS_PROTOCOL_INVALID`; disable executable for the session | Upgrade/reinstall the embeddings executable; lexical search remains available. |
| Legacy vectors | Quarantine/reindex under the active signature; never treat an incompatible vector file as current | Run the documented reindex flow. |

Legacy bundle versions are accepted only when they can satisfy the complete-manifest rule without
inventing missing data. Older partial bundles, unsafe paths, unknown file categories, and unversioned
executable responses are refused. Error codes are stable machine-readable contract values; adapter
copy may add context but must not relabel a refusal as success.

## Delivery order and dependencies

1. **Address containment.** Tighten `MemoryAddress` durable validation and make all store/vector
   scope mapping containment-checked. This gates every later filesystem-facing change.
2. **Reserved dispatcher.** Route CLI and MCP through the common matrix, including sidecar deletion.
   Depends on canonical addresses from phase 1.
3. **Bundle atomicity.** Add complete-plan validation, interface-level restore, journal/rollback,
   recovery, and actual-count reporting. Depends on phases 1–2 because bundles carry addresses and
   file entities.
4. **Embeddings protocol.** Publish the version-1 embeddings-only handshake and typed wire DTOs;
   remove generic executable claims. Depends on phase 1 for canonical scope/identity transport.
5. **Bounded retrieval.** Add lexical top-K candidates, candidate-based vector lookup, and exact
   entity-token seeding. Depends on phase 4 where the out-of-process contributor is enabled.
6. **Release integration.** Update public reference documentation, plugin guidance, CHANGELOG, and
   migration notes only after the contracts and conformance tests are green.

## Test and verification matrix

| Area | Required proof |
|---|---|
| Address/scope | Enumerate malformed, rooted, URI-like, traversal, trailing-dot/space, alias, and valid hierarchical addresses. Prove `local:` and `local-topic:` cannot escape the memories root before I/O. |
| Reserved paths | Table-driven CLI and MCP operation matrix for ordinary, skill, and agent addresses; prove identical routing and that deleting a skill/agent removes its body, sidecar, and versions as applicable. |
| Bundle build gate | For each required manifest/index/artifact/file-entity member, remove or corrupt exactly that member and prove zero durable mutations. Assert unknown categories and count mismatches refuse. |
| Bundle commit/recovery | Inject failures at journal write, each topic/file-entity mutation, verification, and rollback. After cold reload, assert either the entire selected plan is present or the exact pre-import state is present; recovery-required blocks another import. Run the matrix through a non-`FileMemoryStore` test implementation to pin the interface contract. |
| Protocol | Handshake success, unsupported version, absent/malformed/duplicate capability, unavailable provider, and each required operation. Prove unsupported executables are disabled and lexical fallback remains usable. Source-generated serialization tests cover every wire DTO. |
| Retrieval | Assert lexical candidate count never exceeds configured K; contributors and facts receive the same identities; vector lookup cannot return a non-candidate; bounded and unbounded corpus fixtures preserve deterministic ordering within the candidate contract. |
| Entities | Verify case/punctuation normalization, multi-token all-token matching, empty surfaces, and substring false positives (`art`/`martin`). |
| Release | `dotnet build Scrinia.sln`, default `dotnet test Scrinia.sln`, trim/source-generation coverage for changed DTOs, documentation link checks, and a review of public CLI/MCP/plugin guidance for removed generic or permissive claims. |

## Public contract changes and migration guidance

- `MemoryAddress.Parse`/`TryParse` now reject names that cannot be safely represented by durable
  scope storage. Callers must not pre-sanitize, concatenate scopes, or convert addresses to paths.
- CLI and MCP callers receive the same reserved-path behavior. Automation that used CLI `/skill` or
  `/agent` as ordinary memory must use the supported file-entity lifecycle and handle explicit
  unsupported-operation errors.
- Bundle import is all-or-rollback and rejects archives that previously imported partially. Create a
  fresh export from a supported version when an old archive lacks a declared member; do not remove
  manifest declarations to force import.
- `IMemoryStore` exposes the restore/import capability required by bundle import. Alternative store
  implementations must implement it; a runtime downcast is not supported.
- The executable plugin surface is embeddings protocol v1 only. Authors must provide the `status`
  handshake and typed v1 operations; generic executable plugins, `Order`-based execution, and
  undocumented state directories have no supported migration path.
- Search-contributor and vector-store integrations must accept canonical bounded candidates and may
  score only those candidates. Entity integrations must use normalized exact tokens rather than
  substring detection.
- `Scrinia:Search:CandidateLimit` is a new workspace configuration key (default `200`, inclusive
  range `1`–`1000`). Operators that need broader retrieval must set this key deliberately; invalid
  values fail configuration loading rather than restoring whole-corpus work.

## Reviewed sources

The design was grounded in the following reviewed implementation points: `src/Scrinia.Core/Models/MemoryAddress.cs`
(current syntactic-only address acceptance), `src/Scrinia.Core/PathRouter.cs` (unchecked `local:`
join beside guarded legacy handling), `src/Scrinia.Core/Bundles/BundleFormatService.cs` (missing
members skipped, concrete-store cast, and prefix-consistent file writes),
`src/Scrinia.Core/Search/SearchPipeline.cs` and `src/Scrinia.Core/Search/EntityQuerySeeder.cs`
(whole candidate materialization and substring seeding), `src/Scrinia.Core/Embeddings/IVectorStore.cs`
and `src/Scrinia.Core/Embeddings/VectorStore.cs` (full-scope vector reads),
`src/Scrinia/Commands/EmbeddingsHost.cs` and
`src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs` (embeddings-specific child host and
unversioned tools), and `src/Scrinia.Plugin.Abstractions/IScriniaPlugin.cs` (the public `Order`
claim).

## Spec self-review

- **Placeholders:** passed; no deferred owner, unspecified compatibility mode, or unbounded
  validation instruction remains.
- **Consistency:** passed; bundle refusal occurs before journaling, while a post-journal failure
  rolls back or becomes recovery-required; neither is described as a successful import.
- **Ambiguity:** passed; protocol version, required capabilities, candidate boundary, strict entity
  rule, failure codes, and filtered-import atomicity are explicit.
- **Scope:** passed; the release coordinates only the five approved hardening boundaries and their
  documentation, migration, and verification work.
