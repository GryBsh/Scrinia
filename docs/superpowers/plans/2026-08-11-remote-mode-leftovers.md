# Remote-Mode Leftovers (Group C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close Group C of `docs/superpowers/specs/2026-08-10-residuals-closure.md` — the four
remote-mode leftovers from `remote-write-correctness` — and, by the user's explicit ruling at plan
time, every other instance of the case-sensitivity defect class the re-verification found. The spec
named 2 sites. There are **10**, and three of them lose data.

**Architecture:** One defect class in ten places, plus two unrelated one-file fixes. No new
abstractions and no new endpoints — the PATCH endpoint that would make remote metadata writes
actually persist is E4, explicitly out of scope.

**Tech Stack:** C#/.NET 10, xunit 2.9.3, FluentAssertions 6.12.2, no mocking frameworks
(hand-written doubles in `tests/Scrinia.TestDoubles`).

## Global Constraints

- **`cmds.txt` is untracked, pre-existing, and is not part of this work.** Never run `git add .`
  or `git add -A`. Stage explicit paths only. Do not delete, move, edit, or stage `cmds.txt`.
- **Nothing is pushed.** Local commits only. No `git push`, no PR.
- Prefix every shell command with `rtk`, including inside `&&` chains.
- **`src/Scrinia.Merge/Scrinia.Merge.csproj` must not gain a `ProjectReference` to anything.** Not
  touched by this plan; the constraint stands regardless.
- **No new deferred-followups.** Anything found mid-branch is fixed in this branch, or raised as an
  explicit decision at the time. Never written to a `/findings/` memory.
- Tracked specs and docs must cite nothing under `.superpowers/` (it is gitignored).
- `TreatWarningsAsErrors=true` for every project not ending in `.Tests`/`.Benchmarks`
  (`Directory.Build.props:44-46`). **The gate is 0 errors, not 0 warnings.**
- **Verify against `rtk dotnet build Scrinia.sln` only.** The C# language server in this workspace
  emits phantom `CS0101`/`CS1061`/`CS0234`/`CS8019` errors, including for files that do not exist
  on disk. Do not trust the diagnostic stream; trust the build.
- **`rtk grep` silently undercounts, and so does `rtk proxy grep … | head -N`.** Both were measured
  dropping real matches during this plan's verification. Use the Grep tool for any search whose
  completeness matters.
- Full suite before any task is reported DONE: `rtk dotnet test Scrinia.sln`. Baseline at branch
  point is **1849 passed, 0 failed** (measured, not assumed).

---

## Established facts

Every claim carries the command or `file:line` that produced it. All were re-read at branch point
`bc033da` and then independently re-checked by a hostile verification pass, which falsified four of
them; the corrections are folded in below and the failure modes are noted where they bite.

**F1. `SanitizeName` does not fold case.** `FileMemoryStore.cs:279-294` replaces `..`, `/`, `\` and
a portable-invalid character set with `_`, then takes `Path.GetFileName`. No case transformation.
So `SanitizeName("MixedCase") == SanitizeName("mixedcase")` is **false** under ordinal `==`.

**F2. Ten production sites compare a memory name against a subject case-sensitively.** Three
compare *sanitized* names; seven compare `e.Name` against a `subject` that came from
`ParseQualifiedName`. Verified with the **Grep tool** (`\.Name == |== subject|== srcSubject`), after
the same sweep run through `rtk proxy grep | head` returned only three.

*Sanitized-name sites (Task 1):*
- `src/Scrinia.Core/IMemoryStore.cs:41` — the interface default (spec item 1)
- `src/Scrinia/Services/EntityTypingEventHandler.cs:39` — the upstream gate (spec item 2)
- `src/Scrinia/HttpMemoryStore.cs:409` — **not in the spec's table**

*Raw-comparison sites (Task 2), all of the form `FirstOrDefault(e => e.Name == subject)`:*

| Site | What a case mismatch costs |
|---|---|
| `src/Scrinia.Server/Services/MemoryOrchestrator.cs:113` | **`ArchiveVersion` skipped** (`:117-118`) and `CreatedAt` reset (`:114`), then `WriteArtifactAsync` overwrites the same NTFS file. The prior version is gone with no archive. |
| `src/Scrinia.Server/Services/MemoryOrchestrator.cs:257` | Append path: `existingEntry?.ChunkEntries` is null, so `updatedChunks` becomes `[newChunkEntry]` alone — **every prior chunk entry dropped** — and `CreatedAt` resets. |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:579` | Compact: the metadata read that the comment at `:571-578` says is "the only channel it has" returns null, so attributes/keywords/description are **not carried through the rewrite**. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs:320` | The MCP twin of `:113` — same archive-skip and `CreatedAt` reset. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs:1163` | Append path: `CreatedAt` reset, prior `ContentPreview` not preserved. |
| `src/Scrinia.Core/FileMemoryStore.cs:1512` | `CopyMemory` → ephemeral destination: source metadata not found, destination built from defaults. |
| `src/Scrinia.Core/FileMemoryStore.cs:1540` | `CopyMemory` → persistent destination: same. |

`src/Scrinia.Server/Chat/Providers/OpenAiChatProvider.cs:100` also matches the regex and is
**unrelated** — a tool-call function name, not a memory subject. Do not touch it.

**F3. `FileMemoryStore.TryUpdateEntry` already got this fix.** `FileMemoryStore.cs:1050` uses
`.Equals(SanitizeName(subject), StringComparison.OrdinalIgnoreCase)`. Its guard,
`tests/Scrinia.Tests/TryUpdateEntryTests.cs:113-126`, is the naming and structure template for the
new guards.

**F4. The interface default is reachable only from the test doubles.** Implementers: `HttpMemoryStore`
(`src/Scrinia/HttpMemoryStore.cs:17`, overrides at `:407`), `FileMemoryStore` via `ILocalMemoryStore`
(`src/Scrinia.Core/ILocalMemoryStore.cs:16`, overrides at `:1040`), and `FakeMemoryStore`
(`tests/Scrinia.TestDoubles/FakeMemoryStore.cs:18`, **no override**). Fixing it is a fix to the
contract and to double fidelity, **not** to a live production bug. Do not let the closure text
overstate it.

**F4a. `FakeMemoryStore` persists nothing.** `Upsert` is `{ }` and `LoadIndex` is `=> []`
(`FakeMemoryStore.cs:25-26`) — it is a deliberate no-op double. **The first draft of this plan
specified a test against it that could never pass**, and neither of that draft's two STOP guards
would have fired: the implementer would have reached "confirm it passes" and been stuck. Task 1
Step 1 below is written against that measurement. All members are `virtual` precisely so a test can
subclass and override the one or two it needs (`FakeMemoryStore.cs:13-14`) — that is the sanctioned
route, and it leaves the shared double, used by five test files across two assemblies, untouched.

**F4b. `TryUpdateEntry` is a default interface method.** C# does not surface a DIM through the
concrete type, so `new FakeMemoryStore().TryUpdateEntry(…)` is a **real** `CS1061` build error (not
the phantom kind). The call must go through an `IMemoryStore`-typed reference.

**F5. `HttpMemoryStore.TryUpdateEntry` always returns `false` by design** (`:423`), and `:411-422`
is deliberately-kept dead work with a comment forbidding its removal (`:411-420`). The ordinal `==`
at `:409` decides only whether `mutate` is invoked — i.e. whether the store distinguishes "no such
entry" from "found it, cannot persist". That distinction is the override's entire stated purpose
(`:382-394`) and what `RemoteLoadIndexTests`' two `mutateRan` assertions pin (`:118-121`,
`:149-152`). **The defect is a lost distinction, not lost data.**

**F6. The security-headers middleware has the wipe exposure — measured, not inferred.**
`Program.cs:371-379` sets four headers by direct assignment before `await next()`.
`app.UseExceptionHandler(...)` is at `Program.cs:352`, so it wraps this middleware, and
`ExceptionHandlerMiddleware` calls `Response.Clear()` before writing its 500 body. A probe over a
real Kestrel host returned `500` with **all four headers absent**, and the same four present on a
non-exception response. The proven fix is `Response.OnStarting`, used for exactly this reason at
`Program.cs:402-413` with the rationale at `:389-401`.

**F7. That fix has a live regression guard to mirror.** `tests/Scrinia.Server.Tests/ApiVersionHeaderOn500Tests.cs`
passes today (verified by running it). Its `ThrowingStoreServerFactory` is `public sealed` in
namespace `Scrinia.Server.Tests` (`:73`), reusable from a second file in the same assembly.

**F7a. `/health` returns 503 under that factory**, not 2xx — the health check enumerates stores and
the throwing backend degrades it (measured). `/health/live` returns 200 unconditionally
(`HealthEndpoints.cs:16`). Task 3's negative control uses `/health/live`.

**F8. `Keywords` is dropped on the listing wire at four points, and the server never sends it.**
Server DTO `MemoryListItem` (`src/Scrinia.Server/Models/ApiDtos.cs:34-43`) has no such field; the
projection (`MemoryEndpoints.cs:130-139`) builds it from `item.Entry`, which **does** carry
`Keywords` (`Models/ArtifactEntry.cs:11`) — the data is in hand and simply not projected. Client DTO
`ListApiItem` (`HttpMemoryStore.cs:130-132`) has no such field, so `LoadIndex` (`:352-360`) and
`ListScoped` (`:496-504`) build every remote `ArtifactEntry` with `Keywords` null. Each DTO has
exactly **one** construction site repo-wide, no positional deconstruction, and no
`UnmappedMemberHandling.Disallow` anywhere — so appending an optional trailing parameter is safe.

**F9. Who reads `Keywords`, and the sharp version of what null costs.**
`MaintenanceEventSink.cs:68` (legacy `ref:` idempotency), `:95` (inbound-ref detection), `:102` (the
orphan read-modify-write). At `:102`, `sourceEntry.Keywords?.ToList() ?? []` is remotely always
empty, so when the orphan branch fires it writes `Keywords = ["orphan"]` — **clobbering every other
keyword on the entry**, not merely mislabelling it. (An earlier draft claimed `isOrphan` is "always
false so the branch always fires"; that was too strong — the branch is gated on `!hasInboundRefs`,
and inbound-ref detection still works remotely through the `ref` *attribute*, which is on the wire.
The clobber is the real harm; the trigger is narrower than claimed.) Today the write is refused by
F5, so nothing is lost. **When E4's PATCH endpoint lands, the same read drives a live clobbering
write.** Fixing the read now disarms it before E4 arms it.

**F10. Nothing snapshots these DTOs.** `OpenApiTests.cs` is 34 lines asserting only that the
document contains `"openapi"` and `html`. No golden, no schema check.

**F11. Casing survives the whole server round trip** — measured end-to-end over Kestrel: store
`"MixedCaseProbe"`, list it back, get `MixedCaseProbe`. `StoreRequest → ParseQualifiedName →
WriteSidecarCore → LoadEntriesFromSidecars → MemoryListItem` folds case nowhere. This is what makes
Tasks 1 and 2's guards non-vacuous; it was the single highest-risk premise in the plan and it holds.

**F12. A plain keyword survives to `ArtifactEntry.Keywords`.** Measured server-side: storing with
`keywords: ["zebra"]` yields `[body, keywords, probe, zebra]` — `AttributeExtractor.RouteKeywords`
leaves it plain (no `key:value` shape) and `MemoryOrchestrator` merges it with auto-extracted terms.
**Assert `Contain`, never equality.**

**F13. No existing test relies on the doubles distinguishing names by case.** `TryUpdateEntry` is
never invoked on any `FakeMemoryStore` or subclass anywhere in `tests/`. This is inspection, not
measurement — the verification pass could not run the suite with the fix applied. Treat a surprise
failure in Task 1 Step 12 as signal.

### Two rulings (challenge them if they are wrong)

**R1 — `RemoteApiVersion.Current` is NOT bumped by Task 4.** `RemoteApiVersionGuard.Validate`
(`Resilience/RemoteApiVersion.cs:49-71`) checks only header presence and exact equality against
`Current = "1"`. No schema check, no field-level validation; `Conformance/StoreConformanceTests.cs`
references none of these types. A new optional *response* field cannot trip any guard: a new client
against an old server reads `Keywords: null`, byte-for-byte today's behaviour, and an old client
ignores the field. Neither direction can produce a silently-wrong result.

**R2 — `web/` is not touched.** `web/src/api/types.ts:35-45` carries this response's TypeScript
shape, and the only `keywords` in all of `web/src` is on `StoreRequest` (`:8`) — nothing reads
keywords off a listing. Adding an unused optional field is speculative. Recorded so its absence
reads as a decision.

---

## File Structure

| File | Change | Task |
|---|---|---|
| `src/Scrinia.Core/IMemoryStore.cs` | `:41` ordinal `==` → `OrdinalIgnoreCase` | 1 |
| `src/Scrinia/HttpMemoryStore.cs` | `:409` same | 1 |
| `src/Scrinia/Services/EntityTypingEventHandler.cs` | `:38-39` same | 1 |
| `tests/Scrinia.Tests/TryUpdateEntryTests.cs` | Add persisting subclass + default guard | 1 |
| `tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs` | Add mixed-case promotion guard | 1 |
| `tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs` | Add mixed-case `mutateRan` guard | 1 |
| `src/Scrinia.Server/Services/MemoryOrchestrator.cs` | `:113`, `:257` | 2 |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | `:320`, `:1163` | 2 |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | `:579` | 2 |
| `src/Scrinia.Core/FileMemoryStore.cs` | `:1512`, `:1540` | 2 |
| `tests/Scrinia.Server.Tests/CaseInsensitiveRestoreTests.cs` | Create — the data-loss guards | 2 |
| `tests/Scrinia.Tests/` (existing files) | Guards for the MCP-side sites | 2 |
| `src/Scrinia.Server/Program.cs` | `:371-379` → `OnStarting` | 3 |
| `tests/Scrinia.Server.Tests/SecurityHeadersOn500Tests.cs` | Create | 3 |
| `src/Scrinia.Server/Models/ApiDtos.cs` | `Keywords` on `MemoryListItem` | 4 |
| `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` | Project `item.Entry.Keywords` | 4 |
| `src/Scrinia/HttpMemoryStore.cs` | `Keywords` on `ListApiItem` + both sites | 4 |
| `tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs` | Keywords round-trip guard | 4 |
| `CHANGELOG.md` | Wire addition + the data-loss fixes | 2, 4 |
| `docs/superpowers/specs/2026-08-10-residuals-closure.md` | Close Group C | 5 |

---

### Task 1: Case-insensitive matching at the three sanitized-name sites

**Files:**
- Modify: `src/Scrinia.Core/IMemoryStore.cs:41`, `src/Scrinia/HttpMemoryStore.cs:409`,
  `src/Scrinia/Services/EntityTypingEventHandler.cs:38-39`
- Test: `tests/Scrinia.Tests/TryUpdateEntryTests.cs`,
  `tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs`,
  `tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: no signature changes, behaviour only. Task 2 fixes the same defect class at different
  sites and does not depend on this task's code.

- [ ] **Step 1: Write the failing guard for the interface default**

`FakeMemoryStore` persists nothing (F4a), so the guard needs a double that does. Add a private
subclass and the test to `tests/Scrinia.Tests/TryUpdateEntryTests.cs`, inside the existing class
(before its closing brace at `:127`). Add `using Scrinia.TestDoubles;` at the top if absent —
`Scrinia.Tests` already references that project (`Scrinia.Tests.csproj:33`).

```csharp
    /// <summary>
    /// A <see cref="FakeMemoryStore"/> that actually stores things. The shared double is a
    /// deliberate no-op (its Upsert is empty and its LoadIndex returns []), which is right for the
    /// five test files that use it and useless for pinning a lookup. Overriding the two members
    /// this needs is the route its own doc comment prescribes, and it leaves every other consumer
    /// of the shared double untouched.
    /// </summary>
    private sealed class PersistingFakeStore : FakeMemoryStore
    {
        private readonly List<ArtifactEntry> _entries = [];
        public override IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local") => _entries;
        public override void Upsert(ArtifactEntry entry, string scope = "local")
        {
            _entries.RemoveAll(e => e.Name == entry.Name);
            _entries.Add(entry);
        }
    }

    /// <summary>
    /// The same guard as <see cref="MutateWithDifferentCasing_StillFindsAndUpdatesTheEntry"/>, one
    /// level up: <c>IMemoryStore</c>'s DEFAULT <c>TryUpdateEntry</c> matched sanitized names with an
    /// ordinal <c>==</c> long after FileMemoryStore's override was fixed. No production store
    /// reaches the default — both real stores override it — so this does not guard a live
    /// production bug. It guards the doubles built on the default disagreeing with both real
    /// stores, which is how a test comes to pass for behaviour production does not have.
    /// </summary>
    [Fact]
    public void InterfaceDefault_MutateWithDifferentCasing_StillFindsTheEntry()
    {
        // IMemoryStore-typed on purpose: TryUpdateEntry is a default interface method and is not
        // surfaced through the concrete type (a `var` here is a CS1061 build error, not a phantom).
        IMemoryStore store = new PersistingFakeStore();
        store.Upsert(new ArtifactEntry("MixedCase", "uri", 1, 1, DateTimeOffset.UtcNow), "local");

        bool mutateRan = false;
        bool changed = store.TryUpdateEntry("mixedcase", "local", e =>
        {
            mutateRan = true;
            return e with { ContentPreview = "touched" };
        });

        changed.Should().BeTrue(
            because: "the default must resolve a case-mismatched subject the way both real stores do");
        mutateRan.Should().BeTrue(
            because: "returning true without invoking the mutation would satisfy the assertion above " +
                     "while doing nothing");
        store.LoadIndex("local").Single(e => e.Name == "MixedCase")
            .ContentPreview.Should().Be("touched");
    }
```

**Sanity-check the double before trusting the guard:** temporarily call `TryUpdateEntry("MixedCase", …)`
with *exact* casing and confirm it returns `true`. If that also fails, the subclass is not
persisting and the test is measuring the wrong thing — fix that before proceeding. Do not skip
this: the previous branch's four Critical findings were all tests passing or failing for a reason
other than the claimed one.

- [ ] **Step 2: Run it and watch it fail for the right reason**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~InterfaceDefault_MutateWithDifferentCasing"`

Expected: FAIL on `changed.Should().BeTrue()` — `SanitizeName` is identity on the double
(`FakeMemoryStore.cs:23`), so the default compares `"MixedCase" == "mixedcase"` ordinally.

- [ ] **Step 3: Fix the interface default**

`src/Scrinia.Core/IMemoryStore.cs:41` — replace:

```csharp
        var current = LoadIndex(scope).FirstOrDefault(e => SanitizeName(e.Name) == SanitizeName(subject));
```

with:

```csharp
        var current = LoadIndex(scope).FirstOrDefault(
            e => SanitizeName(e.Name).Equals(SanitizeName(subject), StringComparison.OrdinalIgnoreCase));
```

Then add one paragraph to the member's existing doc comment (`:30-38`), immediately before the
closing `</summary>`:

```csharp
    /// <para>Name matching is case-insensitive, matching <c>FileMemoryStore.TryUpdateEntry</c> and
    /// <c>Remove</c>'s <c>File.Exists</c> guard — <c>SanitizeName</c> does not fold case, so an
    /// ordinal comparison here silently reported "not found" for an entry stored under a different
    /// casing.</para>
```

If `StringComparison` does not resolve, add `using System;` — but check the build first; an
unnecessary using is its own risk under this project's warning settings.

- [ ] **Step 4: Confirm it passes**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Write the failing guard for the entity-typing gate**

Append to `tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs`, inside the existing class. This is
a mixed-case variant of the sibling at `:38-57`. Verified against the real implementation:
`ParseQualifiedName("/findings/note")` yields scope `local:findings`, subject `note`.

```csharp
    /// <summary>
    /// The sink's OWN entry lookup — upstream of <c>TryUpdateEntry</c> — matched sanitized names
    /// with an ordinal <c>==</c>, so a memory stored as "Note" and written under the qualified name
    /// "/findings/note" was gated out at the very first read: the sink returned before reaching the
    /// store's (already case-insensitive) update path and the promotion silently did not happen.
    /// Fixing the store alone would not have fixed this; the gate is above it.
    /// </summary>
    [Fact]
    public async Task Promotes_WhenTheStoredNameDiffersInCasingFromTheEvent()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(new ArtifactEntry("known", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["thing"] = ["susie"] }), "local:findings");
        store.Upsert(Mentioning("Note", "seraphim"), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f], ["susie"] = [0f, 0f, 1f] };
        var sink = new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3));

        // Lowercase subject; the entry is stored as "Note".
        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        var reloaded = store.LoadIndex("local:findings").Single(e => e.Name == "Note");
        (reloaded.Attributes!.TryGetValue("mention", out var m) ? m : []).Should().NotContain("seraphim",
            because: "the promotion must run even though the event's subject casing differs from " +
                     "the stored entry's");
        reloaded.Attributes!["thing"].Should().Contain("seraphim");
    }
```

**Platform note:** `FileMemoryStore.TryUpdateEntry:1045` guards on
`File.Exists(SanitizeName(subject) + ".meta.json")`, which is case-sensitive on Linux/macOS. This
test and the existing `MutateWithDifferentCasing_StillFindsAndUpdatesTheEntry` both rely on NTFS
case-insensitivity. That is pre-existing and documented in the older test's comment; the new test
inherits it. Do not try to fix it here — note it and move on.

- [ ] **Step 6: Run it and watch it fail**

Run: `rtk dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Promotes_WhenTheStoredNameDiffersInCasing"`

Expected: FAIL — `entry` is null at `:38-39`, `PromoteAsync` returns at `:40`, `mention` still holds
`"seraphim"`. If it fails on a *different* assertion, the test is not pinning what it claims: verify
by temporarily storing the entry as `"note"`, which the sibling test proves promotes.

- [ ] **Step 7: Fix the gate**

`src/Scrinia/Services/EntityTypingEventHandler.cs:38-39` — replace:

```csharp
        var entry = store.LoadIndex(scope)
            .FirstOrDefault(e => store.SanitizeName(e.Name) == store.SanitizeName(subject));
```

with:

```csharp
        // Case-insensitive to match FileMemoryStore.TryUpdateEntry and IMemoryStore's default:
        // SanitizeName does not fold case, so an ordinal match here gated a case-mismatched name
        // out of promotion entirely, above and independent of the store's own lookup.
        var entry = store.LoadIndex(scope)
            .FirstOrDefault(e => store.SanitizeName(e.Name)
                .Equals(store.SanitizeName(subject), StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 8: Confirm it passes**

Run the Step 6 command. Expected: PASS.

- [ ] **Step 9: Write the failing guard for the remote override**

Append to `tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs`, inside the existing class. Its
premise — that the server preserves the stored name's casing — is measured (F11), so this guard is
not vacuous. `mutateRan` is the only observable, since the override returns `false` either way (F5).

```csharp
    /// <summary>
    /// <c>HttpMemoryStore.TryUpdateEntry</c>'s own lookup used an ordinal
    /// <c>SanitizeName(e.Name) == SanitizeName(subject)</c>, so a case-mismatched subject fell out
    /// at the <c>current is null</c> guard and returned <c>false</c> WITHOUT invoking the mutation
    /// — collapsing "no such entry" back into "found it, cannot persist", the exact distinction
    /// this override exists to keep apart (the two sibling tests above pin each side of it). The
    /// return value is <c>false</c> in both cases by design, so <c>mutateRan</c> is the only thing
    /// that can tell them apart.
    /// </summary>
    [Fact]
    public async Task TryUpdateEntry_FindsTheEntry_WhenTheSubjectCasingDiffers()
    {
        await StoreAsync("MixedCaseProbe", "probe body for casing");

        bool mutateRan = false;
        bool updated = _remote.TryUpdateEntry("mixedcaseprobe", "local", entry =>
        {
            mutateRan = true;
            return entry;
        });

        mutateRan.Should().BeTrue(
            because: "the entry exists under a different casing and must be found — falling out at " +
                     "the null guard would report it absent, which is a different condition");
        updated.Should().BeFalse(
            because: "finding the entry changes nothing about this store's inability to persist a " +
                     "metadata-only mutation — the fail-closed contract is unchanged by this fix");
    }
```

- [ ] **Step 10: Run it and watch it fail**

Run: `rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~TryUpdateEntry_FindsTheEntry_WhenTheSubjectCasingDiffers"`
Expected: FAIL on `mutateRan.Should().BeTrue()`.

- [ ] **Step 11: Fix the remote override**

`src/Scrinia/HttpMemoryStore.cs:409` — replace:

```csharp
        var current = LoadIndex(scope).FirstOrDefault(e => SanitizeName(e.Name) == SanitizeName(subject));
```

with:

```csharp
        var current = LoadIndex(scope).FirstOrDefault(
            e => SanitizeName(e.Name).Equals(SanitizeName(subject), StringComparison.OrdinalIgnoreCase));
```

Do **not** touch `:411-423`. The comment at `:411-420` explicitly forbids collapsing the dead work
into an early `return false`, and this fix does not change that argument.

- [ ] **Step 12: Full build and suite**

Run: `rtk dotnet build Scrinia.sln` (expect 0 errors), then `rtk dotnet test Scrinia.sln`
(expect baseline 1849 + 3 = **1852 passed, 0 failed**).

A pre-existing test that now *fails* because a double became case-insensitive is real signal (F13
says there is no candidate, by inspection not measurement): report it by name rather than adjusting
the test to match.

- [ ] **Step 13: Commit**

```bash
rtk git add src/Scrinia.Core/IMemoryStore.cs src/Scrinia/HttpMemoryStore.cs src/Scrinia/Services/EntityTypingEventHandler.cs tests/Scrinia.Tests/TryUpdateEntryTests.cs tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs
rtk git commit -m "fix: match sanitized subject names case-insensitively at the three remaining sites"
```

---

### Task 2: The seven raw `e.Name == subject` sites, and the three that lose data

**Files:**
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs:113`, `:257`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:320`, `:1163`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:579`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1512`, `:1540`
- Create: `tests/Scrinia.Server.Tests/CaseInsensitiveRestoreTests.cs`
- Test: existing files under `tests/Scrinia.Tests/` for the MCP-side sites
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing from Task 1 — same defect class, disjoint code.
- Produces: no signature changes.

This task exists because the user ruled at plan time that the branch fixes the whole defect class,
not just the sites the spec happened to name. **Three of these lose data** (F2's table); those get
guards. The other four get the same one-line change under the build and the existing suite, and
Step 7 says so explicitly rather than implying coverage that does not exist.

- [ ] **Step 1: Write the failing guard for the lost version archive**

Create `tests/Scrinia.Server.Tests/CaseInsensitiveRestoreTests.cs`. Model the fixture on
`RemoteLoadIndexTests.cs:37-69` (`ScriniaServerFactory`, `UseKestrel(0)`, `StartServer()`,
`CreateAuthenticatedClient()`, the `StoreAsync` helper) — read that file and mirror it rather than
inventing a fixture.

The assertion that is directly observable is **`CreatedAt` preservation**: re-storing an existing
memory must keep its original `CreatedAt` and set `UpdatedAt`, and `MemoryOrchestrator.cs:114-115`
derives both from `existingEntry`. Store as `"CasingVersioned"`, capture `createdAt` from the
listing, re-store as `"casingversioned"`, and assert the listing still reports the original
`createdAt` and now reports a non-null `updatedAt`.

**Also assert the version archive** if the repo shows a way to observe it: search
`tests/Scrinia.Server.Tests/` and `tests/Scrinia.Tests/` for existing coverage of `ArchiveVersion`
or a versions listing, and follow whatever it does. If no such observation exists, state that in the
test's doc comment — "the archive itself is not observable through any surface this suite has;
`CreatedAt` is the proxy" — rather than silently asserting only the proxy.

- [ ] **Step 2: Run it and watch it fail**

Expected: FAIL — before the fix, `existingEntry` is null, so `createdAt` is reset to now and
`updatedAt` stays null.

If the re-store instead creates a **second distinct entry**, that is also a failure and a more
visible one; adjust the assertion to whichever the run actually shows, and say which in the comment.

- [ ] **Step 3: Write the failing guard for the dropped chunk entries**

Same file. `MemoryOrchestrator.cs:257` is the append path: with `existingEntry` null,
`updatedChunks` becomes `[newChunkEntry]` alone, dropping every prior chunk entry. Store a memory
as `"CasingAppended"`, append to `"casingappended"`, and assert the resulting entry's chunk count
reflects both writes rather than only the appended one. Use the listing's `chunkCount` — it is a
real wire field (`MemoryEndpoints.cs`, and `HttpMemoryStore.cs:350-351` confirms it).

Run it. Expected: FAIL, with a chunk count that has lost the original.

- [ ] **Step 4: Write the failing guard for compaction's dropped metadata**

`MemoryTools.Lifecycle.cs:579` is MCP-side, so this guard belongs under `tests/Scrinia.Tests/`.
Find the existing compact coverage first (search for `Compact` in `tests/Scrinia.Tests/`) and add
the mixed-case variant beside it, following that file's fixture. Store a memory with attributes and
keywords under one casing, compact it under another, and assert the attributes and keywords survive
— the comment at `MemoryTools.Lifecycle.cs:571-578` states that preserving them is the entire
reason the read happens before the write.

Run it. Expected: FAIL, with the metadata absent after compaction.

- [ ] **Step 5: Apply all seven fixes**

Each is the same one-line change: `e.Name == subject` → `e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase)`
(and `srcSubject` at the two `FileMemoryStore` sites). The exact lines:

- `src/Scrinia.Server/Services/MemoryOrchestrator.cs:113` and `:257`
- `src/Scrinia.Mcp/MemoryTools.Core.cs:320` and `:1163`
- `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:579`
- `src/Scrinia.Core/FileMemoryStore.cs:1512` and `:1540`

Add this comment above `MemoryOrchestrator.cs:113` only — one statement of the reasoning for the
whole class, not seven copies:

```csharp
        // Case-insensitive: `subject` comes from ParseQualifiedName and `e.Name` from the index,
        // and neither folds case. An ordinal match here reported "new memory" for a re-store under
        // different casing, which skipped ArchiveVersion below and reset CreatedAt — while the
        // write still landed on the same file on a case-insensitive filesystem, losing the prior
        // version with no archive. The same shape existed at six other sites; all are fixed.
```

**Do not touch `src/Scrinia.Server/Chat/Providers/OpenAiChatProvider.cs:100`** — it matches the
search pattern and is an unrelated tool-call function name (F2).

- [ ] **Step 6: Confirm the three guards pass, then the full suite**

Run each new test's filter. Expected: PASS.

Then: `rtk dotnet build Scrinia.sln` (0 errors) and `rtk dotnet test Scrinia.sln`.

Expect **1852 + the number of tests you actually added**. State the number; do not assume mine.
Existing tests that now behave differently are signal — several of these paths are widely exercised,
and a changed result is worth reading before it is worth fixing.

- [ ] **Step 7: Record what is fixed without a dedicated guard**

Four sites get the fix with no new test of their own: `MemoryTools.Core.cs:320` and `:1163`,
`FileMemoryStore.cs:1512` and `:1540`. Before accepting that, check whether existing tests cover
`CopyMemory`'s metadata propagation (search `tests/` for `CopyMemory`). If they do, say so in the
CHANGELOG entry's supporting detail. **If they do not, add one guard for `FileMemoryStore.cs:1540`**
— the persistent copy path — asserting a copy under mismatched source casing carries the source's
metadata rather than defaults.

Do not claim coverage you have not verified. Silence about a gap reads as "covered".

- [ ] **Step 8: CHANGELOG**

Read `CHANGELOG.md`'s Unreleased section and match its heading style. Add one entry covering the
class, naming the three data-loss paths specifically:

```markdown
- Memory-name lookups are now case-insensitive across the write paths. Re-storing a memory under a
  different casing than it was created with (`MixedCase` then `mixedcase`) was treated as a brand
  new memory: the previous version was not archived and `CreatedAt` was reset, while the write
  still landed on the same file on a case-insensitive filesystem — the prior version was lost with
  no archive. Appending under a mismatched casing dropped every prior chunk entry, and compacting
  under one dropped the memory's attributes and keywords. Ten call sites in total.
```

- [ ] **Step 9: Commit**

```bash
rtk git add src/Scrinia.Server/Services/MemoryOrchestrator.cs src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Server.Tests/CaseInsensitiveRestoreTests.cs CHANGELOG.md
rtk git commit -m "fix: case-mismatched re-store no longer discards the prior version"
```

Add any `tests/Scrinia.Tests/` files you touched to that `git add` line. Explicit paths only.

---

### Task 3: Security headers must survive the exception handler's Response.Clear()

**Files:**
- Modify: `src/Scrinia.Server/Program.cs:371-379`
- Create: `tests/Scrinia.Server.Tests/SecurityHeadersOn500Tests.cs`

**Interfaces:**
- Consumes: `ThrowingStoreServerFactory` from `ApiVersionHeaderOn500Tests.cs` (`public sealed`,
  namespace `Scrinia.Server.Tests`) — reused as-is, not modified.
- Produces: nothing.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Server.Tests/SecurityHeadersOn500Tests.cs`:

```csharp
using System.Net;
using FluentAssertions;

namespace Scrinia.Server.Tests;

/// <summary>
/// The defect <see cref="ApiVersionHeaderOn500Tests"/> pins for the API-version header, one
/// middleware higher: the security-headers middleware set its four headers by direct assignment
/// before <c>await next()</c>, and <c>UseExceptionHandler</c> — registered at
/// <c>Program.cs:352</c>, so it wraps this middleware — calls <c>Response.Clear()</c> on an
/// unhandled exception, emptying the header collection before writing the 500 body. Measured
/// before the fix: a genuine 500 arrived with all four headers ABSENT. The response most likely to
/// carry a stack trace or an unexpected content type was the one response with no protection
/// against sniffing or framing.
///
/// <para>Driven over a real Kestrel host via <see cref="ThrowingStoreServerFactory"/>, whose
/// backend throws from inside <c>Program.cs</c>'s store-resolution middleware — a genuine
/// unhandled exception on the real request path, not a simulated response.</para>
/// </summary>
public sealed class SecurityHeadersOn500Tests : IAsyncLifetime
{
    private readonly ThrowingStoreServerFactory _factory = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateAuthenticatedClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "strict-origin-when-cross-origin")]
    [InlineData("X-XSS-Protection", "0")]
    public async Task GenuineUnhandledException_StillCarriesTheSecurityHeaders(string header, string expected)
    {
        var resp = await _client.GetAsync($"/api/v1/stores/{_factory.ThrowingStore}/memories");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            because: "the configured backend throws when the store is resolved, so this must reach " +
                     "the global exception handler");

        resp.Headers.TryGetValues(header, out var values).Should().BeTrue(
            because: $"{header} must survive the exception handler's Response.Clear() — a 500 is " +
                      "the response that most needs it, not the one that can afford to lose it");
        values!.Should().ContainSingle().Which.Should().Be(expected);
    }

    /// <summary>
    /// Negative control for the theory above: on a non-exception response the headers were never in
    /// question. Without this, a middleware that emitted them ONLY on the error path would still
    /// satisfy the theory. <c>/health/live</c> is used deliberately — plain <c>/health</c> returns
    /// 503 under this factory, because its check enumerates stores and the throwing backend
    /// degrades it (measured), whereas <c>/health/live</c> returns 200 unconditionally
    /// (<c>HealthEndpoints.cs:16</c>).
    /// </summary>
    [Fact]
    public async Task NonExceptionResponse_AlsoCarriesTheSecurityHeaders()
    {
        var resp = await _client.GetAsync("/health/live");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.TryGetValues("X-Content-Type-Options", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be("nosniff");
    }
}
```

- [ ] **Step 2: Run it and watch the split**

Run: `rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~SecurityHeadersOn500Tests"`

Expected: the four `[InlineData]` cases FAIL (`TryGetValues` returns false); the negative control
PASSES. **That split is the point** — it proves the headers exist and are lost specifically to
`Response.Clear()`, not that the middleware never ran.

If all five pass before the fix, stop and report: F6 needs correcting.

- [ ] **Step 3: Apply the fix**

`src/Scrinia.Server/Program.cs:371-379` — replace the whole middleware:

```csharp
// Security headers
//
// Registered via Response.OnStarting, not direct assignment, for exactly the reason documented on
// the API-version middleware below: UseExceptionHandler (registered above, so it wraps this) calls
// Response.Clear() on an unhandled exception, emptying the header collection before it writes the
// 500 body. Headers assigned directly here were wiped by that Clear(), so a genuine server crash —
// the response most likely to carry an unexpected content type — arrived with no nosniff and no
// framing protection at all. OnStarting callbacks live outside the header collection and fire
// immediately before the response starts, i.e. after any Clear(), so they survive whatever happens
// downstream (reproduced: see SecurityHeadersOn500Tests).
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        context.Response.Headers.XXSSProtection = "0"; // modern browsers: CSP preferred
        return Task.CompletedTask;
    });
    await next();
});
```

- [ ] **Step 4: Confirm all five pass**

Run the Step 2 command. Expected: PASS (4 theory cases + 1 fact).

- [ ] **Step 5: Full build and suite**

`rtk dotnet build Scrinia.sln` (0 errors), then `rtk dotnet test Scrinia.sln` — expect Task 2's
total + 5.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Server/Program.cs tests/Scrinia.Server.Tests/SecurityHeadersOn500Tests.cs
rtk git commit -m "fix(server): security headers no longer wiped by the exception handler"
```

---

### Task 4: Carry `Keywords` on the listing wire

**Files:**
- Modify: `src/Scrinia.Server/Models/ApiDtos.cs:34-43`,
  `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:130-139`,
  `src/Scrinia/HttpMemoryStore.cs:130-132`, `:352-360`, `:496-504`
- Test: `tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing from Tasks 1-3.
- Produces: `MemoryListItem` and `ListApiItem` each gain a trailing `string[]? Keywords = null`.
  Both are positional records with exactly one construction site each (F8), so nothing breaks.

- [ ] **Step 1: Write the failing round-trip test**

Append to `tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs`. Extend the existing `StoreAsync`
helper (`:63-69`) rather than writing a second one — `StoreRequest` already has `Keywords`
(`ApiDtos.cs:15`):

```csharp
    private async Task StoreAsync(string name, string body,
        Dictionary<string, string[]>? attributes = null, string[]? keywords = null)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest([body], name, body, Attributes: attributes, Keywords: keywords));
        resp.EnsureSuccessStatusCode();
    }
```

and add:

```csharp
    /// <summary>
    /// The listing wire carried no keywords at all — <c>MemoryListItem</c> had no such field, so
    /// every remote <c>ArtifactEntry</c> came back with <c>Keywords</c> null regardless of what was
    /// stored, while the server held them in hand. <c>MaintenanceEventSink</c> reads that field in
    /// three places; at <c>:102</c> an empty read makes the orphan branch write
    /// <c>Keywords = ["orphan"]</c>, clobbering every other keyword. That is inert only because
    /// <c>HttpMemoryStore.TryUpdateEntry</c> refuses every write today — the E4 PATCH endpoint
    /// would arm it.
    /// </summary>
    [Fact]
    public async Task LoadIndex_CarriesKeywords()
    {
        await StoreAsync("keyword-probe", "probe body for keywords", keywords: ["zebra"]);

        var entry = _remote.LoadIndex("local").Single(e => e.Name == "keyword-probe");

        entry.Keywords.Should().NotBeNull(
            because: "the server holds this entry's keywords and the listing endpoint must project " +
                     "them — a null here is the wire dropping data the server has in hand");
        // Contain, not equality: MemoryOrchestrator merges agent keywords with auto-extracted ones,
        // so the stored set is a superset (measured: [body, keywords, probe, zebra]).
        entry.Keywords!.Should().Contain("zebra");
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `rtk dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~LoadIndex_CarriesKeywords"`
Expected: FAIL on `entry.Keywords.Should().NotBeNull()`.

- [ ] **Step 3: Add the field to the server DTO**

`src/Scrinia.Server/Models/ApiDtos.cs:34-43` — append a parameter to `MemoryListItem`:

```csharp
public sealed record MemoryListItem(
    string Name,
    string QualifiedName,
    string Scope,
    int ChunkCount,
    long OriginalBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string Description,
    Dictionary<string, string[]>? Attributes,
    string[]? Keywords = null);
```

- [ ] **Step 4: Project it server-side**

`src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:130-139` — add `item.Entry.Keywords` as the final
argument to the existing `new MemoryListItem(...)`.

- [ ] **Step 5: Add the field to the client DTO and both construction sites**

`src/Scrinia/HttpMemoryStore.cs:130-132`:

```csharp
    internal sealed record ListApiItem(string Name, string QualifiedName, string Scope, int ChunkCount,
        long OriginalBytes, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, string Description,
        Dictionary<string, string[]>? Attributes, string[]? Keywords = null);
```

Then add `Keywords: item.Keywords` to the `ArtifactEntry` construction in `LoadIndex` (`:352-360`)
and in `ListScoped` (`:496-504`). Both already use named arguments; match that.

- [ ] **Step 6: Confirm it passes**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 7: CHANGELOG**

Match the Unreleased section's existing heading style. Add:

```markdown
- The store listing endpoint (`GET /api/v1/stores/{store}/memories`) now returns each memory's
  `keywords`. The field was absent from the wire entirely, so a remote client saw `null` keywords
  for every memory regardless of what was stored. Additive and optional in both directions: an
  older client ignores the new field, and a newer client against an older server reads `null`
  exactly as before, so `X-Scrinia-Api-Version` is deliberately not bumped.
```

- [ ] **Step 8: Full build and suite**

`rtk dotnet build Scrinia.sln` (0 errors), then `rtk dotnet test Scrinia.sln` — expect Task 3's
total + 1.

- [ ] **Step 9: Commit**

```bash
rtk git add src/Scrinia.Server/Models/ApiDtos.cs src/Scrinia.Server/Endpoints/MemoryEndpoints.cs src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Server.Tests/RemoteLoadIndexTests.cs CHANGELOG.md
rtk git commit -m "fix(server): carry keywords on the store listing wire"
```

---

### Task 5: Close Group C in the closure spec

**Files:**
- Modify: `docs/superpowers/specs/2026-08-10-residuals-closure.md`

**Interfaces:**
- Consumes: the commit SHAs from Tasks 1-4 (`rtk git log --oneline` on this branch).
- Produces: nothing.

- [ ] **Step 1: Rewrite the Group C section**

Replace the `## Group C — remote-mode leftovers (OPEN, 4 items)` heading and its table (`:108-117`)
with a closure section in the style of the Group A/B/D sections already in the file. It must record:

- All four specced items fixed — **and that the spec's table named 2 sites of the case-sensitivity
  defect when there were 10.** The user ruled at plan time that the branch fixes the whole class.
  Name the seven extra sites and the three data-loss paths among them
  (`MemoryOrchestrator.cs:113` lost version archive, `:257` dropped chunk entries,
  `MemoryTools.Lifecycle.cs:579` dropped metadata through compaction).
- That the interface default is reachable only from the test doubles, so its fix is a
  contract/double-fidelity fix, not a live production bug fix. **Do not overstate it.**
- That the remote override's fix restores a *distinction* (`mutateRan`), not data — the override
  still returns `false` by design, and E4 remains the real fix for remote metadata writes.
- That `Keywords` on the wire disarms a trap E4 would otherwise arm (F9), and that
  `RemoteApiVersion.Current` was deliberately not bumped, with R1's reasoning.
- Any site fixed without a dedicated guard, per Task 2 Step 7.
- The commit SHAs and the plan path `docs/superpowers/plans/2026-08-11-remote-mode-leftovers.md`.

Cite nothing under `.superpowers/`.

- [ ] **Step 2: Update the closure-order list**

Move Group C from "Ready to execute" (`:266-268`) into the "Done" list (`:259-264`), matching the
existing strikethrough style, and renumber the remaining entries so the three specs-needed items
are 1-3.

- [ ] **Step 3: Commit**

```bash
rtk git add docs/superpowers/specs/2026-08-10-residuals-closure.md
rtk git commit -m "docs(residuals): Group C closes"
```

---

## Self-review notes

**Spec coverage:** Group C's four table rows map to Task 1 (items 1-2), Task 3 (item 3), Task 4
(item 4). Task 2 covers the seven sites the spec did not know about, per the user's scope ruling.
Task 5 records the undercount rather than quietly correcting it.

**What the verification pass changed.** It falsified four claims in the first draft: the interface-default
test could never have passed (`FakeMemoryStore` persists nothing) and neither STOP guard would have
caught it; the same test would not have compiled (DIM through a concrete type); the `/health`
negative control asserted a 2xx that is actually 503; and F2's "exactly three sites" was an artifact
of `rtk proxy grep | head` dropping matches — there are ten. The tooling caveat is now a Global
Constraint.

**Residual risk — Task 2's guards are specified by intent, not verbatim.** Steps 1, 3, 4 and 7 tell
the implementer what must be observable and which existing fixture to mirror, rather than giving
finished code, because the fixtures they must match were not read at plan time. That is a deliberate
trade: prescribing exact code against unread files is what produced the falsified claims above.
Each step names its expected failure mode so a vacuous guard is caught at the "watch it fail" step.

**Test counts** are arithmetic from the measured 1849 baseline, and Task 2's is left open on
purpose. They exist to make an unexpected count visible, not as assertions about the suite.
