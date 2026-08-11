# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Scrinia uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The single source of truth for the version number is `Directory.Build.props` at the
repository root — bumping it there propagates to the CLI banner, the MCP `ServerInfo`,
and all assembly attributes.

## [Unreleased]

### Changed (breaking) — the HTTP store request and response shapes

- `POST /api/v1/stores/{store}/memories`'s request body drops `tags`, `reviewAfter`, and
  `reviewWhen` in favour of `attributes` — a map of key to string array, the same shape the MCP
  tools already write. Listing responses (`GET /api/v1/stores/{store}/memories`) now carry the same
  `attributes` map instead of a flattened `tags` array. Search responses carry `originalBytes` and
  `createdAt` for `entry`/`chunk` results (null for `topic` results, which are scope labels, not
  memories). Nothing is deployed over HTTP today, so there is no migration path and none is
  needed — but a pre-change client speaking the old shape will not work against this server.
- There is no `UnmappedMemberHandling.Disallow` on the request DTO, so unknown members are
  **ignored, not rejected**: a stale client still sending `tags`/`reviewAfter`/`reviewWhen` gets a
  `201` and those values are silently dropped rather than surfaced as an error.
- `POST /api/v1/stores/{store}/memories` now **rejects** two attribute shapes it previously
  accepted, with a `400` naming the bound. More than `KnownAttributes.MaxAttributes` (32) keys in
  `attributes` is refused rather than accepted-and-truncated (`KnownAttributes.Merge` silently drops
  keys past the cap, so accepting would lose data quietly). More than
  `KnownAttributes.MaxValuesPerKey` (256) values under a single key is refused as a
  storage-growth guard. Neither bound can fire for a map produced by Scrinia's own code: both are
  enforced by `KnownAttributes.Merge`, which every write path funnels through, so local and remote
  accept the same input. They are the wire guard for a hand-rolled client.
  *(The values-per-key bound was 64 during development and was reachable from routine
  auto-extraction — nothing caps `file`/`ref` values, so a memory citing 70 distinct paths stored
  locally and `400`'d remotely. 256 is unreachable from extraction.)*
- **`remember()` now requires the `search` permission as well as `store`.** The write path's
  existing-entry lookup goes through `LoadIndex`, which proxies to
  `GET /api/v1/stores/{store}/memories` — and that endpoint requires `search`
  (`MemoryEndpoints.ListMemory`). An API key scoped to `store` alone could `remember()` before this
  change and now gets a `403` from inside the write. This coupling is a consequence of proxying the
  listing rather than adding a dedicated existence-check endpoint; decoupling them needs one.

### Changed (breaking) — "matched nothing" is no longer reported as success

- An operation that completed without doing what was asked now reports a non-success outcome on
  every surface. Previously the `warning` envelope rendered in green, exited `0`, and returned
  `200` — so a CI step gating on the exit code or the status code passed while nothing had
  happened. Three call sites produce this envelope: an empty or fully-filtered bundle import, a
  `facts` entity argument that matched nothing, and an update specifying no changes.
- **CLI:** `scri facts` exits `1` on a warning envelope: an entity argument that resolves to
  nothing (e.g. `scri facts profile person:nobody`), or one that resolves to more than one key —
  a `DisambiguationResult`, "matched too many" rather than "matched nothing" (e.g.
  `scri facts profile jolene` when both `person:jolene` and `place:jolene` exist). `scri bundle
  import` exits `1` when a topic filter matches nothing, e.g.
  `scri bundle import --topics doesnotexist`. `scri bundle export`, `scri store` and `scri forget`
  print a warning envelope in yellow and exit `1` through the same code path, but as of this
  writing none of their call sites can actually produce a warning envelope — only `facts` and
  `bundle import` do today.
- **HTTP:** `POST /api/v1/stores/{store}/import` returns `422 Unprocessable Content`
  instead of `200` when no topics were imported. `422` rather than `400`, because the request was
  well-formed and authorised — it simply yielded nothing to import.
- **Also fixed:** `scri store` had no error guard at all and returned `0` unconditionally, so a
  genuinely failed store reported success. This is the same dead-guard defect previously fixed at
  `scri show`, `scri forget`, `scri bundle export` and `scri bundle import`; `store` was missed.
- Anything gating on exit `0` or HTTP `200` from these paths will now see a failure where it
  previously saw success. That is the point of the change, but it is breaking.

### Fixed — remote writes silently discarded the agent's metadata

- The write seam (`IMemoryStore.WriteArtifactAsync`) carried only the artifact text, and
  `HttpMemoryStore.Upsert` is a no-op by design (persistent writes go through
  `WriteArtifactAsync` instead), so a `remember()` against a remote store (`scri serve --remote`)
  sent the attributes map, the keywords, and the curated description nowhere — none of it reached
  the server. `WriteArtifactAsync` now takes an optional `ArtifactWriteMetadata`, and the MCP
  remember path assembles it *before* the write instead of trying to patch it in afterward.

### Fixed — remote search results reported fabricated metadata

- Every remote search result was rebuilt with `originalBytes: 0` and `createdAt: <now>`, so a
  caller reading a remote result's creation time got the time of the query, not the memory's actual
  age. Search results now carry the real values from the server. Two fabrications remain and are
  not fixed by this: `uri` (always `""`; making a remote result dereferenceable is a separate
  capability decision) and `chunkCount` (always `1`, wrong for any multi-chunk memory — it has no
  wire field yet).

### Fixed / Changed — `LoadIndex` proxies remotely; `TryUpdateEntry` fails closed

- `HttpMemoryStore.LoadIndex` returned `[]` unconditionally in remote mode. Every caller that asks
  "does this entry already exist" — the async entity-typing sink, the maintenance sink, every MCP
  tool's existing-entry lookup — got an empty answer for a scope that was not actually empty.
  `LoadIndex` now proxies to the same listing endpoint `ListScoped` already uses, and paginates
  using the server's reported total so a scope larger than one page still comes back whole. This is
  now a genuine blocking network round trip (more than one for a large scope) where it was
  previously a free in-process read — there is no async seam on `LoadIndex` to hide the cost
  behind.
- **This does not fix entity-typing promotion remotely — it still does not work.** Fixing
  `LoadIndex` in isolation would have made things *worse*: with `Upsert` a no-op remotely,
  `IMemoryStore`'s default `TryUpdateEntry` would now *find* the entry via the fixed `LoadIndex`,
  apply the caller's mutation, hand it to the no-op `Upsert`, and still return `true` — a false
  success where before there was at least an honest "not found". `HttpMemoryStore.TryUpdateEntry`
  is therefore overridden to fail closed (always returns `false`) until a dedicated attributes-only
  PATCH endpoint exists server-side. Entity-typing promotion against a remote store still does not
  persist; it now fails honestly instead of silently.
- `HttpMemoryStore.ListScoped` now throws on a non-2xx listing response, matching `LoadIndex`. It
  used to fall through to an empty list, so a `403` from the `search` permission gate made `list()`
  answer "No memories stored." for a store full of them.

### Fixed — remote `link()`/`update()` reported writes that never happened

- `MemoryTools.UpdateMeta` — reached by `update()` and by both halves of `link()` — went
  `LoadIndex` → mutate → `Upsert` → success. It reached the no-op remote `Upsert` **directly**,
  bypassing the `TryUpdateEntry` override above, so once `LoadIndex` was fixed to return the real
  listing, a remote `link()` answered `status: success / action: linked` while no `ref` attribute
  ever reached the server. It now routes through `TryUpdateEntry` and reports `NOT_SUPPORTED` when
  the store cannot persist a metadata-only change — the same fail-closed mechanism, not a second
  one. Locally this also makes the update atomic under `FileMemoryStore`'s per-scope write lock
  instead of a racy read-modify-write. `MaintenanceEventSink`'s auto-link and orphan-marking writes
  were converted for the same reason.

### Fixed — remote `append()` and `compact()` destroyed the attributes `remember()` persists

- Both called `WriteArtifactAsync` without metadata and assembled the entry afterwards for the
  no-op `Upsert`. Remotely the write re-runs the server's whole store path, replacing the entry with
  server-extracted attributes only — so an `append()` to a memory carrying `tag`/`anchor`/`person`
  left just the freshly extracted mentions behind, while reporting success. Identical calls against
  a local store preserved everything. `append()` now hoists its carried-forward attribute map above
  the write and hands it over (the same reorder `remember()` got); `compact()` reads the existing
  entry *before* the write — reading after sampled the already-replaced entry — and carries its
  attributes, keywords and description across. The CLI compaction path
  (`ScriniaCommands.CompactEntryToSingleChunk`) carries them too; it is typed `ILocalMemoryStore` so
  it cannot reach a remote store today, but the call site now states the requirement rather than
  relying on the parameter's static type.
- `HttpMemoryStore.WriteArtifactAsync` now includes the server's response body (truncated via
  `HttpErrorBody`) in the exception it throws for a failed write. A `400` naming the exact bound it
  refused used to reach the caller as a bare "400 BadRequest".

### Fixed — a re-store under a different casing discarded the prior version

- Memory-name lookups are now case-insensitive across the write paths. Re-storing a memory under a
  different casing than it was created with (`MixedCase` then `mixedcase`) was treated as a brand
  new memory: the previous version was not archived and `CreatedAt` was reset, while the write
  still landed on the same file on a case-insensitive filesystem — the prior version was lost with
  no archive. Appending under a mismatched casing dropped everything the entry carried forward
  (its attributes, its curated preview, its per-chunk entries), and compacting under one dropped
  the memory's attributes and keywords. Ten call sites in total.
- A resolved entry now also hands its own casing forward. Matching the name is only half the fix:
  every one of these paths turns the subject back into a file path afterwards, so leaving the
  caller's casing in place would have left the fix relying on the filesystem to fold case on its
  behalf — right on NTFS, and a second entry alongside the first anywhere case-sensitive.
  **Visible consequence:** `remember({ path: 'mixedcase' })` against a memory created as
  `MixedCase` leaves it named `MixedCase`, and the response reports `MixedCase` rather than what
  the caller typed. On a case-insensitive filesystem the old code silently renamed the memory to
  the caller's casing.
  *Ruling: re-storing under a different casing is an update, so there is no longer any route to
  change a memory's name casing through `remember()`. That is intended — casing renames are not a
  `remember()` capability, and the old behaviour was an accident of the defect, not a feature.*
- `copy()` to a *destination* named in a different casing now reports the collision instead of
  creating a second memory. The destination check asked the filesystem
  (`File.Exists(ArtifactPath(...))`), which folds case on an ordinary NTFS volume and does not
  elsewhere — so on a case-sensitive filesystem, copying onto `Archive` while `archive` existed saw
  no collision and wrote a second memory, and with `overwrite: true` wrote a twin rather than
  replacing. The destination is now resolved through the index and adopts the existing memory's
  casing, so the bytes land on the memory that is already there and it keeps its own name.
  *Ruling: `archive` and `Archive` are one memory.* The alternative — case-sensitive names
  throughout — is a coherent design, but it is a storage-format decision (two names differing only
  in case need two files, which an ordinary NTFS volume cannot hold), not a change to this check.
  This was the last open site of the class.
- `copy()` from a source named in a different casing now carries the source's metadata. It resolved
  the source *file* case-insensitively (the filesystem did it) but its *entry* ordinally, so the
  copy silently took the synthesized-defaults branch: destination created, bytes intact, attributes
  and keywords and curated preview gone. Its two lookups are now one, taken before the path is
  built. Covered by `CopyMemory_WithMismatchedSourceCasing_CarriesTheSourcesMetadata`; the
  pre-existing `CopyMemory_persistent_to_persistent` uses one casing throughout and asserts only
  that the destination exists, so it could not see this.
- Three remaining filesystem-case dependencies in `FileMemoryStore` are closed for
  case-sensitive filesystems: `TryUpdateEntry`'s `File.Exists` fast path (which reported "no such
  entry" for an entry its own authoritative lookup finds, without invoking the mutation) is gone,
  and `Remove` and `DeleteArtifact` each fall back to a case-insensitive directory scan when the
  exact name misses. None is observable on Windows, where NTFS folds case for all three — each
  carries a ruling comment at the site saying so and naming what would observe it.
- `forget()` no longer reports success while leaving the memory's content on disk.
  `Remove` (the sidecar) had been made case-insensitive while `DeleteArtifact` (the `.nmp2` file)
  had not, and `forget()` calls both. On a case-sensitive filesystem `forget('mixedcase')` against a
  memory stored as `MixedCase` therefore answered "Forgot: mixedcase." with the artifact still on
  disk — orphaned, unreachable through `list()` or `search()`, and invisible to the next `forget()`.
  Fixed in the store primitive rather than at the three call sites, so the pairing agrees for every
  caller.
- `append()` resolves the memory's artifact under the entry's own casing, not the caller's. The
  index lookup now runs *before* the artifact read in both implementations (the MCP tool and the
  server orchestrator). Previously the read ran first on the caller's casing; on a case-sensitive
  filesystem it missed, and the create-new fallback underneath it — whose own lookup *is*
  case-insensitive — then landed on the existing memory and replaced its content with the appended
  chunk alone. `append()` behaving as `replace()`. NTFS folds case in `File.Exists`, so this was
  never reachable on Windows; both sites carry a ruling comment saying so, and the ordering itself
  is pinned by a test at each.

### Added — the client refuses an unrecognised server API version

- Remote search filter parameters (`excludeTopics`, `pathPrefix`, and friends) were sent as
  additive query params, so a server that predates them silently ignored what it didn't understand
  and returned an unfiltered result set the client presented as filtered. The server now stamps
  `X-Scrinia-Api-Version` on every response under `/api/v1/stores`, and `HttpMemoryStore` throws if
  a response is missing the header or carries a version it doesn't recognise, on both the
  synchronous and asynchronous send paths.

### Changed — BM25 ranking differs slightly between a local and a remote write

- The same memory, stored through MCP against a local store and through
  `POST /api/v1/stores/{store}/memories`, gets different term frequencies and therefore ranks
  slightly differently. Over the wire the carrier keys (`tag`/`review`/`anchor`/`file`) arrive
  inside one flat `attributes` map and are seeded into TF; locally `SeedTermFrequencies` runs
  *before* the carrier merge, so those keys are never seeded. The server also cannot tell an
  agent-supplied attribute from a carrier one — a wire map is flat by construction — so it treats
  the caller's keywords as agent-supplied and boosts them accordingly. Attribute *maps* match
  exactly; only ranking differs. Reproducing local's split needs the two maps sent separately,
  which is a wire-shape change. Documented at `MemoryOrchestrator.cs`.

### Changed — MCP and REST disagree on a bare date in a `review` attribute

- The MCP `remember()` tool routes a `review` attribute value through a lenient
  `DateTimeOffset.TryParse` and stores the result in round-trip (`"o"`) format — `"2026-08-01"`
  becomes `"2026-08-01T00:00:00.0000000+00:00"`, which `TryReadReview` recognises as a review
  *date* and reports as `[stale]` once it passes. A direct
  `POST /api/v1/stores/{store}/memories` performs no such parsing — `KnownAttributes.Normalize`
  only trims and lowercases — so the same bare `"2026-08-01"` is stored verbatim, `TryReadReview`
  cannot parse it as a round-trip date, and it is read as a review *condition* instead: the memory
  reports `needsReview: true` forever rather than going stale on the date. The MCP tool description
  (`MemoryTools.cs`) advertises exactly the lenient bare-date form (`review: ['2026-08-01']`) that
  only works correctly through MCP, not through the REST endpoint directly.

### Changed — store read path

- A cached `LoadIndex` no longer opens a file handle. The shared `.lock` was acquired before the
  in-memory cache was consulted, so every call paid an OS handle open and close even when no I/O
  followed. The lock still guards cold reads, which are the only ones that touch disk — it never
  guarded cache freshness, because nothing invalidates the cache across processes.
- `SearchQuery.Candidates` is now a cost hint that cannot make a search's answer *wider* — supplying
  a wider or differently-scoped candidate list previously returned memories from scopes the query
  had excluded, silently. The store now re-applies the query's own `scopes`/`excludeTopics` to
  whatever is handed in. `SearchPipeline` — the only production caller that sets it — already
  built both from the same values, so no caller's results change. Note the filter can only
  remove candidates, never source them: a list *narrower* than the query's scopes still
  under-returns.
- `IMemoryStore.LoadIndex` returns `IReadOnlyList<ArtifactEntry>` and no longer copies the cached
  list on every read. The single caller that mutates the result (`ImportTopicEntries`) now copies
  explicitly. The read-only type makes a caller's mutation a compile error rather than silent
  corruption of the store's index cache.

### Changed — `IMemoryStore` split: local-only members moved to a new `ILocalMemoryStore`

- `IMemoryStore` lost eight members. Six moved to a new `ILocalMemoryStore : IMemoryStore` in
  `Scrinia.Core` — `ArtifactPath`, `FindArtifactPath`, `GetStoreDirForScope`, `WorkspaceRoot`,
  `ListTopicArtifacts`, `ImportTopicEntries`. These are the members a store without a filesystem
  could only ever implement by throwing; `v0.4.0` added `WorkspaceRoot` to the shared interface on
  exactly that basis ("Remote stores throw `NotSupportedException`"), and this reverses that call.
  "Needs local files" is now a compile-time fact rather than a runtime surprise, and no member of
  `IMemoryStore` throws for lack of a filesystem any more.
- Three more members were removed outright rather than moved: `SaveIndex` (no caller outside the
  store implementations — now a plain public member of `FileMemoryStore`, on neither interface),
  `GenerateContentPreview` (both implementations had byte-identical bodies that touched no store
  state — now the static `Scrinia.Core.ContentPreview.Generate`), and `GetResultScope` (no callers
  left after the search-contract change).
- `FileMemoryStore` implements `ILocalMemoryStore`; `HttpMemoryStore` implements only
  `IMemoryStore`. `Scrinia.Core` is not published to NuGet, so this affects in-process plugin
  authors rather than package consumers — but a plugin DLL compiled against the old interface will
  fail to load. An `IStorageBackend` plugin whose store has a workspace on disk should implement
  `ILocalMemoryStore`; one that does not can implement `IMemoryStore` alone and every caller
  degrades or refuses cleanly instead of throwing.

### Fixed — remote-store operations that threw or returned incomplete results

- `remember()` against a remote store (`scri serve --remote`) threw `NotSupportedException` for
  any non-ephemeral memory carrying a `file` attribute — the drift-detection hasher read
  `IMemoryStore.WorkspaceRoot`, which only a local store can answer, unconditionally. A remote
  `remember()` now stores the memory without file hashes instead of failing outright; drift
  detection against those hashes is inherently a local-only capability (see `list(mode:
  'drift')` below). Ephemeral remembers (`/temp/*`) were unaffected — `HttpMemoryStore` has
  always implemented ephemeral storage for real.
- `list()` against a remote store threw in every mode, not only the mode that needed it: a
  single overly-broad narrowing meant `summary` and `full` modes failed even though neither
  reads the local filesystem. Both modes now return real listings against a remote store.
  `list(mode: 'drift')` still declines — drift detection re-hashes files on disk by nature — but
  now with a clean `NOT_SUPPORTED` error (see below) instead of an unhandled exception.
- `guide()` and `restore()` against a remote store previously succeeded in a degraded form (the
  conflict-scan, skills, and agent-config sections silently omitted) only because their file
  reads happened to sit inside a swallowing `try/catch`. That behavior is preserved — remote
  `guide()`/`restore()` still degrade the same way — but is now a deliberate, type-checked branch
  covered by tests, not an incidental side effect of exception handling.
- Server bundle endpoints (`POST /export`, `POST /import`) returned an unhandled-exception `500`
  against a non-local store backend, because the request path read a `RequestContext` accessor
  that threw. Both now return a clean `501 Not Implemented`.
- `recall('/agent/<name>')` against a remote store threw `NotSupportedException`. The `/agent/`
  branch prefers a hand-authored `.md` file in the workspace, which a remote store has none of;
  it now falls through to ordinary NMP/2 resolution instead of refusing, so the call returns a
  stored `/agent/*` memory if the backend has one and a `NOT_FOUND` error if it does not. This is
  neither a refusal nor a degraded success — the lookup genuinely runs, it just cannot consult a
  local file first.

### Added — `NOT_SUPPORTED` MCP error code

- MCP tools whose operation is inherently local-only — `export()`/`import()` (bundle
  files), `reconcile()` (merge-conflict scan), skill creation/loading from a file path, and
  `remember()` to an `/agent/*` config — now return a structured error response with
  `code: NOT_SUPPORTED` when the active store is remote, instead of letting a raw
  `NotSupportedException` propagate out of the tool call. Anything parsing MCP tool error codes
  (previously `INVALID_PARAMETER`, `INVALID_ACTION`, `INVALID_PATH`, `NOT_FOUND`, `CONFLICT`,
  `INTERNAL`) will see this new code for the first time; each response's `actionNeeded[]` names
  the local-only feature and, where one exists, a remote-compatible alternative.

### Changed — server handler names now match the published MCP tool vocabulary

- `MemoryEndpoints`'s internal handler methods were renamed to line up with the MCP tool
  names: `StoreMemory` → `RememberMemory`, `ListMemories` → `ListMemory`, `ShowMemory` →
  `RecallMemory`, `SearchMemories` → `SearchMemory`, `SearchMemoriesPost` →
  `SearchMemoryPost`. Route paths and the HTTP contract are unchanged. **One exception**:
  Minimal API derives an endpoint's OpenAPI `operationId` from the handler method name when
  none is set explicitly, and none of these five endpoints calls `.WithName()`.
  `AddOpenApi()`/`MapOpenApi()` are wired up in `Program.cs` — exposed when the server runs
  in Development or with `Scrinia:ExposeOpenApi=true`, and covered by `OpenApiTests`. Any
  client generated from `/openapi/v1.json` against these five endpoints will see their
  `operationId` change on this release.

### Changed — CLI exit codes and not-found output

- `scri show` and `scri forget` now exit `1` when the memory does not exist. Both guarded on
  `result.StartsWith("Error:")`, a shape the response serialiser has never emitted — every
  envelope opens with `status: ` — so the branch was dead and both commands reported success
  on a genuine not-found. Scripts that relied on the old always-zero exit code will now see a
  failure where a failure actually occurred. MCP tool output is unchanged.
- On that not-found path, both commands now print the error followed by the recovery hints:

  ```
  $ scri show /findings/nope; echo $?
  Error: Memory '/findings/nope' not found.
    - list() to see available memories
    - search({ query: '...' }) to find by keyword
  1
  ```

- **`--json` shape change on that same path.** `scri show --json` and `scri forget --json`
  previously emitted `CliShowOutput` / `CliForgetOutput` with the error envelope sitting in the
  content field and exit `0`. They now emit the standard error envelope, `{"error": "..."}`,
  with exit `1` — the shape the dead branch always intended. The recovery hints are terminal
  output only; the JSON `error` field stays a single clean message. Scripts parsing
  `.content` from a not-found `scri show --json` must switch to checking the exit code.
- `scri bundle export` and `scri bundle import` exited 0 on genuine failure, via the same dead
  `result.StartsWith("Error:")` guard fixed above for `show`/`forget` — a shape the renderer has
  never emitted, since every envelope opens with `status: `. Any CI step or script gating on
  their exit code was not gated. Both now read the typed status, and an import matching no topics
  exits 1 — see "matched nothing is no longer reported as success" below.
- **`--json` shape change on that same path.** `scri bundle export --json` and `scri bundle
  import --json` previously fell through the same dead guard into the success path and emitted
  `CliExportOutput{path, message}` / `CliImportOutput{message}` with exit `0` on genuine failure.
  They now emit the standard error envelope, `{"error": "..."}`, with exit `1`. Scripts parsing
  `.message` from a failed `scri bundle import --json` must switch to `.error` plus the exit code.
  `scri store --json` gets the same shape change for a different reason: it had no error guard at
  all (see "Also fixed" above), so a genuinely failed store emitted `CliStoreOutput` with exit `0`.
  It now emits the same `{"error": "..."}` envelope with exit `1`; scripts must switch from
  reading `.name`/`.bytes` to checking `.error` plus the exit code.

### Changed — search reuses the pipeline's materialized candidates

- `SearchPipeline.ExecuteAsync` now passes its already-materialized candidate list through
  `SearchQuery.Candidates` so `FileMemoryStore.SearchAll` skips a second `ListScoped` call.
  Measured against a LoCoMo-seeded corpus (70–272 memories, 200–500 queries, `scrinia-bench
  searchcost`): consistently faster (~0.2–0.3ms lower median `SearchAll` cost, roughly 20–25%
  at these corpus sizes) but sub-millisecond in absolute terms — real, not noise, but not the
  point of the change. **The behavior change that matters**: candidates are captured before
  the `await` on the score contributor (which for an embedding provider can run for seconds),
  so a memory written during that window no longer appears in the same search's results —
  previously the store's own re-list would have picked it up. Search was never transactional
  with concurrent writes, and the contributor already scored against the earlier snapshot, so
  this makes the two halves of one search consistent with each other rather than half-fresh.

### Changed — remote-mode search filters server-side

- `POST /api/v1/stores/{store}/search` now also bounds each `attributeFilter` value array at 64
  entries, mirroring the existing 64-key bound on the filter itself. A request where any single
  key carried more than 64 values previously passed validation and relied on Kestrel's body limit
  as the only backstop; it now gets a 400 where it previously got 200.
- `HttpMemoryStore.SearchAll` sent only `q`, `scopes`, and `limit` to `GET /search`;
  `excludeTopics`, `pathPrefix`, and `attributeFilter` were applied by post-filtering the
  results after they came back over the wire — filtering an already-truncated top-K. A
  remote search scoped to a `pathPrefix` that most of the corpus falls outside of could
  return far fewer than `limit` matches, or none, even when enough matching memories
  existed. All three filters are now sent to the server, so a filtered search against a
  remote store returns up to `limit` matching results.
- New `POST /api/v1/stores/{store}/search`, taking a `SearchRequest` body — needed
  because `attributeFilter` is a dictionary of arrays, which does not fit a query string.
  `GET /search` additively gained `excludeTopics` and `pathPrefix`. Both are
  backward-compatible: existing `GET /search` callers are unaffected, and
  `HttpMemoryStore` only POSTs when an attribute filter is present, GETting otherwise.
  **That compatibility only runs one direction.** An old client against a new server is
  fine. A new client (this branch's `HttpMemoryStore`) against an *older* server is not:
  `excludeTopics` and `pathPrefix` now travel only as additive query parameters, and an
  older v1 server ignores query parameters it doesn't recognize rather than rejecting the
  request. So `scri search "x" --path-prefix /findings/` against an older server silently
  returns memories from every path, and `--exclude-topics goal` silently returns memories
  from the topic the caller asked to exclude — this branch trades a recall bug (results
  under-filled by post-limit filtering) for a silent precision bug (filters not applied at
  all) whenever the server on the other end predates this change. `attributeFilter` is the
  one exception, and only by luck, not by design: it requires `POST /search`, which an
  older server doesn't expose, so the 404/405 hits `HttpMemoryStore`'s
  `!resp.IsSuccessStatusCode` guard and the search returns empty instead of unfiltered.
  There is no version handshake in `HttpMemoryStore` — the mismatch is undetected on both
  ends. A capability probe (client asks the server what it supports before relying on it)
  is the right fix and is deferred to the `store-boundaries` branch.

### Fixed — remote search results reported every entry, chunk, and topic as scope "local"

- `HttpMemoryStore.SearchAll` recovered a result's scope only when its wire name
  contained `':'`. An entry or chunk's wire name is a v3 path (e.g.
  `/goal/G-1/widget-0`) and a topic's is a `FormatScopeLabel` display string — neither
  ever contains `':'` — so every remote search result reported `scope: "local"`
  regardless of where it actually lived. Entry and chunk names now parse
  unconditionally via `ParseQualifiedName`; topic names via the new
  `MemoryNaming.ParseScopeLabel`, the inverse of `FormatScopeLabel`.

### Fixed — response envelope can no longer be impersonated by memory content

- The twenty internal handlers in `Scrinia.Mcp` returned already-rendered YAML, so consumers
  recovered structure by substring-matching it. Because `content` is emitted as an indented
  but unescaped block, text inside a response body was indistinguishable from the envelope's
  own `status:` line, and a successful command could be reported as a failure. Three commands
  could reach it: `scri link` (echoes `--reason` into its success message), `scri reconcile`
  (embeds decoded conflict bodies in its scan report), and `scri facts` (renders entity values
  verbatim, and those come from unsanitised attribute text). Handlers now return the typed
  response and serialise once at the tool boundary; the CLI reads the status field.

### Fixed — git merge driver

- The `.nmp2` merge driver crashed on every real conflict. Git passes extensionless temp
  files, so `Path.GetDirectoryName` returned an empty string and `Path.GetFullPath` threw;
  the merge halted with an unhandled `ArgumentException` and no way to resolve it through
  `reconcile()`. Both drivers now receive git's `%P` pathname and resolve `merge.config`
  from it; the `.nmp2` driver additionally resolves the artifact, its sidecar, and the
  conflict directory from the same path.
- A conflicted `.nmp2` now stops the merge with git's path marked unmerged, writes whole
  `conflict/current` and `conflict/incoming` copies beside the artifact, and leaves markers
  for `reconcile()` to find. `reconcile()` prefers the whole `conflict/current` and
  `conflict/incoming` copies when resolving a conflicted `.nmp2`, falling back to the marker
  body when a copy is absent — so a marker body that is not independently decodable no
  longer blocks resolution.
- A driver registered before `%P` repairs its own local git config and stops, rather than
  proceeding without knowing which artifact it is merging.
- `scri-merge` is published alongside `scri` and included in the CLI release archives. No
  release previously contained it, so `scri setup --multi-user` registered a driver that was
  not present and asked the user to copy a binary they had never been given.
- Conflict copies are no longer committable: the driver writes a `.gitignore` containing `*`
  into the conflict directory as it creates it. Committed copies persisted after
  `git merge --abort` and were preferred by `reconcile()` on every later conflict, resolving
  from stale bytes instead of the actual merge.
- A `merge.config` whose `ConflictDir` resolves to the artifact directory itself — `"."`,
  `""`, `"./"`, `".//"`, or `"." + separator` — is now **rejected** with an
  `InvalidOperationException` rather than accepted. Previously the driver wrote the
  `.gitignore` of `*` above into the artifact directory itself. Git does not retroactively
  untrack files already tracked, so this did not silently untrack existing memories — but it
  did clobber any `.gitignore` the user already had there, and every memory written into the
  directory afterwards became invisible to `git status`/`git add` and was silently lost on a
  fresh clone. Reaching this required hand-editing `merge.config`, since `scri setup
  --multi-user` hard-codes `"conflict"`.
- The driver no longer writes a `"conflicted": true` property into a conflicted artifact's
  `.meta.json`. Nothing read it, and the next legitimate sidecar write dropped it again
  (`WriteSidecarCore` serializes the typed `ArtifactEntry`, which has no such field); its
  only observable effect was a spurious diff.
- A legacy four-argument invocation (a driver registered before `%P` was added) with only
  one of the two driver keys (`merge.scrinia-meta.driver`, `merge.scrinia-nmp2.driver`)
  registered no longer reports the other, unregistered key with failure language. `git
  config --get` on a key that was simply never registered is indistinguishable from a key
  that failed to read, so the message now names only the invoked key's outcome and notes
  that a repair was also attempted for the other, without characterising its result — e.g.
  `[scrinia] merge driver invoked without %P — updated merge.scrinia-nmp2.driver to pass
  %P; meta repair also attempted; re-run your merge`.

**Upgrade action for existing workspaces.** A workspace configured before this release has
`merge.scrinia-nmp2.driver` pointing at `.scrinia/hooks/scri-merge`, a path no release ever
shipped. Git cannot run it, so the driver never executes and cannot repair itself: the merge
leaves the path unmerged with `ours`' content and no conflict markers. Re-run
`scri setup --multi-user` once per clone to re-point both drivers at the installed binary.

### Fixed — bundle import

- Bundle import left the cached entity-fact store stale, so `facts()` and entity-fact
  surfacing in search omitted every imported memory for the life of the process.
- `POST /api/v1/stores/{store}/import` rejected every multipart bundle upload with 415
  Unsupported Media Type. Its `topics` parameter was declared without `[FromQuery]`, so Minimal
  API inferred it as JSON-body-bound and content negotiation failed before the handler ran —
  meaning none of the endpoint's own permission, writability or file-presence guards were ever
  reached. The endpoint had likely never worked for a real caller. Note `topics` takes repeated
  query keys (`?topics=a&topics=b`), not a comma-separated list.

### Changed — file-entity unification

- `.scrinia/skills/` and `.scrinia/agent/` now resolve through a single authority
  (`PathRouter.ToFileEntityDir`). The location was previously hardcoded at nine call sites
  behind two divergent `GetScriniaBaseDir` implementations that agreed only by coincidence.
- Agent configs move back out of `.scrinia/memories/agent/` to `.scrinia/agent/`. Keeping
  hand-authored Markdown inside a store scope directory put `AgentFileMeta` sidecars in a
  filename namespace shared with the store's own `ArtifactEntry` sidecars, where
  `SaveIndex`'s orphan sweep deleted them — reachable by importing a bundle containing a
  memory topic named `agent`. The v3 migration never shipped, so no released workspace is
  affected and no data migration is required.
- `.scrinia/workflows/` is deleted. Nothing ever wrote a workflow; the category could only
  be populated by importing a bundle. Bundles containing one still import cleanly and now
  report the skipped category rather than dropping it silently.
- The legacy `local:skill` NMP/2 read fallback is deleted. `v0.4.0` contains no skill code
  at all, so such entries exist only in unreleased dev builds.
- `IMemoryStore` gains `WorkspaceRoot`. Remote stores throw `NotSupportedException`: skills
  and agent configs are local-only, which was previously accidental rather than stated.

### Changed — embedding provider error truncation now matches the chat providers

- A truncated provider error body in the embedding provider's warning log now ends with
  `...`, matching the three chat providers. The four call sites carried the same 500-character
  truncation rule copied by hand, and only the embedding provider's copy had drifted to omit
  the ellipsis. All four now go through one shared `HttpErrorBody.Truncate` helper.

### Fixed — event-sink lifecycle

- Detached event-sink work is now tracked by `BackgroundWork` instead of being dropped on the
  floor by a bare `_ = Task.Run(...)`. Writes still return without waiting for embedding
  latency, but the work is owned: the CLI and `Scrinia.Server` both drain outstanding sinks
  before exiting, so a short-lived command — or a request that lands just before shutdown —
  no longer ends with a vector file half-written. If the drain exceeds 10s the work is
  cancelled and a warning names `scri reindex` as the recovery.
- A faulting embeddings-plugin shutdown no longer kills the process. The `ProcessExit`
  handler registered by `WorkspaceSetup` called `DisposeAsync().AsTask().Wait(3000)`
  unguarded — and an exception escaping a `ProcessExit` handler terminates the process.
  Since the plugin child has usually already exited by then (making disposal throw over a
  broken pipe), this could take down `scri` on a normal exit. Both the handler and
  `McpPluginHost.DisposeAsync` now contain their own failures.
- Concurrent writes to a fresh workspace no longer fail with `UnauthorizedAccessException:
  Access to the path is denied`. `EnsureMaterialized` calls `EnsureGitIgnore` from *outside*
  the scope's file lock and write lock, and its `File.Exists` guard is check-then-act — so two
  writers to a new workspace both wrote `.scrinia/.gitignore`, and on Windows the losing
  `File.Move` got `ERROR_ACCESS_DENIED`, which `FileRetry` treats as permanent. The content is
  a fixed constant, so the race is benign and is now tolerated (rethrowing only if the file is
  genuinely absent) — the same treatment the sidecar write-back already documented. In
  practice this surfaced whenever a background sink's write raced a foreground write.
- `FileLockTests.ConcurrentUpsert_NoDataLoss` could hang the whole test host indefinitely,
  which the runner reports as an aborted run. Its two workers ran on thread-pool threads and
  blocked on each other at a `Barrier` (deadlock-prone under xunit's parallel load), and a
  worker that threw stopped signalling, parking its partner on `SignalAndWait()` forever —
  hiding the `.gitignore` race above as a hang instead of reporting it. Workers now get
  dedicated threads, a bailing worker removes itself as a participant, and the test surfaces
  the workers' actual exceptions. It fails in ~150ms where it used to hang.
- Test isolation: `TestHelpers.StoreScope` now clears `MemoryEventSinkContext.Default` and
  drains background work before deleting its temp directory. `Default` is a process-wide
  static — correct for the CLI's single session, wrong for a shared test host — so any test
  reaching a `ScriniaCommands` entry point left the real embed/entity-typing/maintenance
  pipeline installed for every later test, whose writes it then rewrote mid-assertion. This
  was the cause of the intermittent `MentionSpanWiringTests` failure and of the
  `UnauthorizedAccessException`/`FileLockTimeoutException` noise against deleted temp stores.

### Removed (breaking) — legacy v1/v2 read-fallback layout

- The read path now knows exactly one on-disk layout (`.scrinia/memories/`). The v1/v2
  fallback machinery that merged reads across `.scrinia/topics/`, namespaced `entity/`/
  `memory/` directories, and legacy per-scope index-file paths has been deleted; a
  workspace still holding content at those legacy locations has it moved (not read
  through) by the automatic migration on first store access.
- `.scrinia/agent/*.md`, like skills below, deliberately stays OUT of the automatic
  migration. An earlier revision swept it into `.scrinia/memories/agent/`, which put its
  `AgentFileMeta` sidecars in a filename namespace shared with the store's own
  `ArtifactEntry` sidecars, where `SaveIndex`'s orphan sweep deleted them (see
  "file-entity unification" above). All of its readers/writers (recall, remember, the
  followUp listing, and bundle export/import) route through `PathRouter.ToFileEntityDir`,
  deliberately outside the store's own path resolution — that is what keeps it out of the
  sweep for good.
- `.scrinia/skills/*.md` deliberately stays OUT of the automatic migration too, for the
  same reason as agent. All of its readers (`MemoryTools.Skills.cs`,
  `MemoryTools.Lifecycle.cs`'s followUp listing, and bundle export/import) route through
  `PathRouter.ToFileEntityDir` rather than the migrated store layout. The manual `scri
  migrate` command (below) doesn't touch it either — both triggers share the same engine.

### Changed — store unification and the v3 memory layout

- `ScriniaArtifactStore` is deleted. `FileMemoryStore` is the only filesystem store, and
  `PathRouter` is the only authority on where a memory lives.
- Every memory now lives under `.scrinia/memories/`. The `store/` and `topics/` roots are
  gone, along with the `entity/` and `memory/` namespace prefixes. (`agent/` is also gone
  from under `memories/` — but it lives on as a top-level `.scrinia/agent/` directory
  alongside `.scrinia/skills/`; see "file-entity unification" above.)
- Existing workspaces migrate automatically on first store access, with a backup written to
  `.scrinia-backup-{timestamp}/`. `scri migrate` runs the same migration manually and gains
  `--dry-run`; it replaces the hidden `scri migrate_v1`, whose `--cleanup` step is no longer
  needed because files are moved rather than copied.
- `scri consolidate` no longer reads through one store and writes through another, which
  could leave the in-memory index stale for the rest of the process.
- **The unrelated attributes-model forward-fill command, previously `scri migrate`, is
  renamed to `scri migrate_attributes`** to free the `migrate` name for the store-layout
  migration above — the two commands do unrelated things and would otherwise collide (this
  repo did the same swap once before, in the other direction, when the attributes command
  was first added — see `docs/planning/plans/2026-07-10-attributes-plan-5-migrate.md`).
  Behavior and flags are unchanged; only the CLI-facing command name moved.

### Fixed — workspace root resolution

- Workspace root resolution is now bounded at the nearest enclosing git repository. Projects
  without their own store no longer bind to a store in a distant ancestor directory, which
  caused unrelated projects to share one memory pool.
- The store is no longer created while reading. Constructing the store and running `hint`,
  `recall`, or `search` against a workspace with no store leave no directory behind.
- Generated `.scrinia/.gitignore` now covers suffixed lock files (`*.lock`) and vector indexes.

### Changed — CLI/MCP memory-surface parity
- **`list` and `search` were the last two CLI commands that reimplemented their
  MCP tool counterpart instead of delegating; they now share one core.**
  `scri search` previously ran BM25-only — it loaded the embeddings and
  entity-graph contributors but never passed them to the scorer. It now routes
  through the shared `ExecuteSearchAsync` pipeline, so CLI and MCP search rank
  identically (fused BM25 + embeddings + entity graph).
- **`scri search --json` gained per-hit `attributes`, `createdAt`, `updatedAt`,
  and `anchors`** (previously only type/name/score/tokens/description/chunk
  info), and the table gained an Attributes column — matching what the MCP
  `search` tool already surfaced. New optional flags: `--context`,
  `--exclude-topics`, `--path-prefix`.
- **`list --summary` "Top attributes" now reports per key with counts
  (`topic (3)`) on both surfaces.** The MCP tool previously enumerated per
  `key:value` (`topic:auth`); the CLI already used per-key counts. Both now share
  one aggregator (`AggregateSummaryFacets`), which also excludes key:value-keyword
  shapes from "Top keywords" on both surfaces — the MCP side used to
  double-report them.
- Internal handler cores renamed to match their tool verbs: `Store` →
  `Remember`, `Show` → `Recall`. No MCP tool or CLI command name changed.
- The dead `codeRefs` parameter on the internal remember handler was removed;
  file dependencies flow in exclusively via the `file` attribute
  (`attributes: { file: ['path'] }`), which already drove drift detection.

### Fixed — CLI/MCP memory-surface parity
- `list mode:'drift'` (and `scri list --drift`) no longer drops the topic path
  from drift rows: a memory at `/api/auth-flow` reported as bare `auth-flow`
  because the scope was pre-stripped of its `local:` prefix before formatting.
  It now passes the scope straight to `FormatQualifiedName`, so topic-scoped
  drift rows keep their `/topic/` path.
- The MCP `list` tool's summary and full-list output (`BuildSummary`/`BuildFullList` in
  `MemoryTools.Core.cs`) carried a stray carriage return on every line. Both builders used
  `StringBuilder.AppendLine`, which emits `\r\n` on Windows, while `ToYaml` renders the result
  by splitting on `\n` only — leaving a `\r` baked into each line. All 20 call sites now use
  `Append(...).Append('\n')` so the line terminator is explicit and platform-independent.

### Changed (breaking) — path-only memory names
- **The `topic:subject` colon naming syntax was removed; names are paths.**
  `api:auth` is now `/api/auth`. `ParseQualifiedName` rejects a colon name with
  a message suggesting the path form. All display (`list`, `search`, store
  confirmations) already rendered paths via `FileMemoryStore.FormatQualifiedName`,
  but `ScriniaArtifactStore.FormatQualifiedName` (used by the CLI) still emitted
  colon — it now emits paths too.
- **The internal `memory/` topic namespace was dropped.** A topic now resolves
  the same whether reached by a `/topic/subject` path or a bare topic filter —
  `local-topic:{topic}` (`.scrinia/topics/{topic}/`), not
  `local-topic:memory/{topic}`. This fixes `--scopes {topic}` / `Export([{topic}])`
  not matching path-stored memories. Data under the old `.scrinia/topics/memory/`
  layout is still read via fallback. `skill` (entity) and `agent` scopes are not
  part of this rename — they resolve separately through `PathRouter.ToFileEntityDir`
  to a top-level `.scrinia/{skills,agent}/` directory outside `memories/` entirely
  (see "file-entity unification" and "store unification and the v3 memory layout" above).
- Agent-config resolution moved from `agent:{name}` to the `/agent/{name}` path,
  which also repairs `restore`'s `/agent/**`, `/patterns/**`, `/checkpoint/latest`,
  and `/sessions/{today}` follow-ups (they were reading legacy colon-scoped dirs
  that path writes never populate).
- **In-content cross-references are now `[[/path]]` wiki-links.** The auto-linker
  (`MaintenanceEventSink` / `ReferenceExtractor`) matches `[[/api/auth]]` /
  `[[note]]` instead of bare `topic:subject`.
- The internal topic-scope prefix was shortened from `local-topic:` to `local:`
  (e.g. `local:api`), and `list --summary` now labels topic groups `/api` instead
  of `topic:api`. Scope strings feed vector-store directory names, so semantic
  search over existing topic data needs a one-time `scri reindex` (the same
  reindex the namespace change above already requires; BM25 search is unaffected).

### Changed (breaking) — flat CLI surface
- **The `memory` command group is gone; its subcommands are now top-level.**
  `scri memory list/search/show/store/forget/append/compact/link` become
  `scri list/search/show/store/forget/append/compact/link`. There is no
  `memory` alias — update any scripts that used the prefix.
- Trimmed agent-directed meta-commentary from `--help`: `guide` no longer says
  "Call once per session", and `hint`'s description drops the hook-envelope
  internals (and a stray XML tag that was leaking into the rendered help).

### Removed (breaking) — LLM-at-rest enrichment
- **LLM-at-rest enrichment pipeline deleted.** `LlmConsolidator` and the entire
  Tier 2 sweep over the local store is gone. Scrinia no longer consults an LLM
  at write time, at read time, or in any background sink. The deletion was
  driven by an empirical finding: MemBench R@5 / R@10 / NDCG@10 are
  **identical** with and without the sentence-MaxSim reranker (0.0 / 0.0 / 0.0
  deltas on the published runs), proving that the candidate-pool composition —
  not the rescoring stage — is the bottleneck. Agent-supplied retrieval anchors
  at write time are the right lever; LLM-at-rest enrichment was polishing the
  wrong end of the system. The LLM clients themselves were moved to the bench
  (see below) where they belong as a test harness simulating the production
  agent.
- `ImportanceScoringSink`, the `Importance` field on `ArtifactEntry`, the
  `Importance` weight in `RankerOptions`, and the importance branch in
  `ComputeTotalScore`. The ranker now composes relevance + recency only;
  setting `α_recency = 0` falls back to pure relevance.
- `McpLlmPluginHost` and the bundled local-LLM plugin projects:
  `Scrinia.Plugin.Llm`, `Scrinia.Plugin.Llm.Cli`, and `Scrinia.Plugin.Llama`
  (plus their test projects). `publish.ps1 -WithLlm` and the
  `scri-plugin-llm` subprocess no longer exist.
- `scri consolidate --with-llm` flag and the entire Tier-2 progress-file
  machinery (`.scrinia/.tier2-progress.json`). Existing files in user
  workspaces are now orphaned and can be deleted by hand.
- `scri reindex --importance` flag and its backfill path.
- The `SessionEnd` consolidate hook installer in `scri setup --hooks`. Only
  `SessionStart` and `UserPromptSubmit` hooks are installed now. The Codex
  no-SessionEnd workaround in the installer is also gone (it existed solely
  to skip the consolidate hook on Codex).
- The entire `Scrinia:Llm:*` configuration block (~14 keys, including
  `Provider`, `BaseUrl`, `Model`, `ApiKey`, `Temperature`, `RequestTimeoutSeconds`,
  `LocalModelFile`, `LocalModelUrl`, `LocalContextSize`, `AnthropicApiKey`,
  `AnthropicBaseUrl`, `GeminiApiKey`, `GeminiBaseUrl`). `scri setup` no longer
  prompts for any LLM provisioning — only embedding-provider config.
- "Tier 1 / Tier 2" framing throughout the user-facing documentation. There is
  no tier model anymore: writes happen at write time and `scri consolidate`
  is a mechanical-only manual sweep.
- `IBackgroundLlm` (and `BackgroundLlmContext.Default`) in `Scrinia.Core`. The
  bench has its own simpler `IBenchLlmClient` interface.

### Added
- **`RetrievalAnchors` field on `ArtifactEntry`** (string[]? — agent-supplied
  phrases, questions, or category-level paraphrases that bridge likely user
  queries to the entry's content). Use the vocabulary you expect users to query
  with, not the vocabulary already in the content (the indexer already covers
  exact-match). Indexed via the weighted field scorer (+15 on per-anchor
  contains-match — same weight the old `Facts` field carried) and TF-seeding
  at +2 so BM25 picks up anchor-derived tokens naturally.
- New `anchors` parameter on the MCP `memory_remember` tool. Optional, defaults
  to null. Empty / whitespace-only anchors are dropped at the boundary; an
  empty array is treated as null on the entry. The parameter is additive —
  existing callers that don't supply `anchors` see no behavior change.
- **Bench-side agent harness** under
  `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/` — an `IAgentHarness`
  abstraction with a single `GenerateIngestAsync` op that, given a doc's
  content, produces the same attributes map and keywords a production agent hands the
  `remember` tool — a typed `attributes` map and freeform `keywords` — in one
  LLM call. `ScriniaRetriever` writes the attributes into `ArtifactEntry.Attributes`
  (feeding the field scorer + entity graph) and merges the keywords with its
  mechanically-extracted ones (prod-faithful TF weights: attributes +3, agent
  keywords +5). New CLI flags: bare `--agent` (on/off — required `--agent-provider`
  when present),
  `--agent-provider <openai|anthropic|gemini|ollama|claude-cli|codex-cli|copilot-cli>`,
  `--agent-base-url`, `--agent-model`, `--agent-api-key`. Response caching under
  `benchmarks/.../cache/agent-{provider}-{model}.ndjson` so reruns hit the cache
  and only first-pass calls hit the LLM. The op is ingest-only — there is no
  query-side transform. The prompt is built from document content only
  (anti-data-leakage: it never sees the gold-answer set or the question dataset).
- The LLM clients themselves moved to
  `benchmarks/Scrinia.Retrieval.Benchmarks/Llm/Providers/`:
  `IBenchLlmClient`, `OpenAiCompatibleBenchLlmClient` (covers
  Ollama / LM Studio / llama.cpp / vLLM / real OpenAI),
  `AnthropicBenchLlmClient`, `GeminiBenchLlmClient`,
  `AgentCliBenchLlmClient` (shells out to `claude` / `codex` / `copilot` CLIs
  reusing the user's CLI auth), and the shared `ResilientLlmProvider` base.

### Changed (breaking)
- **`ArtifactEntry.Facts` renamed to `ArtifactEntry.RetrievalAnchors`.** Hard
  rename, no read-side migration: persisted entries with the old `Facts` JSON
  field deserialize to `RetrievalAnchors = null` silently. Re-save any entries
  whose anchor data must be retained (or accept the loss — the field had no
  user-facing surface beyond ranking weight).
- **`scri consolidate` is mechanical-only.** `--with-llm` is removed (unknown
  flag → exit 1). The command does session compaction plus stale-entry reporting
  and nothing else.
- **`scri reindex` has no `--importance` flag.** Existing scripts passing it
  get an unknown-flag error.
- **`scri setup --hooks` no longer installs the SessionEnd consolidate hook.**
  Existing user installs are not actively cleaned up; re-running
  `--uninstall-hooks` removes scrinia-managed blocks including any prior
  SessionEnd entry. Run `scri consolidate` by hand when you want session
  housekeeping.
- **Production `Scrinia.Core` is LLM-free.** All LLM client code moved to
  `Scrinia.Retrieval.Benchmarks.Llm.Providers`. Downstream consumers that
  referenced `Scrinia.Core.Llm.IBackgroundLlm` or any concrete client need to
  either depend on the bench assembly or implement their own client.

### Changed
- **MCP surface split: `memory(action, …)` dispatcher replaced with ten per-verb
  tools** — `memory_remember`, `memory_recall`, `memory_search`, `memory_list`,
  `memory_append`, `memory_forget`, `memory_compact`, `memory_link`,
  `memory_restore`, `memory_reconcile`. The dispatcher's single JSONSchema had
  to declare every parameter across every operation, so it couldn't express
  "if `action='remember'` then `content[]` is required and `appendContent` is
  ignored" — the most salient parameter name won under autonomous reasoning
  and agents reliably produced malformed calls 40 turns into a session.
  Per-verb schemas give each operation exactly its own fields and upgrade the
  correctness signal from prose-in-description to validator-level. Skill-path
  routing is preserved inside `memory_recall` / `memory_remember` (no separate
  `skill_*` surface). `memory_append` parameter renamed from `appendContent`
  to `content` (the disambiguation suffix was an artifact of the dispatcher's
  ambiguity and is no longer needed). `INVALID_ACTION` error code is gone —
  unknown names now fail at MCP tool-registration, not in a runtime handler.

### Fixed
- `scri hint` no longer silently drops memories whose best score came from a
  chunk match. `WeightedFieldScorer.SearchAll` deduplicates per memory and
  emits one result per memory — either an `EntryResult` or a `ChunkEntryResult`
  depending on which scored higher — and the prior hint filter dropped the
  latter entirely, so multi-chunk session entries whose entry-level score was
  below the floor would never appear even when a chunk matched strongly.
- `FileMemoryStore.SearchAll(query, scopes, limit, excludeTopics)` now uses
  the workspace's configured `RankerOptions` instead of falling back to the
  defaults. Previously any caller exercising topic exclusion silently lost
  `Scrinia:Search:Alpha:*` config.
- `FileMemoryStore.Remove` invalidates the topic-discovery cache when removing
  from a `local-topic:*` scope. Without this, `DiscoverTopics` reported topics
  with no remaining entries until the cache flushed naturally.
- `ImportanceScoringSink.OnAppendedAsync` now rescores against the **full**
  memory after the append (existing + new), not the appended snippet alone.
  Previously a one-line "Yes" append could clobber an `Importance=9`
  architectural decision with a score derived from the snippet alone.
- `ImportanceScoringSink` is now registered in the server-mode event-sink
  pipeline as well as the CLI. Previously the CHANGELOG claimed coverage of
  "every workspace-bootstrap path" but server-mode writes were skipped.
- `MemoryTools.FireEventSinkAsync` now passes `CancellationToken.None` to the
  sink action rather than the request CT. The sink is fire-and-forget on a
  background `Task`, so propagating the request-scoped CT would abort the
  sink as soon as the user-facing response returned — leaving importance
  unscored or the vector index half-written. The synchronous
  `OnForgottenAsync` call sites continue to use the request CT, since their
  side effects must complete before the response returns.

### Added
- `scri hint` now applies **Maximal Marginal Relevance (MMR)** diversity rerank
  to its top-K output. Fixes a documented retrieval failure where one chatty
  session floods the hint results because its memories share vocabulary —
  with λ = 0.6 (default) the reranker breaks the flood by demoting candidates
  too-similar to already-selected ones, surfacing memories from other sources
  (`/findings/`, `/skill/`) that the BM25 top-3 would otherwise miss.
  Similarity is TF-cosine over already-loaded `TermFrequencies` dicts — zero
  additional I/O. Configurable via `Scrinia:Hint:DiversityLambda` (1.0 reverts
  to pre-MMR relevance-only ordering, 0.0 = pure diversity) and
  `Scrinia:Hint:InnerLimit` (the candidate pool size MMR diversifies down to
  top-3; default 10).
- New `IBackgroundLlm.ScoreImportanceAsync` Tier 2 capability — short prompt,
  parses a 1–10 integer reply, clamps out-of-range / null on failure. Wired
  through every concrete provider: OpenAI-compat (Ollama, llama.cpp, LM Studio,
  vLLM), native Anthropic Messages API, native Gemini generateContent, agent CLI
  (Claude Code / Codex / Copilot), and the bundled `scri-plugin-llm` subprocess.
- New `ImportanceScoringSink` event-sink fires asynchronously after every Upsert
  and Append, scoring the memory via Tier 2 and rewriting the sidecar's
  `Importance` field — never blocks the user-facing response, degrades silently
  when no Tier 2 LLM is configured. Registered alongside the embedding and
  maintenance sinks in every workspace-bootstrap path.
- `scri reindex --importance` backfills LLM-scored importance for any memory
  that doesn't already have one. Reports `scored / skipped / failed` counts;
  cancellable via Ctrl-C. Preserves prior scores so external/manual ratings
  aren't overwritten.
- New `scrinia-bench` CLI under `benchmarks/Scrinia.Retrieval.Benchmarks/` —
  retrieval-quality harness that runs Scrinia against academic benchmarks
  (ConvoMem, plus LongMemEval / LoCoMo / MemBench in upcoming commits) and
  reports R@5 / R@10 / R@50 / NDCG@10 with per-category breakdowns. JSON
  output schema mirrors MemPalace's per-question result files so per-question
  hits can be diffed across systems without a schema translator. First
  subcommand: `convomem` against the Salesforce evidence-question dataset.
  This is distinct from the existing perf microbenchmarks under
  `tests/Scrinia.Benchmarks/` (BenchmarkDotNet).
- `scrinia-bench longmemeval` — runs the headline LongMemEval benchmark
  (xiaowu0162/longmemeval-cleaned, 500 questions, per-question haystacks of
  ~53 sessions). Session/turn granularity, MemPalace's `lme_split_50_450.json`
  honoured via `--split full|dev|held_out`. Per-question-type breakdown
  (knowledge-update / multi-session / temporal-reasoning / single-session-{user,
  assistant, preference}). Apples-to-apples vs MemPalace's published 96.6% raw
  R@5 baseline.
- `scrinia-bench --mode bm25_embeddings` (default) — wires Scrinia's model2vec
  MiniLM-L6-v2 embedder into the harness alongside BM25. Embeds every haystack
  doc once at ingest time, caches the vector in memory, and at query time
  embeds the query and feeds cosine-sim supplemental scores into
  `WeightedFieldScorer.SearchAll` via the same `{scope}|{name}` contract the
  production `HybridReranker` uses. Falls back to BM25-only if the model
  isn't present.
- `scrinia-bench --mode bm25` — pure BM25 baseline for apples-to-apples vs
  MemPalace's `--mode raw` track without semantic search.
- `scrinia-bench membench` — runs MemBench (ACL 2025) across all 10 categories
  (8,500 items): simple, highlevel, knowledge_update, comparative, conditional,
  noisy, aggregative, highlevel_rec, lowlevel_rec, post_processing. Per-item
  haystack of 5–30 turns; ground truth matches against turn `sid`s. Apples-to-
  apples vs MemPalace's published 80.3% R@5 overall (43.4% on the designed-hard
  `noisy` category).
- `scrinia-bench locomo` — runs LoCoMo (10 conversations × ~200 QA pairs = 1986
  questions). Per-conversation haystack reuse via a new
  `BenchmarkRunner.RunGroupedAsync` path: each conversation's dialogs ingest
  once and serve every QA in that conversation before the retriever resets.
  Session or dialog granularity (`--granularity session|dialog`). Reports R@5,
  R@10, and R@50 side-by-side so the K=50 structural-guarantee question can be
  settled with data.
- New `BenchmarkQuestionGroup` core type for benchmarks where multiple
  questions share a haystack (LoCoMo's per-conversation shape). The runner has
  a matching `RunGroupedAsync` overload that ingests the group's shared
  haystack once and reuses it across every question in the group.
- `scrinia-bench` ablation knobs — `--alpha-recency`, `--tau-days`,
  `--alpha-importance`, `--diversity-lambda`, `--diversity-inner-limit`.
  Default to "off" so the headline run stays apples-to-apples; turning them on
  isolates the contribution of each Scrinia ranker feature (exp-decay recency,
  Generative-Agents-style importance, MMR diversity) against the benchmark
  ground truth. Output filenames pick up an ablation tag suffix
  (e.g. `__rec1.0_tau30_div0.6`) so sweep results don't collide.
- `benchmarks/Scrinia.Retrieval.Benchmarks/README.md` documents the harness
  methodology — metric definitions (R@K, NDCG@10), ranker composition formula,
  ground-truth matching modes (ID match for LongMemEval/LoCoMo/MemBench,
  substring match for ConvoMem), reproducibility caveats (model2vec
  determinism, MiniLM-architecture parity vs ChromaDB's sentence-transformer
  inference path), and per-question diff recipe vs MemPalace's published JSONL.
- Root README now points at the retrieval-quality harness alongside the
  existing token-cost benchmarks (which compare Scrinia vs flat-file vs
  auto-memory token budgets — distinct work, kept distinct).
- `scripts/prepare-datasets.{ps1,sh}` and `scripts/run-benchmarks.{ps1,sh}` —
  idempotent setup + run for the four retrieval benchmarks. Replaces the
  copy-pasteable curl/git/dotnet command blocks that were previously the
  canonical run path; the manual commands remain in the bench README as a
  fallback, but the scripts are the entry point now. Bash scripts are
  marked executable in the index so they work as-is on Linux/Mac.

### Changed
- Search ranker now uses an **additive composition** matching the Generative
  Agents (Park et al., 2023) score shape: `α_relevance·relevance +
  α_recency·exp(-Δt/τ)·scale + α_importance·(importance/10)·scale`. Replaces
  the previous multiplicative "relevance × (1 + linear recency boost)" formula
  whose maximum +10% nudge over 365 days was effectively noise on any real
  ranker decision. Default τ = 14 days produces a meaningful exponential decay
  (today=1.0, two-weeks=0.37, one-month=0.14) so fresh memories materially
  outrank stale ones on near-ties. Relevance still gates the composition —
  entries with no text match never surface regardless of recency / importance.
  Weights and time-constant are configurable via `Scrinia:Search:Alpha:Relevance`,
  `:Alpha:Recency`, `:Alpha:Importance`, `:TauDays`, `:NeutralImportance`,
  `:Scale:Recency`, `:Scale:Importance`. Setting α_recency = α_importance = 0
  reduces the ranker to pure relevance for users who want the prior behavior.
- `ArtifactEntry` gains an `Importance` field (nullable int, 1–10). Null means
  "not scored yet" — the ranker falls back to a neutral midpoint (5/10) so
  unscored memories rank as if "average importance" rather than getting
  penalised. The scoring path itself ships in a follow-up commit; this commit
  only adds the field, the sidecar round-trip, and the ranker read path.

### Fixed
- `scri hint` and `scri restore --hook` now emit the hook-output JSON envelope
  (`{"hookSpecificOutput":{"hookEventName":"...","additionalContext":"..."}}`)
  understood uniformly by Claude Code, Codex, and Copilot — replaces the bare
  stdout line that the agents were treating as ignorable status output.
  Payload is wrapped in semantic tags (`<scrinia-hint>` for the prompt-time
  hint, `<scrinia-restored-memory>` for the SessionStart dump) with imperative,
  second-person framing that reads as instruction rather than log line. On
  Claude Code the JSON shape arrives via the discreet `additionalContext`
  channel — context reaches the model without polluting the user's transcript
  on every prompt submit. SessionEnd output (`scri consolidate --auto`) is left
  un-wrapped: Claude Code routes it to debug-log only, Codex has no SessionEnd,
  Copilot ignores it. `scri hint --plain` retains the original log-line form
  for human inspection; `scri restore` defaults to YAML for direct invocation
  (only `--hook` switches to the envelope).
- Scrinia hooks now embed the **full absolute path** to the running `scri`
  executable (resolved via `Environment.ProcessPath`) rather than the bare
  `scri` name. Agent CLIs fire hooks in child shells whose `PATH` may not
  contain scrinia's install dir — especially when the CLI is launched from a
  desktop shortcut, a different shell profile, or a CI runner — and the hook
  would silently fail to find the executable. Install-time resolution writes
  the path once into each hook's command field; paths containing whitespace
  are shell-quoted. On Windows the path uses forward slashes — Claude Code
  and similar CLIs spawn hooks via git bash even when the interactive terminal
  is PowerShell, and bash interprets `\` as an escape. Forward slashes work
  uniformly in bash, PowerShell, and cmd. Re-run `scri setup --hooks` to
  update existing installs.
- `scri setup` now configures the Tier 2 LLM backend explicitly. Previously the
  CLI-based provider values (`claude-cli`, `codex-cli`, `copilot-cli`) and the
  native Anthropic/Gemini providers were reachable only via raw
  `scri config Scrinia:Llm:Provider <value>` — setup had no LLM step at all.
  New `--llm <value>` argument pins a backend non-interactively
  (`claude-cli`/`codex-cli`/`copilot-cli`/`openai`/`anthropic`/`gemini`/`plugin`/
  `none`/`auto`); when omitted, setup writes `auto` only if no provider is
  already configured. Secondary keys (API keys for anthropic/gemini, base URL +
  model for openai) are prompted only when missing from existing config.
- `scri setup` clears stale Ollama-derived config when the Ollama path falls
  through (probe failed, user declined, or `--no-ollama`). Previously, switching
  off Ollama left `Scrinia:Embeddings:Provider=ollama` + URL + model intact, so
  every subsequent startup tried Ollama, failed, and only then fell back.
  Custom OpenAI / Anthropic / Gemini configurations are preserved.
- `scri setup` now writes `Scrinia:Embeddings:Provider=model2vec` after the
  local model download step, so re-running setup after a previous configuration
  no longer leaves the value implicitly defaulted.

### Changed (breaking — CLI surface)
- Memory CRUD commands are now grouped under `scri memory <action>` — `scri list`,
  `scri search`, `scri store`, `scri show`, `scri forget`, `scri append`,
  `scri compact`, `scri link` become `scri memory list`, `scri memory search`,
  etc. The top-level `--help` is correspondingly shorter and easier to scan.
- Bundle file operations are grouped under `scri bundle <action>` — `scri export`
  and `scri import` become `scri bundle export` / `scri bundle import`; the old
  top-level `scri bundle` (pack-raw-files) is now `scri bundle pack`.
- `scri migrate` is hidden from top-level `--help` (still callable for one-shot
  v1→v2 store migration).
- `Scrinia:Llm:Provider` value renamed from `openai-compat` to `openai`; the old
  name is no longer accepted. Run `scri config Scrinia:Llm:Provider openai` if
  you have a workspace pinning the old value.

### Added — Agent integration (CLI backends, hooks, hint)
- **`scri hint <prompt>`** — pre-send relevance hint. Sub-millisecond BM25 lookup
  against the workspace store; emits a single-line marker
  (`[scrinia] N memories match: a, b, c…`) when results clear the score floor.
  Prompts shorter than `Scrinia:Hint:MinPromptChars` (default 8) and matches
  below `Scrinia:Hint:MinScore` (default 10.0) suppress the hint. Disable
  globally with `scri config Scrinia:Hint:Enabled false`. Reads prompt from
  positional arg or stdin; auto-detects JSON envelopes (`{prompt, …}`) used
  by some CLIs' hook protocols.
- **`scri setup --hooks`** — auto-install scrinia-managed SessionStart,
  SessionEnd, and UserPromptSubmit hooks across detected agent CLIs (Claude
  Code, OpenAI Codex, GitHub Copilot). SessionEnd fires once at session
  termination — NOT per assistant response — so consolidate runs once when the
  user wraps up, not on every turn. Codex CLI has no SessionEnd event, so the
  consolidate hook is skipped there with a notice (SessionStart and
  UserPromptSubmit still install). Prompts per CLI on PATH. User-global scope
  by default; `--project` writes workspace-local config that's committable for
  team sharing. `--uninstall-hooks` removes only scrinia-managed blocks,
  preserving user-authored hooks.
- **Per-CLI hook adapters** with format-appropriate writes:
  - Claude Code: `~/.claude/settings.json` merge with `_scriniaManaged: "v1"`
    sentinel for drift detection; user-authored hooks in the same event array
    survive every install/uninstall.
  - Codex 0.124+: `~/.codex/hooks.json` (loaded alongside `config.toml` —
    avoids round-tripping the user's TOML config).
  - GitHub Copilot CLI (GA Feb 2026): dedicated `scrinia.json` inside
    `~/.copilot/hooks/` (user) or `.github/hooks/` (project); event names
    canonical→camelCase (`SessionStart` → `sessionStart`, `SessionEnd` → `sessionEnd`).
  - Direct `scri` invocation in every hook — no intermediate shell scripts
    that can drift from their deployment locations (avoids the GSD #1834
    footgun).
- **Agent CLI as background LLM** — three new `Scrinia:Llm:Provider` values
  (`claude-cli`, `codex-cli`, `copilot-cli`) that shell out to the user's
  installed agent CLI in non-interactive print mode. Reuses the user's
  existing CLI authentication so Tier 2 doesn't need a separate API key or
  bundled model. Combined system+user prompt arrives on stdin to avoid
  Windows' 8K argument-length limit. Auto-mode (`Provider=auto`) tries HTTP
  → agent CLIs in preference order (claude → codex → copilot) → bundled
  plugin.
- **Native Anthropic + Gemini LLM providers** (`Provider=anthropic`,
  `Provider=gemini`) using each vendor's native API rather than the
  OpenAI-compat shim. Anthropic targets `/v1/messages` with the Messages API
  shape (system field, content blocks, x-api-key + anthropic-version
  headers) — better feature parity than the compat shim. Gemini targets
  `/v1beta/models/{model}:generateContent` with x-goog-api-key. Both inherit
  from a new `ResilientLlmProvider` base.
- **`IProcessRunner` abstraction** under `Scrinia.Core.Process` — testable
  one-shot process invocation with Windows PATHEXT resolution (`.cmd` shims
  for Node-based CLIs don't resolve through `Process.Start` with
  `UseShellExecute=false`).
- New config keys: `Scrinia:Llm:AnthropicApiKey`, `Scrinia:Llm:AnthropicBaseUrl`,
  `Scrinia:Llm:GeminiApiKey`, `Scrinia:Llm:GeminiBaseUrl`,
  `Scrinia:Hint:Enabled`, `Scrinia:Hint:MinScore`, `Scrinia:Hint:MinPromptChars`.

### Added — Tier 2 LLM consolidation
- `scri consolidate --with-llm` runs an LLM pass over the local store after the
  existing Tier 1 mechanical compaction: regenerates auto-fallback descriptions,
  one-paragraph summaries for compacted session entries, and extracts 3–7 atomic
  facts per memory (Mem0-style). Resumable via `.scrinia/.tier2-progress.json`
  keyed by qualified name + content hash — re-runs only touch memories whose
  content changed since the last pass.
- `ArtifactEntry.Facts: string[]?` field on the sidecar schema (v4). Each fact
  enters `TermFrequencies` at weight +2 so BM25 picks them up naturally;
  `WeightedFieldScorer.ScoreEntryTerm` also gives per-fact contains-match a
  small boost.
- `IBackgroundLlm` interface in `Scrinia.Core/Llm/` with two implementations:
  `OpenAiCompatibleBackgroundLlm` (HTTP via `HttpClient` against any
  OpenAI-compatible chat-completions endpoint — Ollama, llama.cpp server,
  LM Studio, Docker Model Runner) and `PluginBackgroundLlm` (MCP `complete`
  tool on the bundled `scri-plugin-llm`). Prompts live in `Scrinia.Core`
  (`LlmPrompts.cs`) so new Tier 2 tasks don't require a plugin rebuild.
- Bundled `Scrinia.Plugin.Llm` + `Scrinia.Plugin.Llm.Cli` ship LFM2.5-1.2B-Instruct
  on Vulkan via LLamaSharp 0.25. `publish.ps1 -WithLlm` packages it into
  `plugins/scri-plugin-llm/`. The plugin exposes a single `complete` MCP tool
  and a `status` tool; prompts stay on the host side.
- Provider preference for both LLM and embeddings auto-modes: existing HTTP
  backend (Ollama et al.) wins over the bundled Vulkan plugin. The principle is
  "if Ollama is already running, don't spin up another model in our own VRAM."
  Force a backend with `Scrinia:Llm:Provider = plugin` or `openai`.

### Added — Ollama auto-detection
- `scri setup` probes for a running Ollama instance via `GET /api/tags`. When
  reachable, prompts for an embedding model and a completion model (defaults:
  `nomic-embed-text`, `lfm2:1.2b` with `llama3.2:1b` fallback), pulls any
  missing models via streaming `POST /api/pull` with per-layer progress, and
  writes the resulting `Scrinia:Embeddings:*` + `Scrinia:Llm:*` config so
  subsequent `scri serve` / `scri consolidate --with-llm` "just work."
- Skip the Ollama prompt with `scri setup --no-ollama`.

### Added — Chunked embeddings + reindex
- `TextChunker.SliceWindows` slices every memory's decoded content into
  overlapping 1200/200-char windows. Each window becomes one vector keyed by
  `(scope, name, chunkIndex)`. Search-time dedup in
  `WeightedFieldScorer.SearchAll` collapses chunk matches back to one result
  per memory but lets the best-matching window drive the score. A needle
  buried mid-memory is now vector-reachable; the prior single-vector-per-memory
  layout silently lost it past whatever context window the provider supported.
- SVF3 signed vector file format: header carries the active embedding
  signature `{provider}|c{chunkSize}o{overlap}` (e.g.
  `ollama:nomic-embed-text|c1200o200`). On startup, vector files whose
  signature doesn't match the active config are quarantined as
  `vectors.bin.stale-{timestamp}` and `EmbeddingReindexer.ReindexIfStaleAsync`
  rebuilds them. Same flow triggers when the user changes
  `Scrinia:Embeddings:Provider`, `:OllamaModel`, `:ChunkSize`, or `:ChunkOverlap`.
- `scri reindex` command — forces a full vector rebuild by moving every
  `vectors.bin` to a `vectors.bin.pre-reindex-{timestamp}` backup, then letting
  the startup flow re-embed from sidecars.
- New config keys: `Scrinia:Embeddings:ChunkSize` (default 1200),
  `Scrinia:Embeddings:ChunkOverlap` (default 200),
  `Scrinia:Embeddings:MaxChunksPerMemory` (default 100, caps embed cost on
  pathologically long memories — BM25 still indexes the full text).
- `IEmbeddingProvider.EmbedBatchAsync(IReadOnlyList<string>)` default
  implementation (loops `EmbedAsync`) makes per-memory batch embedding work
  on every provider without HTTP-batch overrides; concrete providers can
  override for a perf win.

### Added — Search-path cache hardening
- `FileMemoryStore.DiscoverTopics` switched from a 2-second TTL to fully
  event-driven invalidation (already done in `Upsert` / `SaveIndex`). Repeated
  search calls between mutations now skip the `Directory.GetDirectories` rescan
  entirely.
- `FileMemoryStore.SearchAll` derives `TopicInfo[]` from the candidates already
  loaded by `ListScoped` instead of re-calling `LoadIndex` per topic scope inside
  `GatherTopicInfos`. The `_indexCache` was absorbing the disk reads but every
  duplicate call still acquired a shared file-lock and copied the entry list —
  measurable wear over long daemon sessions on sync-watched workspaces.

### Added — Resilience + observability
- `ResilientEmbeddingProvider` no longer trips its circuit breaker on HTTP 4xx
  responses. Bad payloads are per-input client errors, not provider-health
  signals — letting them cascade-fail every subsequent embed in a reindex was
  what produced the earlier 798/816 failure mode.
- `EmbeddingReindexer` classifies `FileNotFoundException` (sidecar without
  artifact) as Skipped rather than Failed and logs at Debug level.
- Robustness around transient file-access failures in `LlmConsolidator` progress
  flushes: per-N-entry batched writes, atomic `.tmp` rename with [50,100,250]ms
  retry on `IOException`/`UnauthorizedAccessException`.

### Added
- `scri serve` auto-downloads the built-in embedding model on first run if it's
  missing — most users no longer need to run `scri setup` separately. Download
  progress goes to stderr to keep MCP stdio clean; if download fails, the server
  still starts with semantic search degraded to BM25-only. Opt out with
  `scri serve --no-auto-setup`.
- Six new CLI commands wrap the previously MCP-only handlers: `scri guide`,
  `scri append`, `scri compact`, `scri link`, `scri restore`, `scri reconcile`.
  Each wires directly to the same `ScriniaMcpTools` method the MCP server uses,
  so terminal users can evaluate every memory capability without an MCP client.
  `--json` returns the YAML response wrapped in a stable `CliMcpOutput` envelope;
  exit code is `1` on error responses.
- `Compact` MCP handler now returns a structured `NOT_FOUND` error instead of
  throwing `FileNotFoundException` when the target memory does not exist.
- Bounded NMP/2 decode: `Nmp2Strategy.MaxDecodedBytes` (default 64 MB) and `MaxChunkCount`
  (100k) reject memory-pressure DoS attempts during multi-chunk decompression.
- Structured error codes on every `memory()` failure path (`INVALID_PARAMETER`,
  `INVALID_ACTION`, `INVALID_PATH`, `NOT_FOUND`, `CONFLICT`, `INTERNAL`) plus concrete
  recovery hints in `actionNeeded[]`.
- Per-action parameter applicability table in the agent guide, plus elevated
  `memory('restore')` framing at the top of the tool description.
- `withBuiltin` parameter on skill recall (renamed from `reconcile` to disambiguate from
  the `reconcile` action).
- Truncation signal: responses whose content was capped at 8 KB now emit a `followUp`
  hint directing agents to fetch the next chunk.
- Soft-warning when writes target malformed reserved-prefix paths
  (`/Skills/`, `/pattern/`, `/agents/`, …) — the operation still succeeds but an
  `info[]` entry suggests the canonical spelling.
- `Directory.Build.props` centralises `<Version>` and `<AssemblyInformationalVersion>`
  across all projects; no more hardcoded version literals.
- `FileLock` contention observability via `ILogger` — `Warning` on first retry and
  `Error` on timeout with structured `{LockPath}` and `{ElapsedMs}` fields.
- `HnswIndex` upgraded to `ReaderWriterLockSlim` for concurrent search/insert workloads;
  exposes `IDisposable`.
- BM25 corpus-stats dictionary is pre-sized via a `docCountHint` parameter,
  removing rehash churn during search corpus computation.
- `IOptions<EmbeddingOptions>` and `IOptions<ChatOptions>` migration with
  `PostConfigure` for the historical Temperature/MaxTokens normalisation; server
  fails fast on missing required configuration.
- `ListMemories` REST endpoint accepts `offset`/`limit` query parameters
  (default 200, hard cap 1000) and returns the unpaginated total count.
- New documentation: `docs/plugin-authoring.md` (extension interfaces + worked
  example), `docs/security.md` (threat model + recommendations), embedding-provider
  selection section in `docs/architecture/embeddings.md`, `AgentChatPage` entry in
  `docs/web-ui-guide.md`, `scri migrate` reference in `docs/cli-reference.md`,
  chunked-embeddings + Tier 2 LLM coverage in `docs/cli-reference.md` and
  `docs/architecture/embeddings.md`.

### Changed
- Bundled-plugin default GGUF switched from `LFM2.5-1.2B-Thinking` to
  `LFM2.5-1.2B-Instruct`. The thinking variant burned the token budget on
  `<think>…</think>` reasoning blocks for Tier 2 tasks that want terse output;
  the instruct variant gives usable completions at the same parameter count.
  `VulkanLlmProvider` still detects and handles thinking models (heuristic via
  filename + GGUF metadata, `StripReasoningBlocks` regex, 8× max-tokens
  multiplier) for users who configure one explicitly.
- Bundled LLM plugin default context bumped from 4096 to 8192 tokens.
- Ollama probe uses `/api/tags` rather than `/` or `/v1/models` — works on every
  Ollama version including fresh installs with no models pulled, and surfaces a
  human-readable error string when unreachable.
- LLM HTTP probe timeout bumped 2s → 5s to match the Ollama-setup probe
  (Windows IPv6→IPv4 fallback latency on localhost was tripping the shorter
  budget intermittently).
- HybridReranker max-aggregates per-chunk vector scores under the
  `{scope}|{name}` key the whole-memory scorer pass looks up. The chunked
  `{scope}|{name}|{chunkIndex}` keys are still emitted alongside for any
  future per-chunk scoring path.

### Changed
- Trimmed CLI publish now declares `<IsTrimmable>true</IsTrimmable>` on `Scrinia` and
  `Scrinia.Core` and runs with `TreatWarningsAsErrors=true` + `SuppressTrimAnalysisWarnings=false`
  so reflection regressions cannot ship.
- Migrated 12 reflection-based `JsonSerializer` callsites to source-generated context
  overloads (`BundleFormatService`, `FileMemoryStore`, `ScriniaArtifactStore`,
  `ScriniaCommands`, `MemoryTools.Lifecycle`).
- `Nmp2Strategy.Encode` reuses an `ArrayPool<byte>` compression buffer and writes
  URL-safe Base64 directly via `System.Buffers.Text.Base64Url`, eliminating the
  three intermediate byte-array allocations and the `.Replace`/`TrimEnd` chain.
- Action aliases canonicalised: `remember` and `recall` are the canonical names;
  `store`/`show` remain accepted aliases for back-compat.

### Removed
- Ghost `sos-handler` skill reference from `docs/getting-started.md`; the skill set
  matches `src/Scrinia.Mcp/skills/` exactly.
- `docs/reviews/2026-04-29-llm-integration-architect-review.md` moved to
  `docs/reviews/archive/` with a resolution annotation.

### Fixed — the store listing endpoint dropped keywords

- The store listing endpoint (`GET /api/v1/stores/{store}/memories`) now returns each memory's
  `keywords`. The field was absent from the wire entirely, so a remote client saw `null` keywords
  for every memory regardless of what was stored. Additive and optional in both directions: an
  older client ignores the new field, and a newer client against an older server reads `null`
  exactly as before, so `X-Scrinia-Api-Version` is deliberately not bumped.

## [0.5.0] — Initial public baseline

First release with the canonical two-tool MCP surface (`guide`, `memory`) after the
scope reduction that removed the goal/task/concern/plan vocabulary. The runtime is
considered stable from this point — future minor releases will not break the MCP
tool contract without a major-version bump.

[Unreleased]: https://github.com/nickd-scrinia/scrinia/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/nickd-scrinia/scrinia/releases/tag/v0.5.0
