# B2 — Journal Convergence Design

**Date:** 2026-09-11  
**Status:** Approved specification  
**Scope:** Append, restore, and file-entity durable recovery evidence

## Goal

B2 converges the three durable recovery implementations on one versioned common envelope and one scope-safe lifecycle model while preserving their existing on-disk locations, operation semantics, and public recovery contracts.

## Non-goals

B2 does not:

- change NMP/2 or the ordinary artifact, index, metadata, or sidecar formats;
- introduce cross-scope transactions or a workspace-wide transaction barrier;
- add background garbage collection, fleet-wide recovery reporting, or a daemon;
- move or rename existing journal and recovery files;
- redesign normal append, restore, or file-entity business behavior;
- make legacy evidence writable in place merely to normalize it;
- change public MCP tool names, CLI command names, or successful-response semantics except where required to expose the converged recovery result.

## Approved decisions

The following decisions are authoritative for implementation:

1. **Locations are compatibility contracts.** Append evidence remains `.append-journal.json`; restore evidence remains `.restore-journal.json`; file-entity evidence remains in `.file-entity-*.recovery` files in the existing scope directory. The common envelope changes the contents and parsing model, not these names or locations.
2. **The envelope is common; the body is operation-specific.** A single envelope owns identity, version, scope, lifecycle state, and recovery metadata. Append, restore, and file-entity bodies retain only the data required by their own adapter.
3. **Lifecycle is hybrid.** The envelope has common coarse states needed for discovery and admission. Each adapter owns operation-specific substates and legal transitions inside its body.
4. **Evidence is scope-local.** Discovery must qualify every candidate to exactly one store scope before it can affect that scope. A candidate that cannot be safely and unambiguously qualified blocks the affected scope, not unrelated scopes.
5. **Deterministic precedence is discovery order.** Within a scope, restore evidence is admitted first, append evidence second, and file-entity evidence third. This ordering is stable regardless of filesystem enumeration order.
6. **Recovery is fail-closed.** Malformed, unsafe, newer-than-supported, or ambiguous evidence prevents mutation in the affected scope and remains available for diagnosis or retry.
7. **Cleanup is committed work.** A committed operation may return a post-commit warning when cleanup fails, but cleanup evidence is retained and retryable. Cleanup failure must not roll back an already verified live commit.
8. **B1 carry-forward ruling.** B1 proceeded with a documented scope-contamination ruling: an unrelated or unqualified recovery file must never promote an ordinary failure into a recovery-blocked result for another scope, while evidence that belongs to a scope must block that scope until safely resolved. B2 must preserve this ruling and must not worsen scope contamination through common discovery, shared caches, or response mapping.

## Common envelope

The converged envelope is a durable JSON object serialized with the repository’s source-generated JSON infrastructure. Its wire shape is versioned independently from each operation body.

```json
{
  "format": "scrinia.recovery",
  "version": 1,
  "operation": "append",
  "bodyVersion": 1,
  "operationId": "01JOURNAL000000000000000001",
  "scope": "local:notes",
  "address": "/notes/daily-log",
  "state": "prepared",
  "createdAt": "2026-09-11T12:00:00Z",
  "updatedAt": "2026-09-11T12:00:00Z",
  "body": { "oldArtifact": null, "oldSidecar": null, "newArtifact": null, "newSidecar": null }
}
```

### Required envelope members

| Member | Rule |
|---|---|
| `format` | Exact common format discriminator. No heuristic or exception-message parsing. |
| `version` | Positive integer. B2 common envelope version is exactly `1`; newer versions are rejected before mutation. |
| `operation` | Exactly `append`, `restore`, or `fileEntity`. |
| `bodyVersion` | Positive operation-specific schema version. Each adapter declares the versions it can normalize; newer versions are rejected before mutation. |
| `operationId` | Non-empty stable correlation identifier. Recovery responses use the journal’s validated identifier, never a synthetic caller identifier. |
| `scope` | Canonical scope identity. It must equal the destination scope selected by the store. |
| `address` | Canonical logical address when the operation has one. It is omitted only for an operation whose body supplies its entity identity. |
| `state` | Common lifecycle state, encoded as the documented lowercase string. |
| `createdAt`, `updatedAt` | Valid timestamps; `updatedAt` cannot precede `createdAt`. |
| `body` | Object whose schema is selected only by `operation`. |

Unknown optional members may be ignored only when the envelope version is supported. Unknown required structure, wrong member types, duplicate identity members, and a missing body are malformed evidence. `bodyVersion` is required even when the body is empty for an operation that has no additional payload.

### Common lifecycle states

The common state model is:

```text
prepared → applying → committed → cleanupPending → cleaned
     ↘                 ↘
      recoveryRequired  rolledBack
```

- `prepared`: all rollback or replay evidence has been durably written and validated.
- `applying`: the adapter may mutate operation-owned live members.
- `committed`: the operation’s live result passed its commit verification.
- `cleanupPending`: the live result is valid, but journal/archive/staging cleanup is incomplete.
- `cleaned`: all operation-owned evidence and temporary members are removed.
- `recoveryRequired`: safe automatic completion is not possible; explicit recovery is required.
- `rolledBack`: the pre-operation state was restored and verified; final cleanup may still be pending.

`cleaned` is terminal and normally has no durable file because successful cleanup removes the evidence. `recoveryRequired` is terminal for the current attempt but not for a later explicit retry. An adapter must reject illegal transitions rather than infer a state from which files happen to exist.

## Operation-specific bodies and adapters

### Append adapter

The append adapter preserves the current append journal’s rollback guarantees: old and new artifact/sidecar members, archive members, prune candidates, hashes, and operation identity remain represented sufficiently to restore the exact pre-append pair or complete the verified commit.

It maps the existing append substates (`prepared`, archive progress, archived, new-pair progress, committed, cleanup pending, recovery required, and rolled back) into the common lifecycle while retaining append-specific detail in `body`. Existing payload, hash, path, address, and prune-candidate validation remains mandatory before filesystem use.

The adapter must:

1. preflight retained evidence before persistent append admission;
2. validate the envelope, body, scope, canonical address, and contained paths;
3. recover or refuse only the journal’s qualified scope;
4. preserve byte-for-byte retained evidence when recovery is blocked;
5. keep normal post-commit cleanup warnings distinct from pre-admission `RECOVERY_BLOCKED`.

### Restore adapter

The restore adapter owns bundle/topic import staging, snapshot protection, live pair publication, cache invalidation, and staging cleanup. Its body records the import members and enough verified scope-qualified identity to ensure that a later recovery cannot delete a memory created after the import snapshot.

The adapter must sweep only operation-owned staging litter, reject overlong or traversal-producing names before staging or live mutation, and leave live pairs untouched when prepared-journal creation or the first commit write fails. Completed journal cleanup failure is recoverable without re-entering a read/write recovery barrier that is already active for the current operation.

### File-entity adapter

The file-entity adapter treats the entity body and metadata as one recoverable publication pair. Its body records the pair paths, prior snapshots, hashes, publication status, and any operation-owned archive or cleanup members.

The adapter must validate every deserialized path as contained by the expected scope/entity layout before mutation, publish and verify the pair as one logical result, retain committed evidence when cleanup fails, and retry cleanup without reverting a verified publication. Traversal, absolute-path, wrong-scope, and malformed identity data are unsafe evidence.

## Discovery and precedence

Discovery is performed under the existing scope/recovery coordination boundary and produces a typed candidate set, not a list of filenames to interpret opportunistically.

1. Determine the current store scope from the already-selected workspace/store context.
2. Inspect the exact preserved locations for `.restore-journal.json`, `.append-journal.json`, and `.file-entity-*.recovery`.
3. Parse each candidate through the common envelope parser, then dispatch by `operation` to its adapter.
4. Canonicalize and verify scope, address/entity identity, operation identity, and all contained paths.
5. Reject candidates whose filename and envelope operation disagree.
6. Sort valid candidates deterministically by operation precedence: **restore → append → file entity**, then by canonical subject and `operationId` using ordinal comparison.
7. Admit and resolve candidates one at a time in that order. Stop admission for the affected scope on the first blocking candidate; do not inspect an unrelated scope as evidence for the current operation.

Filesystem enumeration order, timestamps, JSON property order, and exception text must not affect precedence or classification.

## Blocking rules

The affected scope is blocked, with no live mutation, when evidence is:

- malformed JSON, structurally incomplete, or semantically invalid;
- unsafe, including traversal, absolute escape, symlink/reparse escape where applicable, wrong-root, or mismatched canonical subject paths;
- newer than the supported common or operation-specific version;
- ambiguous, including multiple candidates claiming the same operation/subject without a deterministic and safe winner, or evidence whose scope cannot be uniquely established;
- internally inconsistent, including invalid state transitions, hash mismatch, invalid payload, or operation/filename mismatch.

The blocked result contains typed status, code, scope, journal/recovery path, operation identity when safely available, reason category, and an action-needed recovery instruction. If identity cannot safely be read, the field is `unknown`; raw untrusted text is not promoted into a trusted path or scope.

An unrelated ordinary failure remains an ordinary failure even if a recovery file exists elsewhere. Conversely, a retained recovery file in the current scope blocks persistent writes that could overwrite or invalidate its evidence. Reads and operations explicitly outside the blocked scope may continue when the existing contract permits them.

## Legacy and mixed-version compatibility

The adapters must read the current pre-convergence append and restore journals and current file-entity recovery records at their preserved locations. Legacy readers normalize them into an in-memory common envelope without rewriting them merely because they were read.

Mixed-version directories are supported by per-candidate parsing: a supported legacy candidate and a supported B2 candidate may coexist, but precedence and conflict checks still apply. A legacy candidate that cannot be normalized safely is blocked rather than silently ignored. A newer common or operation-specific version blocks only its qualified scope and remains retained. Successful explicit recovery may remove an old-format file only after the adapter has verified the resulting live state; failed conversion or cleanup retains the original bytes.

No migration of ordinary sidecars, artifacts, NMP/2 payloads, or unrelated metadata is part of B2.

## Committed cleanup and retry

Cleanup is idempotent and operation-owned. Each adapter must:

- distinguish pre-commit recovery from post-commit cleanup;
- verify the live result before deleting evidence;
- delete only paths named and validated by its envelope/body;
- retain evidence when any cleanup member cannot be removed;
- return a typed post-commit warning while allowing the current read/write to continue when the live result is valid;
- make an explicit retry converge to no recovery evidence without reapplying business mutation;
- invalidate affected in-process caches only after a verified rollback or publication, using the canonical scope and subject identity.

## Public MCP and CLI mapping

The public operation names remain unchanged.

| Surface | Mapping |
|---|---|
| MCP `append` | Pre-admission retained evidence maps to `status: error`, `code: RECOVERY_BLOCKED`; a committed append with cleanup failure remains the existing `success_with_warning`/recovery-warning contract. |
| MCP `restore` / import path | Retained or unsafe restore evidence maps to the existing typed recovery-blocked envelope with scope, evidence path, operation ID, and action-needed guidance. |
| MCP file-entity publication path | Uses the same typed blocked/retry mapping; pair publication failures never escape as an unclassified internal error when typed evidence exists. |
| CLI memory commands | Render the MCP-shaped result through the existing CLI JSON/YAML wrapper and preserve existing exit-class behavior. Do not infer recovery from filesystem existence alone. |
| Explicit recovery/retry | Uses the validated evidence operation ID and scope; caller-generated IDs cannot replace them. |

MCP and CLI adapters must consume typed Core results/exceptions. They must not parse `Exception.Message`, scan arbitrary directories to promote `INTERNAL`, or expose untrusted evidence paths as trusted destinations.

## Serialization requirements

All common envelope, body, state, result, and error DTOs used across durable boundaries must be registered in the appropriate source-generated `JsonSerializerContext`. Reflection-based serialization is forbidden. Custom converters must preserve the exact lowercase wire spellings and reject invalid enum values. Serialization tests must cover round trips, missing/extra members, malformed types, unsupported versions, and legacy normalization.

## Exact B2 task decomposition

1. **B2.1 — Common contract and source generation**
   - Add the envelope, common state, typed candidate/classification result, and operation discriminator.
   - Add source-generated context registrations and converters.
   - Pin exact wire names, version constants, legal common transitions, and size/identity validation.

2. **B2.2 — Append adapter**
   - Adapt the existing append journal reader/writer and state machine to the envelope.
   - Preserve append payload/hash/path/prune validation and existing rollback/cleanup behavior.
   - Add legacy read normalization and B2 round-trip coverage.

3. **B2.3 — Restore adapter**
   - Adapt restore discovery, staging, commit, rollback, cache invalidation, and cleanup.
   - Preserve snapshot isolation and scope-local blocking behavior.
   - Add legacy/mixed-version, crash-boundary, and cleanup-retry coverage.

4. **B2.4 — File-entity adapter**
   - Adapt body/metadata pair publication and committed-evidence recovery.
   - Preserve contained-path validation, archive handling, pair verification, and retry cleanup.
   - Add traversal, wrong-scope, partial-write, and committed-cleanup coverage.

5. **B2.5 — Deterministic discovery coordinator**
   - Implement exact-location discovery and restore→append→file-entity precedence.
   - Enforce candidate qualification, ambiguity detection, deterministic tie-breaking, and scope-local barriers.
   - Add contamination regressions proving unrelated evidence cannot alter classification.

6. **B2.6 — Public adapters and compatibility lane**
   - Wire typed Core outcomes through MCP and CLI without changing command/tool names.
   - Preserve `RECOVERY_BLOCKED`, normal `RECOVERY_REQUIRED`, and post-commit warning distinctions.
   - Add agentic, MCP, CLI, golden, legacy, and mixed-version contract tests.

7. **B2.7 — Cleanup/retry and acceptance hardening**
   - Verify idempotent explicit retry, retained evidence, cache behavior, and no unintended file changes.
   - Run the complete B2 acceptance matrix and record compatibility evidence.

## Dependencies and exclusions

B2 depends on B1’s documented common recovery-boundary decisions, typed recovery result contracts, canonical scope/address routing, operation correlation, path containment, and cache invalidation seams. B2 must land after those B1 contracts are available and must not reopen their settled public mappings.

Explicitly excluded are cross-scope transactions, background GC, fleet reporting, NMP/2 changes, ordinary sidecar-format changes, unrelated artifact/index schema changes, and server/HTTP surfaces.

## Compatibility gates

B2 is acceptable only if all of the following remain true:

- all three legacy locations are byte-preserved until successful, verified cleanup;
- existing successful append, restore, and file-entity behavior is unchanged;
- legacy evidence is readable, mixed-version evidence is deterministic, and newer evidence blocks safely;
- malformed, unsafe, and ambiguous evidence causes no live mutation and blocks only its qualified scope;
- unrelated recovery evidence cannot promote an unrelated failure or widen a barrier;
- cleanup failure retains retryable evidence and does not undo a verified commit;
- MCP and CLI mappings preserve typed status/code/action fields and operation correlation;
- all durable JSON uses source-generated metadata and exact converter spellings;
- focused and full regression suites, build, serialization checks, and whitespace checks pass;
- no source, test, existing documentation, or plan outside the B2 implementation scope is changed by the specification-writing task.

## Acceptance gates

The implementation plan must demonstrate, with named tests or equivalent evidence:

1. envelope/body round trips for every operation and supported legacy form;
2. deterministic discovery under shuffled filesystem enumeration;
3. restore-before-append-before-file-entity admission;
4. scope isolation for unrelated, malformed, unsafe, newer, and ambiguous evidence;
5. no mutation on blocked preflight, including byte-for-byte retained evidence;
6. successful commit followed by failed cleanup, then successful explicit retry;
7. partial publication rollback and cache invalidation for append, restore, and file entity;
8. exact MCP/CLI blocked, required-recovery, and post-commit-warning envelopes;
9. source-generation/trimming-safe serialization with no reflection fallback;
10. regression proof that B2 does not worsen B1’s documented scope-contamination ruling.

## Open risks

- Existing legacy bodies may contain fields that cannot be mapped to a unique canonical subject without conservative blocking.
- Concurrent writers may observe cleanup-pending evidence while another operation is already inside the recovery barrier; lock ordering must avoid deadlock and re-entry.
- Filesystem enumeration and reparse/symlink behavior differ by platform; containment tests must cover the supported platforms.
- A common envelope can accidentally encourage shared helpers to erase operation-specific validation; adapters must remain responsible for their own invariants.
- Cleanup retry can expose stale cache entries if invalidation is performed before, rather than after, verified state transition.
- A retained file with a valid prefix but a corrupt body may reveal an operation ID while not safely revealing its scope; response rendering must keep those fields independently typed and conservative.

## Implementation-plan split criteria

Split the implementation into separate plans when any of these apply:

- common contract/source generation can be completed without changing adapter behavior;
- append, restore, and file-entity adapters touch disjoint production seams and can be tested independently;
- discovery coordination requires a distinct lock/barrier design review;
- public MCP/CLI mapping needs a separate compatibility approval;
- legacy conversion requires more than read-only normalization or introduces a migration command;
- a task would otherwise combine scope-boundary changes with cleanup semantics;
- a plan would modify NMP/2, ordinary sidecars, cross-scope transaction behavior, background GC, or fleet reporting (that work is outside B2 and must be rejected or separately scoped).

## Self-review

- **Placeholder review:** no TODO, TBD, invented endpoint, unresolved filename, ellipsis, or placeholder version is present. The supported common envelope and example body versions are explicitly `1`; operation-specific version constants remain owned by their adapters.
- **Contradiction review:** the specification distinguishes pre-commit blocking, `RECOVERY_REQUIRED`, and post-commit cleanup warnings; it also distinguishes common lifecycle states from operation-specific substates. `cleaned` is terminal conceptually but its file absence is expected after successful cleanup.
- **Scope review:** all discovery, validation, blocking, precedence, cache, and public-result rules are scope-local. No global barrier, cross-scope transaction, background GC, or fleet report is introduced. The B1 carry-forward scope-contamination ruling is explicit and repeated in the acceptance gates.
- **Location review:** `.append-journal.json`, `.restore-journal.json`, and `.file-entity-*.recovery` are named exactly and are not relocated.
- **Boundary review:** NMP/2, ordinary sidecars, public command/tool names, and existing successful operation behavior are explicitly protected.
