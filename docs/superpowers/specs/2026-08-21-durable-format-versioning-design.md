# Durable Format Versioning — Design

**Date:** 2026-08-21
**Stream:** B of the 2026-08-21 design review (Critical C2)
**Branch:** `fix/durable-format-versioning` off `main` @ `931605b`
**Status:** approved in chat, binding

---

## Part A — The problem, restated correctly

The review stated C2 as *"No store format carries a version, and unmapped fields
are destroyed on the next write,"* and added that *"Scrinia has never had a
working version check on any durable format."*

**The second half is false, and the correction changes the design.** Enumerating
every durable format rather than sampling one produces a three-way split:

| Format | Version written | Enforced on read | Written by |
|---|---|---|---|
| `*.meta.json` — `ArtifactEntry` | **none** | — | `FileMemoryStore.WriteSidecarCore` |
| `facts.json` — `EntityFactSidecar.Dto` | `version: 3` | **yes** | `EntityFactSidecar.Serialize` |
| `*.nmp2` artifact body | `NMP/2` sentinel | **yes** | `Nmp2Strategy` / `Nmp2ChunkedEncoder` |
| bundle `manifest.json` — `BundleManifest` | `Version` | **no** | `BundleFormatService` |
| legacy `index.json` — `IndexFile` | `v: 3` | **no** | no longer written; read-only |
| `.scrinia/skills/*.meta.json` — `SkillFileMeta` | **none** | — | `MemoryTools.WriteSidecarMeta` |
| agent `*.meta.json` — `AgentFileMeta` | **none** | — | `MemoryTools.WriteSidecarMeta` |
| `config.json` | none | n/a | free-form `Dictionary<string,string>`; unknown keys survive by construction |

Three findings the one-format framing hid:

1. **`facts.json` already has a working version gate** (`EntityFactSidecar.cs:68`
   — `dto.Version != Version` → `return null` → rebuild). It is the working model,
   but it is the *wrong* model to copy blindly: it is a rebuildable cache, so
   "wrong version → throw it away" is free. The artifact sidecar is the source of
   truth, where discarding is not on the menu.

2. **Two formats write a version nothing reads** (`BundleManifest.Version`,
   legacy `IndexFile.v`). This is worse than having no version, because the file
   looks protected. Any future reader is entitled to assume the field means
   something.

3. **Two formats the review never named exist at all**: `SkillFileMeta` and
   `AgentFileMeta`, persisted under `.scrinia/skills/`, git-tracked, unversioned.
   The review's C2 named one format; the class has seven members.

`facts.json`'s rule is also not the rule the sidecar needs, in a way that shapes
the shared abstraction: it rebuilds on an **older** version too, deliberately,
because its version tracks the *derivation rule* rather than the file shape (see
its own `2 → 3` doc comment). Any shared gate must therefore take a policy for
older and a separate policy for newer, or it cannot express the one format that
already works.

### The mechanism is already running in production

`MetaJsonMerger.cs:53,110` three-way-merges a `"description"` key that
`ArtifactEntry` has had no member for since the `description` → `contentPreview`
rename. The merge driver preserves it across a merge; the store deletes it on the
next read-write. The field happens not to matter today. The mechanism is not
hypothetical and needs no new field to demonstrate.

### Why "the next write" is broader than it sounds

`LoadIndexUnsafe` performs read-time forward-fill: when the attribute migration
says an entry's keys moved, it rewrites the sidecar **from inside `LoadIndex`'s
shared lock** (`FileMemoryStore.cs:768`). A plain `search()` is therefore a write.
Any design that gates writes only at the exclusive-lock seam misses this path,
which is the exact path that makes the loss cheap to trigger.

---

## Part B — The design

### B1. One shared version gate

New namespace `Scrinia.Core.Formats`.

```csharp
public enum FormatDisposition
{
    Load,          // proceed normally
    LoadReadOnly,  // readable; writes to the owning unit refuse
    Rebuild,       // discard and re-derive (rebuildable caches only)
    Refuse         // do not load at all
}

public sealed record FormatContract(
    string Name,             // stable id, e.g. "artifact-sidecar"
    int Current,             // the version this build writes
    int? AbsentMeans,        // version implied by a missing field; null = absent is invalid
    FormatDisposition OnOlder,
    FormatDisposition OnNewer);

public static class FormatVersion
{
    public static FormatDisposition Evaluate(FormatContract contract, int? declared);
}
```

`Evaluate` rules, in order:

1. `declared is null` → if `contract.AbsentMeans is null`, return `Refuse`;
   otherwise substitute `AbsentMeans` and continue.
2. `declared == contract.Current` → `Load`.
3. `declared < contract.Current` → `contract.OnOlder`.
4. `declared > contract.Current` → `contract.OnNewer`.

The arithmetic is deliberately trivial. **The value is not the arithmetic — it is
that a durable format cannot exist without answering four questions**, and that
the answers live in one readable table instead of inline at seven read sites.
This is the same consolidation principle applied in Stream A: one shared path with
the rule intrinsic to it.

### B2. The registry

`Scrinia.Core.Formats.DurableFormats` — one `static readonly FormatContract` per
durable format, and a public `All` collection over them.

| Contract | Current | AbsentMeans | OnOlder | OnNewer |
|---|---|---|---|---|
| `ArtifactSidecar` | 1 | 1 | `Load` | `LoadReadOnly` |
| `EntityFacts` | 3 | 0 | `Rebuild` | `Rebuild` |
| `Nmp2Body` | 2 | `null` | `Refuse` | `Refuse` |

**`BundleManifest.Current` is 2, not 1**, and getting this wrong would have been
the single most likely production break in the stream. `BundleFormatService.WriteManifest`
writes `hasFileEntities ? 2 : 1` — this build already emits version 2 whenever a
bundle carries disk-based file entities. A contract claiming `Current: 1` with
`OnNewer: Refuse` would make scri refuse bundles it had just exported itself.
The 1/2 pair is really a **capability marker** — 2 means "carries file entities" —
which happens to be monotonic, so a linear contract models it correctly. Anything
above 2 is genuinely from a newer build and is refused.

**`LegacyIndex.AbsentMeans` is 3, and it is unreachable.** `IndexFile.Version` is
a non-nullable `int` with a property initializer `= 3`, so an `index.json` with no
version key deserializes to 3 rather than to absent. `AbsentMeans` can never fire
for this format. It is set to 3 so the contract states something true; the
unreachability is recorded here rather than left for a reader to rediscover.

`EntityFacts.AbsentMeans` is **0, not `null`**, and this is load-bearing rather
than cosmetic. `EntityFactSidecar.Dto.Version` is a non-nullable `int`, so a
sidecar with no `"version"` key deserializes to `0`, which today falls into
`dto.Version != Version` and rebuilds. Setting `AbsentMeans = 0` routes absent
through `OnOlder` → `Rebuild` and reproduces that exactly. `AbsentMeans = null`
would return `Refuse`, which happens to have the same observable effect for a
cache but states a different contract and would make D14 false as written.
| `BundleManifest` | **2** | 1 | `Load` | `Refuse` |
| `LegacyIndex` | 3 | **3** | `Load` | `Load` |
| `SkillMeta` | 1 | 1 | `Load` | `LoadReadOnly` |
| `AgentMeta` | 1 | 1 | `Load` | `LoadReadOnly` |

`config.json` is deliberately absent: it is a free-form `Dictionary<string,string>`,
so unknown keys already survive a round-trip and there is no schema to version.
This exclusion is recorded here so a later reader does not read its absence as an
oversight.

### B3. `ArtifactEntry` gains two members

Verified against .NET 10 System.Text.Json source generation before adopting
(scratch probe, not assumed):

```
[JsonExtensionData] on a positional record
    { get; init; }  →  InvalidOperationException: "the extension data property
                       cannot bind with a parameter in the deserialization constructor"
    { get; set; }   →  works
```

So:

```csharp
public sealed record ArtifactEntry(
    /* ...13 existing members, unchanged order... */
    [property: JsonPropertyName("agentKeywords")] string[]? AgentKeywords = null,
    [property: JsonPropertyName("v")] int? SchemaVersion = null)
{
    /// Unknown keys from a sidecar this build does not understand, preserved verbatim so a
    /// read-write cycle cannot delete them. MUTABLE by necessity: System.Text.Json refuses an
    /// init-only extension-data property on a record with a parameterized constructor. It
    /// therefore carries the same equality footgun already documented for _legacyFields —
    /// ArtifactEntry equality is not value-semantic and nothing relies on it.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
```

`SchemaVersion` is a trailing optional positional parameter. There are exactly two
positional construction sites in `src/` (`ScriniaCommands.Bundle.cs:198`,
`FileMemoryStore.cs:1758`) and 151 in `tests/`; every one relies on trailing
defaults, so none breaks.

**`SchemaVersion` is `int?` and is written as `null` while `Current == 1`.** The
context already sets `DefaultIgnoreCondition = WhenWritingNull`, so a v1 sidecar
emits no `"v"` key and is byte-identical to one written before this change.

This is a requirement, not an accident:

- Writing `"v": 1` into every sidecar would rewrite all 48 sidecars in this repo
  and every entry in every user's store, pushing a whole-store diff through the
  merge driver and burning writes for zero information.
- `absent ≡ 1` is the contract either way, so the explicit key adds nothing until
  something actually bumps the version.
- When `Current` becomes 2, v2 builds write `"v": 2`; v1 builds read `2 > 1` and
  go read-only. The gate works from the first bump without a migration file.

### B4. Read path, and the hazard in it

`FileMemoryStore.ReadSidecarRaw` (`:882`) today ends:

```csharp
try { /* read + deserialize */ }
catch { /* skip corrupt sidecar */ return null; }
```

**A version refusal must not be evaluated inside that block.** If it throws there,
a newer sidecar is silently dropped from the index — which is Critical C1's
failure class, rebuilt by hand inside the fix for C2. The gate is evaluated after
the try/catch, on the successfully-deserialized entry.

**`ReadSidecarRaw` keeps its `ArtifactEntry?` signature.** An earlier draft of this
spec had it return the entry *plus* a disposition, threaded outward. That is
rejected: `ArtifactSidecar.OnNewer` is `LoadReadOnly`, never `Refuse`, so the
disposition has exactly one non-`Load` value, every consumer would derive it
identically, and it is already recoverable from a field on the entry itself.
Threading it through three call layers — `LoadEntriesFromSidecarsRaw`,
`LoadEntriesFromSidecars`, `EnumerateRawForMigration` — two of which do not care,
is ceremony that buys nothing.

Instead, one helper owns the question:

```csharp
// Scrinia.Core.Formats
public static bool IsReadOnly(ArtifactEntry entry) =>
    FormatVersion.Evaluate(DurableFormats.ArtifactSidecar, entry.SchemaVersion)
        == FormatDisposition.LoadReadOnly;
```

The gate is still evaluated **outside** `ReadSidecarRaw`'s `catch` — nothing in the
read path may throw on a version — and a `LoadReadOnly` entry **is still loaded**,
because read-only means readable.

Corrupt sidecars keep their current behaviour exactly: skipped, not refused.

**Read-time forward-fill must skip the persist, not catch its failure.**
`LoadIndexUnsafe`'s write-back (`:768`) is wrapped in
`catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`.
`ScopeReadOnlyException` is neither, so a level-1 refusal thrown from inside that
loop would **propagate out of a plain read** and break `search()` on any scope
holding a newer sidecar — turning a data-preservation fix into a data-availability
outage. Widening the catch is the wrong repair: it would mean attempting a write
already known to be refused and then swallowing the result. The loop checks
`IsReadOnly(entry)` first and skips the `WriteSidecar` call, keeping the in-memory
upgrade (`entries[i] = upgraded`), which is what makes the current read correct.
The entry simply stays un-migrated on disk, which is the intended outcome.

### B5. Write refusal, at two levels

One level is not enough, because two different paths write sidecars and only one
of them holds the exclusive lock.

**Level 1 — `WriteSidecarCore` (`:987`), the unconditional backstop.** All eight
sidecar write call sites funnel through it. It refuses any entry whose
`SchemaVersion > ArtifactSidecar.Current`, throwing
`Scrinia.Core.Formats.ScopeReadOnlyException` — the same type level 2 throws, so
a caller has one type to catch regardless of which level fired.

This is a **backstop, and after B4 it should be unreachable from any enumerated
path**: forward-fill now checks `IsReadOnly` before calling `WriteSidecar`, and
level 2 stops the ordinary write verbs earlier still. It exists for the path
nobody enumerated — which is the whole lesson of C1, where the sweep was closed
and the mechanism was not. A test asserts it fires when called directly.

**Level 2 — the gate sits where the entries already are.** An earlier draft put a
`AssertScopeWritableUnsafe` inside `WithExclusiveLock`, loading the scope's index
to decide. **That is rejected**, on three separate grounds, each fatal:

- `Upsert` carries an explicit comment forbidding exactly that read: *"Never
  re-read the scope's sidecars here: that made a single write cost a full
  directory enumeration plus one read and JSON parse per entry, so writing N
  memories cost O(N^2) reads (finding H5)."* The gate would have re-introduced,
  verbatim, a defect a previous review already fixed.
- `LoadIndexUnsafe` is **not read-only** — it performs the forward-fill
  write-back. Calling it from the write path would run sidecar rewrites on every
  write, where they previously ran only under a read's shared lock.
- `PersistMigratedEntry` exists specifically to write to an *explicit* `storeDir`
  that differs from `GetStoreDirForScope(scope)`. A scope-resolved gate would
  inspect a different directory than the one being written.

So the gate goes where the entries are already in hand, at zero additional I/O:

- **`WriteSidecars(entries, storeDir)`** — pre-checks the whole list *before*
  writing any of it. This is the one that matters: it is the whole-list publish
  path, and a mid-loop refusal would leave a partial write, which is the failure
  class Stream A just closed.
- **`Upsert`** — checks its single entry.
- **`WriteSidecarCore`** — the unconditional backstop, below both.

**What this means for blast radius.** A write refuses when it would *touch* a
sidecar this build may not rewrite — not merely because a sibling in the same
scope is newer. Remembering an unrelated memory into that scope succeeds, because
it genuinely touches nothing the newer build wrote. This is narrower than "the
whole topic scope refuses writes", and the narrowing is deliberate: the wider rule
bought no additional data safety and cost an index read per write.

**Deletion is allowed.** `forget` / `Remove` deletes a sidecar rather than
rewriting one, and deleting a memory the user explicitly named loses nothing the
newer build wrote *unexpectedly* — it does exactly what was asked. Refusing it
would leave a user unable to clean up a scope until they upgrade, with no
data-safety payoff.

Reads are unaffected: `search`, `recall`, `list` and `facts` work normally, with
unknown fields intact.

**Body writes are deliberately NOT gated, and this is safe only because of §B7.**
`WriteArtifactAsync`, `DeleteArtifact` and `ArchiveVersion` mutate `.nmp2` bodies
without passing through `WithExclusiveLock` at all. Gating them is unnecessary:
under §B7 an overwrite of a v2 memory inherits v2 and is refused at `Upsert`,
and `StoreAsync` already restores the previous body when `Upsert` throws — a
rollback path the file documents as existing precisely to stop content and
metadata diverging. `ArchiveVersion` copies `.meta.json` **bytes** directly rather
than re-serializing, so it loses no fields and needs no gate; the claim "every
sidecar write funnels through `WriteSidecarCore`" is therefore true of every
write that *serializes*, and not of that byte copy.

### B7. Inheritance on overwrite — without this, the bucket preserves nothing

A read → write round-trip of the *same object* preserves unknown fields; that is
what the extension bucket does and it is easy to test. **But no ordinary write is
a round-trip of the same object.** `remember` on an existing memory re-derives
everything and assembles a brand-new `ArtifactEntry`, which has an empty bucket
and a null version. Without an explicit inheritance rule, the very first `remember`
after this stream ships deletes exactly the fields it was built to preserve — and
every test that round-trips one object would still pass.

The rule has one home. `FileMemoryStore.BuildDerivedEntry` is the sole assembler
of a re-derived entry, with exactly two callers — `StoreAsync` and `AppendAsync` —
and **both already hold the prior entry**: `StoreAsync` loads `existingEntry` to
carry `CreatedAt` forward, and `AppendAsync` must have it to append at all. So:

```csharp
private ArtifactEntry BuildDerivedEntry(
    string subject, string scope, long originalBytes, int chunkCount,
    DateTimeOffset createdAt, DateTimeOffset? updatedAt, DerivedMetadata derived,
    ArtifactEntry? prior)          // ← new, and REQUIRED, not optional
=> AttributeMigration.Forward(new ArtifactEntry(
       /* ...existing named arguments, unchanged... */
       AgentKeywords: derived.AgentKeywords,
       SchemaVersion: prior?.SchemaVersion)
   { Unknown = prior?.Unknown });
```

`prior` is deliberately **not** given a default. An optional parameter would let a
future caller omit it and silently resume deleting unknown fields; a required one
forces every new write path to answer the question. This is the same reasoning that
kept a `bool sweepOrphans` flag out of Stream A.

Paths that rebuild an entry with `with` — `TryUpdateEntry`, the CLI's
`consolidate`, `BundleFormatService`'s `ChunkCount` adjustment — already carry both
members forward automatically, because `with` copies every member it does not name.
Those need no change, and that is a property to verify rather than assume.

**Note what inheritance implies for §B5.** An overwrite of a v2 memory inherits
`SchemaVersion = 2`, so `Upsert`'s check refuses it. That is the mechanism by which
a per-entry gate protects a newer memory from being clobbered by an old build —
the refusal follows the *memory*, not the scope.

### B6. Per-format application

- **`facts.json`** — replace the inline `dto.Version != Version` with
  `FormatVersion.Evaluate(DurableFormats.EntityFacts, dto.Version)`. Behaviour is
  identical by construction; the rule moves into the registry where it is visible.
- **Bundle manifest** — start reading `Version`. `Refuse` on newer aborts the
  import before any write, matching how `BundleFormatService` already refuses an
  unreadable index. A bundle whose `BundleIndex` contains an entry with
  `v > Current` is refused on the same grounds.
- **Legacy `index.json`** — register the contract and cover it with a test. It is
  read-only migration input; the contract is documentary, and that is stated
  rather than implied.
- **`SkillFileMeta` / `AgentFileMeta`** — add `[JsonPropertyName("v")] int?
  SchemaVersion` and a `[JsonExtensionData]` bucket, same shape as the artifact
  sidecar, gated through `ReadSidecarMeta` / `WriteSidecarMeta`.
- **NMP/2** — **deliberately not rerouted.** Its sentinel check is
  `Nmp2Strategy.CanDecode` (`:205-207`), which requires **both** the `"NMP/2 "`
  prefix **and** an `"NMP/END"` marker — a test fixture carrying only the header
  is rejected, so any conformance test must include the footer. (An earlier draft
  of this spec cited `:167`, which is a `continue` inside the Base64 collection
  loop, not enforcement.) It already enforces the contract correctly, and it is a
  hot, heavily-tested encoder. It registers its contract and gets a conformance
  test asserting the parser's behaviour matches the declared policy. Rewriting a
  working parser to route through a gate that would produce the same answer buys
  nothing and risks a path with no current defects.

---

## Part C — Rejected alternatives

**C1. Version field only, no extension bucket.** Rejected: the version tells an
old build that its semantics may be invalid, but does nothing to stop it deleting
the bytes. `"description"` is being deleted today by a build that would happily
report the correct version. The bucket is what preserves; the version is what
refuses. The review's "add both, not either" is correct.

**C2. Extension bucket only, no version field.** Rejected: preserving unknown
bytes does not tell a reader that its *understanding of the known fields* is
still valid. A future v2 that changes the meaning of an existing field — not its
name — round-trips perfectly through a bucket and is still wrong.

**C3. Write `"v": 1` explicitly on every sidecar.** Rejected: see B3. It rewrites
every sidecar in every store for no information, and the review that raised C2
also measured this store writing 86,790 bytes to store 1,302.

**C4. Process-wide read-only on any newer sidecar.** Rejected in chat: one stale
checkout of one topic would block unrelated work.

**C5. Per-entry read-only.** Rejected in chat: a topic's index and `facts.json`
span all of its entries, so a partially-writable topic is a state the store has
no other example of and no tests for.

**C6. Reroute the NMP/2 parser through the gate.** Rejected: see B6.

**C7. One policy knob instead of `OnOlder` + `OnNewer`.** Rejected: `facts.json`,
the only format whose gate currently works, rebuilds on older *and* newer. A
single knob cannot express it, so the shared gate would have to exclude the one
format that proves the pattern.

---

## Part D — Verification

Each of these is a required test, not a suggestion.

**The gate**
1. Table test over every contract in `DurableFormats.All` × {absent, older, equal,
   newer}, asserting the disposition matrix above.
2. `AbsentMeans is null` + absent version → `Refuse`.

**Inheritance on overwrite (§B7) — the tests that stop this stream being decorative**
3a. A memory with an unknown key, **re-stored with entirely new content**, still
    carries the unknown key afterwards. A round-trip test cannot substitute for
    this: it exercises one object, and `remember` builds a new one.
3b. The same for `append`.
3c. Re-storing a memory whose sidecar declares `v: 2` is **refused**, because the
    rebuilt entry inherits version 2. This is the mechanism that protects a newer
    memory from an older build, and it is what makes §B5's per-entry gate
    sufficient in place of a whole-scope lockout.
3d. After that refusal, the memory's **body on disk is unchanged** — `StoreAsync`'s
    rollback put the previous content back, so content and metadata still agree.
    This is the property that makes leaving body writes ungated safe.
3e. `BuildDerivedEntry`'s `prior` parameter is required, not optional — a compile
    check, asserted by the absence of a default in the signature.

**Round-trip preservation**
3. A sidecar carrying an unknown key survives read → write with the key intact.
4. Specifically `"description"` — the live `MetaJsonMerger` instance — survives.
5. Unknown keys are sorted on write, alongside the existing `Keywords` /
   `TermFrequencies` / `FileHashes` sorting in `WriteSidecarCore`'s `sorted`
   expression, so git diffs stay stable.

**No churn**
6. An entry with `SchemaVersion == null` serializes with **no** `"v"` key.
7. A sidecar written before this change and one written after are byte-identical
   for the same entry.

**Read-only enforcement**
8. Re-storing the `v: 2` memory itself refuses; the exception names the memory and
   both versions.
8a. `WriteSidecars` refuses a **list** containing one newer entry **before writing
   any of it** — assert a writable sibling in that same list was not written. A
   mid-loop refusal leaving a partial write is the failure class Stream A closed.
8b. `forget` on the `v: 2` memory **succeeds** — deletion is allowed.
9. Remembering an *unrelated* memory into the same scope succeeds, and a
   *different* scope writes normally.
9a. Writing N memories into one scope performs no per-write index read, guarding
   against re-introducing finding H5's O(N²).
10. `search` and `recall` still return the `v: 2` memory, with its unknown fields
    present.
11. **Read-time forward-fill does not rewrite a `v: 2` sidecar** — the file's bytes
    are unchanged after a `search()` that triggers attribute migration on its
    siblings.
11a. **That same `search()` does not throw.** The sidecar must be legacy-shaped so
    `AttributeMigration.MigratesTo` returns true, which is the only condition under
    which the forward-fill loop reaches a write at all. Without that setup the test
    passes vacuously and proves nothing — a `v: 2` sidecar in modern shape never
    enters the branch. This is the regression guard for the `catch (… IOException
    or UnauthorizedAccessException)` filter that `ScopeReadOnlyException` would
    otherwise escape.
12. **A `v: 2` sidecar is not silently dropped from the index** — it appears in
    `LoadIndex`, proving the gate is outside `ReadSidecarRaw`'s catch. This is the
    C1-class regression test.

**Unchanged behaviour**
13. A genuinely corrupt sidecar is still skipped, not refused.
14. `facts.json` still rebuilds on both an older and a newer version, exactly as
    before the refactor.

**Registry completeness**
15. Every `FormatContract` in `DurableFormats.All` has a distinct `Name`, and a
    test asserts the count, so adding a durable format without registering it
    fails a test rather than passing silently.

---

## Part F — Facts checked against the code, not assumed

An adversarial pass over this spec and its plan found seven wrong claims and
eleven hazards before any implementer ran. The corrections are folded in above;
these are the remaining facts an implementer will otherwise get wrong.

- **`ArtifactEntry` appears in four `JsonSerializerContext`s**, not two:
  `FileStoreJsonContext`, `ScriniaMcpJsonContext` (`MemoryTools.Skills.cs:39`),
  `BundleJsonContext` (transitively, via `BundleIndex`), and the tests' `TestCtx`.
  Source generation regenerates all four from the type, so none *drops* the new
  members — but `ScriniaMcpJsonContext`'s options differ, so the no-churn check
  must cover it.
- **Any test of the extension bucket must use a source-generated context.** A
  hand-built reflection `JsonSerializerOptions` does not exercise the
  `{ get; init; }` failure that makes this design's `{ get; set; }` mandatory, so
  a reflection-based test would let the whole constraint ship untested.
  `ArtifactEntrySerializationTests.TestCtx` is the existing source-gen fixture.
- **`CachedIndex` is constructed at five sites**, not three, and its constructor is
  **expression-bodied** — it must be converted to a block before anything can be
  added. The conclusion that no call site changes still holds.
- **`ReadSidecarMeta<T>` catches only `JsonException`**, unlike `ReadSidecarRaw`'s
  blanket catch, so an `IOException` already escapes it today. The skill/agent
  gate must not add a throw there.
- **`BundleLegacyKeys.NormalizeIndex` mutates the raw `JsonObject` before
  deserialization.** Once the bucket exists, any pre-Merge-3 key it does not
  rewrite will be captured and then written into the user's store on import —
  new behaviour, and it needs a decision at implementation time rather than a
  surprise.
- **`TestHelpers.StoreScope`** (`tests/Scrinia.Tests/TestHelpers.cs:97`) is the
  existing temp-workspace helper, exposing `WorkspaceDir` and `TempDir`. Do not
  write a new one; the two `TempWorkspace` types in the migration tests are
  private nested classes and are not reusable.
- **There is no `FileMemoryStore.Store(...)`.** Writes are
  `await StoreAsync(new MemoryWrite(Name, Scope, Content))` — note **Name first,
  then Scope**.

## Part E — Explicitly out of scope

- **`WriteSidecarMeta`'s non-atomic write.** `MemoryTools.Skills.cs:114` writes
  skill and agent sidecars with a bare `File.WriteAllText`, with no temp-file +
  `File.Move`, unlike `WriteSidecarCore`. That is a real defect of the same family
  as Stream A, found while enumerating for this stream. It is recorded here and
  deferred: fixing it is an atomicity change, not a versioning change, and mixing
  the two would make both harder to review.
- **Bumping any format to version 2.** This stream ships the gate at v1 for every
  format. The first real bump is a separate change that the gate exists to make
  cheap.
- **Streams C–F** of the design review.
