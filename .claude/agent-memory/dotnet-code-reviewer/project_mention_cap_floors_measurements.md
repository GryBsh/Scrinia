---
name: project-mention-cap-floors-measurements
description: MaxMentionSpans=32 saturates the findings/ corpus, so any before/after mention measurement over it reports displacement churn, not growth — Task 6 must not reuse Task 5's ~2% number
metadata:
  type: project
---

`AttributeExtractor.MaxMentionSpans` (32) is **binding on almost every file** in the
`.scrinia/memories/findings/` corpus that gets used as the measurement sample. In Task 5's
gate measurement, 15 of 16 files sat at exactly 32 spans under *both* the old and new
algorithms (totals 500 → 500). Per-file counts therefore **cannot rise**, so an additive
change (e.g. dropping `StripInlineCode`, making inline-code identifiers newly visible) shows
up only as *which* 32 spans survive — never as how many more there would have been.

**Why:** the Task 5 brief framed this corpus as an "upper bound" on the effect because it is
backtick-dense. That is true for spans-per-line, but the cap then truncates exactly the files
where density is highest. The two pull opposite ways, and the resulting symmetric-difference
figure (8/386, ~2%) is a *lower* bound on additive growth for an unsaturated corpus, not an
upper bound. Task 5's report read the cap as "damping" the growth — that inference is
backwards for gating purposes.

**How to apply:** when any task asks for a before/after measurement of mention-span or
attribute extraction over this corpus, insist the variant arm run with the cap lifted (a local
uncapped copy of `ScanMentionLine` — `MaxMentionSpans` is a `const`, so reflection cannot
change it). Report capped and uncapped numbers side by side. Do not let a small symmetric
difference measured under the cap be cited as evidence the change is safe for ordinary
(sparse, non-code-review) memories. Task 6 routes reference extraction through the same
`ProseScanner` rule and will face the identical trap.

Corollary for reading such reports: runs never cross lines (`ScanMentionLine` calls `Emit()`
at end of line), and whitespace does not break a run while every other non-word char does. So
a **single-token** value that appears in only one arm can never be a run-split artifact — it is
cap/`seen`-ordering displacement. Use that to falsify causal stories in measurement reports.

See also [[feedback-review-method]].
