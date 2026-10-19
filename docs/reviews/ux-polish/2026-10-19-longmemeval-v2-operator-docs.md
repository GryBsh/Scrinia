# UX Polish Review — LongMemEval V2 operator documentation

**Date:** 2026-10-19
**Scope:** benchmarks/longmemeval-v2/README.md, SYSTEM_DESCRIPTION.template.md, configs/small.json, freeze_experiment.py, preflight.py, package_submission.py, run_scrinia.py; worker catalog capture help
**Reviewer:** ux-polish-reviewer agent

The operator docs clearly identify the draft configuration and packaging limits; two factual claims need correction, with one clarity fix and one optional CLI improvement.

**Must fix**

1. [README.md:34](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/longmemeval-v2/README.md:34) — The documented controller wire field is incorrect. Both `ModelClientFactory` and the live probe use `chat_template_kwargs.enable_thinking`.

   **Before:** “The controller explicitly transmits thinking=true.”

   **After:** “The controller explicitly sends `chat_template_kwargs.enable_thinking=true`.”

2. [README.md:44](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/longmemeval-v2/README.md:44) — “Full-capacity” overstates what the controller probe tests. `live_probe.py:159` targets 24,576 controller input tokens and 200,000 reader input tokens. The declared served context capacity of at least 220,000 tokens is separately operator-attested.

   **Before:** “The synthetic probe uses newly generated text/nonces, image-only digits, an actual isolated MCP list call through the controller, full-capacity context markers and exact returned model identity checks.”

   **After:** “The synthetic probe uses newly generated text/nonces, image-only digits, an isolated MCP list call through the controller and exact returned model identity checks. Context-marker probes target 24,576 controller input tokens and 200,000 reader input tokens; served context capacity of at least 220,000 tokens is operator-attested.”

   This corrects the claim; whether the tested controller target satisfies the intended readiness contract requires a separate technical review.

**Should fix**

1. [SYSTEM_DESCRIPTION.template.md:9](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/longmemeval-v2/SYSTEM_DESCRIPTION.template.md:9) — “One reader request” can imply one request for the entire experiment. The configuration controls concurrency.

   **Before:** “One prompt worker and one reader request.”

   **After:** “Prompt-build concurrency and reader-request concurrency are both one.”

**Consider**

1. [preflight.py:50](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/longmemeval-v2/preflight.py:50) — The mode flags have no explanatory help. Make the difference between offline validation and live inference visible in `--help`.

   **Before:** `--offline` — no description.  
   **After:** “Verify frozen inputs and reader request formatting without inference.”

   **Before:** `--live` — no description.  
   **After:** “Run synthetic inference and MCP probes; requires --deployment-attestation.”

**Consistency notes**

- Use exact JSON field names when describing transmitted requests.
- Keep empirical probe results, operator attestations and unresolved capabilities distinct.
- Express worker/request limits as concurrency.
- Preserve the clear draft-preset labeling and explicit local-only packaging language.
- No separate copy issues found in `configs/small.json` or the other scoped CLI descriptions. Catalog capture has no explanatory CLI help to review.
- Cited documentation text remained unchanged on the final reread. The `freeze_experiment.py` parser moved from line 227 to line 243 during review; its argument text was unchanged.
