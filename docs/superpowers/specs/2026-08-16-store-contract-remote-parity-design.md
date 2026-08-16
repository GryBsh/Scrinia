# Store Contract & Remote Parity — Design

**Date:** 2026-08-16
**Branch:** 2 of 7 from the 2026-08-16 whole-repository design review
**Findings addressed:** C2 (Critical), H1 (High)
**Status:** approved

---

## The defect

`HttpMemoryStore.ListScoped` (`src/Scrinia/HttpMemoryStore.cs:640-642`) builds its
request URL with no `limit` and no `offset`:

```csharp
string url = scopes is not null
    ? $"{BaseUrl}/memories?scopes={Uri.EscapeDataString(scopes)}"
    : $"{BaseUrl}/memories";
```

The server defaults to 200 rows (`MemoryEndpoints.DefaultListLimit`). The response
carries `ListApiResponse.Total`; `ListScoped` deserializes it (`:660`) and discards it.

Past 200 entries, a remote store answers listing questions confidently and wrongly,
with no error and no truncation marker.

### The blast radius is one level deeper than the review recorded

Every member below is *correct code standing on a truncated foundation*. None of them
has a bug of its own:

| Member | Site | Consequence past 200 entries |
| --- | --- | --- |
| `DiscoverTopics()` | `:1368-1377` | calls `ListScoped(null)`; topics whose entries all fall past the cut **do not exist** |
| `ResolveReadScopes(null)` | `:1403-1416` | built on `DiscoverTopics()` — whole scopes become unreadable, and code that then lists them reports them empty |
| `GatherTopicInfos(scopes)` | `:1379` | calls `ListScoped(scopes)`; wrong per-topic entry counts |
| `GetEntityFactStore()` | `IMemoryStore.cs:125-126` (default) | entity facts derived over a truncated corpus |
| `ListScoped(scopes, excludeTopics)` | `IMemoryStore.cs:149-154` (default) | post-filters a truncated list |
| `list` with `offset > 200`, references, search ranking | various | truncated inputs |

`DiscoverTopics → ResolveReadScopes` is the leg the review missed and the most damaging
one. The failure is not "the tail is missing" — it is "entire scopes are invisible,
including to the code that then reports them as empty."

### The fix already exists, 180 lines above

`LoadIndex` (`:449-499`) pages to exhaustion, and its own comment names this exact bug:

> 1000 is MaxListLimit server-side (MemoryEndpoints.cs) — the largest page the server
> will honour […] The server's default page size (200) is what silently truncated
> LoadIndex before this loop existed: a caller who asked for "the scope" got the first
> page and no signal that more existed.

Two paging loops in one file is duplicated knowledge. That duplication is *why* one of
them stayed broken while the other's comment described the defect precisely.

---

## Decisions

### D-1 — One paging loop, one home

Extract `LoadIndex`'s loop into a private helper on `HttpMemoryStore`:

```csharp
private List<MemoryEntry> FetchAllPages(string? scopes)
```

It requests `{BaseUrl}/memories` with `offset`/`limit` (and `scopes` when non-null),
accumulates wire entries, and loops until `offset >= list.Total`. Both callers consume
it:

- `LoadIndex(scope)` maps pages to `ArtifactEntry` via `ToArtifactEntry`.
- `ListScoped(scopes)` maps to `ScopedArtifact` via `MemoryNaming.ParseScopeLabel`, and
  prepends its client-side ephemeral entries as it does today.

**Preserved from `LoadIndex` verbatim:** the non-2xx throw. Both call sites already
throw rather than returning `[]`, for the reason `LoadIndex:469-478` records — an empty
list is indistinguishable from "this store has nothing in it", and a 403 from the
`search` permission gate previously made `list()` answer "No memories stored." for a
store full of them. The helper throws; neither caller may soften it.

**Unified:** the two messages differ only as `for scope 'x'` versus
`for scopes 'x'`. One wording, built from the helper's `scopes` argument.

**Unchanged:** the page size stays `1000`, matching `MemoryEndpoints.MaxListLimit`.
The bug is the *absence* of an explicit limit, not the value of one.

**Unchanged:** `ListScoped`'s ephemeral-inclusion rule (`:621-629`) and the `scopes is
not null` versus `IsNullOrWhiteSpace` distinction documented at `:632-639`. Both are
correct and out of scope; the comment explaining why they legitimately differ stays.

### D-2 — State the invariant on the interface; keep the defaults

**H1 as written in the review does not survive verification, and is not implemented.**

The review claimed three of `IMemoryStore`'s five default-bodied members are *wrong*
for `HttpMemoryStore` — `GetEntityFactStore` (`:125`), `ListScoped(scopes,
excludeTopics)` (`:149`), `ResolveReadScopes(scopes, excludeTopics)` (`:161`) — while
`TryUpdateEntry` and `TryUpdateEphemeral` are *degraded but correct*, and that the
principled fix is deleting the three so `HttpMemoryStore` takes three compile errors.

Checking each against HEAD, that distinction does not hold:

- All three are ordinary post-filters or rebuilds over `ListScoped`.
- Each becomes **correct** the moment `ListScoped` pages — which D-1 does.
- The wire's `MemoryEntry` record already carries `Attributes`
  (`HttpMemoryStore.cs:160`), so facts derived from a complete remote listing are
  correct, not merely non-empty.
- `HttpMemoryStore.ResolveReadScopes(string?)` is implemented (`:1403`) and correct;
  the two-argument default's post-filter over it produces the same set as
  `FileMemoryStore`'s scope-level override (`:1884`).

Deleting the three would force `HttpMemoryStore` and `FakeMemoryStore` to supply bodies
that are character-for-character the defaults they just lost. That is ceremony, not
prevention.

**What is real is smaller and different.** All three defaults depend on one invariant
that has never been written down: *`ListScoped(null)` returns the complete cross-scope
listing.* That invariant is exactly what `HttpMemoryStore` violated. So:

1. `IMemoryStore.ListScoped(string? scopes)` (`:129`) gets a doc comment stating it:
   the returned list is complete, and an implementation that pages a remote source
   must page to exhaustion.
2. The three defaults' doc comments cite that invariant as their precondition, so the
   next implementer reads *why* inheriting them is safe rather than guessing.
3. The two inaccurate prose claims are corrected:
   - `:55-56` — "Neither member has a default implementation, deliberately: every store
     implements both, so a store added later cannot silently inherit a derivation that
     is wrong for it." True of `StoreAsync`/`AppendAsync`, but stated as though it were
     the file's governing rule, three members before three defaults that contradict it.
   - `:131-137` — "A store that cannot filter must now say so at compile time." True of
     `SearchAll` specifically; read as a file-wide policy it is false.

   Both are narrowed to the members they actually govern. This matters beyond tidiness:
   these two comments are what led the review itself to misdiagnose H1.

### D-3 — The conformance harness crosses the page boundary

`tests/Scrinia.Server.Tests/Conformance/StoreConformanceFixture.cs` is already the
artifact that forces the two stores to agree: one server, one corpus, two handles —
`Local` is the very `IMemoryStore` instance the endpoints use, `Remote` is an
`HttpMemoryStore` over a real Kestrel loopback socket. It is the right shape.

It is also structurally blind to C2: it covers **search only**, over **27 entries** —
below the 200-row boundary where the defect begins.

Add `PagedListingConformanceFixture` beside it, same two-handle construction, seeding
**~250 memories across ~10 topic scopes** through the real REST write path.

**A separate fixture, not an extension of the existing one.** `StoreConformanceTests`
pins exact counts to its 27-entry corpus ("25 of the fixture's 27 seeded entries
contain 'widget'", "an unfiltered top-20 of 27 candidates"). Growing that corpus would
break assertions whose subject is something else.

Parity cases, `Local` versus `Remote`, each failing today and passing after D-1:

| Case | Assertion |
| --- | --- |
| `ListScoped(null)` | full count, and set equality on qualified names |
| `DiscoverTopics()` | set equality |
| `ResolveReadScopes(null)` | set equality |
| `GatherTopicInfos(null)` | per-topic entry counts equal |
| `GetEntityFactStore()` | local's sidecar-cached build and remote's default rebuild agree |

`DiscoverTopics()` is the sharpest case. `FileMemoryStore.ListScoped` enumerates
`ResolveReadScopes` and then `LoadIndex(scope)` per scope, so entries arrive
**scope-by-scope and contiguous**. Truncation at 200 therefore makes whole topics
vanish rather than thinning every topic evenly. The assertion is set equality, so it
holds however the server orders scopes — it does not depend on predicting *which*
topic disappears.

`GetEntityFactStore()` is the case that closes C2's stated harm: it compares two
genuinely different implementations — `FileMemoryStore`'s generation-invalidated
sidecar cache (`:1660`) against the interface default's rebuild from `ListScoped(null)`
— over a corpus large enough for the truncation to change the answer.

---

## Rejected alternatives

**Delete the three defaults (H1 as written).** Rejected in D-2 above: the premise is
false at HEAD, and the migration produces three forwarding bodies identical to what
they replace.

**Make the client's page size injectable so a small test drives several real pages.**
Rejected. It adds a test-visible seam to production code to prove a loop that the
250-entry case already proves is *entered and exhausted*. Recorded as a known
limitation instead (below).

**Seed 1001 entries to exercise genuine multi-page iteration.** Rejected on cost: the
client pages at 1000, so honest multi-page coverage means ~1000 REST writes with full
derivation on every suite run. The defect being fixed triggers at 200; the test that
proves it should sit at 200.

**Add `GET /facts` to the wire.** Deferred — see below.

---

## Out of scope, stated so it is cheap to reverse

**No `/facts` route on the wire.** The review listed its absence under C2. With D-1
applied, the interface default rebuilds facts from a complete listing, so the answer is
**correct**; what remains is cost — one full listing per unfiltered `facts()` call.

That cost is bounded, and verifying where it lands is what makes deferring it safe:

- **CORRECTED 2026-08-16, after the whole-branch review falsified the original claim.**
  This section first asserted that `SearchPipeline` "always runs server-side against a
  `FileMemoryStore`. A remote client never reaches it." **That is false**, and the
  correction matters because the claim is load-bearing for this deferral and for branch 3.
  `MemoryTools.Core.cs:882` passes a hardcoded `surfaceEntityFacts: true` into
  `SearchPipeline.ExecuteAsync` over `CurrentStore` — which in remote mode IS the
  `HttpMemoryStore` — and `SearchPipeline.cs:50` fires
  `store.ListScoped(query.Scopes, query.ExcludeTopics)` on that disjunct **alone**,
  independently of either contributor. So the MCP `search()` tool reaches
  `HttpMemoryStore.ListScoped` on **every remote invocation**.

  What that means for this branch, stated plainly rather than minimised: before the fix,
  that call returned the server's first 200 rows in one request. After it, it returns the
  whole corpus, in `ceil(N/1000)` requests of full `MemoryEntry` records (`TermFrequencies`,
  `ChunkEntries`, `FileHashes`). The answer is strictly **more correct** than before — it
  was previously computing entity facts over a truncated corpus, which is the defect this
  branch exists to close — but the cost now scales with corpus size where it used to be
  constant. `SearchAll` ignores `query.Candidates`, so the sole consumer of that listing is
  `BuildEntityFacts`, which yields at most three summaries.

  This is not treated as a blocker here: it is a cost regression on a correctness fix, not a
  new defect, and this workspace holds tens of memories rather than tens of thousands. It IS
  the strongest argument yet for the `/facts` wire route, and it must be re-examined — not
  re-assumed — when that route or branch 3 is planned. The narrow code fix, deliberately not
  taken in this branch because it changes remote search behaviour rather than cleaning it up,
  is `surfaceEntityFacts: store is ILocalMemoryStore`.
- `EntityTypingEventHandler.PromoteAsync` (`:52`) calls `GetEntityFactStore()` per write
  event, but is installed only through CLI `WorkspaceSetup` plugin loading, and
  `new HttpMemoryStore` appears exactly once in `src/`
  (`ScriniaCommands.Serve.cs:62`), inside the branch that deliberately does not load
  plugins. The per-write path is not reachable with a remote store.
- The single remaining caller is `MemoryTools.Facts.cs:82`, via `SelectFactStore`'s
  unfiltered branch.

So: one full listing per unfiltered `facts()` call against a remote store. Adding a
wire route is a widening of the published REST surface and should earn its own spec.
If measurement later says otherwise, the reversal is local — override
`GetEntityFactStore` in `HttpMemoryStore`.

**~~Known limitation~~ — WITHDRAWN 2026-08-16, it was never true.** This section claimed
the 250-entry fixture proves the loop runs past the server's 200-row default but does not
exercise iteration across more than one client-side page, and that closing the gap required
either a test seam or a 1001-entry corpus.

Multi-page iteration is in fact already covered, and now covers the shared helper.
`tests/Scrinia.Tests/HttpMemoryStoreLoadIndexPagingTests.cs` stubs an `HttpMessageHandler`
serving 5 entries 2 at a time and asserts `RequestedOffsets` equals `[0, 2, 4]`, plus the
single-page case asserting `[0]`. Because D-1 routes `LoadIndex` and `ListScoped` through
one `FetchAllPages`, those pre-existing tests now exercise the shared loop directly.

The rejected alternatives above were answering the wrong question: the choice was never
between a production test seam and ~1000 REST writes, because a stubbed handler was
available the whole time and costs nothing. The 250-entry fixture and the stub tests cover
different things and both belong — the fixture proves the *defect* (a real server's 200-row
default truncating a real client), the stubs prove the *loop* (offsets advance and
terminate).

**Not touched:** `MaintenanceEventSink`, the REST event-sink gap (H4), and the
`docs/architecture/overview.md:140` reconciliation. Those are branch 3, which must
follow this one — branch 3 registers the typing sink server-side, and doing that before
the listing is complete would wire the durable-corruption writer onto a store that
still reads 200 rows.

---

## Success criteria

1. `ListScoped` and `LoadIndex` share exactly one paging loop; `grep` finds one
   `while` loop over `ListApiResponse.Total` in `HttpMemoryStore.cs`.
2. Every parity case in D-3 fails on the pre-fix client and passes on the post-fix one.
3. `IMemoryStore.cs` contains no claim about default implementations that the same file
   contradicts.
4. No member is deleted from `IMemoryStore`; `FakeMemoryStore` is unchanged.
5. Full solution build and test suite green, with the one disclosed pre-existing flake
   (`MemoryEventSinkContextInstallDefaultTests.AParallelScopeLifecycleDoesNotDisturbDefault`).
