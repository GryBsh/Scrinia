# Derivation and Merge Residuals — Design

**Date:** 2026-08-13
**Status:** approved, not yet planned

## Goal

Close the five residuals recorded when `store-owned-derivation` merged (4170858), plus two further
instances of the same two bug classes found while grounding them.

The branch had one thesis — *the store owns all derivation, and one implementation serves every
path* — and it collapsed four derivation implementations into `MemoryDerivation`. These residuals
are the places that thesis has not reached: two `.meta.json` mergers that still implement most of
their rules independently, one derivation entry point missing an input the other has, and two write
seams that predate ephemeral memories and silently drop what they cannot represent.

## Provenance

Every defect below was verified against `4170858` before this spec was written. Line numbers are
from that commit.

| # | Defect | Recorded? |
|---|---|---|
| A1 | `MetaJsonMerger` never merges `termFrequencies` — it preserves *ours* and silently drops *theirs* on a git merge | yes |
| A2 | `reconcile()`'s auto-resolver has no Jaccard admission gate; the merge driver does | yes |
| A3 | `reconcile()` can move `createdAt` **forward**; the driver deliberately keeps the earliest. The two also compare timestamps by different methods | **no — found while grounding** |
| A4 | `reconcile()`'s term-frequency union requires *both* sides to carry a map, so a one-sided map is dropped whole | **no — found while grounding** |
| B1 | `MemoryDerivation.ForAppend` takes no `fileHasher`, so a file first referenced by an appended chunk never gets a drift baseline | yes |
| C1 | `FileMemoryStore.CopyMemory` drops `Keywords`, `TermFrequencies`, `AgentKeywords` and `UpdatedAt` on **both** cross-kind directions | yes (understated — recorded as "ephemeral holes") |
| C2 | `compact()` on an existing ephemeral returns "not found" | yes (misrecorded — recorded as "silent no-op") |

## Global Constraints

- `src/Scrinia.Merge/Scrinia.Merge.csproj` must not gain a `ProjectReference`. It is a standalone
  AOT executable, and that is a hard constraint.
- `TreatWarningsAsErrors=true` for every project not ending in `.Tests` or `.Benchmarks`.
- No mocking frameworks. Hand-written test doubles only (`tests/Scrinia.TestDoubles`).
- Behavioural questions about derivation are governed by
  `docs/superpowers/analysis/2026-08-11-derivation-inventory.md` (rulings D-01…D-29). Where this
  spec changes derivation behaviour it says so explicitly and gives the reason.

---

## Section A — every sidecar field rule becomes shared

### The problem

Two places merge `.meta.json` sidecars, reaching the same files by different routes:

- `Scrinia.Merge.MetaJsonMerger.Merge` — the git merge driver. A **3-way** merge with an ancestor.
- `Scrinia.Mcp.MemoryTools.Lifecycle.TryComputeAutoResolvedMetaJson` — `reconcile()`'s
  auto-resolver. A **2-way** resolve of already-conflicted text, with no ancestor available.

Exactly one field rule is shared between them: `Scrinia.Core.Merge.SidecarKeywordMerge`, extracted
on the previous branch after the two had already drifted on `agentKeywords`. Every other rule is
two independent implementations, and three of them have already diverged:

| Rule | Merge driver | `reconcile()` | Consequence |
|---|---|---|---|
| `termFrequencies` | not merged at all — *ours* preserved verbatim (`MetaJsonMerger.cs:67-68`) | max-per-key union, but only when **both** sides carry a map (`MemoryTools.Lifecycle.cs:481-497`) | a git merge silently discards theirs' contribution; a reconcile drops a one-sided map |
| Jaccard admission | refuses below `config.JaccardThreshold` (`MetaJsonMerger.cs:23-25`) | **no gate** | reconcile auto-merges two sidecars with nothing in common and reports a clean resolve |
| timestamps | `string.Compare(…, Ordinal)`; latest `updatedAt`, **earliest** `createdAt` (`:62-65`, `:131-137`) | `DateTimeOffset.TryParse`; picks one node whole by `updatedAt`, never touches `createdAt` (`:447-461`) | an auto-reconcile can move a memory's creation time forward; ordinal comparison is wrong across offsets |

The ordinal comparison is independently wrong: `2026-08-13T10:00:00+10:00` and
`2026-08-13T00:00:00Z` are the same instant and do not compare equal as strings.

### The design

`Scrinia.Core.Merge` becomes the home of every **field-level** rule. Three types join
`SidecarKeywordMerge`:

**`SidecarTermFrequencyMerge.Union(IReadOnlyDictionary<string,int>? ours, IReadOnlyDictionary<string,int>? theirs)`**
→ `Dictionary<string,int>?`. Union of keys; `Math.Max` on shared keys. A null side is not a veto —
it contributes nothing and the other side survives whole. Returns null only when both sides are
null. This is what closes A4.

**`SidecarAdmission.IsSimilarEnough(string[]? oursKeywords, string[]? theirsKeywords, double threshold)`**
→ `bool`. Wraps the Jaccard computation. `KeywordJaccard` moves from `Scrinia.Merge` to
`Scrinia.Core.Merge`; the driver's copy becomes a forced duplicate like the others.

**`SidecarTimestampMerge.Select(string? oursCreated, string? oursUpdated, string? theirsCreated, string? theirsUpdated)`**
→ `(string? CreatedAt, string? UpdatedAt)`. Latest `updatedAt` wins; **earliest `createdAt` wins**.
Both compared as parsed `DateTimeOffset`, so instants compare as instants. A value that will not
parse is treated as absent for comparison, and the other side's value is taken; when neither parses,
*ours* is preserved, so an unparseable timestamp is never invented or destroyed.

Each type is force-duplicated into `Scrinia.Merge`, and each duplicate names its Core original in a
comment, exactly as `MetaJsonMerger.cs:38-47` does today. `tests/Scrinia.Merge.Tests/` drives both
copies of each rule through identical cases and fails when they disagree — the same mechanism
`SidecarKeywordMergeParityTests` provides for the keyword rule. That test project's `Scrinia.Core`
reference exists for precisely this purpose and is not a violation of the AOT constraint, which
binds `Scrinia.Merge` itself.

### What stays split, and why

Orchestration is **not** unified, and this is deliberate. A 3-way merge with an ancestor can
distinguish "only one side changed this field" from "both sides changed it"; a 2-way resolve of
conflicted text cannot, because git has already discarded the ancestor by the time `reconcile()`
sees the file. `MetaJsonMerger`'s per-field `TryMergeField` (`:122-129`) has no counterpart in
`reconcile()` and must not grow one. After this change the two mergers can differ only in
orchestration, and every value-level answer comes from one implementation.

### Callers after the change

`MetaJsonMerger.Merge` keeps its ancestor-based scalar merge and its Jaccard gate, but sources the
gate, the keyword union, the timestamps and — newly — the term-frequency union from the shared
rules. `termFrequencies` stops being an "unknown property preserved verbatim" and becomes a merged
field; the comment at `:67-68` that lists it among the preserved-verbatim keys must be corrected,
not left to mislead the next reader.

`TryComputeAutoResolvedMetaJson` sources the same four rules, and **gains the admission gate**.

### Behaviour change: reconcile can now refuse

Below the threshold, `reconcile()` stops auto-resolving and reports the memory as a conflict. This
is an outcome the tool already produces for anything it cannot resolve (`MemoryTools.Lifecycle.cs:125`
falls through to the conflict path when the auto-resolve returns false), so no response shape
changes and no caller needs updating. Some conflicts that resolve automatically today will require
manual resolution — which is the point: they were being merged on no evidence of similarity.

The threshold is read from `merge.config`, by the same mechanism `ReadConflictDirName`
(`MemoryTools.Lifecycle.cs:396-404`) already uses to read `conflictDir` from that file, with the
same `0.7` default `MergeConfig.Load` applies. A missing or unparseable config yields the default,
matching the reader's existing broad-catch behaviour rather than introducing a second policy.

---

## Section B — append's drift baseline

### The problem

`MemoryDerivation.ForAppend` (`:177-182`) has no `fileHasher` parameter, while `ForStore`
(`:70-76`) does. Its entire file-hash implementation is one line:

```csharp
var fileHashes = existing?.FileHashes;   // MemoryDerivation.cs:250
```

The carried baseline is preserved — that was D-21's data-loss fix — but nothing is ever added to it.
An appended chunk that references a file for the first time puts that path into the searchable
`file` attribute through the carrier (`:257-261`) while the drift baseline never learns of it.
`list(mode:"drift")` then reports that file clean forever, because it has no recorded hash to
compare against.

### The design

`ForAppend` gains `Func<string,string?>? fileHasher`, matching `ForStore`'s parameter in name and
type, added as the **last** parameter so no existing positional argument rebinds. `ForAppend`'s one
call site passes three positional arguments before `existing:`, and the previous branch's riskiest
task was a positional-rebinding hazard of exactly this shape — appending is the safe direction.

**The rule: hash only `file` values that have no entry in the carried baseline; never re-hash an
existing entry.** Re-hashing on every append would reset the baseline to the file's *current*
content each time, which destroys the drift signal the baseline exists to provide — a file could
never be reported as drifted, because the last append would have re-baselined it. Preserving
existing entries keeps the comparison anchored to the write that first referenced the file.

A path present in `file` but absent from the baseline because it was unhashable at store time
(missing file, no local workspace) is hashed here if it now resolves. That is correct: it was never
baselined, and this is the first opportunity to do so.

`FileMemoryStore.cs:1318` passes `HashWorkspaceFile`. That is `ForAppend`'s only call site, and the
server's append routes through the same store, so local and remote inherit the fix together. No
second change and no wire change; parity holds by construction.

---

## Section C — ephemerals at pre-ephemeral seams

### C-a. `EphemeralEntry` gains `FileHashes`

`ArtifactEntry` carries `FileHashes` (persisted under the legacy JSON key `codeRefs`);
`EphemeralEntry` does not. `EphemeralEntry` gains it with the same member name and the same
`[property: JsonPropertyName("codeRefs")]` mapping, and `PersistEphemeral` writes it through.

### C-b. `HttpMemoryStore` gets a real workspace root — and this must land with C-a

`HttpMemoryStore`'s constructor creates a throwaway root and hands it to the helper store that
services in-process ephemeral writes:

```csharp
_tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-http-{Guid.NewGuid():N}");
Directory.CreateDirectory(_tempDir);
_localHelper = new FileMemoryStore(_tempDir);        // HttpMemoryStore.cs:241-243
```

The comment above it asserts that "an ephemeral write returns from `FileMemoryStore.StoreAsync`
before any path is resolved." **That is false.** `StoreAsync` calls
`ForStore(…, fileHasher: HashWorkspaceFile)` at `FileMemoryStore.cs:1167-1170` and only branches on
`ephemeral` at `:1178`. Hashing has already run, resolved against `_tempDir`, and returned null for
every reference. It is invisible today only because `PersistEphemeral` discards the result for want
of a field to put it in.

So C-a converts a discarded wrong answer into a **stored** wrong answer: every remote ephemeral
write would persist a baseline recording each referenced file as unhashable. The two must land
together.

`HttpMemoryStore` gains an optional workspace-root parameter. Its only production construction site
is `ScriniaCommands.Serve.cs:62`, in the same method whose local branch builds a `FileMemoryStore`
from an already-resolved root — so the correct value is in scope at the one place it is needed.
Omitting the parameter preserves today's behaviour, which keeps every existing test construction
compiling and unchanged.

The stale comment at `HttpMemoryStore.cs:237-240` is corrected as part of this work. A comment that
asserts a safety property the code does not have is how this defect stayed hidden.

### C-c. `CopyMemory` carries metadata on both cross-kind directions

Both cross-kind branches build a fresh entry and populate a subset of its fields:

- **Ephemeral → persistent (promotion)**, `FileMemoryStore.cs:1894-1902` — sets `Name`, `Uri`,
  `OriginalBytes`, `ChunkCount`, `CreatedAt`, `ContentPreview`, `ChunkEntries`, `Attributes`.
- **Persistent → ephemeral**, `:1952-1960` — the same subset.

Both drop `Keywords`, `TermFrequencies`, `AgentKeywords` and `UpdatedAt`, all four of which exist on
both record types. `TermFrequencies` is what BM25 scores against, so a promoted memory keeps its
bytes, preview and attributes and becomes **unfindable by search**.

Both branches carry all four, plus `FileHashes` once C-a lands.

**Copy carries; it does not re-derive.** The artifact bytes are identical by definition, so the
derived metadata is identical — carrying it is correct by construction and cheaper. Re-deriving
would re-hash files against the current workspace and could produce an entry that differs from its
byte-identical source, which is the wrong answer for an operation named `copy`.

Ephemeral → ephemeral (`:1877`) already copies the whole record with `entry with { Name = dstKey }`
and needs no change. It is the shape the other two branches should have had.

### C-d. `compact()` supports ephemerals

`Compact` (`MemoryTools.Lifecycle.cs:532`) never checks `IsEphemeral`. It calls
`store.ReadArtifactAsync`, which is disk-only and throws `FileNotFoundException` for a name with no
file (`FileMemoryStore.cs:1464-1469`), and the handler turns that into:

> `Memory '~scratch' not found.` — with a suggestion to run `list()`, which will show it.

Appending to an ephemeral works (`FileMemoryStore.cs:1321-1323`), so multi-chunk ephemerals
genuinely exist and compaction is a reasonable thing to ask for.

When the name is ephemeral, `Compact` reads the artifact and the existing entry from the ephemeral
map instead of from disk and the index. Everything downstream already works: `StoreAsync` handles
ephemeral writes (`:1178-1184`), and no version archive occurs — correct, since ephemerals have no
`versions/` directory and the existing "original archived" message is already conditional on there
having been an indexed entry.

---

## Testing

**Parity tests**, one per new shared rule, driving both the Core original and the AOT duplicate
through identical cases — the mechanism that keeps the forced duplication honest.

**The vacuity guard is mandatory and applies to each new shared rule.** For every parity test,
break the *shared* implementation and confirm the test fails. One-side mutations prove a parity test
catches divergence; they cannot prove it is not passing because both sides are equally empty. On the
previous branch this exact technique was the only thing that exposed a parity case which passed
because both sides returned nothing.

Behavioural tests:

- A git-merge integration test (alongside the existing `GitMergeIntegrationTests`) proving theirs'
  term frequencies survive a real merge — the defect A1 describes, at the level a user meets it.
- A reconcile test proving two low-Jaccard sidecars are refused rather than auto-merged.
- A reconcile test proving `createdAt` does not move forward.
- A reconcile test proving a one-sided `termFrequencies` map survives.
- An append test asserting a file first referenced by the appended chunk gains a baseline hash,
  **and** that a file already in the baseline keeps its original hash after the referenced file's
  content changes on disk. The second assertion is the one that fails if someone "simplifies" the
  rule to re-hash everything.
- `CopyMemory` round-trips in both cross-kind directions, asserting the copy field-for-field rather
  than spot-checking the four fields this spec names — the style the local/remote parity suite
  established, and the reason it caught fields nobody had thought to list.
- `compact()` on a multi-chunk ephemeral, asserting the chunks merge and the entry's metadata
  survives.

## Out of scope

- Unifying the two mergers' orchestration. Explained under Section A; the ancestor asymmetry is real.
- `remote://` artifact addressing. `HttpMemoryStore.ToArtifactEntry` synthesizes an empty `Uri`, and
  making it dereferenceable is a separate, already-identified design.
- Auditing the remaining lifecycle tools (`restore`, `reconcile`, `versions`) for the same disk-only
  assumption `compact()` had. Considered and deliberately deferred: the audit is unscoped, and
  nothing suggests those tools share the defect. If one does, it is a new residual, recorded as such.

## Risks

**The reconcile admission gate is the only user-visible behaviour change.** Workflows that rely on
`reconcile()` auto-resolving dissimilar sidecars will start seeing conflicts. This is a correction,
not a regression, and the threshold is configurable through the `merge.config` the driver already
reads — but it should appear in `CHANGELOG.md` as a behaviour change rather than a bug fix.

**Section C's ordering is load-bearing.** C-a without C-b converts an invisible defect into a
persisted one. A plan that splits them into separate tasks must not allow C-a to merge alone.

**`KeywordJaccard` moving to Core is a namespace change** for an existing type with existing tests
in `Scrinia.Merge.Tests`. The AOT copy keeps the name, so both assemblies will declare a
`KeywordJaccard` — the same collision `MergeConfig` already has, which
`tests/Scrinia.Tests/MergeConfigRoundTripTests.cs:3` resolves with a `using` alias. Any test
referencing both needs the same treatment.
