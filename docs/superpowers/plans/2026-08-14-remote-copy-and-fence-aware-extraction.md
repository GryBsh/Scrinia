# Remote Copy and Fence-Aware Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close every residual the `derivation-merge-residuals` branch produced — a copy that can cross the client/server store boundary, reference extraction that respects code fences, and eight correctness and hygiene items.

**Architecture:** A new server route carries a client-derived entry so an ephemeral can be promoted without the server re-deriving it against its own filesystem; demotion needs no wire change and is done client-side from existing GETs. A single prose scanner, lifted out of `AttributeExtractor`'s two duplicated loops, becomes the one answer to "which parts of this content are prose?" and is adopted by `ReferenceExtractor`. The hygiene items are independent single-site fixes.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, System.Text.Json source generators (AOT), xunit 2.9.3, FluentAssertions 6.12.2.

## Global Constraints

- `src/Scrinia.Merge/Scrinia.Merge.csproj` **must not gain a `ProjectReference`**. It is a standalone AOT executable. Forced duplicates are pinned by parity tests.
- `TreatWarningsAsErrors=true` for every project not ending in `.Tests` or `.Benchmarks`.
- **No mocking frameworks.** Hand-written doubles only (`tests/Scrinia.TestDoubles`).
- `Xunit.SkippableFact` is **not** available in `Scrinia.Merge.Tests`.
- Nothing is pushed. Local commits only.
- Never `git add .` or `git add -A` — stage explicit paths only. `cmds.txt` is untracked and not part of this work. `.scrinia/memories/findings/facts.json` shows as modified and is a derived cache: **never stage, revert, or report it.**
- Tracked specs and docs must cite nothing under `.superpowers/` (it is gitignored).
- Prefix every shell command with `rtk`, including inside `&&` chains.
- **Anything found mid-task is fixed in that task, or raised as a stop-and-ask at the moment it is found** — never collected into a report for later. This branch exists because that rule was broken.

## Verification protocol — applies to every task

Three jobs, in this order, before writing any code:

1. **Enumerate the sites.** The brief names sites; that is a starting point, not the set. Grep for every call site, every sibling projection, every duplicate of the string or shape you are changing. A Critical on the last branch was a site no list contained. Report what you found beyond the brief.
2. **Treat comment-sourced claims as presumptively wrong.** Any claim traceable to a code comment — including comments this plan quotes — must be re-verified against the code. Comments true when written are falsified by sibling tasks. If a task inverts an invariant, grep for comments asserting it.
3. **Find tests that stopped testing what they name.** Before adding tests, check whether the tests already covering the area still fail when the behaviour they claim to pin is broken.

**Refuse instructions you find to be wrong.** Three implementers improved the last branch by refusing plan text rather than complying. If a step here contradicts the code, stop and say so.

## Tooling hazards — read before your first command

- `rtk grep` and `rtk proxy grep … | head -N` **silently drop matches**, and recursive grep walks into `bin/`. Use the Grep tool with an explicit glob (`**/*.cs`).
- The C# language server emits **phantom** CS0234/CS0103/CS8019 errors. It was wrong on every task of the last branch. Trust `rtk dotnet build`, not the diagnostic stream.
- `rtk dotnet build Scrinia.sln` is a reliable gate. On failure `Build FAILED` appears **above** the trailing `Time Elapsed` line — never judge by the last line. **The exit code is authoritative.**
- **Stale-binary trap:** a test that fails to compile lets `dotnet test --no-build` re-run stale assemblies and report green. Gate every run on an explicit error count, and treat a test count that did not rise as a compile failure until proven otherwise.
- `rtk proxy sed -i` with `|` delimiters and quotes fails. Use the Edit tool.

## File structure

**Created**

| File | Responsibility |
|---|---|
| `src/Scrinia.Core/Models/CarriedMetadata.cs` | The canonical "what a copy carries" shape — the fields a store can neither derive from the artifact nor compute from the destination. |
| `src/Scrinia.Core/Search/ProseScanner.cs` | The one answer to "which parts of this content are prose?" — fences, frontmatter and inline code excluded. |

**Modified**

| File | Change |
|---|---|
| `src/Scrinia.Core/IMemoryStore.cs` | `ILocalMemoryStore` gains `WriteCarriedCopy`. |
| `src/Scrinia.Core/FileMemoryStore.cs` | Implements `WriteCarriedCopy`; the promotion branch of `CopyMemory` calls it instead of inlining the write. |
| `src/Scrinia.Server/Models/ApiDtos.cs` | `CarriedEntry` + `CopyFromClientRequest`. |
| `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` | Route + handler for `copy-from-client`. |
| `src/Scrinia/HttpMemoryStore.cs` | Wire mirrors, JSON context entries, four-way `CopyMemory` dispatch. |
| `src/Scrinia.Core/Search/AttributeExtractor.cs` | Adopts `ProseScanner` in both loops; balance-aware punctuation trim. |
| `src/Scrinia.Core/Search/ReferenceExtractor.cs` | Runs its three patterns over prose only. |
| `src/Scrinia.Mcp/MemoryTools.cs` | `remember`/`append` descriptions state one extraction rule. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | `UpdateMeta` ephemeral path; `Link`'s precheck narrows. |
| `src/Scrinia.Core/MemoryDerivation.cs` | `ForMetadataUpdate`'s body lifted to a shared core. |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | `ReadStrings` escalates on malformed; `compact` prose. |
| `src/Scrinia/Commands/ScriniaCommands.Memory.cs` | `compact` prose (second site). |
| `src/Scrinia.Core/Merge/KeywordJaccard.cs` + the `Scrinia.Merge` copy | Dead branch removed from both. |

---

## Task 1: `CarriedMetadata` and `WriteCarriedCopy`

Gives the store one way to write an artifact plus caller-supplied metadata without deriving. The server's new endpoint (Task 3) and the existing local promotion path both use it, so one implementation serves both.

**Files:**
- Create: `src/Scrinia.Core/Models/CarriedMetadata.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs` (add to `ILocalMemoryStore`)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1886-1921` (promotion branch calls the new member)
- Test: `tests/Scrinia.Tests/FileMemoryStoreTests.cs`

**Interfaces:**
- Produces: `Scrinia.Core.Models.CarriedMetadata` (record, 9 members, below) and
  `bool ILocalMemoryStore.WriteCarriedCopy(string destinationName, string artifact, CarriedMetadata carried, bool overwrite, out string message)`.
  Tasks 2 and 3 depend on both names exactly.

- [ ] **Step 1: Enumerate before you write**

Run the Grep tool for `CopyMemory` over `**/*.cs` and read every hit. Read `FileMemoryStore.CopyMemory` in full (it starts at `:1852`). You are extracting **only** the ephemeral→persistent branch's write; the other three branches keep their current shape in this task.

Report anything you find that this brief does not name.

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

- [ ] **Step 3: Write the failing test**

Add to `tests/Scrinia.Tests/FileMemoryStoreTests.cs`:

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
    var entry = _store.ListScoped("local").Single(e => e.Name == "promoted");
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

/// <summary>Refuses an existing destination unless overwrite is set — the same contract
/// <c>CopyMemory</c> gives, so the endpoint built on this cannot be laxer than the local path.</summary>
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
```

Add `using Scrinia.Core.Encoding;` and `using Scrinia.Core.Models;` to the file's usings if absent.

- [ ] **Step 4: Run the test to verify it fails**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriteCarriedCopy"`

Expected: **compile failure** — `WriteCarriedCopy` is not defined. That is the correct failure. Do not proceed on a green run: see the stale-binary trap above.

- [ ] **Step 5: Declare the member on `ILocalMemoryStore`**

In `src/Scrinia.Core/IMemoryStore.cs`, inside the `ILocalMemoryStore` interface:

```csharp
/// <summary>
/// Writes <paramref name="artifact"/> to <paramref name="destinationName"/> and indexes
/// <paramref name="carried"/> against it <b>verbatim — nothing is derived</b>.
///
/// <para>This is the copy rule <c>FileMemoryStore.CopyMemory</c> states inline: the artifact bytes
/// are identical by definition, so the derived metadata is identical, and re-deriving would re-hash
/// `file` values against THIS workspace and produce an entry that differs from its byte-identical
/// source. It exists as a store member because a copy can now arrive from a client whose ephemeral
/// storage this process cannot see, and that caller needs the same guarantee an in-process copy
/// gets.</para>
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

Add `using Scrinia.Core.Models;` to the file if absent.

- [ ] **Step 6: Implement it on `FileMemoryStore`**

Place it immediately above `CopyMemory` in `src/Scrinia.Core/FileMemoryStore.cs`:

```csharp
/// <inheritdoc />
public bool WriteCarriedCopy(
    string destinationName, string artifact, CarriedMetadata carried, bool overwrite,
    out string message)
{
    ArgumentNullException.ThrowIfNull(carried);

    var (dstScope, dstSubject) = ParseQualifiedName(destinationName);

    // Adopt the existing memory's casing, exactly as CopyMemory's persistent branch does: the
    // resolved subject decides whether the collision check below sees a collision, where the bytes
    // land, and what the entry is called. Asking the filesystem instead reads case-insensitively on
    // NTFS and case-sensitively elsewhere.
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

- [ ] **Step 7: Run the test to verify it passes**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~WriteCarriedCopy"`

Expected: 2 passed.

- [ ] **Step 8: Route the promotion branch through it**

Replace the body of `CopyMemory`'s ephemeral→persistent branch (currently `FileMemoryStore.cs:1886-1921`, from the `// Ephemeral → Persistent (promotion)` comment through `return true;`) with:

```csharp
            // Ephemeral → Persistent (promotion). The write itself is WriteCarriedCopy's, so the
            // in-process path and the one a remote client reaches over `copy-from-client` cannot
            // drift: the carry rule has one implementation.
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

Note the behaviour this deliberately changes: the old branch derived `ChunkCount` from `entry.ChunkCount`, the new one reads it off the artifact. For a well-formed ephemeral those agree; where they disagree the artifact is right.

- [ ] **Step 9: Run the existing promotion tests**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CopyMemory"`

Expected: all pass, including `CopyMemory_PromotingAnEphemeral_CarriesEveryMetadataField` (`FileMemoryStoreTests.cs:239`), which is the pre-existing pin on this branch's behaviour.

- [ ] **Step 10: Prove the refactor is pinned**

Temporarily change `FileHashes: carried.FileHashes` to `FileHashes: null` in `WriteCarriedCopy`. Run the same filter. `CopyMemory_PromotingAnEphemeral_CarriesEveryMetadataField` and `WriteCarriedCopy_IndexesTheCarriedMetadataWithoutDeriving` must both **fail**. Revert the change and confirm green.

If either passes with `FileHashes: null`, that test is vacuous — say so and fix it before continuing.

- [ ] **Step 11: Full build and suite**

Run: `rtk dotnet build Scrinia.sln` — expect 0 errors.
Run: `rtk dotnet test Scrinia.sln` — expect 0 failed, and a test count **2 higher** than the baseline.

- [ ] **Step 12: Commit**

```bash
rtk git add src/Scrinia.Core/Models/CarriedMetadata.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/FileMemoryStoreTests.cs
rtk git commit -m "feat(core): one implementation of the carry rule, reachable by a remote caller"
```

---

## Task 2: The wire shapes

Both sides of the new route's request, and the AOT serializer entries they need. Split from Task 3 so a reviewer can reject the contract without rejecting the handler.

**Files:**
- Modify: `src/Scrinia.Server/Models/ApiDtos.cs:144` (beside `CopyRequest`)
- Modify: `src/Scrinia/HttpMemoryStore.cs:184` (beside `CopyApiRequest`) and `:108-124` (JSON context)
- Test: `tests/Scrinia.Server.Tests/` — a round-trip test named in Step 3

**Interfaces:**
- Consumes: `Scrinia.Core.Models.CarriedMetadata` from Task 1.
- Produces: `Scrinia.Server.Models.CarriedEntry`, `Scrinia.Server.Models.CopyFromClientRequest`, and the client mirrors `HttpMemoryStore.CarriedEntryDto`, `HttpMemoryStore.CopyFromClientApiRequest`. Task 3 uses the server pair; Task 4 uses the client pair.

- [ ] **Step 1: Enumerate before you write**

Read `src/Scrinia.Server/Models/ApiDtos.cs` in full and `src/Scrinia/HttpMemoryStore.cs:108-190`. Note that **two** JSON contexts exist on the client (`HttpStoreJsonContext` at `:124` and `PatchBodyJsonContext` at `:138`) and that they differ only in null handling. The new request belongs in the first one only — it has no presence-versus-null contract.

- [ ] **Step 2: Add the server shapes**

In `src/Scrinia.Server/Models/ApiDtos.cs`, immediately after `CopyRequest` (`:144-146`):

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

- [ ] **Step 3: Write the failing round-trip test**

Create `tests/Scrinia.Server.Tests/CopyFromClientContractTests.cs`:

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

    [Fact]
    public void CopyFromClientRequest_RoundTripsThroughTheServerSerializer()
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

        string json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<CopyFromClientRequest>(json);

        back.Should().BeEquivalentTo(original);
    }

    /// <summary>Overwrite defaults to false: a body that omits it must not silently replace an
    /// existing memory.</summary>
    [Fact]
    public void CopyFromClientRequest_DefaultsOverwriteToFalse()
    {
        var back = JsonSerializer.Deserialize<CopyFromClientRequest>(
            """{"content":["c"],"entry":{"originalBytes":1}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        back!.Overwrite.Should().BeFalse();
    }
}
```

- [ ] **Step 4: Run it to verify it fails**

Run: `rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~CopyFromClientContract"`

Expected: compile failure before Step 2 is applied; 3 passed after.

- [ ] **Step 5: Add the client mirrors**

In `src/Scrinia/HttpMemoryStore.cs`, immediately after `CopyApiRequest` (`:184`):

```csharp
/// <inheritdoc cref="Scrinia.Server.Models.CarriedEntry" path="/summary"/>
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
/// DESTINATION — unlike <c>/copy</c>, where it is the source. See
/// <see cref="CopyMemory"/> for why the source cannot be named in the path.</summary>
internal sealed record CopyFromClientApiRequest(
    string[] Content, CarriedEntryDto Entry, bool Overwrite = false);
```

`Scrinia` does not reference `Scrinia.Server`, so write the summary text out rather than relying on the `inheritdoc` resolving — replace the `inheritdoc` line with the same prose used server-side if the build warns.

- [ ] **Step 6: Register both for AOT serialization**

In the `HttpStoreJsonContext` attribute list (`src/Scrinia/HttpMemoryStore.cs:111-123`), after the `CopyApiRequest` line:

```csharp
    [JsonSerializable(typeof(CopyFromClientApiRequest))]
    [JsonSerializable(typeof(CarriedEntryDto))]
```

Do **not** add them to `PatchBodyJsonContext` — that context exists solely to write nulls for PATCH's presence contract, which this request has no part in.

- [ ] **Step 7: Build and run the full suite**

Run: `rtk dotnet build Scrinia.sln` — expect 0 errors. A missing `JsonSerializable` entry surfaces at runtime, not compile time, so Task 3's tests are what actually prove Step 6.
Run: `rtk dotnet test Scrinia.sln` — 0 failed, count 3 higher than after Task 1.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia.Server/Models/ApiDtos.cs src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Server.Tests/CopyFromClientContractTests.cs
rtk git commit -m "feat(wire): a copy request that carries only what the server cannot derive"
```

---

## Task 3: The `copy-from-client` endpoint

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:32` (route) and a new handler beside `CopyMemory` (`:516`)
- Test: `tests/Scrinia.Server.Tests/CopyFromClientEndpointTests.cs`

**Interfaces:**
- Consumes: `ILocalMemoryStore.WriteCarriedCopy` and `CarriedMetadata` (Task 1); `CopyFromClientRequest`, `CarriedEntry` (Task 2).
- Produces: `POST /api/v1/stores/{store}/memories/{name}/copy-from-client`, where `{name}` is the **destination**. Task 4 calls it.

- [ ] **Step 1: Enumerate before you write**

Read `MemoryEndpoints.cs:18-46` (route table and the two constants), `:516-536` (`CopyMemory`, the handler this sits beside), `:48-119` (`RememberMemory`, whose validation order this mirrors) and `:191-199` (`ValidateAttributeBounds`).

Confirm for yourself what `ctx.Store`'s declared type is. `WriteCarriedCopy` is on `ILocalMemoryStore`, and `IMemoryStore` was deliberately split from it — if `ctx.Store` is the wider type, the handler needs the narrowing check in Step 3. If it is already the narrower one, delete that check rather than leaving dead code, and say so in your report.

- [ ] **Step 2: Register the route**

In `MemoryEndpoints.cs`, immediately after the existing copy route at `:32`:

```csharp
        group.MapPost("/memories/{name}/copy", CopyMemory);
        // {name} is the DESTINATION on this one — see CopyFromClient's doc comment.
        group.MapPost("/memories/{name}/copy-from-client", CopyFromClient);
```

- [ ] **Step 3: Write the handler**

Place it immediately after `CopyMemory` (`:536`):

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

Add `using Scrinia.Core.Encoding;` and `using Scrinia.Core.Models;` if the file lacks them.

**Encoding is deterministic, which is why the wire carries decoded chunks rather than the artifact.** `Nmp2ChunkedEncoder.EncodeChunks` is a pure function of the chunks and a constant `CharsPerLine = 76`, so re-encoding here reproduces the client's artifact byte-for-byte. Verify that claim yourself before relying on it (`src/Scrinia.Core/Encoding/Nmp2ChunkedEncoder.cs:48`); if it is false, stop and raise it, because the carry rule depends on it.

- [ ] **Step 4: Write the endpoint tests**

Create `tests/Scrinia.Server.Tests/CopyFromClientEndpointTests.cs`. Follow the existing harness in `tests/Scrinia.Server.Tests/` for building a test server with an API key — read a neighbouring endpoint test file first and reuse its fixture rather than inventing one.

```csharp
/// <summary>
/// The promotion carries a drift baseline naming a file that does NOT exist in the server's
/// workspace. That is the assertion that matters: derivation hashes `file` values against the local
/// filesystem, so a baseline surviving intact can only have been carried. A test that merely
/// asserted the memory exists would pass while the server silently re-derived it to empty — the
/// exact defect this route exists to prevent.
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

    var resp = await Client.PostAsJsonAsync($"{Base}/memories/promoted/copy-from-client", body);

    resp.StatusCode.Should().Be(HttpStatusCode.OK);

    var entryResp = await Client.GetAsync($"{Base}/memories/promoted/entry");
    var entry = await entryResp.Content.ReadFromJsonAsync<MemoryEntry>();
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
    await Client.PostAsJsonAsync($"{Base}/memories",
        new StoreRequest(["UNRELATED server-side content"], "collide"));

    var body = new CopyFromClientRequest(
        Content: ["the client's content"],
        Entry: new CarriedEntry(5, null, null, null, null, null, null, null),
        Overwrite: true);
    var resp = await Client.PostAsJsonAsync($"{Base}/memories/collide/copy-from-client", body);

    resp.StatusCode.Should().Be(HttpStatusCode.OK);

    var show = await Client.GetFromJsonAsync<ShowResponse>($"{Base}/memories/collide");
    show!.Content.Single().Should().Be("the client's content");
    show.Content.Single().Should().NotContain("UNRELATED");
}

[Fact]
public async Task CopyFromClient_RefusesAnExistingDestinationWithoutOverwrite()
{
    var body = new CopyFromClientRequest(
        ["first"], new CarriedEntry(1, null, null, null, null, null, null, null));
    (await Client.PostAsJsonAsync($"{Base}/memories/taken/copy-from-client", body))
        .StatusCode.Should().Be(HttpStatusCode.OK);

    var again = await Client.PostAsJsonAsync($"{Base}/memories/taken/copy-from-client", body);

    again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    (await again.Content.ReadAsStringAsync()).Should().Contain("already exists");
}

[Fact]
public async Task CopyFromClient_RejectsAnEmptyContentArray()
{
    var body = new CopyFromClientRequest(
        [], new CarriedEntry(0, null, null, null, null, null, null, null));

    var resp = await Client.PostAsJsonAsync($"{Base}/memories/empty/copy-from-client", body);

    resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    (await resp.Content.ReadAsStringAsync()).Should().Contain("content is required");
}
```

- [ ] **Step 5: Run the endpoint tests**

Run: `rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~CopyFromClientEndpoint"`

Expected: 4 passed.

- [ ] **Step 6: Prove the wrong-memory test is not vacuous**

Temporarily change the handler to resolve `{name}` in its own store first — replace the `WriteCarriedCopy` call with `ctx.Store!.CopyMemory(decoded, decoded, req.Overwrite, out string msg)`. `CopyFromClient_IgnoresASameNamedMemoryInTheServersOwnStore` must **fail**. Revert and confirm green.

- [ ] **Step 7: Full build and suite**

Run: `rtk dotnet build Scrinia.sln` — 0 errors.
Run: `rtk dotnet test Scrinia.sln` — 0 failed, count 4 higher than after Task 2.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia.Server/Endpoints/MemoryEndpoints.cs tests/Scrinia.Server.Tests/CopyFromClientEndpointTests.cs
rtk git commit -m "feat(server): accept a copy whose source lives only on the client"
```

---

## Task 4: Four-way copy dispatch on the client

The user-visible fix. Promotion stops silently copying the wrong memory; demotion stops vanishing into the server's own ephemeral map.

**Files:**
- Modify: `src/Scrinia/HttpMemoryStore.cs:786-827` (`CopyMemory`)
- Test: `tests/Scrinia.Tests/RemoteStoreToolTests.cs`

**Interfaces:**
- Consumes: `CopyFromClientApiRequest`, `CarriedEntryDto` (Task 2); the route from Task 3; the existing `ToArtifactEntry` (`:205`), `GetEphemeral` (`:292`), `RememberEphemeral` (`:277`), `SendSync` (`:29`), `ShowApiResponse` (`:149`).

- [ ] **Step 1: Enumerate before you write**

Read `HttpMemoryStore.cs:786-827` and, for the shape a demotion must build, `FileMemoryStore.cs:1958-1989` — the local persistent→ephemeral branch. Match its field-by-field construction, including its `sourceEntry?.` null tolerance and its `$"Loaded from {sourceName}"` preview fallback, so local and remote demotion produce the same entry.

Read `:507-521` for the established "GET the entry, deserialize, handle 404" pattern.

- [ ] **Step 2: Write the failing tests**

Add to `tests/Scrinia.Tests/RemoteStoreToolTests.cs`, following the file's existing `RemoteStoreScope` fixture:

```csharp
/// <summary>
/// Promotion sends the client's own derived metadata to the destination route. The assertion is on
/// the REQUEST: a promotion that posted to `/copy` with a stripped name is the defect, and it would
/// still return 200 from a server that has an unrelated memory of that name.
/// </summary>
[Fact]
public void CopyMemory_PromotingAnEphemeral_PostsTheCarriedEntryToTheDestinationRoute()
{
    using var scope = new RemoteStoreScope(okJson: """{"message":"ok"}""");
    var store = scope.Store;
    store.RememberEphemeral("scratch", new EphemeralEntry(
        Name: "scratch",
        Artifact: Nmp2ChunkedEncoder.EncodeChunks(["notes about src/Local.cs"]),
        OriginalBytes: 23,
        ChunkCount: 1,
        CreatedAt: DateTimeOffset.UtcNow,
        ContentPreview: "scratch preview",
        Keywords: ["kw"],
        TermFrequencies: new Dictionary<string, int> { ["kw"] = 3 },
        UpdatedAt: null,
        ChunkEntries: null,
        FileHashes: new Dictionary<string, string> { ["src/Local.cs"] = "client-hash" },
        Attributes: new Dictionary<string, string[]> { ["file"] = ["src/Local.cs"] },
        AgentKeywords: ["kw"]));

    bool ok = store.CopyMemory("~scratch", "/notes/promoted", overwrite: false, out string msg);

    ok.Should().BeTrue(msg);
    scope.LastRequestPath.Should().EndWith("/copy-from-client");
    scope.LastRequestPath.Should().Contain("promoted");
    scope.LastRequestBody.Should().Contain("client-hash");
    scope.LastRequestBody.Should().Contain("\"originalBytes\":23");
}

/// <summary>A 404 means the server predates the route. It must surface as a refusal — never as a
/// copy that appeared to work, which is the failure mode the separate route exists to make
/// impossible.</summary>
[Fact]
public void CopyMemory_PromotingAgainstAnOlderServer_ReportsARefusalRatherThanSuccess()
{
    using var scope = new RemoteStoreScope(statusCode: HttpStatusCode.NotFound);
    var store = scope.Store;
    store.RememberEphemeral("scratch", NewEphemeral("scratch"));

    bool ok = store.CopyMemory("~scratch", "/notes/promoted", overwrite: false, out string msg);

    ok.Should().BeFalse();
    msg.Should().Contain("does not support");
}

/// <summary>Demotion writes into THIS process's ephemeral map. Before this, the request went to the
/// server, which copied into its own map and reported success the client could never see.</summary>
[Fact]
public void CopyMemory_DemotingToAnEphemeral_WritesLocallyAndCarriesMetadata()
{
    using var scope = new RemoteStoreScope(entryJson: OneEntryJson, showJson: OneShowJson);
    var store = scope.Store;

    bool ok = store.CopyMemory("/notes/source", "~demoted", overwrite: false, out string msg);

    ok.Should().BeTrue(msg);
    var eph = store.GetEphemeral("demoted");
    eph.Should().NotBeNull();
    eph!.TermFrequencies.Should().NotBeNull();
    eph.FileHashes.Should().NotBeNull();
    eph.Attributes.Should().NotBeNull();
}

/// <summary>A missing client-side source fails without a request: there is nothing to ask the
/// server about, and asking is how the old path found the wrong memory.</summary>
[Fact]
public void CopyMemory_PromotingAMissingEphemeral_SendsNoRequest()
{
    using var scope = new RemoteStoreScope(okJson: """{"message":"ok"}""");

    bool ok = scope.Store.CopyMemory("~absent", "/notes/x", overwrite: false, out string msg);

    ok.Should().BeFalse();
    msg.Should().Contain("was not found");
    scope.RequestCount.Should().Be(0);
}
```

`RemoteStoreScope` may not expose `LastRequestPath`, `LastRequestBody` or `RequestCount` yet. Extend the existing fixture — do not add a second one. If the fixture cannot record requests, add that recording as part of this task and say so in your report.

`NewEphemeral(string)`, `OneEntryJson` and `OneShowJson` are helpers you add beside the tests; write them from the shapes in `HttpMemoryStore.MemoryEntry` (`:153`) and `ShowApiResponse` (`:149`).

- [ ] **Step 3: Run them to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RemoteStoreToolTests.CopyMemory"`

Expected: the four new tests fail. Confirm the failure reason is the behaviour, not a missing fixture member — fix the fixture first if so.

- [ ] **Step 4: Rewrite `CopyMemory`**

Replace `src/Scrinia/HttpMemoryStore.cs:786-827` entirely:

```csharp
    /// <summary>
    /// Four directions, three homes. Ephemeral memories live in THIS process
    /// (<see cref="_ephemeral"/>); persistent ones live on the server. A copy that crosses that
    /// boundary cannot be a server-side name lookup, because the server has never seen the client's
    /// ephemeral storage — it used to be exactly that, which 400'd when no same-named persistent
    /// memory existed and silently copied an unrelated one when it did.
    /// </summary>
    public bool CopyMemory(string src, string dst, bool overwrite, out string message)
    {
        bool srcEphemeral = IsEphemeral(src);
        bool dstEphemeral = IsEphemeral(dst);

        if (srcEphemeral && dstEphemeral)
            return CopyEphemeralToEphemeral(src, dst, overwrite, out message);
        if (srcEphemeral)
            return PromoteToServer(src, dst, overwrite, out message);
        if (dstEphemeral)
            return DemoteToThisProcess(src, dst, overwrite, out message);

        // Persistent → persistent: both names are the server's, so the server resolves both.
        string encoded = Uri.EscapeDataString(src);
        var req = new CopyApiRequest(dst, overwrite);
        var content = JsonContent.Create(req, HttpStoreJsonContext.Default.CopyApiRequest);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/memories/{encoded}/copy") { Content = content };
        using var resp = SendSync(httpReq);

        if (resp.IsSuccessStatusCode)
        {
            message = $"Copied '{src}' to '{dst}'.";
            return true;
        }

        message = $"Error copying '{src}' to '{dst}': {resp.StatusCode}";
        return false;
    }

    private bool CopyEphemeralToEphemeral(string src, string dst, bool overwrite, out string message)
    {
        string srcKey = MemoryNaming.StripEphemeralPrefix(src);
        string dstKey = MemoryNaming.StripEphemeralPrefix(dst);
        var entry = GetEphemeral(srcKey);
        if (entry is null)
        {
            message = $"Error: source memory '{src}' was not found.";
            return false;
        }
        if (!overwrite && GetEphemeral(dstKey) is not null)
        {
            message = $"Error: destination memory '{dst}' already exists. Set overwrite=true to replace it.";
            return false;
        }
        RememberEphemeral(dstKey, entry with { Name = dstKey });
        message = $"Copied '{src}' to '{dst}'.";
        return true;
    }

    /// <summary>Ephemeral → persistent. The entry this process derived travels with the artifact,
    /// because the server would otherwise re-hash `file` values against ITS filesystem and hand back
    /// a promoted memory whose drift baseline is empty or wrong.</summary>
    private bool PromoteToServer(string src, string dst, bool overwrite, out string message)
    {
        var entry = GetEphemeral(MemoryNaming.StripEphemeralPrefix(src));
        if (entry is null)
        {
            // No request: there is nothing to ask the server about, and asking is how the old path
            // landed on an unrelated memory of the same name.
            message = $"Error: source memory '{src}' was not found.";
            return false;
        }

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(entry.Artifact);
        string[] chunks = new string[chunkCount];
        for (int i = 0; i < chunkCount; i++)
            chunks[i] = Nmp2ChunkedEncoder.DecodeChunk(entry.Artifact, i);

        var body = new CopyFromClientApiRequest(
            Content: chunks,
            Entry: new CarriedEntryDto(
                OriginalBytes: entry.OriginalBytes,
                UpdatedAt: entry.UpdatedAt,
                Description: entry.ContentPreview,
                Attributes: entry.Attributes,
                Keywords: entry.Keywords,
                TermFrequencies: entry.TermFrequencies,
                ChunkEntries: entry.ChunkEntries?
                    .Select(c => new ChunkEntryDto(c.ChunkIndex, c.ContentPreview, c.Keywords, c.TermFrequencies))
                    .ToArray(),
                FileHashes: entry.FileHashes,
                AgentKeywords: entry.AgentKeywords),
            Overwrite: overwrite);

        string encodedDst = Uri.EscapeDataString(dst);
        var content = JsonContent.Create(body, HttpStoreJsonContext.Default.CopyFromClientApiRequest);
        using var httpReq = new HttpRequestMessage(
            HttpMethod.Post, $"{BaseUrl}/memories/{encodedDst}/copy-from-client") { Content = content };
        using var resp = SendSync(httpReq);

        if (resp.IsSuccessStatusCode)
        {
            message = $"Copied '{src}' to '{dst}'.";
            return true;
        }

        // A 404 is the route being absent, not the memory: this route creates its destination and
        // resolves nothing, so it has no "not found" of its own. Reported as a refusal so a
        // promotion against an older server can never read as a copy that worked.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            message = $"Error copying '{src}' to '{dst}': this server does not support promoting an "
                + "ephemeral memory (no copy-from-client route). Upgrade the server, or store the "
                + "memory persistently instead.";
            return false;
        }

        message = $"Error copying '{src}' to '{dst}': {resp.StatusCode}";
        return false;
    }

    /// <summary>Persistent → ephemeral. Written here, in the process that owns ephemeral storage.
    /// Sending it to the server put the copy in the SERVER's ephemeral map and reported a success
    /// the caller could never observe.</summary>
    private bool DemoteToThisProcess(string src, string dst, bool overwrite, out string message)
    {
        string dstKey = MemoryNaming.StripEphemeralPrefix(dst);
        if (!overwrite && GetEphemeral(dstKey) is not null)
        {
            message = $"Error: destination memory '{dst}' already exists. Set overwrite=true to replace it.";
            return false;
        }

        string encoded = Uri.EscapeDataString(src);

        MemoryEntry? stored;
        using (var entryReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/memories/{encoded}/entry"))
        using (var entryResp = SendSync(entryReq))
        {
            if (entryResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                message = $"Error: source memory '{src}' was not found.";
                return false;
            }
            if (!entryResp.IsSuccessStatusCode)
            {
                message = $"Error copying '{src}' to '{dst}': {entryResp.StatusCode}";
                return false;
            }
            using var stream = entryResp.Content.ReadAsStream();
            stored = JsonSerializer.Deserialize(stream, HttpStoreJsonContext.Default.MemoryEntry);
        }

        ShowApiResponse? show;
        using (var showReq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/memories/{encoded}"))
        using (var showResp = SendSync(showReq))
        {
            if (!showResp.IsSuccessStatusCode)
            {
                message = $"Error copying '{src}' to '{dst}': {showResp.StatusCode}";
                return false;
            }
            using var stream = showResp.Content.ReadAsStream();
            show = JsonSerializer.Deserialize(stream, HttpStoreJsonContext.Default.ShowApiResponse);
        }

        if (stored is null || show is null)
        {
            message = $"Error copying '{src}' to '{dst}': the server returned a 2xx with no body.";
            return false;
        }

        var carried = ToArtifactEntry(stored);
        // Field-for-field the same construction FileMemoryStore's demotion branch uses, including
        // its preview fallback, so a local and a remote demotion produce the same entry.
        RememberEphemeral(dstKey, new EphemeralEntry(
            Name: dstKey,
            Artifact: Nmp2ChunkedEncoder.EncodeChunks(show.Content),
            OriginalBytes: show.OriginalBytes,
            ChunkCount: show.Content.Length,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: carried.ContentPreview ?? $"Loaded from {src}",
            Keywords: carried.Keywords,
            TermFrequencies: carried.TermFrequencies,
            UpdatedAt: carried.UpdatedAt,
            ChunkEntries: carried.ChunkEntries,
            FileHashes: carried.FileHashes,
            Attributes: carried.Attributes,
            AgentKeywords: carried.AgentKeywords));

        message = $"Copied '{src}' to '{dst}'.";
        return true;
    }
```

Note what changed beyond the split: the persistent→persistent branch no longer strips a `~` from `src`, because it can no longer be reached with one.

- [ ] **Step 5: Run the tests**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RemoteStoreToolTests.CopyMemory"`

Expected: all pass.

- [ ] **Step 6: Prove the promotion test is not vacuous**

Temporarily change `PromoteToServer` to post to `$"{BaseUrl}/memories/{encodedDst}/copy"` instead. `CopyMemory_PromotingAnEphemeral_PostsTheCarriedEntryToTheDestinationRoute` must **fail** on the path assertion. Revert and confirm green.

- [ ] **Step 7: Full build and suite**

Run: `rtk dotnet build Scrinia.sln` — 0 errors.
Run: `rtk dotnet test Scrinia.sln` — 0 failed, count 4 higher than after Task 3.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Tests/RemoteStoreToolTests.cs
rtk git commit -m "fix(remote): a copy across the ephemeral boundary reaches the right memory"
```

---

## Task 5: `ProseScanner`, and one exclusion set

`AttributeExtractor` has two line loops with **different** exclusion sets. `Extract` (`:53`) skips fenced blocks and deliberately **reads** frontmatter — `:7` names frontmatter as the first source of attributes — and does not strip inline code. `ExtractMentionSpans` (`:200`) skips fences, skips frontmatter (`:222`), and strips inline code (`:224`).

They reconcile onto **fenced blocks only**. That is the only direction that destroys nothing documented: moving both to the mention-span set would stop `Extract` reading frontmatter.

**This changes mention-span behaviour** — frontmatter and inline code now contribute spans, which feed the entity graph. Step 5 measures that before you keep it.

**Files:**
- Create: `src/Scrinia.Core/Search/ProseScanner.cs`
- Modify: `src/Scrinia.Core/Search/AttributeExtractor.cs:59-94`, `:208-226`
- Test: `tests/Scrinia.Tests/ProseScannerTests.cs`, and existing `AttributeExtractor` tests

**Interfaces:**
- Produces: `public static IEnumerable<string> Scrinia.Core.Search.ProseScanner.ProseLines(string? content)` — the content's lines with fenced blocks and their delimiters removed, everything else verbatim. Task 6 consumes it.

- [ ] **Step 1: Enumerate before you write**

Grep `**/*.cs` for `inFence` and for ` ``` `. Read every hit. There are two loops in `AttributeExtractor` this task touches; if you find a third anywhere, stop and report it before changing anything.

Read `AttributeExtractor.cs:1-20` (class doc), `:53-97`, `:193-231`, and `StripInlineCode`.

- [ ] **Step 2: Write `ProseScanner`**

```csharp
namespace Scrinia.Core.Search;

/// <summary>
/// The one answer to "which parts of this content are prose?" — the single line scanner every
/// extractor runs through.
///
/// <para><b>Fenced blocks only.</b> A fenced block is bulk machine output: a pasted build log, a
/// diff, a config dump. Nobody is citing the paths inside it, so minting attributes and drift
/// baselines from them is noise at best and a false baseline at worst. Everything else the author
/// typed on purpose:</para>
/// <list type="bullet">
/// <item><b>YAML frontmatter is prose here.</b> It is where deliberate <c>key: value</c> metadata
/// lives, and <see cref="AttributeExtractor.Extract"/> has always read it.</item>
/// <item><b>Inline-code spans are prose here.</b> <c>see `src/Foo.cs`</c> is the commonest way a
/// person cites a file; excluding it would drop the most deliberate reference form there is.</item>
/// </list>
///
/// <para>This replaces two loops that disagreed: <c>Extract</c> read frontmatter and kept inline
/// code, while <c>ExtractMentionSpans</c> skipped both. One rule now, so the tool descriptions can
/// state it once and be true of every route.</para>
///
/// <para><b>Only ``` opens a fence, not ~~~.</b> Inherited from the loops this replaces, and left
/// alone deliberately: widening it is a behaviour change with its own blast radius, not a
/// tidy-up. Recorded here so the next reader does not take it for an oversight.</para>
/// </summary>
public static class ProseScanner
{
    /// <summary>The prose lines of <paramref name="content"/>: every line outside a fenced block,
    /// with the ``` delimiter lines themselves omitted. Line order and content are otherwise
    /// verbatim — no trimming beyond the trailing <c>\r</c> a CRLF split leaves.</summary>
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

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class ProseScannerTests
{
    [Fact]
    public void ProseLines_DropsFencedBlocksAndTheirDelimiters()
    {
        string content = "before\n```\ninside one\ninside two\n```\nafter";

        ProseScanner.ProseLines(content).Should().Equal("before", "after");
    }

    /// <summary>Frontmatter is prose: it is where deliberate key: value metadata lives, and
    /// Extract has always read it.</summary>
    [Fact]
    public void ProseLines_KeepsYamlFrontmatter()
    {
        string content = "---\nfile: src/A.cs\n---\nbody";

        ProseScanner.ProseLines(content).Should().Equal("---", "file: src/A.cs", "---", "body");
    }

    /// <summary>Inline code is prose: a backtick citation is the commonest deliberate file
    /// reference there is.</summary>
    [Fact]
    public void ProseLines_KeepsInlineCodeSpans()
    {
        ProseScanner.ProseLines("see `src/Foo.cs` for it").Should().Equal("see `src/Foo.cs` for it");
    }

    /// <summary>An unterminated fence swallows the rest of the document. That is the safe
    /// direction — the alternative extracts from what is probably still machine output — and it is
    /// what the loops this replaces already did.</summary>
    [Fact]
    public void ProseLines_TreatsAnUnterminatedFenceAsRunningToTheEnd()
    {
        ProseScanner.ProseLines("before\n```\nno close\nstill inside").Should().Equal("before");
    }

    [Fact]
    public void ProseLines_HandlesCrlfWithoutLeavingCarriageReturns()
    {
        ProseScanner.ProseLines("a\r\n```\r\nx\r\n```\r\nb").Should().Equal("a", "b");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ProseLines_IsEmptyForNoContent(string? content)
    {
        ProseScanner.ProseLines(content).Should().BeEmpty();
    }
}
```

- [ ] **Step 4: Run them**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ProseScannerTests"`

Expected: 7 passed.

- [ ] **Step 5: Measure the mention-span change before adopting it**

Before changing `ExtractMentionSpans`, write a temporary throwaway test (do not commit it) that runs both the current implementation and a fences-only variant over the real corpus in `.scrinia/memories/`, and prints the total mention count for each.

Report both numbers. **If the fences-only count is more than ~25% higher, stop and ask** — the `orphan:true` hub-node pathology (`KnownAttributes.StateMarkerKeys`) is what a flood of low-value entity nodes looks like, and it is cheaper to catch here than after it is indexed.

If growth is modest, continue.

- [ ] **Step 6: Adopt the scanner in both loops**

In `Extract` (`:53-97`), replace the loop header and the fence/frontmatter block with:

```csharp
        foreach (string line in ProseScanner.ProseLines(content))
        {
            var (key, value) = MatchAttributeLine(line);
```

Delete the now-unused `inFence`, `firstLine` and `inFrontmatter` locals and the `---` block. Note what this changes: the `---` delimiter lines are now offered to `MatchAttributeLine`, which they were not before. Confirm `MatchAttributeLine("---")` yields no key; if it does, keep a `if (line.Trim() == "---") continue;` guard and say so in your report.

In `ExtractMentionSpans` (`:200-231`), the same substitution, and **delete the `StripInlineCode` call** at `:224`:

```csharp
        foreach (string line in ProseScanner.ProseLines(content))
        {
            if (!ScanMentionLine(line, values, seen))
                break; // per-memory cap reached
        }
```

If `StripInlineCode` now has no callers, delete it too — an unused private helper is a claim that something still uses it.

- [ ] **Step 7: Update the class doc**

`AttributeExtractor.cs:12` says "Conservative: nothing inside code fences, …". Extend it to name the one rule and where it lives:

```csharp
/// Conservative: every route reads only what <see cref="ProseScanner"/> calls prose — fenced code
/// blocks excluded, frontmatter and inline code included — plus URL-remainder values skipped, a
/// tight key charset, and the recommended-key gate on inline pairs.
```

Also update `ExtractMentionSpans`' summary (`:196-197`), which currently claims spans are taken "outside code fences, inline-code spans, and YAML frontmatter". Two thirds of that is about to become false.

- [ ] **Step 8: Run the full suite and read the failures**

Run: `rtk dotnet test Scrinia.sln`

Existing `AttributeExtractor` tests asserting frontmatter or inline-code exclusion for mention spans **will fail**. Each is a real behaviour change, not a broken test: update it to the new expectation and rewrite its doc comment to say why. Do not delete a failing test.

- [ ] **Step 9: Commit**

```bash
rtk git add src/Scrinia.Core/Search/ProseScanner.cs src/Scrinia.Core/Search/AttributeExtractor.cs tests/Scrinia.Tests/ProseScannerTests.cs tests/Scrinia.Tests/AttributeExtractorTests.cs
rtk git commit -m "refactor(search): one prose rule, replacing two loops that disagreed"
```

---

## Task 6: References respect fences

The residual itself. Paste a fenced build log today and every path in it becomes a `file` attribute **and** gets hashed into the drift baseline — which Tasks 5 and 6 of the previous branch extended to appends and ephemerals.

**Files:**
- Modify: `src/Scrinia.Core/Search/ReferenceExtractor.cs:49-75`
- Test: `tests/Scrinia.Tests/ReferenceExtractorTests.cs`

**Interfaces:** Consumes `ProseScanner.ProseLines` from Task 5.

- [ ] **Step 1: Capture pre-change behaviour first**

Before editing, write a test that pins what extraction produces for **prose without any fence** — several paths, a `[[wiki-link]]`, a `~ephemeral`, a `file:src/x.cs` after a colon, `../../src/a.cs`, and a backtick-quoted `` `src/Foo.cs` ``. Run it against the **current** code and record the exact output.

This is the technique that matters here: the reference regex was fixed three times on the last branch, and each round fixed the case in front of it while silently breaking a neighbour, because the tests asserted expectations rather than prior behaviour. Your pinned output is the contract — it must be **byte-identical** after the change.

- [ ] **Step 2: Write the failing fence tests**

```csharp
/// <summary>A pasted build log is bulk machine output, not a citation. Every path in it used to
/// become a `file` attribute AND get hashed into the drift baseline, so the memory reported a clean
/// baseline for files it never meant to reference.</summary>
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

/// <summary>Frontmatter and inline code stay prose — both are deliberate citations. This is the
/// half of the rule that must NOT change, and it is the easier half to break by accident.</summary>
[Fact]
public void ExtractFileRefs_StillReadsFrontmatterAndInlineCode()
{
    string content = "---\nfile: src/Front.cs\n---\nSee `src/Inline.cs` too.";

    ReferenceExtractor.ExtractFileRefs(content).Should().BeEquivalentTo(["src/Front.cs", "src/Inline.cs"]);
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ReferenceExtractor"`

Expected: the two fence tests fail; the frontmatter/inline test passes already.

- [ ] **Step 4: Route both extractors through the scanner**

Replace the bodies of `ExtractFileRefs` (`:49`) and `ExtractMemoryRefs` (`:62`) so each pattern runs per prose line rather than over the whole string. None of the three patterns spans a newline, so per-line matching changes nothing else.

```csharp
    /// <summary>Extract file path references from content. Reads only what
    /// <see cref="ProseScanner"/> calls prose: fenced blocks are excluded, frontmatter and inline
    /// code are not. A path inside a pasted log is not a reference anyone made.</summary>
    public static string[] ExtractFileRefs(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return ProseScanner.ProseLines(content)
            .SelectMany(line => FilePathPattern().Matches(line).Cast<Match>())
            .Select(m => m.Value.TrimStart('.', '/', '\\'))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc cref="ExtractFileRefs"/>
    /// <summary>Extract memory name references ([[/path]] / [[name]] wiki-links and ~ephemeral)
    /// from content, reading only prose per <see cref="ProseScanner"/>.</summary>
    public static string[] ExtractMemoryRefs(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var lines = ProseScanner.ProseLines(content).ToArray();
        var persistent = lines
            .SelectMany(line => MemoryNamePattern().Matches(line).Cast<Match>())
            .Select(m => m.Groups[1].Value);
        var ephemeral = lines
            .SelectMany(line => EphemeralNamePattern().Matches(line).Cast<Match>())
            .Select(m => m.Groups[1].Value);

        return persistent.Concat(ephemeral)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
```

`ExtractMemoryRefs` materializes `lines` because it enumerates twice; `ProseLines` is a lazy iterator and re-enumerating would re-scan. Keep the two-pass order — persistent before ephemeral — because the result order is what `Distinct` preserves and Step 1's pinned output depends on it.

- [ ] **Step 5: Run Step 1's pinned test**

The pre-change output must be reproduced **exactly**. A single changed or reordered element means per-line matching altered something — investigate rather than updating the expectation.

- [ ] **Step 6: Run the full suite**

Run: `rtk dotnet test Scrinia.sln` — 0 failed.

Watch for failures in drift and attribute tests: a fixture whose content puts a path inside a fence will now produce fewer references. Each such failure is the fix working; update the fixture's expectation and note it in your report.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Core/Search/ReferenceExtractor.cs tests/Scrinia.Tests/ReferenceExtractorTests.cs
rtk git commit -m "fix(search): a path in a pasted log is not a reference"
```

---

## Task 7: The tool descriptions state one rule

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs:112-127` (`remember`), `:267-277` (`append`)
- Test: none — description text. Verified by reading.

- [ ] **Step 1: Enumerate before you write**

Read both descriptions in full. Grep `src/Scrinia.Mcp/**/*.cs` for `fenced` and for `WHOLE content` and read every hit — there may be sites beyond the two this brief names, and a description that keeps the old rule is worse than one that never mentioned it.

- [ ] **Step 2: Rewrite both**

The old text enumerates which routes skip fences and says file paths are read "over the WHOLE content — fenced blocks and frontmatter included." After Tasks 5 and 6 that is false in both halves. Replace with the single rule, keeping the existing voice and the practical guidance:

- Every extraction route reads the same thing: everything except fenced code blocks.
- Frontmatter and inline-code spans **are** read — `` `src/Foo.cs` `` in prose is a reference on purpose.
- A path in a fenced block contributes nothing: no attribute, no drift baseline.
- The append caveat, stated plainly: an append extracts from the appended chunk only and unions what it finds, so attributes minted from a fence before this change stay until the memory is re-stored with `remember`.

Do not invent a claim you have not verified. If you cannot confirm a statement against the code, leave it out.

- [ ] **Step 3: Build and commit**

Run: `rtk dotnet build Scrinia.sln` — 0 errors (these are `[Description]` string literals; a broken concatenation is a compile error).

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.cs
rtk git commit -m "docs(mcp): one extraction rule, stated once"
```

---

## Task 8: `link` works on ephemerals

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:305-460` (`UpdateMeta`), `:1014-1018` (`Link`'s precheck)
- Test: `tests/Scrinia.Tests/` — the file that already covers `Link`; find it in Step 1

**Interfaces:** Consumes the existing `MemoryDerivation.ForMetadataUpdate(ArtifactEntry current, ArtifactEntry updated)` (`MemoryDerivation.cs:418`) **unchanged**.

- [ ] **Step 1: Enumerate before you write**

Read `UpdateMeta` in full (`:305` to its close) and `Link` (`:1000-1035`). Find the existing `Link` tests. Grep `**/*.cs` for `IsEphemeral` in `Scrinia.Mcp` and read every hit — other tools refuse ephemerals too, and this task changes only `UpdateMeta`'s and `Link`'s refusals.

Find how an `EphemeralEntry` projects to an `ArtifactEntry` today. `ListEphemeral` in `FileMemoryStore` and `HttpMemoryStore` both build one. **Reuse an existing projection if one is reachable; add a second only if none is, and say which you did.**

- [ ] **Step 2: Note the shape that makes this small**

`UpdateMeta`'s mutation is already a lambda over `ArtifactEntry` handed to `store.TryUpdateEntry` (`:338`), and `ForMetadataUpdate` takes and returns `ArtifactEntry`. So the ephemeral path needs **no new derivation rule**: project `EphemeralEntry → ArtifactEntry`, run the same lambda, run the existing `ForMetadataUpdate`, project back, and write through `RememberEphemeral`.

Extract the lambda body into a named private method so both paths call one implementation. Do **not** copy it.

- [ ] **Step 3: Write the failing tests**

```csharp
/// <summary>link() refused when either side was ephemeral, because UpdateMeta writes through
/// TryUpdateEntry and ephemerals live in a separate map. EphemeralEntry has carried Attributes for
/// some time; only the write path was missing.</summary>
[Fact]
public async Task Link_WithAnEphemeralSide_WritesTheRefOnBothMemories()
{
    // Arrange: one persistent memory and one ephemeral, both existing.
    await Tools.Remember(["persistent content"], "/notes/persistent");
    await Tools.Remember(["scratch content"], "~scratch");

    var response = await Tools.Link("~scratch", "/notes/persistent");

    response.IsError.Should().BeFalse(response.Error);
    var eph = Store.GetEphemeral("scratch");
    eph!.Attributes!["ref"].Should().Contain("/notes/persistent");
    var persistent = Store.ListScoped("local").Single(e => e.Name == "persistent");
    persistent.Attributes!["ref"].Should().Contain("~scratch");
}

/// <summary>Both sides are still checked before either is written. A link that half-succeeds leaves
/// a dangling one-sided ref pointing at a memory that was never linked back — worse than either a
/// clean link or a clean refusal.</summary>
[Fact]
public async Task Link_WithAMissingEphemeralSide_WritesNeitherSide()
{
    await Tools.Remember(["persistent content"], "/notes/persistent");

    var response = await Tools.Link("~absent", "/notes/persistent");

    response.IsError.Should().BeTrue();
    var persistent = Store.ListScoped("local").Single(e => e.Name == "persistent");
    (persistent.Attributes?.ContainsKey("ref") ?? false).Should().BeFalse();
}

[Fact]
public async Task Link_BetweenTwoEphemerals_WritesBothRefs()
{
    await Tools.Remember(["one"], "~alpha");
    await Tools.Remember(["two"], "~beta");

    var response = await Tools.Link("~alpha", "~beta");

    response.IsError.Should().BeFalse(response.Error);
    Store.GetEphemeral("alpha")!.Attributes!["ref"].Should().Contain("~beta");
    Store.GetEphemeral("beta")!.Attributes!["ref"].Should().Contain("~alpha");
}
```

Adapt the fixture names (`Tools`, `Store`) to whatever the `Link` test file already uses.

- [ ] **Step 4: Run to verify they fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Link_With"`

Expected: all three fail on the current refusal.

- [ ] **Step 5: Implement the ephemeral path in `UpdateMeta`**

Replace the outright refusal at `:315-319` with a branch that does the work. The persistent path is unchanged. Requirements the tests pin:

- Loads the `EphemeralEntry` by its stripped key; returns the same structured `not found` error shape the persistent path uses when it is absent.
- Runs the **same** mutation method the persistent path runs.
- Applies `MemoryDerivation.ForMetadataUpdate(projectedCurrent, projectedUpdated)` so the `+3` attribute seeding and `+5` keyword boost reach an ephemeral exactly as they reach a persistent memory. Skipping it would make an ephemeral's term frequencies diverge from a persistent memory given identical input — a store-mode divergence, the class this codebase keeps removing.
- Writes back with `RememberEphemeral(key, updatedEphemeral)`.
- Returns the same response shape, so a caller cannot tell which branch ran except by the name it passed.

- [ ] **Step 6: Narrow `Link`'s precheck**

At `:1014`, replace the ephemeral refusal with an existence check on both sides. Keep the both-sides-before-either-write property and its comment (`:1006-1012`) — that is load-bearing and its reason has not changed. Only the predicate moves from "is ephemeral" to "does not exist".

Update `Link`'s summary (`:995-999`) if it mentions the ephemeral refusal.

- [ ] **Step 7: Run the tests, then hunt the stale comments**

Run the filter from Step 4 — expect 3 passed. Then run the full suite.

Then apply verification job 2: this task **inverts an invariant** ("link refuses ephemerals"), so grep `**/*.cs` for `ephemeral` within 3 lines of `link` and read every hit, including test doc comments and the `link` tool description in `MemoryTools.cs`. On the last branch a comment falsified by a sibling task was found three separate times. Fix every one you find.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.cs tests/Scrinia.Tests/
rtk git commit -m "feat(mcp): link an ephemeral instead of refusing it"
```

---

## Task 9: `ReadStrings` escalates on malformed input

`MemoryTools.Lifecycle.cs:679` is `if (node?[key] is not JsonArray array) return null;` — absent, JSON `null`, and "present but not an array" all return null. Every other sidecar field now refuses rather than guesses. Absent must stay null: for `agentKeywords`, absent means UNKNOWN provenance and `MemoryDerivation` depends on it.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:675-684` and its four call sites (`:552`, `:582`, `:634`, `:635`)
- Test: `tests/Scrinia.Tests/` — the reconcile/auto-merge test file

- [ ] **Step 1: Enumerate before you write**

Grep `**/*.cs` for `ReadStrings` and read **every** call site — the brief names four; confirm the count yourself. Read `TryReadIntMap` and `TryReadTimestampString` in the same file: they are the escalate-on-malformed shape this is being aligned to, and their `Try`-returning signature is the pattern to copy.

Check whether `Scrinia.Merge`'s `MetaJsonMerger` has its own copy of this read. If it does, it moves too, and the parity suite covers both. If it does not, say so.

- [ ] **Step 2: Know the trap before you write the code**

`node?[key]` returns a **null reference for both** "key absent" and "key present with value JSON `null`" — System.Text.Json represents a JSON null as a null node. Only `JsonObject.TryGetPropertyValue` distinguishes them. This exact trap produced parity gaps twice on the previous branch. Use `TryGetPropertyValue`.

Decide and state which bucket JSON `null` falls in. Recommended: treat explicit `null` as **absent** (it carries the same "no value recorded" meaning), and escalate only on a present, non-null, non-array value. Write that decision into the doc comment.

- [ ] **Step 3: Write the failing tests**

```csharp
[Fact]
public void AutoMerge_RefusesASidecarWhoseKeywordsAreNotAnArray()
{
    // "keywords": "not-an-array" is malformed, not absent. Refusing surfaces the conflict to a
    // human; the old read returned null and merged on as though the side carried no keywords.
    string ours = """{"name":"x","keywords":"not-an-array"}""";
    string theirs = """{"name":"x","keywords":["b"]}""";

    bool merged = TryAutoResolve(ours, theirs, out _);

    merged.Should().BeFalse();
}

[Fact]
public void AutoMerge_TreatsAnAbsentKeywordsKeyAsNoKeywords()
{
    string ours = """{"name":"x"}""";
    string theirs = """{"name":"x","keywords":["b"]}""";

    bool merged = TryAutoResolve(ours, theirs, out string result);

    merged.Should().BeTrue();
    result.Should().Contain("\"b\"");
}

[Fact]
public void AutoMerge_TreatsAnExplicitJsonNullAsAbsentRatherThanMalformed()
{
    string ours = """{"name":"x","keywords":null}""";
    string theirs = """{"name":"x","keywords":["b"]}""";

    bool merged = TryAutoResolve(ours, theirs, out _);

    merged.Should().BeTrue();
}
```

Adapt `TryAutoResolve` to the helper the existing reconcile tests use.

- [ ] **Step 4: Run to verify the first and third fail**

Run the reconcile test filter. The malformed case currently merges (wrongly) and the null case currently merges (rightly) — so expect exactly one failure before the change, and confirm which.

- [ ] **Step 5: Implement**

Change `ReadStrings` to a `Try`-shaped read matching its neighbours, returning false only for a present, non-null, non-array value. Update every call site to propagate the refusal the way `TryReadIntMap`'s callers already do. Rewrite the doc comment at `:675-676`, which currently states the conflated behaviour as if it were intended.

- [ ] **Step 6: Run everything, including the parity suite**

Run: `rtk dotnet test Scrinia.sln` — 0 failed. `Scrinia.Merge.Tests`' parity suites must stay green; if a shared rule moved, they are what proves both copies moved together.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.Lifecycle.cs tests/Scrinia.Tests/
rtk git commit -m "fix(reconcile): a malformed keywords array is refused, not read as absent"
```

---

## Task 10: The mechanical set

Four independent single-site corrections, each verified false against `main` at `bc7fdb3`. No behaviour changes.

**Files:**
- Modify: `tests/Scrinia.Tests/StoreBoundaryTests.cs:26`, `tests/Scrinia.Tests/RemoteStoreToolTests.cs:730`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:777`, `src/Scrinia/Commands/ScriniaCommands.Memory.cs:535`
- Modify: `src/Scrinia.Core/Merge/KeywordJaccard.cs:35` and the `Scrinia.Merge` copy

- [ ] **Step 1: Enumerate before you write**

For each of the three claims below, grep for **every** site of the string or shape — the line numbers here are where they were found, not proof they are the only ones. `compact`'s prose was found twice this way after being reported once.

- [ ] **Step 2: The two comments that name a tool that does not exist**

`UpdateMeta` is `internal`, is called by no CLI command, and is reachable only through `Link`'s two hardcoded `ref:` calls. There is no public `update()` MCP tool.

- `StoreBoundaryTests.cs:26` — "the MCP tools that need a metadata-only write — `update()` and `link()`, both via `MemoryTools.UpdateMeta`".
- `RemoteStoreToolTests.cs:730` — "`UpdateMeta` — reached by `update()` and by both halves of `link()`".

Correct both to describe the real caller graph. The surrounding claims about `TryUpdateEntry`, `PATCH` and the keyword refusal are accurate — leave them.

- [ ] **Step 3: `compact`'s archive prose, both sites**

`MemoryTools.Lifecycle.cs:777` and `ScriniaCommands.Memory.cs:535` both read "Compact a multi-chunk memory by merging chunks. Archives the original version." It is imprecise for the no-index-entry case. Correct both **in the same commit** — correcting one is how the two drift.

- [ ] **Step 4: `KeywordJaccard`'s unreachable branch**

`src/Scrinia.Core/Merge/KeywordJaccard.cs:35` ends `return union == 0 ? 1.0 : (double)intersection / union;`. By that line `:28-29` has returned for every empty combination, so both sets are non-empty, `setA.Count >= 1`, and `union >= 1`. The branch cannot be taken.

Replace with `return (double)intersection / union;` in **both** copies — Core and the `Scrinia.Merge` one. Leave `:28-29` and their tests alone: those are the reachable empty-side behaviour the dead branch is easily mistaken for.

- [ ] **Step 5: Verify the parity suite still binds**

Run: `rtk dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj`

Expected: green. Then change **only the Core copy** to `return 0.0;` and re-run — `KeywordJaccardParityTests` must fail. Revert and confirm green. If it passes with the copies disagreeing, the parity test is vacuous; fix it and say so.

- [ ] **Step 6: Build, full suite, commit**

```bash
rtk git add tests/Scrinia.Tests/StoreBoundaryTests.cs tests/Scrinia.Tests/RemoteStoreToolTests.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia.Core/Merge/KeywordJaccard.cs src/Scrinia.Merge/
rtk git commit -m "chore: three claims the code does not support, and one branch it cannot reach"
```

---

## Task 11: Two tests that stopped testing what they name

**Files:**
- Modify: `tests/Scrinia.Tests/WriterCutoverTests.cs:215`, `tests/Scrinia.Tests/FileMemoryStoreTests.cs:189`

- [ ] **Step 1: Prove each is vacuous before changing it**

For `WriterCutoverTests.cs:215` (`CopyMemory_EphemeralToPersistent_CarriesFields`): it asserts only `Attributes`, while the path it names carries thirteen fields. Break a different carried field in `WriteCarriedCopy` — set `TermFrequencies: null` — and confirm the test still **passes**. That is the proof.

For `FileMemoryStoreTests.cs:189` (`CopyMemory_persistent_to_persistent`): it asserts only that the destination exists. Break the metadata carry on the persistent→persistent branch and confirm it still passes.

Record both results. If either already fails, it is not vacuous — say so and leave it.

- [ ] **Step 2: Widen each to assert what its name claims**

`CopyMemory_EphemeralToPersistent_CarriesFields` must assert every field the promotion carries, not one. `CopyMemory_persistent_to_persistent` must assert the destination carries the source's metadata, not merely that a file appeared.

Do not rename them: the names were always right — the bodies were not.

- [ ] **Step 3: Prove the widened versions are not vacuous**

Repeat Step 1's breakage for each. Both must now **fail**. Revert and confirm green.

- [ ] **Step 4: Commit**

```bash
rtk git add tests/Scrinia.Tests/WriterCutoverTests.cs tests/Scrinia.Tests/FileMemoryStoreTests.cs
rtk git commit -m "test: two copy tests assert what their names claim"
```

---

## Task 12: Docs

**Files:**
- Modify: `CHANGELOG.md`, `docs/architecture/core.md`, `docs/architecture/cli.md` if either describes extraction or copy

- [ ] **Step 1: Enumerate before you write**

Grep `docs/` and `*.md` for `copy`, `fenced`, `ephemeral` and `drift baseline`. Read the hits. Architecture docs describing extraction as fence-blind, or remote copy as working, are now false.

- [ ] **Step 2: Remove the Known Issue**

`CHANGELOG.md:151-161` documents remote copy as broken. That is now fixed — **delete the entry** rather than editing it to say "partly fixed", and describe the fix under the release's Fixed section.

- [ ] **Step 3: Write the CHANGELOG entries**

Cover, in the project's existing voice:

- Remote copy across the ephemeral boundary, both directions, and the wrong-memory symptom it removes.
- The new route, why it is a route rather than a field, and that `{name}` is the destination on it.
- Reference extraction skipping fenced blocks, and explicitly that frontmatter and inline-code citations still count.
- The mention-span change from Task 5, with the measured number from its Step 5.
- `link` on ephemerals.
- The reconcile refusal on a malformed `keywords` array.

State the append caveat plainly: a memory that is only appended to keeps attributes minted from a fence until it is re-stored.

- [ ] **Step 4: Verify every claim you just wrote**

Re-read each entry against the code. Nine false claims reached the last branch's docs; every one was confident prose about code the author had not re-read. Anything you cannot confirm comes out.

- [ ] **Step 5: Commit**

```bash
rtk git add CHANGELOG.md docs/
rtk git commit -m "docs: remote copy works across the ephemeral boundary; fences are not references"
```

---

## Task order

1 → 2 → 3 → 4 (remote copy; each gates the next)
5 → 6 → 7 (extraction; 5 gates 6, 6 gates 7's text)
8, 9, 10, 11 (independent — any order)
12 last, so one docs pass covers everything.

## What this plan does not do

- **E2 (addressable artifacts) and E3 (remote scoring surface)** — unspecced design efforts. The `copy-from-client` route is a narrow fix that E2 should retire when it lands.
- **Attribute-value provenance** — required for an append to drop stale extracted attributes. Not started; the consequence is documented instead.
- **`~~~` fences** — `ProseScanner` inherits `` ``` ``-only from the loops it replaces.
- **Term-frequency drift** — closed as ruled at `SidecarTermFrequencyMerge.cs:23-26`, not reopened.
- **Group G's two unverified items** — the `WorkspaceRoot` layout-migration latch and the `WithInfo` prepend/append ordering.

