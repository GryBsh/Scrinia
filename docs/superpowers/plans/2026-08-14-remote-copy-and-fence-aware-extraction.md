# Remote Copy and Fence-Aware Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close every residual the `derivation-merge-residuals` branch produced — a copy that can cross the client/server store boundary, reference extraction that respects code fences, and six correctness and hygiene items.

**Architecture:** A new server route carries a client-derived entry so an ephemeral can be promoted without the server re-deriving it against its own filesystem; demotion needs no wire change and is done client-side from existing GETs. A single prose scanner, lifted out of `AttributeExtractor`'s two disagreeing loops, becomes the one answer to "which parts of this content are prose?" and is adopted by `ReferenceExtractor`. The hygiene items are independent multi-site corrections.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, System.Text.Json source generators (AOT), xunit 2.9.3, FluentAssertions 6.12.2.

> **This plan was rewritten after a four-agent falsification pass found 36 false claims and 53 missed sites in its first draft.** Line numbers, fixture names, method contracts and site counts below were verified against the code. They are still not a substitute for the enumeration step in each task.

## Global Constraints

- `src/Scrinia.Merge/Scrinia.Merge.csproj` **must not gain a `ProjectReference`**. It is a standalone AOT executable (it references nothing but the framework). Forced duplicates are pinned by parity tests.
- `TreatWarningsAsErrors=true` for every project not ending in `.Tests` or `.Benchmarks`. A broken `<see cref="…"/>` is CS1574 and therefore a **build failure**.
- **No mocking frameworks.** Hand-written doubles only (`tests/Scrinia.TestDoubles`).
- `Xunit.SkippableFact` is **not** available in `Scrinia.Merge.Tests`.
- Nothing is pushed. Local commits only.
- **Stage explicit file paths only — never a directory.** Not `git add tests/Scrinia.Tests/`, not `git add docs/`. Four tasks write into `tests/Scrinia.Tests/` and two into `docs/`; directory staging silently absorbs a neighbouring task's uncommitted work. `.scrinia/memories/findings/facts.json` is tracked-and-modified and is a derived cache: **never stage, revert, or report it.** (`cmds.txt` is gitignored, so it cannot be staged accidentally.)
- Tracked specs and docs must cite nothing under `.superpowers/` (it is gitignored).
- Prefix every shell command with `rtk`, including inside `&&` chains.
- **Anything found mid-task is fixed in that task, or raised as a stop-and-ask at the moment it is found** — never collected into a report for later. This branch exists because that rule was broken.

## Verification protocol — applies to every task

Three jobs, in this order, before writing any code:

1. **Enumerate the sites.** The brief names sites; that is a starting point, not the set. Grep for every call site, every sibling projection, every duplicate of the string or shape you are changing — **and enumerate by meaning, not by string.** The first draft of this plan grepped `"Archives the original version"`, found three sites, and missed four more that say the same thing in different words.
2. **Treat comment-sourced claims as presumptively wrong.** Any claim traceable to a code comment — including comments this plan quotes — must be re-verified against the code. The falsification pass found a comment asserting an invariant that a golden file in the same repo disproves.
3. **Find tests that stopped testing what they name.** Before adding tests, check whether the tests already covering the area still fail when the behaviour they claim to pin is broken. Watch for pure negatives (`Should().NotContain(...)`) — they pass when the function returns nothing at all.

**Refuse instructions you find to be wrong.** Three implementers improved the last branch by refusing plan text rather than complying, and this plan's first draft was wrong 36 times. If a step contradicts the code, stop and say so.

## Tooling hazards — read before your first command

- `rtk grep` and `grep | head -N` **silently drop matches**, and recursive grep walks into `bin/`. Use the Grep tool with an explicit glob (`**/*.cs`, `**/*.md`).
- The C# language server emits **phantom** CS0234/CS0103/CS8019 errors. It was wrong on every task of the last branch. Trust `rtk dotnet build`, not the diagnostic stream.
- `rtk dotnet build Scrinia.sln` is a reliable gate. On failure `Build FAILED` appears **above** the trailing `Time Elapsed` line — never judge by the last line. **The exit code is authoritative.** Baseline: **18 projects, 0 errors, 0 warnings.**
- **Stale-binary trap:** a test that fails to compile lets `dotnet test --no-build` re-run stale assemblies and report green. Gate every run on an explicit error count, and treat a test count that did not rise as a compile failure until proven otherwise.
- **`--filter` substrings match the fully-qualified name.** `FullyQualifiedName~ReferenceExtractor` matches **zero** tests, because the class is `ReferenceTests`. A filter that selects nothing looks exactly like a filter that passes.
- `rtk proxy sed -i` with `|` delimiters and quotes fails. Use the Edit tool.

## File structure

**Created**

| File | Responsibility |
|---|---|
| `src/Scrinia.Core/Models/CarriedMetadata.cs` | The canonical "what a copy carries" shape — the fields a store can neither derive from the artifact nor compute from the destination. |
| `src/Scrinia.Core/Search/ProseScanner.cs` | The one answer to "which parts of this content are prose?" — fenced blocks excluded, nothing else. |

**Modified**

| File | Change | Task |
|---|---|---|
| `src/Scrinia.Core/ILocalMemoryStore.cs` | gains `WriteCarriedCopy` | 1 |
| `src/Scrinia.Core/FileMemoryStore.cs` | implements it; promotion branch calls it | 1 |
| `tests/Scrinia.Tests/StoreBoundaryTests.cs` | the pinned member list gains a seventh name | 1 |
| `docs/architecture/core.md` | "The six members" → seven; extraction prose; `ReferenceExtractor` prose | 1, 5, 6 |
| `src/Scrinia.Server/Models/ApiDtos.cs` | `CarriedEntry`, `CopyFromClientRequest` | 2 |
| `src/Scrinia.Server/Models/ServerJsonContext.cs` | registers both | 2 |
| `src/Scrinia/HttpMemoryStore.cs` | client mirrors, `HttpStoreJsonContext`, four-way dispatch | 2, 4 |
| `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` | route + handler | 3 |
| `docs/architecture/server.md`, `docs/server-admin.md` | the new route | 3 |
| `tests/Scrinia.Tests/RemoteStoreToolTests.cs` | fixture extension + tests | 4 |
| `src/Scrinia.Core/Search/AttributeExtractor.cs` | both loops adopt `ProseScanner` | 5 |
| `tests/Scrinia.Tests/MentionSpanExtractionTests.cs` | one test changes meaning and name | 5 |
| `src/Scrinia.Core/Search/ReferenceExtractor.cs` | prose-only, class summary included | 6 |
| `tests/Scrinia.Tests/ReferenceTests.cs` | fence tests, pinned fixture, four negatives get positive halves | 6 |
| `src/Scrinia.Mcp/MemoryTools.cs` | `remember`/`append` descriptions; `link` description; `compact` description | 7, 8, 10 |
| `src/Scrinia.Core/Models/EphemeralEntry.cs` | projection helpers | 8 |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | `UpdateMeta` ephemeral path; `Link` precheck | 8 |
| `tests/Scrinia.Tests/ReferenceTests.cs`, `Golden/__snapshots__/link-partial-failure.yaml`, `Golden/ResponseGoldenTests.cs` | the invariant Task 8 extends | 8 |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | `ReadStrings` escalates; `compact` prose | 9, 10 |
| `src/Scrinia.Merge/MetaJsonMerger.cs` | its `GetStrings` twin escalates | 9 |
| `src/Scrinia.Core/Merge/KeywordJaccard.cs` + `src/Scrinia.Merge/KeywordJaccard.cs` | dead branch removed from both | 10 |
| `README.md`, `AGENTS.md`, `docs/cli-reference.md`, `src/Scrinia.Mcp/prompts/guide.md`, `src/Scrinia/Commands/ScriniaCommands.Memory.cs` | `compact` prose | 10 |
| `tests/Scrinia.Tests/WriterCutoverTests.cs`, `tests/Scrinia.Tests/FileMemoryStoreTests.cs` | two vacuous tests widened | 11 |
| `CHANGELOG.md` | Known Issues deleted; new entries; `update()` mentions | 10, 12 |

**Explicitly NOT modified.** `src/Scrinia.Core/MemoryDerivation.cs` — `ForMetadataUpdate` is reused **unchanged**; its no-op reference-identity contract (`:416-417`) is why. `AttributeExtractor`'s punctuation trim — `TrimTrailingUnbalancedPunctuation` (`:448`) is **already** balance-aware at `:460`. Both appeared in the first draft's file table and would have invited an implementer to "fix" working code.

---

## Task 1: `CarriedMetadata` and `WriteCarriedCopy`

Gives the store one way to write an artifact plus caller-supplied metadata without deriving. The server's new endpoint (Task 3) and the existing local promotion path both use it.

**Files:**
- Create: `src/Scrinia.Core/Models/CarriedMetadata.cs`
- Modify: `src/Scrinia.Core/ILocalMemoryStore.cs` (**not** `IMemoryStore.cs` — the interface has its own file, and already has `using Scrinia.Core.Models;` at `:1`)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1886-1921` (promotion branch)
- Modify: `tests/Scrinia.Tests/StoreBoundaryTests.cs:40-61` — **required, see Step 5**
- Modify: `docs/architecture/core.md:392`
- Test: `tests/Scrinia.Tests/FileMemoryStoreTests.cs`

**Interfaces:**
- Produces: `Scrinia.Core.Models.CarriedMetadata` (9 members, below) and
  `bool ILocalMemoryStore.WriteCarriedCopy(string destinationName, string artifact, CarriedMetadata carried, bool overwrite, out string message)`.
  Tasks 2, 3 and 11 depend on both names exactly.

- [ ] **Step 1: Enumerate and read before you write**

Read `FileMemoryStore.CopyMemory` in full (starts `:1852`). You are extracting **only** the ephemeral→persistent branch's write.

Then read `tests/Scrinia.Tests/CaseSensitiveFileStoreTests.cs:108-160`. Those two tests are the whole subject of the index-casing destination resolution this task moves, and they describe the semantics — read them **before** writing `WriteCarriedCopy`, not after.

`ILocalMemoryStore` has exactly **one** implementor: `FileMemoryStore` (`:22`). `FakeMemoryStore` implements `IMemoryStore` only, deliberately (its doc says so), so nothing in `tests/` breaks from the new member. Confirm that yourself.

- [ ] **Step 2: Write `CarriedMetadata`**

```csharp
namespace Scrinia.Core.Models;

/// <summary>
/// Exactly the metadata a copy carries: what a store can neither derive from the artifact nor
/// compute from the destination.
///
/// <para><c>Name</c>, <c>Uri</c> and <c>Scope</c> belong to the destination; <c>ChunkCount</c> is
/// read off the artifact; <c>CreatedAt</c> is stamped when the copy is made. None of them appear
/// here, so none of them can disagree with the artifact they travel with — the rule
/// <c>Scrinia.Server.Models.ShowResponse</c> states for the response side.</para>
/// </summary>
public sealed record CarriedMetadata(
    long OriginalBytes,
    DateTimeOffset? UpdatedAt,
    string? ContentPreview,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    Dictionary<string, int>? TermFrequencies,
    ChunkEntry[]? ChunkEntries,
    Dictionary<string, string>? FileHashes,
    string[]? AgentKeywords = null);
```

- [ ] **Step 3: Write the failing tests**

Add to `tests/Scrinia.Tests/FileMemoryStoreTests.cs`. **Read the entry back with `LoadIndex`, not `ListScoped`** — `ListScoped` returns `List<ScopedArtifact>`, a `(Scope, Entry)` pair with no `Name`, and eleven assertions would fail to compile. `FileMemoryStoreTests.cs:265` is the sibling precedent.

```csharp
/// <summary>
/// <c>WriteCarriedCopy</c> indexes the metadata it is handed rather than deriving any of it. The
/// distinguishing field is <c>FileHashes</c>: derivation hashes `file` attribute values against the
/// workspace, so a baseline naming a path that does not exist here is one that can ONLY have been
/// carried. A test asserting a re-derivable field would pass either way.
/// </summary>
[Fact]
public void WriteCarriedCopy_IndexesTheCarriedMetadataWithoutDeriving()
{
    string artifact = Nmp2ChunkedEncoder.EncodeChunks(["notes about src/Absent.cs"]);
    var carried = new CarriedMetadata(
        OriginalBytes: 4242,
        UpdatedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        ContentPreview: "carried preview",
        Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            { ["file"] = ["src/Absent.cs"] },
        Keywords: ["carried-keyword"],
        TermFrequencies: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            { ["carried"] = 9 },
        ChunkEntries: null,
        FileHashes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["src/Absent.cs"] = "carried-hash-not-from-this-workspace" },
        AgentKeywords: ["carried-keyword"]);

    bool ok = _store.WriteCarriedCopy("promoted", artifact, carried, overwrite: false, out string msg);

    ok.Should().BeTrue(msg);
    var entry = _store.LoadIndex("local").Single(e => e.Name == "promoted");
    entry.OriginalBytes.Should().Be(4242);
    entry.UpdatedAt.Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    entry.ContentPreview.Should().Be("carried preview");
    entry.Keywords.Should().BeEquivalentTo(["carried-keyword"]);
    entry.AgentKeywords.Should().BeEquivalentTo(["carried-keyword"]);
    entry.TermFrequencies!["carried"].Should().Be(9);
    entry.FileHashes!["src/Absent.cs"].Should().Be("carried-hash-not-from-this-workspace");
    entry.Attributes!["file"].Should().BeEquivalentTo(["src/Absent.cs"]);
    // Destination-owned and artifact-derived, never carried:
    entry.Name.Should().Be("promoted");
    entry.ChunkCount.Should().Be(1);
    entry.CreatedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
}

[Fact]
public void WriteCarriedCopy_RefusesAnExistingDestinationWithoutOverwrite()
{
    string artifact = Nmp2ChunkedEncoder.EncodeChunks(["first"]);
    var carried = new CarriedMetadata(1, null, null, null, null, null, null, null);
    _store.WriteCarriedCopy("taken", artifact, carried, overwrite: false, out _).Should().BeTrue();

    bool second = _store.WriteCarriedCopy("taken", artifact, carried, overwrite: false, out string msg);

    second.Should().BeFalse();
    msg.Should().Contain("already exists");
}

/// <summary>
/// Promotion now resolves its destination through the index and adopts the existing entry's casing.
/// The branch this replaces did NOT — it went straight to <c>ArtifactPath</c> — so on a
/// case-sensitive volume, promoting onto "Archive" while "archive" existed wrote a second memory.
/// This is the promotion-side variant of what CaseSensitiveFileStoreTests pins for
/// persistent→persistent, and nothing covered it before.
/// </summary>
[Fact]
public void WriteCarriedCopy_AdoptsTheExistingDestinationsCasing()
{
    var carried = new CarriedMetadata(1, null, null, null, null, null, null, null);
    _store.WriteCarriedCopy("Archive", Nmp2ChunkedEncoder.EncodeChunks(["first"]),
        carried, overwrite: false, out _).Should().BeTrue();

    _store.WriteCarriedCopy("archive", Nmp2ChunkedEncoder.EncodeChunks(["second"]),
        carried, overwrite: true, out _).Should().BeTrue();

    _store.LoadIndex("local").Where(e => e.Name.Equals("archive", StringComparison.OrdinalIgnoreCase))
        .Should().ContainSingle().Which.Name.Should().Be("Archive");
}
```

Add `using Scrinia.Core.Encoding;` and `using Scrinia.Core.Models;` if absent.

- [ ] **Step 4: Run to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriteCarriedCopy"`

Expected: **compile failure** — `WriteCarriedCopy` is not defined. That is the correct failure. Do not proceed on a green run.

- [ ] **Step 5: Declare the member, and update the test that pins the interface**

In `src/Scrinia.Core/ILocalMemoryStore.cs`, after `ImportTopicEntries`:

```csharp
/// <summary>
/// Writes <paramref name="artifact"/> to <paramref name="destinationName"/> and indexes
/// <paramref name="carried"/> against it <b>verbatim — nothing is derived</b>.
///
/// <para>This is the copy rule <c>FileMemoryStore.CopyMemory</c> states inline: the artifact bytes
/// are identical by definition, so the derived metadata is identical, and re-deriving would re-hash
/// `file` values against THIS workspace and produce an entry that differs from its byte-identical
/// source. It exists as a store member because a copy can now arrive from a client whose ephemeral
/// storage this process cannot see, and that caller needs the guarantee an in-process copy gets.</para>
///
/// <para><c>ChunkCount</c> is read off <paramref name="artifact"/> and <c>CreatedAt</c> is stamped
/// now — a copy is created now. Both are therefore absent from
/// <see cref="CarriedMetadata"/>.</para>
/// </summary>
/// <returns>False with a caller-facing <paramref name="message"/> when the destination exists and
/// <paramref name="overwrite"/> is false.</returns>
bool WriteCarriedCopy(
    string destinationName, string artifact, CarriedMetadata carried, bool overwrite,
    out string message);
```

**`tests/Scrinia.Tests/StoreBoundaryTests.cs:40-61` pins this interface's member list as an exact set** (`BeEquivalentTo` on a `HashSet`, so set equality — not a subset check). It fails the moment the member lands, which makes every later "0 failed" gate in this plan unreachable. Add `"WriteCarriedCopy"` to `LocalOnlyMembers`, and **amend the `because:` text**, which currently reads "the six members that need a filesystem live here and nowhere else". It becomes seven, and the criterion needs its second half stated: `WriteCarriedCopy` is not a path accessor but a write that only a filesystem-backed store can perform — a remote store forwards it rather than performing it.

Then correct `docs/architecture/core.md:392`, which also says "**The six members**".

- [ ] **Step 6: Implement it on `FileMemoryStore`**

Place it immediately above `CopyMemory`:

```csharp
/// <inheritdoc />
public bool WriteCarriedCopy(
    string destinationName, string artifact, CarriedMetadata carried, bool overwrite,
    out string message)
{
    ArgumentNullException.ThrowIfNull(carried);

    var (dstScope, dstSubject) = ParseQualifiedName(destinationName);

    // Adopt the existing memory's casing. The promotion branch this serves did NOT do this — it went
    // straight to ArtifactPath — so on a case-sensitive volume it could write a twin. The resolved
    // subject decides three things: whether the collision check below sees a collision, where the
    // bytes land, and what the entry is called.
    ArtifactEntry? existing = LoadIndex(dstScope).FirstOrDefault(
        e => e.Name.Equals(dstSubject, StringComparison.OrdinalIgnoreCase));
    if (existing is not null)
        dstSubject = existing.Name;

    string destPath = ArtifactPath(dstSubject, dstScope);
    if (File.Exists(destPath) && !overwrite)
    {
        message = $"Error: destination memory '{destinationName}' already exists. Set overwrite=true to replace it.";
        return false;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
    AtomicWriteAllText(destPath, artifact);

    Upsert(new ArtifactEntry(
        Name: dstSubject,
        Uri: ArtifactUri(dstSubject, dstScope),
        OriginalBytes: carried.OriginalBytes,
        ChunkCount: Nmp2ChunkedEncoder.GetChunkCount(artifact),
        CreatedAt: DateTimeOffset.UtcNow,
        ContentPreview: carried.ContentPreview,
        Keywords: carried.Keywords,
        TermFrequencies: carried.TermFrequencies,
        UpdatedAt: carried.UpdatedAt,
        ChunkEntries: carried.ChunkEntries,
        FileHashes: carried.FileHashes,
        Attributes: carried.Attributes,
        AgentKeywords: carried.AgentKeywords), dstScope);

    message = $"Wrote '{destinationName}'.";
    return true;
}
```

- [ ] **Step 7: Route the promotion branch through it**

Replace the body of `CopyMemory`'s ephemeral→persistent branch (`:1886-1921`, from the `// Ephemeral → Persistent (promotion)` comment through `return true;`):

```csharp
            // Ephemeral → Persistent (promotion). The write is WriteCarriedCopy's, so the in-process
            // path and the one a remote client reaches over `copy-from-client` cannot drift: the
            // carry rule has one implementation.
            if (!WriteCarriedCopy(
                    dstKey,
                    entry.Artifact,
                    new CarriedMetadata(
                        OriginalBytes: entry.OriginalBytes,
                        UpdatedAt: entry.UpdatedAt,
                        ContentPreview: entry.ContentPreview,
                        Attributes: entry.Attributes,
                        Keywords: entry.Keywords,
                        TermFrequencies: entry.TermFrequencies,
                        ChunkEntries: entry.ChunkEntries,
                        FileHashes: entry.FileHashes,
                        AgentKeywords: entry.AgentKeywords),
                    overwrite,
                    out message))
                return false;

            message = $"Copied '{sourceName}' to '{destinationName}'.";
            return true;
```

**This changes promotion behaviour in two ways, both deliberate, both to be named in your report:**
(a) `ChunkCount` is now read off the artifact rather than copied from `entry.ChunkCount` — where they disagree the artifact is right; (b) the destination is now resolved through `LoadIndex` and adopts the existing entry's casing, which the old branch never did.

- [ ] **Step 8: Run the copy tests**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CopyMemory"`

Expected: all pass, including the pre-existing `CopyMemory_PromotingAnEphemeral_CarriesEveryMetadataField` (`FileMemoryStoreTests.cs:239`) and the two `CaseSensitiveFileStoreTests` cases.

- [ ] **Step 9: Prove the refactor is pinned**

Temporarily change `FileHashes: carried.FileHashes` to `FileHashes: null` in `WriteCarriedCopy`. Re-run. `CopyMemory_PromotingAnEphemeral_CarriesEveryMetadataField` and `WriteCarriedCopy_IndexesTheCarriedMetadataWithoutDeriving` must both **fail**. Revert and confirm green.

- [ ] **Step 10: Full build and suite**

Run: `rtk dotnet build Scrinia.sln` — expect 0 errors, 0 warnings.
Run: `rtk dotnet test Scrinia.sln` — expect 0 failed, and a test count **3 higher** than the baseline.

- [ ] **Step 11: Commit**

```bash
rtk git add src/Scrinia.Core/Models/CarriedMetadata.cs src/Scrinia.Core/ILocalMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/FileMemoryStoreTests.cs tests/Scrinia.Tests/StoreBoundaryTests.cs docs/architecture/core.md
rtk git commit -m "feat(core): one implementation of the carry rule, reachable by a remote caller"
```

---

## Task 2: The wire shapes

**Files:**
- Modify: `src/Scrinia.Server/Models/ApiDtos.cs:144` (beside `CopyRequest`)
- Modify: `src/Scrinia.Server/Models/ServerJsonContext.cs` (after the `CopyRequest` registration at `:20`)
- Modify: `src/Scrinia/HttpMemoryStore.cs:184` and the `HttpStoreJsonContext` list at `:111-123`
- Test: `tests/Scrinia.Server.Tests/CopyFromClientContractTests.cs`

**Interfaces:**
- Consumes: `Scrinia.Core.Models.CarriedMetadata` (Task 1).
- Produces: `Scrinia.Server.Models.CarriedEntry`, `Scrinia.Server.Models.CopyFromClientRequest`, and the client mirrors `HttpMemoryStore.CarriedEntryDto`, `HttpMemoryStore.CopyFromClientApiRequest`.

- [ ] **Step 1: Enumerate before you write**

Read `ApiDtos.cs` in full, `HttpMemoryStore.cs:108-190`, and **`src/Scrinia.Server/Models/ServerJsonContext.cs`**, which registers all 22 wire DTOs and is installed at `Program.cs:311`. The client has **two** contexts (`HttpStoreJsonContext:124`, `PatchBodyJsonContext:138`); the new request belongs in the first only — it has no presence-versus-null contract.

- [ ] **Step 2: Add the server shapes**

In `ApiDtos.cs`, immediately after `CopyRequest` (`:144-146`):

```csharp
/// <summary>
/// The metadata a client-side source carries to a destination this server owns: exactly the fields
/// the server can neither derive from the artifact nor compute from the destination name.
///
/// <para><c>Name</c>, <c>QualifiedName</c> and <c>Scope</c> are the destination's; <c>ChunkCount</c>
/// is <c>Content.Length</c>; <c>CreatedAt</c> is stamped on arrival. None of them appear here, so
/// none of them can disagree with the request that carries them — the same rule
/// <see cref="ShowResponse"/> states, applied to a request.</para>
/// </summary>
public sealed record CarriedEntry(
    long OriginalBytes,
    DateTimeOffset? UpdatedAt,
    string? Description,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    Dictionary<string, int>? TermFrequencies,
    ChunkEntryDto[]? ChunkEntries,
    Dictionary<string, string>? FileHashes,
    string[]? AgentKeywords = null);

/// <summary>
/// Body of <c>POST /memories/{name}/copy-from-client</c>, where <c>{name}</c> is the
/// <b>DESTINATION</b> — the opposite of <c>POST /memories/{name}/copy</c>, where it is the source.
///
/// <para>That asymmetry is deliberate and is the whole reason this route exists: the source lives
/// only on the client, so there is no server-side name to put in the path. The caller supplies the
/// artifact and the metadata it already derived, and the server stores both without deriving
/// anything.</para>
/// </summary>
public sealed record CopyFromClientRequest(
    string[] Content,
    CarriedEntry Entry,
    bool Overwrite = false);
```

- [ ] **Step 3: Register them server-side**

In `ServerJsonContext.cs`, after the `[JsonSerializable(typeof(CopyRequest))]` line:

```csharp
[JsonSerializable(typeof(CopyFromClientRequest))]
[JsonSerializable(typeof(CarriedEntry))]
```

`Scrinia.Server` is not published trimmed today, so omitting this would still pass every test — and the codebase treats source-gen registration as a standing invariant anyway (`Program.cs:302-304`, `ArtifactEntry.cs:71-73`). Step 4's round-trip test is written to force it.

- [ ] **Step 4: Write the contract tests**

Create `tests/Scrinia.Server.Tests/CopyFromClientContractTests.cs`. **Serialize through `ServerJsonContext.Default`** — a bare `JsonSerializer.Serialize` passes whether or not Step 3 happened, which is a test that does not test what it names.

```csharp
using System.Text.Json;
using FluentAssertions;
using Scrinia.Server.Models;
using Xunit;

namespace Scrinia.Server.Tests;

/// <summary>
/// The request shape carries no field the server derives or recomputes. Asserted as a property of
/// the TYPE, not of one instance: a later edit that adds <c>ChunkCount</c> or <c>Name</c> back —
/// the shape <c>ShowResponse</c> exists to prevent — fails here rather than in review.
/// </summary>
public sealed class CopyFromClientContractTests
{
    [Theory]
    [InlineData("Name")]
    [InlineData("QualifiedName")]
    [InlineData("Scope")]
    [InlineData("ChunkCount")]
    [InlineData("CreatedAt")]
    public void CarriedEntry_DoesNotCarryADerivableOrDestinationOwnedField(string forbidden)
    {
        typeof(CarriedEntry).GetProperty(forbidden).Should().BeNull(
            $"'{forbidden}' is computed from the destination or the artifact; carrying it would let "
            + "the request disagree with itself");
    }

    /// <summary>Round-trips through the SERVER's source-generated context — the one
    /// <c>Program.cs:311</c> installs — so an unregistered type fails here rather than at runtime
    /// under trimming.</summary>
    [Fact]
    public void CopyFromClientRequest_RoundTripsThroughServerJsonContext()
    {
        var original = new CopyFromClientRequest(
            Content: ["chunk one", "chunk two"],
            Entry: new CarriedEntry(
                OriginalBytes: 17,
                UpdatedAt: new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
                Description: "a preview",
                Attributes: new Dictionary<string, string[]> { ["file"] = ["src/A.cs"] },
                Keywords: ["kw"],
                TermFrequencies: new Dictionary<string, int> { ["kw"] = 2 },
                ChunkEntries: [new ChunkEntryDto(0, "preview", ["kw"], null)],
                FileHashes: new Dictionary<string, string> { ["src/A.cs"] = "deadbeef" },
                AgentKeywords: ["kw"]),
            Overwrite: true);

        string json = JsonSerializer.Serialize(original, ServerJsonContext.Default.CopyFromClientRequest);
        var back = JsonSerializer.Deserialize(json, ServerJsonContext.Default.CopyFromClientRequest);

        back.Should().BeEquivalentTo(original);
    }

    /// <summary>Overwrite defaults to false: a body that omits it must not silently replace an
    /// existing memory.</summary>
    [Fact]
    public void CopyFromClientRequest_DefaultsOverwriteToFalse()
    {
        var back = JsonSerializer.Deserialize(
            """{"content":["c"],"entry":{"originalBytes":1}}""",
            ServerJsonContext.Default.CopyFromClientRequest);

        back!.Overwrite.Should().BeFalse();
    }
}
```

That file contains **7 test cases** (a 5-case `[Theory]` plus 2 `[Fact]`s), not 3.

- [ ] **Step 5: Run them**

Run: `rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~CopyFromClientContract"`

Expected: 7 passed.

- [ ] **Step 6: Add the client mirrors and register them**

In `HttpMemoryStore.cs`, after `CopyApiRequest` (`:184`):

```csharp
/// <summary>The metadata a client-side source carries to a server-owned destination: exactly the
/// fields the server can neither derive from the artifact nor compute from the destination name.
/// Mirrors <c>Scrinia.Server.Models.CarriedEntry</c>.</summary>
internal sealed record CarriedEntryDto(
    long OriginalBytes,
    DateTimeOffset? UpdatedAt,
    string? Description,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords,
    Dictionary<string, int>? TermFrequencies,
    ChunkEntryDto[]? ChunkEntries,
    Dictionary<string, string>? FileHashes,
    string[]? AgentKeywords = null);
/// <summary>Body of <c>POST /memories/{name}/copy-from-client</c>, where <c>{name}</c> is the
/// DESTINATION — unlike <c>/copy</c>, where it is the source. See <see cref="CopyMemory"/> for why
/// the source cannot be named in the path.</summary>
internal sealed record CopyFromClientApiRequest(
    string[] Content, CarriedEntryDto Entry, bool Overwrite = false);
```

`Scrinia` does not reference `Scrinia.Server`, so write the prose out rather than using `inheritdoc` across assemblies.

In the `HttpStoreJsonContext` attribute list, after `CopyApiRequest`:

```csharp
    [JsonSerializable(typeof(CopyFromClientApiRequest))]
    [JsonSerializable(typeof(CarriedEntryDto))]
```

Do **not** add them to `PatchBodyJsonContext`.

- [ ] **Step 7: Build and run the full suite**

Run: `rtk dotnet build Scrinia.sln` — 0 errors, 0 warnings.
Run: `rtk dotnet test Scrinia.sln` — 0 failed, count **7 higher** than after Task 1.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia.Server/Models/ApiDtos.cs src/Scrinia.Server/Models/ServerJsonContext.cs src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Server.Tests/CopyFromClientContractTests.cs
rtk git commit -m "feat(wire): a copy request that carries only what the server cannot derive"
```

---

## Task 3: The `copy-from-client` endpoint

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:32` (route) and a new handler after `CopyMemory` (`:536`)
- Modify: `docs/architecture/server.md:175` (endpoint→permission table), `docs/server-admin.md:306-312` ("Copy a Memory")
- Test: `tests/Scrinia.Server.Tests/CopyFromClientEndpointTests.cs`

**Interfaces:**
- Consumes: `ILocalMemoryStore.WriteCarriedCopy`, `CarriedMetadata` (Task 1); `CopyFromClientRequest`, `CarriedEntry` (Task 2).
- Produces: `POST /api/v1/stores/{store}/memories/{name}/copy-from-client`, `{name}` = **destination**.

- [ ] **Step 1: Enumerate before you write**

Read `MemoryEndpoints.cs:18-46`, `:48-119` (`RememberMemory`, whose validation order this mirrors), `:191-199` (`ValidateAttributeBounds`), `:516-536` (`CopyMemory`).

`RequestContext.Store` is `IMemoryStore?` (`src/Scrinia.Server/Auth/RequestContext.cs:15`) — the **wider** type, and nullable. So the `is not ILocalMemoryStore local` narrowing in Step 3 is required and doubles as the null guard. It is live code, not defensive padding: `BundleEndpointTests.cs:358` constructs a non-local store through the `IStorageBackend` plugin seam, and `MemoryEndpoints.cs:669` and `:705` already use this exact pattern.

`MaxNameLength` (`:40`), `MaxContentBytes` (`:41`), `ValidateAttributeBounds` (`:191`), `IsWritable` (`:45`), `ChunkEntry` and `Nmp2ChunkedEncoder` are all already in scope in this file. Confirm rather than assume.

- [ ] **Step 2: Register the route**

After the existing copy route at `:32`:

```csharp
        group.MapPost("/memories/{name}/copy", CopyMemory);
        // {name} is the DESTINATION on this one — see CopyFromClient's doc comment.
        group.MapPost("/memories/{name}/copy-from-client", CopyFromClient);
```

- [ ] **Step 3: Write the handler**

Place it immediately after `CopyMemory`:

```csharp
    /// <summary>
    /// Stores an artifact plus the metadata the CALLER derived, deriving nothing itself.
    ///
    /// <para><b><c>{name}</c> is the DESTINATION here</b> — the opposite of
    /// <see cref="CopyMemory"/>, where it is the source. That is deliberate and is the whole reason
    /// this route exists: the source is a client-side ephemeral this process has never seen, so
    /// there is no server-side name to put in the path. <b>The handler performs no name resolution
    /// against this store at all</b>, which is precisely what removes the defect it replaces — a
    /// promotion used to arrive as a bare name and get resolved here, silently copying an unrelated
    /// persistent memory that happened to share it.</para>
    ///
    /// <para>A separate route rather than an optional field on <see cref="CopyRequest"/>: a server
    /// predating this would ignore an unknown field and fall through to that same wrong resolution,
    /// reporting success. An unknown route 404s, which the client surfaces as a refusal.</para>
    /// </summary>
    private static Task<IResult> CopyFromClient(
        string store,
        string name,
        CopyFromClientRequest req,
        RequestContext ctx)
    {
        if (!ctx.HasPermission("copy"))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse("Permission 'copy' required."), statusCode: 403));

        if (!IsWritable(ctx, store))
            return Task.FromResult<IResult>(Results.Json(new ErrorResponse($"Store '{store}' is read-only for this key."), statusCode: 403));

        string decoded = Uri.UnescapeDataString(name);
        if (string.IsNullOrWhiteSpace(decoded))
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("A destination name is required.")));
        if (decoded.Length > MaxNameLength)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse($"Name must not exceed {MaxNameLength} characters.")));
        if (req.Content is null || req.Content.Length == 0)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("content is required.")));
        if (req.Entry is null)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse("entry is required.")));
        if (req.Content.Any(c => c is not null && System.Text.Encoding.UTF8.GetByteCount(c) > MaxContentBytes))
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse($"Each content element must not exceed {MaxContentBytes / (1024 * 1024)} MB.")));

        // Normalized before the bounds check, matching RememberMemory and PatchEntry: Normalize can
        // only shrink a map, so checking the raw one rejects bodies the store itself would accept.
        var attributes = KnownAttributes.Normalize(req.Entry.Attributes);
        if (ValidateAttributeBounds(
                attributes, KnownAttributes.MaxAttributes, KnownAttributes.MaxValuesPerKey)
            is { } boundsError)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(boundsError)));

        if (ctx.Store is not ILocalMemoryStore local)
            return Task.FromResult<IResult>(Results.Json(
                new ErrorResponse("This store cannot accept a client-carried copy."), statusCode: 501));

        bool ok = local.WriteCarriedCopy(
            decoded,
            Nmp2ChunkedEncoder.EncodeChunks(req.Content),
            new CarriedMetadata(
                OriginalBytes: req.Entry.OriginalBytes,
                UpdatedAt: req.Entry.UpdatedAt,
                ContentPreview: req.Entry.Description,
                Attributes: attributes,
                Keywords: req.Entry.Keywords,
                TermFrequencies: req.Entry.TermFrequencies,
                ChunkEntries: req.Entry.ChunkEntries?
                    .Select(c => new ChunkEntry(c.ChunkIndex, c.ContentPreview, c.Keywords, c.TermFrequencies))
                    .ToArray(),
                FileHashes: req.Entry.FileHashes,
                AgentKeywords: req.Entry.AgentKeywords),
            req.Overwrite,
            out string msg);

        if (!ok)
            return Task.FromResult<IResult>(Results.BadRequest(new ErrorResponse(msg)));
        return Task.FromResult<IResult>(Results.Ok(new { message = msg }));
    }
```

**On re-encoding.** The wire carries decoded chunks and this handler re-encodes them. `EncodeChunks` is a pure function of the chunks and a constant `CharsPerLine = 76` — no timestamp, GUID or environment read anywhere in the chain. The property this design needs is **round-trip fidelity** (re-encoding the same chunks yields an artifact that decodes back to the same chunks), which is guaranteed. It does *not* need byte-identity with the client's artifact, and could not have it: Brotli output is not contractually stable across .NET versions. Nothing compares the two artifacts, `OriginalBytes` is carried rather than recomputed, and `ChunkCount` is read off the server's own artifact.

- [ ] **Step 4: Write the endpoint tests**

Create `tests/Scrinia.Server.Tests/CopyFromClientEndpointTests.cs`. The fixture is **`ScriniaServerFactory`** (`tests/Scrinia.Server.Tests/ScriniaServerFactory.cs:12`), which exposes `CreateAuthenticatedClient()`, `PrimaryStore`, `CreateRestrictedStoreKey(...)` — **there is no `Client` or `Base` property.** Follow `MemoryEndpointTests.cs:9-20`:

```csharp
public sealed class CopyFromClientEndpointTests : IClassFixture<ScriniaServerFactory>
{
    private readonly HttpClient _client;
    private readonly string _base;

    public CopyFromClientEndpointTests(ScriniaServerFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _base = $"/api/v1/stores/{factory.PrimaryStore}";
    }
```

Tests to write:

```csharp
/// <summary>
/// The promotion carries a drift baseline naming a file that does NOT exist in the server's
/// workspace. That is the assertion that matters: derivation hashes `file` values against the local
/// filesystem, so a baseline surviving intact can only have been carried. A test that merely
/// asserted the memory exists would pass while the server silently re-derived it to empty.
/// </summary>
[Fact]
public async Task CopyFromClient_CarriesTheClientsDriftBaselineVerbatim()
{
    var body = new CopyFromClientRequest(
        Content: ["a note referencing src/OnlyOnTheClient.cs"],
        Entry: new CarriedEntry(
            OriginalBytes: 99,
            UpdatedAt: new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
            Description: "carried preview",
            Attributes: new Dictionary<string, string[]> { ["file"] = ["src/OnlyOnTheClient.cs"] },
            Keywords: ["carried"],
            TermFrequencies: new Dictionary<string, int> { ["carried"] = 7 },
            ChunkEntries: null,
            FileHashes: new Dictionary<string, string> { ["src/OnlyOnTheClient.cs"] = "client-side-hash" },
            AgentKeywords: ["carried"]),
        Overwrite: false);

    var resp = await _client.PostAsJsonAsync($"{_base}/memories/promoted/copy-from-client", body);
    resp.StatusCode.Should().Be(HttpStatusCode.OK);

    var entry = await _client.GetFromJsonAsync<MemoryEntry>($"{_base}/memories/promoted/entry");
    entry!.FileHashes!["src/OnlyOnTheClient.cs"].Should().Be("client-side-hash");
    entry.TermFrequencies!["carried"].Should().Be(7);
    entry.OriginalBytes.Should().Be(99);
    entry.AgentKeywords.Should().BeEquivalentTo(["carried"]);
    entry.Name.Should().Be("promoted");
}

/// <summary>No name resolution happens against this store, so a same-named persistent memory is
/// irrelevant rather than a collision to copy from. This is the wrong-memory defect, pinned.</summary>
[Fact]
public async Task CopyFromClient_IgnoresASameNamedMemoryInTheServersOwnStore()
{
    await _client.PostAsJsonAsync($"{_base}/memories",
        new StoreRequest(["UNRELATED server-side content"], "collide"));

    var body = new CopyFromClientRequest(
        Content: ["the client's content"],
        Entry: new CarriedEntry(5, null, null, null, null, null, null, null),
        Overwrite: true);
    var resp = await _client.PostAsJsonAsync($"{_base}/memories/collide/copy-from-client", body);
    resp.StatusCode.Should().Be(HttpStatusCode.OK);

    var show = await _client.GetFromJsonAsync<ShowResponse>($"{_base}/memories/collide");
    show!.Content.Single().Should().Be("the client's content");
}
```

Then **one test per rejection path** — the spec requires it and the first draft shipped seven of them untested: 403 without the `copy` permission, 403 on a read-only store (use `CreateRestrictedStoreKey`), 400 on an over-long name, 400 on empty `Content`, 400 on an over-`MaxContentBytes` element, 400 on out-of-bounds attributes, and 400 on an existing destination without `overwrite`. Write each asserting the status **and** the specific message fragment, so a handler that 400s for the wrong reason fails.

- [ ] **Step 5: Run them**

Run: `rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~CopyFromClientEndpoint"`

- [ ] **Step 6: Prove the wrong-memory test is not vacuous**

**Do not** substitute `ctx.Store!.CopyMemory(decoded, decoded, …)` — `FileMemoryStore.cs:1927-1936` refuses source==destination outright, so the handler would 400 unconditionally and the test would fail for a reason unrelated to name resolution.

Instead substitute a mutation that really re-resolves: read the server-side artifact for `decoded` and write **that** instead of `req.Content`. `CopyFromClient_IgnoresASameNamedMemoryInTheServersOwnStore` must then fail on its **content** assertion. Revert and confirm green.

- [ ] **Step 7: Document the route**

`docs/architecture/server.md:175` carries the endpoint→permission table and `docs/server-admin.md:306-312` documents "Copy a Memory". Add the new route to both, stating the `{name}`-is-destination asymmetry. A new endpoint absent from the permission table reads as an endpoint with no permission.

`tests/Scrinia.Server.Tests/OpenApiTests.cs:44-45` asserts only on `/memories/{name}` and needs no change.

- [ ] **Step 8: Full build and suite, then commit**

```bash
rtk git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs tests/Scrinia.Server.Tests/CopyFromClientEndpointTests.cs docs/architecture/server.md docs/server-admin.md
rtk git commit -m "feat(server): accept a copy whose source lives only on the client"
```

---

## Task 4: Four-way copy dispatch on the client

The user-visible fix. **Nothing tests `HttpMemoryStore.CopyMemory` today** — the whole 42-line method is uncovered, including the two branches this task leaves unchanged. Verification job 3 will find nothing here; that is the finding, not an absence of one.

**Files:**
- Modify: `src/Scrinia/HttpMemoryStore.cs:786-827`
- Modify: `tests/Scrinia.Tests/RemoteStoreToolTests.cs` — fixture extension **and** tests
- Test: same file

**Interfaces:** Consumes `CopyFromClientApiRequest`, `CarriedEntryDto` (Task 2), the route (Task 3), and the existing `ToArtifactEntry` (`:205`), `GetEphemeral` (`:292`), `RememberEphemeral` (`:277`), `SendSync` (`:29`), `ShowApiResponse` (`:149`).

- [ ] **Step 1: Enumerate and read before you write**

Read `HttpMemoryStore.cs:786-827`, and `FileMemoryStore.cs:1958-1989` — the local persistent→ephemeral branch, whose field-by-field construction (including its `sourceEntry?.` null tolerance and `$"Loaded from {sourceName}"` preview fallback) the remote demotion must match. Read `:507-521` for the established GET-entry pattern.

`MemoryTools.Copy` (`src/Scrinia.Mcp/MemoryTools.Core.cs:768-778`) is the **only** production caller, and there is no CLI `copy` command. It wraps failures as `ResponseBuilder.Error(msg, ErrorCodes.Internal)`. Decide whether `Internal` is right for a version-skew refusal and say so in your report — do not inherit it silently.

**A divergence to rule on, not to inherit:** `FileMemoryStore.cs:1871-1875` refuses `~a`→`~a` with "source and destination are the same"; `HttpMemoryStore.cs:792-810` does not. This task is the moment to close or pin it.

- [ ] **Step 2: Extend the test fixture first**

The plan's tests cannot compile or pass against today's fixture. Fix it before writing them.

`RemoteStoreScope`'s real constructor is `RemoteStoreScope(string listJson = EmptyListJson, HttpStatusCode patchStatus = HttpStatusCode.OK, string? workspaceRoot = null)` (`RemoteStoreToolTests.cs:190-191`). `StubBackendHandler.Respond` (`:120-173`) serves exactly four cases and 404s everything else, and records requests as `"METHOD path"` only (`:62`).

You need:
1. A branch for `POST …/memories/{dst}/copy-from-client`, with a **configurable status** (200 for the happy path, 404 for the older-server test).
2. A branch for `GET …/memories/{name}` (show). Note `:124`'s condition is `path.EndsWith("/memories")`, which a single-name show path does not match — this is a new branch, not a widened one.
3. **Request body capture**, read **synchronously**: both `Send` and `SendAsync` are overridden (`:68-73`) and `HttpMemoryStore.SendSync` uses the synchronous one.

**Every response must keep stamping the API version header** (`:171`). `SendSync` calls `RemoteApiVersionGuard.Validate` *before* any status handling (`:88-96`) and throws on a missing header — so an unstamped 404 makes the "older server" test throw instead of returning a refusal.

- [ ] **Step 3: Write the failing tests**

```csharp
/// <summary>
/// Promotion sends the client's own derived metadata to the destination route. The assertion is on
/// the REQUEST: a promotion that posted to `/copy` with a stripped name is the defect, and it would
/// still return 200 from a server that has an unrelated memory of that name.
/// <para>The fixture is deliberately MULTI-CHUNK: DecodeChunk is 1-based, and a single-chunk
/// fixture passes a 1-vs-0 off-by-one that still drops data at two chunks.</para>
/// </summary>
[Fact]
public void CopyMemory_PromotingAnEphemeral_PostsEveryChunkAndTheCarriedEntry()
{
    using var scope = new RemoteStoreScope(copyFromClientStatus: HttpStatusCode.OK);
    var store = scope.Store;
    store.RememberEphemeral("scratch", new EphemeralEntry(
        Name: "scratch",
        Artifact: Nmp2ChunkedEncoder.EncodeChunks(["first chunk about src/Local.cs", "second chunk"]),
        OriginalBytes: 23, ChunkCount: 2, CreatedAt: DateTimeOffset.UtcNow,
        ContentPreview: "scratch preview",
        Keywords: ["kw"],
        TermFrequencies: new Dictionary<string, int> { ["kw"] = 3 },
        UpdatedAt: null, ChunkEntries: null,
        FileHashes: new Dictionary<string, string> { ["src/Local.cs"] = "client-hash" },
        Attributes: new Dictionary<string, string[]> { ["file"] = ["src/Local.cs"] },
        AgentKeywords: ["kw"]));

    bool ok = store.CopyMemory("~scratch", "/notes/promoted", overwrite: false, out string msg);

    ok.Should().BeTrue(msg);
    scope.LastRequestPath.Should().EndWith("/copy-from-client");
    scope.LastRequestPath.Should().Contain("promoted");
    scope.LastRequestBody.Should().Contain("client-hash");
    scope.LastRequestBody.Should().Contain("first chunk");
    scope.LastRequestBody.Should().Contain("second chunk");
}
```

Plus: a 404-from-the-route test asserting `ok == false` and a message naming the unsupported operation; a demotion test asserting the ephemeral lands **in this process** with its metadata; a missing-source test asserting `RequestCount == 0`; and — the spec requires it and the first draft omitted it — **a persistent→persistent test proving `POST /copy` still behaves exactly as today**, since Task 4 modifies that branch (it no longer strips a `~` from `src`).

- [ ] **Step 4: Run to verify they fail for behavioural reasons**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RemoteStoreToolTests.CopyMemory"`

- [ ] **Step 5: Rewrite `CopyMemory`**

Replace `:786-827` with the four-way split. The full bodies of `CopyEphemeralToEphemeral`, `PromoteToServer` and `DemoteToThisProcess` follow the shapes described in Step 1; the parts that are easy to get wrong:

**The decode loop is 1-based.** `Nmp2ChunkedEncoder.DecodeChunk` throws `ArgumentOutOfRangeException` when `chunkIndex < 1` (`:290-295`), and its doc says "(1-based)":

```csharp
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(entry.Artifact);
        string[] chunks = new string[chunkCount];
        for (int i = 0; i < chunkCount; i++)
            chunks[i] = Nmp2ChunkedEncoder.DecodeChunk(entry.Artifact, i + 1);
```

A 0-based loop throws on the first iteration of **every** promotion, before any request is sent. (`Nmp2ChunkedEncoder.DecodeAllChunksVerified` at `:327` does this walk with CRC re-checks and size guards, but it is `internal` to `Scrinia.Core` and `Scrinia` cannot call it. Note that in a comment rather than leaving the re-implementation unremarked.)

**A missing client-side source sends no request** — there is nothing to ask the server about, and asking is how the old path found the wrong memory.

**A 404 from `copy-from-client` is the route being absent, not the memory.** This route creates its destination and resolves nothing, so it has no "not found" of its own. Report it as an explicit refusal naming the unsupported operation, never as a copy that worked.

**Demotion writes locally**, from `GET …/entry` + `GET …/memories/{name}`, constructing the `EphemeralEntry` field-for-field as `FileMemoryStore.cs:1972-1985` does.

**Persistent→persistent keeps `POST /copy`** and no longer strips a `~` from `src`, because it can no longer be reached with one.

- [ ] **Step 6: Prove the promotion test is not vacuous**

Temporarily post to `…/copy` instead of `…/copy-from-client`. The path assertion must **fail**. Revert and confirm green.

- [ ] **Step 7: Full build and suite, then commit**

```bash
rtk git add src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Tests/RemoteStoreToolTests.cs
rtk git commit -m "fix(remote): a copy across the ephemeral boundary reaches the right memory"
```

---

## Task 5: `ProseScanner`, and one exclusion set

`AttributeExtractor` has two line loops with **different** exclusion sets, every cell of this verified:

| | `Extract` (`:53`) | `ExtractMentionSpans` (`:200`) |
|---|---|---|
| Fenced blocks | skipped (`:71`) | skipped (`:213`) |
| YAML frontmatter | **read** — no skip exists | skipped (`:222`) |
| Inline-code spans | **not stripped** | stripped (`:224`) |

They reconcile onto **fenced blocks only** — the only direction that destroys nothing documented, since moving both to the mention-span set would stop `Extract` reading frontmatter (`:6-7` names it in the content-extraction list).

**In `Extract`, `inFrontmatter` is dead state.** It is written at `:75`/`:76` and read only at `:76`; it gates nothing, because `---` lines `continue` unconditionally. So the whole `:73-79` block is behaviourally `if (line.Trim() == "---") continue;`. That is positive proof the substitution is behaviour-preserving for `Extract`.

**Files:**
- Create: `src/Scrinia.Core/Search/ProseScanner.cs`
- Modify: `src/Scrinia.Core/Search/AttributeExtractor.cs:53-97`, `:200-231`, class doc `:12`, mention doc `:196-197`
- Modify: `tests/Scrinia.Tests/MentionSpanExtractionTests.cs:29-34`
- Test: `tests/Scrinia.Tests/ProseScannerTests.cs`

**Interfaces:** Produces `public static IEnumerable<string> Scrinia.Core.Search.ProseScanner.ProseLines(string? content)`. Task 6 consumes it.

- [ ] **Step 1: Enumerate before you write**

There are exactly **two** fence loops repo-wide; confirm it. `benchmarks/…/AgentPrompts.cs:152` (`StripCodeFence`) is a response-envelope trim, not a stateful scan — not a third. `src/Scrinia.Merge/` has no copy of either extractor, so the forced-duplicate rule does **not** apply to Tasks 5–7.

`StripInlineCode` has exactly one caller, `:224`. After Step 5 it has none.

- [ ] **Step 2: Write `ProseScanner`**

```csharp
namespace Scrinia.Core.Search;

/// <summary>
/// The one answer to "which parts of this content are prose?" — the single line scanner every
/// extractor runs through.
///
/// <para><b>Fenced blocks only.</b> A fenced block is bulk machine output: a pasted build log, a
/// diff, a config dump. Nobody is citing the paths inside it. Everything else the author typed on
/// purpose:</para>
/// <list type="bullet">
/// <item><b>YAML frontmatter is prose here.</b> It is where deliberate <c>key: value</c> metadata
/// lives, and <see cref="AttributeExtractor.Extract"/> has always read it.</item>
/// <item><b>Inline-code spans are prose here.</b> A backtick-quoted <c>[[wiki-link]]</c> or
/// <c>~ephemeral</c> extracts today and must keep doing so, and inline code is where mention spans
/// find real entity names.</item>
/// </list>
///
/// <para>This replaces two loops that disagreed: <c>Extract</c> read frontmatter and kept inline
/// code, while <c>ExtractMentionSpans</c> skipped both. One rule now, so the tool descriptions can
/// state it once and be true of every route.</para>
///
/// <para><b>Two known gaps, both inherited and both left alone deliberately.</b> Only <c>```</c>
/// opens a fence, not <c>~~~</c>; and the test is <c>StartsWith</c> with no leading-whitespace trim,
/// so a fence indented inside a list item or blockquote is invisible. In practice the second matters
/// more than the first. Widening either is a behaviour change with its own blast radius, not a
/// tidy-up — recorded here so the next reader does not take them for oversights.</para>
/// </summary>
public static class ProseScanner
{
    /// <summary>The prose lines of <paramref name="content"/>: every line outside a fenced block,
    /// with the <c>```</c> delimiter lines themselves omitted. Line content is otherwise verbatim,
    /// beyond the trailing <c>\r</c> a CRLF split leaves — character-identical to what the two loops
    /// this replaces produced.</summary>
    public static IEnumerable<string> ProseLines(string? content)
    {
        if (string.IsNullOrEmpty(content)) yield break;

        bool inFence = false;
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            yield return line;
        }
    }
}
```

- [ ] **Step 3: Write `ProseScannerTests`**

Cover: fenced blocks and their delimiters dropped; frontmatter kept; inline-code spans kept; an unterminated fence running to the end (the safe direction, and what the loops already did); CRLF leaving no carriage returns; null and empty yielding nothing; **an indented fence NOT treated as a fence** (pinning the known gap rather than leaving it undiscovered).

- [ ] **Step 4: Measure the mention-span change before adopting it**

This is a real gate, not a formality.

Write a temporary throwaway test (**do not commit it**) that runs the current `ExtractMentionSpans` and a fences-only variant over the real corpus in `.scrinia/memories/`, and reports:

- absolute mention counts for each, **and the symmetric difference of the two sets** — not just totals.

Totals alone cannot see the effect that matters. `StripInlineCode` replaces backticks *and their contents* with spaces, and in `ScanMentionLine` whitespace does **not** break a run (`:296`) while other non-word characters do (`:309`). So today an inline-code span is invisible *and transparent*: `` Alpha `x` Beta `` is the single span `alpha beta`. Without the strip it becomes two spans, `alpha` and `beta`. That changes entity-graph node **identity**, and a totals-only measurement would show +1 and call it additive.

Caveats to carry into your report: the corpus is **16 memories in one scope** (`findings/`), all agent-authored code review documents — unusually dense in fences and backticked identifiers, so the growth is an **upper bound**, not a representative sample. Report absolute numbers alongside percentages. Read content through the artifact reader; `.nmp2` is not plain text. Note also that `src/Scrinia/Services/EntityTypingEventHandler.cs:46` promotes mention values into typed keys by rewriting entries, so growth here costs write amplification, not just index size.

**If the symmetric difference is large, stop and ask.** `KnownAttributes.StateMarkerKeys` documents what a flood of low-value entity nodes does.

`EntityFactSidecar.Version` does **not** need a bump: the sidecar caches what `ParseEntityAttributes` yields from stored `Attributes`, which this does not change, and any entry whose attributes change gets a new content signature anyway. Do not bump it defensively — that would discard every sidecar.

- [ ] **Step 5: Adopt the scanner in both loops**

In `Extract`, replace the loop header and the fence/frontmatter block with `foreach (string line in ProseScanner.ProseLines(content))`, deleting the now-unused `inFence`, `firstLine` and `inFrontmatter` locals and the `---` block.

**No `---` guard is needed.** Verified: `BoldLabelPattern` requires `^\*\*`, `LineStartPairPattern` requires `^[A-Za-z]`, and `InlineProseKeyValuePattern` requires a `:` — `---` matches none of them, so `MatchAttributeLine("---")` returns `(null, null)` and `MatchInlineProsePairs("---")` yields nothing. Do not add the guard.

In `ExtractMentionSpans`, the same substitution, and **delete the `StripInlineCode` call**. Then delete `StripInlineCode` itself — it has no other caller. (An unused private *method* is not a build error here, so this is hygiene, not a forced change.)

- [ ] **Step 6: Update the two class docs**

`AttributeExtractor.cs:12` ("Conservative: nothing inside code fences, …") and `:196-197` ("outside code fences, inline-code spans, and YAML frontmatter"). Two thirds of the second is about to become false.

- [ ] **Step 7: Fix the test this changes**

`tests/Scrinia.Tests/MentionSpanExtractionTests.cs:29-34` — `CodeFencesAndInlineCode_Excluded` asserts at `:33` that `` Set `MaxRetries` and call Bob. `` yields `["bob"]`. After Step 5 it yields `["maxretries", "bob"]`. The fence assertion at `:32` still passes.

**The test's name becomes a lie**, so rename it (e.g. `CodeFences_Excluded_InlineCodeIsProse`) and rewrite its doc to say inline code is now read *and* that a backtick now breaks a mention run. Do not delete it.

Positively cleared, do not "fix" these: `AttributeExtractorTests` `:13-23` and `:47-63`, `MentionSpanExtractionTests` `:12/:19/:36/:42/:50`, `MentionSpanWiringTests:23-50`, `ScriniaMcpToolsTests:2507-2534`.

- [ ] **Step 8: Full suite, then commit**

```bash
rtk git add src/Scrinia.Core/Search/ProseScanner.cs src/Scrinia.Core/Search/AttributeExtractor.cs tests/Scrinia.Tests/ProseScannerTests.cs tests/Scrinia.Tests/MentionSpanExtractionTests.cs
rtk git commit -m "refactor(search): one prose rule, replacing two loops that disagreed"
```

---

## Task 6: References respect fences

**Files:**
- Modify: `src/Scrinia.Core/Search/ReferenceExtractor.cs` — `ExtractFileRefs` (`:49`), `ExtractMemoryRefs` (`:62`), **and the class summary at `:5`**
- Test: `tests/Scrinia.Tests/ReferenceTests.cs` — **that is the file; `ReferenceExtractorTests.cs` does not exist**

**Interfaces:** Consumes `ProseScanner.ProseLines` (Task 5).

- [ ] **Step 1: Pin pre-change behaviour first**

Before editing, write a test that records what extraction produces **today** for a fixture containing, at minimum:

- several plain paths, a `[[wiki-link]]`, a `~ephemeral`, `file:src/x.cs` after a colon, `../../src/a.cs`
- **a line-wrapped `[[\n/api/auth\n]]`** — see Step 3
- **a backtick-quoted path, pinned as ABSENT** — it has never extracted
- **a backtick-quoted `[[link]]` and `~ephemeral`, pinned as PRESENT** — these do extract
- **an indented fence, pinned as NOT a fence**

Run it against current code and record the exact output. That output is the contract: byte-identical after the change. The reference regex was fixed three times on the last branch, each round breaking a neighbour, because tests asserted expectations rather than prior behaviour.

- [ ] **Step 2: Know what does and does not extract from inline code**

**`ExtractFileRefs` has never extracted a backtick-quoted path.** `FilePathPattern` (`:35`) carries the backtick inside its lookbehind exclusion class, and every interior position of the token is preceded by a word character, `/` or `.` — so there is no restart position either. Verified empirically: `` See `src/Inline.cs` too. `` → `[]`, while `plain src/Plain.cs here` → `[src/Plain.cs]`.

**This is load-bearing, not a bug.** `:10-16` documents that narrowing the lookbehind *shifts* the match rather than suppressing it, turning `file:src/new.cs` into the corrupt `rc/new.cs`, and `ReferenceTests.cs:49-64` pins it. **Do not widen the lookbehind.** A backtick-quoted path reaches attributes only when written `` file:`src/a.cs` ``, which `StripReferenceWrappers` unwraps.

Backtick-quoted **memory** refs *do* extract — `MemoryNamePattern` has no lookbehind and `EphemeralNamePattern`'s `(?<!\w)` permits a preceding backtick.

- [ ] **Step 3: Know that one pattern spans newlines**

`MemoryNamePattern` (`:41`) is `\[\[\s*(…)\s*\]\]`, and `\s` matches `\n`. Verified: `Links to [[\n/api/auth\n]] here.` extracts `/api/auth` today. Naive per-line matching **silently loses it** — precisely the failure class this branch exists to remove.

So: `FilePathPattern` and `EphemeralNamePattern` run **per line** (proven unable to span a newline: no `\s`, no bare `.`, no `Singleline`); `MemoryNamePattern` runs over the prose lines **rejoined** with `\n`, which excludes fenced blocks while preserving cross-line matches.

- [ ] **Step 4: Write the failing fence tests**

```csharp
[Fact]
public void ExtractFileRefs_IgnoresPathsInsideAFencedBlock()
{
    string content = "See src/Real.cs.\n\n```\n  at Foo() in src/Fenced.cs:line 12\n  src/Other.cs\n```\n";
    ReferenceExtractor.ExtractFileRefs(content).Should().Equal("src/Real.cs");
}

[Fact]
public void ExtractMemoryRefs_IgnoresLinksInsideAFencedBlock()
{
    string content = "Links to [[/api/real]].\n```\n[[/api/fenced]]\n~fenced-scratch\n```\n";
    ReferenceExtractor.ExtractMemoryRefs(content).Should().Equal("/api/real");
}

/// <summary>Frontmatter stays prose — a file: line there is deliberate metadata.</summary>
[Fact]
public void ExtractFileRefs_StillReadsFrontmatter()
{
    ReferenceExtractor.ExtractFileRefs("---\nfile: src/Front.cs\n---\nbody")
        .Should().Equal("src/Front.cs");
}

/// <summary>Inline code stays prose for MEMORY refs, which is where the claim is true.
/// A backtick-quoted PATH is not asserted here because it has never extracted — see the
/// lookbehind at ReferenceExtractor.cs:35 and the comment block at :10-16.</summary>
[Fact]
public void ExtractMemoryRefs_StillReadsInlineCode()
{
    ReferenceExtractor.ExtractMemoryRefs("See `[[/api/auth]]` and `~scratch` here")
        .Should().BeEquivalentTo(["/api/auth", "~scratch"]);
}

/// <summary>A wiki link wrapped across lines still extracts: MemoryNamePattern's \s* matches \n,
/// so this pattern runs over rejoined prose rather than line by line.</summary>
[Fact]
public void ExtractMemoryRefs_StillReadsALinkWrappedAcrossLines()
{
    ReferenceExtractor.ExtractMemoryRefs("Links to [[\n/api/auth\n]] here.")
        .Should().Equal("/api/auth");
}
```

- [ ] **Step 5: Run them**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ReferenceTests"`

**Not `~ReferenceExtractor`** — that substring appears in no fully-qualified name and selects zero tests, which looks identical to passing.

Expected: the two fence tests fail; the other three pass already.

- [ ] **Step 6: Route both extractors through the scanner**

`ExtractFileRefs` runs its pattern per prose line. `ExtractMemoryRefs` runs `MemoryNamePattern` over `string.Join("\n", ProseScanner.ProseLines(content))` and `EphemeralNamePattern` per line, keeping the persistent-then-ephemeral order so `Distinct` sees the same order as before.

Do **not** write `.Cast<Match>()` — `MatchCollection` already implements `IEnumerable<Match>` on this target (`:54` proves it by calling `.Select` directly). It compiles, but it reads as though the cast is load-bearing.

Update the **class summary at `:5`** as well as the two method summaries. A class doc stating the old unqualified contract is worse than one that never mentioned it.

- [ ] **Step 7: Run the pinned fixture from Step 1**

Byte-identical, or investigate rather than update the expectation.

- [ ] **Step 8: Give the four pure negatives a positive half**

`ReferenceTests.cs:37` (`ExtractFileRefs_IgnoresUrls`), `:103` (`…UrlWithKnownExtension_StillIgnored`) and `:171` (`ExtractMemoryRefs_IgnoresNamespaces`) assert only `NotContain` — they pass if extraction returns nothing at all, which is exactly the failure this task can introduce. Add a real reference to each fixture that must still be found. `:113` (`ExtractFileRefs_ContextTable`) uses `Contain` rather than `Equal` across 20 rows, so it cannot catch a spurious *extra* extraction; tighten it to `Equal`. (`:56-59` already documents this anti-pattern in-file, so the standard exists.)

- [ ] **Step 9: Run the full suite and read the failures**

Fixtures that put a path inside a fence now produce fewer references. Each such failure is the fix working.

**Name this in your report:** `MaintenanceEventSink.cs:100` calls `ExtractMemoryRefs` directly on **every** store (`:62`) and **every** append (`:74`). So a `[[wiki-link]]` inside a fence no longer auto-links its target back, and a memory whose only inbound reference lived inside a fence can now be marked **`orphan:true`**. No existing test breaks (no fenced fixtures there), but it is a user-visible behaviour change on a write path and it goes in the CHANGELOG.

- [ ] **Step 10: Commit**

```bash
rtk git add src/Scrinia.Core/Search/ReferenceExtractor.cs tests/Scrinia.Tests/ReferenceTests.cs
rtk git commit -m "fix(search): a path in a pasted log is not a reference"
```

---

## Task 7: The tool descriptions state one rule

**Files:** Modify `src/Scrinia.Mcp/MemoryTools.cs` — `remember` content description `:111-127`, `append` content description `:267-281`

- [ ] **Step 1: Enumerate before you write**

Read both descriptions in full. Their real ranges are `:111-127` and `:267-281`. Grep `src/Scrinia.Mcp/**/*.cs` for `fenced` and `WHOLE content`.

Cleared, needing no change: `prompts/guide.md`, `prompts/scrinia-memory-skill.md`, `src/Scrinia/Commands/**`, `README.md` — none states a fence/frontmatter extraction rule.

- [ ] **Step 2: Rewrite both**

State the rule once: **no extraction route reads inside a fenced code block; frontmatter and inline code are read.**

Three things not to write:

- **Do not say "every route reads the same thing."** It is false for a multi-chunk `remember`: `MemoryDerivation.ForStore:85` feeds `Extract` from `string.Concat(content)` while `:99` feeds `ExtractRefAttributes` from `string.Join("\n", content)`, so fence state can diverge at a chunk boundary. That join asymmetry is deliberate and documented (D-02, `:96-98`) — do not "fix" it, and do not claim input parity.
- **Do not claim a backtick-quoted path becomes a `file` attribute.** It does not, and never did.
- **Preserve `:279-280`** — "this text is not scanned for proper-noun 'mention' spans — only whole-memory writes do that". Still true: `MemoryDerivation.cs:237-239` deliberately calls `Extract` and `ExtractRefAttributes` on append but not `ExtractMentionSpans` (D-22).

Keep the append caveat: an append extracts from the appended chunk only and unions what it finds, so attributes minted from a fence before this change persist until the memory is re-stored with `remember`.

- [ ] **Step 3: Build and commit**

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.cs
rtk git commit -m "docs(mcp): one extraction rule, stated once"
```

---

## Task 8: `link` works on ephemerals

**Files:**
- Modify: `src/Scrinia.Core/Models/EphemeralEntry.cs` (projection helpers — see Step 2)
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — `UpdateMeta` `:305-460`, `Link` precheck `:1014-1018`, comment `:1006-1012`
- Modify: `src/Scrinia.Mcp/MemoryTools.cs:360-364` (the `link` tool description — the user-facing sentence this task exists to make true)
- Modify: `tests/Scrinia.Tests/ReferenceTests.cs` — including **rewriting `:258-281`**
- Modify: `tests/Scrinia.Tests/Golden/__snapshots__/link-partial-failure.yaml` and `Golden/ResponseGoldenTests.cs:169-172`
- Modify: `CHANGELOG.md:126`

**Interfaces:** Consumes `MemoryDerivation.ForMetadataUpdate(ArtifactEntry, ArtifactEntry)` (`:418`) **unchanged**.

- [ ] **Step 1: Enumerate before you write**

`Scrinia.Mcp` has exactly **two** ephemeral refusals and this task changes both: `UpdateMeta:315` and `Link:1014`. The other three `IsEphemeral` sites are ephemeral **support**, not refusal — `Core.cs:237` (routing), `Core.cs:788` (`Forget`), `Lifecycle.cs:789` (`Compact`). Leave them alone; do not read "refusal" into them.

`Link` has **two** production callers: the MCP tool (`MemoryTools.cs:365`) and **the CLI `scrinia link` command** (`src/Scrinia/Commands/ScriniaCommands.Memory.cs:562`, call at `:575`). The CLI surface means Step 7's stale-prose grep must cover `src/Scrinia/Commands/`.

`UpdateMeta` has two production callers, both inside `Link` — but **fourteen test call sites** across seven files. "Reachable only through `Link`" is true of the shipping graph and false of the whole graph.

- [ ] **Step 2: There is no reusable projection — build one, in the right place**

`MemoryTools.cs:16` types `CurrentStore` as `IMemoryStore`, which exposes only `RememberEphemeral`/`ForgetEphemeral`/`GetEphemeral`. **None of the four existing `EphemeralEntry ⇄ ArtifactEntry` projections is reachable from `MemoryTools.Core.cs`** — `FileMemoryStore.ListEphemeral:326` is off-interface, `HttpMemoryStore.ListEphemeral:301` is private, and both reverse builds (`FileMemoryStore.cs:1398`, `:1972`) are private.

So a projection must be added — but **not as a third inline copy in `Scrinia.Mcp`**. There are already two forward copies, and `PersistEphemeral`'s doc (`:1384-1389`) warns that "a field present in one and null in the other means the answer depends on which read you came through". Put a `ToArtifactEntry()` / `WithMetadataFrom(ArtifactEntry)` pair on `EphemeralEntry` itself and adopt it at the existing sites too.

**`ArtifactEntry` has no `Artifact` field.** A naive Eph→Artifact→Eph round trip loses the memory's content. The write-back must be `existingEphemeral with { … }`, never a fresh `new EphemeralEntry(…)`.

- [ ] **Step 3: Extract the mutation, keeping `found` in the wrapper**

The lambda at `MemoryTools.Core.cs:338-443` closes over `found`, `changes`, and the method parameters `keywords`/`description`/`reviewAfter`/`reviewWhen` — and nothing else. Extract it as:

```csharp
private static ArtifactEntry? ApplyMetadataMutation(
    ArtifactEntry entry,
    string[]? keywords,
    string? description,
    string? reviewAfter,
    string? reviewWhen,
    List<string> changes);   // accumulator, mutated in place; null return == "no write"
```

`found` stays in the wrapper lambda: it means "the store handed us an entry", a fact about `TryUpdateEntry`, not about the mutation. `mutate` is invoked exactly once in all three implementations (`IMemoryStore.cs:107`, `FileMemoryStore.cs:1129`, `HttpMemoryStore.cs:527`), so the accumulator cannot double-count.

- [ ] **Step 4: Write the failing tests**

Add to `tests/Scrinia.Tests/ReferenceTests.cs`. The fixture members are `_tools` (a **field**, `:14`) and `MemoryStoreContext.Current!` — **there is no `Tools` property and no `Store` property**. Read a persistent side the way the file's existing `Link` tests do (`:239-241`):

```csharp
var (scope, subject) = store.ParseQualifiedName(name);
var entry = store.LoadIndex(scope).First(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
```

`ListScoped("local")` will **not** find `/notes/persistent` — that parses to scope `local:notes`, and `ListScoped` does not expand topics. Read an ephemeral with `store.GetEphemeral("scratch")`.

Write: link with one ephemeral side; link between two ephemerals; link with a **missing** side writing neither; and **one remote-mode link between two ephemerals** — ephemerals are client-side in both modes, so this is cheap and it is the only thing that proves the spec's "identically in local and remote mode" claim.

**Expected before the change: 2 of the new tests fail, not 3.** The missing-side test passes today for the wrong reason — the current precheck fires on `IsEphemeral("~absent")` regardless of existence. Keep it, but prove it by an isolated revert of the **existence** check.

The filter `FullyQualifiedName~Link_With` also captures the **existing** `ReferenceTests.cs:259` `Link_WithAnEphemeralSide_RefusesBeforeWritingEither`, which passes today and **must** fail after — `:270` asserts the response contains "ephemeral", and the new success message does not. That is the test this task invalidates; rewrite it in Step 5, preserving the history note at `:261-264`.

- [ ] **Step 5: Implement the ephemeral path in `UpdateMeta`**

The new branch must sit **before** `ParseQualifiedName` at `:321`, where the refusal sits now — `ParseQualifiedName` does not strip `~`. Use `MemoryNaming.StripEphemeralPrefix`, and render "not found" as `~{key}`, not `FormatQualifiedName(scope, subject)`.

It loads the entry, runs **the same** `ApplyMetadataMutation`, applies `MemoryDerivation.ForMetadataUpdate` on the projected pair, writes back with `RememberEphemeral(key, existing with { … })`, sets `found` itself, and handles the null (no-write) return with the same `changes.Count == 0` warning at `:452-453`.

**Record this deliberately in your report:** `ForMetadataUpdate` is documented (`MemoryDerivation.cs:401-405`, `IMemoryStore.cs:76-89`) as belonging to the store — "applied by whichever side holds the store" — and names `MemoryTools.UpdateMeta` as the caller that once re-created the defect. This adds a third call site, in `Scrinia.Mcp`. Either state plainly that it is an accepted, narrowly-scoped exception (ephemerals never cross the wire, so no derived value travels), or route the write through a new store member so the rule stays store-side. **Silence here is how the invariant erodes** — pick one and say which.

- [ ] **Step 6: Narrow `Link`'s precheck — and know that you are extending an invariant, not preserving one**

Replace the ephemeral refusal at `:1014` with an existence check on both sides.

**The comment at `:1006-1012` claims "BOTH sides are checked BEFORE either is written". That is false today for a non-existent persistent side, and the proof is checked into the repo:** `tests/Scrinia.Tests/Golden/__snapshots__/link-partial-failure.yaml` records `link('/findings/does-not-exist', '/patterns/retry')` reporting "Partial link failure" **while `/patterns/retry` shows `1 attribute(s) merged`** — the dangling one-sided `ref` the comment says is prevented. The property holds only for the ephemeral case.

So Step 6 **creates** that guarantee for non-existent memories for the first time. Three consequences:

1. The **rationale** survives (a dangling one-sided `ref` is worse than a clean refusal); two of the comment's four sentences do not. `:1006-1007` ("UpdateMeta refuses an ephemeral name outright") dies at Step 5, and `:1011-1012` ("the tool description has always said…") dies at Step 7. Rewrite the comment to state the new predicate.
2. **`link-partial-failure.yaml` changes byte-for-byte** — message, `errorCode` and `actionNeeded`. It regenerates only under `SCRINIA_UPDATE_GOLDEN=1` (`ResponseGoldenTests.cs:179`), so it will simply fail otherwise. Its seeding comment at `:169-172` becomes false and the golden's **name** becomes a lie; rename and re-comment it. (`link.yaml` at `:157` is unaffected.)
3. `Link`'s own XML summary at `:995-999` does not mention ephemerals, so it needs no change.

- [ ] **Step 7: Update the description, then hunt the stale prose**

`src/Scrinia.Mcp/MemoryTools.cs:362-363` says "Refuses if either side is an ephemeral (~name) memory." That is the sentence this whole task exists to make false.

Then apply verification job 2: this task **inverts an invariant**, so grep `**/*.cs` **and `**/*.md`** for `ephemeral` near `link`. `CHANGELOG.md:126` ("`link()` now refuses before writing anything when either side is an ephemeral") is inside `## [Unreleased]` and must be corrected here, not deferred to Task 12.

- [ ] **Step 8: Full suite, then commit**

```bash
rtk git add src/Scrinia.Core/Models/EphemeralEntry.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.cs tests/Scrinia.Tests/ReferenceTests.cs tests/Scrinia.Tests/Golden/ResponseGoldenTests.cs tests/Scrinia.Tests/Golden/__snapshots__/link-partial-failure.yaml CHANGELOG.md
rtk git commit -m "feat(mcp): link an ephemeral instead of refusing it"
```

---

## Task 9: `ReadStrings` escalates on malformed input

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:675-684`, its call sites, and the `<see cref="ReadStrings"/>` at `:535`
- Modify: `src/Scrinia.Merge/MetaJsonMerger.cs:155-162` (`GetStrings` — the same defect, six invocations)
- Test: `tests/Scrinia.Tests/ReconcileMetaProvenanceTests.cs` and `tests/Scrinia.Merge.Tests/`

- [ ] **Step 1: Enumerate**

`ReadStrings` is called on four **lines** — `:552`, `:582`, `:634`, `:635` — but each holds **two** invocations: eight in total. Plus the `<see cref="ReadStrings"/>` at `:535`, which a rename breaks into CS1574, a build failure.

`src/Scrinia.Merge/MetaJsonMerger.cs:155-162` is the driver's twin. It already uses `TryGetPropertyValue` but still collapses absent / null / non-array into `null`, so both mergers agree today and both must move together.

- [ ] **Step 2: Explicit JSON `null` is MALFORMED, matching its siblings**

`TryReadTimestampString` (`:707`) and `TryReadIntMap` (`:734`) both classify an explicit JSON `null` as malformed, each saying so in its own comment (`:713`, `:744`), and three green tests pin it (`ReconcileMetaProvenanceTests.cs:350`, `:382`, `SidecarTimestampMergeParityTests.cs:96-157`). Reading `keywords: null` as absent would leave one file with two null policies, and would let a foreign writer's explicit `null` be read as UNKNOWN provenance — the distinction this fix exists to protect.

`ReadStrings`' parameter is `JsonNode?`, **not** `JsonObject?`, so `TryGetPropertyValue` is not available without a type pattern. Keep the parameter type and follow the siblings' shape exactly:

```csharp
if (node is not JsonObject nodeObj || !nodeObj.TryGetPropertyValue(key, out var raw))
    return true; // absent
```

- [ ] **Step 3: `:552` returns `true` on malformed — the opposite of the other three**

`:582`, `:634` and `:635` are inside `TryComputeAutoResolvedMetaJson`, where `false` means escalate. **`:552` is inside `MetaSidecarsAreSimilarEnough`, whose `bool` answers "are the two sides similar enough?"** — its doc at `:523-527` is explicit: "Content that cannot be parsed returns `true` here — an unparseable side is a genuine auto-resolve failure, not a dissimilarity refusal", and its `catch` at `:554-560` returns `true` for exactly this reason.

Returning `false` there makes `ScanForConflicts` report the file as "sides too dissimilar" instead of "auto-resolve failed" — the misreport `ReconcileScanTests.cs:355-357` exists to prevent. Run and re-read `ReconcileScanTests.cs:284-360` before touching `:552`.

- [ ] **Step 4: Write tests that can actually reach the code**

**Every fixture needs `.scrinia/merge.config` = `{"jaccardThreshold":0.0}`.** The default threshold is 0.7 (`:418-425`), and `KeywordJaccard.Compute` returns 0.0 when exactly one side is empty — so a fixture with keywords on one side only is refused by the **admission gate** before `ReadStrings` is ever consulted. Follow `ReconcileMetaProvenanceTests.cs:70-72`.

There is no `TryAutoResolve` helper and none can exist — `TryComputeAutoResolvedMetaJson` is `private static`. The real shape is `SeedConflictAsync` (`:26`) → `MemoryReconcile(conflictId:)` → `ResponseParser.Parse(result).Status` → `ResolvedAsync` (`:36`). Model the tests on the `termFrequencies` siblings at `:220`, `:310`, `:350`, `:382`, each of which asserts **both** the error status **and** that the conflict markers survive.

Do **not** add a `merge.config` to `ReconcileScanTests.cs:118` or `:156` — those pin the 0.7 default deliberately.

- [ ] **Step 5: Write the matched driver-side pair**

**There is no existing parity suite covering the JSON read.** `SidecarAdmissionParityTests:44-45` feeds the Core side `string[]?` arrays directly and only the driver side JSON, so it cannot detect a divergence in how the two projects *read* `keywords`. Both helpers are private in projects that cannot reference each other, so the only way to pin them together is a matched pair: a driver-side case in `tests/Scrinia.Merge.Tests/` driving `MetaJsonMerger.Merge(…, new MergeConfig(JaccardThreshold: 0.0))` and asserting `MergeResult.Conflict`, beside the reconcile-side case from Step 4. Precedent: `SidecarTimestampMergeParityTests.cs:96-157` paired with `ReconcileMetaProvenanceTests.cs:310`/`:350`.

- [ ] **Step 6: Implement, then run everything**

Rewrite the doc comment at `:675-676`, which states the conflated behaviour as if intended, and re-check the `:529-542` paragraph that names `ReadStrings` as the lazy-throw site — the `catch` stays, but the sentence must name the new method.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.Lifecycle.cs src/Scrinia.Merge/MetaJsonMerger.cs tests/Scrinia.Tests/ReconcileMetaProvenanceTests.cs tests/Scrinia.Merge.Tests/
rtk git commit -m "fix(reconcile): a malformed keywords array is refused, not read as absent"
```

(Stage the specific new/changed files under `tests/Scrinia.Merge.Tests/`, not the directory.)

---

## Task 10: The mechanical set

Three claims, **thirty sites**. None of them is single-site; that is the point.

**Files:** see each step. **Runs after Task 9** — both touch `MemoryTools.Lifecycle.cs`.

- [ ] **Step 1: The `update()` claim — 21 sites**

`UpdateMeta` is `internal`; the twelve `[McpServerTool]` methods contain no `update`; no CLI command reaches it. Its only production callers are `Link`'s two `ref:` calls. **But tests call it directly at fourteen sites**, so the replacement wording must be **"no caller outside `Link` and the tests"** — an implementer who believes the tests cannot see it will widen its behaviour unguarded.

Production source (these ship): `MemoryTools.Core.cs:377`, `:430` (which contradicts `:428` three lines above), `MemoryDerivation.cs:549`, `MemoryEndpoints.cs:252`.
Tests: `StoreBoundaryTests.cs:26`, `RemoteStoreToolTests.cs:730`, `ScriniaMcpToolsTests.cs:2718/:2762/:2765/:2775`, `TryUpdateEntryTests.cs:108`, `MemoryDerivationTests.cs:616`, `CompactCuratedKeywordTests.cs:205/:225`, `Scrinia.Server.Tests/RemoteLoadIndexTests.cs:202`, `PatchEndpointTests.cs:249`.
`CHANGELOG.md` (all inside `## [Unreleased]`): `:204`, `:220`, `:234`, `:385` (a section **heading**), `:387`.

Leave `docs/superpowers/specs/2026-08-10-residuals-closure.md:389` — historical record.

- [ ] **Step 2: The `compact` claim — 7 sites, and the diagnosis matters**

The **behaviour** is already correct: `MemoryTools.Lifecycle.cs:954` emits `Original archived.` only when `hadIndexedEntry`. That flag is false for **two** reasons — the memory is ephemeral (set unconditionally at `:888`; the common, user-reachable case) and, rarely, a persistent artifact with no index entry. Correction text naming only the rare one would omit the case users actually hit.

Sites: `MemoryTools.Lifecycle.cs:777`; `ScriniaCommands.Memory.cs:535` (**the `scri compact --help` text**, not an internal note); `src/Scrinia.Mcp/prompts/guide.md:94` (**what `guide()` returns to every agent at session start**); `README.md:201`; `AGENTS.md:83`; `docs/cli-reference.md:162`; and `MemoryTools.cs:342-344`, which is already conditional but names only the ephemeral case.

All seven in one commit. Correcting two would leave the carve-out at one site and the unconditional claim at five — **more** divergent than before.

- [ ] **Step 3: `KeywordJaccard`'s unreachable branch**

`src/Scrinia.Core/Merge/KeywordJaccard.cs:35` — `union == 0 ? 1.0 : …`. Past `:29` both sets are non-empty, so `union >= 1`. Replace with the division in **both** copies; the driver's is at `src/Scrinia.Merge/KeywordJaccard.cs` (`:14-23`, logically identical). Leave `:28-29` and their tests alone.

- [ ] **Step 4: Verify the parity suite still binds**

Run `tests/Scrinia.Merge.Tests`. Then change **only the Core copy** to `return 0.0;` and re-run — `KeywordJaccardParityTests` must fail (it aliases both types and drives nine cases through each). Revert and confirm green.

- [ ] **Step 5: Commit with explicit paths**

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Core/MemoryDerivation.cs src/Scrinia.Server/Endpoints/MemoryEndpoints.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs src/Scrinia.Mcp/MemoryTools.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia.Mcp/prompts/guide.md src/Scrinia.Core/Merge/KeywordJaccard.cs src/Scrinia.Merge/KeywordJaccard.cs README.md AGENTS.md docs/cli-reference.md CHANGELOG.md tests/Scrinia.Tests/StoreBoundaryTests.cs tests/Scrinia.Tests/RemoteStoreToolTests.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs tests/Scrinia.Tests/TryUpdateEntryTests.cs tests/Scrinia.Tests/MemoryDerivationTests.cs tests/Scrinia.Tests/CompactCuratedKeywordTests.cs tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs tests/Scrinia.Server.Tests/PatchEndpointTests.cs
rtk git commit -m "chore: two claims the code does not support, across thirty sites"
```

---

## Task 11: Two tests that stopped testing what they name

**Depends on Task 1** — both vacuity probes edit `WriteCarriedCopy`, which does not exist until then.

**Files:** `tests/Scrinia.Tests/WriterCutoverTests.cs:215`, `tests/Scrinia.Tests/FileMemoryStoreTests.cs:189`

- [ ] **Step 1: Prove each is vacuous — running the right test**

`CopyMemory_EphemeralToPersistent_CarriesFields` asserts only `Attributes["tag"]` and `["anchor"]`, out of a 13-field carry. Any of the other twelve can be nulled and it stays green. Prove it with `TermFrequencies: null` in `WriteCarriedCopy`.

`CopyMemory_persistent_to_persistent` asserts only that an entry named `dst` exists — not `msg`, not the bytes, not one carried field. **Run only that test for the probe.** The same breakage also fails `CopyMemory_WithMismatchedSourceCasing_CarriesTheSourcesMetadata` (`:213-236`), and an implementer running the `~CopyMemory` filter would see red and invoke Step 1's own "it is not vacuous, leave it" escape hatch — leaving the vacuous test in place.

- [ ] **Step 2: Widen each**

For `WriterCutoverTests`, assert all thirteen carried fields. `ReadSidecarDirect` (`:43-48`) deserializes the complete `ArtifactEntry`, so every one is assertable.

For `CopyMemory_persistent_to_persistent`, note that its fixture carries **no metadata to assert** — the source is `new ArtifactEntry("src", "", 100, 1, DateTimeOffset.UtcNow)`. Give it metadata, and aim the widening at what `:213` does **not** already cover: `TermFrequencies`, `FileHashes`, `ChunkEntries`, `AgentKeywords`, `UpdatedAt`. Otherwise the widened test is a same-casing duplicate of `:213`.

Do not rename either — the method names were always right.

- [ ] **Step 3: Fix the comments this falsifies**

`FileMemoryStoreTests.cs:208-210` says the pre-existing test "asserts **only that the destination exists**, so it cannot see this." False the moment Step 2 lands.

`WriterCutoverTests.cs:209-212`, `:27` and `:30` refer to `Fields`, which was renamed to `Attributes`. A widened test documenting a type member that no longer exists is worse than the vacuous one.

- [ ] **Step 4: Prove the widened versions fail under the same breakages, then commit**

```bash
rtk git add tests/Scrinia.Tests/WriterCutoverTests.cs tests/Scrinia.Tests/FileMemoryStoreTests.cs
rtk git commit -m "test: two copy tests assert what their names claim"
```

---

## Task 12: Docs

**Files:** `CHANGELOG.md`, plus the enumerated list below. **Not** `docs/architecture/cli.md` — its only relevant line is the twelve-tool list at `:101`, which no task changes.

- [ ] **Step 1: The enumeration, already done — verify rather than rediscover**

| File:line | What it says | Task that falsifies it |
|---|---|---|
| `CHANGELOG.md:151-161` | remote copy broken (Known Issues) | 4 — **delete the entry**, don't soften it |
| `CHANGELOG.md:141-146` | extraction is fence-blind for paths and wiki-links | 5, 6, 7 — every clause becomes false |
| `CHANGELOG.md:147-149` | `compact`'s description no longer unconditional | 10 |
| `docs/architecture/core.md:288-292` | `Extract` "skips code fences"; `ReferenceExtractor` has no fence rule — and `:292`'s claim that it "is no longer called directly by the write paths" is **already false** (`MaintenanceEventSink.cs:100`) | 5, 6 |
| `docs/architecture/core.md:301` | drift-baseline derivation from `Attributes["file"]` — which paths reach it changes | 6 |
| `docs/architecture/core.md:347` | `IMemoryStore` member inventory | 1 |

Task 12's grep terms would surface `:288` but **not** `:292` — none of `copy`, `fenced`, `ephemeral`, `drift baseline` appears on that line. Read the section, don't grep it.

- [ ] **Step 2: Write the entries**

Cover, in the project's voice: remote copy across the ephemeral boundary and the wrong-memory symptom removed; the new route, why a route rather than a field, and the `{name}`-is-destination asymmetry; fenced blocks excluded from extraction while frontmatter and inline code stay; **the `MaintenanceEventSink` consequence** — fenced wiki-links no longer auto-link and a memory can newly become `orphan:true`; the mention-span change with Task 5's measured symmetric difference; `link` on ephemerals; the reconcile refusal on a malformed `keywords`.

State the append caveat plainly: a memory only ever appended to keeps fence-minted attributes until re-stored.

- [ ] **Step 3: Verify every claim you just wrote**

Re-read each entry against the code. Nine false claims reached the last branch's docs and thirty-six reached this plan's first draft — every one confident prose about code the author had not re-read. Anything you cannot confirm comes out.

- [ ] **Step 4: Commit with explicit paths**

```bash
rtk git add CHANGELOG.md docs/architecture/core.md
rtk git commit -m "docs: remote copy works across the ephemeral boundary; fences are not references"
```

`git add docs/` would sweep `docs/superpowers/` — this plan and spec — into the docs commit.

---

## Task order

**1 → 2 → 3 → 4** (remote copy; each gates the next)
**5 → 6 → 7** (extraction; 5 gates 6, 6 gates 7's text)
**8**, then **9**, then **10** (9 and 10 both modify `MemoryTools.Lifecycle.cs`; 10 also touches files 4 and 8 edit)
**11** — after Task 1, since its probes edit `WriteCarriedCopy`
**12** last.

Tasks 8, 9, 10 and 11 all write into `tests/Scrinia.Tests/`. With explicit-path staging they compose safely; with directory staging they do not.

## What this plan does not do

- **E2 (addressable artifacts) and E3 (remote scoring surface)** — unspecced design efforts. The `copy-from-client` route is a narrow fix that E2 should retire when it lands.
- **Attribute-value provenance** — required for an append to drop stale extracted attributes. Not started; the consequence is documented instead.
- **`~~~` fences and indented fences** — `ProseScanner` inherits both gaps from the loops it replaces, and records them.
- **Widening `FilePathPattern`'s lookbehind** so backtick-quoted paths extract. That reopens the `rc/new.cs` corruption `ReferenceTests.cs:49-64` pins.
- **Term-frequency drift** and **the punctuation trim** — both already correct; see the spec's preamble and 3.3.
- **Group G's two unverified items** — the `WorkspaceRoot` layout-migration latch and the `WithInfo` prepend/append ordering.

