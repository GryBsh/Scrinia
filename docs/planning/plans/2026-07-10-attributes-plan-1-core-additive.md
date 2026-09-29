# Attributes Model — Plan 1: Core (additive) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the additive core of the keywords/attributes model — a per-attribute-key weight table, a uniform entity-graph feed (any attribute key links, not just 5 blessed ones), and weight-scaled attribute scoring — with **zero behavior change** (weights start at parity) and **no removals** (legacy tag/anchor/description scoring stays as the dual-read path).

**Architecture:** Scrinia already has the internal `Fields` model (`Dictionary<string,string[]>` on `ArtifactEntry`), `FieldExtractor.RouteKeywords`, and `Fields` scoring in `WeightedFieldScorer`. This plan generalizes the *curation* from a hard 5-key graph gate (`KnownFields.EntityKeys`) to a soft, tunable per-key **weight table**, feeds the graph from **all** attributes, and threads the weight through attribute scoring. Everything is additive; the tool surface, migration, and legacy-removal are later plans.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions (test projects `tests/Scrinia.Tests`).

## Global Constraints

- This feature is **approved product surgery** on `Scrinia.Core` — the earlier "don't modify production src/" rule was bench-scoped and does not apply here. Do NOT touch the user's bench WIP files (`.gitignore`, `benchmarks/.../AgenticIngestPrompts.cs`, `LlmAgentHarness.cs`, `ScriniaRetriever.cs`, `OpenAiCompatibleBenchLlmClient.cs`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`, `benchmarks/.../tools/`, `package.json`) — they are unrelated to this feature.
- **Parity first, tune later.** Initial attribute weights are all `1.0` — this plan must not change any existing score or ranking. The weight *table* is the mechanism; tuning its values is a later, separate effort judged on real-workflow signal, never a benchmark fit.
- **Additive only.** Do NOT remove the legacy `Tags` / `RetrievalAnchors` / `Description` scoring blocks or model fields in this plan — they are the dual-read path until migration. Removal is the cleanup plan.
- **Do not touch entity-graph propagation/seeding internals** — only change *what feeds* it (all attributes) here. Weighting inside the graph and hub down-weighting are the separate graph-tuning effort.
- Commit trailer, exactly:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
  then `Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g`
- Never push. Branch off `main` before Task 1.

## File Structure

- `src/Scrinia.Core/Search/KnownFields.cs` — MODIFY: add the weight table (`AttributeWeight`), `RecommendedKeys`, `ReviewKey`; keep `EntityKeys`/`ReferenceKeys`/`IsMultiValued`/`MaxFields` (still referenced elsewhere).
- `src/Scrinia.Core/Search/EntityGraph.cs` — MODIFY: `ParseEntityFields` yields **all** `(key, value)` pairs, not only `EntityKeys`.
- `src/Scrinia.Core/Search/WeightedFieldScorer.cs` — MODIFY: scale the `Fields` scoring block by `KnownFields.AttributeWeight(key)`.
- `tests/Scrinia.Tests/KnownFieldsTests.cs` — EXTEND (already exists).
- `tests/Scrinia.Tests/EntityGraphTests.cs` — EXTEND (already exists; uniform-feed test).
- `tests/Scrinia.Tests/Bm25ScorerTests.cs` — EXTEND (already exists; the scorer is exercised here via `SearchAll` — `ScoreEntryTerm` is `private static`, so weight/parity tests go through the public `SearchAll` path, mirroring this file's existing store-build-and-search pattern).

Test files are flat in `tests/Scrinia.Tests/` (project `tests/Scrinia.Tests/Scrinia.Tests.csproj`, which has `InternalsVisibleTo` on Scrinia.Core). Read the existing file for a class before extending it and mirror its construction helpers.

---

### Task 1: Attribute weight table in `KnownFields`

Introduce the per-key weight table that will (a) scale attribute scoring here and (b) be read by the graph in its later tuning. Parity: every weight is `1.0` for now, so nothing changes yet — this task only adds the mechanism and proves it's wired.

**Files:**
- Modify: `src/Scrinia.Core/Search/KnownFields.cs`
- Test: `tests/Scrinia.Tests/KnownFieldsTests.cs` (exists — add the tests below to the existing class; keep existing tests)

**Interfaces:**
- Consumes: nothing new.
- Produces: `public static double KnownFields.AttributeWeight(string key)` — returns the key's scoring/influence multiplier (default `1.0`); `public static readonly IReadOnlySet<string> KnownFields.RecommendedKeys`; `public const string KnownFields.ReviewKey = "review"`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Tests/Search/KnownFieldsTests.cs
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests.Search;

public class KnownFieldsTests
{
    [Fact]
    public void AttributeWeight_IsParityOneForKnownAndUnknownKeys()
    {
        // Parity: the table exists but starts neutral — known and arbitrary keys weigh the
        // same (1.0) so this change alters no existing score. Curation values come later.
        KnownFields.AttributeWeight("person").Should().Be(1.0);
        KnownFields.AttributeWeight("status").Should().Be(1.0);   // arbitrary key
        KnownFields.AttributeWeight("PERSON").Should().Be(1.0);   // case-insensitive
    }

    [Fact]
    public void RecommendedKeys_AreTheGuidedSet()
    {
        KnownFields.RecommendedKeys.Should().BeEquivalentTo(
            new[] { "person", "place", "org", "thing", "topic", "file", "ref", "review" });
    }

    [Fact]
    public void ReviewKey_IsReview()
    {
        KnownFields.ReviewKey.Should().Be("review");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~KnownFieldsTests`
Expected: FAIL — `AttributeWeight` / `RecommendedKeys` / `ReviewKey` do not exist (CS1061 / CS0117).

- [ ] **Step 3: Write minimal implementation**

Add to `KnownFields` (keep the existing `EntityKeys`, `ReferenceKeys`, `MaxFields`, `IsMultiValued` members unchanged):

```csharp
    public const string ReviewKey = "review";

    /// <summary>The guided attribute keys — what the guide teaches and agents write most.
    /// The engine treats any key uniformly; this set is guidance + the weight-table seed, not a gate.</summary>
    public static readonly IReadOnlySet<string> RecommendedKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "person", "place", "org", "thing", "topic", "file", "ref", "review" };

    // Per-attribute-key scoring/influence multiplier. This is the curation mechanism that
    // replaces the old "only EntityKeys graph" gate: a soft, tunable knob. Seeded at parity
    // (every key 1.0) so it changes no existing score today; tune values later on
    // real-workflow signal, never a benchmark fit. Consumed by WeightedFieldScorer now and
    // by the entity graph in its separate tuning effort.
    private static readonly IReadOnlyDictionary<string, double> AttributeWeights =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            // (intentionally empty at parity — add entries here to tune, e.g. ["person"] = 1.5)
        };

    public static double AttributeWeight(string key) =>
        AttributeWeights.TryGetValue(key, out double w) ? w : 1.0;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~KnownFieldsTests`
Expected: PASS (3/3).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/KnownFields.cs tests/Scrinia.Tests/Search/KnownFieldsTests.cs
git commit -F- <<'EOF'
feat(core): add per-attribute-key weight table to KnownFields (parity)

Introduces AttributeWeight(key) (the curation knob that will replace the hard
EntityKeys graph gate), RecommendedKeys (the guided set), and the ReviewKey
constant. Seeded at parity — every weight is 1.0 — so no existing score changes;
this only wires the mechanism. Tuning values and graph consumption are later.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 2: Uniform entity-graph feed

Drop the `EntityKeys` filter so **every** attribute `(key, value)` becomes a graph node — capturing relations the 5-key gate silently blocked (e.g. co-`file:` memories), type-scoped so no false cross-key links. The graph is inert on our visible workload, so this changes no measured score; it broadens what *can* relate.

**Files:**
- Modify: `src/Scrinia.Core/Search/EntityGraph.cs` (method `ParseEntityFields`, ~line 108)
- Test: `tests/Scrinia.Tests/EntityGraphTests.cs` (exists — add to the existing class)

**Interfaces:**
- Consumes: nothing new.
- Produces: `EntityGraph.ParseEntityFields(fields)` now yields a `(Type, Value)` for **every** field key (lowercased key as Type), not only `KnownFields.EntityKeys`. `ParseTypedEntities` (the legacy typed-keyword reader) is unchanged.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Tests/Search/EntityGraphTests.cs — add this test to the existing class
[Fact]
public void ParseEntityFields_YieldsEveryKey_NotJustEntityKeys()
{
    var fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["person"] = new[] { "maria" },   // a known entity key
        ["status"] = new[] { "done" },    // an arbitrary key — must ALSO become a node now
    };

    var pairs = EntityGraph.ParseEntityFields(fields).ToList();

    pairs.Should().Contain(("person", "maria"));
    pairs.Should().Contain(("status", "done"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~EntityGraphTests.ParseEntityFields_YieldsEveryKey_NotJustEntityKeys`
Expected: FAIL — `status` is filtered out today (only `EntityKeys` pass), so the second assertion fails.

- [ ] **Step 3: Write minimal implementation**

In `EntityGraph.ParseEntityFields`, remove the `KnownFields.EntityKeys.Contains(kv.Key)` guard so every key yields. Current shape (from source):

```csharp
    public static IEnumerable<(string Type, string Value)> ParseEntityFields(
        IReadOnlyDictionary<string, string[]>? fields)
    {
        if (fields is null) yield break;
        foreach (var kv in fields)
        {
            if (!KnownFields.EntityKeys.Contains(kv.Key)) continue;   // <-- DELETE this line
            ...
        }
    }
```

Delete only the `if (!KnownFields.EntityKeys.Contains(kv.Key)) continue;` line (read the method first to preserve the value-emission lines below it verbatim). Update the method's XML doc: it now yields all keys, uniformly; type is the key, so `tag:paris` and `place:paris` stay distinct nodes.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~EntityGraphTests`
Expected: PASS (new test + all existing EntityGraph tests still green).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/EntityGraph.cs tests/Scrinia.Tests/Search/EntityGraphTests.cs
git commit -F- <<'EOF'
feat(core): feed the entity graph from all attributes, not 5 blessed keys

ParseEntityFields now yields a node for every attribute (key, value), not only
KnownFields.EntityKeys. Nodes are keyed by (key, value), so type-disambiguation
holds automatically (tag:paris != place:paris). This captures relations the hard
gate blocked (e.g. co-file:/co-ref: memories) and makes curation a weight concern,
not an include/exclude list. Graph propagation internals are untouched.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 3: Weight-scale attribute scoring

Thread `KnownFields.AttributeWeight(key)` through the existing `Fields` scoring block in `WeightedFieldScorer.ScoreEntryTerm`. At parity (weights = 1.0) scores are byte-identical; the multiplier is the seam curation tuning uses later. Legacy `Tags`/`Anchors`/`Description` blocks are **left untouched** (dual-read).

**Files:**
- Modify: `src/Scrinia.Core/Search/WeightedFieldScorer.cs` (the `Fields` block in `ScoreEntryTerm`, lines ~398-414)
- Test: `tests/Scrinia.Tests/Bm25ScorerTests.cs` (exists — add to the existing class)

**Interfaces:**
- Consumes: `KnownFields.AttributeWeight(string)` (Task 1).
- Produces: attribute-value match scores in `ScoreEntryTerm` are multiplied by `AttributeWeight(field.Key)` (currently ×1.0). No signature change.

- [ ] **Step 1: Write the failing test**

`ScoreEntryTerm` is `private static`, so exercise it through the public `WeightedFieldScorer.SearchAll` path — **read `tests/Scrinia.Tests/Bm25ScorerTests.cs` first and mirror exactly how it builds candidates and calls `SearchAll`** (it constructs `ArtifactEntry` / `ScopedArtifact` candidates and asserts on the returned `SearchResult` ranking). Since weights are 1.0, this is a **parity/no-regression guard**: an entry whose only query-matching signal is a `person` attribute value must still surface for a value-matching query, exactly as it does today. Sketch (adapt names/helpers to the existing file):

```csharp
// tests/Scrinia.Tests/Bm25ScorerTests.cs — add to the existing class, reusing its
// candidate-building + SearchAll invocation helpers.
[Fact]
public void AttributeValueMatch_SurfacesEntry_AtParity()
{
    // Entry whose ONLY match for "maria" is its person attribute value.
    var entry = MakeEntryWithFields(name: "acme-note",
        fields: new() { ["person"] = new[] { "maria" } });

    var results = /* SearchAll("maria", [entry as candidate], ...) mirroring Bm25ScorerTests */;

    results.Should().Contain(r => /* result maps to "acme-note" */);
    // At weight 1.0 the value-exact contribution is the current 40 — unchanged.
}
```

If `Bm25ScorerTests` lacks a fields-carrying entry builder, add a minimal one alongside its existing helpers (do not restructure the file).

- [ ] **Step 2: Run test to verify it passes (parity guard)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Bm25ScorerTests.AttributeValueMatch_SurfacesEntry_AtParity`
Expected: PASS once it compiles. Role: a regression guard around the Step 3 edit — it must be GREEN both before and after (the ×1.0 edit changes nothing).

- [ ] **Step 3: Write minimal implementation**

In `ScoreEntryTerm`, the `Fields` block currently is:

```csharp
        if (entry.Fields is { Count: > 0 })
        {
            foreach (var field in entry.Fields)
            {
                foreach (string value in field.Value)
                {
                    if (termKey is not null
                        && field.Key.Equals(termKey, StringComparison.OrdinalIgnoreCase)
                        && value.Equals(termValue, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 50);
                    else if (value.Equals(term, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 40);
                    else if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 12);
                }
            }
        }
```

Scale each field's contribution by its key weight:

```csharp
        if (entry.Fields is { Count: > 0 })
        {
            foreach (var field in entry.Fields)
            {
                double w = KnownFields.AttributeWeight(field.Key);   // curation knob; 1.0 at parity
                foreach (string value in field.Value)
                {
                    if (termKey is not null
                        && field.Key.Equals(termKey, StringComparison.OrdinalIgnoreCase)
                        && value.Equals(termValue, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 50 * w);
                    else if (value.Equals(term, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 40 * w);
                    else if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 12 * w);
                }
            }
        }
```

Do **not** modify the legacy `Tags`, `RetrievalAnchors`, `Description`, or the legacy `key:value`-keyword blocks — they remain the dual-read path.

- [ ] **Step 4: Run tests to verify parity holds**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~WeightedFieldScorerTests`
Expected: PASS — the new parity test and every existing scorer test stay green (weights are 1.0, so scores are unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/WeightedFieldScorer.cs tests/Scrinia.Tests/Search/WeightedFieldScorerTests.cs
git commit -F- <<'EOF'
feat(core): weight-scale attribute scoring via KnownFields.AttributeWeight

The Fields scoring block now multiplies its 50/40/12 contributions by the key's
AttributeWeight (1.0 at parity, so no score changes). This is the seam curation
tuning uses later — a soft per-key knob instead of a hard key gate. Legacy
Tags/Anchors/Description scoring is untouched (dual-read until migration).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

## Verification

1. `dotnet build src/Scrinia.Core/Scrinia.Core.csproj` clean.
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` fully green — critically, **no pre-existing scorer/graph test changes its expected value** (this plan is parity-only).
3. Confirm additivity: `git diff main --stat` touches only `KnownFields.cs`, `EntityGraph.cs`, `WeightedFieldScorer.cs`, and their tests. No tool, model, or removal changes.

## Self-review (done during authoring)

- **Spec coverage:** covers the spec's "KnownFields reorg → weight table" (Task 1), "uniform graph feed / drop EntityKeys gate" (Task 2), and "attribute scoring scaled by the weight table, seeded at parity" (Task 3). Deferred by design to later plans: tool surface (attributes param, search filter, list), rename, `scri migrate`, legacy-scoring removal, guide.
- **No regression:** weights are 1.0 and no legacy path is removed, so Task 3 and Task 2 (graph inert) change no measured score; the parity test + existing suite guard this.
- **Type consistency:** `AttributeWeight(string) → double` is defined in Task 1 and consumed identically in Task 3; `RecommendedKeys`/`ReviewKey` are additive and unused by later tasks in this plan (they seed Plan 2/5).
