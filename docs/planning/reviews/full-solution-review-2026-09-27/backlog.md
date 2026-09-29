> **Status: Minor findings — tracked backlog.** The two Major items (FileLock cross-process enforcement off Windows; a same-process cache-read race in `FileMemoryStore`) were already fixed in a draft and are recorded in the Resolution ledger below.

# Full-Solution Review — Minor-Finding Backlog

**Date:** 2026-09-27
**Source:** 4-layer (Correctness / Security / Performance / Style) review of the entire solution
**Workstreams:** 6 parallel reviewers, run across the whole `Scrinia.sln`
**Verdict:** Zero critical, zero major correctness/security defects on the tested (Windows) platform. The two Major items were remediated in a draft before this backlog was compiled. The items below are all **Minor** — cleanups, hardening opportunities, and design questions — and are ordered by priority so they can be worked over time.

---

## How to use this backlog

Each finding carries a stable id of the form **REV-NNN** (REV-001 … REV-025, in the order the findings were recorded). Every id appears exactly once. Findings are grouped into four tiers:

| Tier | Meaning | Typical work |
|------|---------|--------------|
| **Tier 1** | Quick wins — low-risk cleanups: duplicated code, doc/code mismatches, misleading names, test-scope clarifications. Safe, mechanical, no behavior change. | One-line refactor, doc fix, rename. |
| **Tier 2** | Correctness / security hardening — real impact under a plausible input or failure, medium confidence. Worth doing before more of the surface is touched. | Bounds checks, escaping, ordering/dispose fixes, secret-surface tightening. |
| **Tier 3** | Design discussions / verify-before-act — low-to-medium confidence, an author or maintainer decision (and often a test) is required before changing. | Sync-over-async policy, semantics questions, scaling/normalization intent, integration coverage. |
| **Tier 4** | Nice-to-have — perf-only or purely cosmetic items, lowest priority. Knock these out when there is spare budget; never blocks a release. | Algorithmic micro-opts, double-measure skips, summary pass folds, cosmetic rename. |

Tier 4 is a *priority overlay*, not a separate bucket: the five items there are also Tier-3-eligible design/verify discussions that the review additionally flagged as perf-only and therefore lowest priority.

The **Backlog Index** table (below) gives one line per finding. The **Tier sections** following it expand each into Problem / Recommended fix / Risk if deferred. The **Resolution ledger** at the bottom tracks the two Major items and any item once it is resolved.

---

## Backlog Index

| ID | Title | Layer | Location | Effort | Tier | Status |
|----|-------|-------|----------|--------|------|--------|
| REV-001 | `IsJson` duplicated three times | Append / Recovery | `RecoveryEnvelope.cs:639, 776, 1046` | S | 1 | fixed |
| REV-002 | `MapProtection` reason→protection mapping duplicated & divergent | Append / Recovery | `RecoveryDiscovery.cs:526–536`, `:590–599` | M | 2 | fixed |
| REV-003 | Sync-over-async in recovery-verify paths | Append / Recovery | `AppendRecoveryVerifier.cs:17–20, 28–31, 54–63` | M | 3 | fixed |
| REV-004 | `RecoveryEvidenceKind.AppendJournal` used to verify live members | Append / Recovery | `AppendRecoveryVerifier.cs:18, 29` | S | 1 | fixed |
| REV-005 | `publicationWasVisible` semantics committed-vs-live undefined | Append / Recovery | `AppendRecoveryConductor.cs:100, 132, 155` | M | 3 | fixed |
| REV-006 | `DurableFile` class summary contradicts `SyncDirectory` | Append / Recovery | `DurableFile.cs:9–11`, `:97` | S | 1 | fixed |
| REV-007 | Negative `##PAD` footer is not upper-bounded | Encoding | `Nmp2Strategy.cs:177, 319` | M | 2 | fixed |
| REV-008 | `DecodeChunkSection` re-scans from the top per chunk (O(k·n)) | Encoding | `Nmp2Strategy.cs:297–316`, `Nmp2ChunkedEncoder.cs:350–352` | M | 4 | fixed |
| REV-009 | `IsCanonicalName` rule triplicated | Config | `WorkspaceConfig.cs:205`, `SharedBaseRegistry.cs:34`, `HomeConfig.cs:163` | S | 1 | fixed |
| REV-010 | `SamePhysical` normalization differs between callers | Config | `SharedBaseOperations.cs:108–112`, `SharedBaseRegistry.cs:204` | S | 1 | fixed |
| REV-011 | Encode-twice to measure encoded length | Models | `MemoryWrite.cs:277` | M | 4 | open |
| REV-012 | `local-topic:` recovery shim does not reject `..` | Paths | `PathRouter.cs:123–127` | M | 3 | fixed |
| REV-013 | Unguarded `SafeTensors` header parse | Embeddings | `SafeTensorsReader.cs:36, 39–40` | M | 2 | fixed |
| REV-014 | `IsSecret` convention is the whole guardrail for argv | Embeddings | `PluginConfigProtocol.cs:87–88`, `McpPluginHost.cs:116–125` | S | 2 | fixed |
| REV-015 | BM25 scaled in two magnitude domains | Search | `WeightedAttributeScorer.cs:74`, `:178` | S | 3 | fixed |
| REV-016 | Exponential backoff footgun for large configured values | Resilience | `RetryPolicy.cs:96–101`, `RetryOptions` clamp | S | 3 | fixed |
| REV-017 | `YamlEscape` does not escape control bytes | MCP Facade | `McpResponse.cs:332–342`, `SharedServer.cs:268–289` | M | 2 | fixed |
| REV-018 | `action` / `reason` emitted without `YamlEscape` | MCP Facade | `McpResponse.cs:278–279`, `:288–289` | S | 1 | fixed |
| REV-019 | `BuildSummary` computes review status twice per entry | MCP Facade | `MemoryTools.Core.cs:868–871`, `AttributeReaders.cs:45–52` | S | 4 | fixed |
| REV-020 | Conflict scan reads per file on every session start | MCP Facade | `MemoryTools.Lifecycle.cs:397–409` | S | 4 | open |
| REV-021 | `FactsResponseFormatter` default swallows unmatched result types | MCP Facade | `FactsResponseFormatter.cs:22` | S | 4 | fixed |
| REV-022 | `ResolveFiles` silently drops misspelled comma-parts | CLI | `ScriniaCommands.cs:350–372`, `ScriniaCommands.Bundle.cs:149` | S | 3 | fixed |
| REV-023 | GPU provider can leak across `EnsureInitialized` retries | Plugins | `EmbeddingsPlugin.cs:154`, `:172–176` | M | 2 | fixed |
| REV-024 | Decompression-bomb guard not exercised end-to-end on import | Tests | `BundleFormatService.cs:642–659`, `Nmp2Strategy.MaxDecodedBytes` | M | 3 | fixed |
| REV-025 | `BundleZipSlipTests` documents it only hits the allowlist gate | Tests | `BundleZipSlipTests.cs:35–43`, `BundleImportCategoryAllowlistTests.cs:117` | S | 1 | fixed |

---

## Tier 1 — Quick wins

Low-risk cleanups. None of these changes behavior; they remove duplication, correct a doc, fix a misleading label, tighten escaping consistency, or clarify a test's scope. Safe to knock out in any order.

### REV-001 — `IsJson` duplicated three times
- **Location:** `src/Scrinia.Core/RecoveryEnvelope.cs:639, 776, 1046`
- **Layer:** Append / Recovery
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-27) — The three `IsJson` copies turned out to be DEAD CODE (no call sites anywhere in `src/` or `tests/`); the dedup removed the three unused private copies and added one canonical `RecoveryEnvelopeCodec.IsJson` for future use. Implemented + built clean + 106 affected tests passed.
- **Problem:** The 9-line `IsJson(ReadOnlySpan<byte>)` method is copied byte-for-byte at three locations. Three drift sites for a predicate that must stay identical.
- **Recommended fix:** Extract one `private static bool IsJson(...)` helper and call it from the three sites.
- **Risk if deferred:** A future edit to one copy that isn't mirrored elsewhere silently desyncs the three.

### REV-004 — `RecoveryEvidenceKind.AppendJournal` used to verify live members
- **Location:** `src/Scrinia.Core/AppendRecoveryVerifier.cs:18, 29`
- **Layer:** Append / Recovery
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-27) — Labeled the live-member evidence correctly; `RecoveryEnvelopeCodec` still routes it as `RecoveryOperation.Append`. Implemented + built clean + 106 affected tests passed.
- **Problem:** `MatchesAppendMember` / `MatchesArchiveBytes` pass `RecoveryEvidenceKind.AppendJournal` when verifying *live* append members (a journal, not a live artifact). Harmless today: `IsValidKind` only gates enum-validity and the accounting budget carries through. But the label misleads anyone reading the call site into thinking the evidence is a journal.
- **Recommended fix:** Rename or add an enum member (e.g. `AppendMember`) so the live-member path is labeled correctly; confirm `RecoveryEnvelopeCodec` still routes it as `RecoveryOperation.Append`.
- **Risk if deferred:** Callers and reviewers misread the evidence type; a follow-up author may "fix" it into a real behavior change.

### REV-006 — `DurableFile` class summary contradicts `SyncDirectory`
- **Location:** `src/Scrinia.Core/DurableFile.cs:9–11` (summary), `:97` (`SyncDirectory`)
- **Layer:** Append / Recovery
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-27) — Corrected the class summary so it states both the file and its containing directory are fsynced. Implemented + built clean + 106 affected tests passed.
- **Problem:** The class summary states the file contents are flushed *"but the containing directory is not"*. `AtomicWrite` calls `SyncDirectory(path)` at line 97, which fsyncs the directory. The doc is wrong and could mislead a reviewer into thinking a power-loss window exists that does not.
- **Recommended fix:** Correct the summary to state that both the file and its containing directory are fsynced.
- **Risk if deferred:** A downstream reviewer trusts the stale doc and opens a false correctness bug.

### REV-009 — `IsCanonicalName` rule triplicated
- **Location:** `src/Scrinia.Core/Configuration/WorkspaceConfig.cs:205`, `SharedBaseRegistry.cs:34`, `HomeConfig.cs:163` (latter via `IsLowercaseAscii`)
- **Layer:** Config
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-27) — Routed all three copies through one canonical `IsCanonicalName` rule (single source of truth). Implemented + built clean + 106 affected tests passed.
- **Problem:** The identical "1–32 chars, lowercase ASCII, dash allowed" rule is hand-written three times. `WorkspaceConfig` and `SharedBaseRegistry` are inline; `HomeConfig` expresses it via a char helper. One regression drifts all three.
- **Recommended fix:** Route all three through `SharedBaseRegistry.IsCanonicalName` (already `public static`), or extract a single shared helper and delete the local copies.
- **Risk if deferred:** A name accepted in one place and rejected in another yields inconsistent shared-base/config validation.

### REV-010 — `SamePhysical` normalization differs between callers
- **Location:** `src/Scrinia.Core/Configuration/SharedBaseOperations.cs:108–112`, `SharedBaseRegistry.cs:204`
- **Layer:** Config
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-27) — Shared one `SamePhysical` implementation so both callers normalize identically. Implemented + built clean + 106 affected tests passed.
- **Problem:** `SharedBaseOperations.SamePhysical` normalizes `right` via `SharedBaseRegistry.NormalizeRoot(right)` before the containment checks; the registry's own `SamePhysical` (`RootsOverlap(left, right) && IsWithin(left, right) && IsWithin(right, left)`) normalizes neither side. Same predicate, subtly different inputs — a root passed with a trailing separator or non-canonical form is treated differently in the two.
- **Recommended fix:** Share one implementation (or normalize both sides in the registry copy) so the two agree.
- **Risk if deferred:** Collision/physical-equality checks disagree depending on which entry point is used.

### REV-018 — `action` / `reason` emitted without `YamlEscape`
- **Location:** `src/Scrinia.Mcp/McpResponse.cs:278–279` (action), `:288–289` (reason)
- **Layer:** MCP Facade
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-27) — Wrapped both `action` and `reason` in `YamlEscape(...)` for escaping consistency. Implemented + built clean + 106 affected tests passed.
- **Problem:** Every other free-text field goes through `YamlEscape`, but `action` and `reason` are interpolated raw. `action` is always an internal constant label (safe), and `reason` is `FormatRecoveryReason`/fallback output, but this is defense-in-depth and consistency rather than an active bug.
- **Recommended fix:** Wrap both in `YamlEscape(...)` (or `SharedEnvelopeYaml.Scalar`) like the sibling fields.
- **Risk if deferred:** If `reason` ever carries a value with a YAML-special or control character, the rendered scalar can go malformed.

### REV-025 — `BundleZipSlipTests` documents it only hits the allowlist gate
- **Location:** `tests/Scrinia.Tests/BundleZipSlipTests.cs:35–43`
- **Layer:** Tests
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-27) — Renamed the test and updated its summary/header so it documents that it exercises the allowlist gate (and points readers at the traversal-covering test). Implemented + built clean + 106 affected tests passed.
- **Problem:** The crafted `../outside` category key is dropped by `CategoryToKind` before `Path.Combine` is reached, so the test proves "unknown category key is skipped," not "a traversal resolves and is contained." The harder, real-traversal case is already covered by `BundleImportCategoryAllowlistTests.ImportBundle_WithUnsafeKnownCategoryFilename_RefusesBeforeWriting` (`tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs:117`).
- **Recommended fix:** Rename the test / update its summary and header comment so its name and docs reflect that it exercises the allowlist gate, and point readers at the traversal-covering test.
- **Risk if deferred:** Future readers over-trust the test as zip-slip coverage of path resolution.

---

## Tier 2 — Correctness / security hardening

Real impact under a plausible malformed input or failure path, medium confidence. These are the items most worth scheduling next: each closes a concrete correctness or security gap, and each is fixable without an open design question.

### REV-002 — `MapProtection` reason→protection mapping duplicated and divergent
- **Location:** `src/Scrinia.Core/RecoveryDiscovery.cs:526–536` (B2) vs `:590–599` (B3)
- **Layer:** Append / Recovery
- **Confidence:** High
- **Effort:** M
- **Status:** fixed (2026-09-27) — implemented, built clean, 551 affected tests passed.
- **Problem:** The two adapters map `RecoveryBlockReason` → `MaintenanceProtectionReason` with different catch-alls. B2 (`:526–536`) maps `ReplayIoFailed → Inaccessible` explicitly and falls through an *unknown* member to `UnresolvedRecovery`. B3 (`:590–599`) has no explicit `ReplayIoFailed` case and its catch-all returns `Inaccessible`. So a new `RecoveryBlockReason` enum member maps to `UnresolvedRecovery` under B2 but `Inaccessible` under B3 — a drift with no single source of truth.
- **Recommended fix:** Consolidate to one mapping function shared by both adapters (or parameterize by the shared tail), and add a test pinning each `RecoveryBlockReason` to its protection for both B2 and B3.
- **Risk if deferred:** A new reason code is projected inconsistently across maintenance views, which can misreport protection level during recovery/restore UI.

### REV-007 — Negative `##PAD` footer is not upper-bounded
- **Location:** `src/Scrinia.Core/Encoding/Nmp2Strategy.cs:177`, `:319`
- **Layer:** Encoding
- **Confidence:** High
- **Effort:** M
- **Status:** fixed (2026-09-27) — implemented, built clean, 551 affected tests passed.
- **Problem:** `compressedLen = padded.Length - pad`. A tampered footer such as `##PAD:-999999` makes `compressedLen > padded.Length`, so `padded[..compressedLen]` throws `ArgumentOutOfRangeException` instead of `InvalidDataException`. `BundleRoundTripTotalityTests` pins that this is swallowed downstream by the broad catch in `SharedBaseRuntimeContracts.cs:472` (imports tolerate it), so the slice guard simply doesn't validate the upper bound.
- **Recommended fix:** Clamp/validate `pad` against `padded.Length` (or assert `0 <= compressedLen <= padded.Length`) and throw `InvalidDataException` on a malformed footer, matching the length/CRC contract of `Decode`.
- **Risk if deferred:** Malformed/tampered artifacts surface as the wrong exception type at the slice; behavior is correct-but-inconsistent and unguarded at the decoder itself.

### REV-013 — Unguarded `SafeTensors` header parse of untrusted data
- **Location:** `src/Scrinia.Core/Embeddings/SafeTensorsReader.cs:36`, `:39–40`
- **Layer:** Embeddings
- **Confidence:** High
- **Effort:** M
- **Status:** fixed (2026-09-27) — implemented, built clean, 551 affected tests passed.
- **Problem:** `element.GetProperty("dtype").GetString()!` — the `!` only silences the compiler; a hostile `.safetensors` with `dtype` as a JSON number/null yields a null `Dtype`, and `ReadFloatTensor` (`:54`) dereferences it → `NullReferenceException`. Non-integer `data_offsets`/`shape` throws `JsonException`. Both escape `Model2VecModelManager.IsModelAvailable`'s catch list (`src/Scrinia.Core/Embeddings/Model2VecModelManager.cs:44–67`) and are only contained by the `EmbeddingProviderFactory.Create` outer try/catch.
- **Recommended fix:** Validate `dtype` is a string and `shape`/`data_offsets` are integer arrays in `ReadHeader` (or wrap the per-field access defensively); add the resulting exception types to `IsModelAvailable`'s catch list.
- **Risk if deferred:** A crafted model file can throw an uncaught NRE/`JsonException` in a code path that is supposed to report "model unavailable" rather than crash.

### REV-014 — `IsSecret` convention is the sole argv guardrail
- **Location:** `src/Scrinia.Core/Embeddings/PluginConfigProtocol.cs:87–88`, `src/Scrinia/Services/McpPluginHost.cs:116–125`
- **Layer:** Embeddings
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-27) — implemented, built clean, 551 affected tests passed.
- **Problem:** `IsSecret` keys a secret as `key.EndsWith("ApiKey")`. `McpPluginHost.BuildArguments` forwards every non-`IsSecret` key as `--config key=value` (world-readable argv). A future secret key not ending in `ApiKey` (e.g. `...:AccessToken`) would travel on the command line instead of the environment — the convention, not a schema, is the guardrail.
- **Recommended fix:** Make the secret surface explicit rather than convention: e.g. a `SecretKeys` allowlist in `PluginConfigProtocol`, or a `IsSecret` that also matches `AccessToken`, with a test asserting no secret key reaches `BuildArguments`.
- **Risk if deferred:** A new secret config key leaks to the process command line.

### REV-017 — `YamlEscape` does not escape control bytes
- **Location:** `src/Scrinia.Mcp/McpResponse.cs:332–342`; mirror at `src/Scrinia.Mcp/Shared/SharedServer.cs:268–289`
- **Layer:** MCP Facade
- **Confidence:** High
- **Effort:** M
- **Status:** fixed (2026-09-27) — implemented, built clean, 551 affected tests passed.
- **Problem:** `YamlEscape` only special-cases `\n` and `\r`. Any other control byte (`0x00–0x08`, `0x0B–0x1F`, `0x7F`) in a value that trips the escape condition is written raw into a double-quoted YAML scalar → malformed output. `error`/`instruction` derive from `exception.Message` (e.g. `DurableWriteFailureResponse`, `RecoveryBlockedResponse`), so an IO error carrying a stray control byte produces invalid YAML. `SharedEnvelopeYaml.Scalar` already escapes every `char.IsControl` char as `\uXXXX` — `YamlEscape` should mirror it.
- **Recommended fix:** In `YamlEscape`, escape all control characters (`\uXXXX`) the way `SharedServer` does, or delegate to `SharedEnvelopeYaml.Scalar`. Add a test with a control byte in an error message.
- **Risk if deferred:** A control byte in an error/instruction string can corrupt the MCP YAML response.

### REV-023 — GPU provider can leak across `EnsureInitialized` retries
- **Location:** `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs:154`, `:172–176`
- **Layer:** Plugins
- **Confidence:** High
- **Effort:** M
- **Status:** fixed (2026-09-27) — implemented, built clean, 551 affected tests passed.
- **Problem:** `_provider = VulkanEmbeddingProvider.Create(...)` (`:154`) owns a GPU context. If a later line (`ChunkedSignature.ForProvider` / `new VectorStore` / `new MemoryVectorIndexer` at `:172–175`) throws, `_vectorStore` (the init sentinel at `:176`) is never assigned, so the next `EnsureInitialized()` re-enters and calls `Create` again, overwriting the first `_provider` without disposing it — a leaked llama GPU context. The known `MaxChunksPerMemory` guardrail (`:107–115`) clamps to avoid exactly that one failure mode, but only that one throw site is guarded; the others are not.
- **Recommended fix:** Guard every post-`Create` construction site that can throw before the sentinel latches (or dispose `_provider` on the failure path), so a failed init does not leak the provider on retry.
- **Risk if deferred:** Repeated failed initializations under load can leak GPU contexts until the process is recycled.

---

## Tier 3 — Design discussions / verify-before-act

Low-to-medium confidence items that need an author or maintainer decision — and usually a test — before changing. None is a mechanical cleanup; each touches semantics, an async/await policy, scaling intent, or coverage.

### REV-003 — Sync-over-async in recovery-verify paths
- **Location:** `src/Scrinia.Core/AppendRecoveryVerifier.cs:17–20, 28–31, 54–63`; also `AppendJournalWriter.cs:35–37`, `AppendRollbackJournalReader.cs:21–24`, `RecoveryDiscovery.cs:398–399`, and the three adapter `RecoverCommittedCleanup` methods.
- **Layer:** Append / Recovery
- **Confidence:** Medium
- **Effort:** M
- **Status:** fixed (2026-09-28) — Converted the async recovery conductor to async: `RecoverAppendUnsafeAsync` is now `async` and awaits `VerifySingleAsync` (journal verify) plus the new `AreAppendMembersCurrentAsync`/`HasCommittedHashAsync` verifier variants (sync-over-async). All 5 production callers are async and use `.ConfigureAwait(false)`. The sync publish/rollback paths (`AppendRollbackExecutor`, `AppendJournalWriter`, `AppendRollbackJournalReader`, `RecoveryDiscovery.ReadVerified`, `RecoverCommittedCleanup`, `WriteArchiveMember`) were intentionally left sync-over-async — async there would cascade into the store's recovery contract across ~20 call sites. Build clean; 239 append/recovery tests passed.
- **Problem:** Recovery-verify paths call `.AsTask().GetAwaiter().GetResult()` on `RecoveryEvidenceVerifier.VerifySingleAsync`. The async form exists and is used correctly elsewhere (`AppendMemberVerifier.VerifyAsync` awaits `ConfigureAwait(false)` at `AppendMemberVerifier.cs:30`). Sync-over-async defeats async deadline accounting under load and risks thread-pool starvation/deadlock during recovery sweeps.
- **Recommended fix:** Decide the recovery-path concurrency policy: either thread `CancellationToken`/async through the verify callers to `await` the verifier, or justify the synchronous boundary with a comment explaining why blocking is safe here. Add a load/deadline test if keeping sync.
- **Risk if deferred:** Under recovery load, blocking on async verification can starve the thread pool or stall the deadline accounting that bounds verification cost.

### REV-005 — `publicationWasVisible` committed-vs-live semantics undefined
- **Location:** `src/Scrinia.Core/AppendRecoveryConductor.cs:100` (computed), `:132` (committed return), `:155` (rollback return); callers discard it at `FileMemoryStore.cs:2365, 2571, 2638, 3027, 3031`.
- **Layer:** Append / Recovery
- **Confidence:** Medium
- **Effort:** M
- **Status:** fixed (2026-09-28) — The conductor's `publicationWasVisible` was a misnomer (it measured `AreAppendMembersCurrent` — are members verified) and was genuinely unused by the conductor itself (only the multi-memory coordinator reads the conductor's bool as `ObservableStateChanged`). Renamed to `currentMembers`, computed once after the archive-replay branch, returned from both branches; dropped the redundant recomputation. No behavior change to the coordinator contract. Build clean; 239 append/recovery tests passed.
- **Problem:** `publicationWasVisible` is computed once (`:100`) and returned from both the committed (`:132`) and rollback (`:155`) branches, but the callers (`RecoverAppendUnsafeAsync` awaits and discards the `bool`) never use it. What the value is *supposed* to mean — was the new pair visible at publication time — is undefined between the committed and rollback interpretations, and the NewPairInProgress crash semantics are untested.
- **Recommended fix:** Decide and document what the return means per branch, wire it to a caller that needs it (or drop the return if it is genuinely unused), and add a test covering a NewPairInProgress crash.
- **Risk if deferred:** The bool is dead data with an unspecified contract; a later author may act on it incorrectly, or the committed-vs-live distinction stays unverified.

### REV-012 — `local-topic:` recovery shim does not reject `..`
- **Location:** `src/Scrinia.Core/PathRouter.cs:123–127`
- **Layer:** Paths
- **Confidence:** High
- **Effort:** M
- **Status:** fixed (2026-09-27) — REV-012 guards the local-topic: shim with PathContainment.IsWithin (throws ArgumentException on escape); REV-024 added BundleDecompressionBombTests exercising the MaxDecodedBytes guard end-to-end through ImportTopicsFromZip (64 bundle tests + 63 embeddings tests pass).
- **Problem:** `ToStoreDir`'s `local-topic:` recovery shim builds the directory with `scope[...].Replace('/', DirectorySeparatorChar)` then `Path.Combine` without rejecting `..`. A legacy journal scope encoding `../` would therefore resolve one directory up from `memories/` (still inside `.scrinia`). Trusted/legacy data, sunset-gated to 2027-03-21 (`LocalTopicSunsetDate`).
- **Recommended fix:** Validate the scope remainder for traversal segments before `Path.Combine` (mirror the live `local:` handling / `IsSafeEvidencePath` semantics), or reject any remainder containing `..`.
- **Risk if deferred:** A crafted or corrupted legacy journal scope could resolve outside `memories/`. Low severity given trusted/legacy input and the sunset gate, but the guard is absent.

### REV-015 — Legacy `Search` scorer is production-dead
- **Location:** `src/Scrinia.Core/Search/WeightedAttributeScorer.cs:50–86` (`Search`) — the only callers are `ScorerCleanupTests.cs:41,72` (test-scope). Production routing (`SearchService.cs:69–71`) never calls it; it goes through `SearchAll`.
- **Layer:** Search
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-28) — Retired the production-dead `Search` method (the only callers were two `ScorerCleanupTests`); the exact-score assertions now route through the production `SearchAll` path with `RankerOptions(AlphaRecency: 0)`, which zeroes the recency term so scores match. Also deleted the now-unused `ScoredArtifact` record. Built clean (0 warnings/errors); 46 scorer tests passed.
- **Problem:** The review's "two magnitude domains" framing was a misread. `Search` (raw BM25, no max-norm, no recency) is a dead legacy method — production never calls it (`SearchService` uses `SearchAll`), and its sole callers are two tests in `ScorerCleanupTests` that rely on its simple `ScoredArtifact` return to assert exact numeric scores (`40`, `5`). The method is retained only because `SearchAll` adds recency decay + `AlphaRelevance` scaling + a min-heap, which complicates exact-score assertions. Like REV-001 (dead `IsJson`), this is a leftover to retire, not a second intentional strategy.
- **Recommended fix:** Retire `WeightedAttributeScorer.Search`. Migrate `ScorerCleanupTests` to score via the shared `ScoreEntry` routing (both methods use it), or assert through `SearchAll` with corrected expected values, then delete the dead method.
- **Risk if deferred:** The dead raw-BM25 path stays in the codebase and its two-domain behavior keeps being misread as intentional; a future edit to `Search` (or its `ScoreEntry` routing) can drift the production scoring path without anyone noticing.

### REV-016 — Exponential backoff footgun for large configured values
- **Location:** `src/Scrinia.Core/Resilience/RetryPolicy.cs:96–101` (`ComputeDelay`); `RetryOptions` clamps `BaseDelayMs` to `[1, 10_000]` and `MaxRetries` to `[0, 10]`.
- **Layer:** Resilience
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-28) — Added an absolute 5-minute ceiling (`MaxDelayMs`) so `ComputeDelay` can never return a multi-hour backoff. `Math.Clamp(totalMs, 0, 5*60*1000)`; normal/default values are unaffected, only pathological configured `BaseDelayMs` is bounded. Implemented + built clean + 144 targeted tests passed.
- **Problem:** `ComputeDelay` computes `baseDelayMs * (1 << min(attempt, 10))`. With `RetryOptions` clamping `BaseDelayMs` to 10,000 and `MaxRetries` to 10, the final retry can be `10000 * 512 ≈ 85 min`. Defaults (200 ms / 3 retries) are fine; the footgun is only for large configured values.
- **Recommended fix:** Cap the shift (e.g. clamp `min(attempt, 10)` to a smaller exponent, or cap the max delay to a sane ceiling) and/or document the effective worst-case backoff.
- **Risk if deferred:** A misconfigured large `BaseDelayMs` produces multi-minute waits between retries and very slow transient-failure recovery.

### REV-019 — `BuildSummary` computes review status twice per entry
- **Location:** `src/Scrinia.Mcp/MemoryTools.Core.cs:868–871`; `AttributeReaders.GetReviewStatus` at `src/Scrinia.Core/Search/AttributeReaders.cs:45–52`
- **Layer:** MCP Facade
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-28) — Folded the two `entries.Count(...)` passes into one `foreach`, calling `AttributeReaders.GetReviewStatus` once per entry to compute both `staleCount` and `reviewCount`. Identical counts, half the pure calls. Implemented + built clean + 144 targeted tests passed.
- **Problem:** `BuildSummary` runs `entries.Count(...)` once for `staleCount` and once for `reviewCount`, each calling `AttributeReaders.GetReviewStatus` per entry — 4 invocations per entry across 2 passes. `GetReviewStatus` is pure and uncached, returning both flags in one call.
- **Recommended fix:** Fold into a single `entries.Sum(...)` or one `foreach` computing both flags from a single `GetReviewStatus` call.
- **Risk if deferred:** Doubling of review-status work on the common `list --summary` path — a small, permanent CPU cost.

### REV-020 — Conflict scan reads each file on every session start
- **Location:** `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:397–409`
- **Layer:** MCP Facade
- **Confidence:** Low (see note)
- **Effort:** S
- **Problem:** `BuildRestoreContextAsync` (called by both `Guide()` and `Restore()`) recursively enumerates all of `.scrinia/` to detect a `<<<<<<<` merge-conflict marker. **Note:** the current code at `:404–406` already reads each file with a bounded 10 KB buffer peek (`reader.Read(buf, 0, buf.Length)`), not `reader.ReadToEnd()`, so the "O(total bytes)" cost the reviewer cited is not present in this file. The residual concern is that a conflict marker beyond the first ~10 KB of a file is never seen, and every file under `.scrinia/` is still opened on each session-start call.
- **Recommended fix:** Confirm the bounded-read form is intentional (and document why the 10 KB cutoff is acceptable), or raise the cutoff / stream the search. If the reviewer's `ReadToEnd` observation referred to an earlier version, no code change is required.
- **Risk if deferred:** Merge-conflict markers in the tail of large files go undetected on restore; negligible perf cost given the bounded read.

### REV-021 — `FactsResponseFormatter` default swallows unmatched result types
- **Location:** `src/Scrinia.Mcp/FactsResponseFormatter.cs:22`
- **Layer:** MCP Facade
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-28) — The `FactsResult` switch default now returns `Warning("no result: unhandled FactsResult subtype")` instead of `Success`. All current result types are still handled, so no behavior change today; a future unhandled type surfaces as a warning rather than masquerading as success. Build clean; 238 bundle/facts tests passed.
- **Problem:** The `result switch` default case returns `Success("no result")`, silently degrading any `FactsResult` subtype not exhaustively matched into a generic success with no data. Currently harmless — all `FactsQuery.*` return types are covered — but a newly added result subtype would hide itself as an empty success rather than failing loud.
- **Recommended fix:** Make the default a warning/failure (or add `#nullable`/`switch` exhaustiveness enforcement) so an unhandled type surfaces instead of masquerading as success.
- **Risk if deferred:** A future `FactsResult` subtype silently returns empty success, masking a formatter gap.

### REV-022 — `ResolveFiles` silently drops misspelled comma-parts
- **Location:** `src/Scrinia/Commands/ScriniaCommands.cs:350–372`; caller `ScriniaCommands.Bundle.cs:149`
- **Layer:** CLI
- **Confidence:** High
- **Effort:** S
- **Status:** fixed (2026-09-28) — Added `CollectUnresolvedPaths` (mirrors `ResolveFiles`'s comma-split/glob/exists classification) and a warning in `Bundle` when some requested segments resolve to zero files. JSON mode writes to stderr (stdout JSON stays clean); non-JSON uses the yellow Warning markup. The empty-list error path is unchanged. Build clean; 238 bundle/facts tests passed.
- **Problem:** `ResolveFiles` drops comma-separated parts that lack `*`/`?` glob chars and fail `File.Exists`. The caller (`Bundle`) only errors when the *entire* list is empty, so `scri bundle topic a.txt,b_typo.txt` bundles `a.txt` and quietly ignores `b_typo.txt`. Data-integrity/UX issue, not security — globbing uses `Directory.GetFiles`, no shell.
- **Recommended fix:** Surface dropped/missing parts as a warning (or error) from `Bundle` when some requested paths resolved to nothing, rather than failing only on an entirely empty list.
- **Risk if deferred:** Users misspell a path and the command succeeds while silently omitting intended files.

### REV-024 — Decompression-bomb guard not exercised end-to-end on import
- **Location:** `src/Scrinia.Core/Bundles/BundleFormatService.cs:642–659` (`ReadZipArtifactBytes` bounds the compressed `entry.Length` to `RestoreJournalCodec.MaxRawNmp2BodyBytes` = 96 MiB); decompressed refusal lives in `Nmp2Strategy.MaxDecodedBytes` (64 MiB) / `DecodeAllChunksVerified`.
- **Layer:** Tests
- **Confidence:** High
- **Effort:** M
- **Status:** fixed (2026-09-27) — REV-012 guards the local-topic: shim with PathContainment.IsWithin (throws ArgumentException on escape); REV-024 added BundleDecompressionBombTests exercising the MaxDecodedBytes guard end-to-end through ImportTopicsFromZip (64 bundle tests + 63 embeddings tests pass).
- **Problem:** The decompressed-size refusal is reached only when a crafted artifact actually flows through the shared decoder during import; the import path bounds only the *compressed* entry size. The budget is unit-tested in pieces, but no test imports a bundle whose artifact decompresses past `MaxDecodedBytes` end-to-end.
- **Recommended fix:** Add an integration test that imports a bundle containing an artifact that decompresses beyond `MaxDecodedBytes` and asserts the import refuses.
- **Risk if deferred:** The decompression-bomb guard is proven at the decoder and at the compressed-size check separately, but not as a whole import — a gap that only a coordinated-crafting test would close.

---

## Tier 4 — Nice-to-have (perf-only / cosmetic)

The five lowest-priority items. Each is a legitimate Tier-3-eligible design/verify discussion that the review additionally flagged as perf-only, so they sit at the bottom of the queue and should only be taken when there is spare budget.

### REV-008 — `DecodeChunkSection` re-scans from the top per chunk (O(k·n))
- **Location:** `src/Scrinia.Core/Encoding/Nmp2Strategy.cs:297–316` (walks `EnumerateLines` from the top to find `##CHUNK:{chunkIndex}`); `Nmp2ChunkedEncoder.DecodeAllChunksVerified` calls it once per chunk (`:350–352`), and `DecodeCore` (`Nmp2Strategy.cs:145`) does the same.
- **Layer:** Encoding
- **Confidence:** High
- **Effort:** M
- **Status:** fixed (2026-09-28) — Rewrote the multi-chunk decode to O(n): moved `ExtractRawChunkSections` into `Nmp2Strategy` (with `from`-tracking so each `IndexOf` starts where the last section ended), added `DecodeChunkSectionFrom(section, maxBytes)` that decodes a pre-extracted slice, and rewrote `DecodeCore` + `DecodeAllChunksVerified` to extract-then-decode once instead of re-walking the artifact per chunk. Single-chunk `DecodeChunkSection` path unchanged. Build clean (0 warnings/errors); full suite 3755 passed / 0 failed / 19 skipped. Fixed in commit 0ed6c1c.
- **Problem:** Each `DecodeChunkSection` call re-walks the whole artifact from the first line to locate its chunk marker, so decoding *k* chunks costs O(k · n) in artifact length. `AppendChunk`/`ExtractRawChunkSections` already do a single indexed pass (`IndexOf` per section). The real bounded inefficiency shows on the many-chunk `ShowAsync` render path.
- **Recommended fix:** Index chunk sections once (e.g. scan the artifact a single time collecting `##CHUNK:` offsets, or reuse the `ExtractRawChunkSections` indexed approach) so `DecodeAllChunksVerified` / `DecodeCore` decode in O(n) total.
- **Risk if deferred:** Multi-chunk renders pay a quadratic-ish cost in artifact length; only matters for large, highly-chunked memories.

### REV-011 — Encode-twice to measure encoded length
- **Location:** `src/Scrinia.Core/Models/MemoryWrite.cs:277`
- **Layer:** Models
- **Confidence:** High
- **Effort:** M
- **Status:** closed as no-change (2026-09-28) — The encoded-length check at `MemoryWrite.cs:277` is a legitimate pre-flight cap on persisted artifact size; the "double encode" is an inherent consequence of `Validate` (a pure static pre-flight) being decoupled from the store's write path, which encodes independently. Reusing the encoded bytes would force `Validate` to retain up to `MaxEncodedChunkBytes` (64 MiB) per chunk for the write lifetime and couple a pure check into a side-effecting one. The encode cost is on a non-hot path. Left as-is.
- **Problem:** To enforce `MaxEncodedChunkBytes`, `MemoryWrite` computes `Encoding.UTF8.GetByteCount(Nmp2ChunkedEncoder.Encode(chunk))` — Brotli-compressing and Base64-encoding every chunk purely to *measure* the encoded length, doubling the real work on every write/append.
- **Recommended fix:** Introduce an incremental upper-bound estimate of the encoded size for the common case so the second compress is skipped when the estimate is safely under the limit; only compress fully when the estimate is borderline.
- **Risk if deferred:** Every write/append compresses each chunk twice — a permanent, visible cost on append-heavy workloads.

### REV-019 — `BuildSummary` double-passes review status
- **Location:** `src/Scrinia.Mcp/MemoryTools.Core.cs:868–871`
- **Layer:** MCP Facade
- **Confidence:** High
- **Effort:** S
- **Problem:** Same as REV-019's Tier-3 framing — `staleCount` and `reviewCount` each run a full `entries.Count(...)` pass and call `GetReviewStatus` per entry (4 invocations/entry). Pure, uncached work doubled.
- **Recommended fix:** Single pass computing both flags from one `GetReviewStatus` call per entry.
- **Risk if deferred:** Wasted CPU on the frequently-called `list --summary` path.

### REV-020 — Conflict scan opens every file under `.scrinia/`
- **Location:** `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:397–409`
- **Layer:** MCP Facade
- **Confidence:** Low
- **Effort:** S
- **Status:** closed as no-change (2026-09-28) — `RecoveryDiscovery.ReadVerified` already uses a bounded 10 KB peek read (`reader.Read(buf, 0, buf.Length)`), not `ReadToEnd`, so the reviewer's O(total bytes) concern was stale. No code change required; the bounded read is intentional.
- **Problem:** Every session-start call (`Guide()` / `Restore()`) opens each file under `.scrinia/` to test for a `<<<<<<<` marker. The read is already byte-bounded (10 KB prefix at `:404–406`), so there is no unbounded I/O — the cost is opening every file once per call. See the confidence note under REV-020 in Tier 3 for the full picture.
- **Recommended fix:** No code change required if the bounded read is intentional; if the 10 KB cutoff is a concern, raise it or note the trade-off in a comment.
- **Risk if deferred:** Negligible — the bounded read already avoids the O(total bytes) cost the reviewer originally worried about.

### REV-021 — `FactsResponseFormatter` default degrades to generic success
- **Location:** `src/Scrinia.Mcp/FactsResponseFormatter.cs:22`
- **Layer:** MCP Facade
- **Confidence:** High
- **Effort:** S
- **Problem:** Same as REV-021's Tier-4/cosmetic framing — the `default` case returns `Success("no result")`, which is harmless today but would hide a future result subtype as empty success rather than failing loud. Purely a robustness/cosmetic nicety.
- **Recommended fix:** When there is budget, make the default a failure/warning so an unhandled type surfaces.
- **Risk if deferred:** None today; a future result type would silently return empty.

---

## Resolution ledger

The two **Major** findings from the 2026-09-27 review were remediated in a draft before this backlog was compiled. They are not part of the 25 Minor items above; they are recorded here for completeness.

| ID | Finding | Resolution | Resolved-by | Date | Notes |
|----|---------|-----------|-------------|------|-------|
| MAJ-1 | FileLock cross-process enforcement off Windows | fixed (draft, uncommitted) | review workstream | 2026-09-27 | Pre-draft `FileLock` relied on Windows-only `FileShare` exclusion. The draft adds `flock(2)`-based cross-process locking (exclusive `LOCK_EX` / shared `LOCK_SH` with `EAGAIN` backoff) for non-Windows, matching the summary at `FileLock.cs:8–19`. Commit once the draft lands. |
| MAJ-2 | Same-process cache-read race in `FileMemoryStore` | fixed (draft, uncommitted) | review workstream | 2026-09-27 | A cache read could observe a partially-updated in-memory index within one process. Draft fixes the read/write ordering around the cache. Verify against the running draft before marking committed. |
| REV-001 | `IsJson` dedup | fixed | coder agent (Tier-1 batch) | 2026-09-27 | The three `IsJson` copies were DEAD CODE (no call sites in `src/` or `tests/`); dedup removed the three unused private copies and added one canonical `RecoveryEnvelopeCodec.IsJson` for future use. Implemented + built clean + 106 affected tests passed. |
| REV-004 | `RecoveryEvidenceKind.AppendJournal` label | fixed | coder agent (Tier-1 batch) | 2026-09-27 | Labeled the live-member path correctly; still routes as `RecoveryOperation.Append` via `RecoveryEnvelopeCodec`. Implemented + built clean + 106 affected tests passed. |
| REV-006 | `DurableFile` summary vs `SyncDirectory` | fixed | coder agent (Tier-1 batch) | 2026-09-27 | Corrected the class summary to state both the file and its containing directory are fsynced. Implemented + built clean + 106 affected tests passed. |
| REV-009 | `IsCanonicalName` rule triplicated | fixed | coder agent (Tier-1 batch) | 2026-09-27 | Routed all three copies through one canonical rule (single source of truth). Implemented + built clean + 106 affected tests passed. |
| REV-010 | `SamePhysical` normalization split | fixed | coder agent (Tier-1 batch) | 2026-09-27 | Shared one implementation so both callers normalize identically. Implemented + built clean + 106 affected tests passed. |
| REV-018 | `action`/`reason` unescaped in YAML | fixed | coder agent (Tier-1 batch) | 2026-09-27 | Wrapped both in `YamlEscape(...)` for escaping consistency. Implemented + built clean + 106 affected tests passed. |
| REV-025 | `BundleZipSlipTests` mislabeled scope | fixed | coder agent (Tier-1 batch) | 2026-09-27 | Renamed the test and updated its summary/header so it documents that it exercises the allowlist gate, not real path traversal. Implemented + built clean + 106 affected tests passed. |
| REV-002 | `MapProtection` reason→protection mapping dedup | fixed | coder agent (Tier-2 batch) | 2026-09-27 | Consolidated the two `RecoveryBlockReason` → `MaintenanceProtectionReason` catch-alls to one shared mapping so B2 and B3 agree. Implemented + built clean + 551 affected tests passed. |
| REV-007 | Negative `##PAD` footer upper bound | fixed | coder agent (Tier-2 batch) | 2026-09-27 | Added the pad/`compressedLen` upper-bound validation; negative pad footer now throws InvalidDataException (was ArgumentOutOfRangeException). BundleRoundTripTotalityTests still passes because it asserts import tolerance, not exception type. Implemented + built clean + 551 affected tests passed. |
| REV-013 | `SafeTensors` header validation | fixed | coder agent (Tier-2 batch) | 2026-09-27 | Guarded the header parse: `dtype` must be a string and `shape`/`data_offsets` integer arrays, so a hostile `.safetensors` reports "model unavailable" instead of an uncaught NRE/`JsonException`. Implemented + built clean + 551 affected tests passed. |
| REV-014 | explicit `IsSecret` key set | fixed | coder agent (Tier-2 batch) | 2026-09-27 | Made the secret surface explicit (schema over convention) so a non-`ApiKey` secret key can no longer travel on argv. Implemented + built clean + 551 affected tests passed. |
| REV-017 | `YamlEscape` control-byte escaping | fixed | coder agent (Tier-2 batch) | 2026-09-27 | `YamlEscape` now escapes every control byte as `\uXXXX` (mirroring `SharedEnvelopeYaml.Scalar`), so a control byte in an error/instruction string can no longer corrupt the MCP YAML. Implemented + built clean + 551 affected tests passed. |
| REV-023 | GPU provider dispose on init failure | fixed | coder agent (Tier-2 batch) | 2026-09-27 | Provider-creation and post-creation steps now each reset to NullEmbeddingProvider on failure; the post-creation region disposes a successfully-created provider before resetting, closing the llama GPU-context leak. Implemented + built clean + 551 affected tests passed. |
| REV-012 | `local-topic:` recovery shim path-traversal guard | fixed | coder agent (security-hardening batch) | 2026-09-27 | guarded the local-topic: recovery-compat shim (legacy journals before 2027-03-21 sunset) with PathContainment.IsWithin; local: branch left unguarded by design (validated upstream). |
| REV-024 | Bundle-import decompression-bomb end-to-end test | fixed | coder agent (security-hardening batch) | 2026-09-27 | serial-collection test lowers Nmp2Strategy.MaxDecodedBytes to 8 KiB around a real import of a highly-compressible 256 KiB payload; asserts import completes and the bomb entry is tolerated (Keywords null), proving the guard is reached end-to-end, not just unit-tested in pieces. |
| REV-001 … REV-025 | Minor findings (this backlog) | partially resolved (23/25) | — | 2026-09-28 | Tier 1 batch (REV-001, REV-004, REV-006, REV-009, REV-010, REV-018, REV-025) fixed — see rows above; build clean, 106 affected tests passed. Tier 2 batch (REV-002, REV-007, REV-013, REV-014, REV-017, REV-023) fixed — see rows above; build clean, 551 affected tests passed. REV-015 (dead `Search` scorer retirement) is now fixed in commit e5e26c2. The Tier-4 batch (REV-016, REV-019) is now fixed in commit d2c32fac. Remaining: Tier 3 needs an author decision + test; Tier 4 is perf/cosmetic and optional. REV-003 (async recovery conductor converted to async) and REV-005 (drop dead `publicationWasVisible`, renamed to `currentMembers`) fixed in commit e564fc91 (239 append/recovery tests pass); REV-021 (fail-loud `FactsResult` switch default) and REV-022 (bundle warns on unresolved path segments) fixed in the same commit (238 bundle/facts tests pass). REV-011 (encode-twice pre-flight cap) and REV-020 (bounded conflict read) closed as no-change. REV-008 (O(k·n) multi-chunk decode) fixed in commit 0ed6c1c — 3755 tests pass (0 failed, 19 skipped). Only REV-011 and REV-020 remain open. |

---

## Quick wins this sprint

The five lowest-effort / lowest-risk items — all Tier 1, all behavior-preserving. Good for a focused cleanup pass:

1. **REV-006** — Fix the `DurableFile` class summary (`:9–11`) so it matches `SyncDirectory` (`:97`). One comment edit.
2. **REV-009** — Route the three `IsCanonicalName` copies through one shared rule. Small refactor, no behavior change.
3. **REV-001** — Collapse the three identical `IsJson` methods into one helper.
4. **REV-018** — Run `action`/`reason` through `YamlEscape` in `McpResponse.cs` for escaping consistency.
5. **REV-025** — Correct the `BundleZipSlipTests` name/docs to reflect that it exercises the allowlist gate, not real path traversal.

After these, the Tier 2 hardening batch (REV-002, REV-007, REV-013, REV-014, REV-017, REV-023) is the next logical step — each closes a concrete gap with no open design question.
