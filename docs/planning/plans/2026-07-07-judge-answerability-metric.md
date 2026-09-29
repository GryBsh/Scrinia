# LLM-Judge Answerability Metric Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an optional generate-then-grade LLM-judge scoring pass that measures whether each benchmark question is answerable from the top-K retrieved content, applied to both the agentic-ingest and mechanical arms so they stay comparable.

**Architecture:** The judge is a scoring post-pass orthogonal to the existing `BenchmarkScorer` R@k functions. The runner keeps computing R@k; when `--judge` is on it additionally runs an answerer call (question + retrieved content → answer) and a grader call (answer vs gold → correct/incorrect) via one held-constant judge model, and attaches an `Answerable` verdict to the `QuestionResult`. R@k is never modified — for the agentic arm it is relabeled in reporting as the "provenance lower bound."

**Tech Stack:** C# / .NET 10; existing bench infra (`IBenchLlmClient.CompleteAsync`, `AgentClientBuilder`, `LlmResponseCache`, `BenchmarkRunner`, `BenchmarkScorer`); xUnit + FluentAssertions.

## Global Constraints

- **R@k is never modified.** The judge is additive; existing scorer outputs stay byte-identical.
- **Judge models held constant across arms** — one `--judge-model` for both answerer and grader; warn (do not hard-fail) if it equals `--agent-model`.
- **Never cache a failed/empty judge call.** A null or blank completion from answerer or grader is not written to the cache and yields no verdict. Log every swallowed failure with its cause at the point it is swallowed. (`IBenchLlmClient.CompleteAsync` already returns null on transport/timeout/empty and hides `finish_reason`; the judge mitigates truncation by generous `maxTokens` — answerer 512, grader 16 — rather than reading `finish_reason`, which the interface does not expose.)
- **No ground-truth leakage into the answerer.** The answerer sees only the question + retrieved content — never the gold answer. Only the grader sees gold.
- **Metric unit for the judge is the question** — `answerable@K` = mean of the per-question boolean over *judged* questions (no-gold and failed questions are excluded, not counted as 0).
- **Datasets in scope: LongMemEval + LoCoMo.** MemBench and ConvoMem gold answers are out of scope for v1.
- **Commit trailer** on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- Build/test the bench directly: `dotnet build benchmarks/Scrinia.Retrieval.Benchmarks`, `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests`. Bench tests are flat in `tests/Scrinia.Retrieval.Benchmarks.Tests`, namespace `Scrinia.Retrieval.Benchmarks.Tests`, xUnit + FluentAssertions. Reuse the stubbed-`IBenchLlmClient` pattern from existing sibling tests (e.g. `LlmAgentHarnessTests.cs`).
- Roslyn LSP diagnostics are often stale here (CS0103 for new types until build) — trust `dotnet build`/`dotnet test`.
- The working tree has unrelated uncommitted user WIP (`LlmAgentHarness.cs`, `ScriniaRetriever.cs`, `.gitignore`, `scrinia.ts`) and an untracked `tools/` dir — never touch or stage them; each task's `git add` names only its own files.

---

## File Structure

- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkQuestion.cs` — `GoldAnswer` field.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LongMemEvalLoader.cs` — parse `answer` (string-or-list).
- `benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LoCoMoLoader.cs` — parse per-QA `answer`.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkResult.cs` — `Answerable` on `AggregateMetrics` + `QuestionResult` (+ `JudgeAnswer`, `JudgeGraderVerdict`).
- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkScorer.cs` — `Aggregate` averages `Answerable` over judged-only.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Judge/JudgePrompts.cs` (new) — answerer + grader prompts and parsers.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Judge/AnswerabilityJudge.cs` (new) — two-call judge + cache + counters.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs` — `RetrievalHit.Text` = retrieved memory content.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs` — optional judge, run after scoring.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs` — `BuildJudgeAsync` from `--judge-*`.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LoCoMoCommand.cs`, `LongMemEvalCommand.cs`, `Program.cs`, `README.md` — wiring, flags, reporting.

---

## Task 1: GoldAnswer on BenchmarkQuestion + loader parsing

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkQuestion.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LongMemEvalLoader.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LoCoMoLoader.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/GoldAnswerLoadingTests.cs` (new)

**Interfaces:**
- Produces: `BenchmarkQuestion.GoldAnswer` (`string?`, trailing optional param, default null). Null = no gold answer available (judge skips the question).

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/GoldAnswerLoadingTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class GoldAnswerLoadingTests
{
    [Fact]
    public void BenchmarkQuestion_GoldAnswer_DefaultsToNull()
    {
        var q = new BenchmarkQuestion("id", "q?", [], [], [], "cat");
        q.GoldAnswer.Should().BeNull();
    }

    [Fact]
    public void BenchmarkQuestion_GoldAnswer_CanBeSet()
    {
        var q = new BenchmarkQuestion("id", "q?", [], [], [], "cat", GoldAnswer: "42");
        q.GoldAnswer.Should().Be("42");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter GoldAnswerLoadingTests`
Expected: FAIL — `GoldAnswer` not defined.

- [ ] **Step 3: Add the GoldAnswer param**

In `Core/BenchmarkQuestion.cs`, add a trailing optional param to `BenchmarkQuestion`:

```csharp
public sealed record BenchmarkQuestion(
    string Id,
    string QueryText,
    IReadOnlyList<HaystackDoc> Haystack,
    IReadOnlyList<string> GroundTruthIds,
    IReadOnlyList<string> GroundTruthTexts,
    string Category,
    string? GoldAnswer = null);
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter GoldAnswerLoadingTests`
Expected: PASS.

- [ ] **Step 5: Parse `answer` in LongMemEvalLoader**

In `Loaders/LongMemEvalLoader.cs`, add an `answer` field to the `LongMemEvalRecord` record. Because the dataset ships it as string-or-list, read it as `System.Text.Json.JsonElement`:

```csharp
[property: JsonPropertyName("answer")] System.Text.Json.JsonElement Answer,
```

(insert after `AnswerSessionIds`; the record already has the other fields). Delete the stale "`answer` is intentionally omitted" comment above the record.

Add a static formatter to the loader class:

```csharp
private static string? FormatGoldAnswer(System.Text.Json.JsonElement answer)
{
    switch (answer.ValueKind)
    {
        case System.Text.Json.JsonValueKind.String:
            string? s = answer.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        case System.Text.Json.JsonValueKind.Array:
            var parts = new List<string>();
            foreach (var el in answer.EnumerateArray())
                if (el.ValueKind == System.Text.Json.JsonValueKind.String && el.GetString() is { } a && a.Length > 0)
                    parts.Add(a);
            return parts.Count == 0 ? null : "acceptable answers: " + string.Join("; ", parts);
        case System.Text.Json.JsonValueKind.Number:
            return answer.GetRawText();
        default:
            return null;  // Undefined (field absent) / Null / object
    }
}
```

In `LoadAsync`, set `GoldAnswer: FormatGoldAnswer(rec.Answer)` on the `yield return new BenchmarkQuestion(...)`.

- [ ] **Step 6: Parse `answer` in LoCoMoLoader**

In `Loaders/LoCoMoLoader.cs`, `ParseQa` traverses `JsonElement`. After resolving `question`, read the QA's `answer` (LoCoMo answers are string, number, or occasionally bool):

```csharp
string? goldAnswer = null;
if (qa.TryGetProperty("answer", out var ansEl))
{
    goldAnswer = ansEl.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => ansEl.GetString(),
        System.Text.Json.JsonValueKind.Number => ansEl.GetRawText(),
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        _ => null,
    };
    if (string.IsNullOrWhiteSpace(goldAnswer)) goldAnswer = null;
}
```

Pass `GoldAnswer: goldAnswer` to the `new BenchmarkQuestion(...)`.

- [ ] **Step 7: Add loader assertions**

Append to `GoldAnswerLoadingTests.cs` two tests that build each loader from a tiny temp-file fixture (copy the fixture-loading approach from the sibling `LoCoMoLoaderTests.cs` / `LongMemEvalLoaderTests.cs`), asserting: a LongMemEval record with `"answer": "Paris"` yields `GoldAnswer == "Paris"`, and a list `"answer": ["a","b"]` yields `GoldAnswer` containing `"a"` and `"b"`; a LoCoMo QA with `"answer": "5"` yields `GoldAnswer == "5"`. If replicating the fixture is impractical, assert the narrower invariant by calling `FormatGoldAnswer` on hand-built `JsonElement`s (make it `internal` + add `InternalsVisibleTo` only if not already present) — do not fabricate dataset files.

- [ ] **Step 8: Run tests and build**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter GoldAnswerLoadingTests`
Expected: PASS.
Run: `dotnet build benchmarks/Scrinia.Retrieval.Benchmarks -c Debug`
Expected: build succeeds (existing `BenchmarkQuestion(...)` call sites still compile — the new param is trailing-optional).

- [ ] **Step 9: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkQuestion.cs benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LongMemEvalLoader.cs benchmarks/Scrinia.Retrieval.Benchmarks/Loaders/LoCoMoLoader.cs tests/Scrinia.Retrieval.Benchmarks.Tests/GoldAnswerLoadingTests.cs
git commit -m "feat(bench): load gold answers for LongMemEval + LoCoMo

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Answerable fields on results + judged-only aggregation

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkResult.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkScorer.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AnswerableAggregateTests.cs` (new)

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `AggregateMetrics.Answerable` (`double` — mean over judged questions, 0 when none judged).
  - `QuestionResult.Answerable` (`bool?` — null = un-judged), `QuestionResult.JudgeAnswer` (`string?`), `QuestionResult.JudgeGraderVerdict` (`string?`), all trailing-optional so `BuildResult` and existing construction sites are unchanged; the runner attaches them via `with`.

- [ ] **Step 1: Write the failing test**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/AnswerableAggregateTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AnswerableAggregateTests
{
    private static QuestionResult Q(bool? answerable) =>
        new("id", "q", "cat", [], [], [], 0, 0, 0, 0, Answerable: answerable);

    [Fact]
    public void Aggregate_AveragesAnswerable_OverJudgedOnly()
    {
        // 2 judged (one true, one false) + 1 un-judged (null) -> 0.5, not 1/3.
        var agg = BenchmarkScorer.Aggregate([Q(true), Q(false), Q(null)]);
        agg.Answerable.Should().Be(0.5);
    }

    [Fact]
    public void Aggregate_Answerable_IsZero_WhenNoneJudged()
    {
        var agg = BenchmarkScorer.Aggregate([Q(null), Q(null)]);
        agg.Answerable.Should().Be(0.0);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AnswerableAggregateTests`
Expected: FAIL — `Answerable` not defined on the records.

- [ ] **Step 3: Add the fields**

In `Core/BenchmarkResult.cs`:

Add `Answerable` to `AggregateMetrics` (trailing):

```csharp
public sealed record AggregateMetrics(
    int QuestionCount,
    double RecallAt5,
    double RecallAt10,
    double RecallAt50,
    double Ndcg10,
    double Answerable = 0.0);
```

Add three trailing-optional fields to `QuestionResult`:

```csharp
public sealed record QuestionResult(
    string QuestionId,
    string Query,
    string Category,
    IReadOnlyList<string> GroundTruthIds,
    IReadOnlyList<string> GroundTruthTexts,
    IReadOnlyList<RetrievalHit> Retrieved,
    double RecallAt5,
    double RecallAt10,
    double RecallAt50,
    double Ndcg10,
    bool? Answerable = null,
    string? JudgeAnswer = null,
    string? JudgeGraderVerdict = null);
```

- [ ] **Step 4: Average Answerable over judged-only in Aggregate**

In `Core/BenchmarkScorer.cs`, `Aggregate`: after the existing r5/r10/r50/ndcg accumulation, add a judged-only mean and pass it through. Replace the `return new AggregateMetrics(...)` with a version computing `Answerable`:

```csharp
double answerableSum = 0; int judged = 0;
foreach (var r in results)
    if (r.Answerable is { } a) { answerableSum += a ? 1.0 : 0.0; judged++; }

int n = results.Count;
return new AggregateMetrics(
    QuestionCount: n,
    RecallAt5: r5 / n,
    RecallAt10: r10 / n,
    RecallAt50: r50 / n,
    Ndcg10: ndcg / n,
    Answerable: judged > 0 ? answerableSum / judged : 0.0);
```

(Keep the existing early-return `new AggregateMetrics(0, 0, 0, 0, 0)` for the empty case — `Answerable` defaults to 0.0.)

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AnswerableAggregateTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkResult.cs benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkScorer.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AnswerableAggregateTests.cs
git commit -m "feat(bench): Answerable metric fields + judged-only aggregation

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Judge prompts

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Judge/JudgePrompts.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/JudgePromptsTests.cs` (new)

**Interfaces:**
- Produces:

  ```csharp
  namespace Scrinia.Retrieval.Benchmarks.Judge;

  public static class JudgePrompts
  {
      public const string AnswererSystem;                       // answer from context only; say if not present
      public static string AnswererUser(string question, IReadOnlyList<string> retrievedContents);
      public const string GraderSystem;                         // output exactly "correct" or "incorrect"
      public static string GraderUser(string question, string candidateAnswer, string goldAnswer);
      public static bool ParseGrade(string? raw);               // true iff the reply is/contains "correct" (not "incorrect")
  }
  ```

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/JudgePromptsTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Judge;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class JudgePromptsTests
{
    [Fact]
    public void AnswererUser_IncludesQuestionAndEveryRetrievedItem()
    {
        string u = JudgePrompts.AnswererUser("Where did Maria go?", ["Maria booked Lisbon.", "Flight on Tuesday."]);
        u.Should().Contain("Where did Maria go?");
        u.Should().Contain("Maria booked Lisbon.").And.Contain("Flight on Tuesday.");
    }

    [Fact]
    public void GraderUser_IncludesAllThreeInputs()
    {
        string u = JudgePrompts.GraderUser("Q?", "Lisbon", "Lisbon, Portugal");
        u.Should().Contain("Q?").And.Contain("Lisbon").And.Contain("Lisbon, Portugal");
    }

    [Theory]
    [InlineData("correct", true)]
    [InlineData("CORRECT", true)]
    [InlineData("incorrect", false)]
    [InlineData("The answer is correct.", true)]
    [InlineData("This is incorrect", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseGrade_MapsVerdict(string? raw, bool expected)
        => JudgePrompts.ParseGrade(raw).Should().Be(expected);
}
```

Note the `"This is incorrect"` case: `ParseGrade` must check for `incorrect` BEFORE `correct` (since "incorrect" contains "correct" as a substring). Implement by testing `incorrect` first.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter JudgePromptsTests`
Expected: FAIL — type not defined.

- [ ] **Step 3: Implement JudgePrompts**

```csharp
using System.Text;

namespace Scrinia.Retrieval.Benchmarks.Judge;

public static class JudgePrompts
{
    public const string AnswererSystem =
        "You answer a question using ONLY the numbered context passages provided. " +
        "Base your answer strictly on that context — do not use outside knowledge. " +
        "If the context does not contain enough information to answer, reply exactly: " +
        "NOT ANSWERABLE FROM CONTEXT. Otherwise give the shortest correct answer (a few words).";

    public static string AnswererUser(string question, IReadOnlyList<string> retrievedContents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Context passages:");
        for (int i = 0; i < retrievedContents.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ");
            sb.AppendLine(retrievedContents[i]);
        }
        sb.AppendLine();
        sb.Append("Question: ").AppendLine(question);
        sb.Append("Answer:");
        return sb.ToString();
    }

    public const string GraderSystem =
        "You grade whether a candidate answer matches the gold answer for a question. " +
        "The candidate is correct if it conveys the same essential information as the gold answer, " +
        "even if worded differently or less completely. Minor phrasing, extra detail, or partial " +
        "specificity that is still consistent with the gold answer counts as correct. " +
        "Reply with exactly one word: correct or incorrect.";

    public static string GraderUser(string question, string candidateAnswer, string goldAnswer)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").AppendLine(question);
        sb.Append("Gold answer: ").AppendLine(goldAnswer);
        sb.Append("Candidate answer: ").AppendLine(candidateAnswer);
        sb.Append("Verdict (correct/incorrect):");
        return sb.ToString();
    }

    public static bool ParseGrade(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim().ToLowerInvariant();
        // Check "incorrect" first — it contains "correct" as a substring.
        if (s.Contains("incorrect")) return false;
        return s.Contains("correct");
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter JudgePromptsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Judge/JudgePrompts.cs tests/Scrinia.Retrieval.Benchmarks.Tests/JudgePromptsTests.cs
git commit -m "feat(bench): answerer + grader prompts for the answerability judge

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: AnswerabilityJudge (two-call judge + cache + counters)

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Judge/AnswerabilityJudge.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AnswerabilityJudgeTests.cs` (new)

**Interfaces:**
- Consumes: `IBenchLlmClient.CompleteAsync(systemPrompt, userPrompt, maxTokens, jsonMode, ct)` (returns `string?`, null on any failure/empty); `LlmResponseCache` (`TryGetString(op, hash, out string?)`, `PutString(op, hash, string?)`, static `Hash(string)`); `JudgePrompts` (Task 3).
- Produces:

  ```csharp
  namespace Scrinia.Retrieval.Benchmarks.Judge;

  public sealed record JudgeVerdict(bool Answerable, string Answer, string GraderVerdict);

  public sealed class AnswerabilityJudge
  {
      public AnswerabilityJudge(IBenchLlmClient llm, LlmResponseCache cache,
          string providerId, string modelSlug, int judgeK);

      public int JudgeK { get; }
      public int JudgedCount { get; }    // questions that produced a verdict
      public int NoGoldCount { get; }    // skipped: no gold answer
      public int FailedCount { get; }    // skipped: a judge call failed/blank

      // Returns null when the question has no gold answer OR a judge call failed
      // (both excluded from the aggregate; the reason is logged). Non-null = a real verdict.
      public Task<JudgeVerdict?> JudgeAsync(
          string questionId, string question, string? goldAnswer,
          IReadOnlyList<string> retrievedContents, CancellationToken ct);
  }
  ```

**Behavior:**
- No gold (`string.IsNullOrWhiteSpace(goldAnswer)`): increment `NoGoldCount`, return null (no log spam — this is expected for un-answered datasets).
- Take the top-`JudgeK` of `retrievedContents`, **distinct** (preserve order, drop exact-duplicate strings and blanks). If none remain, still run the answerer (it will say NOT ANSWERABLE).
- **Answerer**: cache op `"answer"`, key `Hash(modelSlug + "|" + question + "|" + JudgeK + "|" + string.Join("", distinctContents))`. On cache miss call `CompleteAsync(JudgePrompts.AnswererSystem, JudgePrompts.AnswererUser(question, distinctContents), maxTokens: 512, jsonMode: false, ct)`. If the result is null/blank → log `[scrinia-bench:judge] answerer failed for {questionId}: null/blank completion`, increment `FailedCount`, return null, and do NOT cache. Otherwise cache the answer string.
- **Grader**: cache op `"grade"`, key `Hash(modelSlug + "|" + question + "|" + answer + "|" + goldAnswer)`. On cache miss call `CompleteAsync(JudgePrompts.GraderSystem, JudgePrompts.GraderUser(question, answer, goldAnswer), maxTokens: 16, jsonMode: false, ct)`. If null/blank → log, increment `FailedCount`, return null, do NOT cache. Otherwise cache the raw grader reply string.
- `bool answerable = JudgePrompts.ParseGrade(graderReply)`; increment `JudgedCount`; return `new JudgeVerdict(answerable, answer, graderReply)`.
- Cached-hit path uses the cached strings and still returns a verdict (increment `JudgedCount`); a cached null is impossible because failures are never cached.

- [ ] **Step 1: Write the failing tests with a scripted client**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/AnswerabilityJudgeTests.cs`. Define a `ScriptedLlmClient : IBenchLlmClient` that returns queued `string?` replies in call order (`IsAvailableAsync` → true), and tracks a `CallCount`.

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Judge;
using Scrinia.Retrieval.Benchmarks.Llm;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AnswerabilityJudgeTests : IDisposable
{
    private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), $"judge_{Guid.NewGuid():N}.ndjson");
    public void Dispose() { try { File.Delete(_cacheFile); } catch { } }

    private sealed class ScriptedLlmClient : IBenchLlmClient
    {
        private readonly Queue<string?> _replies;
        public int CallCount { get; private set; }
        public ScriptedLlmClient(params string?[] replies) => _replies = new(replies);
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<string?> CompleteAsync(string s, string u, int m, bool j, CancellationToken ct)
        { CallCount++; return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null); }
    }

    private AnswerabilityJudge Judge(ScriptedLlmClient c) =>
        new(c, new LlmResponseCache(_cacheFile), "test", "test-model", judgeK: 10);

    [Fact]
    public async Task Judge_CorrectGrade_YieldsAnswerableTrue()
    {
        var c = new ScriptedLlmClient("Lisbon", "correct");
        var v = await Judge(c).JudgeAsync("q1", "Where?", "Lisbon", ["Maria booked Lisbon."], default);
        v.Should().NotBeNull();
        v!.Answerable.Should().BeTrue();
        v.Answer.Should().Be("Lisbon");
    }

    [Fact]
    public async Task Judge_IncorrectGrade_YieldsAnswerableFalse()
    {
        var c = new ScriptedLlmClient("Paris", "incorrect");
        var v = await Judge(c).JudgeAsync("q1", "Where?", "Lisbon", ["noise"], default);
        v!.Answerable.Should().BeFalse();
    }

    [Fact]
    public async Task Judge_NoGold_ReturnsNull_AndCountsNoGold()
    {
        var c = new ScriptedLlmClient();
        var j = Judge(c);
        var v = await j.JudgeAsync("q1", "Where?", null, ["x"], default);
        v.Should().BeNull();
        j.NoGoldCount.Should().Be(1);
        c.CallCount.Should().Be(0, "no gold means no judge calls");
    }

    [Fact]
    public async Task Judge_AnswererFailure_ReturnsNull_CountsFailed_NotCached()
    {
        var j = Judge(new ScriptedLlmClient((string?)null));
        var v = await j.JudgeAsync("q1", "Where?", "Lisbon", ["x"], default);
        v.Should().BeNull();
        j.FailedCount.Should().Be(1);
        // A fresh judge over the same inputs re-attempts (nothing was cached).
        var j2 = Judge(new ScriptedLlmClient("Lisbon", "correct"));
        (await j2.JudgeAsync("q1", "Where?", "Lisbon", ["x"], default))!.Answerable.Should().BeTrue();
    }

    [Fact]
    public async Task Judge_CachesVerdict_NoSecondLlmCall_OnIdenticalInputs()
    {
        _ = await Judge(new ScriptedLlmClient("Lisbon", "correct"))
            .JudgeAsync("q1", "Where?", "Lisbon", ["Maria booked Lisbon."], default);
        var spy = new ScriptedLlmClient(); // would return null if called
        var v = await Judge(spy).JudgeAsync("q1", "Where?", "Lisbon", ["Maria booked Lisbon."], default);
        v!.Answerable.Should().BeTrue();
        spy.CallCount.Should().Be(0, "identical inputs must hit the cache");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AnswerabilityJudgeTests`
Expected: FAIL — type not defined.

- [ ] **Step 3: Implement AnswerabilityJudge**

Implement per the Behavior spec above. Op-key constants `"answer"` / `"grade"`. Use `Interlocked.Increment` for the counters (the runner may judge concurrently in future; cheap insurance). Distinct-preserving with a `HashSet<string>` guard + a `List<string>` for order. Cache read via `_cache.TryGetString(op, hash, out var cached)`; on hit use `cached` (guaranteed non-null since failures aren't cached, but treat a cached blank defensively as a miss). Log answerer/grader failures exactly as specified.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AnswerabilityJudgeTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Judge/AnswerabilityJudge.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AnswerabilityJudgeTests.cs
git commit -m "feat(bench): AnswerabilityJudge two-call generate-then-grade + cache

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Agentic retriever surfaces memory content in RetrievalHit.Text

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs` (extend)

**Interfaces:**
- Consumes: existing `RetrievalHit(Id, Text, Score)`.
- Produces: agentic `RetrieveAsync` hits whose `Text` is the retrieved **memory's** `ContentPreview` (the content the store surfaced), while `Id` stays the doc-id. R@k is id-match, so this is R@k-neutral.

**Context:** In `RetrieveAsync`, the `switch (r)` extracts `entryName` + `score` from `EntryResult`/`ChunkEntryResult`. The entry's content is `er.Item.Entry.ContentPreview` (and `cr.ParentItem.Entry.ContentPreview`). Currently each emitted `RetrievalHit.Text` is set to `_docsById[docId].Text` (doc text). Change it to the entry's content so the judge sees the memory, not the source doc.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs` (reuse its scripted-client `MakeRetriever` helper):

```csharp
[Fact]
public async Task RetrievedHit_Text_IsMemoryContent_NotDocText()
{
    var remember = new ToolCallRequest("c1", "memory_remember",
        """{"path":"/f/x","content":["MEMORY-SUMMARY-TOKEN about Lisbon.\nref: d1"]}""");
    var (retriever, _, _) = MakeRetriever(
        new ToolTurnResult(null, [remember], "tool_calls"),
        new ToolTurnResult("done", [], "stop"));

    await retriever.IngestAsync(new HaystackDoc("d1", "RAW-DOC-TOKEN Maria booked Lisbon.", null, "s1"));
    var hits = await retriever.RetrieveAsync("Lisbon", topK: 5);

    hits.Should().NotBeEmpty();
    hits[0].Id.Should().Be("d1", "R@k still maps to the source doc id");
    hits[0].Text.Should().Contain("MEMORY-SUMMARY-TOKEN", "the judge must see the stored memory content");
    hits[0].Text.Should().NotContain("RAW-DOC-TOKEN", "not the original doc text");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter "RetrievedHit_Text_IsMemoryContent_NotDocText"`
Expected: FAIL — `Text` currently carries the doc text (`RAW-DOC-TOKEN`).

- [ ] **Step 3: Set Text to memory content**

In `RetrieveAsync`, capture the entry content in the `switch` alongside `entryName`/`score`:

```csharp
string entryName;
string entryContent;
double score;
switch (r)
{
    case EntryResult er:
        entryName = er.Item.Entry.Name;
        entryContent = er.Item.Entry.ContentPreview ?? string.Empty;
        score = er.Score;
        break;
    case ChunkEntryResult cr:
        entryName = cr.ParentItem.Entry.Name;
        entryContent = cr.ParentItem.Entry.ContentPreview ?? string.Empty;
        score = cr.Score;
        break;
    default:
        continue;
}
```

Then in the doc-expansion loop, change the hit's `Text` from the doc text to `entryContent`:

```csharp
hits.Add(new RetrievalHit(Id: docId, Text: entryContent, Score: score));
```

(Delete the now-unused `_docsById.TryGetValue(docId, out var doc) ? doc.Text : string.Empty` lookup that fed the old `Text`.)

- [ ] **Step 4: Run to verify it passes + no regressions**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter AgenticScriniaRetrieverTests`
Expected: PASS (new test + all existing agentic retriever tests — R@k tests use `Id`, unaffected).

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs
git commit -m "feat(bench): agentic RetrievalHit.Text carries memory content for the judge

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Wire the judge into BenchmarkRunner

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs` (new)

**Interfaces:**
- Consumes: `AnswerabilityJudge` (Task 4), `QuestionResult` with `Answerable`/`JudgeAnswer`/`JudgeGraderVerdict` (Task 2).
- Produces: `BenchmarkRunner` accepts an optional judge and attaches verdicts. Add an optional constructor param OR an optional method param — use a constructor param to match the existing `IAgentHarness` injection style:
  - New field `private readonly AnswerabilityJudge? _judge;`
  - New constructor overload `public BenchmarkRunner(IAgentHarness agent, AnswerabilityJudge? judge) : this(agent) { _judge = judge; }` (keep existing constructors; `_judge` defaults null).

**Behavior:** In both `RunAsync` and `RunGroupedAsync`, after `var scored = scorer(q, hits);` and before adding to `results`, if `_judge is not null`:
```csharp
var verdict = await _judge.JudgeAsync(
    q.Id, q.QueryText, q.GoldAnswer,
    hits.Select(h => h.Text).ToList(), ct);
if (verdict is not null)
    scored = scored with { Answerable = verdict.Answerable, JudgeAnswer = verdict.Answer, JudgeGraderVerdict = verdict.GraderVerdict };
```
(The judge internally takes the top-`JudgeK` distinct contents, so passing the full hit list is fine.)

- [ ] **Step 1: Write the failing integration test**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs`. Use the scripted `IBenchLlmClient` from Task 4 (copy the small `ScriptedLlmClient` helper or make it a shared internal test helper) to build an `AnswerabilityJudge`, a trivial in-memory `IRetriever` stub returning fixed hits, and one `BenchmarkQuestion` with a `GoldAnswer`. Assert the resulting `QuestionResult.Answerable` is populated and R@k is unchanged vs a no-judge run.

```csharp
// Sketch:
// - stub retriever returns [ RetrievalHit("d1", "Maria booked Lisbon.", 1.0) ]
// - question GroundTruthIds=["d1"], GoldAnswer="Lisbon"
// - judge scripted: answerer "Lisbon", grader "correct"
// - run via RunAsync with ScoreAnyHitById
// result.Questions[0].Answerable.Should().BeTrue();
// result.Questions[0].RecallAt10.Should().Be(1.0);   // R@k intact
// result.Aggregate.Answerable.Should().Be(1.0);
```

Provide the full stub `IRetriever` (IngestAsync/RetrieveAsync/ResetAsync/DisposeAsync no-ops returning the fixed hits) and a `ToAsync` helper inline, mirroring existing runner-touching tests.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter RunnerJudgeIntegrationTests`
Expected: FAIL — the judge constructor overload / wiring doesn't exist yet.

- [ ] **Step 3: Implement the wiring**

Add the field, constructor overload, and the post-scoring judge block in both `RunAsync` and `RunGroupedAsync` as specified above.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter RunnerJudgeIntegrationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs
git commit -m "feat(bench): run the answerability judge as a post-scoring pass

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: CLI wiring, reporting, and docs

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LoCoMoCommand.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LongMemEvalCommand.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Program.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/README.md`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/JudgeCliTests.cs` (new)

**Interfaces:**
- Consumes: `AgentClientBuilder.BuildAsync(provider, model, baseUrl, apiKey, ct)` → `Backend(Llm, ProviderId, ModelSlug)`; `AnswerabilityJudge` (Task 4); `BenchmarkRunner(IAgentHarness, AnswerabilityJudge?)` (Task 6).
- Produces:
  - `RetrieverFactory.JudgeK(string[] args)` → int, default 10 (internal, unit-testable).
  - `RetrieverFactory.BuildJudgeAsync(string[] args, CancellationToken ct)` → `Task<AnswerabilityJudge?>` (null when `--judge` absent).

- [ ] **Step 1: Write the failing CLI parse tests**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/JudgeCliTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Cli;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class JudgeCliTests
{
    [Fact]
    public void JudgeK_ParsesDefault10()
    {
        RetrieverFactory.JudgeK(["--x", "y"]).Should().Be(10);
        RetrieverFactory.JudgeK(["--judge-k", "20"]).Should().Be(20);
    }

    [Fact]
    public async Task BuildJudge_ReturnsNull_WhenJudgeFlagAbsent()
    {
        (await RetrieverFactory.BuildJudgeAsync(["locomo", "--data", "x.json"], default)).Should().BeNull();
    }

    [Fact]
    public void BuildJudge_RequiresProvider_WhenJudgeEnabled()
    {
        var act = async () => await RetrieverFactory.BuildJudgeAsync(["locomo", "--judge"], default);
        act.Should().ThrowAsync<ArgumentException>().WithMessage("*judge-provider*");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter JudgeCliTests`
Expected: FAIL.

- [ ] **Step 3: Implement JudgeK + BuildJudgeAsync**

In `RetrieverFactory.cs` add:

```csharp
internal static int JudgeK(string[] args) => ParseInt(Program.Flag(args, "--judge-k"), 10);

/// <summary>Builds the answerability judge when --judge is set, else null. One model
/// (via --judge-provider/--judge-model/--judge-base-url/--judge-api-key) drives both the
/// answerer and grader; warns if it equals --agent-model.</summary>
public static async Task<AnswerabilityJudge?> BuildJudgeAsync(string[] args, CancellationToken ct)
{
    if (!HasFlag(args, "--judge")) return null;

    string provider = Program.Flag(args, "--judge-provider")
        ?? throw new ArgumentException("--judge requires --judge-provider (openai | anthropic | gemini | ollama | claude-cli | ...).");
    string? model = Program.Flag(args, "--judge-model");
    string? baseUrl = Program.Flag(args, "--judge-base-url");
    string? apiKey = Program.Flag(args, "--judge-api-key");

    if (model is not null && string.Equals(model, Program.Flag(args, "--agent-model"), StringComparison.OrdinalIgnoreCase))
        Console.Error.WriteLine("[scrinia-bench] WARNING: --judge-model equals --agent-model; the model that authored the store is also judging it (self-preference risk).");

    var backend = await AgentClientBuilder.BuildAsync(provider, model, baseUrl, apiKey, ct);
    string cacheFile = Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "cache",
        $"judge-{backend.ProviderId}-{backend.ModelSlug}.ndjson");
    var judge = new AnswerabilityJudge(backend.Llm, new LlmResponseCache(cacheFile), backend.ProviderId, backend.ModelSlug, JudgeK(args));
    Console.Error.WriteLine($"[scrinia-bench] judge enabled: provider={backend.ProviderId} model={backend.ModelSlug} judgeK={JudgeK(args)} cache={cacheFile}");
    return judge;
}
```

(Add `using Scrinia.Retrieval.Benchmarks.Judge;` and `using Scrinia.Retrieval.Benchmarks.Llm;` if not present. `HasFlag`, `ParseInt`, `Program.Flag` already exist in the file.)

- [ ] **Step 4: Wire the judge into both commands + reporting**

In `LoCoMoCommand.cs` and `LongMemEvalCommand.cs`:
- After building the agent harness, build the judge: `var judge = await RetrieverFactory.BuildJudgeAsync(args, ct);`
- Pass it to the runner: `var runner = new BenchmarkRunner(agent, judge);`
- After `PrintSummary(result)`, if `judge is not null`, print an answerability line and (agentic only) relabel — add a shared helper in `RetrieverFactory`:

```csharp
public static void PrintJudgeSummary(AnswerabilityJudge judge, BenchmarkRunResult result)
{
    Console.WriteLine();
    Console.WriteLine("=== judge answerability ===");
    Console.WriteLine($"answerable@{judge.JudgeK}:  {result.Aggregate.Answerable:P1}");
    Console.WriteLine($"  judged:   {judge.JudgedCount}");
    Console.WriteLine($"  no gold:  {judge.NoGoldCount}");
    Console.WriteLine($"  failed:   {judge.FailedCount}");
}
```
Call `RetrieverFactory.PrintJudgeSummary(judge, result)` when `judge is not null`. In `PrintSummary`, when the mode label contains `agentic-ingest`, append ` (provenance lower bound)` to the R@5/R@10/R@50 header line (a one-line conditional in each command's `PrintSummary`).

- [ ] **Step 5: Help text + README**

`Program.cs` usage — add under both commands:
```
--judge                 Enable the LLM-judge answerability metric (needs --judge-provider). Works with or without --agentic-ingest.
--judge-provider <p>    Judge model provider (openai | anthropic | gemini | ollama | claude-cli | ...). Held constant across arms.
--judge-model <m>       Judge model (both answerer and grader).
--judge-base-url <u>    OpenAI-compatible endpoint for the judge.
--judge-api-key <k>     API key for the judge provider.
--judge-k <n>           Retrieved items the answerer sees (default 10).
```
`README.md` — add a "Judge answerability metric" subsection: what it measures (can the top-K retrieved content answer the question), generate-then-grade, one held-constant judge model that must differ from the agent, applies to both arms for comparison, provenance-R@k relabeled as a lower bound for the agentic arm, and the cache location.

- [ ] **Step 6: Run tests and build**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests --filter JudgeCliTests`
Expected: PASS.
Run: `dotnet build benchmarks/Scrinia.Retrieval.Benchmarks -c Release`
Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LoCoMoCommand.cs benchmarks/Scrinia.Retrieval.Benchmarks/Cli/LongMemEvalCommand.cs benchmarks/Scrinia.Retrieval.Benchmarks/Program.cs benchmarks/Scrinia.Retrieval.Benchmarks/README.md tests/Scrinia.Retrieval.Benchmarks.Tests/JudgeCliTests.cs
git commit -m "feat(bench): --judge CLI wiring, answerability reporting, docs

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Full-suite green + manual verification note

**Files:**
- Test: full `tests/Scrinia.Retrieval.Benchmarks.Tests` suite.

- [ ] **Step 1: Full suite**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests`
Expected: all green (new judge tests + existing). Note: 2 pre-existing `AgentPromptsTests.ParseTypedKeywords_*` failures may exist from unrelated uncommitted user WIP — confirm they are the ONLY failures and are not in any `Judge`/`Answerable`/`GoldAnswer`/`RunnerJudge` test.

- [ ] **Step 2: Manual verification (not automated)**

Document in the commit message / PR that real-endpoint judging is verified manually, e.g.:
```powershell
./scripts/run-benchmarks.ps1 -Benchmark locomo -ExtraArgs "--agentic-ingest --agent-provider openai --agent-base-url http://100.73.66.47:8080/v1 --agent-model qwen3 --granularity dialog --judge --judge-provider openai --judge-base-url http://100.73.66.47:8080/v1 --judge-model qwen3 --judge-k 10 --category single-hop"
```
Confirm: the judge banner prints, `cache/judge-openai-qwen3.ndjson` fills (not with nulls), the answerability block prints with judged/no-gold/failed counts, and the agentic R@k line is relabeled as the provenance lower bound. Compare `answerable@10` against the mechanical baseline (same command minus `--agentic-ingest`).

- [ ] **Step 3: Commit (if any doc note added)**

```bash
git add -A
git commit -m "test(bench): confirm judge suite green

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review Notes

- **Spec coverage:** gold-answer loading (Task 1), Answerable metric surface + judged-only aggregation (Task 2), answerer/grader prompts (Task 3), two-call judge + cache + never-cache-failure + counters (Task 4), store-output-as-judge-input via memory content in `Text` (Task 5), runner post-pass (Task 6), CLI/flags/reporting/relabel + README (Task 7), full-suite + manual (Task 8). All spec sections map to a task.
- **Truncation rule adaptation:** the spec's "surface finish_reason=length" is not reachable through `IBenchLlmClient.CompleteAsync` (it returns `string?` and hides finish_reason); mitigated by generous `maxTokens` (answerer 512, grader 16) + never-cache-null + logging every null/blank — documented in Global Constraints and Task 4.
- **No network in unit tests:** every test uses a scripted `IBenchLlmClient` or a temp-file cache.
- **R@k invariant:** the judge only reads `hit.Text`; `Id`-based R@k is untouched, and Task 5's `Text` change is R@k-neutral for the id-match arms.
