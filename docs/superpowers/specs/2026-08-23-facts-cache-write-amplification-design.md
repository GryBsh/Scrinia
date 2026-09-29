# `facts.json` write-amplification reduction — approved design

**Date:** 2026-08-23  
**Status:** approved  
**Scope:** entity-fact cache persistence in the local store and its CLI/MCP/plugin lifecycle

## Context and measured root cause

`facts.json` is a derived cold-start cache. The source of truth remains the memory artifacts and
their index entries; facts are rebuilt from those entries when a sidecar is absent or its validity
token does not match. The cache is useful after a process restart, but it is not required for an
in-process facts query and must never be trusted over the current artifact/index state.

The current write path creates the observed amplification. `FileMemoryStore.GetEntityFactStore()`
rebuilds a stale or missing per-scope cache and `QueueFactSidecarWrite` attempts to write the full
serialized sidecar immediately. A small fact delta therefore rewrites the complete `facts.json`.
The fallback worker retries a failed full write, but does not change the normal successful path.

The measurement was made on a byte-for-byte copy of the repository's `.scrinia` workspace at
`N=46` memories. One `remember` storing 1,302 bytes caused 86,790 bytes of writes and 90,793
bytes of reads: approximately **65x write amplification** relative to the new payload. The
load-bearing write was `findings/facts.json`: 81,019 bytes were rewritten from byte 22 for an
approximately 272-byte fact delta. Four files changed mtime, but `vectors.bin` was append-only;
mtime-only measurement therefore under-reports the cost.

The cost is volume, not latency: observed latency stayed roughly 53–57 ms across the measured
corpus. The facts sidecar is approximately 1.9 KB per memory in the real corpus, implying about
180 KB at 100 memories, 900 KB at 500, and 1.8 MB at 1,000 for each otherwise-small write.

## Decision

Do not synchronously persist `facts.json` during a normal facts read or mutation. Build and publish
the in-memory `EntityFactStore` immediately, mark the required sidecar writes dirty, and defer
their full-file persistence until the existing lifecycle drain. The CLI's shutdown drain is the
normal production flush point; an explicit drain used by a host or test is the same contract.

This is **deferred coalescing**, not an append log and not a weaker facts cache:

- Keep at most one pending full serialized payload per sidecar path.
- If the same path is invalidated or rebuilt repeatedly before a drain, replace its pending payload
  with the newest payload rather than writing intermediate generations.
- Do not perform a background write merely because a facts query discovered a stale sidecar.
- A successful drain writes each final pending path once, subject to the existing retry and
  cancellation rules.
- A process that exits without reaching its drain boundary may leave a stale or missing sidecar;
  the next process rebuilds it from authoritative entries. It must not lose a memory or return
  stale facts in the current process.

The design intentionally trades immediate cache durability for bounded write volume. `facts.json`
is a restart accelerator, not a durability boundary.

## Generation, failure, and cancellation semantics

The existing fact-store generation remains the invalidation authority. Each mutation that can
change entity facts increments the generation and clears the in-memory merged view. A build takes
a generation snapshot. It may publish its result only when the generation is still current; if a
mutation raced the build, the result is returned to that caller but is not installed as the shared
cache.

Each pending sidecar payload records the generation at which it was derived. Coalescing replaces a
path's older pending payload with the newer payload, never the reverse. A successful write clears
the dirty state only when:

1. the path's pending payload was written;
2. no newer payload for that path is queued;
3. no pending path remains; and
4. the written generation is still the store's current generation.

If a write fails, the payload remains queued and the cache remains dirty. The failure is observable
through the existing diagnostic state and background-work warning path; it is not converted into a
successful flush merely because the in-memory query succeeded.

If `BackgroundWork.DrainAsync` reaches its timeout, it cancels the shutdown token after swapping in
a fresh token for later work. A canceled facts flush must requeue the payload it had dequeued,
preserve the dirty/failed state, decrement its in-flight accounting, and allow a fresh worker or
later drain to retry it. Cancellation must not discard a newer payload that was coalesced while the
attempt was in flight. A successful retry under the fresh token may clear dirty state normally.

The drain remains bounded. An unavailable filesystem must not prevent CLI shutdown forever, and a
timed-out flush must not claim that the sidecar is durable.

## Lifecycle integration

### Local stdio CLI

The only server surface is the local stdio `scri serve` process. The CLI also runs one-shot
commands in the same process model. `src/Scrinia/Program.cs` already calls
`BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10))` after command execution. Facts flush work
must participate in that drain without creating a second shutdown mechanism or a per-command
blocking write.

The lifecycle contract is:

1. `remember`, `append`, `compact`, import, metadata updates, and ordinary store mutations persist
   the authoritative artifact/index state and invalidate fact generations.
2. A facts query may rebuild the in-memory store and enqueue coalesced sidecar payloads, but does
   not write them synchronously.
3. Detached event sinks continue to run through `BackgroundWork`; their facts reads observe the
   current generation and may replace pending payloads.
4. The program-level drain waits for all registered background work and flushes pending fact
   payloads before returning success.
5. On timeout, the CLI retains the existing warning semantics. The warning must describe the
   possibility of incomplete derived work, not imply loss of durable memory data.

The implementation may extend `BackgroundWork` with a drain-participant/flush registration
contract, but it must preserve the current guarantees: work fired during a drain is included,
later writes receive a live token, and a canceled operation is not silently forgotten. A store
must unregister or become inert when disposed so a closed test store cannot be flushed by a later
process-wide drain.

### MCP and plugin event pipeline

MCP tools use the same `IMemoryStore` write path as the CLI. Plugins remain local stdio plugin
processes discovered by the CLI; there is no HTTP server, REST store, remote CLI mode, or removed
server lifecycle to support.

When embeddings are available, `WorkspaceSetup` currently installs the embedding event handler,
`EntityTypingEventHandler`, and `MaintenanceEventSink`. The approved ordering change puts
`MaintenanceEventSink` before `EntityTypingEventHandler` in every equivalent installation site.
Maintenance may update ref/orphan attributes; those updates invalidate the fact generation before
entity typing reads the vocabulary. Entity typing then performs one fresh fact build instead of
building once, having the first sink mutate the store, and rebuilding again. The event sequence is:

1. Persist the authoritative artifact and index entry.
2. Invalidate the fact-store generation.
3. Publish the memory event once.
4. Run `MaintenanceEventSink` first; any actual metadata update persists through the store and
   invalidates the generation again.
5. Run `EntityTypingEventHandler`; it reads the current fact view and may perform a typed-attribute
   update through `TryUpdateEntry`, which invalidates again if it changes the entry.
6. Coalesce the final sidecar payloads for the resulting generations.
7. Flush them only at drain/shutdown.

Vector indexing remains downstream and best-effort. A vector failure does not roll back or hide a
durable memory or facts mutation, and facts persistence does not wait for embedding latency.

## Ephemeral memories and forget invalidation

Ephemeral `~name` memories participate in the in-memory cross-scope fact view but have no on-disk
scope directory and therefore no `facts.json` sidecar. Remember, append, metadata update, and
forget of an ephemeral memory must still increment the same fact-store generation and invalidate
the merged in-memory view.

In particular, `ForgetEphemeral` must invalidate facts even though it has no sidecar write to
queue. A subsequent unfiltered facts query must not return an entity from the forgotten ephemeral
entry. A subsequent filtered query must likewise derive from the current `ListScoped` result.
Forgetting a persistent memory queues the affected scope's coalesced sidecar payload when a later
build needs one; forgetting an ephemeral memory queues nothing for that scope.

Forget event ordering remains authoritative deletion first, generation invalidation second, then
the single `OnForgottenAsync` notification with the existing `wasDeleted` value. Forget handlers
must not resurrect a deleted ephemeral entry or make a stale fact view visible while the event is
being drained.

## Preserved behavior and compatibility

The change is persistence scheduling only. It preserves all of the following:

- **Sidecar format and token validation.** `facts.json` keeps its current serialization and
  `EntityFactSidecar.ComputeToken` inputs. A token mismatch still rebuilds from entries; a matching
  sidecar can still be reused. No cache is trusted over the current index.
- **Cross-scope aggregation.** Unfiltered `GetEntityFactStore()` still merges all readable
  persistent scopes plus ephemerals exactly as before.
- **Filtered facts queries.** `facts` with normalized `scopes` or `excludeTopics` continues to use
  `EntityFactDerivation.BuildStore(store.ListScoped(...))`, not the unfiltered cache. Empty or
  blank filters continue to normalize to the unfiltered cached path.
- **Query semantics.** `entities`, `profile`, and `intersect`, including entity ambiguity,
  co-occurrence, dates, limits, and response formatting, are unchanged.
- **In-process freshness.** A mutation invalidates the merged view before any later facts query
  can use it. A racing build cannot replace a newer generation.
- **Sidecar scope layout.** Persistent scopes retain their existing `facts.json` location;
  ephemeral data remains memory-only.
- **Error handling.** Sidecar I/O remains best-effort for a derived artifact. Failed writes stay
  dirty and retryable; they never fail the already-completed authoritative memory write.
- **Token and payload stability.** Coalescing does not alter deterministic derivation, ordering,
  or serialized content. It only avoids writing superseded full payloads.

## Non-goals

- No changes to the NMP/2 artifact format, index format, sidecar schema, or derivation rules.
- No facts database, append-only journal, partial-file patching, memory-mapped index, or remote
  cache service.
- No HTTP/REST server, API endpoint, remote store, or web UI integration.
- No change to BM25, entity scoring, filtered-query semantics, embedding storage, or vector
  reindex behavior.
- No guarantee that a sidecar is on disk immediately after a facts query or write.
- No synchronous flush on every MCP tool response, CLI subcommand, event, or test teardown.
- No per-test `BackgroundWork` drain; that pattern is known to cause severe suite slowdown and is
  not part of production lifecycle integration.
- No suppression of generation invalidation merely because a sidecar write is deferred.
- No deletion of a stale sidecar as a substitute for rebuilding it.

## Byte-counted acceptance criteria

The implementation is accepted only when measured with byte-counting file instrumentation, not
mtime-only observation:

1. On the measured `N=46` repository-copy corpus, one `remember` with a 1,302-byte new payload
   writes **no `facts.json` bytes before drain**.
2. For that same operation, one successful drain writes at most **one complete replacement per
   changed sidecar path**. It must not write the superseded intermediate payloads.
3. Repeating ten fact-changing mutations and issuing one drain writes at most **one final
   `facts.json` payload per affected scope**, rather than ten full payloads. The exact payload
   bytes must equal the result of rebuilding from the final authoritative entries.
4. The measured pre-drain write volume for a facts-only cache miss is **0 bytes to `facts.json`**;
   the post-drain volume is the serialized final sidecar length, not that length multiplied by the
   number of intermediate generations.
5. No acceptance run may count append-only `vectors.bin` bytes as a facts rewrite. The report must
   identify file paths and byte deltas separately.
6. A successful drain leaves `HasDirtyFactCache == false` and no pending sidecar payloads.
7. A failed or canceled drain leaves the newest pending payload queued, reports dirty state, and
   permits a later drain with a fresh token to write the same final payload exactly once.
8. A mutation racing a build must never publish a sidecar whose token or entity content reflects an
   older generation than the final authoritative index. A later drain may write only the newest
   coalesced generation.
9. Ephemeral remember/forget operations must cause **0 bytes** of `facts.json` writes while still
   changing the unfiltered in-memory fact result immediately.
10. Filtered facts queries must cause **0 bytes** of sidecar writes as a consequence of their
    temporary filtered derivation; only the unfiltered cache path may queue persistent sidecars.

## Complete test matrix

| Area | Case | Required assertion |
|---|---|---|
| Scheduling | First unfiltered cache build with a missing sidecar | Facts are immediately correct; byte counter records no sidecar write before drain; dirty state is true. |
| Scheduling | Matching sidecar token | Sidecar is read and reused; no rebuild and no write are performed. |
| Scheduling | Stale token | Facts are rebuilt from current entries; stale content is not returned; write remains deferred until drain. |
| Scheduling | Multiple mutations before drain | One pending payload per path; intermediate generations are never written. |
| Scheduling | Multiple scopes before drain | Each affected persistent scope receives its own final payload exactly once. |
| Scheduling | Unchanged scope in a cross-scope rebuild | Its valid sidecar is reused and it receives no write. |
| Generation | Mutation during a build | Older build cannot become the shared cache or clear dirty state for a newer generation. |
| Generation | Newer payload replaces older queued payload | Queue contains the newest generation and serialized content only. |
| Generation | Successful write with another path pending | Dirty state remains true until every pending path is complete. |
| Failure | Sidecar directory/file unavailable | Write failure leaves payload queued, dirty state true, and facts query successful. |
| Failure | Retry after filesystem repair | Fresh drain writes the queued final payload and clears failure/dirty state. |
| Cancellation | Drain timeout while write is retrying | Payload is requeued; in-flight count returns to zero; dirty state remains true. |
| Cancellation | New write after timed-out drain | New work receives a live token and is not canceled by the prior drain. |
| Cancellation | Retry after canceled drain | Fresh worker writes the queued payload and clears state only after success. |
| Lifecycle | One-shot CLI exit | Program drain flushes pending facts before process exit when within the timeout. |
| Lifecycle | `scri serve` shutdown | Shutdown drain uses the same bounded flush contract as one-shot commands. |
| Lifecycle | Work fired during drain | Drain waits for work registered while the drain is in progress. |
| Lifecycle | Disposed store | Disposed store contributes no later flush work. |
| Ordering | Maintenance before entity typing | One event sequence observes the maintenance mutation before typing's fact read; no superseded sidecar is written. |
| Ordering | Event handler failure | Durable artifact/index and facts view remain correct; unrelated handlers and drain accounting continue. |
| Ordering | Vector failure after memory write | Durable memory and facts changes remain visible; vector failure does not roll them back. |
| Ephemeral | Ephemeral remember | Unfiltered facts include the new entity immediately; no persistent sidecar write is queued. |
| Ephemeral | Ephemeral append/update | Entity changes are reflected immediately and generation advances. |
| Ephemeral | Ephemeral forget after warm cache | Forgotten entity disappears immediately from unfiltered facts. |
| Ephemeral | Forget missing ephemeral | Existing not-found result is preserved and the fact generation is not spuriously changed. |
| Ephemeral | Persistent forget | Persistent facts disappear immediately; affected sidecar is rebuilt/coalesced for the next drain. |
| Queries | `facts entities` unfiltered | Uses the cached cross-scope store and preserves result/limit semantics. |
| Queries | `facts profile` unfiltered | Preserves count, date span, co-occurrences, and timeline. |
| Queries | `facts intersect` unfiltered | Preserves all-entity intersection semantics. |
| Queries | Explicit scopes | Builds from the normalized filtered listing and does not write a sidecar. |
| Queries | `excludeTopics` | Excludes topics before derivation and does not write a sidecar. |
| Queries | Empty/blank filters | Normalizes to the unfiltered cached path rather than an unnecessary full filtered rebuild. |
| Compatibility | Sidecar token round trip | A drained sidecar reloads as valid on a fresh store and yields identical facts. |
| Compatibility | Deterministic payload | Coalesced final serialization is byte-equivalent to a direct final rebuild. |
| Measurement | Byte-counted amplification regression | Report confirms no pre-drain `facts.json` bytes and bounded one-write-per-path post-drain behavior. |

## Self-review

- **Architecture:** References only the current local stdio CLI/MCP/plugin architecture, `FileMemoryStore`,
  `BackgroundWork`, and existing event sinks. It does not introduce the removed HTTP server or REST
  store.
- **Coverage:** Includes the measured ~65x root cause, full-rewrite mechanism, drain/shutdown-only
  decision, coalescing, generation and failure/cancellation rules, lifecycle and event ordering,
  ephemeral forget invalidation, preserved sidecar/token/filtered-query behavior, non-goals,
  byte-counted criteria, and a complete test matrix.
- **Consistency:** The design treats facts as derived and rebuildable, keeps durable writes ahead of
  events, preserves current filtered-query selection, and does not claim immediate sidecar
  durability. Cancellation explicitly preserves dirty work and later retryability.
- **Placeholders:** None. All acceptance cases name a concrete behavior, boundary, or measured byte
  outcome.
- **Scope discipline:** Documentation only. No source files or tests are changed, and no tests or
  commits are requested by this artifact.

**Self-review result:** approved for implementation planning; no unresolved contradictions or
placeholders found.
