# Attributes P6.5 — Scorer Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the legacy scoring blocks from `WeightedFieldScorer` now that `Fields` is populated everywhere — landing the deferred, spec-accepted uniform scoring change (tag 50/15→40/12, description 10→5, anchor-contains 15→12 / exact 15→40) — plus retire the now-redundant anchors→`Keywords` fold and harmonize the `GetReviewStatus` gate.

**Architecture:** The scorer `Math.Max`-combines per-term branches. P6.1/P6.3 made `Fields["tag"|"anchor"|"review"|"file"|…]` populated at read AND write, so the legacy `entry.Tags`/`entry.RetrievalAnchors`/`entry.Description`/`key:value`-in-`Keywords` branches are now redundant duplicates that only pin the score to the *old* (higher) tiers. Deleting them lets the `Fields` block (40/12) and `ContentPreview` (5) become the single source — the intended end-state. Topics are a **separate carrier** (`TopicInfo`/`TopicResult`, no `Fields` map) — their tag/description scoring **stays**. This is a deliberate ranking change, so it is **measured (A/B) before merge**, exactly as P6.1/P6.1.2 were.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions.

## Global Constraints

- **This is the intended scoring change — do not try to preserve the old scores.** After this plan, the moved signals score at their `Fields`/`ContentPreview` tiers (tag 40/12, anchor 40/12, description→preview 5). Tests asserting the *old* legacy scores are updated to the new reality (do NOT re-inflate weights to fake parity — that would defeat the cleanup). The `AttributeWeights` table stays empty/at-parity (w=1.0) — no weight tuning here.
- **Topics stay.** `ScoreTopicTerm` topic-tags (485-498, 50/15) and topic-description (501-503, 10) are NOT deleted — `TopicInfo` has no `Fields` map; deleting them would gut topic search. The `TopicResult` build (`WeightedFieldScorer.cs:225`) is untouched.
- **Non-recommended key:value keywords → ACCEPT** (sub-decision 1): deleting the `key:value`-in-`Keywords` block (417-435) degrades legacy un-migrated non-recommended `key:value` keywords (`basedOn:`/`role:`) to plain-keyword scoring. Real surface ≈ nil (exact-prefix machine reads). Do NOT broaden `Forward`'s routing to compensate (that would pollute the attribute namespace + rewrite migrated stores).
- **Keep the `sep`/`termKey`/`termValue` parse** (394-396) — the `Fields` block (405-408) uses it for pair-exact matching. Only its `key:value`-in-`Keywords` consumer (417-435) is deleted.
- **Anchor fold retirement is scoring-neutral** given `Fields["anchor"]` exists (Fields tiers ≥ keyword tiers under `Math.Max`); it triggers a **one-time idempotent re-write** on already-forward-filled anchor entries (`Forward` stops adding the anchor to `Keywords` → `MigratesTo` returns true once → re-write strips it). Benign convergence; note it.
- **No model change** (`ArtifactEntry`/`EphemeralEntry` legacy fields stay — removal is P6.6). No writer/reader change. `FileHashes` untouched.
- **Branch** off `main` @ current HEAD, named `feature/attributes-scorer-cleanup`. **Never push.** Product projects build with `TreatWarningsAsErrors`. Do NOT touch `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, `.superpowers/**`.
- **Commit trailer, exactly** (every commit):
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```

---

## Task 1: Delete legacy scoring blocks + re-home chunk parent-tags (`WeightedFieldScorer.cs`)

**Files:**
- Modify: `src/Scrinia.Core/Search/WeightedFieldScorer.cs` — `ScoreEntryTerm` (delete 4 blocks) + `ScoreChunkEntryTerm` (re-home parent-tags)
- Test: `tests/Scrinia.Tests/` (audit + update the ~4-8 affected assertions; see Steps)

**Deletions in `ScoreEntryTerm` (328-448):**
1. **Tags** block (341-354, `entry.Tags` → 50/15) — DELETE.
2. **RetrievalAnchors** block (375-386, `entry.RetrievalAnchors` → 15) — DELETE.
3. **key:value-in-Keywords** block (417-435, → 50/40/12) — DELETE. KEEP the `sep`/`termKey`/`termValue` parse at 394-396 (the Fields block needs it).
4. **Description** block (438-440, `entry.Description` → 10) — DELETE.
KEEP: Name (333-338), plain Keyword (357-370), Fields (398-415), ContentPreview (443-445).

**Re-home in `ScoreChunkEntryTerm` (538-605):** the parent-tags block (589-602) currently reads `parentEntry.Tags` at half-weight (25/8). Replace it to read `parentEntry.Fields` `"tag"` values, scored at **20 exact / 6 contains** (half of the new entry-level `Fields` tag tiers 40/12, consistent with the parent-keywords block 573-586 at 20/6). Keep it structurally parallel to the parent-keywords block:
```csharp
// Parent tags (half-weight) — now sourced from Fields["tag"] (the entry-level Tags→Fields cutover)
if (cr.ParentItem.Entry.Fields is { } pf && pf.TryGetValue(KnownFields.TagKey, out var parentTags))
{
    foreach (string tag in parentTags)
    {
        if (tag.Equals(term, StringComparison.OrdinalIgnoreCase)) { score = Math.Max(score, 20); break; }
        if (tag.Contains(term, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 6);
    }
}
```
(Use the actual local/param name for the parent entry at that site — confirm whether it's `parentEntry` or `cr.ParentItem.Entry`.)

- [ ] **Step 1: Audit + write/adjust the failing tests**

The scorer is `internal` (Scrinia.Core has `InternalsVisibleTo("Scrinia.Tests")`). First AUDIT existing tests for reliance on the deleted legacy scores: grep `tests/` for `WeightedFieldScorer`, and for score-ordering assertions in `FieldExtractorTests.cs`, `Bm25ScorerTests.cs`, and any search-ordering test. Per the research, most Fields-based parity tests PASS unchanged (person:maria etc. are forward-filled → Fields block covers them). The likely edits: a stale `because:` comment in `FieldExtractorTests.cs:173-186` (the "dual-read parses" rationale is now wrong — the score still passes via Fields), and any test asserting a tag-exact 50 out-ranks a keyword-exact 40 by that 10-pt margin (audit; none found in research but confirm). Add a focused test proving the intended post-cleanup scoring: e.g. an entry whose tag lives ONLY in `Fields["tag"]` (legacy `Tags` null) scores value-exact 40 (not 50) and still out-ranks a ContentPreview-only (5) match — i.e. the Fields block is the sole tag scorer now.

- [ ] **Step 2: Run to verify the new test fails / the audit reveals the breaks**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Scorer|FullyQualifiedName~FieldExtractor|FullyQualifiedName~Bm25"`
Expected: the new post-cleanup assertion fails against the OLD scorer (it still scores tags 50 via the legacy block); any genuinely-broken ordering test fails.

- [ ] **Step 3: Delete the 4 blocks + re-home the chunk parent-tags** (per above).

- [ ] **Step 4: Run the scorer/extractor tests** — same filter. Expected: PASS.

- [ ] **Step 5: Full suite** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` AND `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`. Update any remaining assertion that depended on a deleted legacy score to the new tier (document each in the report; do NOT re-inflate weights). Expected: green.

- [ ] **Step 6: Build warning-clean** — `dotnet build src/Scrinia.Core/Scrinia.Core.csproj` + `src/Scrinia.Mcp/Scrinia.Mcp.csproj` + `src/Scrinia.Server/Scrinia.Server.csproj`. 0 warnings each.

- [ ] **Step 7: Commit** — `"refactor(core): delete legacy scorer blocks; chunk tags -> Fields[tag] (P6.5)"`

---

## Task 2: Retire the anchors→`Keywords` fold (`AttributeMigration.cs`)

**Files:**
- Modify: `src/Scrinia.Core/Migration/AttributeMigration.cs` — `Forward` (the fold at ~85-91)
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs` (~2-4 assertion flips)

**Approach:** anchors are already routed to `Fields["anchor"]` (P6.1.2, at ~73-81 of `Forward`) and TF-seeded into BM25 at write time — the `Keywords` fold is the redundant second copy. Remove the fold block (85-91) so `mergedKeywords` stays `entry.Keywords` unchanged:
```csharp
// (delete the anchor→Keywords fold; anchors live in Fields["anchor"] + BM25 TF-seed)
string[]? mergedKeywords = entry.Keywords;
return entry with { Fields = mergedFields, Keywords = mergedKeywords };
```
`MigratesTo` still compares `Fields` + `Keywords`; a previously-folded entry will re-write once (stripping the folded anchor from `Keywords`) then converge — benign, note it in the report.

- [ ] **Step 1: Update the failing tests** — in `AttributeMigrationTests.cs`, the assertions that `Forward` folds anchors INTO `Keywords` (grep for `.Keywords` + anchor/`RetrievalAnchors` in that file, ~2-4 sites, e.g. the `Forward_MapsTagsToTagAttribute_AnchorsToKeywords` test) FLIP to assert anchors are in `Fields["anchor"]` and NOT in `Keywords`. Keep the `Fields["anchor"]` assertions. Rename any test whose name says `AnchorsToKeywords` to reflect the new reality (e.g. `AnchorsToAnchorField`).

- [ ] **Step 2: Run to verify they fail** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AttributeMigration"`. Expected: FAIL (fold still present → anchors still in Keywords).

- [ ] **Step 3: Remove the fold block.**

- [ ] **Step 4: Run to verify they pass** — same filter. Expected: PASS.

- [ ] **Step 5: Full suite** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`. The `ScriniaMcpToolsTests` anchor BM25/append tests (TF-seed + model-field, not fold-dependent) must still pass — confirming anchor search bridging survives the fold removal. Expected: green.

- [ ] **Step 6: Build warning-clean** — `dotnet build src/Scrinia.Core/Scrinia.Core.csproj`.

- [ ] **Step 7: Commit** — `"refactor(core): retire redundant anchors->Keywords fold (anchors live in Fields)"`

---

## Task 3: Harmonize `GetReviewStatus` gate (`AttributeReaders.cs`) — M1

**Files:**
- Modify: `src/Scrinia.Core/Search/AttributeReaders.cs` — `GetReviewStatus` (33-47)
- Test: `tests/Scrinia.Tests/AttributeReadersTests.cs`

**Approach:** drop the `&& review is { Length: > 0 }` length gate so the `Fields["review"]` branch triggers on KEY-EXISTENCE (consistent with `DisplayTags`/`DisplayAnchors`), keeping a null-guard:
```csharp
if (fields is not null
    && fields.TryGetValue(KnownFields.ReviewKey, out var review)
    && review is not null)   // key-existence, not length (matches DisplayTags/DisplayAnchors)
{
    // … existing positional parse …
}
```
Safe: an empty `review` array can't be produced (`Forward`/writers only add the key when `Count > 0`; `KnownFields.Merge` never creates empties), so this is a consistency cleanup, not a behavior change for producible inputs.

- [ ] **Step 1: Write the test** — add to `AttributeReadersTests.cs`: `GetReviewStatus` with `Fields = {["review"] = []}` (empty array) returns `(false, false)` derived from the (empty) Fields review — NOT falling through to a legacy `ReviewAfter`. (Construct the empty-array case explicitly to lock the harmonized behavior.)

- [ ] **Step 2: Run to verify it fails** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AttributeReaders"`. Expected: FAIL (length gate makes it fall through to legacy args).

- [ ] **Step 3: Drop the length gate** (per above).

- [ ] **Step 4: Run to verify it passes** — same filter. Expected: PASS (all AttributeReaders tests green, including the existing no-key→legacy fallback).

- [ ] **Step 5: Build warning-clean** — `dotnet build src/Scrinia.Core/Scrinia.Core.csproj`.

- [ ] **Step 6: Commit** — `"refactor(core): gate GetReviewStatus on review-key existence (M1 harmonize)"`

---

## Post-implementation: ranking measurement gate (controller step, not a code task)

After Tasks 1-3, before merge, A/B the ranking impact — the deferred uniform scoring change dual-write set up. Same protocol as P6.1/P6.1.2:
- **Harness:** old scorer (legacy blocks present) vs new (deleted) on the same corpus + query set, through the real `SearchAll` scorer+graph path. Compare top-3 (head) and top-5 (body) stability.
- **Corpus (~30 docs):** a hub tag on ~12 docs (tag 50→40 stress); anchor-only docs (exact 15→40 UP + contains 15→12 DOWN); **description-only docs where the term is in `Description` but NOT `ContentPreview`** (the 10→0 cliff — the sharpest edge, quantify how many lose their sole match and whether any were head results); legacy non-recommended `key:value` docs (`basedOn:`/`role:`, value-exact 40→12); recommended `key:value` legacy docs (`person:maria` — must stay NEUTRAL); control docs with strong name/keyword/BM25 hits.
- **Verdict (P6.1 framing):** head (top-3) stability is the gate — genuine field/BM25 hits (100-630) dominate the moved deltas (≤40). Report per-query-class top-k movement; call out any description-only doc that drops out of the head. **Merge if benign at the head**; record the finding in the spec as the P6.5 measurement note. If a head result is lost, hold and report options.

## Self-Review (author checklist — completed)

- **Spec coverage:** decisions 3 (Description removed from scoring → ContentPreview) + 4 (key:value dual-read removed; recommended keys covered by Fields) + the P6.5 bullet (delete Tags/RetrievalAnchors/Description/key:value-keyword blocks). Covered by Task 1. Anchor fold retirement (Task 2). M1 (Task 3).
- **Scope discipline:** topics kept (separate carrier); chunk parent-tags re-homed (completes the cleanup, no stranded legacy read for P6.6); non-recommended key:value accepted (near-nil surface); no weight tuning; no model/writer/reader change.
- **The scoring change is measured before merge** (the whole reason dual-write deferred it). Description-only 10→0 is the sharpest edge to watch.
- **Type consistency:** `KnownFields.TagKey`/`ReviewKey` used; chunk re-home mirrors the parent-keywords block shape.
