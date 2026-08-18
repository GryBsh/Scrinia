# Published Surface Coherence — Design

**Date:** 2026-08-18
**Branch:** 6 of 7 from the 2026-08-16 design review
**Findings:** M1 (MCP tool family disagrees with itself), M3 (plugin contract cannot be versioned independently), M5 (one authorization policy, two sources of truth)
**Base:** `main` at `41ba101`

## The root shape

All three findings are the same failure wearing three costumes: **a rule that is stated once in prose and enforced by hand at N call sites, where nothing makes N+1 automatic.**

- M5: one route→permission policy, hand-copied into 19 handler guards and re-stated as a switch on the MCP side.
- M1: one address-canonicalization rule, hand-placed at 19 call sites across four agent-facing surfaces; one implicit family contract, hand-honoured (and hand-violated) per tool.
- M3: one plugin contract, stated by an assembly whose real surface is everything transitively reachable through `IMemoryStore`.

The fix in each case is to make the rule intrinsic — carried by a type, by metadata, or by a compile-time boundary — so that a new call site cannot silently omit it. Where that is not achievable, the rule is pinned by a table-driven test that enumerates from the authoritative list (the route table, the attribute set) rather than from an existing guard's call sites.

## Corrections to the review's findings

Verified against `main` at `41ba101`. Three of the review's claims have moved and the plan must not inherit them:

1. **M5's count is 19, not 28.** `HasPermission` appears 13× in `MemoryEndpoints.cs`, 3× in `EmbeddingsEndpoints.cs`, 2× in `ChatEndpoints.cs`, 1× in `KeyEndpoints.cs` (plus its definition in `RequestContext.cs`).

2. **No route is currently unguarded.** All 27 mapped routes were traced. `MemoryEndpoints`' 13 guards cover its 14 routes because GET and POST `/search` share `RunSearch`. `EmbeddingsEndpoints` is 3/3, `ChatEndpoints` 2/2, `KeyEndpoints` is covered by the `ManageKeys` policy plus a per-key check, `HealthEndpoints` by the `Health` policy. The review's claim that "H6's plugin group already is [an open endpoint]" was true at review time and is no longer — branch 4 closed it. **M5 is therefore a drift finding, not a live hole.** This lowers urgency but not value, and it means the change can be made cleanly rather than under pressure.

3. **The plugin contract is narrower than feared.** The only real plugin (`Scrinia.Plugin.Embeddings`) calls exactly two members on the store it is handed: `ParseQualifiedName` and `ListScoped`. It does not use the hook contexts at all — it implements `IMemoryEventSink`, which lives in `Scrinia.Core`, not in `Scrinia.Plugin.Abstractions`. The assembly whose entire premise is third-party compilation is not the contract the only real plugin binds to.

M1's six sub-claims were each re-verified and all still hold; see Part B.

## Global decisions

**Breaking changes are permitted and recorded.** The product is at `0.5.0`, pre-1.0. Schema and envelope shapes are fixed to what they should be, with no compatibility shims and no dual-accept paths. Every break gets a CHANGELOG entry. Rationale: a shim is carried code plus a later removal, and this branch exists to reduce the number of places a rule lives, not increase it.

**Ordering is A → B → C.** Part B introduces `MemoryAddress` into `Scrinia.Core`; Part C moves types *out* of `Scrinia.Core`. Running C first would mean relocating a type that B is still reshaping. The two are otherwise decoupled: `IMemoryEventSink`'s methods keep `string qualifiedName`, because an address is a surface concern and not a plugin concern.

**Enumerate from the authoritative list, never from an existing guard's call sites.** Recorded as a trap in `/patterns/cli-bypasses-mcp-facade`: scoping a guard by "where the existing check is already called" silently omitted two write routes and produced a Critical. Every completeness check in this branch enumerates from the route table, the `[McpServerTool]` attribute set, or the `[Argument]`-taking command set.

---

## Part A — Server authorization (M5)

### A1. One vocabulary

A `Permissions` static class replaces the ten bare string literals now in use (`append`, `chat`, `copy`, `export`, `forget`, `import`, `read`, `reindex`, `search`, `store`). Both surfaces reference the constants, so a rename is a compile error rather than a silent divergence between `MemoryEndpoints` and `McpPermissionFilter`.

### A2. One declaration

Each route declares its requirement as endpoint metadata rather than opening its handler with a guard:

```csharp
group.MapPost("/memories", RememberMemory).RequirePermission(Permissions.Store);
group.MapPost("/", ChatHandler).RequirePermission(Permissions.Chat, Permissions.Read, Permissions.Search);
```

`RequirePermission` is `params string[]` because the chat route genuinely requires three. Semantics are conjunctive (all listed permissions required), matching the existing `||`-chained guard it replaces.

### A3. One enforcement point

A single `IEndpointFilter` reads the metadata and returns the same 403 `ErrorResponse` body the handlers return today, so the wire contract is unchanged. All 19 `if (!ctx.HasPermission(...))` blocks are deleted.

The read-only-store boundary currently checked in `McpPermissionFilter` after the permission check keeps that ordering: permission first, then store writability, so a caller lacking the permission still sees that error first.

### A4. Fail closed at startup

A startup validator walks `EndpointDataSource` after the app is built. Any endpoint carrying `RequireAuthorization` but neither permission metadata nor an explicit `PublicEndpoint()` marker throws, and the server refuses to boot. Health's anonymous routes carry the explicit marker.

This is the piece that turns M5 from "correct today" into "cannot regress". It is also the piece with the highest blast radius in this branch — a mistake here is a server that will not start. It therefore gets the test in A6 that asserts both directions: a declared route boots, an undeclared one throws.

### A5. Close the MCP fallback

`McpPermissionFilter.RequiredPermission`'s `_ => null` arm currently means an unrecognised tool name is **ungated**. That is the same hole as a missing REST guard, in a different shape. It becomes fail-closed: an unknown `tools/call` target is denied.

The two mappings stay separate. They are different keyspaces — five REST permissions (`copy`, `export`, `import`, `reindex`, `chat`) have no MCP analogue — and forcing them into one table would be a false consolidation. They share the vocabulary from A1, which is where the drift risk actually lives.

### A6. Tests

- Table-driven over the route table: every route answers 403 when the caller lacks its declared permission.
- The startup validator rejects an endpoint declaring neither a permission nor `PublicEndpoint()`, and accepts one that declares either.
- Reflection over `[McpServerTool]`: every tool name resolves to an entry in `RequiredPermission`, so a new tool cannot land ungated.

---

## Part B — Tool family coherence (M1)

### B1. `MemoryAddress`

A readonly struct that canonicalizes the ephemeral alias on construction. The internal `MemoryTools.Core` methods take `MemoryAddress` instead of `string`.

This is what makes the rule intrinsic. Today the rule has one definition (`MemoryNaming.CanonicalizeEphemeralAlias`) and 19 hand-placed call sites across four agent-facing surfaces — the MCP facade, the CLI commands, the MCP resource (`ScriniaMcpResources.cs`), and the chat agent loop's `recall_memory` tool. Three sites were missed on branch 2's first pass and were caught only by a whole-branch review. With the internal methods typed, a fifth surface added later **cannot compile** without converting, and conversion canonicalizes.

The 19 calls collapse into boundary conversions.

Note the recorded hazard: tests that drive the internal methods directly (e.g. `ScriniaMcpToolsTests.cs:1683`) will need updating for a correct change. That is expected, not a regression signal.

### B2. The six contradictions

| # | Contradiction | Resolution |
|---|---|---|
| 1 | `remember` on `/skill/` routes to `SkillCreate` and drops `attributes` and `keywords` silently, after ~2 KB of schema prose describing them | Return success **with a warning** naming the ignored arguments. Skills are markdown files with nowhere to put facets; the honest fix is saying so, not inventing storage. |
| 2 | `remember` and `recall` route `/skill/`; `forget` does not, so it answers `NOT_FOUND` for a skill `recall` just returned | Implement `SkillDelete` and route `forget` through the same dispatcher. A built-in name with no disk override returns a specific "built-in skill, cannot be forgotten" error, not `NOT_FOUND`. |
| 3 | `reconcile({choice})` without `conflictId` silently performs a read-only scan and reports `status: success, action: reconciled` | Error when `choice` is supplied without `conflictId`. |
| 4 | `list`'s `mode` is checked against two literals; anything else, including a misspelling of `full`, falls through to summary as success. `facts` errors explicitly, so the family contradicts itself | Unknown `mode` errors, matching `facts`. |
| 5 | `guide` and `restore` return the same payload in swapped envelope fields: `guide` concatenates `ctx.ContentSections` into `info:`, `restore` returns them as the `content:` body | One shared builder emits the restore context into identical fields from both tools. `guide` keeps the guide document in `content:`; `restore` gets a summary line there. **Breaking for `restore` callers.** |
| 6 | `search` takes `attributeFilter` as a map but `scopes` and `excludeTopics` as comma-strings, so the schema cannot express a scope name containing a comma | `scopes` and `excludeTopics` become arrays. **Breaking.** |

### B3. Tests

Reflection over all four agent-facing surfaces — `[McpServerTool]` methods, `[Argument]`-taking CLI commands, the MCP resource template, and the agent-loop tool — asserting every address-taking parameter round-trips `/temp/probe` identically. This is the net recorded in `/patterns/cli-bypasses-mcp-facade` as the cheap interim that would have caught all three of branch 2's misses; it is kept even though B1 makes the rule type-enforced, because it also covers surfaces that take an address without reaching the typed internal methods.

Per-contradiction tests for each row in B2.

---

## Part C — Plugin contract inversion (M3)

> **SPLIT OUT OF THIS BRANCH, 2026-08-18. The design below is superseded — do not implement it.**
> **Superseded by `docs/superpowers/specs/2026-08-18-plugin-contract-narrowing-design.md` (2026-08-18).**
> That spec establishes there is no external plugin audience, so the assembly move, the
> `ProjectReference` reversal and the `AssemblyVersion` pin are all withdrawn. What survives —
> narrowing the sink contract — is implemented there without moving any type between assemblies.
>
> A pre-implementation falsification pass found Part C structurally blocked, and found that two of the measurements this section rests on were wrong. Both errors share one cause: a `grep` whose pattern could not express the usage shape that actually exists, returning empty, with the emptiness reported as a measurement.
>
> - **"No hook implementation reads `ctx.Store` at all" is false.** Nine sites pass it as an *argument* into parameters typed `IMemoryStore` (`EmbeddingsPlugin.cs:274,279,284`; `EventSinkOperationHook.cs:62,71,80`; `BuiltInEmbeddingsService.cs:92,95,103`). The pattern used matched member *access* only.
> - **"The only real plugin uses exactly `ParseQualifiedName` and `ListScoped`" is false.** Both plugin-shaped sinks route append through `MemoryArtifactSource.ReadForAppendAsync` (`:93-101`), needing `ParseQualifiedName`, `IsEphemeral`, `ResolveArtifactAsync` and `ReadArtifactAsync`. `ListScoped` is not used by the sink contract at all — only by `ReindexStoreAsync`, which is `IEmbeddingsAdmin`.
> - **There are seven `IMemoryEventSink` implementations, not six.** `CoreEmbeddingEventHandler` (`src/Scrinia/Services/CoreEmbeddingEventHandler.cs:17`) was missed, and needs the same four members.
> - **The reversal pulls ASP.NET Core into the trimmed CLI publish.** Abstractions carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />` because `IScriniaPlugin` takes `IApplicationBuilder` and `IEndpointRouteBuilder`. `FrameworkReference` is transitive, and `src/Scrinia` publishes `--self-contained -p:PublishTrimmed=true` with `IsTrimmable`. This likely forces splitting Abstractions into an ASP.NET-free contract assembly and a hosting assembly — a design question, not a plan correction.
>
> What survives verification: **the build-graph flip itself is clean.** No cycle is possible, the three DTOs are self-contained, the move is serialization-inert (no `[JsonSerializable]`, no `Type.GetType` naming them), and `_legacyFields` touches no `InternalsVisibleTo`. The blocker is the *contract*, not the graph.
>
> Consequence for the narrow-contract argument: a five-member `IPluginStore` still pins something far smaller than `IMemoryStore`'s twenty-two, so pinning the version remains defensible — but the "two members" framing that justified it here does not survive.
>
> Full detail is in `/findings/plugin-contract-inversion-blockers`. M3 gets its own brainstorm → spec → plan cycle.

### C1. Two sink interfaces, one meaning each

`IMemoryEventSink` cannot move to Abstractions while its methods take Core's `IMemoryStore` — that is a circular reference. The store parameter must be an Abstractions type, and the measured evidence says one type cannot serve both audiences.

What each sink implementation actually uses from the store it is handed:

| Sink | Members used |
|---|---|
| `EmbeddingsPlugin` (the only third-party-shaped one) | `ParseQualifiedName`, `ListScoped` |
| `BuiltInEmbeddingsService` | `ParseQualifiedName`, `ListScoped` |
| `EmbeddingReindexer` | `FormatQualifiedName`, `ListScoped` |
| `McpPluginHost` | `ParseQualifiedName`, `ReadArtifactAsync` |
| `MaintenanceEventSink` | `AnyScoped`, `FormatQualifiedName`, `LoadIndex`, `ParseQualifiedName`, `SanitizeName`, `TryUpdateEntry` |
| `EntityTypingEventHandler` | `GetEntityFactStore`, `LoadIndex`, `ParseQualifiedName`, `SanitizeName`, `TryUpdateEntry` |

The union is nine members, one of which (`GetEntityFactStore`) returns a Core-internal type that would drag further types across the boundary. A single interface wide enough to avoid casts is therefore not a narrow contract, which is the problem M3 describes rather than a fix for it.

So the boundary splits in two, because there genuinely are two:

- **`IMemoryEventSink`** (Abstractions) takes `IPluginStore`. This is what third parties implement.
- **`ICoreEventSink`** (Core) takes the full `IMemoryStore`. This is what `MaintenanceEventSink`, `EntityTypingEventHandler`, `EmbeddingReindexer` and `McpPluginHost` implement.

This is not duplication. "What a plugin implements" and "what Core's internal handlers implement" are different contracts with different audiences and different stability guarantees; collapsing them is what produced the finding.

### C2. Move the contract to the bottom

`ArtifactEntry`, `ChunkEntry`, `ScopedArtifact`, `IMemoryEventSink`, the hook contexts, and a new narrow `IPluginStore` move into `Scrinia.Plugin.Abstractions`. `Scrinia.Core` references `Scrinia.Plugin.Abstractions`; the existing `ProjectReference` reverses.

`IPluginStore` carries exactly the two members the plugin-shaped sinks use — `ParseQualifiedName` and `ListScoped` — and grows only on evidence of a real consumer. `IMemoryStore : IPluginStore`, so the concrete store satisfies both interfaces and can be handed to either sink kind without a cast.

The six hook contexts take `IPluginStore` instead of `IMemoryStore`. Measured: **no hook implementation reads `ctx.Store` at all** — `MemoryVectorIndexer`, `EventSinkOperationHook`, `PluginPipeline` and `EmbeddingsPlugin` all ignore it. Retyping it is therefore free, and it is kept rather than deleted because a hook that wants store access is a legitimate affordance even though none exercises it today.

### C3. One pipeline, one adapter

`CompositeEventSink` and `MemoryEventSinkContext` live in Core and work in terms of `ICoreEventSink`. A single `PluginSinkAdapter : ICoreEventSink` wraps an `IMemoryEventSink` and forwards the store — the upcast from `IMemoryStore` to `IPluginStore` is implicit, so the adapter is a pass-through with no conversion logic.

`InstallDefault` keeps one array type and its existing three guards (null array, empty array, null element). Plugin sinks are wrapped at registration, so the fan-out stays a single ordered list rather than two lists with an ordering question between them.

### C4. Pin the version

```xml
<AssemblyVersion>0.1.0.0</AssemblyVersion>
```

On its own line, bumped only deliberately. `Version` keeps floating from `Directory.Build.props` for the informational and package version, so the CLI banner and MCP `ServerInfo` are unaffected.

**Not `1.0.0.0`.** The contract is changing shape in this very branch; declaring stability the same day it breaks would be a promise made and broken at once.

### C5. Tests

- The contract assembly is self-sufficient: a test fixture implementing `IMemoryEventSink` and `IScriniaPlugin` compiles with a reference to `Scrinia.Plugin.Abstractions` alone and no reference to `Scrinia.Core`. This is the assertion M3 exists to make true, so it is pinned by a compilation boundary rather than by inspection.
- `PluginSinkAdapter` forwards all three events to the wrapped `IMemoryEventSink`, with the same per-sink exception isolation `CompositeEventSink` already provides.
- `InstallDefault`'s three existing guards still fire after the signature change.

---

## Out of scope

- **Unifying the REST and MCP permission mappings into one table.** Different keyspaces; five REST permissions have no MCP analogue. The shared vocabulary (A1) addresses the real drift risk.
- **Widening `IPluginStore` speculatively.** It gets the two members with a demonstrated consumer. Reuse is discovered, not predicted.
- **Anything in M2, M4, L2, L1a.** Those are branch 7.
- **The `EmbeddingsPlugin.ConfigureServices` overlap guardrail and the missing ephemeral `ReindexStoreAsync` test**, carried from branch 4. These touch the plugin project, which Part C also touches, but they are correctness items rather than surface-coherence items; folding them in would widen an already-large branch. They stay on the backlog.

## Risks

**The startup validator (A4) is the highest-blast-radius change here.** A false positive is a server that will not boot. Mitigated by testing both directions explicitly, and by the validator naming the offending route in its exception message.

**Part C touches 14 files referencing `IMemoryEventSink`, and splits that interface in two.** The move is mechanical but wide, and the split means every existing implementation must be assigned to the correct side — plugin-shaped sinks to `IMemoryEventSink`, Core's handlers to `ICoreEventSink`. Assigning one wrongly shows up as a compile error rather than silently, because the store parameter types differ. Part C runs last so that a failure there cannot destabilise A or B.

**A reversed `ProjectReference` is a build-graph change.** If `Scrinia.Core` referencing `Scrinia.Plugin.Abstractions` creates a cycle with anything else in the solution, it surfaces at restore rather than as a subtle runtime fault. The first task of Part C should establish the reversal builds clean before any type is moved.

**Part B's breaking changes are agent-facing.** The MCP surface is what external agents integrate against. The CHANGELOG entry is load-bearing, not ceremony.
