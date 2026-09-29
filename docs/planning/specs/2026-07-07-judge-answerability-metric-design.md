# LLM-Judge Answerability Metric — Design

**Date:** 2026-07-07
**Status:** Approved (design review with user)

## Motivation

The agentic-ingest arm's provenance-mapped R@k is flawed under **consolidation**.
`AgenticScriniaRetriever` only surfaces a haystack doc if some stored memory cites it via
a `ref:` line. When the ingesting agent searches, sees a fact is already captured by an
earlier session's memory, and correctly declines to re-store it, the later gold doc never
gets a `ref:` — so that question scores 0, even though the information *is* retrievable
from the agent's store. Cross-session `memory_search` (a deliberate feature) makes this
worse: good agent behavior — deduping and consolidating — is exactly what the metric
punishes.

Provenance-R@k measures "did the agent leave a citation trail to this exact doc," not "can
the question be answered from what the store surfaced." Those diverge the moment the agent
consolidates, which is the whole point of the arm.

This design adds an **LLM-judge answerability metric**: retrieve the top-K for a question,
have a judge model answer using only that retrieved content, and grade the answer against
the dataset's gold answer. It applies to the agentic arm *and* the mechanical arms, so the
two stay comparable on one honest axis — "can questions be answered from what this ingestion
strategy produced." Provenance-R@k is kept, relabeled as a lower bound.

## Decisions (from design review)

| Question | Decision |
|---|---|
| What the judge reads | The **store's own retrieved output** — agentic *memory* contents for the agentic arm, doc text for the mechanical arm. Each arm judged on what its store surfaces. |
| Judge task | **Generate-then-grade**: an answerer produces an answer from retrieved content (no gold), then a grader compares it to gold. |
| Judge models | **One judge model, both roles** (`--judge-provider`/`--judge-model`/…), held constant across arms; warn if it equals `--agent-model`. |
| K | **Single K, default 10** (`--judge-k`). One answerable@K number. R@5/10/50 still reported separately. |
| Datasets | **LongMemEval + LoCoMo** (the id-match arms). MemBench (messy `answer`) and ConvoMem (text-match) out of scope for v1. |
| Comparability | Same judge model + judge-k across arms; answerer/grader held constant so differences reflect retrieval, not answerer competence. |

## Architecture

The judge is an **optional scoring post-pass**, orthogonal to the existing
`BenchmarkScorer` R@k functions. The runner keeps computing R@k exactly as today; when the
judge is enabled it additionally runs the judge and attaches an `Answerable` verdict to the
`QuestionResult`. R@k is never modified — for the agentic arm it is relabeled in reporting
as the "provenance lower bound."

Because the judge only needs `(question, retrieved content, gold answer)`, it is
arm-agnostic: the same code path runs on agentic and mechanical arms.

### Data flow (per question, judge enabled)

1. Runner ingests + retrieves as today → `IReadOnlyList<RetrievalHit> hits`.
2. Existing scorer → `QuestionResult` with R@k (unchanged).
3. If `--judge` and the question has a gold answer:
   a. Take the distinct `hit.Text` values from the top-`judge-k` hits — the store's
      surfaced content.
   b. **Answerer call**: `(question, retrieved contents)` → an answer (or "not answerable
      from the provided content").
   c. **Grader call**: `(question, answer, gold answer)` → `correct` / `incorrect`.
   d. Attach `Answerable` (+ the generated answer and grader verdict) to the result.
4. Aggregate: `answerable@K` = mean of `Answerable` over judged questions.

## Components

- **`Core/BenchmarkQuestion.cs`** — add trailing `string? GoldAnswer = null` to
  `BenchmarkQuestion` (keeps every existing construction site compiling).
- **`Loaders/LongMemEvalLoader.cs`** — parse the dataset `answer` (string-or-list; a list
  becomes `"acceptable answers: a; b; c"`), populate `GoldAnswer`.
- **`Loaders/LoCoMoLoader.cs`** — parse each QA's `answer`, populate `GoldAnswer`.
- **`Judge/AnswerabilityJudge.cs`** (new) — the two-call generate-then-grade judge over an
  `IBenchLlmClient` transport + a `LlmResponseCache`. Public surface:

  ```csharp
  public sealed record JudgeVerdict(bool Answerable, string Answer, string GraderVerdict);

  public sealed class AnswerabilityJudge
  {
      public AnswerabilityJudge(IBenchLlmClient llm, LlmResponseCache cache,
          string providerId, string modelSlug, int judgeK);

      // Returns null when the question has no gold answer (caller skips/excludes it).
      public Task<JudgeVerdict?> JudgeAsync(
          string question, string? goldAnswer, IReadOnlyList<string> retrievedContents,
          CancellationToken ct);
  }
  ```

- **`Judge/JudgePrompts.cs`** (new) — answerer + grader system/user prompts and their
  parsers. The answerer prompt: answer using ONLY the provided content; if the content
  doesn't contain the answer, say so explicitly. The grader prompt: LongMemEval-style —
  given question, candidate answer, and gold answer, output a single `correct`/`incorrect`.
- **`Core/BenchmarkResult.cs`** — add `Answerable` to `AggregateMetrics`; add
  `Answerable` (nullable — null = un-judged), `JudgeAnswer`, `JudgeVerdict` to
  `QuestionResult`.
- **`Core/BenchmarkScorer.cs`** — `Aggregate` averages `Answerable` over judged questions
  only (null excluded); un-judged questions don't dilute the rate.
- **`Core/BenchmarkRunner.cs`** — accept an optional `AnswerabilityJudge`; run it after
  scoring in both `RunAsync` and `RunGroupedAsync`.
- **`Core/AgenticScriniaRetriever.cs`** — set each `RetrievalHit.Text` to the retrieved
  **memory's** content (its `ContentPreview`) rather than the provenance-mapped doc text.
  `Id` stays the doc-id (R@k uses id-match, so this is R@k-neutral).
- **`Cli/RetrieverFactory.cs`** (or a sibling `JudgeFactory`) — build the judge from
  `--judge-*` flags; warn if `--judge-model` == `--agent-model`.
- **`Cli/LoCoMoCommand.cs`, `LongMemEvalCommand.cs`, `Program.cs`, `README.md`** — wire the
  flags, print `answerable@K`, relabel agentic R@k as the provenance lower bound, help text.

## `RetrievalHit.Text` semantics

`RetrievalHit.Text` is clarified to mean **"the content the store surfaced for this hit."**

- Mechanical arm: doc text (already the case; also what ConvoMem's text-match scorer wants).
- Agentic arm: the retrieved memory's content (change from provenance-mapped doc text).
  Since the agentic arm scores id-match, `Text` is unused by its R@k — repurposing it is
  safe and R@k-neutral.

The judge takes the **distinct** `Text` values across the top-`judge-k` hits (agentic
doc-hits that share a memory collapse to that one memory's content).

## Judge models and CLI

- `--judge` — enable the judge post-pass (off = zero cost, current behavior).
- `--judge-provider` / `--judge-model` / `--judge-base-url` / `--judge-api-key` — one model
  for both answerer and grader, via the existing `AgentClientBuilder` (plain completions —
  every provider works). Warn (don't hard-fail) if `--judge-model` equals `--agent-model`.
- `--judge-k <n>` — retrieved items the answerer sees; default 10.
- The judge is arm-independent — it works with or without `--agentic-ingest`, so the
  mechanical baseline can be judged with the same command minus `--agentic-ingest`.

## Caching and failure discipline

- `cache/judge-{provider}-{model}.ndjson`, `LlmResponseCache` pattern, two ops:
  - `answer` — keyed on `hash(judgeModel + question + judgeK + retrieved-contents)`. The
    retrieved-contents hash means a different arm/run (different retrieval) correctly
    re-judges rather than reusing a stale answer.
  - `grade` — keyed on `hash(judgeModel + question + answer + goldAnswer)`.
- **Hard rules (carried from the tool-call-truncation incident):**
  - A failed judge call (transport error, timeout, empty/blank completion) is **never
    cached** and never counted as a verdict.
  - Every swallowed failure is logged with its cause at the point it is swallowed.
  - A truncated grader/answerer response (`finish_reason=length`) is logged loudly, not
    silently treated as a verdict.

## Reporting

- Summary prints `answerable@K` for the arm alongside R@k.
- For the agentic arm, the R@k lines are labeled `(provenance lower bound)`.
- The agentic-vs-mechanical comparison is `answerable@K(agentic)` vs
  `answerable@K(mechanical)`.
- The gap between `answerable@K` and provenance-R@k on the agentic arm quantifies how much
  consolidation the provenance metric under-counted.
- Judge run stats: questions judged, un-judged (no gold), judge-call failures — printed so a
  high failure/skip count is visible, not hidden.

## Testing

Unit tests drive the judge with a scripted `IBenchLlmClient` (no network):

1. Answerer returns a canned answer, grader returns `correct` → `Answerable == true`;
   grader returns `incorrect` → `false`.
2. No gold answer → `JudgeAsync` returns null; runner excludes it from the aggregate.
3. Cache round-trip: a second judge over identical (question, contents, gold) hits the cache
   (spy asserts no second LLM call); a *different* retrieved-contents set re-judges.
4. Never-cache-failure: a null/blank answerer or grader completion is not written to the
   cache and yields no verdict.
5. `Aggregate` averages `Answerable` over judged questions only (null excluded).
6. Runner integration: with a scripted judge, `QuestionResult.Answerable` is populated and
   R@k is byte-identical to a no-judge run.

Real-endpoint judging stays manual (`run-benchmarks.ps1` with `--judge` + `--judge-*`).

## Out of scope (possible later)

- MemBench / ConvoMem judging (needs `answer`-shape handling / text-match arm treatment).
- Multi-K answerability (5/10/50) — deferred for cost; single K default 10.
- Graded (partial-credit) verdicts — binary for v1.
- Separate answerer vs grader models — one model for both in v1.
