# Bundle Round-Trip Totality — Design

**Date:** 2026-08-18
**Branch:** `fix/bundle-round-trip-totality`, based on `main` at `403318b`
**Addresses:** finding M4 of the 2026-08-16 design review (`docs/reviews/2026-08-16-design-review.html`)
**Supersedes:** M4's "What" and "Fix" text, in four specifics recorded below

---

## The defect

`BundleFormatService.ExportTopicsToZip` (`:46`) serializes `List<ArtifactEntry>` through the
source-generated `BundleJsonContext` (`:26`, applied at `:80`) — every member, automatically.
`ImportTopicsFromZip` (`:125`) re-reads the same JSON by hand through `JsonDocument`, member by
member, at `:213-291`.

The two halves are maintained independently, and they have drifted. `ArtifactEntry`
(`src/Scrinia.Core/Models/ArtifactEntry.cs`) declares **13 primary-constructor members**. Export
writes all 13. Import reads **7**: `Name`, `OriginalBytes`, `ChunkCount`, `CreatedAt`,
`ContentPreview`, `ChunkEntries`, `Attributes`.

Five members are dropped outright:

| Member | Consequence of loss | Recoverable? |
|---|---|---|
| `AgentKeywords` | Curated-vs-auto keyword provenance | **No** — see below |
| `FileHashes` | `file`-attribute drift-detection baselines | No — baselines are historical |
| `TermFrequencies` | BM25 scoring input | Yes, from content |
| `Keywords` | BM25 keyword matching | Yes, from content |
| `UpdatedAt` | Last-modified history | No |

`AgentKeywords` is the sharpest. Its own documentation (`ArtifactEntry.cs:32-52`) records that the
distinction between caller-supplied and auto-extracted keywords is destroyed by the merge that
produces `Keywords`, that recovering it by subtracting the content's own terms "cannot be made
exact", and that both alternatives were tried on the branch that introduced it and both were wrong.
Losing it on restore is unrepairable by any later rewrite.

A sixth member, `CreatedAt`, is parsed at `BundleFormatService.cs:255-258` and then discarded:
`FileMemoryStore.ImportTopicEntries` (`:2521`) overwrites it at `:2547` with
`DateTimeOffset.UtcNow`. Restoring a backup resets every memory's creation date, and the parse that
looks like it preserves history is dead code.

Nothing repairs any of this downstream. `ImportTopicEntries` writes the entries it is handed and
calls `SaveIndex`; no derivation runs. Seven bundle test files exist
(`tests/Scrinia.Tests/Bundle*.cs`, `tests/Scrinia.Server.Tests/BundleEndpointTests.cs`); none
asserts that any of the six members survives a round trip.

**The shape, not the field list, is the defect.** A member added to `ArtifactEntry` tomorrow
inherits the same fate, with no compiler signal and no failing test. Export/import is the backup
path; silent field loss there is the least acceptable place for it.

### Four corrections to the review's M4

Recorded so the next reader does not re-derive them, and so no implementer "fixes" something that
is already right.

1. **`Uri` is not lost.** The review names it as "the one already lost". `ImportTopicEntries:2546`
   overwrites it with `ArtifactUri(entry.Name, topicScope)` — correctly, because the exporting
   machine's URI is wrong on the importing machine. It is the one member the store already
   repairs. It stays a deliberate, documented exception.
2. **`CreatedAt` is lost, and the review missed it.** See above.
3. **The manifest `Version` field is never read.** `grep` for it returns only the declaration
   (`BundleFormatService.cs:14`) and the two write sites. Import discriminates v1 from v2 by
   whether the file-entity category directories are present, not by the version number. So the
   CLI's literal `1` (`ScriniaCommands.Bundle.cs:233`) is inert today — and since `scri bundle`
   writes no file entities, `1` is in fact the value `BundleFormatService:101` would compute for
   the same input. The defect is the duplicated writer, not a wrong version number.
4. **"Delete the CLI's second writer and call `BundleFormatService`" is not feasible as written.**
   `ExportTopicsToZip` reads entries from the store; `scri bundle` builds entries from loose files
   on disk that were never memories. They are different operations with different inputs. The
   shared unit is one level down — the archive-writing step.

---

## Design

Four changes. Each is independently testable; changes 1–3 are all in the import path and share a
test surface.

### 1. Import deserializes through the context that serializes

`ImportTopicsFromZip` replaces the hand-written member reads at `:213-291` with
`JsonSerializer.Deserialize(node, BundleJsonContext.Default.BundleIndex)`. The round trip becomes
total by construction rather than by vigilance.

The hand-parse is not purely sloppy: it carries deliberate back-compat reads for pre-Merge-3
bundles, and those must survive the rewrite.

- `"description"` → `ContentPreview` (`:220-225`), when `contentPreview` is absent
- top-level `"tags"` array → `Attributes[KnownAttributes.TagKey]` (`:227-252`), when the modern
  `attributes` object carries no `tag` key

**Where legacy handling goes.** Three options were considered:

- *Holder properties on `ArtifactEntry`*, matching the existing `LegacyFields` precedent
  (`ArtifactEntry.cs:55-88`). **Rejected.** `LegacyFields` is store-sidecar legacy, and
  `ArtifactEntry` also serializes into every scope's `index.json`. These two keys are bundle-only
  — confirmed: no `"description"` or `"tags"` back-compat read exists anywhere in
  `src/Scrinia.Core/` outside `BundleFormatService`. Adding them to the model would push
  bundle-format history into a type the store shares.
- *A bundle-specific DTO with an explicit mapping to `ArtifactEntry`.* **Rejected outright.** The
  mapping is hand-written, so a new member silently fails to appear in it. That is the defect
  reintroduced one layer down.
- **A bounded key-migration pre-pass.** *Chosen.* Parse `index.json` into a `JsonNode` DOM; for
  each entry, rename `description` → `contentPreview` and fold `tags` → `attributes.tag`, in both
  cases **only where the modern key is absent**; then deserialize the normalized node through
  `BundleJsonContext`.

The pre-pass touches two keys and knows nothing about the other eleven members, so adding a member
to `ArtifactEntry` requires no edit here. It is also visibly a shim — deletable in one piece when
pre-Merge-3 bundles age out, which the holder-property shape would not be.

**Trim safety.** `Scrinia.Core` is `IsTrimmable`, and `scri` publishes with `PublishTrimmed=true`,
`SuppressTrimAnalysisWarnings=false` and `TreatWarningsAsErrors=true` (`publish.ps1:34-37`).
`System.Text.Json.Nodes` DOM manipulation and `Deserialize(JsonNode, JsonTypeInfo<T>)` are both
trim-safe, so this is expected to pass — and the publish gate is what proves it. Running a trimmed
publish is a required verification step, not an assumption.

**A third legacy path is partly fixed, and the limit matters.** The hand-parse never calls
`ArtifactEntry.NormalizeAttributesKey()`, so a bundle whose index carries the pre-rename `"fields"`
key loses its attributes entirely today. `LegacyFields` is `[JsonInclude]` on the record, so
deserializing through the context picks the key up and the existing normalize seam folds it into
`Attributes`. Call `NormalizeAttributesKey()` on each deserialized entry.

`NormalizeAttributesKey` is a **fallback, not a merge** — `Attributes ?? _legacyFields`
(`ArtifactEntry.cs:85-88`). So `fields` is folded only when nothing else produced an `Attributes`
object, and an entry carrying both `fields` **and** a legacy top-level `tags` array keeps only the
tags-derived attributes: the pre-pass creates `Attributes` from `tags`, and the `??` then discards
the `fields` payload whole. That is not a regression — the hand-parse never read `fields` at all —
but it is not "fixed for free" either, and the two legacy attribute carriers are never merged.
Merging them would be a change to `NormalizeAttributesKey`'s contract, which is shared with the
store's sidecar path; it is deliberately **out of scope** here. Tests must cover the two carriers
separately rather than in one fixture.

**Two defaults the hand-parse supplied that deserialization does not.** The loop being deleted
defaults a missing `chunkCount` to `1` (`:219`) and hard-codes `Uri: ""` (`:285`). Source-generated
deserialization leaves both at `default` — `ChunkCount` becomes `0`, and `Uri` is transiently
`null` despite being declared non-nullable. Modern bundles always carry both keys, so this reaches
only pre-Merge-3 and hand-crafted bundles. `Uri` needs nothing: `ImportTopicEntries` overwrites it
before any reader sees it. `ChunkCount` is restored explicitly to `1`, because `0` is not a value
any reader is prepared for, and pinned by a fixture that omits the key.

### 2. Timestamps survive the import

`ImportTopicEntries` stops overwriting `CreatedAt` (`FileMemoryStore.cs:2547`). It preserves what
the bundle carried, falling back to `DateTimeOffset.UtcNow` only when the bundle carried no
timestamp — which after change 1 presents as `default(DateTimeOffset)`, so the fallback must test
for that explicitly rather than for null. `UpdatedAt` round-trips with every other member and needs
no special handling.

`Uri` continues to be recomputed at `:2546`. That line gains a comment stating why, so it reads as
a decision rather than as the same accident that happened to be right.

**Out of scope, explicitly:** `FileMemoryStore.CopyMemory` also sets
`CreatedAt = DateTimeOffset.UtcNow` (`:2384-2385`). A copy creates a new memory, so that is correct
and must not be changed.

### 3. Derivation fills only what the bundle omits

`scri bundle` packages loose files that were never memories, so its entries carry no `Keywords` and
no `TermFrequencies` — and import derives nothing. Those memories import unsearchable by keyword
until something else rewrites them.

Round-tripping and deriving cover disjoint gaps: round-tripping restores what **cannot** be
re-derived (`AgentKeywords`, `FileHashes`, timestamps); deriving supplies what was **never there**.
So import trusts the bundle where it carries a value and derives only where it does not.

When `Keywords` is null — the marker that the entry was never derived — import decodes the entry's
artifact content and calls `MemoryDerivation.ForStore(content, keywords: entry.AgentKeywords,
fileHasher: null)`, taking `Keywords`, `TermFrequencies` and `ChunkEntries` from the result.
Passing `AgentKeywords` as the raw keyword input keeps provenance coherent when the bundle carried
it, and `ForStore` re-derives it when it did not.

**Null and empty are different facts here**, exactly as they are for `AgentKeywords`. A null
`Keywords` means the entry was never derived and derivation must run. An **empty** `Keywords` array
means derivation ran and produced nothing, which is a real answer — leave it alone. Gating on
`is null` rather than on `is not { Length: > 0 }` is the whole of the distinction, and it is easy
to get wrong.

`fileHasher` is null deliberately: hashing this machine's files against paths captured on the
exporting machine would fabricate a drift baseline that never existed. A bundle's `FileHashes` is
preserved as-is and never recomputed.

### 4. One archive writer

The archive layout has two seams, and both are duplicated today, so `BundleFormatService` gains
**two** internal writers rather than one:

- `WriteTopic(zip, bundleTopic, entries, artifactContents)` — one topic's `index.json` plus its
  `.nmp2` artifact entries. Called once per topic.
- `WriteManifest(zip, topics, totalEntries, fileEntities)` — the manifest, including the version
  rule. Called once per archive.

`ExportTopicsToZip` and `ScriniaCommands.Bundle` (`:211-241`) both call both. Splitting them this
way is what lets the CLI reuse the layout at all: it has one topic and no file entities, while
`ExportTopicsToZip` has many topics and may have file entities, so a single combined method would
need a parameter shape that fits neither caller well.

The CLI's duplicated layout and its literal manifest version go away; the version comes from the
shared writer's rule (`fileEntities is { Count: > 0 } ? 2 : 1`), which for `scri bundle` still
computes 1.

---

## The pin

The load-bearing artifact is a **reflective totality test**, because the field list is not the
defect and a test that names today's fields would go stale exactly as the importer did.

- Enumerate `typeof(ArtifactEntry)`'s primary-constructor parameters.
- Build an entry with **every** member set to a distinctive non-default value.
- Round-trip it through a real zip: export → import → read back from the store.
- Assert each member survives.

`LegacyFields` is deliberately **not** enumerated: it is not a constructor parameter, and its
contract is that it normalizes to null and re-serializes as absent (`ArtifactEntry.cs:55-63`). A
round trip that preserved it would be the bug. The legacy-bundle fixture below is what covers it.

Members legitimately not preserved sit on an **explicit named exception list** — today that is
`Uri` alone, recomputed by the store by design. The default for a newly added member is therefore
*must survive*, and an exception is a deliberate edit someone has to write down and justify.

**The pin must be falsified by mutation before it is trusted.** Remove one field from the writer
and confirm the test goes red. Branch 6's most expensive recurring lesson was tests that passed
while the thing they claimed to pin was broken — two of that branch's own Task 9 tests did exactly
that, one because its fixture was seeded into the scope the bug narrowed to, the other because
single-element inputs could not express the failure.

### Tests beyond the pin

- **Legacy bundle fixture.** A bundle index carrying all three old keys (`description`, top-level
  `tags`, `fields`) imports with preview, tag attributes and attributes intact. This pins the
  back-compat that change 1 must not lose, and it must fail if the pre-pass is removed.
- **Timestamps.** A bundle carrying `createdAt`/`updatedAt` imports with both preserved; a bundle
  carrying neither falls back to now.
- **Derivation.** `scri bundle` of a loose file → `import` → the memory is findable by a keyword
  drawn from its content, and its `AgentKeywords` is not fabricated.
- **No double-derivation.** An entry that arrives with `Keywords` populated keeps exactly those
  keywords; derivation does not run over it.

---

## Scope boundary

In: finding M4 only.

Out, going to branch 7b: **M2** (publish script divergence — and note it is a silent
data-corruption risk on non-Windows installs, since `publish.sh:85`'s `find` deletes the plugins
directory it just published and no non-Windows build ships the merge driver at all), **L1a** (the
read-only store gate has no claim producer and no comment saying so), **L2** (CLI surface
inconsistencies), and the two items carried from branch 6 — the pre-existing Http/File
topic-naming divergence in `ResolveReadScopes`, and Task 5's reflection gate keying on parameter
names.

## Global constraints

- .NET 10, C# — Windows host, PowerShell primary shell.
- `TreatWarningsAsErrors` is on for every project whose name ends in neither `.Tests` nor
  `.Benchmarks` (`Directory.Build.props:44-46`).
- `tests/Scrinia.Tests/GlobalUsings.cs` declares only `global using Xunit;` — every new test file
  needs its own `using FluentAssertions;`.
- Baseline at `403318b`: build green, 2670 passed / 0 failed / 4 skipped.
  `MemoryEventSinkContextInstallDefaultTests.AParallelScopeLifecycleDoesNotDisturbDefault` is a
  known flake — do not chase it.
- The LSP in this workspace emits phantom `CS0535`/`CS1503`/`CS0108` errors during large edits
  while `dotnet build` is green. Trust the build, not the diagnostic stream.
- A trimmed publish must be run to verify change 1's trim safety. It is a gate, not a formality.
- `.scrinia/` changes are part of commits.
