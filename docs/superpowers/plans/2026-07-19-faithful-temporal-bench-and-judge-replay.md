# Faithful Temporal Grounding + Judge Replay & Ranking — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the LoCoMo benchmark measure a temporally-faithful, layer-attributable answerability signal, replayable offline over already-saved retrieval, and mirror the date surface in the production MCP `search` tool.

**Architecture:** The production `search` result block gains `created`/`updated` pseudo-attributes derived from the entry's structural date fields (display-only). The LoCoMo answerer context mirrors this by prefixing each surfaced passage with its dataset session date (the date already rides on `HaystackDoc.Timestamp`). The judge's collapsed `Answerable` bit is split into `SurfacedFromContext` (answerer sentinel) + `AnsweredCorrectly` (grader), giving a per-category three-state attribution. An offline replay command re-runs only the judge over saved `Retrieved` hits, rejoining gold answers and dates from `locomo10.json`. A ranking reader summarizes runs by the judge metric.

**Tech Stack:** C# / .NET 10, xUnit, System.Text.Json, PowerShell (runner scripts).

## Global Constraints

- Target framework: `net10.0`.
- Build/test the solution with `-p:NuGetAudit=false` — `Scrinia.sln` restore otherwise fails on a pre-existing Scrinia.AppHost MessagePack NuGet-audit error unrelated to this work.
- The `created`/date value is **display/answerer metadata only** — it must never enter BM25 tokenization or embedding input, in product or bench.
- The answerer prompt stays **general** — no LoCoMo-specific phrasing, gold format, or relative-time coaching added.
- Date render format is ISO date-only: `created: 2023-05-03`. `updated:` line emitted only when the value is non-null.
- `created`/`updated` pseudo-attributes are **derived from structural `Entry.CreatedAt`/`Entry.UpdatedAt`** — never written into the `Attributes` dictionary, never agent-writable.
- Shell is PowerShell; runner scripts are `.ps1`.
- Bench test project: `tests/Scrinia.Retrieval.Benchmarks.Tests`. MCP test project: `tests/Scrinia.Tests`.

---

### Task 1: Split the collapsed `Answerable` signal into surfaced + correct

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Judge/AnswerabilityJudge.cs` (JudgeVerdict record ~line 12; `JudgeAsync` return ~line 100-102)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Judge/JudgePrompts.cs` (expose the sentinel as a constant)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkResult.cs` (`QuestionResult`, `AggregateMetrics`)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs` (both judge-write sites, ~line 93 and ~line 164)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkScorer.cs` (`Aggregate`, ~line 150)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AnswerabilityJudgeTests.cs`, `tests/Scrinia.Retrieval.Benchmarks.Tests/BenchmarkScorerTests.cs`

**Interfaces:**
- Produces: `JudgePrompts.NotAnswerableSentinel` (const string). `JudgeVerdict(bool SurfacedFromContext, bool AnsweredCorrectly, string Answer, string GraderVerdict)`. `QuestionResult` gains `bool? SurfacedFromContext`, `bool? AnsweredCorrectly` (replacing `bool? Answerable`). `AggregateMetrics` gains `double SurfacedRate`, `double CorrectRate` (replacing `double Answerable`).
- Consumes: nothing from later tasks.

- [ ] **Step 1: Expose the sentinel constant.** In `JudgePrompts.cs`, add above `AnswererSystem`:

```csharp
    /// <summary>The exact string the answerer emits when the context is insufficient.
    /// Used as the deterministic "not surfaced" signal, so it must match AnswererSystem verbatim.</summary>
    public const string NotAnswerableSentinel = "NOT ANSWERABLE FROM CONTEXT";
```

Then change `AnswererSystem` to interpolate it so the two can never drift:

```csharp
    public static readonly string AnswererSystem =
        "You answer a question using ONLY the numbered context passages provided. " +
        "Base your answer strictly on that context — do not use outside knowledge. " +
        "If the context does not contain enough information to answer, reply exactly: " +
        $"{NotAnswerableSentinel}. Otherwise give the shortest correct answer (a few words).";
```

- [ ] **Step 2: Add a surfaced-detection helper + failing test.** In `AnswerabilityJudgeTests.cs`, add:

```csharp
    [Fact]
    public void SurfacedFromContext_is_false_when_answer_is_the_sentinel()
    {
        Assert.False(AnswerabilityJudge.IsSurfaced(JudgePrompts.NotAnswerableSentinel));
        Assert.False(AnswerabilityJudge.IsSurfaced("  not answerable from context  "));
        Assert.True(AnswerabilityJudge.IsSurfaced("Yesterday"));
        Assert.True(AnswerabilityJudge.IsSurfaced("2023-05-03"));
    }
```

- [ ] **Step 3: Run it, verify it fails.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~SurfacedFromContext_is_false"` — Expected: FAIL (`IsSurfaced` not defined).

- [ ] **Step 4: Implement the split in `AnswerabilityJudge.cs`.** Replace the `JudgeVerdict` record and the tail of `JudgeAsync`:

```csharp
public sealed record JudgeVerdict(bool SurfacedFromContext, bool AnsweredCorrectly, string Answer, string GraderVerdict);
```

Add the static helper (place near the top of the class body):

```csharp
    /// <summary>The answerer surfaced an answer iff it did not emit the not-answerable sentinel.
    /// Case-insensitive, whitespace-trimmed, prefix-matched so trailing model chatter still counts as not-surfaced.</summary>
    public static bool IsSurfaced(string? answer) =>
        !(answer ?? string.Empty).Trim().StartsWith(JudgePrompts.NotAnswerableSentinel, StringComparison.OrdinalIgnoreCase);
```

Change the return in `JudgeAsync` (was `bool answerable = JudgePrompts.ParseGrade(...)` / `new JudgeVerdict(answerable, answer, graderReply)`):

```csharp
        bool correct = JudgePrompts.ParseGrade(graderReply);
        Interlocked.Increment(ref _judgedCount);
        return new JudgeVerdict(IsSurfaced(answer), correct, answer, graderReply);
```

- [ ] **Step 5: Update the schema records in `BenchmarkResult.cs`.** In `QuestionResult`, replace `bool? Answerable = null` with:

```csharp
    bool? SurfacedFromContext = null,
    bool? AnsweredCorrectly = null,
```

In `AggregateMetrics`, replace `double Answerable = 0.0` with:

```csharp
    double SurfacedRate = 0.0,
    double CorrectRate = 0.0);
```

Update the `AggregateMetrics` XML doc-comment sentence that mentions `Answerable` to name the two new rates.

- [ ] **Step 6: Update both judge-write sites in `BenchmarkRunner.cs`.** Replace each `scored = scored with { Answerable = verdict.Answerable, JudgeAnswer = ..., JudgeGraderVerdict = ... }` (lines ~93 and ~164) with:

```csharp
                    scored = scored with
                    {
                        SurfacedFromContext = verdict.SurfacedFromContext,
                        AnsweredCorrectly = verdict.AnsweredCorrectly,
                        JudgeAnswer = verdict.Answer,
                        JudgeGraderVerdict = verdict.GraderVerdict,
                    };
```

- [ ] **Step 7: Update `BenchmarkScorer.Aggregate` for two rates + a failing test.** In `BenchmarkScorerTests.cs` add:

```csharp
    [Fact]
    public void Aggregate_reports_surfaced_and_correct_rates_over_judged_only()
    {
        var q = new BenchmarkQuestion("q", "query", [], ["g"], [], "single_hop");
        var hits = new List<RetrievalHit> { new("g", "t", 1.0) };
        var surfacedCorrect = BenchmarkScorer.ScoreFractionalById(q, hits) with { SurfacedFromContext = true, AnsweredCorrectly = true };
        var surfacedWrong   = BenchmarkScorer.ScoreFractionalById(q, hits) with { SurfacedFromContext = true, AnsweredCorrectly = false };
        var notSurfaced     = BenchmarkScorer.ScoreFractionalById(q, hits) with { SurfacedFromContext = false, AnsweredCorrectly = false };
        var unjudged        = BenchmarkScorer.ScoreFractionalById(q, hits); // nulls

        var agg = BenchmarkScorer.Aggregate([surfacedCorrect, surfacedWrong, notSurfaced, unjudged]);

        Assert.Equal(4, agg.QuestionCount);
        Assert.Equal(2.0 / 3.0, agg.SurfacedRate, 5); // 2 of 3 judged surfaced
        Assert.Equal(1.0 / 3.0, agg.CorrectRate, 5);  // 1 of 3 judged correct
    }
```

- [ ] **Step 8: Run it, verify it fails.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Aggregate_reports_surfaced"` — Expected: FAIL (compile error on `SurfacedRate`).

- [ ] **Step 9: Implement in `BenchmarkScorer.Aggregate`.** Replace the `answerableSum`/`judged` block and the `return` with:

```csharp
        double surfacedSum = 0, correctSum = 0; int judged = 0;
        foreach (var r in results)
        {
            if (r.AnsweredCorrectly is { } c) { correctSum += c ? 1.0 : 0.0; judged++; }
            if (r.SurfacedFromContext is { } s) surfacedSum += s ? 1.0 : 0.0;
        }

        int n = results.Count;
        return new AggregateMetrics(
            QuestionCount: n,
            RecallAt5: r5 / n,
            RecallAt10: r10 / n,
            RecallAt50: r50 / n,
            Ndcg10: ndcg / n,
            SurfacedRate: judged > 0 ? surfacedSum / judged : 0.0,
            CorrectRate: judged > 0 ? correctSum / judged : 0.0);
```

Also fix the empty-results early return `new AggregateMetrics(0, 0, 0, 0, 0)` → `new AggregateMetrics(0, 0, 0, 0, 0, 0, 0)`.

- [ ] **Step 10: Run the full bench test project, verify green.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false` — Expected: PASS (fix any other references to the removed `Answerable` field the compiler flags — e.g. `RunnerJudgeIntegrationTests.cs`, `RetrieverFactory.PrintJudgeSummary`; rename reads to `AnsweredCorrectly`/`CorrectRate`).

- [ ] **Step 11: Commit.**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): split judge Answerable into SurfacedFromContext + AnsweredCorrectly"
```

---

### Task 2: Record granularity + temporal scenario in `RunMetadata`

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkResult.cs` (`RunMetadata`)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs` (`RunAsync`, `RunGroupedAsync` signatures + both `new RunMetadata(...)`)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LoCoMoCommand.cs` (pass values through)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing from later tasks.
- Produces: `RunMetadata` gains `string? Granularity = null`, `string? TemporalScenario = null`. `RunGroupedAsync`/`RunAsync` gain trailing optional params `string? granularity = null, string? temporalScenario = null`.

- [ ] **Step 1: Add nullable fields to `RunMetadata`.** Append to the record (after `ScriniaVersion`):

```csharp
    string ScriniaVersion,
    string? Granularity = null,
    string? TemporalScenario = null);
```

- [ ] **Step 2: Add a failing round-trip test.** In `RunnerJudgeIntegrationTests.cs`:

```csharp
    [Fact]
    public void RunMetadata_serializes_granularity_and_scenario_and_defaults_null()
    {
        var meta = new RunMetadata("locomo", "bm25", 50, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "v",
            Granularity: "dialog", TemporalScenario: "session-date-ingest");
        string json = JsonSerializer.Serialize(meta, Program.JsonOutputOptions);
        var back = JsonSerializer.Deserialize<RunMetadata>(json, Program.JsonOutputOptions)!;
        Assert.Equal("dialog", back.Granularity);
        Assert.Equal("session-date-ingest", back.TemporalScenario);

        var bare = new RunMetadata("membench", "bm25", 10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "v");
        Assert.Null(bare.Granularity);
        Assert.Null(bare.TemporalScenario);
    }
```

- [ ] **Step 3: Run it, verify it fails.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RunMetadata_serializes_granularity"` — Expected: FAIL (compile error, no `Granularity` param).

- [ ] **Step 4: Thread params through `BenchmarkRunner`.** Add to the end of both `RunAsync` and `RunGroupedAsync` parameter lists (after `CancellationToken ct = default`, moving `ct` last is not required — add before `ct`):

```csharp
        string? granularity = null,
        string? temporalScenario = null,
        CancellationToken ct = default)
```

In both `new RunMetadata(...)` blocks, add:

```csharp
            ScriniaVersion: ReadScriniaVersion(),
            Granularity: granularity,
            TemporalScenario: temporalScenario);
```

- [ ] **Step 5: Pass values from `LoCoMoCommand`.** At the `RunGroupedAsync` call site, pass `granularity: granularity.ToString().ToLowerInvariant()` and (placeholder for Task 4) `temporalScenario: null`. Concretely, locate the `runner.RunGroupedAsync(...)` invocation and add the named arguments before `ct`.

- [ ] **Step 6: Run tests + build the bench project.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RunMetadata_serializes_granularity"` then `dotnet build benchmarks/Scrinia.Retrieval.Benchmarks -p:NuGetAudit=false` — Expected: PASS + build succeeds.

- [ ] **Step 7: Commit.**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): record Granularity + TemporalScenario in RunMetadata"
```

---

### Task 3: Render `created`/`updated` pseudo-attributes in the production search block

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`AppendResultBlock`, ~line 950-996; add a helper near `AppendAttributesLine` ~line 1011)
- Test: `tests/Scrinia.Tests/McpDisplayCutoverTests.cs` (or a new `tests/Scrinia.Tests/SearchResultDateDisplayTests.cs`)

**Interfaces:**
- Consumes: `ArtifactEntry.CreatedAt` (DateTimeOffset), `ArtifactEntry.UpdatedAt` (DateTimeOffset?).
- Produces: search/`list` result blocks for entry and chunk results contain a `    created: yyyy-MM-dd` line, and `    updated: yyyy-MM-dd` when `UpdatedAt` is non-null.

- [ ] **Step 1: Add a failing test.** Create `tests/Scrinia.Tests/SearchResultDateDisplayTests.cs`. Follow the existing construction pattern in `McpDisplayCutoverTests.cs` for building a store, storing one entry, and calling `Search`. Assert on the returned body:

```csharp
    [Fact]
    public async Task Search_result_block_shows_created_pseudo_attribute()
    {
        // Arrange: store one memory (reuse the helper/setup McpDisplayCutoverTests uses).
        // ... store an entry whose text contains a searchable term "pottery" ...
        string body = await tools.Search(query: "pottery");

        Assert.Matches(@"created: \d{4}-\d{2}-\d{2}", body);
        // The created date must NOT appear as a stored attribute key the agent could round-trip.
        Assert.DoesNotContain("\"created\"", body);
    }
```

(Match the actual `Search` signature and store-setup used in `McpDisplayCutoverTests.cs`; if a shared fixture helper exists there, reuse it rather than re-implementing setup.)

- [ ] **Step 2: Run it, verify it fails.** Run: `dotnet test tests/Scrinia.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Search_result_block_shows_created"` — Expected: FAIL (no `created:` line).

- [ ] **Step 3: Add the render helper.** In `MemoryTools.Core.cs`, next to `AppendAttributesLine`:

```csharp
    /// <summary>Renders record dates as display-only pseudo-attributes, derived from the entry's
    /// structural CreatedAt/UpdatedAt — never stored in Attributes, never agent-writable. Date-only, ISO.</summary>
    private static void AppendDatesLine(System.Text.StringBuilder sb, DateTimeOffset createdAt, DateTimeOffset? updatedAt)
    {
        sb.Append("    created: ").Append(createdAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        if (updatedAt is { } u)
            sb.Append("    updated: ").Append(u.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
    }
```

- [ ] **Step 4: Call it from the entry and chunk arms of `AppendResultBlock`.** In the `EntryResult` case, immediately after the header `.Append("  ~").Append(estTokens).Append(" tokens\n");` line and before `AppendAttributesLine`, add:

```csharp
                    AppendDatesLine(sb, er.Item.Entry.CreatedAt, er.Item.Entry.UpdatedAt);
```

In the `ChunkEntryResult` case, in the same position before its `AppendAttributesLine`, add:

```csharp
                    AppendDatesLine(sb, cr.ParentItem.Entry.CreatedAt, cr.ParentItem.Entry.UpdatedAt);
```

(TopicResult has no single entry date — leave it unchanged.)

- [ ] **Step 5: Run the test, verify it passes.** Run: `dotnet test tests/Scrinia.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Search_result_block_shows_created"` — Expected: PASS.

- [ ] **Step 6: Guard against index leakage.** Add a test asserting the date is display-only — search by the date string must not match a memory that doesn't contain that date in its text:

```csharp
    [Fact]
    public async Task Created_date_is_display_only_not_searchable()
    {
        // Store a memory whose TEXT does not contain any date. Search for its created date string.
        string created = /* the stored entry's CreatedAt formatted yyyy-MM-dd */;
        string body = await tools.Search(query: created);
        Assert.Contains("No matching memories found.", body);
    }
```

Run: `dotnet test tests/Scrinia.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Created_date_is_display_only"` — Expected: PASS (the render change adds no index term, so this passes without further code; if it fails, the date leaked into tokenization and must be reverted out of the index path).

- [ ] **Step 7: Run the MCP display test file + commit.**

```bash
dotnet test tests/Scrinia.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Display|FullyQualifiedName~SearchResultDate"
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/SearchResultDateDisplayTests.cs
git commit -m "feat(mcp): show created/updated pseudo-attributes on search+list result blocks"
```

---

### Task 4: Surface the session date to the LoCoMo answerer (bench mirror)

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs` (build a docId→date map per group; prefix judge contents)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LoCoMoCommand.cs` (set `temporalScenario`)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs`

**Interfaces:**
- Consumes: `HaystackDoc.Timestamp` (DateTimeOffset?), `RetrievalHit.Id`, `JudgeVerdict` from Task 1, `RunGroupedAsync` scenario param from Task 2.
- Produces: `BenchmarkRunner.WithCreatedDates(IReadOnlyList<string> hitTexts, IReadOnlyList<RetrievalHit> hits, IReadOnlyDictionary<string, DateTimeOffset?> dateById)` — static, returns contents each prefixed `created: yyyy-MM-dd\n<text>` when a date is known. Scenario label constant `BenchmarkRunner.SessionDateScenario = "session-date-ingest"`.

- [ ] **Step 1: Add a failing test for the prefixing helper.** In `RunnerJudgeIntegrationTests.cs`:

```csharp
    [Fact]
    public void WithCreatedDates_prefixes_known_dates_and_passes_unknown_through()
    {
        var hits = new List<RetrievalHit> { new("D1:3", "I went yesterday", 1.0), new("D9:9", "unrelated", 0.5) };
        var texts = hits.Select(h => h.Text).ToList();
        var dates = new Dictionary<string, DateTimeOffset?> { ["D1:3"] = new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero) };

        var result = BenchmarkRunner.WithCreatedDates(texts, hits, dates);

        Assert.Equal("created: 2023-05-04\nI went yesterday", result[0]);
        Assert.Equal("unrelated", result[1]); // no date known → unchanged
    }
```

- [ ] **Step 2: Run it, verify it fails.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~WithCreatedDates_prefixes"` — Expected: FAIL (`WithCreatedDates` not defined).

- [ ] **Step 3: Implement the helper + scenario constant in `BenchmarkRunner.cs`.**

```csharp
    public const string SessionDateScenario = "session-date-ingest";

    /// <summary>Prefixes each answerer passage with its memory's ISO created date when known,
    /// mirroring the production search block's `created:` pseudo-attribute. Display/answerer only —
    /// this string is fed to the judge answerer, never to retrieval scoring. Only applies to the
    /// id-mapped hit path; the map is keyed by RetrievalHit.Id.</summary>
    public static IReadOnlyList<string> WithCreatedDates(
        IReadOnlyList<string> hitTexts,
        IReadOnlyList<RetrievalHit> hits,
        IReadOnlyDictionary<string, DateTimeOffset?> dateById)
    {
        var outp = new List<string>(hitTexts.Count);
        for (int i = 0; i < hitTexts.Count; i++)
        {
            DateTimeOffset? date = i < hits.Count && dateById.TryGetValue(hits[i].Id, out var d) ? d : null;
            outp.Add(date is { } dd
                ? $"created: {dd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}\n{hitTexts[i]}"
                : hitTexts[i]);
        }
        return outp;
    }
```

- [ ] **Step 4: Run it, verify it passes.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~WithCreatedDates_prefixes"` — Expected: PASS.

- [ ] **Step 5: Wire the date map into `RunGroupedAsync`.** At the top of the `await foreach (var group in groups...)` body, after ingest, build the map once per group:

```csharp
            var dateById = group.SharedHaystack.ToDictionary(d => d.Id, d => d.Timestamp, StringComparer.OrdinalIgnoreCase);
```

In the judge block, wrap the `judgeContents` used for the answerer. Change:

```csharp
                    var judgeContents = retriever is ISurfacedContentProvider scp
                        ? await scp.GetSurfacedContentsAsync(queryText, _judge.JudgeK, ct)
                        : hits.Select(h => h.Text).ToList();
```

to (id-mapped path only — the agentic `ISurfacedContentProvider` path is not id-mapped, so it is left untouched and documented as a follow-up):

```csharp
                    IReadOnlyList<string> judgeContents = retriever is ISurfacedContentProvider scp
                        ? await scp.GetSurfacedContentsAsync(queryText, _judge.JudgeK, ct)
                        : WithCreatedDates(hits.Select(h => h.Text).ToList(), hits, dateById);
```

- [ ] **Step 6: Set the scenario label in `LoCoMoCommand`.** Change the `temporalScenario:` argument on the `RunGroupedAsync` call from `null` (Task 2 placeholder) to `BenchmarkRunner.SessionDateScenario`.

- [ ] **Step 7: Add an integration assertion.** Extend `RunnerJudgeIntegrationTests.cs` with a test that runs `RunGroupedAsync` over a 1-group fixture with a stub judge that echoes its received contents, asserting the answerer saw a `created:`-prefixed passage and `RunMetadata.TemporalScenario == "session-date-ingest"`. (Reuse the existing stub-judge/harness pattern already in this test file.)

- [ ] **Step 8: Run bench tests, verify green + commit.**

```bash
dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): surface session created-date to the LoCoMo answerer (session-date-ingest scenario)"
```

---

### Task 5: Deterministic date-presence sub-diagnostic (temporal)

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkResult.cs` (`QuestionResult` gains `bool? DateSurfaced = null`)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs` (compute per judged question)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs`

**Interfaces:**
- Consumes: the `created:`-prefixed judge contents from Task 4.
- Produces: `QuestionResult.DateSurfaced` — true iff any answerer passage carried a `created:` line; null when not judged.

- [ ] **Step 1: Add the field.** In `QuestionResult`, after `AnsweredCorrectly`:

```csharp
    bool? DateSurfaced = null,
```

- [ ] **Step 2: Add a failing test.** In `RunnerJudgeIntegrationTests.cs`:

```csharp
    [Fact]
    public void DateSurfaced_true_when_any_passage_carries_a_created_line()
    {
        Assert.True(BenchmarkRunner.AnyDateSurfaced(["created: 2023-05-04\nfoo", "bar"]));
        Assert.False(BenchmarkRunner.AnyDateSurfaced(["foo", "bar"]));
    }
```

- [ ] **Step 3: Run it, verify it fails.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~DateSurfaced_true"` — Expected: FAIL.

- [ ] **Step 4: Implement the helper + record it.** In `BenchmarkRunner.cs`:

```csharp
    /// <summary>Deterministic, model-independent: did Scrinia put a resolvable created date into the
    /// answerer's context? Separates "surface layer put the date there" from the answerer's own judgment.</summary>
    public static bool AnyDateSurfaced(IReadOnlyList<string> judgeContents)
    {
        foreach (var c in judgeContents)
            if (c.StartsWith("created: ", StringComparison.Ordinal) || c.Contains("\ncreated: ", StringComparison.Ordinal))
                return true;
        return false;
    }
```

In the `RunGroupedAsync` judge block, after computing `judgeContents` and before/with the verdict write, set `DateSurfaced`:

```csharp
                    if (verdict is not null)
                        scored = scored with
                        {
                            SurfacedFromContext = verdict.SurfacedFromContext,
                            AnsweredCorrectly = verdict.AnsweredCorrectly,
                            JudgeAnswer = verdict.Answer,
                            JudgeGraderVerdict = verdict.GraderVerdict,
                            DateSurfaced = AnyDateSurfaced(judgeContents),
                        };
```

- [ ] **Step 5: Run tests, verify green + commit.**

```bash
dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~DateSurfaced"
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): deterministic date-presence sub-diagnostic on judged questions"
```

---

### Task 6: Offline judge replay command

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/JudgeReplayCommand.cs`
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Judge/LoCoMoGoldIndex.cs` (rejoin gold answers + session dates from `locomo10.json`)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Program.cs` (register the `judge-replay` verb)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/JudgeReplayCommandTests.cs`

**Interfaces:**
- Consumes: `LoCoMoLoader` id conventions (`session_N`, `D{n}:{i}`), `AnswerabilityJudge`, `RunGroupedAsync`'s judge-content shape, `WithCreatedDates`/`AnyDateSurfaced` from Tasks 4-5.
- Produces:
  - `LoCoMoGoldIndex.Load(string dataPath) : LoCoMoGoldIndex` with `string? GoldAnswer(string questionId)` and `DateTimeOffset? DateForHitId(string hitId)`.
  - `JudgeReplayCommand.RunAsync(string[] args, CancellationToken)` writing `<input>.judged.json`.

- [ ] **Step 1: Failing test for gold rejoin + date-by-id.** In `JudgeReplayCommandTests.cs`, write a tiny `locomo10.json` fixture (one conversation, one session with a `_date_time`, one qa with `answer` + `evidence` `dia_id`) to a temp path, then:

```csharp
    [Fact]
    public void LoCoMoGoldIndex_resolves_gold_answer_and_date_for_both_id_shapes()
    {
        string path = WriteFixture(); // helper writes the JSON described above
        var idx = LoCoMoGoldIndex.Load(path);

        Assert.Equal("May 4, 2023", idx.GoldAnswer("conv_0_qa0"));
        Assert.Equal(new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero), idx.DateForHitId("session_1"));
        Assert.Equal(new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero), idx.DateForHitId("D1:3"));
    }
```

- [ ] **Step 2: Run it, verify it fails.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~LoCoMoGoldIndex_resolves"` — Expected: FAIL.

- [ ] **Step 3: Implement `LoCoMoGoldIndex`.** Parse `locomo10.json` once, reusing `LoCoMoLoader`'s existing parsing conventions:
  - Build `questionId → goldAnswer` by iterating conversations (`sample_id` → `conv_{idx}` fallback identical to `LoCoMoLoader`) and their `qa` arrays, replicating `LoCoMoLoader`'s `qaIdx` numbering (skip questions with blank `question` or empty `evidence`, exactly as the loader does, so `_qa{N}` ids line up).
  - Build `sessionNum → date` from `session_N_date_time`.
  - `DateForHitId`: `session_N` → date of N; `D{n}:{i}` → date of n (regex `^D(\d+):(\d+)$` and `^session_(\d+)$`, mirroring `LoCoMoLoader`).

Key requirement: the `_qa{N}` numbering MUST match `LoCoMoLoader.ParseQa` exactly (same skip rules) or gold answers misalign. Factor the shared numbering into a single traversal that applies the loader's skip conditions verbatim.

- [ ] **Step 4: Run the index test, verify green.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~LoCoMoGoldIndex_resolves"` — Expected: PASS.

- [ ] **Step 5: Failing test for end-to-end replay.** Write a minimal `BenchmarkRunResult` fixture JSON (one question, two `Retrieved` hits with ids `session_1`/`D1:3`, no judge fields) + the `locomo10.json` fixture; run `JudgeReplayCommand` with a stub `IBenchLlmClient` (answerer returns `"May 4, 2023"`, grader returns `"correct"`), assert the emitted `*.judged.json` has `SurfacedFromContext=true`, `AnsweredCorrectly=true`, `DateSurfaced=true`, and the original file is unchanged.

- [ ] **Step 6: Run it, verify it fails.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~JudgeReplay"` — Expected: FAIL.

- [ ] **Step 7: Implement `JudgeReplayCommand.RunAsync`.** Flags: `--input <result.json>` (required), `--data <locomo10.json>` (required), `--judge`-family flags reused via `RetrieverFactory.BuildJudgeAsync`. Steps:
  1. Deserialize the input `BenchmarkRunResult` with `Program.JsonOutputOptions`.
  2. `var gold = LoCoMoGoldIndex.Load(dataPath);`
  3. For each `QuestionResult q`: build `judgeContents = WithCreatedDates(q.Retrieved.Select(h => h.Text).ToList(), q.Retrieved, q.Retrieved.ToDictionary(h => h.Id, h => gold.DateForHitId(h.Id)))`; call `judge.JudgeAsync(q.QuestionId, q.Query, gold.GoldAnswer(q.QuestionId), judgeContents, ct)`; on non-null verdict write the four judge fields + `DateSurfaced = AnyDateSurfaced(judgeContents)`.
  4. Recompute `Aggregate`/`PerCategory` via `BenchmarkScorer` and stamp `RunMetadata.TemporalScenario = BenchmarkRunner.SessionDateScenario` (keep existing `Granularity`).
  5. Serialize to `<input without .json>.judged.json`.

- [ ] **Step 8: Register the verb in `Program.cs`.** Add a `"judge-replay" => JudgeReplayCommand.RunAsync(rest, ct)` arm alongside the existing benchmark verbs (match the existing dispatch pattern).

- [ ] **Step 9: Run tests, verify green + commit.**

```bash
dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~JudgeReplay|FullyQualifiedName~LoCoMoGoldIndex"
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): offline judge-replay command over saved retrieval + gold rejoin"
```

---

### Task 7: Ranking reader + PowerShell wrapper

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RankCommand.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Program.cs` (register `rank` verb)
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/scripts/rank-locomo.ps1`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/RankCommandTests.cs`

**Interfaces:**
- Consumes: `BenchmarkRunResult` JSON with `AggregateMetrics.CorrectRate`/`SurfacedRate` (Task 1) + `RunMetadata.Granularity`/`TemporalScenario` (Task 2); tolerant read of the legacy `Answerable` field for un-rejudged historical files.
- Produces: `RankCommand.Summarize(IEnumerable<string> files) : IReadOnlyList<RankRow>` where `RankRow(string File, int QuestionCount, double CorrectRate, double SurfacedRate, string? Granularity, string? TemporalScenario)`, sorted by `CorrectRate` desc.

- [ ] **Step 1: Failing test for tolerant legacy read + sort.** In `RankCommandTests.cs`, write two fixtures — one new-schema (`CorrectRate`/`SurfacedRate`) and one legacy (`Answerable` only) — then:

```csharp
    [Fact]
    public void Summarize_reads_new_and_legacy_and_sorts_by_correct_rate_desc()
    {
        var rows = RankCommand.Summarize([legacyPath, newPath]);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].CorrectRate >= rows[1].CorrectRate);
        // legacy Answerable maps into CorrectRate; SurfacedRate is unknown (NaN/0 sentinel) for legacy.
        var legacy = rows.Single(r => r.File.EndsWith("legacy.json"));
        Assert.Equal(0.42, legacy.CorrectRate, 3);
    }
```

- [ ] **Step 2: Run it, verify it fails.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Summarize_reads_new_and_legacy"` — Expected: FAIL.

- [ ] **Step 3: Implement `RankCommand.Summarize`.** Parse each file's `Aggregate` and `RunMetadata` with `JsonDocument` (not the strict record) so a missing `CorrectRate` can fall back to a legacy `Answerable` property. Define `RankRow`. Sort by `CorrectRate` descending. `RunAsync(args, ct)` globs `--dir` (default the results dir) for `scrinia_locomo*.json` + `*.judged.json`, prints an aligned table with columns: file, n, correct%, surfaced%, granularity, scenario.

- [ ] **Step 4: Run it, verify it passes.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Summarize_reads_new_and_legacy"` — Expected: PASS.

- [ ] **Step 5: Register the verb + write the PowerShell wrapper.** In `Program.cs` add the `"rank"` arm. Create `scripts/rank-locomo.ps1`:

```powershell
#requires -version 7
# Rank LoCoMo runs by judge metric. Optionally replay the judge over runs that lack judge data first.
param(
    [string]$ResultsDir = "benchmarks/Scrinia.Retrieval.Benchmarks/results",
    [string]$Data       = "benchmarks/Scrinia.Retrieval.Benchmarks/data/locomo10.json",
    [switch]$Replay,
    [string]$ReplayGlob = "scrinia_locomo_bm25_embeddings__chunked_*.json"
)
$proj = "benchmarks/Scrinia.Retrieval.Benchmarks"
if ($Replay) {
    Get-ChildItem -Path $ResultsDir -Filter $ReplayGlob |
        Where-Object { $_.Name -notlike "*.judged.json" } |
        ForEach-Object {
            dotnet run --project $proj -c Release -- judge-replay --input $_.FullName --data $Data --judge
        }
}
dotnet run --project $proj -c Release -- rank --dir $ResultsDir
```

- [ ] **Step 6: Smoke-run the script (no-replay) + commit.**

```bash
pwsh benchmarks/Scrinia.Retrieval.Benchmarks/scripts/rank-locomo.ps1
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): rank command + rank-locomo.ps1 (judge metric, legacy-tolerant)"
```

---

## Self-Review

**Spec coverage:**
- A (production date pseudo-attributes) → Task 3.
- B (bench session-date seeding to answerer) → Task 4 (date already on `HaystackDoc.Timestamp`; injected into answerer contents, kept out of scoring).
- C (RunMetadata granularity + scenario) → Task 2.
- D (offline judge replay) → Task 6.
- E (ranking, legacy-tolerant) → Task 7.
- F (surfaced/correct three-state + rename + date-presence sub-diagnostic) → Task 1 (schema split + aggregation) and Task 5 (deterministic date-presence). Per-category three-state is derivable from `PerCategory` `SurfacedRate`/`CorrectRate`; the ranking table (Task 7) surfaces overall rates. If an explicit not-surfaced/surfaced-but-wrong/correct per-category breakdown table is wanted in CLI output, it is a thin add on `PerCategory` — noted, not separately tasked.

**Known scoping limit (documented, not silent):** Task 4 surfaces dates only on the **id-mapped** retriever path (bm25/embeddings/chunked). The agentic `ISurfacedContentProvider` path returns memory content not keyed by hit id, so date-threading through `AgenticScriniaRetriever`/`EpisodeCache` is a larger follow-up and is intentionally out of this plan. The two historically-judged runs are agentic-ingest; they will be re-judged via Task 6 replay for the id-mapped modes, and the agentic temporal lift is a separate future plan.

**Placeholder scan:** No TBD/TODO. The one "placeholder" is the deliberate two-step wiring of `temporalScenario` (Task 2 sets `null`, Task 4 sets the real constant) — sequenced intentionally so Task 2 stays independently testable.

**Type consistency:** `SurfacedFromContext`/`AnsweredCorrectly` (bool?) and `SurfacedRate`/`CorrectRate` (double) used identically across Tasks 1, 5, 6, 7. `WithCreatedDates`/`AnyDateSurfaced`/`SessionDateScenario` defined in Task 4/5 and consumed in Task 6. `LoCoMoGoldIndex.GoldAnswer`/`DateForHitId` defined and consumed within Task 6. `RankRow` defined and consumed within Task 7.
