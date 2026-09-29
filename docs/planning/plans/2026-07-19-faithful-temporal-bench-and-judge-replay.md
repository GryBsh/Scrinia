# Faithful Temporal Grounding + Judge Replay & Ranking — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the LoCoMo benchmark measure a temporally-faithful, layer-attributable answerability signal, mirror the date surface in the production MCP `search` tool, and let any run be re-judged offline from data it persists at run time.

**Architecture:** The production `search` result block gains `created`/`updated` pseudo-attributes derived from the entry's structural date fields (display-only). Every benchmark run now assembles and **persists** the exact date-prefixed answerer context (top-JudgeK) plus the gold answer per question — whether or not a judge runs live. The judge's collapsed `Answerable` bit is split into `SurfacedFromContext` (answerer sentinel) + `AnsweredCorrectly` (grader). Because the answerer context is persisted, offline replay is a dataset-free, retriever-free re-run of the judge over saved context — no reconstruction, exact live/replay parity.

**Tech Stack:** C# / .NET 10, xUnit, System.Text.Json, PowerShell (runner scripts).

## Global Constraints

- Target framework: `net10.0`.
- Build/test with `-p:NuGetAudit=false` — `Scrinia.sln` restore otherwise fails on a pre-existing Scrinia.AppHost MessagePack NuGet-audit error unrelated to this work.
- The `created`/date value is **display/answerer metadata only** — never enters BM25 tokenization or embedding input, in product or bench.
- The answerer prompt stays **general** — no LoCoMo-specific phrasing, gold format, or relative-time coaching added.
- Date render format is ISO date-only: `created: 2023-05-03`. `updated:` line only when non-null.
- `created`/`updated` pseudo-attributes are **derived from structural `Entry.CreatedAt`/`Entry.UpdatedAt`** — never written into `Attributes`, never agent-writable.
- Answerer-context persistence is **always on** (every run, every benchmark) — no gating flag.
- `JudgeK` default is `10` (`RetrieverFactory.JudgeK`); the persisted-context K is `judge?.JudgeK ?? 10`.
- Shell is PowerShell; runner scripts are `.ps1`.
- Bench test project: `tests/Scrinia.Retrieval.Benchmarks.Tests`. MCP test project: `tests/Scrinia.Tests`.

---

### Task 1: Schema — split the judge bit + add persistence fields

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Judge/JudgePrompts.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Judge/AnswerabilityJudge.cs` (JudgeVerdict ~line 12; `JudgeAsync` tail ~line 100)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkResult.cs` (`QuestionResult`, `AggregateMetrics`)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkScorer.cs` (`Aggregate` ~line 150; empty-return)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs` (both judge-write sites — minimal rename only; Task 4 restructures)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs` (`PrintJudgeSummary` ~line 351 reads `Aggregate.Answerable`)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AnswerabilityJudgeTests.cs`, `BenchmarkScorerTests.cs`

**Interfaces:**
- Produces: `JudgePrompts.NotAnswerableSentinel` (const). `AnswerabilityJudge.IsSurfaced(string?)` (static). `JudgeVerdict(bool SurfacedFromContext, bool AnsweredCorrectly, string Answer, string GraderVerdict)`. `QuestionResult` gains `bool? SurfacedFromContext`, `bool? AnsweredCorrectly`, `bool? DateSurfaced`, `string? GoldAnswer`, `IReadOnlyList<string>? JudgeContext` (and drops `bool? Answerable`). `AggregateMetrics` gains `double SurfacedRate`, `double CorrectRate` (drops `double Answerable`).

- [ ] **Step 1: Expose the sentinel.** In `JudgePrompts.cs`, add above `AnswererSystem`:

```csharp
    public const string NotAnswerableSentinel = "NOT ANSWERABLE FROM CONTEXT";
```

Change `AnswererSystem` from a `const` to a `static readonly string` that interpolates it (so they can't drift):

```csharp
    public static readonly string AnswererSystem =
        "You answer a question using ONLY the numbered context passages provided. " +
        "Base your answer strictly on that context — do not use outside knowledge. " +
        "If the context does not contain enough information to answer, reply exactly: " +
        $"{NotAnswerableSentinel}. Otherwise give the shortest correct answer (a few words).";
```

- [ ] **Step 2: Failing test for `IsSurfaced`.** In `AnswerabilityJudgeTests.cs`:

```csharp
    [Fact]
    public void IsSurfaced_false_only_for_the_sentinel()
    {
        Assert.False(AnswerabilityJudge.IsSurfaced(JudgePrompts.NotAnswerableSentinel));
        Assert.False(AnswerabilityJudge.IsSurfaced("  not answerable from context  "));
        Assert.True(AnswerabilityJudge.IsSurfaced("Yesterday"));
        Assert.True(AnswerabilityJudge.IsSurfaced("2023-05-03"));
    }
```

- [ ] **Step 3: Run it, verify FAIL.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~IsSurfaced_false_only"` — Expected: FAIL (`IsSurfaced` undefined).

- [ ] **Step 4: Implement the split in `AnswerabilityJudge.cs`.** Replace the record:

```csharp
public sealed record JudgeVerdict(bool SurfacedFromContext, bool AnsweredCorrectly, string Answer, string GraderVerdict);
```

Add the helper near the top of the class body:

```csharp
    /// <summary>The answerer surfaced an answer iff it did not emit the not-answerable sentinel.
    /// Case-insensitive, trimmed, prefix-matched so trailing chatter still counts as not-surfaced.</summary>
    public static bool IsSurfaced(string? answer) =>
        !(answer ?? string.Empty).Trim().StartsWith(JudgePrompts.NotAnswerableSentinel, StringComparison.OrdinalIgnoreCase);
```

Change the `JudgeAsync` tail (was `bool answerable = JudgePrompts.ParseGrade(...)` / `new JudgeVerdict(answerable, answer, graderReply)`):

```csharp
        bool correct = JudgePrompts.ParseGrade(graderReply);
        Interlocked.Increment(ref _judgedCount);
        return new JudgeVerdict(IsSurfaced(answer), correct, answer, graderReply);
```

- [ ] **Step 5: Update schema records in `BenchmarkResult.cs`.** In `QuestionResult`, replace `bool? Answerable = null` with:

```csharp
    bool? SurfacedFromContext = null,
    bool? AnsweredCorrectly = null,
    bool? DateSurfaced = null,
    string? GoldAnswer = null,
    IReadOnlyList<string>? JudgeContext = null,
```

In `AggregateMetrics`, replace `double Answerable = 0.0` with:

```csharp
    double SurfacedRate = 0.0,
    double CorrectRate = 0.0);
```

Update the `AggregateMetrics`/`QuestionResult` doc-comments that mention `Answerable` to name the new fields.

- [ ] **Step 6: Minimal rename at the judge-write sites in `BenchmarkRunner.cs`.** (Task 4 restructures these; here just keep it compiling.) Replace each `scored = scored with { Answerable = verdict.Answerable, JudgeAnswer = ..., JudgeGraderVerdict = ... }` (~lines 93, 164) with:

```csharp
                    scored = scored with
                    {
                        SurfacedFromContext = verdict.SurfacedFromContext,
                        AnsweredCorrectly = verdict.AnsweredCorrectly,
                        JudgeAnswer = verdict.Answer,
                        JudgeGraderVerdict = verdict.GraderVerdict,
                    };
```

- [ ] **Step 7: Update `RetrieverFactory.PrintJudgeSummary`.** Change `result.Aggregate.Answerable` (~line 351) to `result.Aggregate.CorrectRate`; update the label from `answerable@` to `correct@` (and optionally add a `surfaced@` line reading `SurfacedRate`).

- [ ] **Step 8: Failing test for two-rate aggregation.** In `BenchmarkScorerTests.cs`:

```csharp
    [Fact]
    public void Aggregate_reports_surfaced_and_correct_rates_over_judged_only()
    {
        var q = new BenchmarkQuestion("q", "query", [], ["g"], [], "single_hop");
        var hits = new List<RetrievalHit> { new("g", "t", 1.0) };
        var correct  = BenchmarkScorer.ScoreFractionalById(q, hits) with { SurfacedFromContext = true,  AnsweredCorrectly = true };
        var wrong    = BenchmarkScorer.ScoreFractionalById(q, hits) with { SurfacedFromContext = true,  AnsweredCorrectly = false };
        var missed   = BenchmarkScorer.ScoreFractionalById(q, hits) with { SurfacedFromContext = false, AnsweredCorrectly = false };
        var unjudged = BenchmarkScorer.ScoreFractionalById(q, hits);

        var agg = BenchmarkScorer.Aggregate([correct, wrong, missed, unjudged]);

        Assert.Equal(4, agg.QuestionCount);
        Assert.Equal(2.0 / 3.0, agg.SurfacedRate, 5);
        Assert.Equal(1.0 / 3.0, agg.CorrectRate, 5);
    }
```

- [ ] **Step 9: Run it, verify FAIL.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Aggregate_reports_surfaced"` — Expected: FAIL (compile error `SurfacedRate`).

- [ ] **Step 10: Implement in `BenchmarkScorer.Aggregate`.** Replace the `answerableSum`/`judged` block and the `return` with:

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

Fix the empty-results early return `new AggregateMetrics(0, 0, 0, 0, 0)` → `new AggregateMetrics(0, 0, 0, 0, 0, 0, 0)`.

- [ ] **Step 11: Run the full bench test project, verify GREEN + commit.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false` (fix any remaining `Answerable` references the compiler flags — e.g. `RunnerJudgeIntegrationTests.cs`).

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): split judge bit into Surfaced/Correct; add persistence fields to QuestionResult"
```

---

### Task 2: Record granularity + temporal scenario in `RunMetadata`

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkResult.cs` (`RunMetadata`)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs` (`RunAsync`, `RunGroupedAsync` signatures + both `new RunMetadata(...)`)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LoCoMoCommand.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs`

**Interfaces:**
- Produces: `RunMetadata` gains `string? Granularity = null`, `string? TemporalScenario = null`. `RunAsync`/`RunGroupedAsync` gain optional `string? granularity = null, string? temporalScenario = null` before `ct`.

- [ ] **Step 1: Add nullable fields to `RunMetadata`** (after `ScriniaVersion`):

```csharp
    string ScriniaVersion,
    string? Granularity = null,
    string? TemporalScenario = null);
```

- [ ] **Step 2: Failing round-trip test.** In `RunnerJudgeIntegrationTests.cs`:

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

- [ ] **Step 3: Run it, verify FAIL.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RunMetadata_serializes_granularity"` — Expected: FAIL.

- [ ] **Step 4: Thread params through `BenchmarkRunner`.** Add to both `RunAsync` and `RunGroupedAsync` parameter lists before `CancellationToken ct = default`:

```csharp
        string? granularity = null,
        string? temporalScenario = null,
```

In both `new RunMetadata(...)` blocks:

```csharp
            ScriniaVersion: ReadScriniaVersion(),
            Granularity: granularity,
            TemporalScenario: temporalScenario);
```

- [ ] **Step 5: Pass granularity from `LoCoMoCommand`.** At the `runner.RunGroupedAsync(...)` call, add `granularity: granularity.ToString().ToLowerInvariant()` and `temporalScenario: null` (Task 4 sets the real value).

- [ ] **Step 6: Run test + build, verify GREEN + commit.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~RunMetadata_serializes_granularity"` then `dotnet build benchmarks/Scrinia.Retrieval.Benchmarks -p:NuGetAudit=false`.

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): record Granularity + TemporalScenario in RunMetadata"
```

---

### Task 3: Render `created`/`updated` pseudo-attributes in the production search block

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`AppendResultBlock` ~line 950; helper near `AppendAttributesLine` ~line 1011)
- Test: `tests/Scrinia.Tests/SearchResultDateDisplayTests.cs` (new)

**Interfaces:**
- Consumes: `ArtifactEntry.CreatedAt` (DateTimeOffset), `ArtifactEntry.UpdatedAt` (DateTimeOffset?).
- Produces: entry and chunk result blocks contain `    created: yyyy-MM-dd`, and `    updated: yyyy-MM-dd` when `UpdatedAt` non-null.

- [ ] **Step 1: Failing test.** Create `tests/Scrinia.Tests/SearchResultDateDisplayTests.cs`, reusing the store/setup pattern in `McpDisplayCutoverTests.cs`:

```csharp
    [Fact]
    public async Task Search_result_block_shows_created_pseudo_attribute()
    {
        // Arrange: store one memory whose text contains "pottery" (reuse McpDisplayCutoverTests setup).
        string body = await tools.Search(query: "pottery");
        Assert.Matches(@"created: \d{4}-\d{2}-\d{2}", body);
        Assert.DoesNotContain("\"created\"", body); // display-only, never a stored attribute key
    }
```

(Match the real `Search` signature + store setup used in `McpDisplayCutoverTests.cs`; reuse any shared fixture there.)

- [ ] **Step 2: Run it, verify FAIL.** Run: `dotnet test tests/Scrinia.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Search_result_block_shows_created"` — Expected: FAIL.

- [ ] **Step 3: Add the helper** near `AppendAttributesLine`:

```csharp
    /// <summary>Render record dates as display-only pseudo-attributes from the entry's structural
    /// CreatedAt/UpdatedAt — never stored in Attributes, never agent-writable. Date-only, ISO.</summary>
    private static void AppendDatesLine(System.Text.StringBuilder sb, DateTimeOffset createdAt, DateTimeOffset? updatedAt)
    {
        sb.Append("    created: ").Append(createdAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        if (updatedAt is { } u)
            sb.Append("    updated: ").Append(u.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
    }
```

- [ ] **Step 4: Call it from the entry + chunk arms** of `AppendResultBlock`, right after the header `" tokens\n"` line and before `AppendAttributesLine`. In `EntryResult`:

```csharp
                    AppendDatesLine(sb, er.Item.Entry.CreatedAt, er.Item.Entry.UpdatedAt);
```

In `ChunkEntryResult`:

```csharp
                    AppendDatesLine(sb, cr.ParentItem.Entry.CreatedAt, cr.ParentItem.Entry.UpdatedAt);
```

(TopicResult unchanged.)

- [ ] **Step 5: Run the test, verify PASS.** Run: `dotnet test tests/Scrinia.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Search_result_block_shows_created"` — Expected: PASS.

- [ ] **Step 6: Index-leak guard test.** Add a test that stores a memory whose text has no date, searches for its created-date string, and asserts `No matching memories found.` (proves the date didn't enter the index). Run: `dotnet test tests/Scrinia.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~SearchResultDate"` — Expected: PASS.

- [ ] **Step 7: Commit.**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/SearchResultDateDisplayTests.cs
git commit -m "feat(mcp): show created/updated pseudo-attributes on search+list result blocks"
```

---

### Task 4: Always assemble + persist the date-prefixed answerer context (id-mapped path)

The runner now builds the answerer context for **every** question and persists it (`JudgeContext` + `GoldAnswer` + `DateSurfaced`), whether or not a judge runs — this is what makes forward replay dataset-free. This task covers the id-mapped retrievers (bm25/embeddings/chunked); Task 5 makes the agentic branch return date-prefixed content so the same assembly code works there too.

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AnswererDateFormat.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs` (context assembly in `RunAsync` + `RunGroupedAsync`)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LoCoMoCommand.cs` (scenario label)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AnswererDateFormatTests.cs` (new), `RunnerJudgeIntegrationTests.cs`

**Interfaces:**
- Produces:
  - `AnswererDateFormat.Apply(string text, DateTimeOffset? date)` / `.Present(string content)` / `.Prefix` (const) — the single date-framing source, reused by Tasks 5, 6, 7.
  - `BenchmarkRunner.WithCreatedDates(IReadOnlyList<string> hitTexts, IReadOnlyList<RetrievalHit> hits, IReadOnlyDictionary<string, DateTimeOffset?> dateById)`.
  - `BenchmarkRunner.AnyDateSurfaced(IReadOnlyList<string> contents)`.
  - `BenchmarkRunner.SessionDateScenario = "session-date-ingest"`, `BenchmarkRunner.DefaultJudgeContextK = 10`.
  - `Task<IReadOnlyList<string>> BuildAnswererContextAsync(IRetriever, string query, IReadOnlyList<RetrievalHit> hits, IReadOnlyDictionary<string, DateTimeOffset?> dateById, int k, CancellationToken)` (private).

- [ ] **Step 1: Create `AnswererDateFormat` + failing test.** Create `Core/AnswererDateFormat.cs`:

```csharp
using System.Globalization;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Single source of truth for how a memory's created date is surfaced to the judge answerer,
/// mirroring the production search block's `created:` pseudo-attribute. Display/answerer only —
/// never fed to retrieval scoring. Shared by the id-mapped path (BenchmarkRunner), the agentic
/// path (AgenticScriniaRetriever), and the date-presence diagnostic so all produce identical framing.
/// </summary>
public static class AnswererDateFormat
{
    public const string Prefix = "created: ";

    public static string Apply(string text, DateTimeOffset? date) =>
        date is { } d ? $"{Prefix}{d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n{text}" : text;

    public static bool Present(string content) =>
        content.StartsWith(Prefix, StringComparison.Ordinal) || content.Contains("\n" + Prefix, StringComparison.Ordinal);
}
```

In `tests/Scrinia.Retrieval.Benchmarks.Tests/AnswererDateFormatTests.cs`:

```csharp
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

public class AnswererDateFormatTests
{
    [Fact]
    public void Apply_prefixes_known_date_and_passes_null_through()
    {
        var d = new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("created: 2023-05-04\nfoo", AnswererDateFormat.Apply("foo", d));
        Assert.Equal("foo", AnswererDateFormat.Apply("foo", null));
    }

    [Fact]
    public void Present_detects_leading_and_embedded_created_lines()
    {
        Assert.True(AnswererDateFormat.Present("created: 2023-05-04\nfoo"));
        Assert.True(AnswererDateFormat.Present("head\ncreated: 2023-05-04\nfoo"));
        Assert.False(AnswererDateFormat.Present("foo bar"));
    }
}
```

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~AnswererDateFormat"` — Expected: PASS (create the class if the first run fails to compile).

- [ ] **Step 2: Failing tests for the runner helpers.** In `RunnerJudgeIntegrationTests.cs`:

```csharp
    [Fact]
    public void WithCreatedDates_prefixes_known_and_passes_unknown_through()
    {
        var hits = new List<RetrievalHit> { new("D1:3", "I went yesterday", 1.0), new("D9:9", "unrelated", 0.5) };
        var dates = new Dictionary<string, DateTimeOffset?> { ["D1:3"] = new DateTimeOffset(2023, 5, 4, 0, 0, 0, TimeSpan.Zero) };
        var result = BenchmarkRunner.WithCreatedDates(hits.Select(h => h.Text).ToList(), hits, dates);
        Assert.Equal("created: 2023-05-04\nI went yesterday", result[0]);
        Assert.Equal("unrelated", result[1]);
    }

    [Fact]
    public void AnyDateSurfaced_true_when_any_passage_carries_a_created_line()
    {
        Assert.True(BenchmarkRunner.AnyDateSurfaced(["created: 2023-05-04\nfoo", "bar"]));
        Assert.False(BenchmarkRunner.AnyDateSurfaced(["foo", "bar"]));
    }
```

- [ ] **Step 3: Run them, verify FAIL.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~WithCreatedDates_prefixes_known|FullyQualifiedName~AnyDateSurfaced_true"` — Expected: FAIL.

- [ ] **Step 4: Implement the helpers in `BenchmarkRunner.cs`.**

```csharp
    public const string SessionDateScenario = "session-date-ingest";
    public const int DefaultJudgeContextK = 10;

    public static IReadOnlyList<string> WithCreatedDates(
        IReadOnlyList<string> hitTexts, IReadOnlyList<RetrievalHit> hits,
        IReadOnlyDictionary<string, DateTimeOffset?> dateById)
    {
        var outp = new List<string>(hitTexts.Count);
        for (int i = 0; i < hitTexts.Count; i++)
        {
            DateTimeOffset? date = i < hits.Count && dateById.TryGetValue(hits[i].Id, out var d) ? d : null;
            outp.Add(AnswererDateFormat.Apply(hitTexts[i], date));
        }
        return outp;
    }

    /// <summary>Deterministic, model-independent: did Scrinia put a resolvable created date in the answerer's context?</summary>
    public static bool AnyDateSurfaced(IReadOnlyList<string> contents)
    {
        foreach (var c in contents) if (AnswererDateFormat.Present(c)) return true;
        return false;
    }

    /// <summary>Assembles the date-prefixed answerer context for one question. Agentic retrievers return
    /// already-date-prefixed surfaced content (Task 5); id-mapped retrievers get their top-k hit texts
    /// date-prefixed here. This is the exact list persisted as JudgeContext and fed to the judge.</summary>
    private static async Task<IReadOnlyList<string>> BuildAnswererContextAsync(
        IRetriever retriever, string query, IReadOnlyList<RetrievalHit> hits,
        IReadOnlyDictionary<string, DateTimeOffset?> dateById, int k, CancellationToken ct)
    {
        if (retriever is ISurfacedContentProvider scp)
            return await scp.GetSurfacedContentsAsync(query, k, ct);
        var top = hits.Take(k).ToList();
        return WithCreatedDates(top.Select(h => h.Text).ToList(), top, dateById);
    }
```

- [ ] **Step 5: Rewrite the per-question tail in `RunGroupedAsync`** to always assemble + persist, then judge. Build the date map once per group (top of the `await foreach (var group…)` body, after ingest):

```csharp
            var dateById = group.SharedHaystack.ToDictionary(d => d.Id, d => d.Timestamp, StringComparer.OrdinalIgnoreCase);
```

Replace the per-question block (from `var hits = await retriever.RetrieveAsync(...)` through `results.Add(scored);`) with:

```csharp
                var hits = await retriever.RetrieveAsync(queryText, topK, ct);
                var scored = scorer(q, hits);

                int contextK = _judge?.JudgeK ?? DefaultJudgeContextK;
                var judgeContext = await BuildAnswererContextAsync(retriever, queryText, hits, dateById, contextK, ct);
                scored = scored with
                {
                    GoldAnswer = q.GoldAnswer,
                    JudgeContext = judgeContext,
                    DateSurfaced = AnyDateSurfaced(judgeContext),
                };

                if (_judge is not null)
                {
                    var verdict = await _judge.JudgeAsync(q.Id, q.QueryText, q.GoldAnswer, judgeContext, ct);
                    if (verdict is not null)
                        scored = scored with
                        {
                            SurfacedFromContext = verdict.SurfacedFromContext,
                            AnsweredCorrectly = verdict.AnsweredCorrectly,
                            JudgeAnswer = verdict.Answer,
                            JudgeGraderVerdict = verdict.GraderVerdict,
                        };
                }
                results.Add(scored);
```

- [ ] **Step 6: Apply the same assembly in `RunAsync`** (the per-question-haystack path for other benchmarks). Build `dateById` from the question's own haystack just before the judge area:

```csharp
            var dateById = q.Haystack.ToDictionary(d => d.Id, d => d.Timestamp, StringComparer.OrdinalIgnoreCase);
```

and replace its judge block with the same assemble-persist-then-judge shape as Step 5 (using `q` in scope). For benchmarks without timestamps, `Timestamp` is null and `WithCreatedDates` passes text through unchanged.

- [ ] **Step 7: Set the scenario label in `LoCoMoCommand`.** Change the `temporalScenario:` argument on the `RunGroupedAsync` call from `null` to `BenchmarkRunner.SessionDateScenario`.

- [ ] **Step 8: Integration test.** In `RunnerJudgeIntegrationTests.cs`, run `RunGroupedAsync` over a 1-group fixture whose docs carry a Timestamp, with **no judge**, and assert the emitted result has `Questions[0].JudgeContext` non-empty, its first entry `StartsWith("created: ")`, `Questions[0].DateSurfaced == true`, `Questions[0].GoldAnswer` populated, and `RunMetadata.TemporalScenario == "session-date-ingest"`. (Reuse the existing runner-fixture/stub-retriever pattern.)

- [ ] **Step 9: Run bench tests, verify GREEN + commit.**

```bash
dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): always persist date-prefixed JudgeContext + GoldAnswer; DateSurfaced diagnostic"
```

---

### Task 5: Return date-prefixed surfaced content from the agentic-ingest arm

`BuildAnswererContextAsync` (Task 4) routes agentic retrievers through `GetSurfacedContentsAsync`. This task makes that method date-prefix each memory with its authoring session's date, so the agentic arm — the only mode with historical judge data — is temporally faithful. Episodes run per-session; every doc in a session shares one `Timestamp`, so each agent-authored memory's created date is the session it was written during.

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs`

**Interfaces:**
- Consumes: `HaystackDoc.Timestamp`, `AnswererDateFormat.Apply`.
- Produces: `GetSurfacedContentsAsync` returns each memory's content date-prefixed with its authoring session date. Internal `_createdDateByEntryName` map, rebuilt every `ResetAsync`. `RetrieveAsync` (scorer path) unchanged.

- [ ] **Step 1: Failing tests.** In `AgenticScriniaRetrieverTests.cs`, reusing the file's existing stub-harness/episode-cache setup that ingests a session (docs carrying a `Timestamp`) and replays an episode writing a memory citing `D1:3`:

```csharp
    [Fact]
    public async Task GetSurfacedContents_prefixes_memory_with_authoring_session_date()
    {
        var contents = await retriever.GetSurfacedContentsAsync("support group", k: 5);
        Assert.NotEmpty(contents);
        Assert.All(contents, c => Assert.StartsWith("created: 2023-05-04\n", c));
    }

    [Fact]
    public async Task Retrieve_hits_are_not_date_prefixed_so_recall_is_unaffected()
    {
        var hits = await retriever.RetrieveAsync("support group", topK: 10);
        Assert.All(hits, h => Assert.DoesNotContain("created: ", h.Text));
    }
```

- [ ] **Step 2: Run them, verify FAIL.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~GetSurfacedContents_prefixes|FullyQualifiedName~Retrieve_hits_are_not_date"` — Expected: first FAILs.

- [ ] **Step 3: Add the map + reset.** Next to `_fullContentByEntryName` (~line 108):

```csharp
    // entryName -> the date of the session during which the memory was authored (its `created`).
    private Dictionary<string, DateTimeOffset?> _createdDateByEntryName = new(StringComparer.Ordinal);
```

In `ResetAsync`, next to `_fullContentByEntryName = new(...)` (~line 562):

```csharp
        _createdDateByEntryName = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
```

- [ ] **Step 4: Stamp per episode.** In `RunSessionEpisodeAsync`, move the `preEpisode` snapshot to the very top (before `_cache.TryGet`) and stamp after each kept episode. Replace from `if (_cache.TryGet(...))` through the fresh-success `return;`:

```csharp
        var preEpisode = new HashSet<string>(
            _store.ListScoped().Select(a => a.Entry.Name), StringComparer.Ordinal);

        if (_cache.TryGet(key, out var cachedCalls))
        {
            await AgenticIngestHarness.ReplayAsync(cachedCalls, ct);
            StampCreatedDates(preEpisode, sessionDocs);
            _agenticEpisodes++;
            return;
        }

        EpisodeResult result = await _harness.RunEpisodeAsync(sessionDocs, ct);
        _toolCallErrors += result.ToolCallErrors;

        if (!result.Succeeded || MadeNoToolCalls(result))
        {
            RollbackTo(preEpisode);
            result = await _harness.RunEpisodeAsync(sessionDocs, ct);
            _toolCallErrors += result.ToolCallErrors;
        }

        if (result.Succeeded && !MadeNoToolCalls(result))
        {
            _cache.Put(key, result.ToolCalls);
            StampCreatedDates(preEpisode, sessionDocs);
            _agenticEpisodes++;
            return;
        }
```

(The `RollbackTo(preEpisode)` + failed-episode tail below is unchanged.)

- [ ] **Step 5: Implement `StampCreatedDates`** near `RollbackTo`:

```csharp
    /// <summary>Records the authoring session's date as the `created` date of every memory added this
    /// episode (delta vs preEpisode). All docs in a session share one Timestamp; first-authoring wins.</summary>
    private void StampCreatedDates(HashSet<string> preEpisode, IReadOnlyList<HaystackDoc> sessionDocs)
    {
        DateTimeOffset? sessionDate = null;
        foreach (var d in sessionDocs) if (d.Timestamp is { } t) { sessionDate = t; break; }

        foreach (var a in _store.ListScoped())
            if (!preEpisode.Contains(a.Entry.Name) && !_createdDateByEntryName.ContainsKey(a.Entry.Name))
                _createdDateByEntryName[a.Entry.Name] = sessionDate;
    }
```

- [ ] **Step 6: Date-prefix surfaced contents.** In `GetSurfacedContentsAsync`, replace the final `contents.Add(...)`:

```csharp
            if (name is null || !seen.Add(name)) continue;
            string full = _fullContentByEntryName.TryGetValue(name, out var f) ? f : string.Empty;
            DateTimeOffset? date = _createdDateByEntryName.TryGetValue(name, out var dd) ? dd : null;
            contents.Add(AnswererDateFormat.Apply(full, date));
```

- [ ] **Step 7: Run tests, verify GREEN + commit.**

```bash
dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~AgenticScriniaRetriever"
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): agentic arm surfaces authoring-session created-date to the answerer"
```

---

### Task 6: Offline judge replay over persisted context (dataset-free)

Because Task 4 persists `JudgeContext` + `GoldAnswer` on every question, replay is a pure judge re-run — no dataset, no retriever, no reconstruction, exact live/replay parity. Runs produced before this plan lack `JudgeContext`; those questions are skipped and the count logged (by design — we do not target pre-existing files).

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/JudgeReplayCommand.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Program.cs` (register `judge-replay` verb)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/JudgeReplayCommandTests.cs` (new)

**Interfaces:**
- Consumes: `QuestionResult.JudgeContext`/`GoldAnswer` (Task 1), `AnswerabilityJudge`, `BenchmarkRunner.AnyDateSurfaced`/`SessionDateScenario`, `RetrieverFactory.BuildJudgeAsync`.
- Produces: `JudgeReplayCommand.RunAsync(string[] args, CancellationToken)` writing `<input>.judged.json`; `JudgeReplayCommand.ReplayAsync(BenchmarkRunResult input, AnswerabilityJudge judge, CancellationToken) : Task<BenchmarkRunResult>` (the testable core).

- [ ] **Step 1: Failing test for the core.** In `JudgeReplayCommandTests.cs`, build an in-memory `BenchmarkRunResult` with one `QuestionResult` that already has `JudgeContext = ["created: 2023-05-04\nI went yesterday"]`, `GoldAnswer = "May 4, 2023"`, and null judge fields; a stub `AnswerabilityJudge` (answerer → `"May 4, 2023"`, grader → `"correct"`; reuse the stub `IBenchLlmClient` from `AnswerabilityJudgeTests`/`RunnerJudgeIntegrationTests`):

```csharp
    [Fact]
    public async Task ReplayAsync_judges_over_persisted_context_and_sets_all_fields()
    {
        var input = OneQuestionResult(
            judgeContext: ["created: 2023-05-04\nI went yesterday"],
            goldAnswer: "May 4, 2023");
        var judge = StubJudge(answer: "May 4, 2023", grade: "correct");

        var outp = await JudgeReplayCommand.ReplayAsync(input, judge, CancellationToken.None);

        var q = outp.Questions[0];
        Assert.True(q.SurfacedFromContext);
        Assert.True(q.AnsweredCorrectly);
        Assert.True(q.DateSurfaced);
        Assert.Equal(1.0, outp.Aggregate.CorrectRate, 5);
        Assert.Equal(BenchmarkRunner.SessionDateScenario, outp.RunMetadata.TemporalScenario);
    }

    [Fact]
    public async Task ReplayAsync_skips_questions_without_persisted_context()
    {
        var input = OneQuestionResult(judgeContext: null, goldAnswer: "x"); // pre-existing file shape
        var outp = await JudgeReplayCommand.ReplayAsync(input, StubJudge("x", "correct"), CancellationToken.None);
        Assert.Null(outp.Questions[0].AnsweredCorrectly); // untouched
    }
```

- [ ] **Step 2: Run them, verify FAIL.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~ReplayAsync_"` — Expected: FAIL.

- [ ] **Step 3: Implement `JudgeReplayCommand.ReplayAsync`.**

```csharp
    public static async Task<BenchmarkRunResult> ReplayAsync(
        BenchmarkRunResult input, AnswerabilityJudge judge, CancellationToken ct)
    {
        int skipped = 0;
        var updated = new List<QuestionResult>(input.Questions.Count);
        foreach (var q in input.Questions)
        {
            if (q.JudgeContext is not { Count: > 0 } || string.IsNullOrWhiteSpace(q.GoldAnswer))
            {
                skipped++;
                updated.Add(q);
                continue;
            }

            var verdict = await judge.JudgeAsync(q.QuestionId, q.Query, q.GoldAnswer, q.JudgeContext, ct);
            updated.Add(verdict is null
                ? q
                : q with
                {
                    SurfacedFromContext = verdict.SurfacedFromContext,
                    AnsweredCorrectly = verdict.AnsweredCorrectly,
                    JudgeAnswer = verdict.Answer,
                    JudgeGraderVerdict = verdict.GraderVerdict,
                    DateSurfaced = BenchmarkRunner.AnyDateSurfaced(q.JudgeContext),
                });
        }

        if (skipped > 0)
            Console.Error.WriteLine($"[scrinia-bench:judge-replay] skipped {skipped}/{input.Questions.Count} question(s) with no persisted JudgeContext (pre-plan runs are not replay-ready).");

        var aggregate = BenchmarkScorer.Aggregate(updated);
        var perCategory = BenchmarkScorer.PerCategory(updated);
        var meta = input.RunMetadata with { TemporalScenario = BenchmarkRunner.SessionDateScenario };
        return new BenchmarkRunResult(meta, aggregate, perCategory, updated);
    }
```

- [ ] **Step 4: Run the core tests, verify GREEN.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~ReplayAsync_"` — Expected: PASS.

- [ ] **Step 5: Implement `RunAsync` (file I/O + `--judge` wiring).** Flags: `--input <result.json>` (required), judge-family flags via `RetrieverFactory.BuildJudgeAsync`. Steps: deserialize `<input>` with `Program.JsonOutputOptions`; `var judge = await RetrieverFactory.BuildJudgeAsync(args, ct);` (error out if null — replay requires a judge); `var outp = await ReplayAsync(result, judge, ct);` serialize to `<input trimmed of .json>.judged.json`. Add a small file round-trip test asserting the sibling file is written and the original is byte-unchanged.

- [ ] **Step 6: Register the verb** in `Program.cs`: add a `"judge-replay" => JudgeReplayCommand.RunAsync(rest, ct)` arm matching the existing dispatch pattern.

- [ ] **Step 7: Run tests, verify GREEN + commit.**

```bash
dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~JudgeReplay|FullyQualifiedName~Replay"
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): dataset-free judge-replay over persisted JudgeContext"
```

---

### Task 7: Ranking reader + PowerShell wrapper

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RankCommand.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Program.cs` (register `rank` verb)
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/scripts/rank-locomo.ps1`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/RankCommandTests.cs` (new)

**Interfaces:**
- Consumes: `AggregateMetrics.CorrectRate`/`SurfacedRate` (Task 1) + `RunMetadata.Granularity`/`TemporalScenario` (Task 2); tolerant read of the legacy `Answerable` field for un-rejudged historical files.
- Produces: `RankCommand.Summarize(IEnumerable<string> files) : IReadOnlyList<RankRow>` where `RankRow(string File, int QuestionCount, double CorrectRate, double SurfacedRate, string? Granularity, string? TemporalScenario)`, sorted by `CorrectRate` desc.

- [ ] **Step 1: Failing test for legacy-tolerant read + sort.** In `RankCommandTests.cs`, write two fixtures — one new-schema (`CorrectRate`/`SurfacedRate`), one legacy (`Answerable` only, value `0.42`):

```csharp
    [Fact]
    public void Summarize_reads_new_and_legacy_and_sorts_by_correct_rate_desc()
    {
        var rows = RankCommand.Summarize([legacyPath, newPath]);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].CorrectRate >= rows[1].CorrectRate);
        var legacy = rows.Single(r => r.File.EndsWith("legacy.json"));
        Assert.Equal(0.42, legacy.CorrectRate, 3);
    }
```

- [ ] **Step 2: Run it, verify FAIL.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Summarize_reads_new_and_legacy"` — Expected: FAIL.

- [ ] **Step 3: Implement `RankCommand.Summarize`.** Parse each file's `Aggregate` + `RunMetadata` with `JsonDocument` (tolerant), so a missing `CorrectRate` falls back to a legacy `Answerable` property. Define `RankRow`. Sort by `CorrectRate` desc. `RunAsync(args, ct)` globs `--dir` (default results dir) for `scrinia_locomo*.json` + `*.judged.json`, prints an aligned table: file, n, correct%, surfaced%, granularity, scenario.

- [ ] **Step 4: Run it, verify PASS.** Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests -p:NuGetAudit=false --filter "FullyQualifiedName~Summarize_reads_new_and_legacy"` — Expected: PASS.

- [ ] **Step 5: Register the verb + write the wrapper.** In `Program.cs` add the `"rank"` arm. Create `scripts/rank-locomo.ps1`:

```powershell
#requires -version 7
# Rank LoCoMo runs by judge metric. -Replay first re-judges runs that lack judge data (over persisted context).
param(
    [string]$ResultsDir = "benchmarks/Scrinia.Retrieval.Benchmarks/results",
    [switch]$Replay,
    [string]$ReplayGlob = "scrinia_locomo_bm25_embeddings__chunked_*.json"
)
$proj = "benchmarks/Scrinia.Retrieval.Benchmarks"
if ($Replay) {
    Get-ChildItem -Path $ResultsDir -Filter $ReplayGlob |
        Where-Object { $_.Name -notlike "*.judged.json" } |
        ForEach-Object { dotnet run --project $proj -c Release -- judge-replay --input $_.FullName --judge }
}
dotnet run --project $proj -c Release -- rank --dir $ResultsDir
```

(Note: `judge-replay` no longer needs `--data` — replay reads persisted context.)

- [ ] **Step 6: Smoke-run (no-replay) + commit.**

```bash
pwsh benchmarks/Scrinia.Retrieval.Benchmarks/scripts/rank-locomo.ps1
git add benchmarks/Scrinia.Retrieval.Benchmarks tests/Scrinia.Retrieval.Benchmarks.Tests
git commit -m "feat(bench): rank command + rank-locomo.ps1 (judge metric, legacy-tolerant)"
```

---

## Self-Review

**Spec coverage:**
- A (production date pseudo-attributes) → Task 3.
- B (bench session-date to answerer) → Task 4 (id-mapped: bm25/embeddings/chunked) **and** Task 5 (agentic-ingest). Date rides on `HaystackDoc.Timestamp`; surfaced into answerer context on both paths, kept out of scoring.
- C (RunMetadata granularity + scenario) → Task 2.
- D (offline judge replay) → Task 6, redesigned forward-looking: run assembles + persists `JudgeContext`+`GoldAnswer` (Task 4), replay re-judges over it dataset-free.
- E (ranking, legacy-tolerant) → Task 7.
- F (surfaced/correct three-state + rename + date-presence) → Task 1 (schema split + aggregation, `DateSurfaced`/`GoldAnswer`/`JudgeContext` fields) and Task 4 (`AnyDateSurfaced` computed + persisted for every question). Per-category three-state derivable from `PerCategory` `SurfacedRate`/`CorrectRate`; the rank table (Task 7) surfaces overall rates.

**No deferred scope.** Both answerer paths surface dates (Tasks 4, 5). Forward-looking replay (per the agreed refinement) makes replay dataset-free and gives exact live/replay parity — the earlier `LoCoMoGoldIndex` rejoin, `DateForHitId` reconstruction, and agentic content-source gap are all eliminated, not deferred. Pre-plan result files are intentionally not replay-targeted (they lack `JudgeContext`); Task 6 skips and logs those questions.

**Placeholder scan:** No TBD/TODO. The one intentional two-step is `temporalScenario` (Task 2 sets `null` so Task 2 stays independently testable; Task 4 sets the real constant).

**Type consistency:** `SurfacedFromContext`/`AnsweredCorrectly`/`DateSurfaced` (bool?), `GoldAnswer` (string?), `JudgeContext` (IReadOnlyList<string>?) defined in Task 1, consumed in Tasks 4/6. `SurfacedRate`/`CorrectRate` (double) defined Task 1, consumed Tasks 1/7. `AnswererDateFormat.Apply`/`.Present`/`.Prefix` defined Task 4, consumed Tasks 4/5. `WithCreatedDates`/`AnyDateSurfaced`/`BuildAnswererContextAsync`/`SessionDateScenario`/`DefaultJudgeContextK` defined Task 4, consumed Tasks 4/6. `StampCreatedDates`/`_createdDateByEntryName` internal to Task 5. `JudgeReplayCommand.ReplayAsync`/`RunAsync` defined/consumed in Task 6. `RankRow`/`Summarize` in Task 7.
