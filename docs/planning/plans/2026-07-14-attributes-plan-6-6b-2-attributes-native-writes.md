# P6.6b Merge 2: Attributes-native write paths

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Attributes`/`ContentPreview` authoritative **at write time** by pre-folding the incoming tool/request params (tags, review, anchors, file, description) into them **before** entry construction — instead of relying on `AttributeMigration.Forward` to fold post-construction — so that when Merge 3 deletes the legacy members and Forward's carrier-folds, nothing is lost. Legacy members stay set (dual-write) this merge.

**Architecture:** Purely additive. Extract one `AttributeMigration.BuildLegacyCarrierFields(...)` helper (the single source of truth for carrier→attribute folding); `Forward` delegates to it, and the persistent store/append write sites call it to compute the attributes dict before construction. `Forward` still runs on the write paths (idempotent, byte-identical re-fold — scalar keys replace with the same value, `file` unions an identical set), and stays intact for the read-time forward-fill hook + `scri migrate`. The `hint`/update handler additionally rebuilds `Attributes["review"]` + sets `ContentPreview` directly (dual-write). One direct danger-read (`Consolidate` staleness) routes through `AttributeReaders.GetReviewStatus`.

**Tech Stack:** .NET 10, C# records, System.Text.Json source-gen, xUnit + FluentAssertions.

**Design source:** `scratchpad/merge2-writepath-design.md` (full site-by-site inventory with verified current line numbers).

## Global Constraints

- **TreatWarningsAsErrors on all product projects.** Zero warnings. Ignore IDE-only stale CS8019/CS8933 and any phantom `.Fields`/`.Attributes` LSP diagnostics — trust `dotnet build`/`dotnet test` output ONLY (the LSP mis-reports against cached files in this workspace).
- **`dotnet build Scrinia.sln` fails on a PRE-EXISTING unrelated `Scrinia.AppHost` NuGet-audit restore error.** Build/test with `-p:NuGetAudit=false` (a build flag — edit no file for it). `src/**` and `tests/**` are 0/0.
- **Do NOT touch:** `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, `.superpowers/**`, `.git/**`.
- **Commit trailer — exact, both lines, every commit:**
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```
- **Never push.** **Staging: NEVER `git add -A`.** Use `git add -- src tests ':!src/Scrinia.Mcp/plugins'`, then `git status` and confirm no `benchmarks/`, `plugins/`, `.gitignore`, or stray untracked docs staged.
- **ADDITIVE only.** Do NOT remove any member, do NOT remove Forward's folds, do NOT change scoring. Legacy members stay dual-written.
- **Byte-parity is the invariant.** The pre-fold must reproduce `Forward`'s output exactly: key names `tag`/`review`/`anchor`/`file`; review array = `[ReviewAfter.ToString("o", CultureInfo.InvariantCulture)]` then `[ReviewWhen]` when non-blank, in that order; anchors whitespace-dropped + `OrdinalIgnoreCase`-deduped; `file` = `FileHashes.Keys`. Any divergence in format/order breaks `AttributeReaders`' positional review parse and would change stale/review counts.
- **Scoring-neutral → NO ranking-measurement gate.** (Same persisted Attributes/ContentPreview/Keywords, computed earlier.)

---

### Task 1: Extract `BuildLegacyCarrierFields` helper; `Forward` delegates to it

**Files:**
- Modify: `src/Scrinia.Core/Migration/AttributeMigration.cs`
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs` (extend)

**Interfaces:**
- Produces: `internal static Dictionary<string,string[]> AttributeMigration.BuildLegacyCarrierFields(string[]? tags, IEnumerable<string>? fileKeys, DateTimeOffset? reviewAfter, string? reviewWhen, string[]? anchors)` — returns the `incoming` dict (keys among `tag`/`file`/`review`/`anchor`, only for non-empty inputs) that the write sites merge over their attributes dict. Does NOT include the `key:value` Keywords fold or the Description→ContentPreview fold (those stay inline in `Forward`).
- Consumes: nothing new.

**Context:** `Forward` (AttributeMigration.cs ~34-111) currently builds the `incoming` dict inline. Extracting the tag/file/review/anchor portion into a helper lets the write sites reproduce it byte-for-byte. The `key:value`-Keywords fold and the ContentPreview backfill stay in `Forward` (they're not carrier params the write sites pass).

- [ ] **Step 1: Write the failing parity test**

In `AttributeMigrationTests.cs`, add a fact asserting the helper reproduces the tag/file/review/anchor portion of what `Forward` produces. Construct an entry with `Tags`, `FileHashes`, `ReviewAfter`+`ReviewWhen`, `RetrievalAnchors` set; call `AttributeMigration.BuildLegacyCarrierFields(entry.Tags, entry.FileHashes?.Keys, entry.ReviewAfter, entry.ReviewWhen, entry.RetrievalAnchors)`; assert the returned dict has `tag`/`file`/`review`/`anchor` with the exact values Forward would fold (review = `[ra.ToString("o", CultureInfo.InvariantCulture), reviewWhen]`; anchor whitespace-dropped+deduped; file = FileHashes.Keys). Also assert `Forward(entry).Attributes` contains those same keys/values (proving Forward still delegates correctly).

- [ ] **Step 2: Run it — fails to compile** (`BuildLegacyCarrierFields` doesn't exist).
Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter AttributeMigrationTests`

- [ ] **Step 3: Implement the helper + refactor `Forward` to delegate**

Add to `AttributeMigration`:
```csharp
/// <summary>The single source of truth for folding legacy carriers (tags/file-hashes/review/anchors)
/// into attribute keys. Both <see cref="Forward"/> and the attributes-native write paths call this so
/// the write-time fold is byte-identical to the read-time fold. Returns only keys with non-empty input.
/// Excludes the key:value-Keywords fold and the Description→ContentPreview backfill (Forward-only).</summary>
internal static Dictionary<string, string[]> BuildLegacyCarrierFields(
    string[]? tags, IEnumerable<string>? fileKeys,
    DateTimeOffset? reviewAfter, string? reviewWhen, string[]? anchors)
{
    var incoming = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    if (tags is { Length: > 0 })
        incoming[TagKey] = tags;
    if (fileKeys is not null)
    {
        var keys = fileKeys.ToArray();
        if (keys.Length > 0) incoming["file"] = keys;
    }
    var review = new List<string>(2);
    if (reviewAfter is { } ra)
        review.Add(ra.ToString("o", CultureInfo.InvariantCulture));
    if (!string.IsNullOrWhiteSpace(reviewWhen))
        review.Add(reviewWhen!);
    if (review.Count > 0)
        incoming[KnownFields.ReviewKey] = review.ToArray();
    if (anchors is { Length: > 0 })
    {
        var cleanAnchors = anchors
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (cleanAnchors.Length > 0) incoming[KnownFields.AnchorKey] = cleanAnchors;
    }
    return incoming;
}
```
Then in `Forward`, REPLACE the inline tag/file/review/anchor blocks with:
```csharp
var incoming = BuildLegacyCarrierFields(entry.Tags, entry.FileHashes?.Keys, entry.ReviewAfter, entry.ReviewWhen, entry.RetrievalAnchors);
```
Keep the `key:value`-Keywords fold appending into `incoming` (unchanged), and the `KnownFields.Merge(entry.Attributes, incoming)` + ContentPreview backfill (unchanged). Verify `CultureInfo` is imported (`using System.Globalization;` already present).

- [ ] **Step 4: Run the full suite** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`. Expected: PASS, all existing `AttributeMigrationTests`/`MigrationTests` green (Forward behavior byte-identical) + the new parity test.

- [ ] **Step 5: Build product + commit**
```bash
dotnet build src/Scrinia/Scrinia.csproj -p:NuGetAudit=false   # 0/0
git add -- src tests ':!src/Scrinia.Mcp/plugins'
git status
git commit -m "refactor(core): extract AttributeMigration.BuildLegacyCarrierFields; Forward delegates"
```

---

### Task 2: Pre-fold the two `MemoryOrchestrator` persistent write sites

**Files:**
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs`
- Test: `tests/Scrinia.Server.Tests/MemoryOrchestratorFieldsTests.cs` (verify/extend)

**Interfaces:**
- Consumes: `AttributeMigration.BuildLegacyCarrierFields` (Task 1).

**Context (verified lines — re-locate by the `new ArtifactEntry(...)` calls, lines drift):**
- **Store persistent:** `new ArtifactEntry(...)` at ~125-140, `Attributes: fields` (~140), `Forward` at ~144. StoreRequest carries `Tags`, `ReviewAfter`, `ReviewWhen` (no anchors/codeRefs). Pre-fold needed: `tag`←`req.Tags`, `review`←`parsedReviewAfter`/`req.ReviewWhen`.
- **Append persistent:** `new ArtifactEntry(...)` at ~262-277, `Attributes: mergedFields` (~277), `Forward` at ~283. Carries review from `existingEntry`. Pre-fold needed: `review`←`existingEntry?.ReviewAfter`/`existingEntry?.ReviewWhen` (Tags null → no tag).

- [ ] **Step 1:** Confirm/extend `MemoryOrchestratorFieldsTests.cs` asserts the persistent Store path populates `Attributes["tag"]` and `Attributes["review"]` at write time (raw sidecar read, cold-cache contract). If an assertion for `review`-at-write is missing, add one: Store with `ReviewAfter`+`ReviewWhen` set, read the raw persisted entry, assert `Attributes["review"]` = `[date "o", condition]`. Run to see current state.

- [ ] **Step 2:** In the **Store persistent** path, immediately BEFORE `new ArtifactEntry(...)`, merge the carrier fold into `fields`:
```csharp
var carrier = AttributeMigration.BuildLegacyCarrierFields(req.Tags, fileKeys: null, parsedReviewAfter, req.ReviewWhen, anchors: null);
fields = carrier.Count > 0 ? KnownFields.Merge(fields, carrier) : fields;
```
(Use the actual local names — `fields`, `parsedReviewAfter`, `req.Tags`, `req.ReviewWhen`.) Leave `Attributes: fields` and the `Forward` call untouched.

- [ ] **Step 3:** In the **Append persistent** path, BEFORE `new ArtifactEntry(...)`, merge review into `mergedFields`:
```csharp
var carrier = AttributeMigration.BuildLegacyCarrierFields(tags: null, fileKeys: null, existingEntry?.ReviewAfter, existingEntry?.ReviewWhen, anchors: null);
mergedFields = carrier.Count > 0 ? KnownFields.Merge(mergedFields, carrier) : mergedFields;
```
Leave `Attributes: mergedFields` and `Forward` untouched. Add `using Scrinia.Core.Migration;` if not present.

- [ ] **Step 4:** Run `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false` and `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`. Expected: green (additive — `Forward` re-folds idempotently, so pre-existing assertions hold).

- [ ] **Step 5:** Build Server + commit
```bash
dotnet build src/Scrinia.Server/Scrinia.Server.csproj -p:NuGetAudit=false   # 0/0
git add -- src tests ':!src/Scrinia.Mcp/plugins'
git status
git commit -m "feat(server): pre-fold tag/review into Attributes at write time (MemoryOrchestrator store+append)"
```

---

### Task 3: Pre-fold the two `ScriniaMcpTools` persistent write sites

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs`
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` (verify/extend), `tests/Scrinia.Tests/AutoReviewWhenTests.cs` (must stay green)

**Interfaces:**
- Consumes: `AttributeMigration.BuildLegacyCarrierFields` (Task 1).

**Context (verified lines — re-locate by the calls):**
- **Store persistent:** `new ArtifactEntry(...)` at ~371-388, `Attributes: fields` (~388), `Forward` at ~389. Full param set: `tags`, `cleanedAnchors`, `parsedReviewAfter`/`reviewWhen`, `codeRefDict`. Pre-fold needed: `tag`←`tags`, `anchor`←`cleanedAnchors`, `review`←`parsedReviewAfter`/`reviewWhen`, `file`←`codeRefDict.Keys`. **ORDERING: the pre-fold must run AFTER `codeRefDict` is fully built (~369) AND after the review auto-derivation / back-parse (~321-346)** so it captures the auto-`reviewWhen` (CountPattern) and back-parsed review — otherwise `AutoReviewWhenTests` and the review back-parse diverge.
- **Append persistent:** `new ArtifactEntry(...)` at ~1252-1269, `Attributes: meta.Fields` (~1269), `Forward` at ~1270. `meta` from `BuildAppendMetadata` (~1233), which ALREADY folds tag+anchor (~1356-1364) but NOT review/file. Add review←`existingEntry?.ReviewAfter`/`existingEntry?.ReviewWhen` and file←`existingEntry?.FileHashes?.Keys` into the `BuildAppendMetadata` fold (or immediately after `meta` is built, before construction).

- [ ] **Step 1:** In `ScriniaMcpToolsTests.cs`, confirm there are assertions that the persistent Store populates `Attributes["tag"/"anchor"/"review"/"file"]` at write (raw entry). If `review`/`file`-at-write coverage is thin, add a Store test with tags+anchors+reviewAfter+reviewWhen+codeRefs → assert all four keys present in the raw persisted `Attributes` with correct values/format. Run to see baseline.

- [ ] **Step 2:** In the **Store persistent** path, immediately before `new ArtifactEntry(...)` (after `codeRefDict` and review derivation are complete), merge:
```csharp
var carrier = AttributeMigration.BuildLegacyCarrierFields(tags, codeRefDict?.Keys, parsedReviewAfter, reviewWhen, cleanedAnchors);
fields = carrier.Count > 0 ? KnownFields.Merge(fields, carrier) : fields;
```
(Use actual local names.) Leave `Attributes: fields` and `Forward` untouched.

- [ ] **Step 3:** For the **Append persistent** path, extend `BuildAppendMetadata`'s existing tag+anchor fold (~1356-1364) to also fold review + file, using the shared helper for parity. Where the method has the carried `ReviewAfter`/`ReviewWhen`/`FileHashes` available, compute:
```csharp
var carrier = AttributeMigration.BuildLegacyCarrierFields(<tags>, <fileHashes?.Keys>, <reviewAfter>, <reviewWhen>, <anchors>);
mergedFields = carrier.Count > 0 ? KnownFields.Merge(mergedFields, carrier) : mergedFields;
```
Replace the hand-rolled `tagAnchorIncoming` block with the helper call if it produces identical output (verify tag+anchor parity), OR merge the review/file additions alongside it. Keep `meta.Fields`/`Attributes` and `Forward` untouched. (Read `BuildAppendMetadata` fully to wire the carried values correctly.)

- [ ] **Step 4:** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`. Expected green — especially `AutoReviewWhenTests` (auto-`reviewWhen` still flows because the pre-fold runs after its derivation) and `ScriniaMcpToolsTests` store/append. If `AutoReviewWhenTests` fails, the pre-fold is ordered BEFORE the auto-derivation — move it after.

- [ ] **Step 5:** Build + commit
```bash
dotnet build src/Scrinia/Scrinia.csproj -p:NuGetAudit=false   # 0/0
git add -- src tests ':!src/Scrinia.Mcp/plugins'
git status
git commit -m "feat(mcp): pre-fold tag/anchor/review/file into Attributes at write time (Store + Append)"
```

---

### Task 4: `UpdateMeta` (`hint`) — rebuild `Attributes["review"]` + set `ContentPreview` (dual-write)

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`UpdateMeta`, ~403-542)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` (add UpdateMeta review-state test)

**Interfaces:**
- Consumes: `KnownFields.Merge`, `KnownFields.ReviewKey`.

**Context (the trickiest site):** `UpdateMeta` computes `updatedDescription` (~457-462), `parsedReviewAfter` (~465-477), `updatedReviewWhen` (three-state: null=preserve, non-blank=set, empty=clear; ~480-485), and `mergedFields`, then `with { Description=updatedDescription, ReviewAfter=parsedReviewAfter, ReviewWhen=updatedReviewWhen, Attributes=mergedFields, … }` (~524-533) + `Forward` (~534). Today only Forward makes Attributes/ContentPreview correct. Merge 2 must ALSO write `Attributes["review"]` + `ContentPreview` directly so Merge 3 can drop the `Description=/ReviewAfter=/ReviewWhen=` clauses. `review` is one SCALAR array holding both date and condition — rebuild the whole array from the already-merged `parsedReviewAfter`+`updatedReviewWhen`.

- [ ] **Step 1: Write the failing UpdateMeta review-state test**

Add a test asserting the persisted `Attributes["review"]` after `UpdateMeta` across states (read the raw entry, not just member): (i) provide `reviewAfter`+`reviewWhen` → `review` = `[date "o", condition]`; (ii) `reviewWhen` = "" (clear) with an existing reviewAfter → `review` = `[date]` only; (iii) both absent on an entry that had review → review preserved (idempotent). Also assert `ContentPreview` updates when `description` is provided. Use the cold-cache/raw-read contract.

- [ ] **Step 2: Run — fails** (Attributes["review"] only correct via Forward's member-fold today, which is what Merge 3 removes; the test pins the direct write). Actually it may pass via Forward today — if so, ALSO add an assertion form that will fail once members are removed, OR frame the test to assert the direct-write path is exercised. If it passes pre-change, note that and proceed (the value is regression-locking the direct write).

- [ ] **Step 3: Implement the direct dual-write.** After `mergedFields`/`parsedReviewAfter`/`updatedReviewWhen`/`updatedDescription` are computed and BEFORE the `with`:
```csharp
var reviewParts = new List<string>(2);
if (parsedReviewAfter is { } ra) reviewParts.Add(ra.ToString("o", CultureInfo.InvariantCulture));
if (!string.IsNullOrWhiteSpace(updatedReviewWhen)) reviewParts.Add(updatedReviewWhen);
if (reviewParts.Count > 0)
    mergedFields = KnownFields.Merge(mergedFields, new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        { [KnownFields.ReviewKey] = reviewParts.ToArray() });
else
    mergedFields?.Remove(KnownFields.ReviewKey);   // both cleared → drop the facet
```
Then in the `with`, add `ContentPreview = ...` reflecting `updatedDescription` — but ONLY change ContentPreview when the `description` param was actually provided (preserve today's behavior where Forward backfills ContentPreview only when null; do NOT overwrite an existing ContentPreview on an unrelated update). Concretely: gate `ContentPreview = updatedDescription` on `description` being non-blank; otherwise leave the entry's existing `ContentPreview`. Keep the `Description=/ReviewAfter=/ReviewWhen=` member clauses (dual-write) and the `Forward` call. Ensure `CultureInfo` is imported.

- [ ] **Step 4:** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter ScriniaMcpToolsTests` then the full suite. Expected green incl. the new review-state test.

- [ ] **Step 5:** Build + commit
```bash
dotnet build src/Scrinia/Scrinia.csproj -p:NuGetAudit=false
git add -- src tests ':!src/Scrinia.Mcp/plugins'
git status
git commit -m "feat(mcp): UpdateMeta writes Attributes[review]+ContentPreview directly (dual-write)"
```

---

### Task 5: Cut over the last direct danger-read — `Consolidate` staleness

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Migration.cs` (~line 385)
- Test: `tests/Scrinia.Tests/` — add a Consolidate stale-count test driven only via `Attributes["review"]` (optional but recommended)

**Interfaces:**
- Consumes: `AttributeReaders.GetReviewStatus` (`src/Scrinia.Core/Search/AttributeReaders.cs`).

**Context:** The Consolidate staleness count reads `.ReviewAfter` directly — the last product read of a soon-to-be-removed member not routed through `AttributeReaders`. Route it through `GetReviewStatus` (matching `MemoryTools.Core.cs:578,719` and `ScriniaCommands.Memory.cs`).

- [ ] **Step 1:** Locate the current line (design says ~385):
```csharp
int staleCount = allEntries.Count(e => e.Entry.ReviewAfter.HasValue && e.Entry.ReviewAfter.Value <= DateTimeOffset.UtcNow);
```

- [ ] **Step 2 (optional test):** Add a test that a memory whose review lives ONLY in `Attributes["review"]` (no legacy `ReviewAfter` member set) is counted stale by Consolidate — pins the cutover.

- [ ] **Step 3:** Replace with:
```csharp
var now = DateTimeOffset.UtcNow;
int staleCount = allEntries.Count(e =>
    AttributeReaders.GetReviewStatus(e.Entry.Attributes, e.Entry.ReviewAfter, e.Entry.ReviewWhen, now).IsStale);
```
Add `using Scrinia.Core.Search;` if absent. `IsStale` == `date.HasValue && date <= now` — identical predicate, now attributes-first.

- [ ] **Step 4:** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`. Expected green.

- [ ] **Step 5:** Build CLI + commit
```bash
dotnet build src/Scrinia/Scrinia.csproj -p:NuGetAudit=false
git add -- src tests ':!src/Scrinia.Mcp/plugins'
git status
git commit -m "refactor(cli): route Consolidate staleness through AttributeReaders.GetReviewStatus"
```

---

## Post-merge validation (user action, before Merge 3)

Rebuild `scri` (`publish.ps1`) and run normal operations — `remember`, `append`, `hint`, `consolidate` — plus `scri list --json`; confirm behavior is unchanged (this merge is behavior-preserving; Attributes just gets populated at write instead of via Forward's post-fold). No migrate re-run is required (Forward still runs; persisted bytes are identical), but a `migrate --dry-run` reporting 0 confirms no drift.

## Self-Review notes (author)
- **Coverage:** all 4 persistent write sites (A/C/E/G) + UpdateMeta + Migration.cs danger-read; ephemeral sites (B/D/F/H) already conformant (design §1). Helper guarantees byte-parity with Forward (§4).
- **The two risk points** flagged in the design: (1) Task 3 ordering — pre-fold AFTER `codeRefDict` build + review auto-derivation (AutoReviewWhenTests is the guard); (2) Task 4 review-array rebuild across provided/cleared/untouched states (the new UpdateMeta test is the guard).
- **Additive/idempotent:** Forward still runs everywhere (decision a); scalar keys replace with identical values; no member removed; no scoring change → no measurement gate.
