# Event Pipeline Unification — Design

**Date:** 2026-08-16
**Branch:** 3 of 7 from the 2026-08-16 whole-repository design review
**Findings addressed:** H4 (High), plus one defect the review did not find (D-1 below)
**Depends on:** branch 2 (store contract & remote parity), merged 2026-08-16 — see D-3
**Status:** approved

---

## What is actually wrong

The review recorded H4 as "REST writes fire NO `IMemoryEventSink` at all … so a REST write does
no ref-linking or orphan marking while the identical write through the MCP endpoint OF THE SAME
PROCESS does." That is true, but it is one symptom of two independent defects, and it frames the
split itself as the bug. The split is not the bug.

### The split is deliberate, documented, and works

`docs/architecture/overview.md:138` records it as a Key Design Decision:

> The CLI and server use different hook mechanisms **to avoid double-firing**
>
> | Path | Event Hooks |
> | --- | --- |
> | CLI (MCP stdio) | `IMemoryEventSink` via `MemoryEventSinkContext` |
> | Server (REST API) | `IMemoryOperationHook` via `PluginPipeline` |
>
> A plugin implementing both interfaces fires only once per operation.

The once-per-operation claim holds: the paths are disjoint, so `BuiltInEmbeddingsService` —
registered both as a hook (`Program.cs:156`) and inside the sink composite (`:151`) — fires
exactly once whichever door a write arrives at. The two contracts are also genuinely different
and not trivially mergeable: `IMemoryOperationHook` is a **published plugin contract**
(`Scrinia.Plugin.Abstractions`) with before/after phases, an `Order`, and a `Cancel` veto;
`IMemoryEventSink` is after-only, unordered, and cannot veto.

**The table is missing a row.** The server hosts an MCP endpoint as well as the REST API, and
`Program.cs:297` sets `MemoryEventSinkContext.Current` for it. So there are three write paths,
not two, and the third is in the same process as the second:

| Path | Embeddings | Maintenance | Entity typing |
| --- | --- | --- | --- |
| CLI MCP stdio (local) | yes | **yes** | **yes** |
| Server **REST** | yes | **no** | no |
| Server **MCP** | yes | **yes** | no |

That missing row is why the divergence went unnoticed: the document models a CLI/server split,
and the actual discrepancy is REST-versus-MCP *inside the server*.

### D-1 (new, not in the review) — maintenance is a passenger on the embeddings registration

There is exactly **one** `AddSingleton<IMemoryEventSink>` in the whole server, at
`Program.cs:151`, and it is nested three deep:

```
if (!pluginProvidesEmbeddings)              // :107
    try {                                    // :109
        if (embeddingProvider.IsAvailable)   // :127
            AddSingleton<IMemoryEventSink>(new CompositeEventSink([
                BuiltInEmbeddingsService, new MaintenanceEventSink() ]));   // :151
```

Both fallbacks — the `else` at `:162` and the `catch` at `:171` — register only an
`ISearchScoreContributor`. Neither registers any sink.

`MaintenanceEventSink` has no dependency on embeddings; it is constructed with `new
MaintenanceEventSink()`. It sits inside that block only because of where the code was written.
The consequence is that ref-linking and orphan marking disappear from the server, silently, in
three ways that have nothing to do with maintenance:

1. **A plugin provides embeddings.** `pluginProvidesEmbeddings` is true, the whole block is
   skipped, no sink is registered. **Installing the Vulkan embeddings plugin turns off
   ref-linking and orphan marking on the server.**
2. **The embedding model is not available** (not yet downloaded — the log line says "Run
   'scri setup'"). `IsAvailable` is false, `else` branch, no sink.
3. **Embedding initialisation throws.** `catch` branch, no sink.

In each case `GetService<IMemoryEventSink>()` at `:297` returns null, so even the MCP door fires
nothing. This is a strictly larger blast radius than H4's REST/MCP gap, and it is the defect a
user is most likely to hit, because case 1 is a supported, documented configuration.

---

## Decisions

### D-1 — Register maintenance independently of embeddings

Move `MaintenanceEventSink`'s registration out of the embeddings block to an unconditional
`builder.Services.AddSingleton<IMemoryEventSink>(new MaintenanceEventSink())` alongside the other
dependency-free services. It takes no constructor arguments and has no relationship to
embeddings; nothing about it should be conditional.

To make that expressible, the registration shape changes from **one composite** to **individual
sinks**:

- Each sink is registered on its own as `IMemoryEventSink`.
- `Program.cs:297` changes from `GetService<IMemoryEventSink>()` to composing every registered
  sink: `new CompositeEventSink([.. sp.GetServices<IMemoryEventSink>()])`.

This is required, not cosmetic: with several individual registrations,
`GetService<IMemoryEventSink>()` returns only the last one, which would silently drop sinks. It
is also what makes D-2's exclusion rule expressible at all — a `CompositeEventSink` is opaque and
cannot be filtered from outside.

### D-2 — One adapter carries the sinks onto the REST path

Add a single `IMemoryOperationHook` implementation — `EventSinkOperationHook` in
`Scrinia.Server/Services` — that holds the registered sinks and fires them from its `OnAfter*`
methods:

| Hook method | Sink call |
| --- | --- |
| `OnAfterStoreAsync(AfterStoreContext)` | `OnStoredAsync(ctx.QualifiedName, ctx.Content, ctx.Store, ct)` |
| `OnAfterAppendAsync(AfterAppendContext)` | `OnAppendedAsync(ctx.Name, ctx.Content, ctx.Store, ct)` |
| `OnAfterForgetAsync(AfterForgetContext)` | `OnForgottenAsync(ctx.Name, ctx.WasDeleted, ctx.Store, ct)` |

It implements no `OnBefore*` method — sinks are after-only and have no veto, so the adapter must
not invent one.

**The exclusion rule, which is the whole point.** The adapter fires only sinks that are **not**
themselves registered as an `IMemoryOperationHook`:

```csharp
_sinks = sinks.Where(s => s is not IMemoryOperationHook).ToArray();
```

`BuiltInEmbeddingsService` implements both interfaces and is registered as a hook in its own
right, so without this filter a REST write would embed twice. This preserves the documented
once-per-operation guarantee while making membership identical across doors — and it puts the
rule in **one place** rather than requiring every future sink to remember which interfaces to
implement, which is the root-cause shape the review named.

**Known asymmetry, carried deliberately.** `PluginPipeline` builds `AfterStoreContext.QualifiedName`
and `AfterAppendContext.Name` through `BuildRoutingQualifiedName`, but sets
`AfterForgetContext.Name = name` raw (`PluginPipeline.cs:169`). The adapter passes each through
as-is rather than "fixing" forget, because `AfterForgetContext` is a published plugin contract
and changing what existing plugins receive is out of scope here. The parity tests (below) assert
on observable side effects rather than on the name string, so they hold either way. Recorded so
it is a known difference rather than a discovery.

### D-3 — Entity typing runs server-side

Register `EntityTypingEventHandler` as an `IMemoryEventSink` on the server, so a server write
gets the same entity-typing promotions a CLI write gets. Unlike maintenance, this one **is**
legitimately conditional: its constructor takes an `IEmbeddingProvider`, so it registers inside
the embeddings-available branch, and its absence when embeddings are unavailable is a real
dependency rather than an accident.

**The class has to move first, and it moves for free.** `EntityTypingEventHandler` currently
lives at `src/Scrinia/Services/EntityTypingEventHandler.cs` — the CLI project, assembly `scri`.
`Scrinia.Server` references only `Scrinia.Core`, `Scrinia.Mcp` and
`Scrinia.Plugin.Abstractions`, so as things stand the server **cannot** reference the type at
all, and D-3 is not implementable without either moving it or making the server depend on the
whole CLI. It moves: every one of its four usings is a `Scrinia.Core.*` namespace,
`EntityTypeClassifier` is already `Scrinia.Core.Search`, and it has no CLI dependency whatever.

It goes to `src/Scrinia.Core/EntityTypingEventHandler.cs`, namespace `Scrinia.Core`, beside its
sibling `MaintenanceEventSink.cs` at Core's root. **No call site changes.** All three
construction sites — `WorkspaceSetup.cs:402`, `:448`, and the tests in
`EntityTypingEventHandlerTests.cs` / `EntityTypingWiringTests.cs` — already carry
`using Scrinia.Core;`, so the type resolves at its new home without an edit. `WorkspaceSetup`
still needs its `using Scrinia.Services;` for `CoreEmbeddingEventHandler`, which stays in the
CLI project (that one is branch 4's business).

**This is safe only because branch 2 landed first, which is why the review sequenced it that
way.** `EntityTypingEventHandler.PromoteAsync` reads the existing typed vocabulary via
`GetEntityFactStore()` and **persists** its promotions. Before branch 2, a remote store's
vocabulary was truncated at 200 entries, so promoting against it wrote durable corruption.
Branch 2 fixed the truncation and put it under `PagedListingConformanceTests`. Registering the
typing sink before that fix would have wired the durable-corruption writer onto a store that
still read 200 rows.

### D-4 — Correct the architecture document

`docs/architecture/overview.md:138` gains the missing third row and states the rule the adapter
now enforces: every write fires the same sink set whichever door it arrived at, and a component
registered as both a hook and a sink still fires exactly once. The table as it stands describes
a two-path world that has not existed since the server grew an MCP endpoint.

The review also noted the split is documented **both** as a Key Design Decision here **and** as
deferred duplication debt in an older spec. Those are reconciled in favour of this document: the
split stays, the membership divergence goes.

---

## Testing

The artifact this area has never had is one that forces the doors to agree. Following branch 2's
shape — and its lesson, that a test which has never been observed to fail is not evidence:

**`EventPipelineParityTests`** (`tests/Scrinia.Server.Tests`). The same write performed twice
against one server — once over REST, once over the hosted MCP endpoint — must produce the same
observable side effects. Assert on effects, not on invocation counts:

- A memory whose content references another memory gets the same ref-link either way.
- Deleting a referenced memory marks the same orphan either way.
- Both writes are embedded exactly once (a double-embed must fail this test — that is the
  exclusion rule's guard).

**`ServerSinkRegistrationTests`.** The three D-1 cases, each asserting `MaintenanceEventSink` is
present in the resolved sinks: embeddings available; embedding provider unavailable; a plugin
providing embeddings. Each must fail against the pre-fix registration.

Every test must be observed failing before the fix. Reverting D-1's registration change and
D-2's adapter must turn the relevant tests red.

---

## Rejected alternatives

**Make `MaintenanceEventSink` implement `IMemoryOperationHook` as well.** Smallest diff, but it
is per-sink duplication: every future sink must remember to implement both interfaces, and
nothing catches one that forgets. That is precisely the root-cause shape this remediation exists
to remove.

**Collapse the two contracts into one.** `IMemoryOperationHook` is published in
`Scrinia.Plugin.Abstractions` and carries before-phases, ordering, and a cancel veto that
`IMemoryEventSink` has no equivalent for. Collapsing means either breaking third-party plugins or
growing the sink contract to a superset it does not need. The double-firing rationale is sound;
only the membership was wrong.

**Route the server's MCP endpoint through `PluginPipeline` too.** One path, no membership
question — but `PluginPipeline` covers only Store/Append/Forget, while the MCP surface also
carries link, compact, reconcile, and the ephemeral paths. The refactor is far larger than the
defect.

**Put `EntityTypingEventHandler` behind a config flag.** Adds a configuration surface and a
second behaviour to test and document, to hedge a risk that branch 2 already removed.

---

## Out of scope

- **The `/facts` wire route.** Branch 2 deferred it and left a corrected cost analysis. That
  analysis must be re-examined, not inherited, if this branch changes what runs server-side —
  see `/findings/h1-store-defaults-falsified` chunk 7, which corrects a claim the branch-2 spec
  got wrong.
- **H3 (embeddings consolidation)** is branch 4. D-1 touches the embeddings registration block
  only to move a non-embeddings line out of it.
- **`CoreEmbeddingEventHandler` vs `BuiltInEmbeddingsService`** duplication — branch 4.

## Success criteria

1. `MaintenanceEventSink` is registered outside every embeddings conditional; all three D-1 cases
   resolve it.
2. A REST write and an MCP write to the same server produce the same observable side effects.
3. Nothing embeds twice — the exclusion rule is under test.
4. `EntityTypingEventHandler` is registered server-side when an embedding provider is available.
5. `overview.md`'s table has three rows and matches the code.
6. Full solution build and test suite green.
