# IMP-04 — `EntityFactMaterializer` extraction design

**Date:** 2026-08-27  
**Status:** Approved design  
**Release relationship:** 0.6.0 improvement; bounded internal refactoring only

## Goal

Extract entity-fact construction and its derived-fact lifecycle behind one bounded internal seam.
The seam preserves `IMemoryStore`, entity-query behavior, sidecar behavior, and all storage
semantics while giving one owner responsibility for the in-memory fact cache, generation and dirty
state, queued sidecar writes, background flushing, and disposal.

## Scope

- Introduce an internal, stateful `EntityFactMaterializer` in `Scrinia.Core.Search`.
- Move the transformation from listed memory entries and typed/legacy attributes into the existing
  `EntityFactStore` representation.
- Route current full and filtered derivation paths through that collaborator.
- Preserve deterministic ordering, duplicate handling, date aggregation, co-occurrence data, and
  entity ambiguity behavior.
- Keep `FileMemoryStore` responsible for host integration: scope/recovery/index resolution,
  persisted-sidecar validation, and invoking the materializer lifecycle.

## Non-scope

- No redesign of `FileMemoryStore` beyond delegating the existing host boundary and calling
  `EntityFactMaterializer.Dispose()` from the store disposal path.
- No change to `IMemoryStore`, MCP `facts`, CLI output, NMP/2, artifacts, indexes, facts sidecar
  format, attribute extraction, entity naming, or scoring weights.
- No new database, persistent materialization format, background service, or remote endpoint.
- No public API change. The collaborator and all supporting types remain internal.

## Stateful ownership and exact boundary

`EntityFactMaterializer` is an internal stateful collaborator. It owns:

- the merged `EntityFactStore` cache and cache publication;
- the monotonic generation and invalidation/dirty-state transitions;
- per-sidecar queued payloads, including generation-aware coalescing and retry state;
- the `IBackgroundWorkParticipant` exposed to `BackgroundWork`; and
- the `IBackgroundWorkRegistration` created for that participant, including idempotent disposal.

Its exact internal boundary is:

```csharp
internal sealed class EntityFactMaterializer : IDisposable
{
    internal EntityFactMaterializer(
        EntityFactMaterializerSource source,
        Func<string, string?> resolveSidecarPath,
        Func<string, string?> readSidecar,
        FactSidecarWriter writeSidecar,
        Func<IBackgroundWorkParticipant, IBackgroundWorkRegistration> registerParticipant);

    internal EntityFactStore Materialize(
        IReadOnlyList<ScopedArtifact> items,
        IReadOnlyDictionary<string, PersistedFactResult> persistedFacts);

    internal EntityFactStore? CachedStore { get; }
    internal long Generation { get; }
    internal bool IsDirty { get; }
    internal IReadOnlyDictionary<string, PendingFactWrite> DirtyQueue { get; }
    internal IBackgroundWorkParticipant FlushParticipant { get; }

    internal void Invalidate();
    internal Task FlushAsync(CancellationToken cancellationToken);
    public void Dispose();
}

internal sealed record PersistedFactResult(
    string Token,
    IReadOnlyList<MemoryEntities> Memories);

internal sealed record PendingFactWrite(string Content, long Generation);

internal delegate IReadOnlyList<MemoryEntities> EntityFactMaterializerSource(
    IEnumerable<ScopedArtifact> items);

internal delegate (bool Written, byte[] Bytes) FactSidecarWriter(
    string path,
    string content);
```

`persistedFacts` contains only sidecars already obtained, parsed, version-validated, token-validated,
and admitted by the host's recovery gates. A scope absent from the dictionary has no usable
persisted fast-path result and is materialized from its selected entries. The materializer groups
the supplied entries by scope, selects a supplied `PersistedFactResult` when present, and derives
the missing scopes through `source`. It uses `resolveSidecarPath` only to identify where a rebuilt
scope payload belongs; it does not perform persisted-fact validity or recovery decisions.

`source`, `readSidecar`, `writeSidecar`, and `registerParticipant` are narrow host delegates. The
materializer receives the sidecar path resolver rather than a single path because paths are
scope-specific and ephemeral scopes have no sidecar. `readSidecar` is retained as the host I/O
delegate used by the materializer's queued-flush lifecycle where required; the host remains the
owner of safe read/recovery gating for the fast path.

Cache selection is also materializer-owned: it may return the cached merged store only when the
current selected input is the cache's current selection and generation. A filtered or otherwise
different selection is built for that call and cannot replace the canonical merged cache. A
per-scope persisted result is never mistaken for the merged cache.

`FileMemoryStore` owns only host lifecycle invocation. It selects scopes and entries, obtains and
validates persisted sidecars after recovery barriers, passes the selected entries plus the
validated `persistedFacts` dictionary to `Materialize`, calls `Invalidate` after mutations, and
calls `Dispose()` when the store is disposed. It does not retain, register, flush, or dispose a
second participant or duplicate the materializer's cache, generation, dirty queue, or write policy.
The materializer owns its registration and disposes it exactly once.

## Data flow

1. The store resolves entries using the current authoritative `ListScoped` behavior and completes
   existing recovery barriers.
2. The store reads and validates each eligible `facts.json` sidecar, including format/version and
   token checks, and places each admitted result in `persistedFacts` keyed by scope.
3. The store calls `Materialize(selectedItems, persistedFacts)`.
4. The materializer reuses admitted scope results, derives missing scopes, builds the merged
   `EntityFactStore`, publishes it according to generation rules, and queues rebuilt sidecars.
5. The materializer's registered participant flushes queued writes through its own `FlushAsync`.
6. `EntityFactStore` exposes the result to `facts` queries and `EntityGraphScoreContributor`.

Legacy `type:value` keywords and current typed attributes remain dual-read as documented in
`docs/architecture/core.md`. Every attribute key remains eligible for graph membership;
recommended entity keys remain guidance rather than a gate.

## Error, generation, and disposal rules

- Null, malformed, or unsupported input follows current derivation behavior; an unreadable host
  result is never silently converted into an empty fact store.
- A throwing source propagates the original exception without wrapping. A failed materialization
  does not publish a partial cache or discard a newer queued payload.
- A build captures its generation and publishes the complete cache only if that generation remains
  current. A build from an older generation may still return its local result but cannot replace the
  current cache or clear newer dirty state.
- `Invalidate` clears the cached store, advances `Generation`, and preserves dirty state needed to
  prevent an in-flight or queued older payload from being treated as current.
- Queued writes are coalesced by sidecar path; newer generations supersede older payloads. Flush
  retries and cancellation preserve pending work and dirty state under the existing semantics.
- `Dispose` is idempotent, marks the participant disposed, and disposes the materializer-owned
  registration. `FileMemoryStore` invokes it once from its existing disposal path.

## Tests and acceptance criteria

- Existing `facts entities`, `profile`, and `intersect` tests pass unchanged.
- Focused tests cover typed attributes, legacy keywords, duplicates, dates, co-occurrences, stable
  ordering, empty input, filtered scopes, excluded topics, and ephemeral-inclusive queries.
- Contract tests cover cache reuse and invalidation, generation publication, persisted-result
  selection versus derivation, dirty queue coalescing, participant flush, cancellation, exception
  identity, and idempotent disposal.
- Full and filtered paths materialize only entries selected by the existing authoritative resolver.
- Persisted fast-path validation and recovery gating remain in `FileMemoryStore`; materializer
  tests do not assume that an invalid sidecar is usable.
- Public API signatures, facts sidecar serialization, file paths, and write timing remain unchanged.

## Affected files

- `src/Scrinia.Core/Search/EntityFactMaterializer.cs` — new stateful collaborator and exact internal
  boundary.
- `src/Scrinia.Core/Search/EntityFactDerivation.cs` — retain the derivation façade and delegate to
  the collaborator.
- `src/Scrinia.Core/Search/EntityFactStore.cs` — retain the result model; integrate only as needed.
- `src/Scrinia.Core/FileMemoryStore.cs` — host scope/sidecar validation, materializer invocation,
  invalidation, and disposal only; no duplicate lifecycle state.
- `tests/Scrinia.Tests/EntityFactMaterializerTests.cs` — characterization and lifecycle-contract
  coverage.
- `docs/architecture/core.md` — update living ownership documentation only when implementation
  lands.

## Dependencies

- Existing `EntityFactDerivation`, `AttributeExtractor`, typed attribute rules, `EntityFactSidecar`,
  `BackgroundWork`, and `ListScoped` semantics are authoritative.
- IMP-01 may consume the materialized store for adjacency memoization.
- IMP-02 depends on existing addressing behavior, not on this extraction seam.

## Explicit no-placeholder content

The exact internal constructor, materialization input, persisted-result representation, lifecycle
properties, invalidation/flush/disposal methods, and ownership split are defined above. The design
introduces no public API, storage contract, or serialization change.
