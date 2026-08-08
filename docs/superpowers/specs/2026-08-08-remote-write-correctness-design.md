# Remote Write Correctness — Design

**Status:** design, awaiting review
**Sequence:** residuals spec 1 of 4. See `2026-08-08-residuals-triage.md` for the verified
inventory this is cut from (tier T1/T3 items).

## Goal

Stop remote-mode operations from reporting success while silently discarding the caller's
data, and bring the HTTP surface up to the tool surface it is supposed to expose.

## Why these six belong together

They look like six findings from three different branches. They are one defect wearing
several hats: **the HTTP API is a generation behind the model it serves, and every place it
falls short, the client swallows the difference instead of surfacing it.**

That framing is stronger than the residual memories recorded, and it changes what the fix is.
The memories describe a wire format "missing" an attributes field. What the code shows (F1a–F1e)
is a wire format frozen at the pre-attributes tool surface, kept alive by a migration helper
that runs on every request. The local store finished that cutover; the HTTP layer never did.
See §1.

Fixing them separately would mean widening the same DTOs three times and reviewing the same
compatibility question three times.

## Established facts

Each carries the evidence that proves it at `c3773bf`. Re-run these rather than trusting the
list; the residuals memories that fed this spec contained two false claims (below).

| # | Fact | Evidence |
|---|---|---|
| F1 | `StoreApiRequest` carries `Content, Name, Description, Tags, Keywords, ReviewAfter, ReviewWhen` — and no general attributes map | `src/Scrinia/HttpMemoryStore.cs:106-107` |
| F1a | The server-side `StoreRequest` has the identical legacy shape | `src/Scrinia.Server/Models/ApiDtos.cs:5-12` |
| F1b | The current MCP tool surface is `path`, `content[]`, `attributes{}`, `keywords[]`. There is no `tags`, `reviewAfter` or `reviewWhen` — `tag` and `review` are *attribute keys* | `KnownAttributes.cs:25` (`TagKey = "tag"`), `:22` (`ReviewKey = "review"`) |
| F1c | The server runs `AttributeMigration.BuildCarrierAttributes` on **every inbound store request**, translating the legacy request fields into the attributes model, with `fileKeys: null` and `anchors: null` hardcoded because the wire cannot express them | `src/Scrinia.Server/Services/MemoryOrchestrator.cs:135-137`, and again at `:290-291` for append |
| F1d | Outbound, the server projects the attributes map back down to a flat `Tags` array to fill the legacy response shape | `MemoryEndpoints.cs:114` (`AttributeReaders.DisplayTags(item.Entry.Attributes)`) into `MemoryListItem.Tags`, `ApiDtos.cs:40` |
| F1e | The storage side finished this migration; the wire did not. `ArtifactEntry` keeps `LegacyFields` only as a back-compat *ingest* path for old sidecars | `ArtifactEntry.cs:33-65`; and `MemoryOrchestrator.cs:155-156` — "the former legacy entry members no longer exist" |
| F1f | The web client mirrors the same legacy shape | `web/src/api/types.ts:6-10`, `:45-46` (`description`, `tags`, `reviewAfter`, `reviewWhen`) |
| F2 | `HttpMemoryStore.LoadIndex` returns `[]` unconditionally | `src/Scrinia/HttpMemoryStore.cs:253-254` |
| F3 | `IMemoryStore`'s default `TryUpdateEntry` does `LoadIndex(scope).FirstOrDefault(...)` and returns false when nothing matches | `src/Scrinia.Core/IMemoryStore.cs:33-41` |
| F4 | `EntityTypingEventHandler` calls `LoadIndex`, and is the async typing-promotion seam | `src/Scrinia/Services/EntityTypingEventHandler.cs` |
| F5 | `BuildSearchGet` appends `excludeTopics`/`pathPrefix` as additive query params; there is no version negotiation anywhere in `HttpMemoryStore` | `src/Scrinia/HttpMemoryStore.cs:409-417` |
| F6 | Remote search results are rebuilt with `OriginalBytes: 0, CreatedAt: DateTimeOffset.UtcNow` | `src/Scrinia/HttpMemoryStore.cs:375-377` |
| F7 | `ArtifactUri` returns a synthesized `remote://{store}/{scope}/{name}` | `src/Scrinia/HttpMemoryStore.cs:492-493` |
| F8 | `Upsert` (`:256`) and `ArchiveVersion` (`:485`) are empty on purpose — the server performs those writes | `src/Scrinia/HttpMemoryStore.cs` |

**Two corrections to the source memories, already applied above.** The store-boundaries
residual states `StoreApiRequest` "carries only content and name"; F1 shows that is wrong, and
the real loss is narrower — tags and review dates do cross the wire. Any task that assumes the
broader claim will write a test that passes vacuously. Second: F8's two no-ops are
*defensible* and are not in scope; only `LoadIndex` is a defect.

## Design

### 1. The HTTP surface catches up to the tool surface

**This is not a missing field. The HTTP API is frozen at the previous generation of the tool
surface, and the server maintains a live migration shim to translate it.**

Per F1b, the product's model is an attributes map. Per F1/F1a, the wire carries `Tags`,
`ReviewAfter` and `ReviewWhen` — which in the current model are not fields at all, but two
attribute *keys*. Per F1c the server bridges the gap by calling `AttributeMigration`, a class
in `Scrinia.Core/Migration/`, on every inbound request. A migration helper running
permanently in a request path is the shape of an unfinished cutover, not a design.

The round trip is lossy in both directions:

- **Inbound:** only `tag` and `review` can be expressed. `person`, `org`, `file`, `ref`,
  `anchor`, `mention` — every other key the local store supports — have no representation.
  F1c makes this explicit: `fileKeys: null, anchors: null` are hardcoded arguments, because
  there is nothing on the wire to pass.
- **Outbound:** F1d flattens the whole map to a `Tags` array, so a remote listing cannot show
  any non-tag attribute even when the server has it.

F1e is what settles it. The storage side completed this migration — `ArtifactEntry` keeps
`LegacyFields` purely as a back-compat ingest path for sidecars written before the rename, and
the orchestrator's own comment states that "the former legacy entry members no longer exist."
Storage moved; the wire did not.

**Design: `attributes` becomes the wire representation, request and response**, matching the
MCP surface. `Tags`/`ReviewAfter`/`ReviewWhen` stay *accepted* on input for compatibility with
existing clients, translated at the edge — which is what `BuildCarrierAttributes` already does,
so the shim survives, demoted from primary path to compatibility edge. Responses gain the full
map; `Tags` stays populated alongside it so existing consumers keep working.

**This supersedes my first draft of this section**, which said to add an `Attributes` field and
keep the legacy fields as-is with the redundancy noted. That was wrong in a way worth recording:
it would have made the shim permanent *and* introduced a second representation beside it, so
the server would accept the same information two ways with an undefined merge order between
them. The correct move is to make the wire match the model and demote the legacy fields to
compatibility inputs.

**Trust:** an attributes map from a client is caller-supplied metadata at the same trust level
as `Tags`, which the server already accepts. No new trust boundary. But the server must
validate keys — an unbounded attribute map is a storage-growth vector in a way a fixed set of
named fields was not. Bound key count and value-array length, mirroring the search-side bounds
in spec 3 §4.

### 2. `LoadIndex` stops lying

Two candidate fixes; take the second.

- *Override `TryUpdateEntry` on `HttpMemoryStore`* to PATCH the entry server-side. Correct, but
  needs a new endpoint and a new DTO, and leaves `LoadIndex` still returning `[]` for every
  other caller.
- **Have `LoadIndex` proxy to the existing listing endpoint** the way `ListScoped` already
  does. One change, and every current and future caller of `LoadIndex` gets real data instead
  of silence.

The second is preferred because `LoadIndex` returning `[]` is not a `TryUpdateEntry` problem —
it is a lie told to everyone, and `TryUpdateEntry` is merely the caller that noticed.

**This has a cost worth stating.** `LoadIndex` is a synchronous method and the listing call is
async; the proxy will block. That is already true of several `HttpMemoryStore` members and is
not a new pattern here, but it makes `LoadIndex` a network round trip where it used to be free.
Callers that loop over `LoadIndex` will feel it. Task ordering must therefore land the
per-caller audit *before* the proxy, not after.

### 3. Filters stop failing open

Add a capability probe: `HttpMemoryStore` asks the server what it supports once, caches the
answer for the client's lifetime, and **refuses rather than silently under-filtering** when a
requested filter is unsupported.

Failing closed is the right default here because the failure mode being fixed is precisely
that the caller believes filtering happened. An unfiltered result set that looks filtered is
worse than an error: it silently widens the blast radius of anything built on it.

If the server does not answer the probe at all, treat it as "v1, no filter support" — that is
the only honest reading of a server that predates the probe.

### 4. Search results stop fabricating metadata

Widen the search response to carry real `OriginalBytes` and `CreatedAt` per result, and have
`HttpMemoryStore` use them. When the server does not supply them (older server, per the probe
above), the client must surface absence rather than substitute `UtcNow`.

This requires `ArtifactEntry`'s remote construction path to tolerate genuinely-unknown values.
Decide during planning whether that means nullable fields or a sentinel; **do not** default to
`DateTimeOffset.MinValue`, which is a fabrication with a different disguise.

### 5. `ArtifactUri`

**Deferred out of this spec, deliberately.** Making `remote://` dereferenceable is a new
capability, not a correction — nothing today claims that URI resolves. It is recorded here so
the next reader knows it was considered and why it was left: fixing it needs a resolvable
addressing scheme designed on its own terms, not bolted onto a correctness pass.

### 6. Supplemental and entity-graph scores

**Also deferred.** `SearchRemote` not forwarding client-computed scores is a documented,
deliberate trust decision, not a silent loss — the scores are keyed by local scope and the
server scores server-side. Whether remote scoring needs its own capability surface is a design
question that deserves its own brainstorm. It is in this spec's inventory only because the
store-boundaries branch inherited it; it does not meet this spec's bar of "reports success
while losing data".

## Out of scope

- `ArtifactUri` dereferenceability (§5) and remote scoring surface (§6).
- `Upsert`/`ArchiveVersion` no-ops — correct as written (F8).
- Any change to local-mode behaviour. Every fix here is client- or server-side HTTP.
- **Updating the web client to consume the new shape.** Per F1f, `web/src/api/types.ts`
  mirrors the legacy DTOs and is a second consumer frozen at the same generation. Because §1
  keeps `Tags` populated on responses, the web client keeps working unchanged — so this is a
  follow-up, not a blocker. Record it; do not fold it in. A UI that surfaces the full attribute
  map is a feature with its own design questions.
- **Retiring the legacy request fields.** §1 demotes them; removing them is a breaking change
  for any existing client and needs a deprecation period. Note the target state, ship the
  demotion.

## Testing

The load-bearing risk is a test that passes under the bug it targets. The store-boundaries
branch hit exactly that: a prescribed assertion on the `POST /memories` payload would have
passed vacuously, because the field it asserted on does not exist in the DTO.

So: **every test here asserts on what the server received or persisted, not on what the client
sent.** A recording server double, or an assertion against the stored entry after a round trip.

Specific cases:
- `remember()` with entities, anchors and an arbitrary attribute key against a remote store →
  the stored entry carries all three.
- Entity-typing promotion against a remote store → the typed entity persists, and a
  re-`recall` sees it. This is the `TryUpdateEntry` path from F2/F3/F4 end to end.
- A filtered search against a server that reports no filter support → an error, not an
  unfiltered result set.
- A remote search result's `CreatedAt` → the memory's real creation time, and where the server
  cannot supply it, something a caller can distinguish from a real timestamp.

## Risks

**The wire format changes.** A new client against an old server, and an old client against a
new server, must both be defined. The probe in §3 handles the first. The second is handled by
additive-only fields: an old client ignores `Attributes` it does not know about. Nothing here
removes or renames an existing field, and nothing should.

**`LoadIndex` becomes a network call.** Stated above; the mitigation is auditing callers first.
Flagging it as the item most likely to produce a surprise during implementation.
