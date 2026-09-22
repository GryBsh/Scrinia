# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Scrinia uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The single source of truth for the version number is `Directory.Build.props` at the
repository root — bumping it there propagates to the CLI banner, the MCP `ServerInfo`,
and all assembly attributes.

## [Unreleased]

### Added — immutable `scri` global tool and `SCRINIA_HOME` runtime layout

- `scrinia` is now packaged as an immutable NuGet **global tool** whose single command is `scri`,
  implementing both server modes (`scri serve` and `scri shared serve`) plus the configuration,
  tracking, and shared-base commands. The package ships only the managed command, its managed
  dependencies, the offline-required embedded prompts/skills/schemas/setup templates, and the
  package readme; it deliberately excludes Merge (`scri-merge`), the Vulkan plugin, Vulkan/LLamaSharp
  native RID assets, downloaded models, repository data, `.scrinia`, and mutable caches. Vulkan is a
  separately installed, RID-specific asset.
- All mutable state now lives below `SCRINIA_HOME` (the Phase 2-resolved per-user home). The built-in
  Model2Vec model and any plugin-downloaded model resolve to `SCRINIA_HOME/models`; the one-time
  migration relocates the executable-relative legacy `models` and `plugins` trees once, and the
  embeddings plugin is discovered only through the mandatory
  `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy with no flat fallback. `AppContext
  .BaseDirectory` is never written at runtime.
- Documented the exact installation, update, and uninstall commands, the immutable-package contract,
  the `SCRINIA_HOME` layout, the mandatory plugin hierarchy, both server commands, and the separate
  Vulkan install in the new `docs/installation.md` and `src/Scrinia/package-readme.md`, and pinned the
  living-doc headings and `SCRINIA_HOME`-naming source diagnostics with doc/source tests.

### Changed (breaking) — tracking precedence now resolves through one authority

- `tracking migrate-legacy` (and `status`/`set`) no longer read the workspace file value directly. The
  effective tracking policy is resolved by `EffectivePolicy.ResolveEffective`, so precedence now
  follows a single order: **single-underscore environment > double-underscore environment > file >
  `Unset`**. `SCRINIA_WORKSPACE_TRACKING` keeps priority where both environment forms are set; a
  workspace that exported the previously-ignored double-underscore form (`SCRINIA__WORKSPACE__TRACKING`)
  is now honored by the CLI.
- `LegacyMigrationService.Read`/`Inspect`/`Migrate`/`Set` now thread `envSingle`/`envDouble` through
  `Inspect`/`Migrate`/`Set`; `Unset` maps to `null` so a missing policy is never misread as a real
  policy downstream (`EffectivePolicy` returns `Unset` for empty input, not null).

### Removed (breaking) — the `local-topic:` recovery shim sunsets after 2027-03-21

- `PathRouter.ToStoreDir` gates the read-only `local-topic:` recovery shim behind
  `LocalTopicSunsetDate` (2027-03-21). Before sunset a legacy `local-topic:<journal>` scope resolves to
  `.scrinia/memories/<topic>`; after sunset the prefix falls through to the `Unknown scope` throw,
  matching every other prefix. Legacy journals from older store layouts are therefore no longer
  reachable by path once the sunset passes.
- The sunset date is a mutable `internal static` field on `PathRouter`, flipped only through the
  test-only `SetLocalTopicSunsetForTest` seam, so the post-sunset `Unknown scope` throw is assertable.

### Removed (breaking) — legacy `local:agent` entries are promoted on touch and bulk-swept

- `/agent/<subject>` recall now auto-promotes a legacy `local:agent` NMP/2 entry to
  `.scrinia/agent/{subject}.md` on first touch — publish the `.md` body and its `.meta.json` sidecar
  through the shared `FileEntityTransactionCoordinator`, then delete the legacy artifact — and
  `tracking migrate-legacy` bulk-sweeps any remaining `local:agent` entries the same way, fault-isolated
  per entry. Promotion is idempotent: a later read finds the published file and never re-deletes.
- **Breaking:** the NMP/2 `local:agent` store entry is deleted on promotion (the `.nmp2` body via
  `DeleteArtifact`, the sidecar via `Remove` — both no-ops when absent), so a workspace that still
  addressed a raw `local:agent` entry by its store address after this change finds it gone. An
  existing `.scrinia/agent/{subject}.md` file still wins over promotion.
- The promotion is one shared internal primitive (`ScriniaMcpTools.PromoteLocalAgentAsync`), reused by
  both the recall handler and the CLI sweep. The CLI reaches it through `Scrinia.Mcp`'s
  `InternalsVisibleTo("scri")` — the same boundary it already uses for other MCP internals — rather
  than moving the primitive into Core.

## [0.6.0] — 2026-08-27

### Changed (breaking) — unified logical memory addressing

- All public memory-facing contracts use the typed `MemoryAddress` value. .NET callers must use
  `MemoryAddress.Parse(raw)` or `MemoryAddress.TryParse(raw, out address, out error)` instead of
  passing raw address strings through stores, callbacks, or event payloads.
- Persistent addresses remain logical paths such as `/api/auth-flow`; bare subjects are normalized
  at the CLI/tool boundary. Ephemeral addresses have one canonical display form, `~name`.
- `/temp/name` is a supported alias for the ephemeral address `~name`. The alias is canonicalized
  before lookup and responses use the `~name` spelling, so the two forms cannot create separate
  memories.
- The CLI is persistent-only. `scri store`, `show`, `forget`, `append`, `compact`, and `link`
  reject `~name` and `/temp/name` with `forbidden_address_kind` and exit status 1. Use MCP through
  the long-lived `scri serve` process for ephemeral memories.
- MCP address inputs accept logical names and paths only. `file://` URIs and other URI forms are
  rejected with `UNSUPPORTED_INPUT_FORM`; drive paths and other physical filesystem paths are
  rejected as malformed. Scrinia does not resolve caller-supplied addresses to arbitrary files.
- `ArtifactEntry` now exposes the logical `Address` and an opaque, store-relative `ArtifactKey`.
  Artifact keys are portable persistence data, not URIs; stores own their resolution and callers
  must not construct filesystem paths from them.
- Plugin callbacks and `IPluginStore` artifact reads use typed addresses. Plugin authors must pass
  `MemoryAddress` values and use the supplied store for artifact access rather than implementing
  URI resolution or relying on physical locations.

Existing persisted metadata is read through the current sidecar/bundle migration path. Legacy
physical-location fields are discarded at bundle import; a legacy subject is converted to a
logical address and a missing artifact key receives a synthetic portable key. Physical URI-shaped
artifact metadata is not a supported public input or output.

### Fixed — interrupted bundle restores recover per topic scope

- Bundle topic restore now records a durable, versioned journal containing the final artifact body and
  sidecar bytes before changing that topic. After an interruption, the next access rolls the affected
  topic forward to those verified bytes before returning data or accepting a write.
- Recovery is scoped to one persistent topic and is demand-driven. Other topics and ephemeral memories
  remain available; imports across multiple topics and skill/agent file entities are not one transaction.
- A malformed, integrity-failed, or newer restore journal is retained and blocks only its topic with
  `RECOVERY_BLOCKED` guidance. Recovery never guesses, rolls back, or rereads the source bundle.

### Fixed — durable file replacement and bundle file-entity admission

- Artifact sidecars, skill and agent files, and imported bundle file entities now replace each selected
  file through a flushed sibling temporary file, so readers do not observe a truncated replacement.
- Bundle file entities are validated before topic import begins. Missing, unsafe, malformed, or
  forward-versioned members now fail with `INVALID_BUNDLE`; filesystem write failures report
  `DURABLE_WRITE_FAILED` through both MCP and `scri bundle import`.
- This is per-file durability, not transactional restore: an interruption can leave a complete applied
  prefix of a skill/agent pair or bundle file set. Topic artifact restores use their own per-topic
  recovery journal; file-entity sets remain independently durable.

### Fixed (breaking) — public outcomes now match durable writes and CLI data

- Persistent sanitized subjects over 182 characters now fail early as `INVALID_PARAMETER`, with no
  filesystem side effects. Existing entries remain readable and are not renamed.
- Empty search/list and no-op compact are warnings and exit 1 rather than successful exit 0.
- Removed the fabricated `rescuedScopes`, `chunkCount`, `success`, and export `path` JSON
  properties; reindex now exposes `embedded`, `total`, `skipped`, and `failed`.
- NMP/2, sidecars, bundles, and plugin/embedding layouts did not change.

### Changed — every durable format now carries a version and an unknown-field bucket

- The artifact sidecar, `facts.json`, the NMP/2 body sentinel, a bundle's `manifest.json` and its
  entries, the legacy `index.json`, and now the skill/agent `.meta.json` sidecars are all routed
  through one shared version gate (`Scrinia.Core.Formats`) instead of scattered ad-hoc checks. The
  last two — skill and agent sidecars — were not in the review finding that started this work at
  all; they turned up only by enumerating every durable format on disk instead of sampling the one
  format that had been reported. Seven formats, not one.
- A sidecar declaring a version newer than this build writes loads read-only: reading and search
  keep working, but a write to that specific file is refused rather than silently downgrading or
  clobbering data from a newer build. Unknown JSON keys — fields a future build added that this
  one doesn't know about — now round-trip through a read-modify-write cycle instead of being
  silently dropped.
- **No format's version was bumped.** This ships the gate, not a migration — every existing
  sidecar in a workspace continues to load and serialize byte-identical to before. A v1 skill or
  agent sidecar still serializes with no `"v"` key at all, exactly as it always has.
- Skill and agent sidecars' `WriteSidecarMeta` now uses the same flushed atomic replacement as
  artifact sidecars and imported file entities.

### Fixed (breaking) — `scri bundle import` no longer deletes memories written during it

- A restore snapshotted the scope index under a shared lock that released immediately, wrote every
  bundled artifact with no lock held, then committed through an orphan sweep that deletes any
  sidecar absent from that snapshot. A memory written while a restore ran was therefore deleted
  **after its own write reported success** — content orphaned, sidecar gone, absent from the index
  permanently. Separately, a memory the bundle never mentioned could have its sidecar rewritten
  from the stale snapshot, leaving its metadata describing an older version than its content.
- Import is now a single locked, additive operation: it adds and overwrites, and the orphan sweep
  is gone from this path entirely rather than locked around it. `SaveIndex`'s own behaviour is
  unchanged.
- Bundled bodies are staged and committed per entry, so a failed or interrupted restore can no
  longer leave a memory whose content and sidecar disagree. A restore that dies part-way now
  leaves a prefix of fully-applied entries; `.staging` leftovers are cleared by the next import.
  One narrower case is not covered and is inert: a crash *inside* the staged body's own atomic
  write leaves a `….staging.{pid}.{guid}.tmp` file, which the sweep's glob does not match. It is
  never read, never discovered as a memory, and accumulates only across crashed imports.
- **Behaviour change:** a concurrent writer to a scope being restored now blocks for the duration
  of that scope's import, and fails loudly if it exceeds its own lock timeout. It previously
  appeared to succeed and silently lost the write. The restore itself takes a 5-minute lock
  budget, so a large bundle no longer fails against the 5-second default that suits a single write.

### Removed (breaking) — the HTTP server, remote mode, and the web UI

- The ASP.NET Core HTTP API — API-key auth, multi-store routing, the REST memory endpoints, the
  chat agent loop — and the React web UI that consumed it are gone, along with `scri serve
  --remote`, which proxied the same MCP tool surface to a remote instance. `HttpMemoryStore`, the
  second `IMemoryStore` implementation, is gone with them.
- **There is now one deployment mode.** `scri` runs as a command-line tool and as an MCP server
  over stdio, reading and writing a local `.scrinia/` directly. Anyone running the HTTP server has
  no upgrade path within Scrinia and should pin to `0.5.0`.
- Sharing a workspace between people is a git question rather than a server one: `.scrinia/` is
  committed like any other directory and the bundled merge driver resolves conflicts. See
  [Multi-User Setup](docs/multi-user-setup.md).
- `docs/server-admin.md`, `docs/architecture/server.md` and `docs/web-ui-guide.md` are removed;
  `docs/security.md` is rewritten for a local-only tool.
- Unaffected: every MCP tool, every `scri` verb, the on-disk format, and the embeddings plugin.
  The MCP surface was never the server's — the CLI has always hosted it over stdio.

### Changed (breaking) — one store interface, and `copy` goes with the server

- `ILocalMemoryStore` is merged into `IMemoryStore`. The split existed so a remote store could
  decline filesystem-layout members; with one implementation the narrowing casts could only
  succeed and the `NOT_SUPPORTED` responses behind them were unreachable. Six members —
  `ArtifactPath`, `FindArtifactPath`, `GetStoreDirForScope`, `WorkspaceRoot`, `ListTopicArtifacts`,
  `ImportTopicEntries` — now sit on `IMemoryStore`. Anything implementing the interface must
  answer them.
- **`copy` is removed, and one capability goes with it:** an ephemeral `~name` can no longer be
  promoted into the persistent store. Nothing else did that, so there is no replacement. Copying a
  persistent memory to a new name is still available by `recall` then `remember`.
- `IStorageBackend`, `FilesystemBackend`, `CarriedMetadata`, `RemoteApiVersion` and
  `RemoteTransportGuard` are removed — each existed only to let one store behave like the other.

### Changed (breaking) — the plugin ABI drops its web half

- `IScriniaPlugin` and `ScriniaPluginBase` lose `ConfigureServices(IServiceCollection,
  IConfiguration)`, `ConfigureMiddleware(IApplicationBuilder)` and
  `MapEndpoints(IEndpointRouteBuilder)`. Their only caller was the deleted server's in-process DLL
  loader; nothing in the repository loads an assembly any more. What remains is identity —
  `Name`, `Version`, `Order`.
- **CLI plugins are unaffected**, and this is the shape that works: a plugin is a standalone
  executable the CLI launches as a child process and drives over MCP stdio. Discovery, the
  handshake and `PluginConfigProtocol` are unchanged. A plugin that implemented the three removed
  members will not compile — but since the only host that ever invoked them is gone, it cannot
  have been doing anything.
- Those three members were the sole reason `Scrinia.Plugin.Abstractions` carried a
  `FrameworkReference` on `Microsoft.AspNetCore.App`, which a program with no HTTP surface has no
  business requiring of its plugin authors. Dropping it took **27 MB** out of the published
  `scri-plugin-embeddings` binary — 129 MB to 102 MB — because a self-contained publish had been
  bundling the entire ASP.NET Core runtime to satisfy it.
- If your plugin needs configuration, bind it yourself; `EmbeddingsPlugin.Configure(IConfiguration)`
  is the worked example. See [Plugin Authoring](docs/plugin-authoring.md).

### Changed (breaking) — `scri migrate` joins the CLI option family

- `scri migrate` took `--workspace` where every other command takes `--workspace-root`. It now
  takes `--workspace-root`. A script passing `--workspace` must be updated.
- `scri migrate --json` is new: the command previously printed only human-readable text, so a
  caller could not tell a completed migration from a no-op without parsing prose.
- A bare `--backup` is no longer accepted. It was a no-op — see the backup fix below.

### Changed (breaking) — `publish.ps1`'s `-WithVulkan` is now `-WithEmbeddings`

- The switch decides whether the embeddings plugin ships; Vulkan is merely the backend that plugin
  carries. The new name matches `publish.sh`'s existing `--with-embeddings`, so the two scripts
  now take the same argument for the same decision.

### Fixed — `scri migrate`'s backup switch now exists

- `scri migrate` documented `--backup=true|false` for turning off the pre-migration backup. No
  such switch was reachable: the parameter was declared `bool backup = true`, and the CLI framework
  binds a `bool` as a bare presence flag with no value syntax, so `--backup=false` never parsed and
  the backup could not be turned off by any input. The documented capability did not exist.
- The switch is now `--skip-backup`, absent by default, and it works. Behaviour with no flag is
  unchanged: the backup still runs. This is a fix rather than a rename — there was no working
  spelling to rename from.

### Fixed — the non-Windows publish path

- `publish.sh` now publishes `scri-merge` when building for the host platform. A local Linux or
  macOS build previously produced a workspace whose configured merge driver pointed at a binary
  that had never been built. Release artifacts were never affected: `release.yml` builds each
  platform on its own runner and has always shipped `scri-merge`.
- A foreign-RID build of `scri-merge` is skipped with a warning rather than attempted. It is
  NativeAOT, and native compilation cannot cross OS or CPU architecture.
- `publish.sh`'s cleanup step, which deleted the plugin directory it had just populated, is gone.
- Both publish scripts now take their project list from `publish.projects.tsv`, so a project added
  to one is added to both, and a test holds the manifest against the release workflow.
- Both scripts now refuse to publish when the Vulkan native libraries are absent (exit status 1),
  instead of silently shipping the CPU backend under a Vulkan-named build.

### Fixed — `scri hint --json` emits parseable JSON, and the documented environment form resolves

- `scri hint --json` hand-built its output with an escaper that did not escape control characters,
  so a memory name containing one produced output no JSON parser would accept. It now serializes
  through the same source-generated context as the rest of the CLI. The output shape changes as a
  result: `CliJsonContext` writes indented (multi-line) JSON where the old hand-rolled emitter wrote
  one line. Field names are unchanged — `count`, `matches`, `scope`, `name`, and `score` still mean
  the same thing — so a caller parsing the JSON rather than matching the raw text is unaffected.
- `SCRINIA__EMBEDDINGS__PROVIDER` — the double-underscore form the configuration docs prescribe —
  now resolves in the CLI. Both underscore forms work;
  `SCRINIA_EMBEDDINGS_PROVIDER` keeps priority where both are set, so no installation that set the
  single-underscore form changes behaviour. An installation that exported the double-underscore form
  (previously ignored by the CLI, which fell through to `.scrinia/config.json`) will see the CLI pick
  it up now.

### Changed (breaking) — MCP `search`, `list` and `facts` take `scopes`/`excludeTopics` as arrays

- `search`, `list` and `facts` now take `scopes` and `excludeTopics` as string arrays instead of
  comma-separated strings, matching `attributeFilter`'s existing map shape. A comma-separated
  string is no longer accepted; callers must pass `["local", "api"]` rather than `"local,api"`.
  An empty array behaves identically to omitting the field. This closes contradiction 6 of 6 in
  the published-surface-coherence spec: the old comma encoding could not express a scope name
  containing a comma. The CLI (`scri list`/`scri search`/`scri facts`) is unaffected and keeps its
  comma-separated `--scopes`/`--exclude-topics` arguments — a deliberate divergence, since
  command-line arguments are strings and repeated flags would be a worse CLI for no gain.

### Changed (breaking) — `Scrinia.Core.IMemoryStore`'s scope/excludeTopics members take lists, not strings

- `IMemoryStore.ListScoped`, `AnyScoped`, `ResolveReadScopes`, `GatherTopicInfos`,
  `ShouldExcludeScope`, `BuildExcludedScopeSet`, and `Search.SearchQuery.Scopes`/`.ExcludeTopics`
   all changed from `string?`/`string` to `IReadOnlyList<string>?`/`IReadOnlyList<string>`. This is
   source- and binary-breaking for any out-of-tree `IMemoryStore` implementation or plugin that
   binds to these members directly. An
  implementation must update its signatures to the list-typed shape to keep compiling against this
  version.

### Changed (breaking) — `restore` now places workspace context in `info:`, matching `guide`

- `restore` now returns the workspace context in `info:` with a summary line in `content:`,
  matching `guide`. Callers reading the sections out of `content:` must read `info:`.
- `scri restore --hook` embeds this yaml verbatim into the `<scrinia-restored-memory>`
  SessionStart payload, so the field move is visible to every agent at session start. The framing
  text and the envelope are unchanged; only the field the sections sit under moves.

### Changed (breaking) — MCP `list` and `reconcile` reject inputs they used to silently paper over

- `list` now errors on an unrecognised `mode` (`INVALID_PARAMETER`, `unknown mode '...'. Use
  'summary', 'full', or 'drift'.`) instead of silently falling through to the `summary` view. A
  caller that sent a typo'd or stale mode string and relied on the quiet fallback now gets a
  structured error instead of an unexpected listing. Explicitly passing `mode: null` or
  `mode: ""` is a deliberate carve-out and still normalizes to `summary` — that is a caller
  omitting the field over JSON-RPC, not naming an unrecognised one, and matches how `facts`
  already treats its own `op` parameter.
- `reconcile` now errors (`'choice' requires 'conflictId' — without one there is no conflict to
  resolve, and the call would silently perform a read-only scan instead.`) when `choice` is
  supplied without `conflictId`. It previously ran the same read-only scan `reconcile({})` runs —
  which reports `action: "reconciled"` on every path, including "nothing found" — so a caller that
  passed `choice` alone, expecting it to apply, was told a resolution had happened when nothing on
  disk had changed.

### Changed (breaking) — `IMemoryEventSink` receives `IPluginStore`, not `IMemoryStore`

- `IMemoryEventSink.OnStoredAsync`, `OnAppendedAsync` and `OnForgottenAsync` now take
  `Scrinia.Core.IPluginStore` — `ParseQualifiedName`, `IsEphemeral`, `ResolveArtifactAsync`,
  `ReadArtifactAsync` — instead of the full `IMemoryStore`. Source-breaking for any out-of-tree
  sink implementation; the fix is a parameter type change. A sink needing more can cast to
  `IMemoryStore`, which every concrete store still implements.
- `MemoryArtifactSource.ReadForAppendAsync` and `MemoryVectorIndexer.RemoveAsync(qualifiedName,
  store, ct)` now take `IPluginStore` instead of `IMemoryStore`. Narrowing a
  parameter to its base interface is contravariant widening: a caller already holding an
  `IMemoryStore` still satisfies the new parameter type with no source change. What changes is
  binary: a precompiled caller assembly requires recompilation to link against the new signature,
  or will fail at runtime with `MissingMethodException`.
- `IMemoryStore` now derives from `IPluginStore`. No change is required of store implementations:
  the four members moved to the base interface and are inherited back.

### Changed — `guide()` no longer truncates its own guide document; truncation advice fits the tool that gave it

- `guide()` is now exempt from the response-size cap that every other tool's `content:` is subject
  to. The embedded guide document had grown to 8046 characters against the 7992-character cap
  (`MaxResponseChars`(8192) − yaml overhead(200)), so it was silently losing its last ~54
  characters — mid-sentence, off the end of the "Workspace" section — on every call. Truncating a
  build-time-fixed document protects nothing; the document is bounded by construction, not by a
  caller's query, so it is enforced at test time instead (`GuideSizeTests`), not clipped at
  response time.
- When `search`, `recall` or `list` truncates, the `followUp` advice now names that tool's own
  parameters (e.g. recall's `recall({ path, chunk: N })`) instead of the generic "narrow with
  search, or recall a chunk" text, which was meaningless coming from a tool with no query to narrow
  and no chunk to recall.

### Changed — `/skill/` is readable, writable and deletable through one dispatcher

- `forget({ path: '/skill/{name}' })` now actually deletes the file. It previously fell through
  to the generic memory-forget path and answered `NOT_FOUND` for a skill `recall` had just
  returned — a skill on disk could be read but never forgotten through the facade. The file is
  archived under `.scrinia/skills/versions/` first, the same way `remember`/`SkillCreate` already
  archives before overwriting.
- Forgetting a **built-in** skill with no project-level override now returns its own
  `INVALID_PARAMETER` error explaining that it ships with Scrinia and cannot be forgotten, with
  `recall`/`remember` follow-ups — not `NOT_FOUND`, which would misdescribe a skill `recall` will
  happily keep returning. Forgetting an override of a built-in deletes the override, reports that
  the built-in resolves again, and leaves the built-in itself untouched.
- `remember({ path: '/skill/...' })` now warns when `attributes` or `keywords` are also supplied,
  since skill files are plain markdown on disk and neither parameter has any effect there.

### Changed — writing memories no longer re-reads the scope, and a memory's vectors replace in one locked write

- **Writing a memory used to cost the whole scope.** `FileMemoryStore.Upsert`, `Remove` and
  `TryUpdateEntry` invalidated the per-scope index cache on every write and rebuilt it by reading
  and JSON-deserializing every sidecar in the scope — so writing N memories into a scope cost
  O(N²) file reads, and `TryUpdateEntry` paid that cost twice. They now update the cached index
  copy-on-write instead: build a new entry list with just the written entry's slot replaced (or
  appended, or omitted for a remove) and install a new cached index over it, reading no sidecar the
  write did not have to touch anyway. A scope that isn't already cached is left uncached rather
  than partially populated; the next read cold-loads it as before. This still honours the
  long-standing rule that a cached index is only ever swapped, never mutated in place
  (`docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md`) — the write path now
  satisfies it far more cheaply. See
  `docs/superpowers/analysis/2026-08-17-write-path-cost-measurements.md` for measured before/after
  timing and allocation.

- **`TryUpdateEntry` reads the entry it mutates off disk, inside the per-scope lock.** It writes
  back a *complete* entry derived from what it read, so serving that read from the in-memory index
  cache would republish stale metadata over a concurrent writer's — reverting fields the mutation
  never touched. Since `.scrinia/memories/**/*.meta.json` are git-tracked in workspaces that
  version their memory, a `git pull` under a running `scri serve` was enough to produce that state.
  It now costs one sidecar read, not the scope-wide re-read it used to pay, so the cost this
  release removes stays removed.

- **Out-of-band changes to a scope's sidecars no longer surface on that scope's next write.**
  Invalidate-and-rebuild made every write an accidental resync point with the filesystem; with
  copy-on-write there is none, so a `git pull`, `merge` or `checkout` stays invisible to a
  long-lived process (restart it, or read a scope it hasn't cached). The accepted cost of the
  change above, recorded in `docs/architecture/core.md`.

- **Legacy `"fields"`→`Attributes` normalization moved into the sidecar load seam itself.** Four
  call sites used to each wrap their own read with `.Select(e => e.NormalizeAttributesKey())`;
  `FileMemoryStore`'s internal `LoadEntriesFromSidecars` now applies it once, so no future read
  site can forget to.

- **Writes no longer clear the topic-discovery cache when the topic list can't have changed.**
  `Upsert` and `Remove` now clear it only on a scope-lifecycle transition — a scope directory
  gaining its first sidecar, or losing its last — instead of on every single write into an
  already-populated scope. `TryUpdateEntry` no longer clears it at all: it only ever updates an
  existing entry, so the scope was and stays non-empty and the topic list cannot change. It used to
  clear it on every call, and `MaintenanceEventSink` calls it once per memory the stored content
  references plus once or twice for the orphan marker, so that forced a full re-walk of the
  `memories/` tree on the next search or scope resolution, several times per store event.

- **`MaintenanceEventSink`'s auto-link/orphan-detection pass no longer materializes the whole
  store to answer "does anything reference this memory?"** A new `IMemoryStore.AnyScoped(scopes,
  predicate)` answers that without building the listing first. `FileMemoryStore` overrides it to
   enumerate scope by scope and return on the first match; the default implementation inherits
   `ListScoped(scopes).Any(predicate)`, which is still correct but
  materializes the listing exactly as before. The sink's second question — "does this entry still
  carry the legacy `orphan` keyword?" — is about one named entry in one known scope, and now reads
  that scope alone rather than scanning every scope in the store.

- **A memory's vectors now replace in a single locked write.** `VectorStore.ReplaceMemoryVectorsAsync`
  collapses what was a `RemoveAsync` call followed by one `UpsertAsync` per vector into one lock
  acquisition, one file probe and one batch of appends. The in-memory replace is now genuinely
  atomic — the new vector list is swapped in under the scope lock in one step, so a concurrent
  vector read can no longer observe a half-replaced memory. The on-disk write is not atomic: the
  delete and add operations still share one open file whose writer can flush more than once for
  anything but a small batch, so a crash mid-write can still leave a memory half-replaced on disk —
  recoverable the same way it always was, by the next index rebuild. What improved is the width of
  that window, not its existence. `UpsertAsync`/`RemoveAsync` are unchanged and remain in use by
  their other callers. See the measurements doc above for before/after timing, allocation and
  file-operation counts.

### Fixed — one embed-and-index policy, and the GPU plugin no longer degrades the search it improves

- **Installing the Vulkan embeddings plugin used to make semantic recall on long memories
  worse or absent.** Seven implementations of embed-and-index had drifted apart, and the
  load-bearing divergence was the chunking unit: some paths sliced content into
  overlapping windows, while the plugin never windowed at all — so a 40 KB memory became one
  mean-pooled vector, or the provider's context limit was exceeded and the memory silently got
  zero vectors. A new `MemoryVectorIndexer` in `Scrinia.Core.Embeddings` now owns windowing,
  the per-memory chunk cap, batching, the vector-count guard, an atomic-in-memory per-memory
  vector replace and write retry for every path. The plugin's reindex additionally never removed
  prior vectors, so it orphaned stale chunks behind each refresh; it now removes first like
  everything else.

- **Chunk-level semantic scores never reached the ranker.** `ChunkEntry.ChunkIndex` is 1-based
  over content elements, and that is the key the search scorer probes — but the built-in path
  wrote 0-based character-window ordinals into that key position, so the probe could never hit.
  Content elements are now the addressable unit and windows nest inside them, with a new
  `VectorScoreProjection` owning the read-side key contract. **Existing vector files are
  quarantined on first load** — the embedding signature carries a layout marker, so the same
  path that already handles a model switch detects them. Until they are rebuilt those memories
  fall back to keyword-only search; nothing is lost, because vectors are derived data.

  **Which deployments rebuild them for you:**

  | Deployment | Migration |
  |---|---|
   | CLI with built-in embeddings | Automatic on next launch |
   | CLI with the Vulkan plugin | Run `scri reindex` |

- **Appending to a memory destroyed its embeddings.** On the built-in path an append embedded
  only the appended fragment, and indexing replaces a memory's vectors atomically — so the rest
  of the memory lost its vectors entirely. Every path now re-reads the memory's full current
  content on append, which is what the Vulkan plugin already did.

- **A `forget` against a store whose vectors hadn't yet been loaded into memory could leave a
  deleted memory's vectors in place and semantically searchable.** `VectorStore.RemoveAsync`
  treated an unloaded scope's cache as empty instead of lazily loading it from disk first — the
  same class of bug `UpsertAsync` already guarded against. Concretely: the first vector
   operation a fresh `VectorStore` performed against a scope, if it happened to be a remove (e.g.
   the leading half of `MemoryVectorIndexer`'s remove-then-upsert in a freshly started process),
  silently no-opped — nothing looked stale in memory, so the on-disk vectors were never removed
  and a subsequent upsert merged in beside them instead of replacing them. `RemoveAsync` now
  loads the scope the same way `UpsertAsync` does.

- **The whole-document vector has been retired.** Embedding a memory used to also produce one
  mean-pooled vector over its entire joined content. A memory's semantic score is now the best
  of its windows rather than the average of everything it contains — the same reason a long
  memory needed windowing in the first place. Nothing needs to be done about this: the rebuild
  described above produces the new layout.

- **The plugin's configured chunk settings never reached its embedding signature.** It composed
  the signature from a default-constructed options object, so a store with custom `ChunkSize`
  or `ChunkOverlap` was stamped as though it used the defaults.

- **`Scrinia:Embeddings:ChunkSize`, `:ChunkOverlap` and `:MaxChunksPerMemory` were silently
  ignored whenever the out-of-process Vulkan plugin owned embeddings.** The host forwards a
  fixed set of config keys to the plugin child process and these three were not on it, so the
  plugin embedded at built-in defaults while the host composed its signature from the configured
  values. They are now forwarded and parsed. Same defect as the in-process one above, one
  process boundary further out.

- **`MaxChunksPerMemory` now defaults to 500, up from 100, and is clamped below 1000.** Under
  the element/window layout every non-blank content element plans at least one chunk, so the cap
  is also a floor on how many content elements can be vector-reachable — a memory of 150 short
   elements lost everything past the hundredth. Because `append` adds elements at the end, the
   dropped tail was the *newest* content. A configured value of 1000 or more used to throw from a
   constructor with no config guardrail; it now clamps to 999 with a warning,
  matching how the sibling `ChunkOverlap`/`ChunkSize` invariant already behaved.

- **Appending to an *ephemeral* memory silently left its vectors stale.** All three append
  re-embed paths read the memory back through `ReadArtifactAsync`, which has no artifact keyed by
  `(scope, subject)` for ephemeral memories and throws — straight into the catch that logs and
  moves on. The two reindex loops already handled it. One helper now owns that rule for all five
  call sites.

### Changed — plugin hosts prefer a single `index` call

- The out-of-process plugin protocol gains an `index` tool that takes a memory's whole content
  array, so the shared indexer runs inside the plugin process instead of the chunking policy
  being restated by every caller that speaks the protocol. `upsert` is unchanged and still
  supported: a plugin that offers only `upsert` keeps receiving the previous call sequence, and
  the host warns on stderr naming what that costs.

### Removed — three unreachable features, and a config key that now works

- `HnswIndex` (referenced only by its own tests and benchmarks; its documented 1000-vector
  threshold existed nowhere in code), `EntityGraph`'s instance side (superseded by
  `EntityFactStore.ScoringAdjacency`, which is what production uses), and `MergeConfig.Resolver`
  and `ResolverCommand` (written by setup, never read by the merge driver) have been deleted.
- **`Scrinia:Search:Fusion` now selects the ranker's fusion mode.** RRF was implemented,
  benchmarked and tested, but `RankerOptions.FromConfig` silently dropped the parameter — a
  positional-record argument that was simply never passed — so production could not reach it.
  Accepts `sum` (default) or `rrf`.

### Changed (breaking) — public surface of `Scrinia.Core` and `Scrinia.Merge`

`Scrinia.Core` is BSD-licensed and other code may compile against it directly, so every change
to a public type is listed here in full.

**Removed or narrowed**

- **`EntityGraph` changed from a `public sealed class` to a `public static class`.** Source- and
  binary-breaking: anything that instantiated `EntityGraph` or held an instance reference will
  fail to build. Both `public static EntityGraph.Build` overloads are gone as well — the
  `(DocKey, Keywords)` one and the `(DocKey, Keywords, Attributes)` one — along with the
  instance members `AddDoc` and `Score`. Use `EntityFactDerivation.BuildStore` and
  `EntityFactStore.ScoringAdjacency`, which is what production runs. `ParseEntityKeywords`,
  `ParseEntityAttributes` and `RealTypes` survive unchanged.
- **`HnswIndex` deleted.** It had no caller outside its own tests and benchmarks.
- **`MergeConfig`'s constructor arity dropped from 4 to 2**, and `ConflictDir` moved from the
  4th positional slot to the 2nd, because `Resolver` and `ResolverCommand` were removed.
  **`new MergeConfig(0.7, "conflict")` still compiles and silently means something different**:
  the second argument used to be `Resolver` and is now `ConflictDir`. Any positional
  construction must be re-read, not just recompiled.

  Reading an existing `merge.config` file is safe. System.Text.Json skips members it cannot map,
  so a file still carrying `resolver` / `resolverCommand` loads with the remaining fields intact
  — there is no migration to run and no need to rewrite the file. A test fixture pins this.
- **`ScriniaCommands.Setup` lost its `resolver` parameter**, and the `scri setup --resolver`
  flag is gone. It only ever wrote the `MergeConfig` fields that nothing read.

**Added**

- `Scrinia.Core.Embeddings.MemoryVectorIndexer` — the single embed-and-index policy.
- `Scrinia.Core.Embeddings.VectorScoreProjection` — the single read-side key projection.
- `Scrinia.Core.Embeddings.MemoryArtifactSource` — reads a memory's content array by
  `(scope, subject)`, ephemeral scope included.
- `Scrinia.Core.Search.ScoringWeights` — the scoring rubric, previously restated in prose in
  five places.
- `EmbeddingOptions.ClampChunkCapBelowStride` and `EmbeddingOptions.MaxChunksPerMemoryCeiling`.
- `PluginConfigProtocol.BuildOptions` — the plugin-side half of the host's config forwarding.
- `EmbeddingReindexer.ProbeStaleScopes` — quarantine detection, separated from remediation.

**Widened**

- `EmbeddingsPlugin.ReindexStoreAsync` changed from `internal` to `public` (it implements
  `IEmbeddingsAdmin`).

### Fixed — a memory remembered at `/temp/x` is now reachable at `/temp/x`

- **`recall`, `forget`, `compact` and `link` now resolve the same `/temp/x` address that `remember`
  just accepted.** The alias rule was two string literals in `MemoryTools.Core.cs` (Remember and
  Append) and existed nowhere else, so those four answered NOT_FOUND for a path `remember` had
  just accepted — including the literal example in `forget`'s own tool description. The rule is
  now `MemoryNaming.CanonicalizeEphemeralAlias`, applied at the two surfaces (the MCP facade and
  the CLI commands).

### Changed — the reserved-prefix hint for a case-mismatched `/TEMP/...` path no longer fires

- The reserved-prefix hint for a case-mismatched `/TEMP/...` path no longer fires, because the
  alias is canonicalized before `ReservedPrefixGuard.HintFor` (`MemoryTools.cs:188` for `store`,
  `:328` for `append`) sees it. The path now simply works, which is what the hint was pointing at.

### Changed (breaking) — ephemeral addresses are refused outside the MCP facade

- **Ephemeral addresses are refused by the CLI.** `scri store`, `show`, `forget`, `append`,
  `compact` and `link` exit 1 for a `~name` or `/temp/...` address. Previously
  `scri store /temp/x` reported success and exit 0 while discarding the content: the ephemeral
  map is in-memory and the CLI process exits immediately. Both spellings are marked
  CLI-unavailable in the reserved-path tables.
- **No migration for existing `.scrinia/memories/temp/` bytes (D-5) — read this before relying on
  a memory stored there.** They are left exactly in place, under the persistent topic scope
  `local:temp`, and are never moved, renamed or deleted by this change. But the address that used
  to reach them, `/temp/x`, no longer resolves there from an MCP client or the CLI: both now
  canonicalize `/temp/x` to the in-memory ephemeral `~x` before resolving, so such a memory can no
  longer be **read or deleted by path** from either surface. `list` and `search` still *display*
  it as `/temp/x` — `FileMemoryStore.FormatQualifiedName` (`:272-275`) formats `local:temp` that
  way regardless of this change — so the address shown in those results is not one that resolves.
   The supported recovery is to export the `temp` topic and re-import its contents under a different
   topic name.

### Fixed — a long destination name turned a link into keywords

- **`link()` no longer degrades a long destination name into a plain keyword.** It recorded each
  side's reference by minting a `ref:{name}`-shaped keyword and relying on keyword routing to turn
  it back into a `ref` attribute. That conversion is conditional: past 120 characters the value
  fails routing's cap and the element stays a keyword, which is then merged into the memory's
  `keywords` (and, from there, its agent-keyword provenance) with no upper bound. The reference is
  now written directly as an attribute, so the destination's name length no longer changes anything
  — a link mints no keyword at any length. Visible effect: a memory linked to long-named
  destinations keeps the `keywords` it derived from its own content instead of accumulating one
  `ref:...` entry per link. Short-name links are unchanged in every respect, including the exact
  attribute value stored and the `N attribute(s) merged` message.

### Fixed — file and memory references no longer extract from pasted code

- `ReferenceExtractor.ExtractFileRefs`/`ExtractMemoryRefs` now scan outside fenced (```` ``` ````)
  code blocks only, via the shared `ProseScanner`. A stack trace, `git diff`, or file listing pasted
  into a **properly closed** fenced block in a memory's content no longer yields spurious file-path
  or `[[wiki-link]]` drift baselines. YAML frontmatter and inline-code spans are unaffected — both
  stay prose, so a `file:` line in frontmatter and a backtick-quoted `[[wiki-link]]` or `~ephemeral`
  still extract. An **unterminated** fence (an opening ``` with no matching closer) is not a fence at
  all — the opener line and everything after it are ordinary prose and extract normally, same as
  before this change existed: `ReferenceExtractor` had no fence awareness at all prior to this
  release, so no fence, closed or not, ever excluded anything from file/memory reference extraction.
  Net effect on an unterminated fence here: none. The only net change is on a properly closed one.
- **Behaviour change on three write-path consumers of the same fence-scanning rule**, all called on
  every store, and two of them on every append too:
  - `AttributeExtractor.ExtractRefAttributes` (`MemoryDerivation.cs:99` store, `:239` append)
    derives the `file`/`ref` attributes written onto every memory. This is the dominant effect in
    practice — most content carries far more file-path mentions than wiki-links. A memory whose only
    mention of a file or another memory lived inside a properly closed fence no longer carries that
    mention in its own `file`/`ref` attributes.
  - `MaintenanceEventSink` calls `ExtractMemoryRefs` to auto-link `[[wiki-link]]` targets and detect
    orphans. A wiki-link that only appears inside a properly closed fenced block no longer triggers
    the auto-link write onto its target — and because that write is also what clears the
    *mentioning* memory's own orphan flag as a side effect, a memory whose only outbound
    `[[wiki-link]]` lived inside such a fence can now be marked `orphan:true` itself (not the memory
    it linked to).
  - **`AttributeExtractor.Extract`** (`MemoryDerivation.cs:103` store, `:238` append) and
    **`AttributeExtractor.ExtractMentionSpans`** (`MemoryDerivation.cs:105`, store only — mention
    spans are deliberately not re-extracted on append) are where the unterminated-fence direction
    IS a genuine, new change: unlike `ReferenceExtractor`, these two already treated an unterminated
    fence as running to end-of-content before this release, so a memory with one stray, never-closed
    ``` lost every `key: value` / `**Bold:**` / inline `key:value` attribute and every `mention:`
    span after it, silently. That over-suppression is now fixed — content after an unterminated
    fence extracts attributes and mention spans normally, the same as any other prose.
    `ExtractMentionSpans` separately stops stripping inline `` `code` `` spans before scanning for
    capitalized runs, and reads YAML frontmatter as ordinary prose instead of skipping it outright —
    both were excluded by logic this change deletes in favour of the one shared `ProseScanner` rule,
    so an identifier that was previously fenced by backticks (or that only appeared in frontmatter)
    can now surface as a `mention:` span. Measured across the 16 top-level memories under
    `.scrinia/memories/findings/`: the symmetric difference between the old and new mention-span
    sets, uncapped, is **11** (11 spans newly present, none lost). The production
    `MaxMentionSpans = 32` cap floors 15 of those 16 files at capacity, which masks this effect
    rather than measuring it — a capped comparison shows only 8 and is not a conservative estimate
    of the real change, since an addition to an already-full set can only displace an existing
    value, never grow the count.

  All of this is the fix working as intended — a reference, attribute, or mention pasted as part of
  quoted/sample content inside a real, properly closed fence was never meant to create a real
  cross-reference, drift baseline, attribute, or mention span; and (for `Extract`/
  `ExtractMentionSpans` specifically) a typo'd fence marker was never meant to blank out the rest of
  a memory's attributes either. Both are visible behaviour changes for any existing memory affected.
  **Migration:** this only takes effect when a memory is re-derived via a full `remember`. An append
  extracts from its new chunk only and never re-derives from a memory's existing content (D-22), so
  appending to a memory does not recompute its already-stored shape. Memories written before this
  change keep whichever `file`/`ref`/attribute/`mention` shape their content produced under the OLD
  rules until they are next stored with `remember` — a corpus can hold multiple shapes side by side
  indefinitely, and appending does not migrate one.

### Changed (breaking) — `reconcile()` refuses a sidecar conflict whose two sides state different facts

- **`reconcile()`'s automatic `.meta.json` merge now escalates when the two sides disagree on
  `name`, `uri`, `description`, `originalBytes` or `chunkCount`**, instead of resolving and keeping
  one side's copy. These are the fields the merge does not itself compute, and with no common
  ancestor there is nothing to say which side changed one — so a disagreement is not a merge, it is
  a question. The git merge driver has always answered it that way; `reconcile()` did not, and the
  two disagreeing was the actual defect. A conflict that resolved automatically before this change
  may now require an explicit `choice`.
- The refusal names the fields it refused on (*"the two sides disagree on originalBytes, chunkCount,
  which the merge cannot decide — there is no common ancestor to tell which side changed it"*),
  rather than falling through to the generic `"'choice' is required"`, which reads as though the
  sidecar were malformed. `reconcile()`'s scan reports the same reason. This is the third distinct
  outcome, alongside the dissimilarity gate below and a genuine parse failure.
- A field only **one** side sets is unchanged behaviour and still merges — a side that never set a
  field did not change it, so the other side's value is adopted.
- **Also fixed, same root cause: the two mergers could write different bytes for one conflict.**
  `reconcile()` used to build its result on whichever side had the later `updatedAt`, defaulting to
  *theirs*; the driver always builds on *ours*. Because the keyword and term-frequency unions are
  case-insensitive and keep the first-seen spelling, one conflict resolved on two machines could
  produce `keywords: ["Auth","JWT"]` on one and `["auth","jwt"]` on the other — and, via
  `termFrequencies`, a genuine BM25 ranking difference. `reconcile()` now builds on *ours*, so the
  two agree. Values are unaffected; only which side's spelling survives, and it is now the same
  side in both.
- **Migration:** nothing on disk changes. A sidecar already auto-resolved keeps whatever it has;
  this only affects conflicts resolved from now on.

### Changed (breaking) — a topic segment that aliases another scope is now refused

- **A memory path whose topic segment ends in `.` or a space, is a bare `.`, or is `NUL` is now
  rejected instead of stored.** Every scope string is built by one constructor
  (`MemoryNaming.BuildScopedTopicScope`), which sanitizes each segment and refuses what substitution
  cannot repair. Previously the topic was interpolated from the raw path segments while only the
  leaf was sanitized, so `/notes./x`, `/notes /x` and `/notes/x` produced three *different* scope
  strings — three index-cache keys and three write locks — over **one** directory, because the
  filesystem trims a trailing dot or space from a path component. A write through any one of them
  left the others' cached listing stale, and `SaveIndex`'s orphan sweep then deleted every sidecar
  absent from that stale list. A bare `.` aliases the parent scope the same way. `NUL` is refused
  because `Directory.CreateDirectory` cannot create it; `CON`/`PRN`/`AUX`/`COM1-9`/`LPT1-9` are
  **not** refused — measured on Windows, those create real, writable directories and alias nothing.
- **Which direction each caller takes.** Write paths — storing, appending, forgetting, copying,
   bundle import/export — throw `ArgumentException`. Read paths — the `scopes` filter,
   `excludeTopics` — treat an unbuildable topic as matching
  nothing rather than refusing, because a scope that cannot be constructed cannot name a stored
   memory either. Scope *recovery* (reading back a directory name the filesystem itself produced) is
   deliberately exempt and unchanged: validating there would make
  an already-stored memory in such a scope undeletable.
- **The repaired direction, which is not a refusal.** `*`, `?`, `<`, `>`, `|` and `"` in a topic
  segment are now substituted with `_`, exactly as the leaf has always treated them — so `/a*b/x`
  stores under `local:a_b` and succeeds, where it previously carried the raw `*` down to
  `Directory.CreateDirectory` and failed there with an I/O error on Windows.
- **Migration, precisely.** A memory already stored under an aliasing scope still appears in `list()`
  and in search results, and is still deletable via `forget()` with its `file://` URI — both go
  through scope *recovery*, which reads the directory name off disk and is exempt. What is refused is
  addressing it **by path**: `recall('/notes./x')`, `forget('/notes./x')` and a re-`remember` under
  that path now fail with the refusal above. Recover such a memory by copying it to a valid path.
  Reaching this state at all requires a filesystem that permits a trailing dot or space in a
  directory name; Windows trims them, so no memory written there is affected.

### Changed (breaking) — `reconcile()` refuses to auto-merge dissimilar sidecars

- `reconcile()`'s automatic `.meta.json` merge now runs the same admission check the git merge
  driver has always applied. The rule is `Scrinia.Core.Merge.SidecarAdmission`, which `reconcile()`
  calls; `MetaJsonMerger` cannot call it — the merge driver is a standalone AOT executable with
  zero project references by hard constraint — so it states the same predicate inline, with
  `SidecarAdmissionParityTests` running both. It refuses to auto-merge two conflicted sidecars
  whose keyword sets score below a Jaccard similarity threshold. Default `0.7`; configurable as `jaccardThreshold` in
  `.scrinia/merge.config`. A conflict that resolved automatically before this change may now
  require an explicit `choice`.
- The refusal is reported with its own error naming the threshold (*"cannot be auto-merged: the
  two sides' keywords are too dissimilar (below the configured similarity threshold of 0.7)"*),
  not the generic `"'choice' is required"` — the gate is a deliberate decision, not a parse
  failure, and the message says so instead of reading like one.

### Changed (breaking) — the git merge driver conflicts on a non-string timestamp

- `MetaJsonMerger` now escalates to a conflict when a sidecar's `createdAt`/`updatedAt` is present
  but not a JSON string — a number, bool, object, array, or an explicit JSON `null` — where it
  previously read the malformed value as absent and auto-resolved on the other side.
  `TryGetTimestampString`'s "check presence before inspecting the value" shape is what makes an
  explicit `null` distinguishable from "missing"; a plain indexer read cannot tell them apart.
  Requires a hand-edited or foreign-tool sidecar to reach.

### Changed (breaking) — `reconcile()` and the git merge driver silently absorbed a malformed `keywords` array

- Both mergers now escalate to a conflict — `reconcile()`'s auto-resolver reports "auto-resolve
  failed", the git merge driver returns `MergeResult.Conflict` — when a sidecar's `keywords` (or,
  independently, `agentKeywords`) is **present but not a well-formed string array**: an explicit
  JSON `null`, a non-array value (a string, a number, an object), or an array holding a non-string
  element. Previously both read any of these shapes exactly as they read a genuinely *absent*
  field and silently auto-resolved on the untouched side — treating a foreign writer's malformed
  metadata as "nothing to reconcile" rather than the conflict it actually is. The admission gate
  (the Jaccard-similarity check above) escalates on the same malformed shapes for the same reason:
  a similarity score computed against a `null`/malformed keyword set is not a real answer to "are
  these two sides similar enough?", so it is reported as an auto-resolve failure, never as "sides
  too dissimilar."
- A non-string array element (e.g. `["auth", 5]`) previously diverged between the two mergers by
  accident rather than by design: Core's reader threw on it, which only ever escalated because the
  exception happened to propagate into an existing broad `catch` in both of its callers; the git
  merge driver's reader silently dropped the bad element and merged the rest. Both readers now
  refuse explicitly — neither relies on an incidental exception to do the right thing.

### Fixed — merges lost term frequencies and could move a memory's `createdAt` forward

- Term frequencies now merge on both routes, and both lost them differently before. `reconcile()`'s
  auto-resolver required BOTH sides to carry a `termFrequencies` map, so a memory whose base
  recorded none and whose other side did lost that side's map whole. The git merge driver never
  merged the field at all: it preserved OURS verbatim as one of the "unknown properties" it carries
  across untouched, which silently discarded theirs' contribution on every merge. Both now union
  whichever side(s) actually carry one, taking the max on a shared term, and both escalate to a
  conflict rather than guessing when either side's map is malformed (present but not an object, or
  holding a value that isn't an int). Term frequencies are what BM25 scores against, so this was a
  silent ranking loss on every affected merge.
- Neither merger now moves a memory's `createdAt` forward, and both agree with each other on the
  result. This closes two independent bugs, not one: the git merge driver compared `createdAt` as
  ISO **strings** with ordinal comparison, which is wrong across UTC offsets (`10:00+10:00` and
  `00:00Z` are the same instant and do not compare equal as strings); `reconcile()`'s auto-resolver
  did not consider `createdAt` at all — it took whichever whole side won on `updatedAt`, so an
  automatic reconcile could silently move a memory's recorded creation time forward. Both mergers
  now select the pair once, through one rule: earliest `createdAt`, latest `updatedAt`, each
  parsed as a `DateTimeOffset` and compared as instants.

### Fixed — an append's drift baseline missed a file its new chunk referenced for the first time

- An append now records a drift baseline for a file its new chunk references for the first time.
  Previously the path reached the searchable `file` attribute through attribute extraction while
  drift detection had nothing to compare it against, so it was reported clean forever. It runs
  through the shared `MemoryDerivation` pipeline.
- A file already in the baseline keeps its ORIGINAL hash across appends — only newly-referenced
  paths are hashed. Re-hashing an already-baselined path on every append would re-baseline it to
  its current content each time, so it could never be reported as drifted; that is the signal the
  baseline exists to provide.

### Fixed — ephemeral memories now carry a drift baseline

- Ephemeral memories now carry a drift baseline (`EphemeralEntry.FileHashes`, persisted as
  `codeRefs`), surfaced in write responses and in `list()`.
- `compact()` now works on ephemeral memories instead of reporting them as not found. It never
  checked whether the target was ephemeral and went straight to the disk-only artifact read, so
  compacting an existing multi-chunk ephemeral memory failed even though `list()` showed it and
  `append()` worked on it without issue.

### Fixed — a path after a colon extracted corrupted, and inline prose `key:value` routed unbounded keys

- A file path written immediately after a colon (`file:src/x.cs`, `ref:docs/a.md`) no longer
  extracts a silently corrupted path. The extraction pattern's lookbehind excluded `:` itself, so
  a match starting there failed and restarted one character later, mid-token —
  `file:src/x.cs` produced `rc/x.cs`, which was indexed, seeded into search, and reported as a
  clean drift baseline for a file that does not exist.
- Multi-level relative paths (`../../src/a.cs`, `..\..\src\a.cs`) now extract at all. The prefix
  group previously restored at most one `../`/`..\` level; a deeper chain matched nothing and the
  reference was silently dropped rather than hashed or made searchable.
- Inline prose `key:value` — a pair appearing mid-sentence, not at line start or inside a
  `**Bold:**` label — now routes to attributes only for the eight recommended keys: `person`,
  `place`, `org`, `thing`, `topic`, `file`, `ref`, `review`. Any other key found this way stays
  plain text: prose is uncontrolled (pasted logs, quoted code, transcripts), so only the keys the
  guide teaches are allowed to mint an attribute from it. The `keywords` argument still routes any
  `key:value` entry — every element there was typed on purpose — and line-start `key: value` and
  `**Key:** value` lines still route any key, not just the recommended eight.

### Fixed — an inline prose value naming nothing, an unaddressable drift row, a half-written link

- An inline prose `key:value` whose value contains no alphanumeric character (`file:-`, `(ref:**)`,
  `file:...`) no longer mints an attribute. `file` and `ref` are graph-forming keys, so a value that
  names nothing became one node shared by every memory whose prose happened to contain the shape —
  and uncontrolled prose (pasted logs, diffs, ASCII tables) is full of those shapes.
- An inline prose value now stores the same spelling the bare extractors produce for the same
  reference: `ref:[[/api/auth]]` stores `/api/auth`, and `file:"src/a.cs"` stores `src/a.cs`. Both
  wrappers previously survived, which split one reference into two values under one key — and the
  quoted form additionally failed the workspace-containment check, so that reference silently got
  no drift baseline at all.
- `list({ mode: "drift" })` now names an ephemeral memory `~name` rather than bare. The bare name
  was unreachable until ephemerals gained a drift baseline in this release; a bare row either misses
  on `recall()` or resolves to an unrelated persistent memory of the same name.
- `link()` now refuses before writing anything when either side **does not exist**. It ran both
  updates unconditionally, so `link('/findings/absent', '/patterns/retry')` failed on the first,
  wrote `/patterns/retry` anyway, and reported a partial failure — leaving a `ref` pointing at a
  memory that carries no `ref` back, which is worse than either a clean link or a clean refusal.
  Both sides are resolved before either is written, through the same store members the write itself
  uses. The response for a missing side is now a `NOT_FOUND` naming it, not `INTERNAL`.

### Changed — `link()` works on ephemeral memories instead of refusing them

- **`link()` now accepts an ephemeral (`~name`) memory on either side.** `UpdateMeta`, which both
   halves of a link go through, refused a `~name` outright, so linking one was impossible; it now
   loads the ephemeral, applies the same metadata mutation the persistent path applies, and writes it
   back. An ephemeral memory lives in the calling process. **This replaces an earlier entry in this
   this `0.6.0` release** which said `link()` refuses before writing anything when either side is an
  ephemeral.
  - **Not reachable from the CLI**, and cannot be: an ephemeral memory lives only in the memory of
    the process that created it, and every `scri` invocation is a fresh process. `scri link ~x /y`
    answers "not found". The MCP tool supports it because `scri serve` is long-lived.
- `IMemoryStore` gains `TryUpdateEphemeral(key, mutate)` — the ephemeral counterpart of
   `TryUpdateEntry`, and the only place the missing atomicity could live. It uses a compare-and-swap
   over the in-process map, so a metadata update that loses a race is re-applied to the newer entry
   rather than overwriting it and invalidates the merged entity-fact cache, as every other mutating
   write path does. A `GetEphemeral` → mutate →
  `RememberEphemeral` caller would silently drop a concurrent writer's change — and for `link` the
  dropped value is a `ref`, so a lost update manufactures exactly the dangling one-sided reference
  the precheck above exists to prevent. **Interface addition:** an external `IMemoryStore`
  implementation inherits an unsynchronized default and keeps compiling, but does not get the
  atomicity until it overrides.

### Fixed — tool descriptions now describe what the tools do

- `remember`'s and `append`'s descriptions never documented that `key:value`-shaped keywords route
  to attributes, or that the same eight recommended keys route from inline content prose. The
  guidance that did exist sat on an internal, non-MCP helper whose `[Description]` attributes are
  inert, because no MCP caller has ever read them.
- `ref`'s description no longer claims it is hashed for drift detection — only `file` is; `ref` is
  a link to another memory.
- `link`'s `reason` parameter description no longer reads as though the reason were persisted — it
  is only echoed into that one call's response text, never written to either memory's sidecar.
- `remember`'s and `append`'s content descriptions no longer claim that NONE of the automatic
  attribute extraction happens inside a fenced code block. **This bullet originally said** three
  routes skipped fences (line-start `key: value`, `**Bold:**` labels, inline prose `key:value`) and
  one skipped inline `code` spans and frontmatter as well (proper-noun `mention` spans), while bare
  file paths and `[[wiki-links]]` were scanned over the whole content, fences included. That split
  no longer exists: see "file and memory references no longer extract from pasted code" above,
  which landed later in this release and made bare file paths and `[[wiki-links]]`
  fence-aware too. All extraction routes now follow the one `ProseScanner` rule — skip a properly
  closed fence, read YAML frontmatter and inline `code` spans as ordinary text — and the descriptions
  were rewritten again to state that once instead of route-by-route.
- `compact`'s description no longer claims unconditionally that it archives the original. An
  ephemeral memory has no `versions/` directory, so compacting one is irreversible — the common,
  user-reachable case — and, rarely, a persistent artifact with no prior index entry has no
  previous version either; the response already reported this correctly and the description now
  agrees with it.

### Changed (breaking) — BM25 ranking uses one attribute-seeding policy

- `SeedTermFrequencies` now runs exactly once, as the last step of attribute handling, over the
  complete attribute map. **This is a deliberate, user-visible ranking change: `tag`, `review` and
  `file` attribute values now seed term frequencies at +3 (previously 0), and `anchor` at +3
  (previously +2)** — one weight for every attribute value, applied the next time a memory is
  written or rewritten.
- **A ruling, not just a consequence of unifying the pipelines:** attribute values added through
  `link()`'s metadata-only write previously seeded at +5 — the same weight as a curated keyword — so an
  attribute added after the fact outranked the same term if it had arrived through `remember()`
  (seeded at +3). They now seed at the same +3 every write path uses. There is no longer a
  preferential lane for a metadata-only update.
- An auto-extracted keyword an `append()` carries forward from a memory's existing content used to
  be reclassified as agent-supplied and reboosted to +5 on every append — compounding, so a term
  that was never curated could out-rank a genuinely curated one after a few appends. It now keeps
  its original auto weight (+2) unless it is genuinely agent-supplied. Nothing is dropped: the
  carried-forward list still leads the keyword merge exactly as before (that guarantee is
  unaffected) — only the *weight* changes.
- Review dates now persist as UTC on every write path through one shared parse
  (`MemoryDerivation.TryParseReviewDate`, anchored to UTC), so a bare `review` date is read the
  same way regardless of where or how it was written.

### Changed — compaction now derives its metadata

- `compact()` used to derive nothing beyond whatever the memory already carried: it hand-assembled
  the compacted entry from the surviving chunks and the existing attributes/keywords, with no pass
  through the extraction/keyword/term-frequency pipeline. It now performs a real store rewrite over
  the compacted chunks, so a compacted memory can gain keywords, term frequencies and chunk entries
  it did not have — and, if its surviving text happens to be count-shaped, a
  `review: "when counts in this memory change"` attribute it did not have either. The memory's
  curated keywords, attributes and description are not lost in the process: they travel forward as
  the caller's own input to the rewrite (read from `ArtifactEntry.AgentKeywords` when the entry
  knows its own provenance; a documented heuristic over the pre-compact chunks for an entry that
  predates that field).

### Fixed — a re-store's rollback could destroy the memory it was overwriting

- Re-storing an existing memory whose sidecar write then failed (a transient I/O error, a locked
  file) used to delete the artifact unconditionally as part of the rollback — so the failure
  destroyed the memory's only live copy on disk while the index entry, never updated because the
  failure happened before it could be, still pointed at a file that no longer existed. Nothing reads
  `versions/` back on that path, so there was no way to recover the memory afterward. The rollback
  now captures the previous artifact before the overwrite and restores it on failure; only a
  brand-new memory's now-orphaned artifact is deleted, matching what `AppendAsync`'s rollback
  already did.

### Changed — stale `facts.json` sidecars rebuild automatically

- `EntityFactSidecar.Version` is `3`. A `facts.json` written under an earlier version is rejected
  and rebuilt the next time it is read. This is expected churn from the derivation collapse, not
  corruption — no action is needed.

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
- **Also fixed:** `scri store` had no error guard at all and returned `0` unconditionally, so a
  genuinely failed store reported success. This is the same dead-guard defect previously fixed at
  `scri show`, `scri forget`, `scri bundle export` and `scri bundle import`; `store` was missed.
- Anything gating on exit `0` from these paths will now see a failure where it previously saw
  success. That is the point of the change, but it is breaking.

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
   index lookup now runs *before* the artifact read in the MCP tool. Previously the read ran first
   on the caller's casing; on a case-sensitive
  filesystem it missed, and the create-new fallback underneath it — whose own lookup *is*
  case-insensitive — then landed on the existing memory and replaced its content with the appended
  chunk alone. `append()` behaving as `replace()`. NTFS folds case in `File.Exists`, so this was
  never reachable on Windows; both sites carry a ruling comment saying so, and the ordering itself
  is pinned by a test at each.

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

### Changed — embedding provider error truncation now uses a shared helper

- Failed single-request and batch embedding calls now pass their provider error bodies through
  the shared `HttpErrorBody.Truncate` helper. Bodies over 500 characters retain their first 500
  characters followed by `...`; shorter bodies remain unchanged, preserving bounded warning logs
  while keeping both embedding-provider failure paths consistent.

### Fixed — event-sink lifecycle

- Detached event-sink work is now tracked by `BackgroundWork` instead of being dropped on the
  floor by a bare `_ = Task.Run(...)`. Writes still return without waiting for embedding
  latency, but the work is owned: the CLI drains outstanding sinks before exiting, so a
  short-lived command no longer ends with a vector file half-written. If the drain exceeds 10s
  the work is cancelled and a warning names `scri reindex` as the recovery.
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
- **`IProcessRunner` abstraction** under `Scrinia.Core.Process` — testable
  one-shot process invocation with Windows PATHEXT resolution (`.cmd` shims
  for Node-based CLIs don't resolve through `Process.Start` with
  `UseShellExecute=false`).
- New config keys: `Scrinia:Hint:Enabled`, `Scrinia:Hint:MinScore`,
  `Scrinia:Hint:MinPromptChars`.

### Added — Ollama auto-detection
- `scri setup` probes for a running Ollama instance and, when reachable, prompts for an embedding
  model and pulls missing models with per-layer progress.
- Skip the Ollama prompt with `scri setup --no-ollama`.

### Added — Chunked embeddings + reindex
- `TextChunker.SliceWindows` slices every memory's decoded content into
  overlapping 1200/200-char windows. Each window becomes one vector. Search-time
  dedup in `WeightedFieldScorer.SearchAll` collapses chunk matches back to one
  result per memory but lets the best-matching window drive the score. A needle
  buried mid-memory is now vector-reachable; the prior single-vector-per-memory
  layout silently lost it past whatever context window the provider supported.
  **Superseded — see the unreleased "one embed-and-index policy" entry.** The
  per-window key described here is the layout that entry identifies as a bug:
  a raw window ordinal was written into the position the search scorer reads a
  1-based content-element index from, so the chunk-level probe could never hit.
  Content elements are now the addressable unit and windows nest inside them.
- SVF3 signed vector file format: header carries the active embedding
  signature `{provider}|c{chunkSize}o{overlap}` (e.g.
  `ollama:nomic-embed-text|c1200o200`). **The format gained a layout-revision
  suffix in the unreleased entry above** — it is now
  `{provider}|c{chunkSize}o{overlap}|e{layoutRevision}`, which is what
  quarantines vectors written under the old per-window key. On startup, vector files whose
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
  **The default is now 500 and the cap has a ceiling — see the unreleased entry
  above.** Under the element/window layout the cap also bounds how many content
  elements can carry a vector, which 100 was too low for.
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
  exposes `IDisposable`. **Moot — `HnswIndex` was deleted unreleased** (see "three
  unreachable features" above); nothing outside its own tests ever referenced it.
- BM25 corpus-stats dictionary is pre-sized via a `docCountHint` parameter,
  removing rehash churn during search corpus computation.
- `IOptions<EmbeddingOptions>` migration with `PostConfigure` for historical
  normalisation; missing required configuration fails fast.
- New documentation: `docs/plugin-authoring.md` (extension interfaces + worked
  example), `docs/security.md` (threat model + recommendations), embedding-provider
  selection section in `docs/architecture/embeddings.md`, `scri migrate` reference
  in `docs/cli-reference.md`, chunked-embeddings + Tier 2 LLM coverage in
  `docs/cli-reference.md` and `docs/architecture/embeddings.md`.

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
- HybridReranker max-aggregates per-chunk vector scores under the
  `{scope}|{name}` key the whole-memory scorer pass looks up. The chunked
  `{scope}|{name}|{chunkIndex}` keys are still emitted alongside.
  **Superseded — see the unreleased "one embed-and-index policy" entry.** That
  per-chunk path is no longer hypothetical: the scorer's chunk pass consumes
  those keys today, and `chunkIndex` now means a 1-based content-element index
  rather than the window ordinal this entry emitted.

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

### Fixed — bundle import preserves every entry member and its timestamps

- **Bundle import no longer drops entry metadata.** Export serialized every `ArtifactEntry`
  member through a source-generated context while import re-read seven of them by hand, so a
  backup restore silently lost `Keywords`, `TermFrequencies`, `UpdatedAt`, `FileHashes` and
  `AgentKeywords`. `AgentKeywords` was the unrecoverable one — the curated-vs-auto keyword
  distinction cannot be reconstructed after the fact. Import now deserializes through the same
  context that serializes, so every member the bundle recorded comes back — except `Uri`, which
  the store recomputes for the importing machine because the exporter's value would be wrong
  there. This is not a promise to tolerate arbitrary malformed bundle JSON, see below.
- **Restoring a backup no longer resets every memory's creation date.** `CreatedAt` was parsed
  from the bundle and then overwritten with the import time. Both `CreatedAt` and `UpdatedAt`
  are now preserved; a bundle carrying no timestamp still gets one at import.

### Fixed — memories bundled from loose files are searchable on import

- `scri bundle` packages files that were never memories, so their entries carry no keywords and
  import derived none. Import now derives keywords and term frequencies for entries that have
  none — and only for those, leaving an entry that was already derived exactly as the bundle
  recorded it.

### Fixed — a bundle with an unreadable topic index is refused before anything is written

- Importing a multi-topic bundle where a later topic's `index.json` fails to parse used to write
  every topic before it and only then report the bundle as invalid, leaving the store
  half-imported. Every topic is now parsed and validated before any of them are written, so a
  malformed topic is refused — naming the topic, in the message — with nothing from that bundle
  written to the store.
- An `index.json` that parses as syntactically valid JSON but is not an object at its root
  (`[]`, `42`, `"x"`, `null`) used to be silently skipped, dropping that whole topic while every
  other topic in the bundle still imported — a silent partial restore, and a regression from the
  pre-branch behavior, which threw on exactly this shape. It is now refused the same way any
  other unreadable index is, naming the topic, before anything is written. An artifact's content
  that fails to decode is a narrower case and is unaffected by either fix: it degrades that one
  entry to "no derived keywords" rather than aborting the import.

### Changed (breaking) — a memory restored from a bundle now carries the metadata the bundle recorded

- A memory restored from a bundle now carries the metadata the bundle recorded rather than a
  partially-reconstructed copy. Anything depending on restored memories having a fresh
  `CreatedAt`, or on their keyword lists being empty, will observe the difference. No API
  signature changed; no caller needs recompiling.

## [0.5.0] — Initial public baseline

First release with the canonical two-tool MCP surface (`guide`, `memory`) after the
scope reduction that removed the goal/task/concern/plan vocabulary. The runtime is
considered stable from this point — future minor releases will not break the MCP
tool contract without a major-version bump.

[Unreleased]: https://github.com/nickd-scrinia/scrinia/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/nickd-scrinia/scrinia/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/nickd-scrinia/scrinia/releases/tag/v0.5.0
