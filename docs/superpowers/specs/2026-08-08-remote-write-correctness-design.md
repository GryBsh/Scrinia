# Remote Write Correctness — Design

**Status:** design, revised 2026-08-10 after grounding against `cdc7050`. §1 gained the
`WriteArtifactAsync` seam widening; F8 and this spec's own correction to the source memory were
both wrong and are fixed. Residuals spec 4 Part A is merged, so §2's `LoadIndex` caller audit is
already done — see `docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md`.
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
| F8 | `ArchiveVersion` (`:485`) is empty on purpose. **`Upsert` (`:256-260`) is not** — see F11 | `src/Scrinia/HttpMemoryStore.cs` |
| F9 | `IMemoryStore.WriteArtifactAsync(string subject, string scope, string artifactText, CancellationToken)` has **no parameter capable of carrying metadata** | `src/Scrinia.Core/IMemoryStore.cs:149` |
| F10 | The only construction of `StoreApiRequest` anywhere is `new StoreApiRequest([text], qualifiedName)` — content and name only; the other five fields take their defaults | `src/Scrinia/HttpMemoryStore.cs:503`, sole match for `new StoreApiRequest` |
| F11 | The MCP remember path calls `WriteArtifactAsync` at `MemoryTools.Core.cs:332`, assembles the `ArtifactEntry` (attributes, keywords, review, preview) at `:336+`, then calls `Upsert`. In remote mode that `Upsert` is a no-op, so **everything assembled after the write is discarded** | as cited |
| F12 | 8 `WriteArtifactAsync` call sites in `src/`: `MemoryTools.Core.cs:332`, `:1152`; `MemoryTools.Lifecycle.cs:571`; `MemoryOrchestrator.cs:118`, `:279`, `:326`; `ScriniaCommands.Migration.cs:293`; plus `FileMemoryStore.cs:1066` (the implementation) | `rtk proxy grep -rn "WriteArtifactAsync" src/ --include=*.cs` |

**Three corrections to the source memories and to this spec's own earlier draft.**

1. **The store-boundaries residual was right and this spec's correction of it was wrong.** The
   residual said `StoreApiRequest` "carries only content and name." F1 shows the *DTO declares*
   seven fields, so the correction looked justified — but F10 shows the *only construction site*
   passes two. Nothing but content and name ever crosses the wire. A task written against
   "tags and review dates do cross" will assert on a field that is always null.
2. **F8 was wrong about `Upsert`.** "The server performs those writes" is true only of the
   content. The server re-derives what it can from the artifact text; every piece of
   agent-supplied metadata assembled locally is dropped by the no-op (F11). The no-op *is* the
   data loss, not a division of labour.
3. Consequently **`LoadIndex` is not the only defect** on the client, as this spec previously
   asserted.

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

**Design: `attributes` replaces the legacy fields on the wire, request and response**, matching
the MCP surface.

**Nothing is deployed over HTTP** (confirmed 2026-08-08). That removes the entire compatibility
burden and makes the clean fix available: `Tags`, `ReviewAfter` and `ReviewWhen` come *out* of
`StoreRequest` and `StoreApiRequest` rather than being demoted and carried indefinitely. There
is no external client to deprecate for, and the only consumer — `web/src/api/types.ts`, F1f —
is in this repo and moves in the same change.

`Description` stays. It is not a legacy carrier: `MemoryOrchestrator.cs:126-128` uses it as a
curated `ContentPreview` that wins over the auto-generated snippet. Different concept, still
current.

The request-translation call to `AttributeMigration.BuildCarrierAttributes`
(`MemoryOrchestrator.cs:135-137`) goes with the fields it translates. Note that the *other*
call at `:290-291` stays — it carries an existing entry's review forward on append, which is
genuine migration work, not request translation. Do not delete the class.

**The seam has to be widened too — this is the part the earlier drafts missed entirely.**

Changing the DTOs is necessary and not sufficient. Per F9 there is no parameter on
`WriteArtifactAsync` to populate `attributes` *from*, and per F11 the metadata does not exist
yet at the moment the write happens — it is assembled afterwards and then handed to a no-op
`Upsert`. Widening `StoreApiRequest` alone would add a field the client can never fill.

**Design: `WriteArtifactAsync` gains an optional metadata parameter.**

```csharp
Task WriteArtifactAsync(string subject, string scope, string artifactText,
    ArtifactWriteMetadata? meta = null, CancellationToken ct = default);
```

- `FileMemoryStore` **ignores** it and keeps taking its metadata through `Upsert`. That asymmetry
  is real and must be commented at the implementation, not left for a reader to infer: the local
  store persists the entry itself, the remote store cannot, and this parameter exists for the
  seam that crosses a network.
- `HttpMemoryStore` puts it on the wire, so a remote write is **one** request carrying content
  and metadata together — one merge on the server, with a defined precedence, rather than two
  sources arriving separately.
- The MCP remember path must be **reordered**: the attribute/keyword assembly currently at
  `MemoryTools.Core.cs:336+` moves above the `WriteArtifactAsync` call at `:332`. This is the
  riskiest mechanical part of the spec and belongs in its own task with its own review.
- Remote `Upsert` stays a no-op, and its comment gets corrected to say why it is *now* legitimate
  — the write already carried the metadata — rather than the current claim, which was not true
  when it was written.

**Three drafts of this section were wrong, all recorded because the errors are instructive.** The
first said to add an `Attributes` field and keep the legacy fields with the redundancy noted —
that would have made the shim permanent and given the server two ways to receive the same
information with no defined merge order. The second demoted them to compatibility inputs with a
deprecation path — correct for a deployed API, and pure overhead for one with no clients. Both
designed for a constraint that was assumed rather than checked. The third — this spec as
written before the seam finding — got the wire format right and never asked whether the client
had anything to put in it. All three failed the same way: reasoning about the DTO in isolation
instead of tracing one write end to end.

**Trust and bounds:** an attributes map from a client is caller-supplied metadata at the same
trust level as `Tags`, which the server already accepts, so no new trust boundary. But an
unbounded map is a storage-growth vector in a way a fixed set of named fields was not. Bound key
count and value-array length, mirroring the search-side bounds in spec 3 §4.

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

The recorded finding is that `excludeTopics`/`pathPrefix` go out as additive query params, an
older server ignores unknown params rather than rejecting them, and the client returns the
unfiltered result set as though it were filtered. There is no version handshake on either end.

**With nothing deployed, the old-server population is empty**, so the capability probe I first
specified is machinery for a scenario that does not exist. The residual routed a "capability
probe" here from the search-contract branch; that recommendation predates knowing there are no
clients.

Do the cheap thing that actually closes the hole: **a version assertion**. The server reports
its API version; `HttpMemoryStore` refuses to operate against a version it does not recognise.
One field, one check, and it fails closed — which is the property that matters, because an
unfiltered result set that looks filtered silently widens the blast radius of everything built
on it.

Revisit if and when something is deployed. A capability probe is the right answer for a real
population of mixed-version servers; it is over-engineering for a client and server that ship
from the same commit.

### 4. Search results stop fabricating metadata

Widen the search response to carry real `OriginalBytes` and `CreatedAt` per result, and have
`HttpMemoryStore` use them.

Simpler than first drafted, for the same reason as §1 and §3: with no older servers, the fields
are always present, so there is no absent-value case to represent. The nullable-versus-sentinel
question I raised does not arise. One rule survives from that draft and is worth keeping in the
plan: if a genuinely-unknown value ever does need representing, **do not** use
`DateTimeOffset.MinValue` — that is a fabrication with a different disguise, which is the exact
defect being fixed here.

Note the response DTOs are `ListApiItem`/`SearchApiItem` on the client
(`HttpMemoryStore.cs:110-114`) and `MemoryListItem` on the server (`ApiDtos.cs:31-40`); the
listing side already carries `OriginalBytes` and `CreatedAt`, so this is the search side only.

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
- `ArchiveVersion`'s no-op — correct as written. Remote `Upsert` stays a no-op too, but only
  *after* §1 makes that true; today it is where the data is lost (F11).
- Any change to local-mode behaviour. Every fix here is client- or server-side HTTP.
- **A UI that surfaces the full attribute map.** `web/src/api/types.ts` is now **in scope** —
  §1 removes fields it declares, so it moves in the same change or the build breaks. But
  updating the type declarations is all this spec owns. Displaying attributes in the web UI is
  a feature with its own design questions; keep it out.

## Testing

The load-bearing risk is a test that passes under the bug it targets. The store-boundaries
branch hit exactly that: a prescribed assertion on the `POST /memories` payload would have
passed vacuously, because the field it asserted on does not exist in the DTO.

So: **every test here asserts on what the server received or persisted, not on what the client
sent.** A recording server double, or an assertion against the stored entry after a round trip.

F10 is why this matters more here than anywhere else in the sequence. The field a naive test
would assert on (`StoreApiRequest.Tags`) is not merely unpopulated by the caller — it is
*never* populated, by anything, so an assertion that it round-trips correctly would have gone
green against a wire that carries nothing.

**The web build is not in `Scrinia.sln`.** `dotnet build Scrinia.sln` will not catch a
TypeScript break in `web/src/api/types.ts`. CI runs `cd web && npm ci && npm run build`
(`.github/workflows/ci.yml:21-22`), so it is gated there — but any task touching `web/` must run
`npm run build` in `web/` itself as its gate. Note CI does **not** run `npm test`, though
`web/package.json` defines a `vitest` script.

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

**The wire format changes incompatibly, and that is now the cheap option.** With nothing
deployed over HTTP, there is no mixed-version population to design for — client, server and web
types ship from one commit. The risk this leaves is not compatibility but *silence*: the change
is only safe as long as that stays true, so the CHANGELOG entry must say plainly that the
`/memories` request and response shapes changed and that pre-change clients will not work.

**`LoadIndex` becomes a network call.** The one item most likely to produce a surprise during
implementation, and the only risk here not reduced by the no-clients answer. Mitigation is
auditing callers before writing the proxy — the same audit spec 4 Part A needs, so do it once.
