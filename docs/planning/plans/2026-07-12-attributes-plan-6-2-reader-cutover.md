# Attributes P6.2 — Reader Cutover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repoint the display and aggregation reads of the legacy per-memory fields (`Description`, `Tags`, `ReviewAfter`/`ReviewWhen`) onto the attributes-model carriers (`ContentPreview`, `Fields["tag"]`, `Fields["review"]`) across the MCP, CLI, and Server surfaces — so the legacy model fields can be removed in P6.6 without touching a single display path.

**Architecture:** A single pure read-side helper (`AttributeReaders` in `Scrinia.Core.Search`) owns the three derivations — review status, preview text, display tags — sourcing from the new carriers with a legacy fallback for entries not yet forward-filled (in-memory ephemeral). All three consumer surfaces (`Scrinia.Mcp`, `Scrinia`, `Scrinia.Server`) call the helper; none re-implement the parse. This is an **additive, non-regressing** cutover: forward-fill (P6.1) guarantees `Fields["tag"]`/`Fields["review"]` equal the legacy values on every persisted entry, so display output is byte-identical while the read source moves.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions.

## Global Constraints

- **Non-regressing display cutover only.** No change to `ArtifactEntry`/`EphemeralEntry` (that is P6.6). No writer/`Store`/`Append`/orchestrator change (P6.3). No scorer change — `WeightedFieldScorer.cs` is untouched (P6.5). No JSON/HTTP wire-contract change.
- **Read from the new carrier, fall back to legacy.** Every cut-over read prefers the attributes carrier and falls back to the legacy field when the carrier key is absent (covers in-memory ephemeral entries that P6.1's load-seam forward-fill never sees). The legacy fallback branch is removed together with the legacy field in P6.6.
- **Non-regression scope — IMPORTANT correction.** Output is byte-identical for the **review / tag / anchor** carriers (P6.1/P6.1.2 forward-fill makes `Fields["review"|"tag"|"anchor"]` value-equal to the legacy fields). It is **NOT** byte-identical for **Description → ContentPreview**: per the spec's locked decision (design doc line 220, *"`Description`: FULLY REMOVE — all display falls back to the always-populated `ContentPreview`"*), `ContentPreview` is independently populated at write time (`store.GenerateContentPreview`), NOT forward-filled from `Description` — so `PreviewText(ContentPreview, Description)` intentionally shows the content preview, which differs from a caller-supplied `Description`. This is the intended behavior. **Consequence for Tasks 3–4:** existing CLI/Server tests that assert on `Description`-derived display output WILL break and must be updated to assert on the `ContentPreview`-derived text — legitimately, provided you (a) trace it to spec line 220, (b) preserve the test's original purpose, (c) confirm the store actually populates `ContentPreview`, and (d) flag the change in your report. Do not weaken a test to hide a genuine failure. (Task 2 already handled one such test, `ScriniaMcpToolsTests.Store_OverwriteSameName_UpdatesIndex`.)
- **Depends on P6.1.2 merged** (`feature/attributes-anchors-to-fields`): anchors are now carried in `Fields["anchor"]`, which this plan's anchors display reads. Do not start until P6.1.2 is on `main`.
- **Explicitly OUT of P6.2 scope (do not touch):**
  - **`[drift]` / `CodeRefs`** (MemoryTools.Core.cs:661,714,717 + `BuildDriftList`) — `Fields["file"]` carries only path keys, not the stored hashes drift needs; renamed/repointed in P6.4.
  - **`TopicResult` reads** (`tr.Tags`/`tr.Description`) — a separate result carrier with no `Fields` equivalent.
  - **Typed review fields on wire DTOs** (`CliMemoryEntry.ReviewAfter`/`ReviewWhen`) — structured passthroughs re-homed in P6.6; re-sourcing a typed `DateTime?` from the positional `Fields["review"]` string array is a lossy round-trip with no display benefit.
- **Branch** off `main` @ `637d0fa`, named `feature/attributes-reader-cutover`. **Never push.**
- **Product projects build with `TreatWarningsAsErrors`** — keep the build warning-clean. Do **not** touch `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, or `.superpowers/**`.
- **Commit trailer, exactly** (every commit):
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```

### Anchors decision (governs Tasks 1–2)
Per the "tag anchors in Fields first" decision, **P6.1.2 adds `Fields["anchor"]`** (additive; the `Keywords` fold is kept). P6.2 therefore repoints the labelled `anchors:` block to read `Fields["anchor"]`, with a legacy `RetrievalAnchors` fallback for entries not yet forward-filled. This is a genuine non-regressing cutover: forward-fill makes `Fields["anchor"]` value-equal to `RetrievalAnchors`. Anchors display is **MCP-only** (`AppendAnchors`, MemoryTools.Core.cs:922,939) — no CLI/Server anchor rendering exists. The `DisplayAnchors` helper (Task 1) and the `AppendResultBlock` cutover (Task 2) implement it.

---

## File Structure

- **Create** `src/Scrinia.Core/Search/AttributeReaders.cs` — the pure read-side helper (Task 1).
- **Modify** `src/Scrinia.Core/Search/KnownFields.cs` — add `TagKey` constant (Task 1).
- **Modify** `src/Scrinia.Mcp/MemoryTools.Core.cs` — `BuildSummary`, `BuildFullList`, `AppendResultBlock` (Task 2).
- **Modify** `src/Scrinia/Commands/ScriniaCommands.Memory.cs` — `list`/`search` JSON + table, `BuildCliSummary` (Task 3).
- **Modify** `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` + `src/Scrinia.Server/Chat/AgentLoop.cs` (Task 4).
- **Create** `tests/Scrinia.Tests/AttributeReadersTests.cs` (Task 1); extend surface tests per task.

---

## Task 1: `AttributeReaders` helper (pure, fully TDD'd)

**Files:**
- Create: `src/Scrinia.Core/Search/AttributeReaders.cs`
- Modify: `src/Scrinia.Core/Search/KnownFields.cs` (add `public const string TagKey = "tag";` next to `ReviewKey`)
- Test: `tests/Scrinia.Tests/AttributeReadersTests.cs`

**Interfaces:**
- Produces (consumed by Tasks 2–4):
  ```csharp
  namespace Scrinia.Core.Search;

  public static class AttributeReaders
  {
      public readonly record struct ReviewStatus(bool IsStale, bool NeedsReview);

      // Sources Fields["review"] when that key exists, else legacy ReviewAfter/ReviewWhen.
      // Positional parse of the review array: an element matching the round-trip ("o")
      // DateTimeOffset format is the review date; any other non-blank element is the condition.
      // IsStale   = a review date is present and <= now.
      // NeedsReview = not stale AND a review condition is present.
      public static ReviewStatus GetReviewStatus(
          IReadOnlyDictionary<string, string[]>? fields,
          DateTimeOffset? legacyReviewAfter,
          string? legacyReviewWhen,
          DateTimeOffset now);

      // Description column / preview text: ContentPreview when present, else Description.
      public static string PreviewText(string? contentPreview, string description);

      // Tags for display/aggregation: Fields["tag"] when that key exists, else legacy Tags; never null.
      public static IReadOnlyList<string> DisplayTags(
          IReadOnlyDictionary<string, string[]>? fields, string[]? legacyTags);

      // Anchors for the anchors: display block: Fields["anchor"] when that key exists, else
      // legacy RetrievalAnchors; null/empty ⇒ empty (caller suppresses the block), preserving
      // AppendAnchors' current "no anchors ⇒ no line" behaviour.
      public static IReadOnlyList<string> DisplayAnchors(
          IReadOnlyDictionary<string, string[]>? fields, string[]? legacyAnchors);
  }
  ```
  (`DisplayAnchors` mirrors `DisplayTags` with `KnownFields.AnchorKey`; test it with the same three cases: prefers `Fields["anchor"]`, falls back to legacy, never null.)

**Rationale for the parse rule:** `AttributeMigration.Forward` writes the review array as `[ReviewAfter.ToString("o")?, ReviewWhen?]` (date first when present, then condition). Matching the date with `TryParseExact(token, "o", …)` — not a loose `TryParse` — prevents a free-text condition that happens to look date-ish (e.g. `"2026"`) from being misread as the review date.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Globalization;
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class AttributeReadersTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 12, 0, 0, 0, TimeSpan.Zero);
    private static Dictionary<string, string[]> F(params (string k, string[] v)[] kv)
        => kv.ToDictionary(x => x.k, x => x.v, StringComparer.OrdinalIgnoreCase);

    // ---- GetReviewStatus: sources Fields["review"] ----
    [Fact]
    public void ReviewStatus_stale_from_fields_past_date()
    {
        var past = Now.AddDays(-1).ToString("o", CultureInfo.InvariantCulture);
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { past })), null, null, Now);
        s.IsStale.Should().BeTrue();
        s.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public void ReviewStatus_needsReview_from_fields_condition_only()
    {
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { "after refactor X" })), null, null, Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public void ReviewStatus_future_date_with_condition_is_needsReview_not_stale()
    {
        var future = Now.AddDays(30).ToString("o", CultureInfo.InvariantCulture);
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { future, "when ready" })), null, null, Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public void ReviewStatus_future_date_no_condition_shows_nothing()
    {
        // Mirrors legacy: future ReviewAfter with no ReviewWhen renders no marker.
        var future = Now.AddDays(30).ToString("o", CultureInfo.InvariantCulture);
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { future })), null, null, Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public void ReviewStatus_falls_back_to_legacy_when_no_review_key()
    {
        // Ephemeral entry never forward-filled: Fields absent → legacy fields drive.
        var s = AttributeReaders.GetReviewStatus(null, Now.AddDays(-1), null, Now);
        s.IsStale.Should().BeTrue();
        var s2 = AttributeReaders.GetReviewStatus(F(("tag", new[] { "x" })), null, "later", Now);
        s2.NeedsReview.Should().BeTrue(); // fields present but no "review" key → legacy fallback
    }

    [Fact]
    public void ReviewStatus_condition_that_looks_numeric_is_not_a_date()
    {
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { "2026" })), null, null, Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeTrue(); // "2026" is not round-trip("o") format → treated as condition
    }

    [Fact]
    public void ReviewStatus_none_when_no_data()
        => AttributeReaders.GetReviewStatus(null, null, null, Now).Should().Be(new AttributeReaders.ReviewStatus(false, false));

    // ---- PreviewText ----
    [Fact]
    public void PreviewText_prefers_contentPreview()
        => AttributeReaders.PreviewText("preview", "desc").Should().Be("preview");

    [Fact]
    public void PreviewText_falls_back_to_description_when_null()
        => AttributeReaders.PreviewText(null, "desc").Should().Be("desc");

    // ---- DisplayTags ----
    [Fact]
    public void DisplayTags_prefers_fields_tag()
        => AttributeReaders.DisplayTags(F(("tag", new[] { "a", "b" })), new[] { "legacy" })
            .Should().Equal("a", "b");

    [Fact]
    public void DisplayTags_falls_back_to_legacy_when_no_tag_key()
        => AttributeReaders.DisplayTags(F(("review", new[] { "x" })), new[] { "legacy" })
            .Should().Equal("legacy");

    [Fact]
    public void DisplayTags_never_null()
        => AttributeReaders.DisplayTags(null, null).Should().BeEmpty();
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AttributeReaders"`
Expected: FAIL — `AttributeReaders` does not exist.

- [ ] **Step 3: Add `KnownFields.TagKey`**

In `src/Scrinia.Core/Search/KnownFields.cs`, beside the existing `public const string ReviewKey = "review";`, add:
```csharp
    /// <summary>The attribute key legacy free-form tags migrate to (see AttributeMigration.TagKey).</summary>
    public const string TagKey = "tag";
```

- [ ] **Step 4: Implement `AttributeReaders`**

```csharp
using System.Globalization;

namespace Scrinia.Core.Search;

/// <summary>
/// Read-side helpers that source display values from the attributes model (<c>Fields</c> /
/// <c>ContentPreview</c>), falling back to the legacy per-memory fields for entries not yet
/// forward-filled (e.g. in-memory ephemeral entries the load-seam migration never sees).
/// Introduced by the P6.2 reader cutover; the legacy fallbacks are removed with the legacy
/// fields in P6.6.
/// </summary>
public static class AttributeReaders
{
    public readonly record struct ReviewStatus(bool IsStale, bool NeedsReview);

    /// <summary>
    /// Review status derived from <c>Fields["review"]</c> (positional: an element in round-trip
    /// <c>"o"</c> DateTimeOffset format is the review date; any other non-blank element is the
    /// condition) when that key exists, else the legacy <paramref name="legacyReviewAfter"/> /
    /// <paramref name="legacyReviewWhen"/>. Mirrors the legacy predicate exactly:
    /// stale iff a review date is present and &lt;= <paramref name="now"/>; needs-review iff not
    /// stale and a condition is present.
    /// </summary>
    public static ReviewStatus GetReviewStatus(
        IReadOnlyDictionary<string, string[]>? fields,
        DateTimeOffset? legacyReviewAfter,
        string? legacyReviewWhen,
        DateTimeOffset now)
    {
        DateTimeOffset? date;
        string? condition;

        if (fields is not null
            && fields.TryGetValue(KnownFields.ReviewKey, out var review)
            && review is { Length: > 0 })
        {
            date = null;
            condition = null;
            foreach (string token in review)
            {
                if (DateTimeOffset.TryParseExact(token, "o", CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed))
                    date ??= parsed;
                else if (!string.IsNullOrWhiteSpace(token))
                    condition ??= token;
            }
        }
        else
        {
            date = legacyReviewAfter;
            condition = string.IsNullOrWhiteSpace(legacyReviewWhen) ? null : legacyReviewWhen;
        }

        bool isStale = date.HasValue && date.Value <= now;
        bool needsReview = !isStale && condition is not null;
        return new ReviewStatus(isStale, needsReview);
    }

    /// <summary>Description column / preview text: <c>ContentPreview</c> when present, else <c>Description</c>.</summary>
    public static string PreviewText(string? contentPreview, string description)
        => contentPreview ?? description;

    /// <summary>
    /// Tags for display/aggregation: <c>Fields["tag"]</c> when that key exists, else the legacy
    /// <paramref name="legacyTags"/>. Never null.
    /// </summary>
    public static IReadOnlyList<string> DisplayTags(
        IReadOnlyDictionary<string, string[]>? fields, string[]? legacyTags)
    {
        if (fields is not null && fields.TryGetValue(KnownFields.TagKey, out var tags) && tags is not null)
            return tags;
        return legacyTags ?? [];
    }

    /// <summary>
    /// Anchors for the <c>anchors:</c> display block: <c>Fields["anchor"]</c> when that key exists,
    /// else the legacy <paramref name="legacyAnchors"/>. Never null (empty ⇒ caller omits the block).
    /// </summary>
    public static IReadOnlyList<string> DisplayAnchors(
        IReadOnlyDictionary<string, string[]>? fields, string[]? legacyAnchors)
    {
        if (fields is not null && fields.TryGetValue(KnownFields.AnchorKey, out var anchors) && anchors is not null)
            return anchors;
        return legacyAnchors ?? [];
    }
}
```
(`KnownFields.AnchorKey = "anchor"` and `TagKey = "tag"` — `AnchorKey` is added in P6.1.2; add `TagKey` here in Step 3 if not already present.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AttributeReaders"`
Expected: PASS (all 13).

- [ ] **Step 6: Build the product projects warning-clean**

Run: `dotnet build src/Scrinia.Core/Scrinia.Core.csproj`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/Search/AttributeReaders.cs src/Scrinia.Core/Search/KnownFields.cs tests/Scrinia.Tests/AttributeReadersTests.cs
git commit  # message: "feat(core): add AttributeReaders read-side helper for P6.2 cutover"
```

---

## Task 2: MCP surface cutover (`MemoryTools.Core.cs`)

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — `BuildSummary` (D1–D3), `BuildFullList` (D4–D5), `AppendResultBlock` (D6, D8)
- Test: `tests/Scrinia.Tests/` (add an MCP display test file if none reaches these methods; see Step 1)

**Interfaces:**
- Consumes: `AttributeReaders.{GetReviewStatus, PreviewText, DisplayTags}` from Task 1.

**Scope for this task (exact sites):**
1. `BuildSummary` staleCount/reviewCount (lines ~564–566) → `GetReviewStatus`.
2. `BuildSummary` "Top keywords" Tags fold (lines ~611–613) → `DisplayTags(item.Entry.Fields, item.Entry.Tags)`. (The separate "Top attributes" block at ~628–646 already reads `Fields` — leave it.)
3. `BuildFullList` `[stale]`/`[review?]` prefix (lines ~705–712) → `GetReviewStatus`; the `[drift]` block (714) and `AppendAnchors` stay untouched.
4. `BuildFullList` description column `string desc = e.Description;` (~735) → `AttributeReaders.PreviewText(e.ContentPreview, e.Description)`.
5. `AppendResultBlock` entry description (921) → `PreviewText(er.Item.Entry.ContentPreview, er.Item.Entry.Description)`; chunk description (938) → `cr.Chunk.ContentPreview ?? PreviewText(cr.ParentItem.Entry.ContentPreview, cr.ParentItem.Entry.Description)`.
6. **Anchors cutover** — `AppendAnchors` call sites (922, 939) source anchors via the helper: `AppendAnchors(sb, AttributeReaders.DisplayAnchors(entry.Fields, entry.RetrievalAnchors))`. Change `AppendAnchors`' signature to `IReadOnlyList<string> anchors` with an early `if (anchors.Count == 0) return;` (DisplayAnchors never returns null; empty ⇒ no line, preserving today's behaviour). The `TopicResult` arm (948–949) stays untouched.

- [ ] **Step 1: Write failing behavioral tests**

These methods are `private static`. Add `[assembly: InternalsVisibleTo("Scrinia.Tests")]` only if needed, or test through the public `list`/`search` tool entry points. Prefer the public seam. Add to a new `tests/Scrinia.Tests/McpDisplayCutoverTests.cs` (or the nearest existing MCP tool test) tests asserting the **new source is honoured**:

```csharp
// An entry whose review data lives ONLY in Fields (Description/Tags/Review legacy fields null,
// as a persisted forward-filled entry would present after P6.6) still renders [stale] and its
// ContentPreview in list output.
// 1. Build a store with an ArtifactEntry: Description="", ContentPreview="the preview",
//    Tags=null, ReviewAfter=null, ReviewWhen=null,
//    Fields = { ["review"] = [<past ISO "o" date>], ["tag"] = ["alpha"] }.
// 2. Call the list tool in mode:'full'; assert the output row contains "[stale]" and "the preview".
// 3. Call list mode:'summary'; assert "1 stale" and that "alpha" appears in Top keywords.
```
Follow the existing MCP tool test setup in `tests/Scrinia.Tests` for store/tool construction (grep for the `list`/`ListAsync` tool test or `ResponseBuilder`). If no such harness exists, add `InternalsVisibleTo` and call `BuildSummary`/`BuildFullList` directly with a `List<ScopedArtifact>`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~McpDisplayCutover"`
Expected: FAIL (output still sourced from legacy nulls → no `[stale]`, no preview).

- [ ] **Step 3: Apply the cutover**

`BuildSummary` — compute `now` once and route the counts through the helper:
```csharp
var now = DateTimeOffset.UtcNow;
int staleCount = entries.Count(e =>
    AttributeReaders.GetReviewStatus(e.Entry.Fields, e.Entry.ReviewAfter, e.Entry.ReviewWhen, now).IsStale);
int reviewCount = entries.Count(e =>
    AttributeReaders.GetReviewStatus(e.Entry.Fields, e.Entry.ReviewAfter, e.Entry.ReviewWhen, now).NeedsReview);
```
"Top keywords" tag fold:
```csharp
foreach (var tag in AttributeReaders.DisplayTags(item.Entry.Fields, item.Entry.Tags))
    keywordCounts[tag] = keywordCounts.GetValueOrDefault(tag) + 1;
```
`BuildFullList` review prefix:
```csharp
var status = AttributeReaders.GetReviewStatus(e.ReviewAfter is null && e.Fields is null ? null : e.Fields,
    e.ReviewAfter, e.ReviewWhen, DateTimeOffset.UtcNow);
string reviewPrefix = status.IsStale ? "[stale] " : status.NeedsReview ? "[review?] " : "";
```
(Pass `e.Fields` directly — the helper already handles the null/missing-key fallback; the ternary above is unnecessary, use `AttributeReaders.GetReviewStatus(e.Fields, e.ReviewAfter, e.ReviewWhen, DateTimeOffset.UtcNow)`.)
`BuildFullList` description:
```csharp
string desc = AttributeReaders.PreviewText(e.ContentPreview, e.Description);
```
`AppendResultBlock`:
```csharp
// entry arm (line 921):
AppendDescription(sb, AttributeReaders.PreviewText(er.Item.Entry.ContentPreview, er.Item.Entry.Description));
// chunk arm (line 938):
AppendDescription(sb, cr.Chunk.ContentPreview
    ?? AttributeReaders.PreviewText(cr.ParentItem.Entry.ContentPreview, cr.ParentItem.Entry.Description));
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~McpDisplayCutover"`
Expected: PASS.

- [ ] **Step 5: Full MCP + core suite regression**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Expected: green (no display test regressed — output byte-identical for forward-filled entries).

- [ ] **Step 6: Commit** — `"refactor(mcp): route list/summary/search display reads through AttributeReaders"`

---

## Task 3: CLI surface cutover (`ScriniaCommands.Memory.cs`)

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs` — `list --json` (D10–D11), `list` table (D12–D13), `BuildCliSummary` (D14–D15), `search --json`/table (D16–D19)
- Test: `tests/Scrinia.Tests/` (CLI command test harness; see Step 1)

**Interfaces:**
- Consumes: `AttributeReaders.*` from Task 1.

**Scope (exact sites, from the inventory):**
1. `list --json` `isStale`/`needsReview` (lines ~70–71) → `GetReviewStatus`.
2. `CliMemoryEntry` fill (line ~75): `Description` → `PreviewText(e.ContentPreview, e.Description)`; `Tags` → `DisplayTags(e.Fields, e.Tags)` materialized to `string[]` (`.ToArray()` to match the DTO's declared type — confirm `CliMemoryEntry`'s field type in `CliJsonContext.cs:33`). **Leave the typed `ReviewAfter`/`ReviewWhen` JSON fields sourced from the legacy fields** (out of scope per Global Constraints — wire DTO passthrough re-homed in P6.6). Keep the `isStale`/`needsReview` bools (now helper-derived) as the JSON's staleness signal.
3. `list` table `[stale]`/`[review?]` prefix (lines ~108–112) → `GetReviewStatus`; description column (~113) → `PreviewText(e.ContentPreview, e.Description)` (keep the existing `.Replace('\n',' ').Replace('\r',' ')` normalization around the result).
4. `BuildCliSummary` stale/review counts (~134–136) → `GetReviewStatus`; "Top keywords" Tags fold (~168–170) → `DisplayTags`. (The "Top fields" block at ~181–193 already reads `Fields` — leave it.)
5. `search --json`/table entry descriptions (274, 312) → `PreviewText(entry.ContentPreview, entry.Description)`; chunk descriptions (269, 300) → `cr.Chunk.ContentPreview ?? PreviewText(cr.ParentItem.Entry.ContentPreview, cr.ParentItem.Entry.Description)`. The `TopicResult` arms (277, 321) stay untouched.

- [ ] **Step 1: Write failing behavioral test**

Locate the CLI command test harness (grep `tests/` for `ScriniaCommands` / `list` command invocation). Add a test: a store entry with legacy `Description=""`, `Tags=null`, review legacy null but `Fields = {["review"]=[past-date], ["tag"]=["alpha"]}` and `ContentPreview="cli preview"` →
- `list` table output contains `[stale]` and `cli preview`;
- `BuildCliSummary` reports `1 stale` and `alpha` in Top keywords.

If the CLI table/summary methods are not reachable, add `InternalsVisibleTo("Scrinia.Tests")` to `src/Scrinia` and call the builder methods directly.

- [ ] **Step 2: Run to verify it fails** — Expected: FAIL (legacy nulls → no marker/preview).

- [ ] **Step 3: Apply the cutover** per the exact sites above, calling `AttributeReaders.*`. Materialize `DisplayTags(...)` with `.ToArray()` wherever a `string[]` is required by the DTO or downstream.

- [ ] **Step 4: Run tests to verify they pass** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~CliDisplayCutover"` (or the chosen name). Expected: PASS.

- [ ] **Step 5: Build `src/Scrinia` warning-clean + regression** — `dotnet build src/Scrinia/Scrinia.csproj` (0 warnings); `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` green.

- [ ] **Step 6: Commit** — `"refactor(cli): route list/search/summary display reads through AttributeReaders"`

---

## Task 4: Server surface cutover (`MemoryEndpoints.cs` + `AgentLoop.cs`)

**Files:**
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` — list DTO (D20), search DTOs (D21–D22)
- Modify: `src/Scrinia.Server/Chat/AgentLoop.cs` — recall/search text (D23–D24)
- Test: `tests/Scrinia.Server.Tests/MemoryEndpointTests.cs` (extend)

**Interfaces:**
- Consumes: `AttributeReaders.*` from Task 1.

**Scope (exact sites):**
1. `MemoryEndpoints.cs` list `MemoryListItem(…, Description, Tags)` (lines ~103–104): `Description` → `PreviewText(e.ContentPreview, e.Description)`; `Tags` → `DisplayTags(e.Fields, e.Tags).ToArray()` (confirm `MemoryListItem`'s `Tags` type in `ApiDtos.cs:31`). **Do not** add/remove DTO fields.
2. `MemoryEndpoints.cs` search entry description (262) → `PreviewText(entry.ContentPreview, entry.Description)`; chunk (269) → `cr.Chunk.ContentPreview ?? PreviewText(cr.ParentItem.Entry.ContentPreview, cr.ParentItem.Entry.Description)`. `TopicResult` arm (276) untouched.
3. `AgentLoop.cs` entry (179) → `AttributeReaders.PreviewText(er.Item.Entry.ContentPreview, er.Item.Entry.Description)`; chunk (180) → `AttributeReaders.PreviewText(cr.ParentItem.Entry.ContentPreview, cr.ParentItem.Entry.Description)` (preserving the existing `?? ""` guard: `PreviewText(...)` already returns non-null when `Description` is non-null; keep `?? ""` only if `Description` can be null in that context). `TopicResult` arm (181) untouched.

- [ ] **Step 1: Write failing test** in `MemoryEndpointTests.cs`: store an entry with `Description=""`, `ContentPreview="server preview"`, `Tags=null`, `Fields={["tag"]=["alpha"]}`; GET the list endpoint; assert the returned `MemoryListItem.Description == "server preview"` and `Tags` contains `"alpha"`. Use the existing `ScriniaServerFactory` harness.

- [ ] **Step 2: Run to verify it fails** — Expected: FAIL (`Description==""`, `Tags` empty).

- [ ] **Step 3: Apply the cutover** per the exact sites above.

- [ ] **Step 4: Run tests to verify they pass** — `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~MemoryEndpoint"`. Expected: PASS.

- [ ] **Step 5: Build `src/Scrinia.Server` warning-clean + regression** — `dotnet build src/Scrinia.Server/Scrinia.Server.csproj` (0 warnings); `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj` green.

- [ ] **Step 6: Commit** — `"refactor(server): route memory endpoint + agent-loop display reads through AttributeReaders"`

---

## Self-Review (author checklist — completed)

- **Spec coverage:** P6.2 named targets — list description → `ContentPreview` (D5/D13/D17/D19/D20/D21/D23/D24, Tasks 2–4 ✔); `[stale]`/`[review?]` → `Fields["review"]` (D1/D2/D4/D10/D12/D14, Tasks 2–3 ✔); summary tag-agg → `Fields["tag"]` (D3/D15, Tasks 2–3 ✔); anchors display → `Fields["anchor"]` (D7/D9, Task 2 ✔, on P6.1.2). Surfaces: MCP (Task 2), CLI (Task 3), Server + AgentLoop + MemoryEndpoints (Task 4) ✔.
- **Out-of-scope items** explicitly fenced: drift/CodeRefs, TopicResult, typed review DTO fields, writer/scorer/model — all deferred with the phase that owns them.
- **Type consistency:** `AttributeReaders` signatures identical across all four tasks; `IReadOnlyList<string>` from `DisplayTags` materialized with `.ToArray()` where a `string[]` DTO field requires it.
- **Non-regression guarantee:** forward-fill (P6.1) makes `Fields["tag"]`/`Fields["review"]` value-equal to legacy on persisted entries, so output is byte-identical; the legacy fallback in the helper covers non-forward-filled ephemeral entries. Every task's regression step runs the surface's existing suite to confirm.
