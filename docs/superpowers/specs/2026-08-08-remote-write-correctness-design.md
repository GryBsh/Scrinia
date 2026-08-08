# Remote Write Correctness — Design

**Status:** design, awaiting review
**Sequence:** residuals spec 1 of 4. See `2026-08-08-residuals-triage.md` for the verified
inventory this is cut from (tier T1/T3 items).

## Goal

Stop remote-mode operations from reporting success while silently discarding the caller's
data. Every fix here is a case where the return value says the write happened and it did not.

## Why these six belong together

They look like six findings from three different branches. They are one defect wearing
several hats: **the HTTP wire format is narrower than the interface it implements, and every
place the format is too narrow, the client swallows the difference instead of surfacing it.**

Fixing them separately would mean widening the same DTOs three times and reviewing the same
compatibility question three times.

## Established facts

Each carries the evidence that proves it at `c3773bf`. Re-run these rather than trusting the
list; the residuals memories that fed this spec contained two false claims (below).

| # | Fact | Evidence |
|---|---|---|
| F1 | `StoreApiRequest` carries `Content, Name, Description, Tags, Keywords, ReviewAfter, ReviewWhen` — and no general attributes map | `src/Scrinia/HttpMemoryStore.cs:106-107` |
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

### 1. Attributes cross the wire

Add an `Attributes` field to `StoreApiRequest` carrying the same
`Dictionary<string, string[]>` shape the local store persists, and have the server-side
remember handler write it through to the entry it creates.

`Description`, `Tags`, `Keywords`, `ReviewAfter` and `ReviewWhen` stay as they are. They are
redundant with the attributes map but removing them is a breaking wire change for existing
servers, and this spec's job is to stop losing data, not to tidy the DTO. Note the redundancy
in a comment; leave the cleanup to a later branch.

**Server side must be authoritative about what it accepts.** An attributes map arriving from
an untrusted client is caller-supplied metadata, not caller-supplied *scores* — it is the same
trust level as `Tags`, which the server already accepts. No new trust boundary is crossed.

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
