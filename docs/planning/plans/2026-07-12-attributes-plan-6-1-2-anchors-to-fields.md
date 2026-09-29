# Attributes P6.1.2 — Anchors → `Fields["anchor"]` (additive) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route `RetrievalAnchors` into a new `Fields["anchor"]` carrier during forward migration — additively, keeping the existing anchors→`Keywords` fold — so the P6.2 reader cutover can display anchors from `Fields["anchor"]` and the field/graph channels see anchors as a first-class facet.

**Architecture:** A one-method additive change to `AttributeMigration.Forward`, mirroring how `Tags`→`Fields["tag"]` already works: add the cleaned anchor array to the `incoming` dictionary before `KnownFields.Merge`. No `IsMultiValued` change — first migration sets the whole array; a re-run replaces it with the identical array (scalar newer-wins over the full array), so it stays idempotent exactly like `tag`. The keyword fold is untouched (additive → keyword scoring unchanged). The **only** ranking effect is the entity-graph channel gaining `anchor` nodes (`ParseEntityFields` emits a node per field key), which is measured before merge — same gate as P6.1.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions.

## Global Constraints

- **Additive only.** Keep the anchors→`Keywords` fold. No removal, no `IsMultiValued`/weight-table change, no scorer edit. The redundant keyword fold is retired later in P6.5.
- **Idempotent.** Re-running `Forward` on an already-migrated entry must not change `Fields`/`Keywords` (so `MigratesTo` returns false → no write-back churn). Verified by test.
- **Ranking gate (measure before merge).** After implementation, A/B the entity-graph ranking impact of `Fields["anchor"]` population on the representative corpus, same method as P6.1. Merge only if benign at the head; otherwise report and hold.
- **Branch** off `main` @ current HEAD, named `feature/attributes-anchors-to-fields`. **Never push.**
- **Product projects build with `TreatWarningsAsErrors`** — keep warning-clean. Do **not** touch `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, `.superpowers/**`.
- **Commit trailer, exactly** (every commit):
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```

---

## Task 1: Route anchors to `Fields["anchor"]` in `AttributeMigration.Forward`

**Files:**
- Modify: `src/Scrinia.Core/Search/KnownFields.cs` — add `public const string AnchorKey = "anchor";`
- Modify: `src/Scrinia.Core/Migration/AttributeMigration.cs` — `Forward`
- Test: `tests/Scrinia.Tests/AttributeMigrationTests.cs` — extend

**Interfaces:**
- Produces: after `Forward`, an entry with non-empty `RetrievalAnchors` has `Fields["anchor"]` = the whitespace-cleaned, ordinal-ignore-case-deduped anchor array, AND `Keywords` still contains those anchors (unchanged fold).

**Current code** (`AttributeMigration.cs`, the tail of `Forward`):
```csharp
var mergedFields = incoming.Count > 0 ? KnownFields.Merge(entry.Fields, incoming) : entry.Fields;

string[]? mergedKeywords = entry.Keywords;
if (entry.RetrievalAnchors is { Length: > 0 })
    mergedKeywords = (entry.Keywords ?? [])
        .Concat(entry.RetrievalAnchors)
        .Where(k => !string.IsNullOrWhiteSpace(k))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

return entry with { Fields = mergedFields, Keywords = mergedKeywords };
```

- [ ] **Step 1: Write the failing tests**

Add to `tests/Scrinia.Tests/AttributeMigrationTests.cs` (match the existing test style/helpers in that file):

```csharp
[Fact]
public void Forward_routes_anchors_into_Fields_anchor()
{
    var entry = NewEntry() with { RetrievalAnchors = new[] { "how to reset", "  ", "reset flow", "how to reset" } };
    var result = AttributeMigration.Forward(entry);

    result.Fields.Should().ContainKey("anchor");
    result.Fields!["anchor"].Should().Equal("how to reset", "reset flow"); // whitespace dropped, dupe deduped, order kept
}

[Fact]
public void Forward_keeps_anchors_in_keywords_additive()
{
    var entry = NewEntry() with { Keywords = new[] { "kw1" }, RetrievalAnchors = new[] { "anchor phrase" } };
    var result = AttributeMigration.Forward(entry);

    result.Keywords.Should().Contain("kw1");
    result.Keywords.Should().Contain("anchor phrase"); // fold preserved
    result.Fields!["anchor"].Should().Equal("anchor phrase"); // also a field
}

[Fact]
public void Forward_no_anchors_adds_no_anchor_field()
{
    var entry = NewEntry() with { RetrievalAnchors = null };
    var result = AttributeMigration.Forward(entry);
    (result.Fields is null || !result.Fields.ContainsKey("anchor")).Should().BeTrue();
}

[Fact]
public void Forward_anchor_routing_is_idempotent()
{
    var entry = NewEntry() with { RetrievalAnchors = new[] { "a1", "a2" } };
    var once = AttributeMigration.Forward(entry);
    AttributeMigration.MigratesTo(once, out _).Should().BeFalse(); // second pass is a no-op
}

[Fact]
public void Forward_anchor_coexists_with_other_fields()
{
    var entry = NewEntry() with
    {
        Tags = new[] { "t1" },
        RetrievalAnchors = new[] { "a1" },
    };
    var result = AttributeMigration.Forward(entry);
    result.Fields!["tag"].Should().Equal("t1");
    result.Fields!["anchor"].Should().Equal("a1");
}
```
(If the file has no `NewEntry()` helper, construct a minimal `ArtifactEntry` inline as the neighbouring tests do.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AttributeMigration"`
Expected: FAIL (the new `anchor` assertions fail; existing anchor→Keywords tests still pass).

- [ ] **Step 3: Add `KnownFields.AnchorKey`**

In `src/Scrinia.Core/Search/KnownFields.cs`, beside `ReviewKey`:
```csharp
    /// <summary>The attribute key retrieval anchors migrate to (display + searchable facet).
    /// Anchors also remain folded into Keywords additively; the redundant fold is retired in P6.5.</summary>
    public const string AnchorKey = "anchor";
```

- [ ] **Step 4: Route anchors into `incoming` before the merge**

In `AttributeMigration.Forward`, insert the anchor→field routing immediately **before** the `mergedFields` line, and leave the keyword fold below unchanged:
```csharp
    // Anchors → Fields["anchor"]: a first-class searchable facet + the display carrier P6.2 reads.
    // Additive — anchors also stay folded into Keywords below (keyword channel unchanged; the
    // redundant fold is retired in P6.5). Cleaned identically to the fold (whitespace + dedupe).
    if (entry.RetrievalAnchors is { Length: > 0 })
    {
        var cleanAnchors = entry.RetrievalAnchors
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (cleanAnchors.Length > 0)
            incoming[KnownFields.AnchorKey] = cleanAnchors;
    }

    var mergedFields = incoming.Count > 0 ? KnownFields.Merge(entry.Fields, incoming) : entry.Fields;

    string[]? mergedKeywords = entry.Keywords;
    if (entry.RetrievalAnchors is { Length: > 0 })
        mergedKeywords = (entry.Keywords ?? [])
            .Concat(entry.RetrievalAnchors)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    return entry with { Fields = mergedFields, Keywords = mergedKeywords };
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AttributeMigration"`
Expected: PASS (new + existing).

- [ ] **Step 6: Build product projects warning-clean + full core suite**

Run: `dotnet build src/Scrinia.Core/Scrinia.Core.csproj` (0 warnings); `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Expected: green.

- [ ] **Step 7: Commit** — `"feat(core): route retrieval anchors into Fields[anchor] (additive)"`

---

## Post-implementation: ranking measurement gate (controller step, not a code task)

After Task 1 passes, before merge, A/B the entity-graph ranking impact of `Fields["anchor"]` population — same protocol as the P6.1 entity-graph measurement:
- Representative corpus with entries carrying anchors; run each query through the real scorer + `EntityGraphScoreContributor` with `Fields["anchor"]` populated vs. absent.
- Report: top-3 stability per query, magnitude/placement of any movement, whether anchor nodes create a hub effect (anchors are typically few and specific per entry, so expected impact ≤ the tag case).
- **Merge only if benign at the head.** Record the finding in the spec (`2026-07-10-memory-model-keywords-attributes-design.md`, under the P6.1 measurement note) as a P6.1.2 addendum. If not benign, hold and report options (e.g. down-weight `anchor` in the graph, or restrict `ParseEntityFields` to `EntityKeys`).

## Self-Review (author checklist — completed)

- **Spec coverage:** implements the "Tag anchors in Fields first" decision (anchors → `Fields["anchor"]`, keyword fold kept), unblocking P6.2 anchors display.
- **Idempotency:** `MigratesTo` set-equality over `Fields` + `Keywords` already handles arbitrary keys; the scalar-replace-with-full-array path makes re-runs a no-op (test `Forward_anchor_routing_is_idempotent`).
- **No hidden scope:** no `IsMultiValued`/weight change (scalar-replace preserves the full anchor array, mirroring `tag`); keyword fold unchanged; scorer untouched. Only the graph channel shifts — gated by measurement.
- **Type consistency:** `KnownFields.AnchorKey = "anchor"` used in both the migration and (next plan) the P6.2 reader.
