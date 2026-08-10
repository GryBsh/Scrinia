# Remote Write Correctness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop remote-mode `remember()` from reporting success while discarding every piece of agent-supplied metadata, and bring the HTTP surface up to the tool surface it exposes.

**Architecture:** The remote write seam (`IMemoryStore.WriteArtifactAsync`) carries only artifact text, and the local metadata write (`Upsert`) is a no-op remotely — so attributes, keywords and the curated description are assembled locally and dropped. This plan widens that seam with an `ArtifactWriteMetadata` parameter, reorders the MCP remember path so the metadata exists before the write, then replaces the wire's legacy `Tags`/`ReviewAfter`/`ReviewWhen` carriers with the `attributes` map on both directions and both ends.

**Tech Stack:** C# / .NET 10, xunit 2.9.3, FluentAssertions 6.12.2, System.Text.Json source generation. React 19 / TypeScript / Vite for `web/`. No mocking frameworks — test doubles are hand-written classes.

**Source spec:** `docs/superpowers/specs/2026-08-08-remote-write-correctness-design.md` (revised 2026-08-10 — read the revised §1; the seam widening is not in the original).

## Global Constraints

- **Never `git add .` or `git add -A`.** `cmds.txt` is untracked, unignored, pre-existing, and is not part of this work. Stage explicit paths only.
- **Nothing is pushed.** Local commits only.
- Prefix every shell command with `rtk`, including inside `&&` chains.
- **`rtk err` is not a build gate.** Use `rtk dotnet build` and read its summary.
- **`rtk grep` silently undercounts in this repo.** Use `rtk proxy grep` for anything numeric. Also pass `--include=*.cs` — a bare `rtk proxy grep -rn` over `src/` matches compiled DLLs in `bin/`/`obj/` and buries the real hits.
- `TreatWarningsAsErrors=true` for projects not ending `.Tests`/`.Benchmarks` (`Directory.Build.props:44-46`). Test projects carry ~1723 pre-existing analyzer warnings — **warning count is not a health signal; the gate is 0 errors.**
- `tests/Scrinia.Tests/GlobalUsings.cs` declares `global using Xunit;` — do **not** add `using Xunit;` there. `tests/Scrinia.Server.Tests` has no `GlobalUsings.cs` and does need it.
- `TestHelpers.LocalStore` (`tests/Scrinia.Tests/TestHelpers.cs:83`) returns **`ILocalMemoryStore`**, not `FileMemoryStore`. `TestHelpers.StoreScope` (`:96`) is `IDisposable` with `WorkspaceDir`/`TempDir`.
- **`web/` is NOT in `Scrinia.sln`.** `rtk dotnet build Scrinia.sln` will **not** catch a TypeScript break. Any task touching `web/` must additionally run, from `web/`: `npm run build` (which is `tsc -b && vite build`). CI does gate this (`.github/workflows/ci.yml:21-22`) but the local .NET gate does not. Node `>=24.15.0 <25`.
- **Suite baseline at the branch point: 1788 passed, 0 failed.**

---

## Established facts

Verified at `cdc7050` on 2026-08-10 by the commands/reads named in each row. **These supersede the spec's own line numbers**, several of which have moved.

| # | Fact | Evidence |
|---|---|---|
| F1 | `IMemoryStore.WriteArtifactAsync(string subject, string scope, string artifactText, CancellationToken ct = default)` has **no parameter capable of carrying metadata** | `src/Scrinia.Core/IMemoryStore.cs:149` |
| F2 | The **only** construction of `StoreApiRequest` anywhere is `new StoreApiRequest([text], qualifiedName)` — content and name only | `src/Scrinia/HttpMemoryStore.cs:503`; sole match for `new StoreApiRequest` |
| F3 | `HttpMemoryStore.Upsert` is an empty no-op | `src/Scrinia/HttpMemoryStore.cs:256-260` |
| F4 | The MCP remember path writes at `MemoryTools.Core.cs:332`, then assembles the entry's metadata at `:336-415`, then calls `Upsert`. Remotely, everything after the write is discarded | as cited |
| F5 | **Nothing in `MemoryTools.Core.cs:336-415` depends on the write having happened.** `artifact` is built at `:328-330`, `joined` at `:138`, `mergedKeywords` at `:146`, `entryAttributes` at `:194-200`, `cleanedAnchors` at `:170-177` — all before `:332`. The block is hoistable wholesale | read of `:138-429` |
| F6 | 7 **caller** sites in `src/`, all passing the token **positionally** so all 7 break when `meta` is inserted before `ct`: `MemoryTools.Core.cs:332` (`cancellationToken`), `:1152` (`cancellationToken`); `MemoryTools.Lifecycle.cs:571` (`cancellationToken`); `MemoryOrchestrator.cs:118` (`ct`), `:279` (`ct`), `:326` (`CancellationToken.None`); `ScriniaCommands.Migration.cs:293` (`ct`) | `rtk proxy grep -rn "WriteArtifactAsync" src/ --include=*.cs`, each site read |
| F6a | There are **four** implementations of the member, not two. `FileMemoryStore.cs:1066` and `HttpMemoryStore.cs:497` in `src/`, **plus two test doubles**: `tests/Scrinia.Tests/FakeMemoryStore.cs:34` (`public virtual`) and `tests/Scrinia.Server.Tests/BundleEndpointTests.cs:363` (`public`, `=> Task.CompletedTask`). Both fail `CS0535` the moment the interface widens | as cited |
| F6b | Inserting `ArtifactWriteMetadata? meta = null` before `CancellationToken ct = default` genuinely breaks positional calls — verified by compiling the exact signature: `error CS1503: Argument 4: cannot convert from 'System.Threading.CancellationToken' to 'ArtifactWriteMetadata?'`. A `ct:`-named call compiles clean | scratch compile |
| F7 | Client `StoreApiRequest(string[] Content, string Name, string? Description, string[]? Tags, string[]? Keywords, string? ReviewAfter, string? ReviewWhen)` | `src/Scrinia/HttpMemoryStore.cs:106-107` |
| F8 | Server `StoreRequest` has the identical shape | `src/Scrinia.Server/Models/ApiDtos.cs:5-12` |
| F9 | The server translates the legacy request fields on **every** store request via `AttributeMigration.BuildCarrierAttributes(req.Tags, fileKeys: null, parsedReviewAfter, req.ReviewWhen, anchors: null)` | `src/Scrinia.Server/Services/MemoryOrchestrator.cs:135-137` |
| F10 | A **second** `BuildCarrierAttributes` call at `MemoryOrchestrator.cs:290-291` carries an existing entry's review forward on append. **That one stays** — it is genuine migration work, not request translation | as cited |
| F11 | Outbound, the server flattens the whole attributes map to a `string[] Tags` | `MemoryEndpoints.cs:113` (`AttributeReaders.DisplayTags(...)`) into `MemoryListItem.Tags` (`ApiDtos.cs:31-40`) |
| F12 | Client response DTOs: `ListApiItem(..., string Description, string[]? Tags)` and `SearchApiItem(string Type, string Name, double Score, string? Description, int? ChunkIndex, int? TotalChunks)` | `HttpMemoryStore.cs:111-115` |
| F13 | Server `SearchResultItem` has the same six members — **no `OriginalBytes`, no `CreatedAt`** | `ApiDtos.cs:60-66` |
| F14 | Remote search results are rebuilt with `OriginalBytes: 0, CreatedAt: DateTimeOffset.UtcNow` | `HttpMemoryStore.cs:372-377` |
| F15 | `HttpMemoryStore.LoadIndex` returns `[]` and now declares `IReadOnlyList<ArtifactEntry>` (narrowed by the store-read-path branch) | `HttpMemoryStore.cs:253-254` |
| F16 | The `LoadIndex` caller audit is **already done** — all 21 production sites classified | `docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md` |
| F17 | There is **no API version negotiation anywhere**. The only `apiVersion` matches are Azure embedding options | `rtk proxy grep -rni "apiversion" src/ --include=*.cs` |
| F18 | Web types mirror the legacy shape: `StoreRequest` with `tags?`/`reviewAfter?`/`reviewWhen?`, and `MemoryListItem` with `tags?` | `web/src/api/types.ts:3-11`, `:36-46` |
| F19 | Source-generated JSON already handles `Dictionary<string, string[]>?` on the wire — `SearchApiRequest.AttributeFilter` does it today | `HttpMemoryStore.cs:116-117`, context at `:91-103` |
| F20 | Bounds precedent for an attributes-shaped payload: 64 keys, 64 values per key, returning `BadRequest(new ErrorResponse(...))` | `MemoryEndpoints.cs:299-303` |

### Corrections this plan carries

- **The spec's F8 was wrong** and is fixed in the revised spec: remote `Upsert`'s no-op is where the data is lost, not a defensible division of labour.
- **The spec's correction of the source memory was itself wrong.** It said "tags and review dates *do* cross the wire." Per F2 they do not — the DTO declares them, the single call site never fills them. **Any test asserting that `Tags` round-trips today would pass against a wire carrying nothing.**
- F16 means §2's caller audit is done. Do not redo it; read it.

---

## Task 1: The write seam gains a metadata parameter

Pure widening. No behaviour changes, nothing is sent yet. Splitting this from Task 2 means the interface change is reviewed on its own, before anything depends on it.

**Files:**
- Create: `src/Scrinia.Core/Models/ArtifactWriteMetadata.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs:149`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1066`
- Modify: `src/Scrinia/HttpMemoryStore.cs:497`
- Test: `tests/Scrinia.Tests/ArtifactWriteMetadataTests.cs` (create)

**Interfaces — Produces:**
```csharp
public sealed record ArtifactWriteMetadata(
    IReadOnlyDictionary<string, string[]>? Attributes = null,
    string[]? Keywords = null,
    string? Description = null);
```
and `Task WriteArtifactAsync(string subject, string scope, string artifactText, ArtifactWriteMetadata? meta = null, CancellationToken ct = default)` on `IMemoryStore` and both implementations.

- [ ] **Step 1: Create the record**

`src/Scrinia.Core/Models/ArtifactWriteMetadata.cs`:

```csharp
namespace Scrinia.Core.Models;

/// <summary>
/// Metadata that must accompany an artifact write across a process boundary.
///
/// <para>This exists for exactly one reason: a remote store cannot persist an entry itself. The
/// local store receives its metadata through <c>Upsert</c> after the write and ignores this
/// parameter entirely; the remote store has no such second channel — its <c>Upsert</c> is a
/// no-op — so without this the caller's attributes, keywords and curated description were
/// assembled and then dropped on the floor.</para>
///
/// <para>Deliberately not the whole <see cref="ArtifactEntry"/>: the fields a server derives for
/// itself (chunk counts, byte counts, timestamps, term frequencies) must not be client-supplied,
/// and sending them would invite a caller to think they are authoritative.</para>
/// </summary>
/// <param name="Attributes">The agent-supplied attributes map — entities, anchors, file refs,
/// tag and review keys. The whole map, not a flattened projection of it.</param>
/// <param name="Keywords">Merged agent-supplied and auto-extracted keywords.</param>
/// <param name="Description">Curated content preview, when the caller supplied one. Wins over
/// the auto-generated snippet.</param>
public sealed record ArtifactWriteMetadata(
    IReadOnlyDictionary<string, string[]>? Attributes = null,
    string[]? Keywords = null,
    string? Description = null);
```

- [ ] **Step 2: Widen the interface**

`src/Scrinia.Core/IMemoryStore.cs:149`. Keep the existing doc if there is one and add:

```csharp
    /// <summary>
    /// Writes the artifact text for <paramref name="subject"/>.
    /// <para><paramref name="meta"/> is honoured only by stores that cannot receive metadata
    /// through <c>Upsert</c> — in practice, remote stores. <see cref="FileMemoryStore"/> ignores
    /// it by design; see the comment at its implementation.</para>
    /// </summary>
    Task WriteArtifactAsync(string subject, string scope, string artifactText,
        ArtifactWriteMetadata? meta = null, CancellationToken ct = default);
```

- [ ] **Step 3: Implement in all four implementations**

Per F6a there are **four**, not two — the two production stores and two test doubles. The test
doubles will fail `CS0535` at Step 4's build; widen their signatures to match and leave their
bodies alone (`FakeMemoryStore` returns default, `BundleEndpointTests`' double returns
`Task.CompletedTask`). Neither needs to do anything with `meta`.

`FileMemoryStore.cs:1066` — widen the signature and **comment the asymmetry at the point where it is ignored**, not somewhere a reader has to go looking:

```csharp
    public async Task WriteArtifactAsync(string subject, string scope, string artifactText,
        ArtifactWriteMetadata? meta = null, CancellationToken ct = default)
    {
        // meta is deliberately ignored. The local store receives an entry's metadata through
        // Upsert immediately after this call, which is richer than meta and is the authority.
        // The parameter exists for HttpMemoryStore, whose Upsert is a no-op because the server
        // owns persistence — for it, this call is the only channel the metadata has.
```
(keep the existing body unchanged below that comment)

`HttpMemoryStore.cs:497` — widen the signature only; wiring it to the request is **Task 3**. For now:

```csharp
    public async Task WriteArtifactAsync(string subject, string scope, string artifactText,
        ArtifactWriteMetadata? meta = null, CancellationToken ct = default)
```
Leave the body as it is. Do not touch `StoreApiRequest` in this task.

- [ ] **Step 4: Build and fix the call sites**

```bash
rtk dotnet build Scrinia.sln
```

Because `meta` is optional and precedes `ct`, existing positional calls fail with
`CS1503: cannot convert from 'System.Threading.CancellationToken' to 'ArtifactWriteMetadata?'`.
That is verified (F6b), and per F6 **all seven** caller sites pass the token positionally, so all
seven break. Fix each by naming the argument:

```csharp
await store.WriteArtifactAsync(subject, scope, artifact, ct: cancellationToken);
```

**Do not pass metadata at any of them yet** — that is Task 2's job, and only for the remember path.

Expect `CS0535` from the two test doubles as well (F6a). Total expected breakage: 7 callers +
2 test-double implementations = 9 sites. If the count differs, say so in your report before
fixing — the plan's claim to have enumerated them is then wrong.

- [ ] **Step 5: Write the test**

`tests/Scrinia.Tests/ArtifactWriteMetadataTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Pins the one asymmetry in the write seam: the local store ignores ArtifactWriteMetadata
/// because Upsert is its real metadata channel, and the remote store cannot ignore it because
/// its Upsert is a no-op. A future reader deleting the "unused" parameter from FileMemoryStore
/// would break remote writes and nothing local, so the intent is pinned here.
/// </summary>
public class ArtifactWriteMetadataTests
{
    [Fact]
    public async Task LocalWriteArtifact_IgnoresMetadata_AndUpsertRemainsTheAuthority()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        string artifact = Nmp2ChunkedEncoder.Encode("body text");
        var meta = new ArtifactWriteMetadata(
            Attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] },
            Keywords: ["ignored"],
            Description: "ignored");

        await store.WriteArtifactAsync("subject", "local", artifact, meta);

        // The write alone creates no index entry — Upsert does that, which is exactly why the
        // local store can afford to ignore meta.
        store.LoadIndex("local").Should().BeEmpty(
            because: "WriteArtifactAsync persists content; Upsert persists the entry");
    }

    [Fact]
    public async Task LocalWriteArtifact_AcceptsNullMetadata()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        string artifact = Nmp2ChunkedEncoder.Encode("body text");
        var act = async () => await store.WriteArtifactAsync("subject", "local", artifact);

        await act.Should().NotThrowAsync();
    }
}
```

If `Nmp2ChunkedEncoder` is not in `Scrinia.Core`, find its namespace with
`rtk proxy grep -rn "class Nmp2ChunkedEncoder" src/ --include=*.cs` and add the using.

- [ ] **Step 6: Run the suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```
Expected: 0 build errors; **1790 passed, 0 failed** (1788 + 2).

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Core/Models/ArtifactWriteMetadata.cs src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Tests/ArtifactWriteMetadataTests.cs
rtk git add <each call site the compiler required>
rtk git commit -m "feat(core): WriteArtifactAsync can carry write metadata across a process boundary"
```

---

## Task 2: Reorder the MCP remember path so metadata exists before the write

**The riskiest mechanical change in this plan.** It moves a 70-line block above a call. Per F5 nothing in that block depends on the write, so it is a pure hoist — but verify that yourself before trusting it.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — hoist `:345-415`, amend the call at `:332`
- Test: `tests/Scrinia.Tests/RememberWriteMetadataTests.cs` (create)

**Interfaces — Consumes:** `ArtifactWriteMetadata` and the widened `WriteArtifactAsync` from Task 1.

- [ ] **Step 1: Verify the hoist is safe before performing it**

Read `src/Scrinia.Mcp/MemoryTools.Core.cs:138-430`. Confirm each of these, and **stop and report if any is false**:

- `artifact` is assigned at `:328-330`, before the write at `:332`.
- `joined` is assigned at `:138`; `mergedKeywords` at `:146`; `cleanedAnchors` at `:170-177`; `entryAttributes` at `:194-200`. All before `:332`.
- The block `:345-415` (from the `// New model: the "review" attribute...` comment through the `entryAttributes = KnownAttributes.Merge(entryAttributes, carrier);` line) reads only from those, from method parameters, and from the filesystem. **It never reads a result of the write.**

Record in your report which lines you moved and what you checked.

- [ ] **Step 2: Perform the hoist**

Move the block currently at `:345-415` so it sits **immediately before** the `await store.WriteArtifactAsync(...)` call. Also move the `contentPreview` assignment (currently `:338-342`, the `// Merge 3 Task 2b:` comment and the statement under it) up with it — the metadata carries it.

Leave in place, after the write: `uri` (`:334`), `chunkCount`, `originalBytes`, `qualifiedName`, and the `new ArtifactEntry(...)` construction. They are not needed before the write.

**Move the code; do not rewrite it.** The block contains a comment that says it was placed "immediately before construction, so it captures the auto-derived reviewWhen (above) and the completed codeRefDict (above) — inserting any earlier would fold stale/empty values." That reasoning is still correct *relative to the things it names*, all of which move with it. Update that comment to say it now sits immediately before the **write**, and why that is still safe — otherwise the next reader sees a comment describing a position the code no longer occupies.

- [ ] **Step 3: Pass the metadata**

Replace the write call with:

```csharp
        await store.WriteArtifactAsync(subject, scope, artifact,
            new ArtifactWriteMetadata(
                Attributes: entryAttributes,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                Description: contentPreview),
            cancellationToken);
```

- [ ] **Step 4: Run the suite — this is the real test of the hoist**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```

Expected: 0 build errors; **1790 passed, 0 failed** — unchanged from Task 1. Local behaviour must be **identical**; the local store ignores `meta`, so any test failure here means the hoist changed a computed value, not that a new feature misbehaved. Investigate any failure as a hoist bug.

- [ ] **Step 5: Write a test that the metadata actually reaches the seam**

A recording store double is the only way to see this — the local store discards `meta` by design.

`tests/Scrinia.Tests/RememberWriteMetadataTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// remember() must hand the agent's attributes to the write seam, not assemble them afterwards.
/// Before the reorder they were built after WriteArtifactAsync and delivered via Upsert — which
/// works locally and silently discarded everything against a remote store.
/// </summary>
public class RememberWriteMetadataTests
{
    /// <summary>
    /// Records what WriteArtifactAsync was handed. Subclasses the existing FakeMemoryStore
    /// (tests/Scrinia.Tests/FakeMemoryStore.cs) rather than writing a fresh 23-member delegator:
    /// it is internal to this same assembly and every member is virtual.
    /// </summary>
    private sealed class RecordingStore : FakeMemoryStore
    {
        public ArtifactWriteMetadata? LastMeta { get; private set; }
        public bool WriteHappened { get; private set; }

        public override Task WriteArtifactAsync(string subject, string scope, string artifactText,
            ArtifactWriteMetadata? meta = null, CancellationToken ct = default)
        {
            LastMeta = meta;
            WriteHappened = true;
            return base.WriteArtifactAsync(subject, scope, artifactText, meta, ct);
        }
    }

    [Fact]
    public async Task Remember_HandsTheAttributesMapToTheWriteSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new RecordingStore(TestHelpers.LocalStore);
        MemoryStoreContext.Current = recording;

        await new ScriniaMcpTools().MemoryRemember(
            path: "/findings/note",
            content: ["a body"],
            attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] });

        recording.WriteHappened.Should().BeTrue("precondition: the write ran");
        recording.LastMeta.Should().NotBeNull(
            because: "the metadata must cross the seam, not arrive later via Upsert");
        recording.LastMeta!.Attributes.Should().ContainKey("person");
        recording.LastMeta.Attributes!["person"].Should().Contain("maria");
    }
}
```

**Verified for you:** `FakeMemoryStore` is `internal class FakeMemoryStore : IMemoryStore` in
namespace `Scrinia.Tests` — same assembly, accessible, and **all 23 members are virtual**, so the
subclass above is cheap. `MemoryStoreContext.Current` is `IMemoryStore`-typed
(`MemoryTools.cs:17-18`), so a `FakeMemoryStore` subclass can be assigned to it. Note
`FakeMemoryStore` deliberately does **not** implement `ILocalMemoryStore`.

**The one risk to watch:** `FakeMemoryStore`'s members return empty/default rather than delegating,
so the remember path will run against `LoadIndex() => []`, a no-op `Upsert`, and whatever its
`ParseQualifiedName`/`FormatQualifiedName`/`IsEphemeral` return. That is fine for this test — it
asserts only on what crossed the write seam — **provided the remember call completes without
throwing.** If it throws on one of those defaults, override just that member on `RecordingStore`
to return something sane. Do **not** switch to wrapping a real local store unless you must; that
means hand-delegating the whole `ILocalMemoryStore` surface.

Confirm the exact signature of `MemoryRemember` (parameter names and types) before writing the
call, with `rtk proxy grep -n -A15 "MemoryRemember" src/Scrinia.Mcp/MemoryTools.cs`, and match it.

- [ ] **Step 6: Run the suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```
Expected: 0 build errors; **1791 passed, 0 failed**.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/RememberWriteMetadataTests.cs
rtk git commit -m "fix(mcp): assemble write metadata before the write, not after it"
```

---

## Task 3: The request wire carries attributes, and drops the legacy carriers

Both ends and the web request type move together — the spec is explicit that splitting this widens the same DTOs twice and reviews the same compatibility question twice.

**Nothing is deployed over HTTP** (confirmed with the user, 2026-08-08). There is no mixed-version population, so the legacy fields come **out** rather than being demoted and carried.

**Files:**
- Modify: `src/Scrinia.Server/Models/ApiDtos.cs:5-12` (`StoreRequest`)
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs` — the store path's `BuildCarrierAttributes` call (`:135-137`) and the `parsedReviewAfter` parse above it
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` — bounds on the inbound map
- Modify: `src/Scrinia/HttpMemoryStore.cs:106-107` (`StoreApiRequest`) and `:503`
- Modify: `web/src/api/types.ts:3-11`
- Test: `tests/Scrinia.Server.Tests/StoreAttributesWireTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Per the spec: **assert on what was persisted, not on what was sent.** F2 is why this matters more here than anywhere — the field a naive test would assert on is never populated by anything, so a "does `Tags` round-trip" test would have gone green against a wire carrying nothing.

`tests/Scrinia.Server.Tests/StoreAttributesWireTests.cs` — follow the existing harness in that project (read `tests/Scrinia.Server.Tests/BundleEndpointTests.cs` for how a `WebApplicationFactory`/`TestServer` is stood up and how a store is reached afterwards; mirror it rather than inventing a second pattern). The test:

1. `POST /api/v1/stores/{store}/memories` with a body carrying
   `attributes: { "person": ["maria"], "anchor": ["§3"], "custom": ["v"] }`.
2. Read the **stored entry** back out of the server's store.
3. Assert all three keys are present on `entry.Attributes`.

Assert on a **non-tag, non-review key** (`person` or `custom`). Asserting only on `tag` would pass under the old `BuildCarrierAttributes` translation and prove nothing.

- [ ] **Step 2: Run it and confirm it fails**

```bash
rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~StoreAttributesWireTests"
```
Expected: **FAIL** — `attributes` is not a member of `StoreRequest`, so it is ignored on bind and the keys never reach the store. If it *passes*, stop and report BLOCKED: the premise is wrong.

- [ ] **Step 3: Change the server request DTO**

`src/Scrinia.Server/Models/ApiDtos.cs`:

```csharp
public sealed record StoreRequest(
    string[] Content,
    string Name,
    string? Description = null,
    Dictionary<string, string[]>? Attributes = null,
    string[]? Keywords = null);
```

`Tags`, `ReviewAfter` and `ReviewWhen` are **removed**. `tag` and `review` are attribute keys in the current model, not fields. `Description` **stays** — it is a curated `ContentPreview`, a different concept that is still current.

- [ ] **Step 4: Stop translating the request in the orchestrator**

In `MemoryOrchestrator`'s store path, delete the `parsedReviewAfter` parse and the `BuildCarrierAttributes` call that consumed `req.Tags`/`req.ReviewWhen` (currently `:131-140`), and merge the request's attributes instead:

```csharp
        if (req.Attributes is { Count: > 0 })
            attributes = KnownAttributes.Merge(attributes, NormalizeAttributes(req.Attributes));
```

**`KnownAttributes.Normalize` does not exist** — verified. `KnownAttributes`
(`src/Scrinia.Core/Search/KnownAttributes.cs`) exposes only `MaxAttributes`, `EntityKeys`,
`ReferenceKeys`, `ReviewKey`, `TagKey`, `AnchorKey`, `MentionKey`, `RecommendedKeys`,
`AttributeWeight`, `IsMultiValued`, `Merge`.

The normalisation the MCP path uses is `private static Dictionary<string, string[]>?
NormalizeAttributes(...)` at `src/Scrinia.Mcp/MemoryTools.Core.cs:1458` — **private, and in
`Scrinia.Mcp`, which `Scrinia.Server` does not reference for this.** It lowercases and trims keys,
trims values, drops empties, and returns null when nothing survives.

You have two options and must pick one, saying which and why in your report:
- **Promote it** to a shared home (`KnownAttributes` in `Scrinia.Core` is the natural one, since
  that is where `Merge` lives) and have both callers use it. Preferred — it is the same
  normalisation and two copies will drift.
- **Copy it** into the server if promoting turns out to drag other private helpers with it.

Either way the local and remote paths must normalise **identically**. Do not invent a third
normalisation.

**Leave the `BuildCarrierAttributes` call in the append path alone** (F10). It carries an existing entry's review forward and is genuine migration work. Deleting it is a regression. Do not delete the `AttributeMigration` class.

- [ ] **Step 5: Bound the inbound map**

Mirror F20's precedent for **style, placement and error shape** — but not necessarily for the
number. **`KnownAttributes.MaxAttributes` is 32**, and this is an attributes map, whereas F20's 64
bounds a *search filter*, a different thing.

**Decide the key bound before writing it, and say which you chose and why in your report.** The
question to settle: does the MCP remember path already enforce `MaxAttributes` on a caller-supplied
map? Find out —

```bash
rtk proxy grep -rn "MaxAttributes" src/ --include=*.cs
```

If it does, use `KnownAttributes.MaxAttributes` here so remote and local reject the same input.
A remote store that accepts 64 keys where the local store accepts 32 is a new divergence, and
this whole spec exists because remote and local drifted apart. If the MCP path does **not**
enforce it, mirror F20's 64 and note the inconsistency as a residual rather than inventing a
third limit.

Shape (substitute your chosen constant):

```csharp
        if (req.Attributes is { Count: > KnownAttributes.MaxAttributes })
            return Results.BadRequest(new ErrorResponse(
                $"attributes may not exceed {KnownAttributes.MaxAttributes} keys."));
        if (req.Attributes is not null && req.Attributes.Any(kv => kv.Value is { Length: > 64 }))
            return Results.BadRequest(
                new ErrorResponse("attributes may not exceed 64 values per key."));
```

Put it in the store endpoint handler alongside its existing validation. An attributes map from a client is caller-supplied metadata at the same trust level as `Tags` was, so this is not a new trust boundary — but an unbounded map is a storage-growth vector in a way a fixed set of named fields was not.

- [ ] **Step 6: Change the client request DTO and fill it**

`src/Scrinia/HttpMemoryStore.cs:106-107`:

```csharp
    internal sealed record StoreApiRequest(string[] Content, string Name, string? Description = null,
        Dictionary<string, string[]>? Attributes = null, string[]? Keywords = null);
```

Then at `:503`, actually populate it from the metadata Task 1 delivered:

```csharp
        var req = new StoreApiRequest(
            [text],
            qualifiedName,
            Description: meta?.Description,
            Attributes: meta?.Attributes?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Keywords: meta?.Keywords);
```

`ToDictionary` because `ArtifactWriteMetadata.Attributes` is `IReadOnlyDictionary` and the DTO needs a concrete `Dictionary` for source-generated serialization (F19 confirms this shape already works on the wire via `SearchApiRequest.AttributeFilter`).

- [ ] **Step 7: Move the web request type**

`web/src/api/types.ts`:

```typescript
export interface StoreRequest {
  content: string[];
  name: string;
  description?: string;
  attributes?: Record<string, string[]>;
  keywords?: string[];
}
```

Then find every use of the removed fields and fix them:

```bash
rtk proxy grep -rn "reviewAfter\|reviewWhen" web/src/
rtk proxy grep -rn "\.tags\b" web/src/
```

Updating type declarations and their compile-time fallout is all this task owns. **Do not build a UI that displays the attribute map** — that is a feature with its own design questions and is explicitly out of scope.

- [ ] **Step 8: Gate the web change separately**

```bash
cd web && rtk npm run build
```

Expected: clean. **`rtk dotnet build Scrinia.sln` cannot catch a break here** — `web/` is not in the solution. Also run `rtk npm run test` from `web/` and report the result; CI does not run it, so it may already be red for unrelated reasons — say so rather than fixing unrelated failures.

- [ ] **Step 9: Run the .NET suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```
Expected: 0 build errors; **1792 passed, 0 failed** (1791 + 1 new).

Existing tests that construct `StoreRequest`/`StoreApiRequest` with the removed fields will break. Fix each by moving the value into `attributes` under the right key (`tag` for tags, `review` for review dates/conditions — per `KnownAttributes.TagKey` and `ReviewKey`). **If a test's whole purpose was pinning the legacy carrier behaviour, do not delete it silently — report it and say what you did.**

- [ ] **Step 10: Commit**

```bash
rtk git add src/Scrinia.Server/Models/ApiDtos.cs src/Scrinia.Server/Services/MemoryOrchestrator.cs src/Scrinia.Server/Endpoints/MemoryEndpoints.cs src/Scrinia/HttpMemoryStore.cs web/src/api/types.ts tests/Scrinia.Server.Tests/StoreAttributesWireTests.cs
rtk git add <any other file the compiler or the web build required>
rtk git commit -m "feat(api)!: attributes replace tags/review carriers on the store request"
```

---

## Task 4: The response wire carries attributes instead of a flattened tag list

Per F11 the server flattens the whole attributes map to a `Tags` array, so a remote listing cannot show any non-tag attribute even when the server has it.

**Files:**
- Modify: `src/Scrinia.Server/Models/ApiDtos.cs` (`MemoryListItem`)
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:113`
- Modify: `src/Scrinia/HttpMemoryStore.cs:111-112` (`ListApiItem`) and wherever it consumes `.Tags`
- Modify: `web/src/api/types.ts` (`MemoryListItem`)
- Test: add to `tests/Scrinia.Server.Tests/StoreAttributesWireTests.cs`

- [ ] **Step 1: Write the failing test**

Add a test that stores a memory with a `person` attribute, then `GET`s the listing and asserts the response item carries `person` — not merely that it carries some tags. Assert on a **non-tag key**, for the same reason as Task 3.

- [ ] **Step 2: Run it and confirm it fails**

```bash
rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~StoreAttributesWireTests"
```
Expected: the new test **FAILS**; the Task 3 tests still pass.

- [ ] **Step 3: Change the server response DTO and projection**

`MemoryListItem`'s `string[]? Tags` becomes `Dictionary<string, string[]>? Attributes`. At `MemoryEndpoints.cs:113`, replace
`AttributeReaders.DisplayTags(item.Entry.Attributes).ToArray()` with the entry's attributes map itself.

Check whether `AttributeReaders.DisplayTags` still has other callers before assuming it is now dead:
```bash
rtk proxy grep -rn "DisplayTags" src/ --include=*.cs
```
It has MCP-side callers (`MemoryTools.Core.cs:740`, `:1092`, `:1139`) — **do not delete it.**

- [ ] **Step 4: Change the client response DTO**

`ListApiItem`'s trailing `string[]? Tags` becomes `Dictionary<string, string[]>? Attributes`. Then find where `HttpMemoryStore` reads `.Tags` off a list item and carry the map through to the `ArtifactEntry` it builds instead of discarding it.

- [ ] **Step 5: Move the web response type**

```typescript
export interface MemoryListItem {
  name: string;
  qualifiedName: string;
  scope: string;
  chunkCount: number;
  originalBytes: number;
  createdAt: string;
  updatedAt?: string;
  description: string;
  attributes?: Record<string, string[]>;
}
```
Fix the compile-time fallout. Again: type declarations only, no new UI.

- [ ] **Step 6: Gate both builds**

```bash
cd web && rtk npm run build
```
then from the repo root:
```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```
Expected: 0 build errors; **1793 passed, 0 failed**.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Server/Models/ApiDtos.cs src/Scrinia.Server/Endpoints/MemoryEndpoints.cs src/Scrinia/HttpMemoryStore.cs web/src/api/types.ts tests/Scrinia.Server.Tests/StoreAttributesWireTests.cs
rtk git commit -m "feat(api)!: listing responses carry the attributes map, not a flattened tag list"
```

---

## Task 5: Search results stop fabricating metadata

Per F14 every remote search result is rebuilt with `OriginalBytes: 0` and `CreatedAt: DateTimeOffset.UtcNow`, so any caller reading a remote result's creation time gets "now".

**Files:**
- Modify: `src/Scrinia.Server/Models/ApiDtos.cs` (`SearchResultItem`)
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` — the search projection
- Modify: `src/Scrinia/HttpMemoryStore.cs:114-115` (`SearchApiItem`) and `:372-377`
- Modify: `web/src/api/types.ts` (`SearchResultItem`)
- Test: `tests/Scrinia.Server.Tests/SearchResultMetadataTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Store a memory with known content, wait or set a known `CreatedAt`, search for it remotely, and assert the returned result's `CreatedAt` is the memory's **real** creation time and `OriginalBytes` is its real size — not `0`, not "now".

Assert `OriginalBytes` is the actual byte count. Asserting merely "not zero" would pass on any fabricated non-zero value.

- [ ] **Step 2: Run it and confirm it fails**

```bash
rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~SearchResultMetadataTests"
```
Expected: **FAIL**.

- [ ] **Step 3: Widen both DTOs and the projection**

Add `long OriginalBytes` and `DateTimeOffset CreatedAt` to the server's `SearchResultItem` and the client's `SearchApiItem`, and fill them from the real entry in the server's search projection (`MemoryEndpoints.cs`, the `results.Select<SearchResult, SearchResultItem>` block).

With nothing deployed, the fields are always present, so there is no absent-value case to represent. **One rule from an earlier draft survives and is binding:** if a genuinely-unknown value ever does need representing, do **not** use `DateTimeOffset.MinValue` — that is a fabrication with a different disguise, which is the exact defect being fixed here.

- [ ] **Step 4: Stop fabricating in the client**

At `HttpMemoryStore.cs:372-378`, replace `OriginalBytes: 0` (`:375`) and
`CreatedAt: DateTimeOffset.UtcNow` (`:377`) with the values from the response item.

**Two more fabrications sit in the same constructor** and are *not* in this task's scope:
`Uri: ""` (`:374`) and `ChunkCount: 1` (`:376`). `Uri` is §5's deferred `ArtifactUri` question.
`ChunkCount: 1` is wrong for any multi-chunk memory but needs its own wire field and its own
decision. **Record both in your report as residuals; do not fix them here** — and do not leave
the reader thinking the constructor is now honest when two of its four fabrications remain.

- [ ] **Step 5: Move the web type, gate both builds, commit**

Add the two fields to `SearchResultItem` in `web/src/api/types.ts`, then:
```bash
cd web && rtk npm run build
```
and from the repo root:
```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```
Expected: 0 build errors; **1794 passed, 0 failed**.

```bash
rtk git add src/Scrinia.Server/Models/ApiDtos.cs src/Scrinia.Server/Endpoints/MemoryEndpoints.cs src/Scrinia/HttpMemoryStore.cs web/src/api/types.ts tests/Scrinia.Server.Tests/SearchResultMetadataTests.cs
rtk git commit -m "fix(api): remote search results carry real byte counts and creation times"
```

---

## Task 6: `LoadIndex` stops returning `[]`

Per F15 `HttpMemoryStore.LoadIndex` returns `[]` unconditionally, so `IMemoryStore`'s default `TryUpdateEntry` finds nothing and returns false — entity-typing promotion writes evaporate remotely with no error.

Two candidate fixes; the spec takes the second, and so does this plan. Overriding `TryUpdateEntry` would need a new endpoint and a new DTO and would leave `LoadIndex` still lying to every other caller. Proxying `LoadIndex` to the existing listing endpoint — the way `ListScoped` already does — fixes it for every current and future caller.

**Files:**
- Modify: `src/Scrinia/HttpMemoryStore.cs:253-254`
- Test: `tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs` (create)

- [ ] **Step 1: Read the caller audit — do not redo it**

`docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md` classifies all 21 production `LoadIndex` call sites. **Read its call-site table before writing the proxy.**

Note what that audit does and does not answer. It classified callers for **mutation**. Your question is **cost**: `LoadIndex` is synchronous and the listing call is async, so the proxy will block, and `LoadIndex` becomes a network round trip where it used to be free. In your report, list which of those call sites can hold an `HttpMemoryStore` at all (several are inside `FileMemoryStore` itself and never can) and flag any that call `LoadIndex` inside a loop. If you find a loop over `LoadIndex` on a remote-reachable path, **stop and report it** rather than shipping a per-iteration round trip.

- [ ] **Step 2: Write the failing test**

Against a `TestServer`-backed remote store: store a memory, then call `LoadIndex(scope)` and assert the entry comes back. Then exercise the real motivating path — `TryUpdateEntry` — and assert the update **persists** and a subsequent read sees it.

The `TryUpdateEntry` assertion is the load-bearing one. `LoadIndex` returning data is necessary but the defect users hit is the silently-failed write.

- [ ] **Step 3: Implement the proxy**

Mirror how `ListScoped` already proxies to the listing endpoint (`HttpMemoryStore.cs:289-290` builds the URL). Return `IReadOnlyList<ArtifactEntry>` — the type was narrowed by the store-read-path branch, so do not widen it back.

Comment the blocking call explicitly: it is a real cost, it is a deliberate trade, and the alternative was a new endpoint that would have fixed one caller.

- [ ] **Step 4: Run the suite and commit**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```
Expected: 0 build errors; **1796 passed, 0 failed** (1794 + 2).

```bash
rtk git add src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs
rtk git commit -m "fix(remote): LoadIndex proxies to the listing endpoint instead of returning nothing"
```

---

## Task 7: The client fails closed against an unrecognised server

Per F5-of-the-spec, `excludeTopics`/`pathPrefix` go out as additive query params; a server that does not understand them ignores them rather than rejecting, and the client returns the unfiltered result set as though it were filtered. Per F17 there is no version negotiation anywhere.

**With nothing deployed, the old-server population is empty** — so a capability probe is machinery for a scenario that does not exist. Do the cheap thing that closes the hole: a version assertion. One field, one check, and it **fails closed**, which is the property that matters, because an unfiltered result set that looks filtered silently widens the blast radius of everything built on it.

Revisit if something is ever deployed. A capability probe is right for a real population of mixed-version servers; it is over-engineering for a client and server that ship from the same commit.

**Files:**
- Modify: `src/Scrinia.Server` — report an API version on an existing response (prefer the health endpoint; do not add a new endpoint if one already carries version-ish data)
- Modify: `src/Scrinia/HttpMemoryStore.cs`
- Test: `tests/Scrinia.Server.Tests/ApiVersionAssertionTests.cs` (create)

- [ ] **Step 1: Put the version on every response, not on a connect-time probe**

**The client never calls anything on connect** — verified: `HttpMemoryStore` has zero references
to `/health` or any other handshake, and `HealthResponse` (`ApiDtos.cs:85`) carries no version
field. So "add it to an existing response" is not available as stated: there is no existing
response the client already fetches at connect time.

Do **not** invent a connect-time probe. That is the same machinery this section rejected for the
capability-probe design, reintroduced under another name.

Instead: **the server sets an API-version response header on every response, and the client
checks it on every response it receives.** No extra round trip, no handshake, no ordering
problem, and it fails closed on the first call the client makes rather than only on a connect
that might never happen.

- Server: a middleware or endpoint filter that stamps `X-Scrinia-Api-Version` on responses under
  the `/api/v1/stores/{store}` group (`MemoryEndpoints.cs:17`).
- Client: a single check where `HttpMemoryStore` already handles responses. If the header is
  absent or unrecognised, throw — do not warn and continue.

State in your report where you put both, and why. If you find a materially better placement, take
it and say so — the requirement is the fail-closed property, not this specific mechanism.

- [ ] **Step 2: Write the failing test**

A server reporting an unrecognised API version → the client **errors**, not returns an unfiltered result set. Assert on the failure, and assert the error message names the version mismatch — a caller who hits this needs to know why.

- [ ] **Step 3: Implement, run, commit**

The check must fail closed: unknown version → refuse. Not "warn and continue".

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```
Expected: 0 build errors; **1797 passed, 0 failed**.

```bash
rtk git add <files> && rtk git commit -m "feat(remote): client refuses an unrecognised server API version"
```

---

## Task 8: CHANGELOG and docs

**Files:**
- Modify: `CHANGELOG.md` (the `[Unreleased]` block)
- Modify: any architecture doc describing the HTTP surface

- [ ] **Step 1: Add the CHANGELOG entries**

Add as the **first** section under `## [Unreleased]` — that block is ordered newest-first, and the previous branch's section sits at the top. Appending at the bottom makes the newest entry read as the oldest.

The wire change is breaking and must say so plainly:

```markdown
### Changed (breaking) — HTTP store request and response shapes

- `POST /api/v1/stores/{store}/memories` takes `attributes` (a map of key to string array)
  instead of `tags`, `reviewAfter` and `reviewWhen`. Those three were not fields at all —
  `tag` and `review` are attribute *keys*, and the server had been translating the legacy
  request shape into the attributes model on every inbound request. **Pre-change clients will
  not work.** Nothing is deployed over HTTP, so there is no migration path and none is needed.
- Listing responses carry `attributes` instead of a flattened `tags` array, so a remote listing
  can show non-tag attributes the server has always held.
- Search responses carry real `originalBytes` and `createdAt`. The client previously rebuilt
  every remote result with `0` and "now", so any caller reading a remote result's creation time
  got the time of the query.
```

and:

```markdown
### Fixed — remote writes silently discarded the agent's metadata

- `remember()` against a remote store dropped the entire attributes map, the keywords and the
  curated description. The write seam (`WriteArtifactAsync`) carried only artifact text, and the
  local metadata write (`Upsert`) is a no-op remotely, so everything assembled after the write
  went nowhere. The seam now carries an `ArtifactWriteMetadata`, and the MCP remember path
  assembles that metadata before the write rather than after it.
- `HttpMemoryStore.LoadIndex` returned an empty list unconditionally, so the interface's default
  `TryUpdateEntry` found nothing and reported failure as "not found" — entity-typing promotion
  writes evaporated remotely with no error. It now proxies to the listing endpoint. Note this
  makes `LoadIndex` a network round trip in remote mode where it was previously free.
- The client now refuses to operate against a server whose API version it does not recognise.
  Filter parameters were sent as additive query params, so a server that did not understand
  `excludeTopics`/`pathPrefix` returned an unfiltered result set that the client presented as
  filtered.
```

- [ ] **Step 2: Verify every claim against the diff**

```bash
rtk proxy git diff main --stat
rtk proxy git log --oneline main..HEAD
```

Read each bullet against the real diff. A previous branch shipped a CHANGELOG entry naming an HTTP route that did not exist, caught only by a verification pass. Confirm specifically: the route path is right (`/api/v1/stores/{store}/memories`, not `/memories` — check), the removed field names are exactly the ones removed, and `LoadIndex` really does proxy.

- [ ] **Step 3: Run both gates and commit**

```bash
cd web && rtk npm run build
```
then from the repo root:
```bash
rtk dotnet build Scrinia.sln && rtk dotnet test Scrinia.sln
```

```bash
rtk git add CHANGELOG.md <any docs touched>
rtk git commit -m "docs: record the remote write correctness changes"
```

---

## Verification pass — RUN, 2026-08-10

A subagent pass ran against this plan before it was handed to any implementer, and its findings
are folded in above. Recorded so a reader knows what was checked and what was not.

**Confirmed:** F5 — the hoist is safe, and this was the highest-stakes claim. Every statement in
`MemoryTools.Core.cs:345-415` reads only method parameters, `store`, `joined` (`:138`),
`entryAttributes` (`:194-200`), `cleanedAnchors` (`:170-177`), values it computes itself, and the
filesystem. It reads **none** of `uri`, `chunkCount`, `originalBytes`, `contentPreview`,
`qualifiedName`, or any result of the write, and nothing between `:332` and `:345` reassigns its
inputs. Task 2's premise stands. Also confirmed: F2 (one construction site repo-wide), F10 (the
second `BuildCarrierAttributes` is in `AppendAsync`, declared `:174`, and carries an existing
entry's review forward), F13/F14, F17, and the **1788 baseline** — so every derived count is right.

**Four claims were wrong and are corrected above:**

1. **SILENT — the CHANGELOG draft named `POST /memories`**, a route that does not exist. The real
   template is `POST /api/v1/stores/{store}/memories` (`MemoryEndpoints.cs:17` + `:21`). This is
   *precisely* the defect a previous branch shipped and that this plan's own Task 8 Step 2 warns
   about — written into the plan's own draft text. Fixed.
2. **SILENT — Task 7 assumed the client already calls something on connect.** It does not:
   `HttpMemoryStore` has no `/health` call and no handshake of any kind, and `HealthResponse`
   carries no version field. The implementer would have discovered this mid-task and improvised
   the connect-time probe the section explicitly rejects. Task 7 now specifies a response-header
   check instead.
3. **LOUD — F6 undercounted the implementations.** Four types implement `WriteArtifactAsync`, not
   two: the two production stores plus `FakeMemoryStore.cs:34` and `BundleEndpointTests.cs:363`.
   Both fail `CS0535` when the interface widens. Recorded as F6a.
4. **LOUD — `KnownAttributes.Normalize` does not exist.** The plan shipped it as primary code.
   The real normalisation is `private NormalizeAttributes` at `MemoryTools.Core.cs:1458`, in
   `Scrinia.Mcp`. Task 3 Step 4 now names the promote-or-copy decision explicitly.

**Also surfaced, and folded in:** `KnownAttributes.MaxAttributes` is **32** while F20's
search-filter precedent is 64 — Task 3 Step 5 now makes the implementer settle which applies
rather than defaulting to the wrong one. And `HttpMemoryStore.cs:374/:376` fabricate `Uri: ""` and
`ChunkCount: 1` alongside the two fabrications Task 5 fixes; recorded there as residuals.

**Not verified:** whether `FakeMemoryStore`'s defaults let the remember path run to completion
(Task 2 Step 5 says what to do if not), and the exact `MemoryRemember` signature.

---

## Self-review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §1 wire catches up — the seam widening | Tasks 1, 2 |
| §1 wire catches up — request direction | Task 3 |
| §1 wire catches up — response direction | Task 4 |
| §1 bounds on the attributes map | Task 3 Step 5 |
| §2 `LoadIndex` stops lying | Task 6 |
| §3 filters stop failing open | Task 7 |
| §4 search results stop fabricating metadata | Task 5 |
| §5 `ArtifactUri`, §6 remote scoring | Deferred by the spec — no task |

**Type consistency:** `ArtifactWriteMetadata(Attributes, Keywords, Description)` is defined in
Task 1 and consumed by name in Tasks 2 and 3. `WriteArtifactAsync`'s widened signature is stated
identically in Tasks 1 and 2. `StoreApiRequest` is changed once (Task 3) and not touched again.

**Ordering:** Task 1 must precede Task 2 (Task 2 consumes the parameter). Task 2 must precede
Task 3 (Task 3's client change reads `meta`, which only exists once Task 2 passes it). Tasks 4–7
are independent of each other and of Task 3, except that Task 4 edits files Task 3 touched.
