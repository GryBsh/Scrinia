# Data integrity hardening — design

**Date:** 2026-07-30
**Status:** Approved (design); implementation plan pending
**Stream:** 2 of 7 from the 2026-07-29 whole-codebase review remediation

## Context

Stream 2 addresses the four confirmed **data-integrity** findings from the 2026-07-29
review, plus the three items deferred during Stream 1. Directive for this stream:
**defer nothing** — any finding surfaced by the final whole-branch review is fixed
in-branch, not parked.

All findings were independently re-verified against the source during Stream 1's
verification pass and re-confirmed here by reading the current code.

## Group A — data integrity

### A1. MetaJsonMerger destroys `.meta.json` metadata (Critical)

**File:** `src/Scrinia.Merge/MetaJsonMerger.cs`

**Root cause:** real sidecars are serialized **camelCase** and carry fields the merger's
`MetaEntry` record does not model (`contentPreview`, `termFrequencies`, `chunkEntries`,
`codeRefs`, `attributes`). But `MetaEntryJsonContext` declares no options → default
**PascalCase, case-sensitive**. A real camelCase sidecar therefore deserializes to an
all-null/zero `MetaEntry`; `KeywordJaccard(null, null)` returns 1.0 (see A2), the merge
"resolves," and `File.WriteAllText(oursPath, Serialize(merged))` rewrites the file as
PascalCase nulls — **dropping every field**, including the five it never modeled.

**Fix — rewrite on the JSON DOM (`System.Text.Json.Nodes`):**
- Parse ours/theirs/ancestor as `JsonObject` (camelCase-correct via case-insensitive
  property lookup; `JsonNode`/`JsonObject` are trim/AOT-safe and need no type resolver —
  so **no `Scrinia.Core` dependency**, preserving the standalone-AOT `scri-merge` binary,
  the same constraint that governed Stream 1's A3).
- Read `keywords` for the Jaccard gate; run the existing 3-way scalar merge
  (`name`/`uri`/`description`/`originalBytes`/`chunkCount`) reading node values; union
  keywords; pick `updatedAt`/`createdAt` as today.
- On `Resolved`, **start from ours' full `JsonObject` and overlay only the merged
  fields**, then write it back — preserving `contentPreview`/`termFrequencies`/
  `chunkEntries`/`codeRefs`/`attributes` and any other unknown property verbatim.
- Remove the now-unused `MetaEntry` record and `MetaEntryJsonContext`.

**Rejected alternative:** adding camelCase + case-insensitive options to the existing
typed context fixes *reading* but the *write* still reserializes only the 8 modeled
fields — it would still drop the 5 unknown ones. Pulling `Scrinia.Core` for `ArtifactEntry`
is rejected for the same reason A3 rejected it (AOT/trim binary, no Core reference) and
because unknown properties would still be lost.

**Tests:** the existing `MetaJsonMergerTests` use PascalCase fixtures **on purpose** (they
only pass because of the bug) — rewrite them to camelCase. Add the load-bearing regression:
a real camelCase sidecar carrying `contentPreview`/`termFrequencies`/`chunkEntries`/
`attributes` on both sides with similar keywords → after `Merge`, the result file **still
contains those fields** and keywords are the union; plus a dissimilar-keywords case →
`Conflict`, file untouched.

### A2. KeywordJaccard treats null-vs-populated as identical

**File:** `src/Scrinia.Merge/KeywordJaccard.cs`

**Now:** `if (a is null || b is null || (a.Length == 0 && b.Length == 0)) return 1.0;` —
the `||` short-circuits so `Compute(null, ["x"])` returns 1.0 (falsely "identical"),
which is what lets A1's null-read clear the Jaccard gate.

**Fix:** both empty (null or length 0) → 1.0; exactly one empty → **0.0**; otherwise the
Jaccard ratio (unchanged).

### A3. SaveIndexUnsafe leaves stale sidecars that reappear after restart

**File:** `src/Scrinia.Core/FileMemoryStore.cs` (`SaveIndexUnsafe`, ~L1033)

**Now:** `SaveIndexUnsafe` calls `WriteSidecars(entries, storeDir)` (one sidecar per
entry) and updates the cache, but never deletes `*.meta.json` for names no longer in
`entries`. `LoadEntriesFromSidecars` reads **every** `*.meta.json`, so on a cold process an
orphaned sidecar resurrects the entry. Latent today (the sole production caller only
replaces/adds), but the API permits shrinking.

**Fix:** in `SaveIndexUnsafe`, before writing, compute the expected sidecar filename set
(`SanitizeName(e.Name) + ".meta.json"`) from `entries`, enumerate the existing
`*.meta.json` (materialized to a list to delete safely during iteration), and delete any
not in the expected set. Runs under the caller's write lock; the scope's `storeDir` holds
only artifact sidecars, so deletion is safe and scope-local.

### A4. Nmp2 decode does not verify CRC32 or original byte count

**File:** `src/Scrinia.Core/Encoding/Nmp2Strategy.cs` (`Decode`, ~L123)

**Now:** `Encode` writes `NMP/2 {N}B CRC32:{hex}` (CRC over original bytes,
`Crc32.HashToUInt32`, `System.IO.Hashing`). `Decode` never verifies output length or CRC —
`ParseHeader` (which extracts both) is never called from `Decode`. Corrupt-but-
decompressible or boundary-truncated data is silently accepted.

**Fix:** after decoding (both single- and multi-chunk paths), verify
`decoded.Length == ParseHeader(artifact).OriginalBytes` and, when the header declares a
CRC (`ParseHeader(...).Crc32 is not null`), `Crc32.HashToUInt32(decoded) == crc`; throw
`InvalidDataException` on mismatch. The implementer confirms the multi-chunk header's CRC
semantics; length is verifiable in all cases.

## Group B — Stream 1 deferred items (folded in)

### B1. PathContainment residuals

**File:** `src/Scrinia.Core/PathContainment.cs`

- **Dead branch:** in `ResolveReal`, `ResolveLinkTarget(returnFinalTarget: true)?.FullName`
  is always absolute, so `Path.IsPathRooted(target) ? target : Path.GetFullPath(Combine(...))`
  never takes the `else` — simplify to `resolved = target;` with a clarifying comment.
- **Fail closed on IOException:** a symlink chain exceeding the OS link-resolution limit
  currently surfaces an unhandled `IOException` from `ResolveLinkTarget`. Wrap the two
  `ResolveReal` calls in `IsWithin` in a `try`/`catch (IOException)` returning `false`
  (containment cannot be established → deny), keeping the fail-closed direction explicit.

### B2. RequestContext.CanAccessStore duplicated wildcard check

**File:** `src/Scrinia.Server/Auth/RequestContext.cs`

`CanAccessStore` inlines its own `Stores.Contains("*", ...)` check, duplicating the
`IsGlobalAdmin` property added in Stream 1's B2. Delegate:
`CanAccessStore(storeName) => IsGlobalAdmin || Stores.Contains(storeName, OrdinalIgnoreCase)`.
Behavior identical; single source of truth for the wildcard.

## Testing strategy

Each fix ships a regression test (see per-finding notes). Full suites green via
`-p:NuGetAudit=false`:
- Core/MCP: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
- Merge: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false`
- Server: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false`

## Non-goals (other streams)

Streams 3 (supply chain / CI-CD), 4 (retrieval correctness), 5 (secrets & transport),
6 (web), 7 (docs) remain their own specs. Stream 1 (containment & authz) is merged.

## Risks

- **A1** is a git **merge driver** rewrite — highest stakes. The preserve-all-fields
  regression test is the safety net; the DOM approach makes the merge lossless even for
  fields the driver does not model.
- **A3** sidecar deletion is bounded to the scope's own `*.meta.json`, under the write
  lock — no cross-scope risk.
- **A4** must not reject legitimate legacy artifacts — verify only against what the header
  actually declares (length always; CRC only when present).
