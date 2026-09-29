# Design: Hybrid entity-span extraction (Piece 2)

**Status:** Approved (design)
**Date:** 2026-07-24
**Scope:** Entity-span extraction only. In-content event-dates and quantities are **deferred**
to a later faithful-binding piece (see Roadmap) — deliberately, not for lack of time.

## Motivation

`AttributeExtractor` today reads only *structure* — frontmatter, line-start `key: value`,
`**Bold:**` labels, and `key:value`-shaped agent keywords. A freeform sentence like
"Jolene bought Seraphim in France" yields **zero** attributes, so it contributes **zero**
entities to the graph and (post-Piece-1) zero facts to the `EntityFactStore`. The aggregation
machinery Piece 1 shipped — counts, co-occurrence, intersection, disambiguation — is only ever
as populated as the agent's explicit tagging. Most real memories are mostly prose.

Piece 2 closes that gap: pull entity spans out of prose mechanically so the fact store has
something to aggregate, **without asserting anything we can't stand behind.**

## Fidelity discipline (the principle that shaped this scope)

Not all extraction compromises are equal, and the distinction is load-bearing:

- **Unavoidable compromises are acceptable.** Piece 1's fact date (`UpdatedAt ?? CreatedAt`) is
  one: a fact *must* be dated somehow, and "when the content was last authored" is the most
  honest thing we can say without an event model. You have to pick something.
- **Avoidable binding claims are not.** A per-memory `EntityFact.EventDate` or
  `EntityFact.Quantity` asserts a **binding** — "*this* is Jolene's count", "*this* is when
  Jolene's event happened" — that mechanical extraction cannot justify. Rules can see that "3",
  "June", and "Jolene" co-occur in one memory; *which clause modifies which entity* is a
  dependency-parse problem we do not solve without real NLP. Per-memory attachment is the store
  asserting a relationship it cannot read, and it **compounds**: "how long between A and B"
  would multiply the Piece-1 date proxy × an event-date guess × a binding guess and present the
  product as one clean number.

**Entity spans have no such problem.** `mention:seraphim` asserts only *"seraphim appears in
this memory"* — true, no binding, no guess. Typing (`mention:` → `thing:`) is a **floored,
recoverable** upgrade: a wrong type still leaves a true presence fact underneath, the agent's own
attribute always overrides it, and it is re-derivable. It never compounds.

So Piece 2 builds the clean part and **refuses the avoidable compromise**: no bound date/quantity
fields, no sidecar re-version. Dates and quantities wait for a mechanism that can bind faithfully
(sentence-level or LLM-assisted extraction), built as its own piece.

## Core concept

Two-stage, mirroring the codebase's existing fast-write / slow-embed split:

1. **Rules propose spans inline (synchronous, embedder-free).** At write time, in the
   `AttributeExtractor` pipeline, a mechanical rule finds proper-noun span candidates and writes
   each as `mention:<value>`. This is a **true presence fact**, needs no embedder, and works with
   the embedder absent. The write stays fast.
2. **Embedder promotes/links spans (asynchronous, best-effort).** An out-of-band
   `IMemoryEventSink` — sibling to the existing `CoreEmbeddingEventHandler` — types each
   `mention:<v>` into `person/place/org/thing/topic:<v>` when confident, using the embedder.
   `IsAvailable`-guarded, wrapped in best-effort try/catch, never blocks or fails the write.

`mention:` is the honest default; typing is the async upgrade. With the embedder absent or unsure,
the memory simply keeps `mention:` entities — which **already aggregate** in both `EntityGraph`
and `EntityFactStore` (verified: `EntityGraph.ParseEntityAttributes` yields a node for *every*
attribute key, and the fact store consumes the same union).

## Stage 1 — inline span rules (mechanical, deterministic)

A new extraction path in `AttributeExtractor` (LLM-free, regex/scan, run at write time alongside
the existing `Extract`), producing `mention` attribute values:

- **Candidate = capitalized token run.** Sequences of capitalized words (`Seraphim`,
  `New York`, `Jolene`) are proper-noun candidates; multi-word runs are kept **whole** (one span
  = one value), matching entity-node identity which keeps the value whole.
- **Value normalization** identical to `EntityRef.Of` / graph node identity: `Trim()` +
  `ToLowerInvariant()`. `Seraphim` → `mention:seraphim`.
- **Exclusions (precision guards):**
  - Nothing inside code fences or inline code (reuse the fence tracking `Extract` already has).
    Double-quoted prose names are **kept** — code is the pollution source, a quoted name is still a
    real entity.
  - **Stop-list guard (the whole precision mechanism).** A capitalized token joins a run only if it
    is not in a stop-list of pronouns/articles/weekday/month names **plus common sentence-openers**
    (imperative verbs, conjunctions, auxiliaries, contractions: `Set`, `Run`, `The`, `When`, …).
    This filters sentence-leading common words while keeping sentence-leading **subjects** (real
    names/places). There is **no positional or echo rule** — an earlier "drop sentence-initial lone
    caps unless echoed" design was rejected because it also dropped leading subjects (`Jolene
    bought a snake.` → nothing), the single most important entity, and could not mechanically
    distinguish a leading name (`Jolene`) from a leading verb (`Set`). Stop-list is uniform,
    defensible, and tunable.
  - A per-memory cap on emitted spans (`MaxMentionSpans`, bounded like `KnownAttributes.MaxAttributes`).
- **`mention` is registered as multi-valued** in `KnownAttributes` (a memory mentions several
  spans). Today only `EntityKeys ∪ ReferenceKeys` are multi-valued; an unregistered `mention`
  would be scalar/newer-wins and collapse to a single span. This is the one `KnownAttributes`
  change.

Stage 1 slots into the existing merge chain **below the agent's authoritative map**:
`routedAttributes < content Extract < refAttributes < mention spans < explicit agent attributes`.
An agent's explicit `person:jolene` therefore always wins over a rule-proposed `mention:jolene`.
(Exact insertion point mirrors both call sites — `MemoryTools.Core.cs` and
`MemoryOrchestrator.cs` — so MCP/CLI and server stay in parity.)

## Stage 2 — async embedder typing (hybrid: nearest-entity → centroid → mention:)

A new `IMemoryEventSink` runs after the write. For each value currently under `mention:` on the
stored/updated memory, assign a type with a **floored, three-step** procedure:

1. **Nearest existing typed entity.** Embed the value; compare to embeddings of existing typed
   entity values in the store (`person/place/org/thing/topic`). If the nearest is above a link
   threshold and unambiguously one type, promote to that type. This links `Seraphim` to an
   existing `thing:seraphim` and bootstraps off agent-typed data, sharpening as the store grows.
2. **Prototype centroid.** Otherwise compare to per-type centroid vectors (precomputed once from
   small seed vocabularies per type). If the argmax clears a typing threshold, promote to that
   type. This gives a cold-start answer when no typed neighbor exists.
3. **Stay `mention:`.** Otherwise leave it untyped. Never guess below threshold.

Thresholds are set **conservatively** — `mention:` is the expected common case; typed keys are
rare-but-right. Static bag-of-words embeddings (Model2Vec) type proper nouns only roughly, so the
floor matters more than recall here.

**Promotion semantics:**
- Promotion **moves** the value from key `mention` to the typed key (remove from `mention`, add
  under `<type>`). It does **not** duplicate — node identity is `type:value`, so keeping both
  `mention:seraphim` and `thing:seraphim` would double-count in `MentionCount`/aggregation.
- **Agent-authoritative:** only values living under `mention:` are ever promoted. A value the
  agent explicitly placed under a typed key is never touched.
- **Idempotent:** re-running on an already-typed memory finds nothing under `mention:` and makes
  no mutation — a natural termination property (see re-entrancy below).
- Persisting the promotion in `entry.Attributes` keeps `entry.Attributes` the sole authority and
  the sidecar a pure cache (write-once, read-many; no embedder on the read/derivation path).

### Write-back, re-entrancy, and concurrency (pinned down)

Verified against the code, and settled here rather than left to the plan:

**1. No re-entrancy — by construction.** `IMemoryEventSink` events are dispatched **only at the
tool layer** (`MemoryTools.Core.cs` fires them via `FireEventSinkAsync`; `EmbeddingReindexer` and
migration are the only other dispatchers). The store's mutation methods (`Upsert`, and the new
primitive below) do **not** dispatch sinks — exactly as `CoreEmbeddingEventHandler` writes the
*vector* store from inside a sink without re-firing anything. So a promotion that writes back
through the store cannot re-enter the sink pipeline. There is no loop to break.

**2. Fire-and-forget ⇒ a guarded write-back is mandatory.** `FireEventSinkAsync` runs the sink as
`_ = Task.Run(...)` — detached, **not awaited**, executing *after* the write tool has returned. A
subsequent (or concurrent) user edit to the same memory can therefore interleave with an
in-flight promotion. A naive `LoadIndex` → edit-snapshot → `Upsert` would clobber the newer write.
Promotion must be a **guarded read-modify-write under the scope write lock**, via a new atomic
store primitive:

```csharp
// Atomic on FileMemoryStore: load current entry, mutate, persist — all under the one
// per-scope write lock. mutate returns null to signal "no change" (no write, no invalidation).
// Default interface impl = load→mutate→Upsert (non-atomic; acceptable for the in-memory/test
// stores that have no concurrent writers). Fires NO sinks.
bool TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate);
```

The typing sink's `mutate` closure, run against the **current** entry (not the event snapshot):
- for each planned `(value → type)`, move the value **only if it is still under `mention:`**;
- if nothing moved, return `null` → no write;
- otherwise return the entry with the value shifted from `mention` to the typed key.

This is simultaneously the fix for three things: it **never clobbers a newer user write** (it
re-reads the current entry and only shifts attribute values, never rewrites content), it is
**inherently idempotent** (an already-typed value is no longer under `mention:` → skipped), and it
**serializes concurrent promotion tasks** through the existing write lock. Idempotency also remains
the backstop for the (now-impossible) re-fire case: empty `mention:` → no-op → terminates.

**3. No new lock ordering.** `TryUpdateEntry` reuses `Upsert`'s existing, Piece-1-fixed acquisition
order (per-scope index write lock → `_factStoreLock`, the latter only to null the cache and bump
`_factStoreGeneration`). The sink takes no second lock and introduces no inversion — the class of
bug Piece 1 hit does not recur here.

**4. `TermFrequencies` is deliberately left untouched.** Promotion edits `Attributes` only. This is
correct, not a shortcut:
- The promoted **value token is unchanged** (`seraphim` whether under `mention:` or `thing:`); only
  the attribute **key** token differs.
- Every attribute-aware consumer reads **live `entry.Attributes`**, not the stored TF:
  `WeightedAttributeScorer`'s attribute channel (`WeightedAttributeScorer.cs:365`), `EntityGraph`,
  and `EntityFactStore` all re-read current attributes — so the promotion is reflected everywhere
  that matters automatically.
- The stored TF feeds **only** the BM25 content/keyword channel (`WeightedAttributeScorer.cs:293`),
  where the sole drift is a structural key token (`mention` vs `thing`) that is not a meaningful
  query term.
- A full TF recompute would be **worse**: the stored entry does not retain agent-vs-auto keyword
  provenance, so re-seeding could not reproduce the write-path's +5/+2 keyword boosts and would
  corrupt real query tokens to fix a near-inert one.

**5. Invalidation & sink composition.** `TryUpdateEntry` nulls `_factStore` / bumps
`_factStoreGeneration` (same as `Upsert`), so the next fact-store query rebuilds with the typed
attributes. `CompositeEventSink` runs the embedding sink then the typing sink **sequentially** on
the one detached task; typing changes only metadata (never content), so it never invalidates the
embedding sink's vectors and the order between them is irrelevant.

## Data model & persistence

- **`EntityFact`, `EntityFactStore`, and the sidecar FORMAT are unchanged; version stays v1.**
  `mention` and the promoted typed keys are ordinary attribute keys the existing derivation already
  handles. **One Piece-1 correctness fix is required:** the sidecar validity token
  (`EntityFactSidecar.ComputeToken`) must become **content-aware** — hashing each entry's attributes,
  not only `(id, effective-date)`. Without it, an attribute-only promotion (`mention:x → person:x`)
  leaves the token unchanged, so `GetEntityFactStore` trusts a stale `facts.json` and never
  re-derives. The naive alternative — stamping `UpdatedAt` on promotion to force a token change — is
  rejected: it **re-dates every fact in the memory to the async-typing moment**, exactly the
  avoidable date compromise this piece refuses. With a content-aware token, promotion changes no
  timestamp and fact dates stay honest. Format is untouched; existing `facts.json` rebuild once.
- **Only `KnownAttributes` changes:** register `mention` as multi-valued.
- No new `EntityFact` fields (`EventDate`/`Quantity` explicitly not built — see Fidelity
  discipline and Non-goals).

## Relationship to Piece 1 and `EntityGraph`

**Fully additive; the Piece-1 guardrail stays intact.** Because `mention:` and the promoted typed
keys are plain attribute keys, `EntityFactStore` and `EntityGraph` still derive entities from the
identical `ParseEntityKeywords ∪ ParseEntityAttributes` union — no reserved-key exception, no
divergence. The Piece-1 test that guards store/graph agreement continues to hold unmodified.

`EntityGraph` remains the per-query PPR scoring contributor; Piece 2 only enriches how populated
the shared attribute set is. Graph unification is still Piece 5.

## Components & boundaries

- **Span rule** — pure function in/near `AttributeExtractor` (`content → mention values`). No I/O,
  no embedder. Unit-testable in isolation.
- **Merge-chain insertion** — the two write call sites (`MemoryTools.Core.cs`,
  `MemoryOrchestrator.cs`) fold `mention` spans in below agent attributes, in parity.
- **Typing sink** — a new `IMemoryEventSink` holding the embedder, the per-type centroids, and the
  promotion logic. Separate from `CoreEmbeddingEventHandler` (single responsibility), best-effort,
  `IsAvailable`-guarded. Writes promotions back via `TryUpdateEntry` only.
- **`TryUpdateEntry` store primitive** — the atomic guarded read-modify-write on `IMemoryStore`
  (atomic override in `FileMemoryStore`, load→mutate→`Upsert` default elsewhere). Fires no sinks;
  reuses `Upsert`'s lock ordering and fact-store invalidation. The single seam through which the
  typing sink mutates the store.
- **Type centroids** — a small, explicit seed vocabulary per type, embedded once and cached. Kept
  separate and data-driven so seeds can be tuned on real-workflow signal, never a benchmark fit.

## Testing

- **Span rule** — proper-noun runs extracted; multi-word kept whole; code fences/quotes excluded;
  sentence-initial lone caps excluded unless echoed mid-sentence; stop-list respected; per-memory
  cap; value normalization matches `EntityRef.Of`; prose with no proper nouns → no `mention`.
- **Multi-valued `mention`** — several spans in one memory all survive the merge.
- **Merge precedence** — agent `person:x` overrides rule `mention:x`; `mention` folds below agent
  attributes at both call sites.
- **Embedder-absent path** — with the provider stubbed unavailable, spans remain `mention:` and
  still aggregate in `EntityFactStore` (Facts/MentionCount/Neighbors over `mention:` values).
- **Typing (embedder present, stubbed vectors)** — nearest-entity promotion; centroid fallback;
  below-threshold stays `mention:`; promotion **moves** (no `mention:`+typed duplication);
  agent-placed typed values untouched; idempotent re-run is a no-op.
- **Write-back re-entrancy** — a promotion via `TryUpdateEntry` fires no sink; no loop.
- **Concurrent-write safety** — a promotion whose `mention:` value was removed/edited by an
  interleaving user write (simulating the fire-and-forget gap) makes no change and does **not**
  clobber the newer write; content is never rewritten. Idempotent re-run is a no-op.
- **`TryUpdateEntry` atomicity** — load-mutate-persist happens under one lock; `null` mutate result
  writes nothing and does not invalidate; a real change invalidates the fact store.
- **TF left intact** — after promotion, BM25 content scoring is unchanged and attribute-channel /
  graph / fact-store results reflect the new typed key (guards the "don't recompute TF" decision).
- **Store/graph agreement** — the Piece-1 guardrail test still passes with `mention:` present.
- **Determinism** — same content + same store → same spans and same typing.

## Roadmap update

The original five-piece decomposition is revised: what was "Piece 2: hybrid deterministic
extraction (rules + embedder spans + dates + quantities)" is split, because dates/quantities need
faithful binding that entity spans do not.

1. EntityFactStore foundation — **done** (merged).
2. **Hybrid entity-span extraction** — *this spec.*
3. Auto-surfacing (deferred).
4. Explicit aggregate tool + CLI (deferred).
5. Graph unification (deferred).
- **New (deferred): faithful date/quantity extraction** — in-content event-dates and quantities
  bound to the specific entity/clause they modify (sentence-level or LLM-assisted), materialized as
  `EntityFact.EventDate` / `EntityFact.Quantity` with a sidecar version bump. Built only when the
  binding can be done faithfully; sequenced against 3/4 by value.

## Non-goals (explicitly out of scope for Piece 2)

- Any in-content event-date extraction or `EntityFact.EventDate` field.
- Any quantity/duration extraction or `EntityFact.Quantity` field / numeric aggregate query.
- Any sidecar format change (stays v1).
- Any change to `EntityGraph` or the scoring path (Piece 5).
- Any consumer surfacing (Piece 3) or MCP tool / CLI verb (Piece 4).
- Per-attribute provenance or dependency-parse-level clause binding.
