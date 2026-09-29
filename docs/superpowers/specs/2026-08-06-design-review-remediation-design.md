# Design-review remediation — design

**Date:** 2026-08-06
**Source:** the design review of 2026-08-06 (lens: DRY, API design, efficiency, ease of
reuse). Eight findings, F1–F8, plus two more found while designing this document.
**Shape:** three branches after the already-specced `internal-consistency`, cut from
`main` in order: `mcp-response-shape` → `search-contract` → `store-boundaries`.

## Relationship to the 2026-08-05 spec

`docs/superpowers/specs/2026-08-05-review-remediation-design.md` defines two branches.
Branch 1 `merge-driver-correctness` is complete but **not merged** — 25 commits ahead of
`main`. Branch 2 `internal-consistency` (C4, C5, C6) is designed and unstarted.

That spec is **not superseded**. Branch 2 runs first, unchanged, for two reasons:

1. **C5 is F7.** The chat-provider retry duplication was already designed there, down to
   the `(HttpResponseMessage? Response, string? Error)` return shape chosen because the
   code sits inside `yield return` iterators. It is not re-specified here.
2. **C4 is a prerequisite for F3.** C4 moves seven hand-rolled `WorkspaceRoot`
   derivations onto `store.WorkspaceRoot`. F3 then moves one property to a new interface
   instead of chasing seven sites.

## Established facts

Measured against the working tree at `a3e75ff`, not estimated. Re-verify before
contradicting.

1. `.ToYaml()` has **98 call sites in `src`**, all inside `Scrinia.Mcp` (7 files), plus 8
   in tests. **20 internal handlers** return `Task<string>` of rendered YAML.
2. Five sites recover structure by substring-matching rendered YAML:
   `ScriniaCommands.cs:43` and `:45`, `McpResponse.cs:213`, `MemoryTools.Core.cs:1617`,
   `tests/Scrinia.Tests/Benchmarks/ScriniaMemorySystem.cs:36`.
3. `McpResponseExtensions.ToYaml` emits content as `content: |` with each line indented
   two spaces and **no escaping**. `string.Contains` ignores that indent, so a body line
   reading `status: error` is indistinguishable from an envelope error.
4. **CORRECTED 2026-08-07 during implementation — the original claim was WRONG.**
   It read: "`MemoryTools.Lifecycle.cs:589` — `Restore` joins `ctx.ContentSections` (memory
   content) into `ResponseBuilder.Success(...)`. Memory text therefore reaches the envelope
   verbatim, and `scri restore` exit code is a substring match over it."

   `Restore` does join `ctx.ContentSections` into the envelope, but `contentSections` is
   populated at exactly two sites — `:655` (a `Skills available (N): ...` line) and `:721`
   (the empty-state fallback). **No memory body ever reaches the restore envelope**, so
   `scri restore` could not exhibit the defect and was the wrong exemplar throughout the
   review, this spec, and the plan.

   The defect is real; the reachable set is `scri link` (echoes `--reason` into its success
   message), `scri reconcile` (embeds decoded conflict bodies in its scan report), and
   `scri facts` (renders entity values verbatim from unsanitised attribute text).
   `scri append` and `scri compact` are also unreachable: their success text interpolates
   only a memory *name*, and `':'` sits in the explicit `s_portableInvalid` set at
   `FileMemoryStore.cs:298-299` — not the platform-dependent `GetInvalidFileNameChars()` —
   so a name can never carry `status: error` on any platform.

   Recorded rather than silently rewritten because the error is instructive: the review
   asserted a data-flow it had not traced to its source, which is the same
   overstatement-from-a-plausible-reading that the branch exists to eliminate.
5. `IMemoryStore` declares **five `SearchAll` overloads**; four carry default
   implementations that post-filter after the top-K cut. `IMemoryStore.cs:109-111`
   documents that post-filtering is wrong; `:119-123` then does it.
6. `HttpMemoryStore` implements **only** `SearchAll(string, string?, int)` (`:329`),
   `ListScoped(string?)` (`:272`), and `ResolveReadScopes(string?)` (`:571`). All
   filtering overloads take the broken defaults.
7. `FileMemoryStore` carries four `SearchAll` bodies (`:1219`, `:1292`, `:1316`, `:1342`)
   sharing the sequence `new WeightedAttributeScorer(RankerOptions)` → `ListScoped` →
   `BuildTopicInfosFromCandidates` → `searcher.SearchAll`. `:1292` and `:1316` differ
   only in whether `supplementalScores` is `null`.
8. `MemoryTools.Core.cs:880` materializes candidates via `ListScoped(scopes,
   excludeTopics)`; `FileMemoryStore.cs:1358` materializes them again inside `SearchAll`.
   `LoadIndexUnsafe:593` returns `cached.Entries.ToList()` — a copy per scope per call.
9. `HttpMemoryStore` has **8 `NotSupportedException` sites**: `:215` (`file://`), `:254`
   (`SaveIndex`), `:450`, `:456`, `:459`, `:462`, `:565`, `:569`.
10. `SaveIndex` has **zero production callers** outside the two store implementations.
    Every other use is a test holding a concrete `FileMemoryStore`.
11. Path-member call sites total 124: 49 inside `Scrinia.Core` (mostly `this` inside
    `FileMemoryStore`), 40 in tests holding a concrete store, and **~32 typed
    `IMemoryStore`** (21 Mcp, 9 CLI, 2 Server). Only the last group changes.
12. `GenerateContentPreview` has 21 call sites.
13. `PluginPipeline.SearchAsync` (`:163-189`) duplicates `ExecuteSearchAsync`: same
    ambient contributor lookup, same `ListScoped`, same cached-vs-scoped fact-store
    branch, same fusion decision. It has **already drifted** — no `excludeTopics`, no
    `pathPrefix`, no `attributeFilter`, no `context` augmentation.
14. Every dependency of `ExecuteSearchAsync` — `EntityQuerySeeder`,
    `EntityFactSummarizer`, `EntityFactDerivation`, `SearchResultFilter`,
    `EntityFactSummary`, `SearchContributorContext` — already lives in
    `src/Scrinia.Core/Search/`.
15. `Scrinia.Mcp.csproj` declares `InternalsVisibleTo` for `scri`, `Scrinia.Tests`, and
    `Scrinia.Server`.
16. `MemoryEventSinkContext.Default` is assigned from six places in `WorkspaceSetup.cs`:
    `:231`, `:391`, `:409`, `:418`, `:438`, `:519`.
17. `ScriniaCommands.Migration.cs:341`'s `is not FileMemoryStore` downcast exists because
    `AttributeMigration.MigrateStore` needs the `internal` `EnumerateRawForMigration` /
    `PersistMigratedEntry`. It is **not** about path members and survives the F3 split.
18. `Scrinia.Merge` has no project references. `Scrinia.Merge.Tests` is unaffected by
    every branch in this spec.

## Corrections to the review that produced this spec

Recorded so they are not re-derived.

**The event-sink half of F4 was already fixed.** `/findings/event-sink-throw-crashes-test-host`
records the investigation and the fix on branch `event-sink-lifecycle`. `StoreScope`'s
`MemoryEventSinkContext.Default = null` is the verified fix, not a workaround; removing
the leak cut the suite from ~52s to ~31s. Two constraints follow:

- `Default` exists because **AsyncLocal does not propagate through the generic host to
  MCP tool handlers in the CLI**. Removing it breaks CLI sink delivery.
- **Do not drain `BackgroundWork` per-test.** Measured: a drain in `StoreScope.Dispose`
  took the suite from 52s to over 10 minutes, via sync-over-async thread-pool starvation
  from hundreds of parallel disposals. Drain once at process exit.

**The `Migration.cs:341` downcast is weaker evidence than the review claimed.** See fact
17. The F3 split does not remove it.

## Rejected alternatives

- **Fold everything into Branch 2.** One diff spanning the MCP response type,
  `IMemoryStore`, the CLI, and the HTTP API. Unreviewable and unbisectable.
- **Supersede the 2026-08-05 spec.** Discards an approved design and its established
  facts to gain a single document.
- **One branch per finding.** F1/F8 and F2/F5 touch the same methods and the second of
  each pair is nearly free once the first lands. Manufactures rebases for nothing.
- **Harden the YAML envelope** (fence or escape the content block). The defect is
  parsing the rendered string, not the format. Once nothing parses it, hardening is
  unnecessary — and byte-identical output makes the refactor provably behaviour-preserving
  against ~30 existing string assertions.
- **Have remote mode throw on filtered search.** Honest, but removes a feature that
  appears to work today.
- **Keep post-filtering with a warning.** Returns the wrong answer, labelled.
- **A capability probe (`SupportsLocalPaths`) instead of splitting `IMemoryStore`.**
  Moves the trap from rung 6 to rung 5 rather than removing it.
- **Constructor-inject `IMemoryStore` into `ScriniaMcpTools`.** Correct end state, but
  the diff touches all 12 tool methods, server per-request wiring, the CLI, and every
  test's `StoreScope` — and collides with F1 in the same methods. `MemoryStoreContext` is
  genuinely per-request in the server; AsyncLocal is the right tool there.

## Branch sequence

| # | Branch | Findings | Entry gate |
|---|---|---|---|
| 0 | merge `merge-driver-correctness` → `main` | — | later branches cut from `main` |
| 1 | `internal-consistency` | C4, C5, C6 (2026-08-05 spec) | C4 before A3 |
| 2 | `mcp-response-shape` | F1, F8, Mcp nits | golden YAML committed first |
| 3 | `search-contract` | F2, F5, F4-scorer, pipeline unification, server filtering | conformance suite green both stores |
| 4 | `store-boundaries` | F3, F4-sink-install, F6 | exactly 1 `NotSupportedException` left |
| 5 | `residuals` (own spec, to be written) | every deferral recorded during branches 2–4 | see below |

**Branch 5 — residuals.** Each branch in this sequence defers findings deliberately, recording
them to Scrinia under `/findings/<branch>-deferred-followups`. Those accumulate into their own
spec at the end of the sequence rather than being smuggled into a branch whose scope was set
for something else. As of 2026-08-07 the list from `mcp-response-shape` is nine items, the
highest-value being that `scri bundle export` / `scri bundle import` still exit 0 on genuine
failure via the same dead `StartsWith("Error:")` guard that branch fixed for `show`/`forget`.

**Process change adopted 2026-08-07.** Every plan written from this spec gets a **subagent
verification pass before execution**: a subagent whose only job is to falsify the plan's
factual claims, reporting each as VERIFIED (with the command or `file:line` that proves it) or
WRONG (with the truth). Adopted because four of the `mcp-response-shape` plan's assertions were
false and all four were caught downstream by implementers and reviewers rather than up front.
Established Facts should carry the command that produced them so a later reader can re-run
rather than re-trust.

A2 and A3 both touch `IMemoryStore` on **disjoint members** — A2 replaces the search
member, A3 removes layout members. Rebasing A3 onto a merged A2 is clean.

## Branch A1 — `mcp-response-shape`

Covers F1 (serialize-too-early) and F8 (extract the renderer).

### End state

`ToYaml()` is called in exactly 12 places: the public `[McpServerTool]` methods in
`MemoryTools.cs`. `McpResponse` is the currency everywhere inside `Scrinia.Mcp`.

### Changes

1. **Retype 20 handlers** `Task<string>` → `Task<McpResponse>`, deleting ~98 `.ToYaml()`
   calls from their returns: `Recall`, `Remember`, `UpdateMeta`, `List`, `Search`,
   `Copy`, `Forget`, `Append`, `References`, `Link`, `BuildDriftList`
   (`MemoryTools.Core.cs`); `Reconcile`, `Compact`, `Restore` (`Lifecycle`); `Export`,
   `Import` (`Bundling`); `SkillCreate`, `SkillLoad` (`Skills`); `TryRouteSkillRecall`,
   `TryRouteSkillRemember` (`MemoryTools.cs`); plus `FactsResponseFormatter`.

2. **The 12 public methods serialize.** `return await Remember(...)` becomes
   `return (await Remember(...)).ToYaml()`.

3. **Delete `InjectInfoHint`** — all of `McpResponse.cs:210-241`. Its two call sites
   (`MemoryRemember`, `MemoryAppend`) become `response.WithInfo(hint)` applied before
   serialization. **Preserve deliberately:** the current implementation no-ops on error
   responses, so the replacement guards on `r.Status != "error"`.

4. **`Link` stops grepping itself.** `MemoryTools.Core.cs:1617` becomes
   `result1.Status == "error" || result2.Status == "error"`. Its partial-failure message
   currently embeds both YAML blobs verbatim; those become
   `result1.Error ?? result1.Content`.

5. **`EmitMcpResult` takes the typed response.** `ScriniaCommands.cs:41` becomes
   `EmitMcpResult(McpResponse r, bool json, string action)`, switching on `r.Status`.
   `--json` output is unchanged: `CliMcpOutput` still carries rendered YAML, obtained by
   calling `r.ToYaml()` at that point.

6. **The bench harness uses the typed path.** `ScriniaMemorySystem.cs:36` has the same
   substring defect — a benchmark memory containing `status: error` throws a spurious
   "write failed", corrupting a retrieval result. Per fact 15 the harness can call the
   internal typed handler and check `.Status`.

7. **F8 — extract `SearchResultRenderer`.** Move `FormatRichSearchResults`,
   `AppendEntityFactsBlock`, `AppendResultBlock`, `AppendTagsLine`, `AppendDatesLine`,
   `AppendAttributesLine`, `AppendDescription`, `AppendAnchors` into
   `src/Scrinia.Mcp/SearchResultRenderer.cs`. All are already `static` with explicit
   parameters — a move, not a redesign. `MemoryTools.Core.cs` drops from 1731 to roughly
   1380 lines.

8. **Nits:** rename `MatchesRefAttributeb` → `MatchesRefAttribute`
   (`MemoryTools.Core.cs:1586`). Verify whether the `YamlDotNet` package reference in
   `Scrinia.Mcp.csproj` still has a consumer — `McpResponse.cs` hand-rolls its serializer
   specifically to avoid it. Remove the reference if unused.

### Error handling

No semantic change. `ResponseBuilder.Success` still truncates at construction, so
`ContentTruncated` and its followUp bullet behave exactly as today. `ErrorCodes` is
untouched; this branch makes it reachable by callers rather than changing it.

### Testing

- **Golden YAML, committed first.** Before any refactor, capture the exact YAML of all 12
  tools across a fixed fixture workspace into golden files, as the branch's first commit.
  The refactor is then judged against equality, not against "it compiles".
- **The bug as a test:** a memory whose content contains the line `status: error`, then
  `scri restore` — assert exit code 0. Fails on `main` today. Same shape for
  `ThrowIfWriteFailed`.
- The ~30 existing `.Should().Contain("status: error")` assertions stay **untouched**.
  They assert against the public tool methods, which still return strings; that they keep
  passing unmodified is itself evidence of behaviour preservation.

## Branch A2 — `search-contract`

Covers F2 (overload set), F5 (double materialization), the scorer half of F4, the
`PluginPipeline` duplication (fact 13), and server-side filtering.

### Changes

**1. `SearchQuery` in `src/Scrinia.Core/Search/`.**

```csharp
public sealed record SearchQuery(string Query)
{
    public string? Scopes { get; init; }
    public int Limit { get; init; } = 20;
    public string? ExcludeTopics { get; init; }
    public string? PathPrefix { get; init; }
    public IReadOnlyDictionary<string, string[]>? AttributeFilter { get; init; }
    public IReadOnlyDictionary<string, double>? SupplementalScores { get; init; }
    public IReadOnlyDictionary<string, double>? EntityGraphScores { get; init; }

    /// Pre-materialized candidates. When set, the store skips its own ListScoped —
    /// the caller already paid for it.
    public IReadOnlyList<ScopedArtifact>? Candidates { get; init; }
}
```

`IMemoryStore` keeps exactly one search member:

```csharp
IReadOnlyList<SearchResult> SearchAll(SearchQuery query);   // no default implementation
```

The five overloads and their four default bodies are deleted. `FileMemoryStore`'s four
near-duplicate bodies (fact 7) collapse to one.

**2. `SearchPipeline` moves to `src/Scrinia.Core/Search/SearchPipeline.cs`.** Per fact 14
this is a lift with no new dependencies, and Core is the one project all three callers
reference.

```csharp
public static Task<SearchOutcome> ExecuteAsync(
    IMemoryStore store,
    SearchQuery query,
    ISearchScoreContributor? contributor,
    EntityGraphScoreContributor? entityContributor,
    string? context = null,
    bool surfaceEntityFacts = false,
    CancellationToken ct = default);
```

`SearchOutcome` — currently a nested record struct on `ScriniaMcpTools` — moves to
`Scrinia.Core/Search/` alongside the pipeline.

Taking the contributors explicitly **is** the scorer half of F4. Three callers resolve
them from ambient context at the edge and pass them in: `ScriniaMcpTools.Search`,
`ScriniaCommands.Search`, `PluginPipeline.SearchAsync`. `ExecuteSearchAsync` becomes a
thin forwarder or disappears. This removes the drifted duplicate rather than adding
filter parameters to both copies.

**3. F5 falls out.** `SearchPipeline` materializes candidates once for the contributors,
then sets `Candidates` on the `SearchQuery` it hands to `SearchAll`. `FileMemoryStore`
uses them instead of calling `ListScoped` a second time.

Related: narrow `LoadIndex` to return `IReadOnlyList<ArtifactEntry>` so
`LoadIndexUnsafe:593` can return the cached list directly instead of `.ToList()`.

**CORRECTED 2026-08-07 during Task 7, then CORRECTED AGAIN 2026-08-07 during Task 7
review — the first correction's mutating-caller count was itself wrong, and it
inverted the trade-off it was arguing.**

It read: "Narrowing the return type does not remove the copy above it; it relocates
it. `LoadIndex` has roughly ten mutating callers (`Upsert`, `Remove`, migration
helpers, and similar) that need a mutable list back... `LoadIndex` stays
`List<ArtifactEntry>`, returning a fresh copy." That count was asserted from the
variable name `existingEntries` at a few call sites, not traced. Traced, of the 20
production `LoadIndex` call sites: `Upsert` and `Remove` do not call `LoadIndex` at
all — they write sidecars directly via `GetStoreDirForScope`/`WriteSidecar`/
`File.Delete` and invalidate the cache, bypassing it entirely. Exactly **one** site
mutates the returned list: `FileMemoryStore.ImportTopicEntries` (`:1603-1630`), which
does `existingEntries[idx] = updatedEntry` / `existingEntries.Add(updatedEntry)`
before `SaveIndex`. The other 19 call sites use `FirstOrDefault`, `foreach`, `.Count`,
`.Any`, or `.Select` — all of which compile unchanged against
`IReadOnlyList<ArtifactEntry>`.

On the real count the trade is **favourable**, not against: narrowing costs one
explicit `.ToList()` (in `ImportTopicEntries`) and saves the other 19 callers a
defensive copy per call, each of which currently also pays `LoadIndex`'s unconditional
`FileLock.AcquireShared` (see the measurement section below) even on a cache hit. What
survives as a reason to defer, not reject: it is a public change to an `IMemoryStore`
member unrelated to search, made inside a branch scoped to the search contract; and it
needs its own analysis of whether handing 19 read-only callers a live alias into
`_indexCache`'s backing list is safe under that cache's invalidation strategy — nobody
has done that analysis. **Status: not implemented here, deferred to the residuals spec
for proper evaluation** — not rejected. See
`/findings/search-contract-deferred-followups`.

**4. Server-side filtering.** Once `PluginPipeline.SearchAsync` delegates to
`SearchPipeline`, it supports every filter. The remaining work is transport:

- **Add `POST /api/v1/stores/{store}/search`** taking a `SearchRequest` body with all
  filter fields. `attributeFilter` is a `Dictionary<string, string[]>` and belongs in a
  body, not a query string.
- **Keep `GET /search`**, additively extended with `excludeTopics` and `pathPrefix`
  (both plain strings). Existing clients unaffected.
- `HttpMemoryStore.SearchAll(SearchQuery)` POSTs when the query carries an
  `attributeFilter`, GETs otherwise.

### The measurement

F5's benefit is asserted, not known. Ingest `datasets/locomo` into a scratch workspace,
time search before and after the `Candidates` change, and **record the actual numbers in
this document** — including if the delta is noise. The change stands on removing a
duplicated call at a boundary that could not express the alternative; it will not be
described as a speedup it is not.

**MEASURED 2026-08-07, Task 6, via the new `scrinia-bench searchcost` subcommand
against a LoCoMo-seeded corpus, 3 runs.** Median `FileMemoryStore.SearchAll` cost with
`SearchQuery.Candidates` pre-materialized vs `Candidates = null`: ~0.87–0.91ms vs
~1.09–1.14ms at a 70-memory corpus; 2.16ms vs 2.45ms at 272 memories. The delta is real
and reproducible across runs, but sub-millisecond in absolute terms — corpus size grew
3.9× (70→272) while the delta grew only 1.3×, so the saving is dominated by a fixed
per-call cost, not the removed `O(n)` list copy scaling with corpus size.

**CORRECTED 2026-08-07 during Task 7 — the mechanism behind that fixed cost was
misreported.** Task 6's own report attributed the flat scaling to "`ListScoped` doesn't
re-touch the filesystem after the first call." That is false. `FileMemoryStore.LoadIndex`
(`:576`) calls `FileLock.AcquireShared` — an OS file-handle open — on **every** call,
before `LoadIndexUnsafe` (`:592`) ever consults the in-memory cache (`:593`). The
cache hit is real, but it is reached only after paying a lock-acquire on each call.
That per-call file-handle open is the fixed cost the flat delta is measuring: removing
the second `ListScoped` call removes one lock-acquire-then-cache-hit round trip, which
is why the saving is roughly constant per call rather than growing with corpus size.
This supports the original conclusion better than the cited mechanism did; the
conclusion is unchanged.

**This strengthens after the Task 7 review correction to the `LoadIndex` narrowing
discussion above:** of `LoadIndex`'s 20 production call sites, 19 are read-only. Each
of those 19 currently pays both `LoadIndex`'s unconditional file-handle open and a
defensive `.ToList()` copy on every call, including a pure cache hit — the lock cost
this section measures, plus a second, distinct cost this section doesn't measure.

272 memories is a small corpus. Whether this delta holds at thousands of entries in one
scope is inference, not measurement.

### Error handling

Removing the default implementations is the point: a store that cannot filter now fails
to compile rather than returning a quietly under-filled list. `HttpMemoryStore`
implements the real thing, so nothing throws — but the next implementation is forced to
choose deliberately.

### Testing

- **A store conformance suite run against both stores.** Same fixture corpus, same
  `SearchQuery` inputs, asserting identical result sets. Nothing like it exists today,
  and it is the test that would have caught the original defect.
- **Fill-to-limit, explicitly:** with 50 memories of which 40 are excluded by
  `pathPrefix`, a `limit: 20` search returns 20 — asserted for both stores.
- **Pipeline parity:** the server `/search` path and the MCP `search` path return the
  same ranking for the same corpus. Regression guard on fact 13.
- `SearchExcludeTopicsTests` and `HybridScorerTests` are rewritten to the new signature —
  mechanical, and they are the behavioural pin while the overloads collapse.

  **CORRECTED 2026-08-07 during Task 7 — half of this claim was WRONG.**
  `HybridScorerTests` was rewritten to the new `SearchQuery` signature, as described.
  `SearchExcludeTopicsTests` never called `SearchAll` at all — per F13 (Task 4's
  call-site audit) it drives `ScriniaMcpTools.MemorySearch` through the MCP tool
  surface, which does not change shape when `IMemoryStore.SearchAll` collapses to one
  overload — so it needed no change and none was made. Recorded because the claim was
  stated as fact, not an estimate, and was half wrong.

## Branch A3 — `store-boundaries`

Covers F3 (interface split), the surviving half of F4, and F6 (vocabulary).

### F3 — split at the throw line

`ILocalStoreLayout` takes `ArtifactPath`, `ArtifactUri`, `FindArtifactPath`,
`GetStoreDirForScope`, `WorkspaceRoot`, `ListTopicArtifacts`, `ImportTopicEntries`.
`FileMemoryStore` implements both interfaces; `HttpMemoryStore` implements only
`IMemoryStore`.

`SaveIndex` goes on **neither** interface. Per fact 10 it has no production callers
outside the store implementations, so it comes off `IMemoryStore` and stays a public
member of `FileMemoryStore` for the tests that use it. Putting it on
`ILocalStoreLayout` would be wrong — it is index persistence, not path layout, and no
caller needs it through an interface.

`GenerateContentPreview` moves off the interface to a static helper. It is a pure string
function on a storage interface; 21 mechanical call sites (fact 12).

This takes `HttpMemoryStore` from 8 `NotSupportedException` sites to **1**: the `file://`
rejection inside `ResolveArtifactAsync`, which is correct where it is — a rejection of an
input value, not an unimplementable capability.

The ~32 call sites typed `IMemoryStore` that use layout members (fact 11) change their
parameter type to `ILocalStoreLayout`. All are local-only features by nature — bundles,
drift detection, file entities — so the narrower type states what they already meant.

### F4 — one install point for the default sink

Per the corrections above, the lifetime work is closed. What remains is ownership:
`MemoryEventSinkContext.Default` is assigned from six places (fact 16), each constructing
its own `CompositeEventSink`.

Add `MemoryEventSinkContext.InstallDefault(params IMemoryEventSink[] sinks)` and route
all six through it. `Default` itself, the AsyncLocal fallback, `StoreScope`'s clear, and
`BackgroundWork` are **untouched**.

### F6 — one vocabulary

Rename the server handler methods to the MCP vocabulary, which is the published one:
`StoreMemory` → `RememberMemory`, `ShowMemory` → `RecallMemory`, `SearchMemories` →
`SearchMemory`, `ListMemories` → `ListMemory`. **Route paths and the HTTP contract do not
change** — internal naming only.

Delete the stale tool-list comment at `ScriniaMcpToolsTests.cs:11-23`, which still
documents `Show` / `Store` / `Export` / `Import` as the tool surface. Delete rather than
update: the tests below it are the documentation.

### Testing

- **The 1-remaining-throw property as a test.** Reflect over `IMemoryStore`'s members,
  call each against `HttpMemoryStore`, assert exactly one throws `NotSupportedException`
  and that it is the `file://` path. The failure mode this pins is someone adding a
  local-only member back to the shared interface, which nothing catches today.
- **Sink composition:** assert `InstallDefault` produces the same composite for each of
  the six paths that previously built one by hand.
- **No new event-sink lifetime tests**, and specifically no per-test drain. See the
  measured trap in the corrections section.

## Verification

Per the discipline established in the 2026-08-05 spec and the environment traps on
record at `/findings/event-sink-throw-crashes-test-host`:

- `dotnet build Scrinia.sln -c Debug` clean before any test run.
- **Never** two `dotnet test` invocations against one project concurrently.
- **Never** `dotnet build` during a test run — it fails on locked DLLs and can wedge the
  in-flight run.
- Clear stray `testhost.exe` / `dotnet.exe` before a full run; apparent hangs are
  orphaned processes holding `FileLock`s on `%TEMP%` stores.

Test scope per branch:

| Branch | Projects |
|---|---|
| A1 | `Scrinia.Tests`, `Scrinia.Server.Tests` |
| A2 | the above, plus `Scrinia.Retrieval.Benchmarks.Tests` and `Scrinia.Plugin.Embeddings.Tests` |
| A3 | all of the above |

`Scrinia.Merge.Tests` is unaffected by every branch here (fact 18).

Each branch closes with a CHANGELOG entry and its residuals recorded to `.scrinia` under
`/findings/`, matching how the last three branches closed.

## Risks

**A2 is the real one.** Removing the default interface implementations is a deliberately
compile-breaking change across the repo. That is the intent — nothing silently inherits a
wrong filter — but it produces one large mechanical commit that cannot be partially
landed. Mitigation: the conformance suite lands first, so the mechanical commit has
something to be judged against.

**A1** risks silent output drift; mitigated by golden files committed before the
refactor.

**A3** risks call-site churn (~53 retypes across the layout split and
`GenerateContentPreview`), all compiler-checked.

## Out of scope

Deliberately excluded, with reasons:

- **Envelope hardening.** The defect is parsing the rendered string, not the format.
- **`HttpMemoryStore`'s fabricated result metadata.** `SearchAll` reconstructs each
  `ArtifactEntry` with `OriginalBytes: 0` and `CreatedAt: DateTimeOffset.UtcNow`, so
  remote results carry today's date. Ranking is unaffected (scoring happens server-side),
  but any caller reading `CreatedAt` off a remote result gets a fabricated value. Fixing
  it widens the wire format. **Recorded as a known limitation** so it is not rediscovered
  as a bug.
- **Constructor-injecting `IMemoryStore` into `ScriniaMcpTools`.** See rejected
  alternatives.
- **Event-sink lifetime work.** Closed on `event-sink-lifecycle`, with a measured trap
  against reopening it.
- **C5 and C6.** They belong to Branch 2 and are not repeated here.
- **The `[Unreleased]` CHANGELOG sweep.** Tracked at
  `/findings/file-entity-unification-deferred-followups`, triggered by cutting a release,
  not by this work.
