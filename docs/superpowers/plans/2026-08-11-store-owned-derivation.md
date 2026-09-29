# Store-Owned Derivation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make MCP and HTTP effectively the same by moving metadata derivation out of both callers
and into the store, then widening the wire to carry what the single pipeline produces.

**Architecture:** One derivation pipeline in `Scrinia.Core`. A store takes raw inputs
(`MemoryWrite`) and returns a derived `ArtifactEntry` — `FileMemoryStore` derives locally,
`HttpMemoryStore` POSTs and lets the server derive. Callers never touch `TextAnalysis`. The wire
then carries complete entries, a chunk array, and a PATCH verb.

**Tech Stack:** C#/.NET 10, xunit 2.9.3, FluentAssertions 6.12.2, `Xunit.SkippableFact` 1.5.61, no
mocking frameworks (hand-written doubles in `tests/Scrinia.TestDoubles`).

**Spec:** `docs/superpowers/specs/2026-08-11-store-owned-derivation-design.md`

## Global Constraints

- **`cmds.txt` is untracked and pre-existing.** Never `git add .` or `git add -A` — explicit paths
  only. Never edit, move, delete or stage it.
- **`.scrinia/memories/findings/facts.json` shows as modified** — a derived cache from the memory
  server's hook. Never stage, revert, or report it.
- **Nothing is pushed.** Local commits only.
- Prefix every shell command with `rtk`.
- **Verify against `rtk dotnet build Scrinia.sln` only.** The C# language server emits phantom
  `CS0101`/`CS1061`/`CS0234`/`CS1503`/`CS8019` errors, including for files that do not exist on
  disk. Ignore the diagnostic stream.
- **`rtk grep` silently undercounts, and so does `rtk proxy grep … | head -N`.** Use the Grep tool
  wherever completeness matters. This is how an earlier plan missed seven of ten defect sites.
- `TreatWarningsAsErrors=true` for every project not ending in `.Tests`/`.Benchmarks` —
  `Scrinia.Core`, `Scrinia.Mcp`, `Scrinia.Server`, `Scrinia` are all such projects. The gate is
  0 errors.
- **No new deferred-followups.** Anything found is fixed in this branch or raised as an explicit
  decision at the time.
- **Break the wire freely** (spec D4). Nothing is deployed over HTTP; no compatibility shims.
- **One entry shape, always complete** (spec D6). No `?full=true`, no light variant, no field whose
  null means "not requested" in one response and "empty" in another.
- Baseline at branch point: **1874 passed, 0 failed, 4 skipped.** The 4 skips are pre-existing
  embedding-model tests. A rise in the skip count is a failure, not a pass.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `docs/superpowers/analysis/2026-08-11-derivation-inventory.md` | The four-way diff and every keep-or-drop ruling | 1 |
| `src/Scrinia.Core/MemoryDerivation.cs` | **New.** The single pipeline. Pure; no I/O. | 2 |
| `src/Scrinia.Core/Models/MemoryWrite.cs` | **New.** Raw inputs a store accepts. | 3 |
| `src/Scrinia.Core/IMemoryStore.cs` | Gains `StoreAsync`/`AppendAsync` | 3 |
| `src/Scrinia.Core/FileMemoryStore.cs` | Implements them via `MemoryDerivation` | 3 |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | Stops deriving; calls the store | 4 |
| `src/Scrinia.Server/Services/MemoryOrchestrator.cs` | Stops deriving; calls the store | 5 |
| `src/Scrinia.Core/Models/ArtifactWriteMetadata.cs` | **Deleted** — the workaround it was | 5 |
| `src/Scrinia.Server/Models/ApiDtos.cs` | `MemoryEntry`, `ChunkEntryDto`, reshaped `ShowResponse` | 6 |
| `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` | New `/entry` GET and `PATCH` | 7 |
| `src/Scrinia/HttpMemoryStore.cs` | Defers everything; real `TryUpdateEntry` | 8 |
| `tests/Scrinia.Server.Tests/LocalRemoteParityTests.cs` | **New.** The thesis as an assertion | 9 |

---

### Task 1: Inventory the four derivation paths

**Files:**
- Create: `docs/superpowers/analysis/2026-08-11-derivation-inventory.md`

**Interfaces:**
- Consumes: nothing.
- Produces: the ruling table Task 2 implements. **Task 2 cannot start without it.**

This task writes no code. Spec D2 requires that every difference between the four implementations
is recorded as a deliberate keep-or-drop *before* any of them is deleted, because the collapse's
real risk is losing a behaviour only one of them had. Two differences are already known; assume
there are more.

- [ ] **Step 1: Locate every derivation call site**

Use the **Grep tool**, not `rtk grep`:

```
pattern: TextAnalysis\.(AnalyzeText|ComputeChunkEntries|MergeKeywords|MergeKeywordsWithSource|IndexTokensForKeyword|ComputeTermFrequencies|ExtractKeywords)
path: src
```

Known at plan time (verify; lines may have moved):

| Path | MCP `src/Scrinia.Mcp/MemoryTools.Core.cs` | Server `src/Scrinia.Server/Services/MemoryOrchestrator.cs` |
|---|---|---|
| store | `:141`, `:146`, `:204` | `:22`, `:27`, `:68` |
| append | `:1322`, `:1333` | `:269`, `:270` |
| compact | `:1147` | `:283` |

Also sweep for the surrounding pipeline, which is part of derivation even though it is not in
`TextAnalysis`: `AttributeExtractor.RouteKeywords`, `.Extract`, `.ExtractRefAttributes`,
`.ExtractMentionSpans`, `.DropMentionsShadowedByTypedKeys`, `.SeedTermFrequencies`,
`KnownAttributes.Merge`, `KnownAttributes.Normalize`, `ContentPreview.Generate`.

- [ ] **Step 2: Read all six regions and build the diff table**

For each of the six, record in order: which functions are called, with what arguments, in what
sequence, and what TF boost is applied. The known-different cells:

*Append* — MCP (`:1322-1341`) vs server (`:269-275`):

| | MCP | Server |
|---|---|---|
| Agent keywords | curated + caller additions | `null` |
| TF boost | agent `+5`, auto `+2` | everything `+2` |
| Boost target | `IndexTokensForKeyword(kw)` | raw `kw` |

*Store* — the server's own comment at `MemoryOrchestrator.cs:57-66` documents a deliberate
difference: locally `SeedTermFrequencies` runs **before** the carrier merge so carrier keys are
never seeded; on the server the carrier keys arrive in one flat map and get seeded. Read that
comment in full — it is the most accurate existing account of the store-path divergence and it
names the fix.

- [ ] **Step 3: Rule on every difference**

Write the table with a fourth column: **keep / drop, and why**. The default is that the MCP
behaviour wins, because it is the one three of four paths implement and the one the local store's
tests pin — but each row gets an explicit ruling, not a blanket rule. A difference you cannot
explain is a stop-and-ask, not a coin toss.

**Where the ordering difference is concerned** (`SeedTermFrequencies` before vs after the carrier
merge), rule explicitly which order the single pipeline uses, and note that the answer changes
observable BM25 ranking for existing memories either way.

- [ ] **Step 4: Commit**

```bash
rtk git add docs/superpowers/analysis/2026-08-11-derivation-inventory.md
rtk git commit -m "docs(analysis): four-way derivation inventory before the collapse"
```

---

### Task 2: The single derivation pipeline

**Files:**
- Create: `src/Scrinia.Core/MemoryDerivation.cs`
- Test: `tests/Scrinia.Tests/MemoryDerivationTests.cs`

**Interfaces:**
- Consumes: Task 1's ruling table.
- Produces:
  ```csharp
  public static class MemoryDerivation
  {
      public static DerivedMetadata ForStore(string[] content, string? description,
          IReadOnlyDictionary<string, string[]>? attributes, string[]? keywords);

      public static DerivedMetadata ForAppend(string[] fullContent, string? description,
          IReadOnlyDictionary<string, string[]>? existingAttributes, string[]? existingKeywords,
          string[]? newKeywords);
  }

  public sealed record DerivedMetadata(
      string[] Keywords,
      Dictionary<string, int> TermFrequencies,
      Dictionary<string, string[]> Attributes,
      ChunkEntry[]? ChunkEntries,
      string ContentPreview);
  ```
  Task 3 calls these.

- [ ] **Step 1: Write the failing test for the store pipeline**

Create `tests/Scrinia.Tests/MemoryDerivationTests.cs`. The assertions come from Task 1's rulings —
this test encodes them. At minimum:

```csharp
using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public class MemoryDerivationTests
{
    [Fact]
    public void ForStore_BoostsAgentKeywordsAboveAutoKeywords()
    {
        var derived = MemoryDerivation.ForStore(
            content: ["the quick brown fox jumps over the lazy dog"],
            description: null,
            attributes: null,
            keywords: ["vulpine"]);

        // Agent-supplied terms get +5, auto-extracted +2 — the contract three of the four
        // pre-collapse implementations shared. Task 1's inventory is the authority for this
        // number; if it ruled differently, this assertion changes with it.
        derived.TermFrequencies["vulpine"].Should().Be(5);
        derived.Keywords.Should().Contain("vulpine");
    }

    [Fact]
    public void ForStore_ComputesChunkEntriesOnlyForMultiChunkContent()
    {
        MemoryDerivation.ForStore(["one chunk"], null, null, null)
            .ChunkEntries.Should().BeNull(
                because: "single-chunk content carries no per-chunk metadata, matching " +
                         "MemoryTools.Core.cs's `content.Length > 1` guard");

        MemoryDerivation.ForStore(["first chunk", "second chunk"], null, null, null)
            .ChunkEntries.Should().HaveCount(2);
    }

    [Fact]
    public void ForStore_CuratedDescriptionWinsOverTheAutoSnippet()
    {
        MemoryDerivation.ForStore(["body text here"], "curated preview", null, null)
            .ContentPreview.Should().Be("curated preview");
    }

    [Fact]
    public void ForAppend_KeepsExistingCuratedKeywordsAsAgentTerms()
    {
        var derived = MemoryDerivation.ForAppend(
            fullContent: ["original body", "appended body"],
            description: null,
            existingAttributes: null,
            existingKeywords: ["curated"],
            newKeywords: null);

        derived.TermFrequencies["curated"].Should().Be(5,
            because: "the append path must not demote a curated term to auto — the server's " +
                     "pre-collapse append passed null for agent keywords and boosted everything +2");
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MemoryDerivationTests"`
Expected: FAIL to compile — `MemoryDerivation` does not exist.

- [ ] **Step 3: Write the pipeline**

Create `src/Scrinia.Core/MemoryDerivation.cs`. The **server's `StoreAsync` at
`MemoryOrchestrator.cs:19-70` is the most complete of the four** and is the base to port — it
already does keyword routing, ref/mention extraction, the merge chain, the shadow drop and TF
seeding. Port it verbatim, then apply each of Task 1's rulings on top.

Sequence, from that source:

1. `string joined = string.Concat(content);`
2. `var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);`
3. `var (routedAttributes, plainKeywords) = AttributeExtractor.RouteKeywords(keywords);`
4. `var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(plainKeywords, autoKeywords);`
5. Boost: for each merged keyword, `int boost = agentKeywordSet.Contains(kw) ? 5 : 2;` then for
   each `token` in `TextAnalysis.IndexTokensForKeyword(kw)`, `tf[token] += boost`.
6. Attribute merge chain: `routedAttributes` ← `AttributeExtractor.Extract(joined)` ←
   `ExtractRefAttributes(joined)` ← `ExtractMentionSpans(joined)`, via `KnownAttributes.Merge`.
7. Fold agent attributes last so they win scalar collisions:
   `KnownAttributes.Merge(attributes, KnownAttributes.Normalize(attributes_in))`.
8. `AttributeExtractor.DropMentionsShadowedByTypedKeys(attributes)`.
9. `AttributeExtractor.SeedTermFrequencies(attributes, tf)` — **at the position Task 1 ruled**,
   which is the one deliberate ordering choice in the whole pipeline.
10. `ChunkEntry[]? chunkEntries = content.Length > 1 ? TextAnalysis.ComputeChunkEntries(content) : null;`
11. `string preview = !string.IsNullOrWhiteSpace(description) ? description : ContentPreview.Generate(joined);`

`ForAppend` differs only in that its agent keyword set is
`UnionDistinct(existingKeywords, newKeywords)` rather than the routed plain keywords — see
`MemoryTools.Core.cs:1332-1334`. If Task 1 ruled any other difference as "keep", implement it here
and cite the ruling in a comment.

**This class does no I/O and touches no store.** It is pure so both stores can call it and so this
test can exercise it directly.

- [ ] **Step 4: Run and confirm green**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Full build and suite**

`rtk dotnet build Scrinia.sln` (0 errors), `rtk dotnet test Scrinia.sln` — expect **1874 + your new
tests**, 0 failed, 4 skipped. Nothing else changed yet, so any other movement is a real signal.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/MemoryDerivation.cs tests/Scrinia.Tests/MemoryDerivationTests.cs
rtk git commit -m "feat(core): one derivation pipeline, not four"
```

---

### Task 3: The store write surface

**Files:**
- Create: `src/Scrinia.Core/Models/MemoryWrite.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`
- Modify: `tests/Scrinia.TestDoubles/FakeMemoryStore.cs`
- Test: `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs`

**Interfaces:**
- Consumes: `MemoryDerivation.ForStore` / `.ForAppend` and `DerivedMetadata` from Task 2.
- Produces:
  ```csharp
  public sealed record MemoryWrite(
      string Name, string Scope, string[] Content,
      string? Description = null,
      Dictionary<string, string[]>? Attributes = null,
      string[]? Keywords = null);

  // on IMemoryStore:
  Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default);
  Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default);
  ```
  Tasks 4, 5 and 8 call these.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs`. Mirror
`tests/Scrinia.Tests/CaseSensitiveFileStoreTests.cs` for fixture style (temp dir + `FileMemoryStore`
rooted there):

```csharp
[Fact]
public async Task StoreAsync_DerivesMetadataTheCallerDidNotSupply()
{
    using var tmp = new TempWorkspace();
    var entry = await tmp.Store.StoreAsync(new MemoryWrite(
        Name: "derived", Scope: "local",
        Content: ["the quick brown fox", "and a second chunk"],
        Keywords: ["vulpine"]));

    entry.Keywords.Should().Contain("vulpine");
    entry.TermFrequencies.Should().NotBeNullOrEmpty(
        because: "the store derives what the caller did not send — that is the point of the surface");
    entry.ChunkEntries.Should().HaveCount(2);
    entry.ContentPreview.Should().NotBeNullOrWhiteSpace();
    entry.ChunkCount.Should().Be(2);
}

[Fact]
public async Task StoreAsync_PersistsSoTheEntryIsReadableAfterwards()
{
    using var tmp = new TempWorkspace();
    await tmp.Store.StoreAsync(new MemoryWrite("persisted", "local", ["body"]));

    tmp.Store.LoadIndex("local").Should().ContainSingle(e => e.Name == "persisted");
}
```

Add a `TempWorkspace` helper to the file if one is not already reachable — `EntityTypingEventHandlerTests.cs:14-32` has the pattern (temp dir, `FileMemoryStore`, recursive delete on dispose).

- [ ] **Step 2: Run and watch it fail**

Expected: FAIL to compile — `StoreAsync` does not exist on the store.

- [ ] **Step 3: Add `MemoryWrite`**

Create `src/Scrinia.Core/Models/MemoryWrite.cs` with the record from **Interfaces** above and a doc
comment saying what it is: the raw inputs a store derives from, deliberately carrying nothing the
store computes for itself.

- [ ] **Step 4: Declare the members on `IMemoryStore`**

Add to `src/Scrinia.Core/IMemoryStore.cs`, with a doc comment stating the contract: *the store
derives; callers must not.* No default implementations — every store implements both, so a new
store cannot silently inherit a wrong one.

- [ ] **Step 5: Implement on `FileMemoryStore`**

`StoreAsync`: call `MemoryDerivation.ForStore`, encode the artifact
(`Nmp2ChunkedEncoder.Encode` for one chunk, `EncodeChunks` for many — see
`MemoryOrchestrator.cs:120-122` for the existing conditional), `ArchiveVersion` when the entry
already exists, `WriteArtifactAsync`, build the `ArtifactEntry`, `Upsert` it, return it.

`AppendAsync`: read the existing artifact, append the chunk (`Nmp2ChunkedEncoder.AppendChunk`),
call `MemoryDerivation.ForAppend` over the full content, write, upsert, return.

**Reuse the existing case-insensitive entry resolution** — `MemoryOrchestrator.cs:124-127` is the
pattern, and the resolved entry's own casing must travel forward (this codebase fixed twelve sites
of exactly that bug; do not reintroduce one).

- [ ] **Step 6: Add both to `FakeMemoryStore`**

`tests/Scrinia.TestDoubles/FakeMemoryStore.cs` is a deliberate no-op double whose members are all
`virtual`. Add:

```csharp
    public virtual Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default) =>
        Task.FromResult(new ArtifactEntry(write.Name, "", 0, write.Content.Length, DateTimeOffset.UtcNow));
    public virtual Task<ArtifactEntry> AppendAsync(MemoryWrite write, CancellationToken ct = default) =>
        Task.FromResult(new ArtifactEntry(write.Name, "", 0, write.Content.Length, DateTimeOffset.UtcNow));
```

- [ ] **Step 7: Run the new tests, then the suite**

Filter first, then `rtk dotnet build Scrinia.sln` and `rtk dotnet test Scrinia.sln`. Callers are
unchanged so far, so the total should be the previous total plus your new tests.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia.Core/Models/MemoryWrite.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.TestDoubles/FakeMemoryStore.cs tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs
rtk git commit -m "feat(core): stores take raw input and return a derived entry"
```

---

### Task 4: MCP stops deriving

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (store, append and compact paths)
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` (compact's write, if derivation reaches it)

**Interfaces:**
- Consumes: `MemoryWrite`, `IMemoryStore.StoreAsync`/`AppendAsync` from Task 3.
- Produces: nothing new. Behaviour must be unchanged for the local path — this is a refactor, and
  the existing MCP tests are the guard.

- [ ] **Step 1: Confirm the guard exists before changing anything**

Run the MCP suite and record the number:
`rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ScriniaMcpTools"`

These tests pin the behaviour you are about to move. **If any fails after the refactor, that is a
real behavioural change** — report it with the test name rather than adjusting the test.

- [ ] **Step 2: Repoint the store path**

Replace the derivation block (currently around `MemoryTools.Core.cs:141-250`) with: build a
`MemoryWrite` from the validated inputs, `await store.StoreAsync(write, ct)`, and format the
response from the returned entry. Delete the `TextAnalysis` and `AttributeExtractor` calls — they
now live in `MemoryDerivation`.

- [ ] **Step 3: Repoint the append path**

Same, at `:1322-1341` and its surrounding write, using `AppendAsync`.

- [ ] **Step 4: Repoint compact**

Compact rewrites content wholesale, which is `StoreAsync` over the compacted text (spec: no third
derivation entry point). The metadata it must preserve — attributes, keywords, curated description
— travels in the `MemoryWrite`. The existing read-before-write at `MemoryTools.Lifecycle.cs:583-586`
supplies them.

- [ ] **Step 5: Prove no `TextAnalysis` call remains in the MCP project**

Use the **Grep tool**:
```
pattern: TextAnalysis\.
path: src/Scrinia.Mcp
```
Expected: no matches. If one remains, it is either a derivation site the inventory missed — report
it — or a genuinely different use, which needs a one-line comment saying why it is not derivation.

- [ ] **Step 6: Run the MCP suite, then the full suite**

Both must match their Step 1 / baseline numbers exactly. A refactor that changes a count has
changed behaviour.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs
rtk git commit -m "refactor(mcp): the store derives, not the tools"
```

---

### Task 5: The server stops deriving

**Files:**
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (drop the `meta` parameter)
- Modify: `src/Scrinia/HttpMemoryStore.cs` (drop the `meta` parameter)
- Modify: `src/Scrinia.Core/IMemoryStore.cs` (drop the `meta` parameter)
- Delete: `src/Scrinia.Core/Models/ArtifactWriteMetadata.cs`

**Interfaces:**
- Consumes: Task 3's store surface.
- Produces: `WriteArtifactAsync` loses its `ArtifactWriteMetadata? meta` parameter.

- [ ] **Step 1: Repoint `MemoryOrchestrator`**

`StoreAsync` (`:15-160`) and `AppendAsync` (`:186+`) build a `MemoryWrite` and call the store. The
long comment at `:57-66` documenting the TF divergence is **deleted with the code it describes** —
the divergence it records cannot occur once there is one pipeline. Do not preserve it as a note;
preserving an explanation for a fixed defect is how the next reader concludes the defect is still
live.

- [ ] **Step 2: Delete `ArtifactWriteMetadata` and its parameter**

Remove the record, and the `ArtifactWriteMetadata? meta = null` parameter from
`WriteArtifactAsync` on `IMemoryStore`, `FileMemoryStore` (`:1141`) and `HttpMemoryStore`. Delete
the "meta is deliberately ignored" comment at `FileMemoryStore.cs:1144-1147` — it documents a
workaround that no longer exists.

Find every caller with the **Grep tool** (`pattern: WriteArtifactAsync`, `path: src`) — the earlier
`remote-write-correctness` work recorded 8 call sites, all passing the cancellation token
positionally, so a removed parameter will bind the wrong argument if any is missed. **This is the
one place in the plan where a missed site compiles and misbehaves**; check every hit.

- [ ] **Step 3: Prove no `TextAnalysis` call remains in the server project**

Grep tool: `pattern: TextAnalysis\.`, `path: src/Scrinia.Server`. Expected: no matches.

- [ ] **Step 4: Build and run the full suite**

Expect the baseline. Server tests are the guard for this half.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Server/Services/MemoryOrchestrator.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia/HttpMemoryStore.cs
rtk git rm src/Scrinia.Core/Models/ArtifactWriteMetadata.cs
rtk git commit -m "refactor(server): the store derives; delete the metadata smuggling channel"
```

---

### Task 6: Reshape the wire

**Files:**
- Modify: `src/Scrinia.Server/Models/ApiDtos.cs`
- Modify: `src/Scrinia.Server/Models/ServerJsonContext.cs`
- Modify: `src/Scrinia/HttpMemoryStore.cs` (client DTOs + `HttpStoreJsonContext`)
- Modify: `src/Scrinia.Core/Resilience/RemoteApiVersion.cs:22`

**Interfaces:**
- Produces: `MemoryEntry`, `ChunkEntryDto`, reshaped `ShowResponse`,
  `ListResponse(MemoryEntry[], int)`. Tasks 7 and 8 use them.

- [ ] **Step 1: Replace the DTOs**

In `src/Scrinia.Server/Models/ApiDtos.cs`, replace `MemoryListItem` (`:34`) with:

```csharp
public sealed record MemoryEntry(
    string Name, string QualifiedName, string Scope,
    int ChunkCount, long OriginalBytes,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    string Description,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    Dictionary<string, int>? TermFrequencies,
    ChunkEntryDto[]? ChunkEntries,
    Dictionary<string, string>? FileHashes);

public sealed record ChunkEntryDto(
    int ChunkIndex, string? ContentPreview, string[]? Keywords,
    Dictionary<string, int>? TermFrequencies);
```

`MemoryEntry` is **always complete** — a null field means genuinely absent, never "not requested".

Reshape `ShowResponse`:

```csharp
public sealed record ShowResponse(string Name, string[] Content, long OriginalBytes);
```

`ChunkCount` is **deleted**, not retained: it is `Content.Length`, and a derivable field that can
disagree with its payload is the defect being fixed.

Update `ListResponse` (`:32`) to `ListResponse(MemoryEntry[] Memories, int Total)`.

- [ ] **Step 2: Register the new types in both JSON contexts**

`src/Scrinia.Server/Models/ServerJsonContext.cs` (registers `ListResponse` at `:12`) and
`HttpStoreJsonContext` in `src/Scrinia/HttpMemoryStore.cs`. Add `MemoryEntry` and `ChunkEntryDto`;
remove `MemoryListItem` / `ListApiItem`.

**Source-generated serialization fails at runtime, not at build, when a type is unregistered.** A
green build here proves nothing; Step 4 is what proves it.

- [ ] **Step 3: Bump the API version**

`src/Scrinia.Core/Resilience/RemoteApiVersion.cs:22` — `Current` from `"1"` to `"2"`. Check whether
any test asserts the literal `"1"` (Grep tool, `pattern: "1"` in `tests/Scrinia.Server.Tests`, and
read `ApiVersionAssertionTests.cs`); update those to the constant rather than a new literal.

- [ ] **Step 4: Build, then run the full suite**

Many tests will fail here — this is a breaking wire change mid-refactor and Tasks 7 and 8 have not
landed. **Record which fail and why.** A failure that is not explained by "the endpoint/client has
not been updated yet" is a real finding.

- [ ] **Step 5: Commit**

```bash
rtk git add src/Scrinia.Server/Models/ApiDtos.cs src/Scrinia.Server/Models/ServerJsonContext.cs src/Scrinia/HttpMemoryStore.cs src/Scrinia.Core/Resilience/RemoteApiVersion.cs
rtk git commit -m "feat(wire): one complete entry shape, chunk array, api v2"
```

---

### Task 7: The server endpoints

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs`
- Test: `tests/Scrinia.Server.Tests/PatchEndpointTests.cs` (create)

**Interfaces:**
- Consumes: Task 6's DTOs.
- Produces: `GET /memories/{name}/entry`, `PATCH /memories/{name}`.

- [ ] **Step 1: Write the failing PATCH tests**

Create `tests/Scrinia.Server.Tests/PatchEndpointTests.cs`, using `ScriniaServerFactory` over
Kestrel (mirror `RemoteLoadIndexTests.cs:37-69`). Four tests:

```csharp
[Fact] public async Task Patch_PersistsAnAttributePromotion() { /* 200; re-read shows the key */ }

[Fact] public async Task Patch_LeavesDerivedFieldsIntact()
{
    // THE load-bearing guard. Store a memory that has TermFrequencies, ChunkEntries and
    // FileHashes; PATCH one attribute; re-read; assert all three are unchanged. This is the
    // entire reason the complete-entry contract was chosen over a field-scoped patch.
}

[Fact] public async Task Patch_RejectsAStaleUpdatedAtWith409()
{
    // Read the entry, mutate the server's copy underneath, PATCH the stale one → 409,
    // and the server's value is unchanged.
}

[Fact] public async Task Patch_RequiresTheStorePermission() { /* 403 */ }
```

- [ ] **Step 2: Run and watch them fail**

Expected: 404 — the route does not exist.

- [ ] **Step 3: Add the two routes**

In the group at `MemoryEndpoints.cs:17-31`:

```csharp
group.MapGet("/memories/{name}/entry", GetEntry);
group.MapPatch("/memories/{name}", PatchEntry);
```

`GetEntry`: permission `read`; resolve the entry case-insensitively; return a complete
`MemoryEntry`; `404` when absent.

`PatchEntry`: permission `store`; `IsWritable` check (mirror `ForgetMemory` at `:162-173`); compare
the body's `UpdatedAt` against the stored value and return `409` on mismatch; apply the same
attribute bounds validator the search filter uses; route through `ctx.Store!.TryUpdateEntry` and
**not** through `PluginPipeline` — `IMemoryStore.TryUpdateEntry`'s contract at
`src/Scrinia.Core/IMemoryStore.cs:33` says it fires no sinks, and it is the seam the entity-typing
sink writes back through. Routing it through the pipeline lets a remote promotion re-enter the sink
that produced it. Do not call `ArchiveVersion` — this is metadata only.

- [ ] **Step 4: Update the list and show projections**

The list projection (around `:130`) builds the new `MemoryEntry` complete, including
`TermFrequencies`, `ChunkEntries` and `FileHashes`. `RecallMemory` (`:146`) returns the reshaped
`ShowResponse` with `Content` as the chunk array — `MemoryOrchestrator.ShowAsync` must stop joining
(`:427`) and return the chunks instead.

- [ ] **Step 5: Run the new tests, then the server suite**

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs src/Scrinia.Server/Services/MemoryOrchestrator.cs tests/Scrinia.Server.Tests/PatchEndpointTests.cs
rtk git commit -m "feat(server): PATCH entry metadata, GET one complete entry, chunks on show"
```

---

### Task 8: The client defers

**Files:**
- Modify: `src/Scrinia/HttpMemoryStore.cs`
- Test: `tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs` (extend)

**Interfaces:**
- Consumes: Tasks 6 and 7.
- Produces: a `HttpMemoryStore` that derives nothing.

- [ ] **Step 1: Write the failing chunk-fidelity test**

Add to `tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs`:

```csharp
[Fact]
public async Task Remote_FiveChunkMemory_KeepsItsChunkBoundaries()
{
    await StoreChunksAsync("chunky", ["alpha", "bravo", "charlie", "delta", "echo"]);

    string artifact = await _remote.ReadArtifactAsync("chunky", "local");

    Nmp2ChunkedEncoder.GetChunkCount(artifact).Should().Be(5,
        because: "the wire carries the chunk array now; it used to send joined text that the " +
                 "client re-encoded as a single chunk while the response claimed five");
    Nmp2ChunkedEncoder.DecodeChunk(artifact, 3).Should().Contain("charlie");
}

[Fact]
public async Task Remote_SingleChunkMemory_StillRoundTrips()
{
    await StoreChunksAsync("solo", ["only"]);
    Nmp2ChunkedEncoder.GetChunkCount(await _remote.ReadArtifactAsync("solo", "local")).Should().Be(1);
}
```

- [ ] **Step 2: Run and watch the five-chunk one fail**

Expected: FAIL asserting `5`, found `1`. **If it fails on `DecodeChunk` instead, the re-encode is
already partly fixed and the plan's premise needs rechecking** — stop and report.

- [ ] **Step 3: Fix the re-encode**

`src/Scrinia/HttpMemoryStore.cs:278` — `Nmp2ChunkedEncoder.EncodeChunks(show.Content)` in place of
`Encode(show.Content)`, now that `show.Content` is `string[]`.

- [ ] **Step 4: Implement `StoreAsync` / `AppendAsync` as pure deferral**

POST the raw `MemoryWrite`; return the `MemoryEntry` from the response mapped to an
`ArtifactEntry`. **No `TextAnalysis` call.** Verify with the Grep tool
(`pattern: TextAnalysis\.`, `path: src/Scrinia`) — expected: no matches.

- [ ] **Step 5: Make `TryUpdateEntry` real**

Replace the body at `:408`: GET `/memories/{name}/entry`, run `mutate`, PATCH the result, return
`true` on `200` and `false` on `404`/`409`.

**Delete the dead block at `:413` onward and its do-not-delete comment.** That comment preserves the
"not found" versus "found but cannot persist" distinction while no persistence channel existed.
This task builds the channel, so the reason expires with it. Removing it is the point, not
collateral — do not leave it out of caution.

- [ ] **Step 6: Run the full suite**

Everything Task 6 broke should now be green. Expect the baseline plus every test added since.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs
rtk git commit -m "feat(client): defer derivation to the server; real TryUpdateEntry; chunk fidelity"
```

---

### Task 9: The parity test

**Files:**
- Create: `tests/Scrinia.Server.Tests/LocalRemoteParityTests.cs`

**Interfaces:**
- Consumes: everything above.

This is the spec's thesis as an assertion, and the one guard that stops the two paths separating
again. It would have caught the append drift.

- [ ] **Step 1: Write it**

Store identical content two ways — once through a `FileMemoryStore` directly, once through an
`HttpMemoryStore` against a Kestrel-hosted server — then assert the resulting entries are equal
field for field:

```csharp
[Fact]
public async Task StoringTheSameContentLocallyAndRemotely_ProducesTheSameEntry()
{
    var write = new MemoryWrite("parity", "local",
        ["the quick brown fox jumps", "and a second chunk mentioning Acme"],
        Description: "curated", Keywords: ["vulpine", "person:jolene"]);

    var local = await _localStore.StoreAsync(write);
    var remote = await _remoteStore.StoreAsync(write);

    // Uri is excluded: it is a synthesized placeholder remotely, and making it dereferenceable
    // is E2's job, explicitly out of this spec's scope.
    remote.Keywords.Should().BeEquivalentTo(local.Keywords);
    remote.TermFrequencies.Should().BeEquivalentTo(local.TermFrequencies);
    remote.Attributes.Should().BeEquivalentTo(local.Attributes);
    remote.ChunkEntries.Should().BeEquivalentTo(local.ChunkEntries);
    remote.ContentPreview.Should().Be(local.ContentPreview);
    remote.ChunkCount.Should().Be(local.ChunkCount);
    remote.OriginalBytes.Should().Be(local.OriginalBytes);
}
```

Add the same for append: store both ways, append both ways, compare.

- [ ] **Step 2: Run it**

If it fails, **do not weaken the assertion.** A field that differs is either a bug this refactor was
supposed to fix or a difference Task 1 ruled as "keep" — in which case exclude that one field with a
comment citing the ruling, and no others.

- [ ] **Step 3: Commit**

```bash
rtk git add tests/Scrinia.Server.Tests/LocalRemoteParityTests.cs
rtk git commit -m "test: local and remote produce the same entry, field for field"
```

---

### Task 10: Documentation

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/superpowers/specs/2026-08-10-residuals-closure.md`

- [ ] **Step 1: CHANGELOG**

Read the Unreleased section and match its heading style. Record: the wire is v2 and breaking; POST
and the listing return complete entries; `ShowResponse.Content` is an array and `ChunkCount` is
gone; PATCH exists; `recall({chunk:N})` works remotely; and that BM25 ranking now matches between
modes — which is a **user-visible ranking change** for anyone who compared the two, and the reason
it changed.

- [ ] **Step 2: Close E4, E5 and both divergences in the closure spec**

Mark E4 and E5 closed with this branch's commits and the plan path. Update the "Two known
divergences" section: both are now closed, with the honest account — the store-path explanation
recorded there was correct and named this fix; the append-path drift was a second, undocumented
cause.

- [ ] **Step 3: Commit**

```bash
rtk git add CHANGELOG.md docs/superpowers/specs/2026-08-10-residuals-closure.md
rtk git commit -m "docs: E4, E5 and both store-mode divergences close"
```

---

## Self-review notes

**Spec coverage.** D1 → Tasks 2, 4, 5. D2 → Task 1. D3 (`Upsert` stays) → Task 3 adds `StoreAsync`
alongside it rather than replacing it. D4/D5 → Task 6. D6 → Task 6's always-complete `MemoryEntry`.
D7 → Tasks 6, 7, 8. D8 → Task 7. The parity thesis → Task 9.

**The riskiest task is 5, not 6.** Task 6 breaks loudly. Task 5 removes a positional parameter from
a method with eight call sites that all pass the cancellation token positionally — a missed site
binds the wrong argument and still compiles. Step 2 says so explicitly.

**Task 1 gates Task 2 and cannot be skipped.** Task 2's test assertions encode Task 1's rulings; an
implementer who starts at Task 2 is guessing which of four behaviours is correct.

**Test counts are deliberately not predicted** past the baseline of 1874. Several tasks add an
unknown number, and Task 6 breaks tests mid-refactor by design. Each task states what it expects
qualitatively instead, and Task 6 requires the failures be explained rather than counted.
