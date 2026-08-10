# Residuals triage — verified against `c3773bf`, 2026-08-08

Source: the six `/findings/<branch>-deferred-followups` memories. Every OPEN item below
carries the `file:line` that proves it at HEAD. Items marked STALE are cases where the
recorded memory does not match the code.

Severity anchors on cost-to-fix-later, not on how the memory phrased it.

---

## Tier 1 — silent failure: reports success, does nothing or loses data

| # | Item | Evidence at HEAD | Status |
|---|---|---|---|
| T1.1 | Remote `remember()` drops the attributes dictionary — entities, anchors, arbitrary keys never reach the server | `HttpMemoryStore.cs:106-107` | OPEN, **fact corrected** |
| T1.2 | `HttpMemoryStore.LoadIndex` returns `[]`, so the interface's default `TryUpdateEntry` finds nothing and returns false — entity-typing promotion writes evaporate remotely with no error | `HttpMemoryStore.cs:253-254`; default at `IMemoryStore.cs:33-41` | OPEN |
| T1.3 | `conflictDir` of `"."` or `""` writes a `.gitignore` containing `*` into the artifact directory — silently ignores every untracked memory at and below it, and clobbers a user `.gitignore` | `Nmp2ConflictHandler.cs:118-119` returns true on equality | OPEN |
| T1.4 | New client vs older server: `excludeTopics`/`pathPrefix` sent as additive query params. An older server ignores unknown params, so filters silently fail open. No version handshake exists | `HttpMemoryStore.cs:409-417` | OPEN |
| T1.5 | `SearchQuery.Candidates` is a silent-correctness contract, not a performance hint — a differently-scoped candidate list changes the answer | `FileMemoryStore.cs:1283-1284` | OPEN |

**T1.1 is corrected from the memory.** The store-boundaries residual says `StoreApiRequest`
"carries only content + name". It actually carries `Content, Name, Description, Tags,
Keywords, ReviewAfter, ReviewWhen`. The loss is narrower and more specific than recorded:
tags and review dates *do* cross the wire; the general attributes map does not.

---

## Tier 2 — dead on arrival: visibly broken surfaces

| # | Item | Evidence at HEAD | Status |
|---|---|---|---|
| T2.1 | `POST /import` declares `string[]? topics = null` with no `[FromQuery]`, so Minimal API infers a JSON body and every multipart bundle upload gets 415 before the handler runs. Sibling `ExportTopics` uses a DTO correctly | `MemoryEndpoints.cs:370`; sibling at `:337` | OPEN |
| T2.2 | `scri bundle export` / `scri bundle import` exit 0 on genuine failure via dead `StartsWith("Error:")` guards — the same mirror defect already fixed for `show`/`forget` | `ScriniaCommands.Bundle.cs:57`, `:99`, both now carrying `// NOTE: dead guard` comments at `:51`, `:95` | OPEN |
| T2.3 | The legacy four-argument branch sits outside the top-level try/catch, so an IOException still escapes as a raw stack trace into git's merge output | branch at `Program.cs:13-35`, `try` opens at `:37` | OPEN |
| T2.4 | Success path phrased as an error: both driver keys reported unconditionally, so a workspace that registered only nmp2 is told meta "could not be read" | `Program.cs:29-33` | OPEN |

---

## Tier 3 — efficiency and hardening

| # | Item | Evidence at HEAD | Status |
|---|---|---|---|
| T3.1 | `LoadIndex` acquires `FileLock.AcquireShared` unconditionally *before* the in-memory cache is consulted — every call pays an OS handle open even on a pure cache hit | lock at `FileMemoryStore.cs:576`, cache check at `:592` | OPEN |
| T3.2 | Narrowing `LoadIndex` to `IReadOnlyList<ArtifactEntry>`. Routed here by name — "EVALUATE THIS PROPERLY IN THE RESIDUALS SPEC, do not skip it as already-rejected". Blocked on an aliasing-safety analysis of `_indexCache` nobody has done | 23 occurrences across 9 `src/` files; 141 across `tests/` | OPEN, needs analysis first |
| T3.3 | `IMemoryEventSink.InstallDefault(params …)` has no guard: empty array silently installs a no-op sink; a null element becomes an NRE that `CompositeEventSink`'s own catch swallows | `IMemoryEventSink.cs:39-40` | OPEN |
| T3.4 | `attributeFilter` bounds key *count* at 64 but never bounds per-key value-array length | `MemoryEndpoints.cs:300` | OPEN |
| T3.5 | Remote mode never forwards `SupplementalScores`/`EntityGraphScores` — no capability surface for remote scoring | absent throughout `HttpMemoryStore.cs` | OPEN, design question |
| T3.6 | `HttpMemoryStore` fabricates search-result metadata: `OriginalBytes: 0`, `CreatedAt: UtcNow`. Any caller reading a remote result's creation time gets "now" | `HttpMemoryStore.cs:375-377` | OPEN, needs wire widening |
| T3.7 | `ArtifactUri` returns a synthesized `remote://{store}/{scope}/{name}` that no caller can dereference | `HttpMemoryStore.cs:492-493` | OPEN, orthogonal |

---

## Tier 4 — hygiene, one-line fixes

| # | Item | Evidence at HEAD | Status |
|---|---|---|---|
| T4.1 | Two dead using directives | `MemoryEndpoints.cs:1` (`System.Threading.RateLimiting`), `:7` (`Scrinia.Plugin.Abstractions`) | OPEN |
| T4.2 | `BuildSearchGet` checks `scopes is not null` while its two siblings check `!IsNullOrWhiteSpace` — an empty-string scopes still emits `&scopes=` | `HttpMemoryStore.cs:411` vs `:413`, `:415` | OPEN |
| T4.3 | The `"conflicted": true` sidecar flag has one writer and zero readers — a spurious committed diff on every conflicted merge that nothing can act on | writer `Nmp2ConflictHandler.cs:126`; no reader in `src/` | OPEN — decide reader or delete |
| T4.4 | `MemoryList` builders use `AppendLine` (`\r\n`) while `ToYaml` splits on `\n` only, leaving stray `\r`. Masked by `GoldenNormalizer`'s blanket `\r` strip | 20 `AppendLine` calls in `MemoryTools.Core.cs` | OPEN — fixing moves every golden |
| T4.5 | `Status == "error"` stringly-typed with no shared predicate | 6 production sites, not the 8 recorded: `MemoryTools.Core.cs:1377`, `ScriniaCommands.cs:66`, `Memory.cs:421`, `:467` (positive); `MemoryTools.cs:154`, `:259` (negative) | OPEN, **count corrected** |

---

## Tier 5 — test coverage

| # | Item | Evidence at HEAD | Status |
|---|---|---|---|
| T5.1 | `ChatProviderHttpTests` never exercises the transient → `RecordFailure` branch — its only non-success case is HTTP 400, which `TransientDetector` does not classify as transient. This is the single place all three chat providers' breaker bookkeeping lives | only `OK` at `:27` and `BadRequest` at `:44` | OPEN |
| T5.2 | `PathRouterTests.TryToFileEntityPath_RejectsUnsafeNames` has no rooted-path case despite `FileEntityName.IsValid` checking `Path.IsPathRooted` | InlineData at `:199-203` | OPEN |
| T5.3 | `StorePurityInvariantTests` globs only `*.md` while its own doc states the invariant as "no .md AND no foreign .meta.json" | `:28` | OPEN — needs a known-names filter, real judgement call |
| T5.4 | `Migration_DoesNotSweepTheAgentDirectory` discards `Migrate()`'s result — passes vacuously if migration ever short-circuits | `AgentFileEntityTests.cs:63` | OPEN |
| T5.5 | `BundleImportOfAnAgentTopic_…` calls `store.SaveIndex` directly rather than a bundle round-trip — tighter than the name promises | `AgentFileEntityTests.cs:89` | OPEN |
| T5.6 | Round-trip test for `merge.config` — the contract is declared twice (CLI writer, `MergeConfig` reader) with nothing pinning them. That exact divergence *was* the camelCase bug. Called the highest-value follow-up by that branch's reviewer | no cross-assembly round-trip test found | OPEN |
| T5.7 | `ScanForConflicts`' `.nmp2` `TryReadConflictCopy` calls untested — a swapped current/incoming argument ships undetected | `MemoryTools.Lifecycle.cs:250-251` | OPEN |
| T5.8 | `GitMergeHarness.LocateDriver` picks the newest `scri-merge` by mtime, so a stale Release build can be exercised against a Debug run | `GitMergeHarness.cs:46` | OPEN |
| T5.9 | Golden gaps: no fixture covers a `warning` status or a `followUp`/truncation section; `compact` golden never exercises the multi-chunk merge branch | 16 fixtures under `Golden/__snapshots__/` | OPEN |
| T5.10 | Remote `remember()` file-refs mutation test only discriminates a CWD-shaped fabricated root | as recorded in store-boundaries | OPEN, narrow |
| T5.11 | `Scrinia.Server.Tests` needed a duplicate set of test doubles because `Scrinia.Tests`' `FakeMemoryStore` is internal to another assembly | as recorded | OPEN — dedup target |

---

## Tier 6 — docs

| # | Item | Evidence at HEAD | Status |
|---|---|---|---|
| T6.1 | CHANGELOG `[Unreleased]` carries three sections on where agent configs live, written at three points in that directory's history, and one is flatly wrong | `CHANGELOG.md:368-369` says "`skill` (entity) and `agent` scopes are unchanged", contradicting `:213-230` and `:281-301` | OPEN |
| T6.2 | The `.gitignore` fix prevents new commits of conflict copies but does not untrack ones a workspace already committed, and `reconcile()` still prefers them with no freshness check. The upgrade note doesn't say so | `docs/multi-user-setup.md:52` still asserts the copies "stay out of the" commit | OPEN |
| T6.3 | Stale comment cites the deleted `GetScriniaBaseDir` and describes a mechanism that no longer gates the test | `BundleZipSlipTests.cs:37` | OPEN |

---

## Closed — verified, no work needed

- `GetResultScope` dead code — deleted in store-boundaries Task 6.
- `docs/architecture/core.md`'s wrong sentence about the three `IMemoryStore` static helpers — the passage is gone.
- `BuildSearchPost`'s `OrdinalIgnoreCase` comparer bug — comparer dropped, `HttpMemoryStore.cs:432`.
- `BuildFullList`'s throwing `WorkspaceRoot` access — degrades now, pinned by a test.
- `reconcile()` sidecar staleness after resolve — carried but **unverified**; see below.

## Deliberate no-action, recorded so nobody "fixes" them back

- Bundle import containment re-anchored to `categoryDir`: stronger against traversal, weaker
  for symlinks. A comment at the check records this.
- `FileEntityDirectories_SitOutsideTheStore` looks weak but is logically complete.
- The 500-char conflict-preview truncation is deliberately *not* folded into `HttpErrorBody`
  — different rule, different reason to change.
- `StoreManager.GetStore` / `IStorageBackend.CreateStore` keep returning `IMemoryStore` to
  preserve the plugin seam.
- Remote `restore()` omitting `/agent/*` and remote `recall('/skill/')` refusing are
  pre-existing behaviours now pinned by tests. Both are arguably wrong on the merits; changing
  either is a feature, not a regression fix.

## Process items — no code

- Plan code-block comments ship as production comments; add a plan-review step that reads them
  as already-committed. (Six findings on one branch traced to this.)
- SDD ledger needs a standing branch-close section: CHANGELOG check, source-spec fact
  re-verification.
- `rtk err` is not a build gate. Already saved to auto-memory.

## Still unverified

- `reconcile()` leaving `originalBytes`/`chunkCount`/`contentPreview`/`keywords` stale after
  resolving, with no reindex triggered.
- Whether `link.yaml`'s golden covers the *partial-failure* envelope or only success.
- Whether `WorkspaceRoot` still bypasses the layout-migration latch (recorded as harmless at
  the time, with the reasoning noted as incomplete).

## One more stale fact

The mcp-response-shape memory says the info-bullet prepend→append change is "provably
unreachable today because Remember/Append never call `WithInfo`". `WithInfo` now has six
production call sites (`MemoryTools.cs:84`, `:155`, `:260`; `Lifecycle.cs:604`;
`Skills.cs:346`, `:379`), two of which append to an existing `Info`. The reachability
argument no longer holds and the ordering concern needs re-deciding.
