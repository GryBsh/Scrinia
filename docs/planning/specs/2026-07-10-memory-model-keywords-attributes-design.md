# Design: Simplify the memory model to `keywords` + `attributes`

Date: 2026-07-10
Status: Approved (brainstorm) — pending spec review
Scope: Scrinia.Core + Scrinia.Mcp product surgery + tool rename + guide rewrite + migration

## Motivation

This is a real-workflow simplification, **not** a benchmark exercise. From reviewing cached items across runs and
from our own use, Scrinia is strong at developing and recalling memory from an agent's own experience, so it should
excel at **turn-by-turn** ingestion — yet in practice it is stronger at full-session import and weaker turn-by-turn.

Root cause (measured, not theorized): the current model overloads `keywords` with two conflicting jobs, and the guide
actively pushes agents toward the wrong one.

- The guide (`src/Scrinia.Mcp/prompts/guide.md`) tells agents to *"prefer typed keywords over bare terms,"* frames
  typed keywords as *"exact-match retrieval signal and entity-graph input,"* and says *"bare keywords only help exact
  lexical matching"* — i.e. it treats plain retrieval keywords as inferior and funnels the agent's keyword budget into
  the entity graph.
- The graph wants **consistency** (`"name an entity the same way every time"`), which at turn granularity makes many
  thin memories carry identical typed tokens (`person:maria`, `topic:contract` everywhere). A query then matches all of
  them alike — overlapping relevance cancels and the right turn cannot separate from the pack. Full-session import dodges
  this because one fat memory wins outright.
- Meanwhile the graph earns nothing on the workload we can see: LoCoMo scores are **identical with and without the
  entity graph**. So agents pay the overlap cost with no relating payback. (LoCoMo is single-hop-lexical and cannot see
  the graph's upside; that is a reason to evaluate the graph separately, not to gut it.)

Two other constructs — `anchors` and `tags` — show little real use, and add surface without pulling weight.

The fix is to **separate the two axes**: `keywords` optimize for *distinctiveness* (lexical retrieval), `attributes`
optimize for *consistency* (controlled entities/facets, incl. the graph). Separated, they stop cancelling each other,
and the tool surface collapses toward a simpler, filesystem-like shape that agents use more reliably.

## Non-goals

- Not tuning for LoCoMo or any benchmark. Success is real-workflow retrieval quality and agent usability.
- **Node identity `(key, value)` IS in this plan set** (Plan 2) — it is a correctness consequence of Plan 1's uniform feed
  (arbitrary keys currently collapse to bare values and cross-link on a live signal), not deferrable tuning. Plan 2 replaces
  the conflict-split heuristic (`ResolveNodeKey` + the two-pass `realTypesByValue`) with a flat `type:value` node for every
  key; `SurfaceOf` already strips the prefix for seeding, so seeding is unaffected. Non-parity by design (it deliberately
  changes the live graph: non-real-type keys become type-scoped; legacy `entity:` markers stop bridging to typed entities).
- Still deferred to a separate graph-**tuning** effort (genuinely quality/perf knobs, evaluated on a relating-heavy
  workload): how propagation *consumes* the per-key weight table, and degree-based anti-hub down-weighting. This spec
  defines the weight table; Plan 2 fixes node identity; the tuning effort owns propagation weighting + anti-hub.
- Not changing the embeddings/hybrid retrieval shipped on `feature/agentic-arm-full-pipeline`.

## The model

A memory is four filesystem-native things:

- **path** — location (unchanged).
- **content** — payload; an array of independently-retrievable chunks (unchanged). Search results display the first
  ~200 chars of content. **`description` is removed entirely** — no param, no field, no auto-derive (agents rarely wrote
  it and it duplicates the content preview we already show).
- **keywords** — pure lexical retrieval terms: the distinctive words **and** the query-phrasings a searcher is likely to
  use (the job `anchors` did, absorbed here). Merged with auto-extracted terms, seeded into BM25. First-class.
- **attributes** — a structured map `key → [values]` (chosen over an array of `key:value` strings: explicit keys, no
  colon-parsing, malformed entries can't misfile). Open vocabulary, **uniform mechanic + curation by weight, not by gate:**
  - **Uniform substrate:** *every* attribute — known or arbitrary — is a graph node, a facet, and an exact-match scoring
    signal. The mechanic never special-cases a fixed key list. NOTE (corrected after implementation): true per-key
    type-scoping is **not** free — the current graph `ResolveNodeKey` only type-scopes among the 5 legacy `RealTypes` on
    genuine conflict and collapses every other key to its bare value, so arbitrary keys can cross-link on a shared value
    (`status:done` + `outcome:done` → one node; `tag:paris` merges with `place:paris` unless "paris" is disputed among 2+
    real types). Making node identity uniformly `(key, value)` is a node-identity change that belongs to the deferred
    entity-graph tuning effort (see Non-goals); this feature only widens *what feeds* the graph.
  - **Curation by weight (soft, not gate):** a per-attribute-key **weight table** scales each key's *influence*. The
    recommended keys weigh heavy; arbitrary keys get a low default. This replaces the old "only 5 keys graph" gate with a
    tunable knob — bump a key or add a key without redesign, and no single hub value dominates.
  - **Behaviors as per-key add-ons** on top of the substrate: `file`/`ref` → **drift detection** (dependency hashing);
    `review` → review-reminder workflow (absorbs `reviewAfter`/`reviewWhen`). Absence of a handler = graph + facet +
    exact-match only. New behaviors are additive.
  - **Guided known set:** `person`/`place`/`org`/`thing`/`topic` (entities) + `file`/`ref` + `review` are what the guide
    teaches, so they're what agents write most — but the engine treats any key uniformly.

Guiding intent: **keywords = distinctiveness; attributes = consistency, curated by weight.** The known set is guidance +
weight + a behavior registry, not a mechanic gate.

### `KnownFields` reorganization

`KnownFields` stops being "`EntityKeys` gate the graph." It becomes three data-only concerns, each iterable as an add-on:
1. **Recommended keys** — the guided set (for the guide + tool descriptions).
2. **Weight table** — `key → weight`, consumed by field scoring now and by the graph later (see Scoring / Non-goals).
   Seed from current effective field weights (parity); tune on real-workflow signal, not a benchmark.
3. **Behavior registry** — `key → handler` for `file`/`ref` (drift) and `review` (reminders).

### Example

```
memory_remember({
  path: "/findings/acme-renewal",
  content: ["Acme signed the annual renewal; Maria is the new account owner."],
  attributes: {
    person: ["maria"],
    org: ["acme"],
    topic: ["contract renewal"],
    review: ["2026-08-01"]
  },
  keywords: ["renewal", "signed", "annual contract", "account owner"]
})
```

Trivial case stays trivial: `memory_remember({ path: "/temp/scratch", content: ["..."] })`.

## Tool naming: drop the redundant `memory_` prefix

Clients prefix tool names with the MCP server name (`ServerInfo.Name = "scrinia"`, `Program.cs:261`), so today the agent
sees `scrinia_memory_remember` — the `memory_` is redundant, and inconsistent with `guide`, which is already bare. Rename
the agent-facing tool names to drop it: `remember`, `append`, `recall`, `search`, `list`, `forget`, `compact`, `link`,
`reconcile`, `restore` (and `guide`, unchanged) → the agent sees `scrinia_remember`, etc.

- Change only the `[McpServerTool(Name = …)]` strings. **Keep the C# method names** (`MemoryRemember`, …) — this is the
  agent-facing win and it bounds the blast radius (the bench harness calls `tools.MemoryRemember` directly).
- **Rename ripples to two production surfaces that key off tool-name strings** — both must move in lockstep:
  - `src/Scrinia.Server/Auth/McpPermissionFilter.cs` (tool name → required permission map);
  - the hook writers (`AgentsMdWriter` / `SkillWriter`) that emit tool names into generated agent files.
- Product tests referencing the old names (`Scrinia.Server.Tests`, `Scrinia.Tests`) update with the rename.
- The benchmark harness defines its **own** tool names (`AgenticIngestPrompts`, a WIP file) and does not go through the
  MCP `Name`, so it is unaffected; realigning bench names to match is an optional follow-up, out of scope here.
- Compat: MCP tool names are discovered per session via `tools/list`, so a rename is not a persistent break; no aliases.

## Tool surfaces (all move together — a partial change is incoherent)

Model-change impact is isolated to three tools; the rest are rename-only (no signature change):

- `remember(path, content, attributes?, keywords?)` — **model change.** Removed params: `description`, `tags`, `anchors`,
  `reviewAfter`, `reviewWhen`, `codeRefs` (codeRefs → `attributes.file`; review → `attributes.review`).
- `append(path, content, attributes?, keywords?)` — **model change.** Same shape; drops `tags`/`anchors`/`description`.
- `search(query, …, attributeFilter?)` — **model change.** The `tags` AND-filter becomes an **attribute filter** (by key,
  or by `key→value`). `pathPrefix`, `context`, `excludeTopics`, `scopes` retained.
- `list` — **output only:** the summary shows attributes instead of a tags column; **signature unchanged.**
- `recall` / `forget` / `compact` / `link` / `reconcile` / `restore` / `guide` — **rename only**, no signature change;
  their guide sections are refreshed for the new model.
- Internal (non-agent-facing) `UpdateMeta` carries `keywords`/`description`/`review` and is updated for the new model
  internally; it is not an MCP tool.

## Scoring / retrieval

`WeightedFieldScorer`:
- Remove `RetrievalAnchors`, `Tags`, and `description` field-scoring paths.
- Add attribute-value scoring: exact / contains match on attribute values, scaled by the per-key **weight table** (see
  the KnownFields reorg). Seed weights from the effective weight the retained signals have today (entity/topic values
  score via typed keywords/fields; tags score exact/contains) — parity first, tune later.
- `keywords` → BM25 term-frequency seeding (unchanged mechanism); auto-extraction unchanged.
- Entity-graph node source = **all attributes uniformly**, `(key, value)` (via the fields path), not a fixed key list.
  Legacy typed keywords still read through dual-read (see Migration). Applying the weight table *inside* graph propagation
  and adding hub/degree down-weighting ride with the separate graph-tuning effort (see Non-goals); this spec only changes
  what feeds the graph (everything) and defines the weight table it will consume.
- Embeddings / hybrid contributor: untouched.

## Guide rewrite (first-class deliverable — the biggest lever)

The guide is how the agent learns the whole tool. It must be **rewritten as a single cohesive, authored document**, not
the current accreted-commentary form with new framing grafted on. Requirements:

- Covers the mental model (path / content / keywords / attributes, filesystem framing) and **every** `memory_*` tool
  (remember, append, recall, search, list, forget, link, reconcile, compact, restore) — when and how to use each.
- Makes `keywords` first-class ("the distinctive terms *and* the phrasings a searcher would use"); deletes the
  "prefer typed keywords over bare terms" / "bare keywords only help exact matching" framing.
- Teaches `attributes` as the uniform linking + faceting layer (any key links; the guided known set is what to reach for),
  names the behavior-bearing keys (file/ref drift, review reminders), and does NOT steer agents to funnel everything into
  the graph.
- Consistent instructional voice throughout; reads as an authored manual.
- The MCP tool `[Description(...)]` strings are rewritten to match and stay consistent with the guide.
- **Acceptance gate:** a documentation / ux-polish review pass — held to "reads as authored instruction," not "LLM notes."

## Migration

Read-time dual-read **plus** a `scri migrate` command (build it now; do not defer).

- **Dual-read (default, zero-risk):** on read/index, map old sidecars into the new model — legacy typed keywords + fields
  → attributes; `anchors` → keywords; `codeRefs` → `attributes.file` (+ `ref`); `reviewAfter`/`reviewWhen` →
  `attributes.review`. Existing memories keep working unchanged; new writes use the new schema. The entity-graph already
  dual-reads (`legacy typed keywords ∪ fields`), so there is precedent.
- **`scri migrate`:** rewrites every sidecar to the new schema so the compat-mapping code can eventually be retired. Runs
  explicitly; reports progress; idempotent.
- **Old `tags` map to a `tag:` attribute key.** Under uniform graphing every attribute links, so `tag:` values graph like
  any other key, with influence bounded by the `tag:` weight (a low default) so old free-form tags can't over-link. (This
  supersedes an earlier draft that carved `tag:` out of the graph — the weight table makes the carve-out unnecessary.)
  CAVEAT (corrected after implementation): with the *current* node identity, a `tag:` value collapses to its bare value
  and can merge with a same-valued `place:`/`topic:` node (see the model NOTE) — true `(key, value)` scoping arrives with
  the graph-tuning effort. New writes have no `tags` param — agents choose the attribute key directly.
- `description` on old memories is dropped on migration (content preview replaces it).

## Implementation units (for the plan; each independently testable)

1. **Attributes model in Core** — generalize `FieldExtractor` / `RouteKeywords` to the attributes map; reorganize
   `KnownFields` into recommended-keys + weight-table + behavior-registry (drop the `EntityKeys` graph gate); graph reads
   all attributes uniformly `(key, value)`; add `review` + `tag:` handling; keep file/ref + review behaviors.
2. **Scoring** — `WeightedFieldScorer` drops anchors/tags/description; adds attribute-value scoring scaled by the weight
   table, seeded at parity.
3. **Tool surfaces** — `remember` / `append` new signatures; `search` attribute filter; `list` attribute display;
   rewrite `[Description]` strings.
4. **Tool rename** — drop `memory_` from the `[McpServerTool(Name)]` strings (methods unchanged); update
   `McpPermissionFilter` name→permission map, the `AgentsMdWriter`/`SkillWriter` hook writers, and product tests. Can land
   with or just after Unit 3.
5. **Dual-read compat** — read-time mapping of old sidecars → new model (the safety net for 1–3).
6. **`scri migrate`** — one-time rewrite command, idempotent, progress-reported.
7. **Guide rewrite** — the cohesive authored document (mental model + every tool, new names) + doc/polish review gate.

## Plan 6 (cleanup) — execution decisions (locked 2026-07-12) and structure

Progress: Units 1–2 (Plans 1–2), 3 (Plan 3), 4 (Plan 4), and 6 `scri migrate` (Plan 5) are DONE and merged. `scri migrate`
was built ADDITIVELY (forward-fills legacy carriers into `Fields`/`Keywords`, does NOT clear them). Plan 5's dual-read is
the scorer keeping legacy blocks; there is no separate read-time synth (that lands in P6.1 below).

Research (2026-07-12) found the cleanup ("retire legacy fields") is NOT a mechanical reader-cutover — it is a
dependency-ordered SUB-SEQUENCE of ~6 plans, because: the WRITE paths (`Store`/`Append`/`UpdateMeta` + the Server twins
`MemoryOrchestrator`/`HttpMemoryStore`) still populate the legacy carriers; STJ silently drops unmapped members (removing a
member loses un-migrated data on next write); the drift baseline hashes live only in `CodeRefs`; the scorer's
`key:value`-in-`Keywords` dual-read has no migration behind it (`AttributeMigration.Forward` never routes `person:maria`-shaped
keywords into `Fields`); `EphemeralEntry` is a parallel record carrying 4 of the members; and there are ~150 test touchpoints.

**Locked design decisions:**
1. **Drift hashes:** KEEP a dedicated hash side-store — rename `CodeRefs` (path→hash) to a purpose-named `FileHashes` member
   as the `file` attribute's private drift baseline (active behavior state, not dead legacy). Drift repoints to it; no hash
   migration needed. `file` attribute *values* stay paths-only (searchable); hashes are the behavior's private state.
2. **Un-migrated safety:** READ-TIME auto forward-fill — hook `AttributeMigration.Forward` into the load seam
   (`FileMemoryStore.LoadIndexUnsafe`, the existing legacy-index auto-upgrade point) so any un-migrated sidecar upgrades
   transparently on read, BEFORE the members are removed. No user action required; STJ silent-drop can't lose data.
3. **`Description`:** FULLY REMOVE — all display falls back to the always-populated `ContentPreview` (accept the minor scorer
   text-match downgrade: description-contains was 10, ContentPreview is 5; dedup the two).
4. **`key:value` keywords:** extend `AttributeMigration.Forward` (and thus the read-time hook) to route `key:value`-shaped
   legacy `Keywords` into `Fields`, so removing the scorer's dual-read block doesn't regress pre-unification entries. (Required.)
5. **`EphemeralEntry`:** strip its legacy members in lockstep, for symmetry (its projection into `ArtifactEntry` breaks otherwise).

**Sub-sequence (each its own plan/branch/review; ordered so the build never breaks — cut readers/writers over BEFORE removing members):**
- **P6.1 Migration completeness** (additive, safe): extend `Forward` to route `key:value` keywords → `Fields`; add the read-time
  auto-forward-fill hook at `LoadIndexUnsafe`. No removals. — **DONE + MERGED** (branch feature/attributes-migration-completeness).
  - Routing scope = `RecommendedKeys` only; routed KEY canonicalized lowercase (survives JSON round-trip). Read-time hook writes back
    primary-scope sidecars once (guarded by MigratesTo); legacy-merge entries in-memory only.
  - Concurrency: write-back runs under LoadIndex's SHARED lock. Hardened with per-call Guid temp names (all 4 atomic writers +
    ScriniaArtifactStore twin), best-effort swallow ONLY at the write-back site, and FileRetry around sidecar reads (transient
    File.Move-replace lock → retry, not skip-as-corrupt). Shared/exclusive lock split means write-back only ever races readers.
  - **MEASURED entity-graph ranking impact** (decision: measure-first): `EntityGraph.ParseEntityFields` emits a PPR node for EVERY
    field key (tag/file/review too, pre-existing Plan-2 design), so read-time Fields population shifts the graph channel on
    un-migrated stores. A/B on a 30-doc representative corpus (hub tag on 12 docs) through the real scorer+graph path: **top-3 STABLE
    on every query** (genuine field/BM25 hits 100–630 vs graph boost capped +50); movement is low-relevance TAIL reshuffle; hub effect
    PRESENT but BOUNDED + self-diluting (PPR splits mass `p/docs.Count`, so bigger hub = thinner per-doc). Verdict: benign at the head →
    merged as-is. **Graph-tuning lever (deferred effort):** restrict `ParseEntityFields` to `EntityKeys` to stop tag/file/review
    bridging if ever undesired (costs intentional file/ref cross-linking) — NOT P6.1's job (would change already-migrated stores too).
- **P6.1.2 Anchors → `Fields["anchor"]`** (additive) — **DONE + MERGED** (branch feature/attributes-anchors-to-fields). Resolves the P6.2
  "anchors display decision": `Forward` routes `RetrievalAnchors` → `Fields["anchor"]` (first-class facet + display carrier) while KEEPING
  the anchors→`Keywords` fold ("keep both, additive" — keyword channel unchanged; redundant fold retired in P6.5). No `IsMultiValued`/weight
  change; scalar-replace-with-full-array mirrors the existing `tag` handling and is idempotent. **MEASURED entity-graph ranking impact**
  (same protocol as P6.1): BENIGN AT THE HEAD — #1 result identical on 7/7 queries, top-3 movement confined to sub-15-pt tail bands; the
  anchor-bridging prior HELD (17/18 anchor nodes degree-1; the one shared 3-doc anchor lifted a weak cluster member +7.9 pts / 0 rank
  positions; PPR self-dilution bounds it). Milder than the P6.1 tag case. Merged as benign.
- **P6.2 Reader cutover** (display/aggregation → `Fields`/`ContentPreview`; no model change) — **DONE + MERGED** (branch
  feature/attributes-reader-cutover). `list` description column → `ContentPreview`; `[stale]`/`[review?]` → `Fields["review"]`;
  summary tag-agg → `Fields["tag"]`; anchors display → `Fields["anchor"]` (legacy fallback); across MCP + CLI + Server + `AgentLoop`
  + `MemoryEndpoints`. Single shared read helper `Scrinia.Core.Search.AttributeReaders` (GetReviewStatus / PreviewText / DisplayTags /
  DisplayAnchors) — sources the carrier, falls back to legacy for non-forward-filled ephemeral; the fallback is removed with the field in P6.6.
  All 24 inventoried display sites cut over (whole-branch opus review verified completeness + cross-surface consistency: no missed
  in-scope site, no deferred site over-reached). **NOTE (byte-identical scope):** review/tag/anchor carriers are byte-identical to legacy
  (forward-fill); **Description → ContentPreview is an INTENDED display change** (decision 3 above) — ContentPreview is write-populated
  (`GenerateContentPreview`), NOT forward-filled from Description, so display now shows the content preview. Deferred (non-blocking):
  `GetReviewStatus` empty-`Fields["review"]`-array gate harmonization → P6.5; a direct chunk-composition test → optional.
- **P6.3 Writer cutover** — **DONE + MERGED** (branch feature/attributes-writer-cutover). Mode = **DUAL-WRITE** (user-locked): write paths
  populate `Fields` at write time AND keep writing legacy, so scoring is neutral this phase (scorer Math.Max keeps legacy 50/15; the scorer
  change lands uniformly + measured at P6.5). Mechanism: persistent `ArtifactEntry` sites apply the additive+idempotent `AttributeMigration.Forward`
  before persist (makes write==read; reuses the tested transform; `MigratesTo`=false for born-complete entries → reduces read-back write-churn);
  ephemeral `EphemeralEntry` sites inline-route tags/anchors → `Fields` (EphemeralEntry has no review/codeRefs); the 3 `ListEphemeral` projections +
  explicit-ctor `CopyMemory` paths now carry `Fields` (ephemeral never hits read-time forward-fill — this was the load-bearing silent-regression fix).
  `Store`/`Append`/`UpdateMeta` + `MemoryOrchestrator` all covered; request params kept, mapped server-side. `description` input + `CodeRefs` hash map
  unchanged (P6.6 / P6.4). **Scoring:** persistent neutral; EPHEMERAL anchor-exact 15→40 (Fields["anchor"] is a new signal ephemeral never got via
  forward-fill) — same mechanism measured benign in P6.1.2, removes a persist-vs-ephemeral asymmetry, unavoidable for ephemeral P6.6-readiness →
  merged as-is (user decision). Whole-branch opus review: READY, no Critical/Important.
- **P6.4 Drift rename** — **DONE + MERGED** (branch feature/attributes-drift-rename). `ArtifactEntry.CodeRefs` → `FileHashes` (the
  `file` attribute's private drift baseline: path→SHA hash). Carries `[property: JsonPropertyName("codeRefs")]` so the persisted sidecar key
  STAYS `"codeRefs"` — zero migration (decision 1); legacy sidecars still load into `FileHashes` (legacy-proving test: hand-written `"codeRefs"`
  sidecar loaded through the real store). Repointed: forward-fill `FileHashes.Keys → Fields["file"]`, both drift readers (`BuildFullList` `[drift]`
  marker + `BuildDriftList`), Store hash-write, append-preserve, FileMemoryStore copy, + 16 test sites. Behavior-preserving (drift + file-search
  identical). `FileHashes` is ACTIVE state — NOT removed at P6.6. EphemeralEntry untouched (no drift).
- **P6.5 Scorer cleanup** — **DONE + MERGED** (branch feature/attributes-scorer-cleanup). Deleted the 4 legacy `ScoreEntryTerm` blocks
  (Tags 50/15, RetrievalAnchors 15, `key:value`-in-Keywords 50/40/12, Description 10) — now scored via the kept `Fields` block (40/12) +
  `ContentPreview` (5). Re-homed chunk `parentEntry.Tags` → `Fields["tag"]` (20/6). Retired the redundant anchors→`Keywords` fold in
  `Forward` (anchors live in `Fields["anchor"]` + write-time BM25 TF-seed; anchor search survives). Harmonized `GetReviewStatus` gate
  (key-existence, M1). **Topics KEPT** (`ScoreTopicTerm` tag/desc — TopicInfo has no `Fields` map). **Non-recommended `key:value`** (basedOn:/role:)
  ACCEPTED-degrade (near-nil surface). This is the deferred UNIFORM scoring change dual-write set up. **MEASURED (A/B, real scorer, 30-doc):
  BENIGN AT THE HEAD** — #1 identical on 9/12 queries, **0 genuine field/BM25 head results displaced**; recommended `person:maria` NEUTRAL;
  the description-only 10→0 cliff dropped only weakest-signal docs with no genuine competitor (exactly decision 3). Whole-branch opus review:
  READY, no findings. Merged as-is (user decision).
- **P6.6 Model-field removal** (last): remove `Tags`/`RetrievalAnchors`/`Description`/`ReviewAfter`/`ReviewWhen` from `ArtifactEntry`
  (+ `EphemeralEntry`), fix the ~15 ctor sites + CLI/Server/Http mirrors + ~105 test constructions.

(Then Unit 7 — the authored guide + docs rewrite, incl. the hand-authored repo-root `AGENTS.md` refresh.)

## Risks / open questions

- **Scoring parity:** removing the tags/anchors/description fields and re-homing their signal into attributes/keywords
  must not silently drop recall on existing stores. Validate with a before/after on real memories (not a benchmark score).
- **Attribute weights:** the weight table needs sensible defaults; seed from current entity/tag weights (parity) and keep
  conservative. It is both the curation mechanism (soft, replacing the old key gate) and the anti-hub lever — but only
  field scoring consumes it in this spec; the graph consumes it during its separate tuning.
- **`memory_link` / `reconcile`:** confirm they don't depend on `tags`/`anchors` in ways the collapse breaks.
- **Guide length/scope:** a full authored rewrite is substantial; it is the highest-leverage item and should not be
  rushed or treated as a doc-comment tweak.
- **Rename lockstep:** `McpPermissionFilter` maps tool-name strings to permissions — if a renamed tool isn't updated
  there, that operation silently loses (or wrongly keeps) its permission gate. The rename must update the filter map and
  its tests together; a test should assert every registered tool name has a filter mapping.
