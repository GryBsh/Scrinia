---
name: ux-polish-review
description: >-
  Dispatch the ux-polish-reviewer agent to give freshly-produced user-facing surfaces a
  professional polish pass, then save its findings as a dated report under
  docs/reviews/ux-polish/. Use this proactively whenever user-facing text or experiences have
  just been written or modified — CLI output and --help text, command banners, error/empty/
  loading states, form validation, notifications, tooltips and labels, onboarding and help text,
  README and docs sections, MCP tool/prompt descriptions, marketing/landing copy, or any string a
  real person will read — even when the user did not explicitly ask for a review. The point is to
  catch inconsistencies, unprofessional or machine-sounding copy, and unverifiable claims before
  they ship. Do NOT trigger when the change touches only internal-facing text (logs, code
  comments, test fixtures, developer-only output) or pure program logic with no reader-facing
  strings.
---

# UX Polish Review

Ship user-facing surfaces with the same care as the code behind them. This skill runs a focused
copy/UX polish pass on whatever reader-facing text was just produced and leaves a durable,
reviewable record of what it found.

It is a thin orchestration layer. The actual reviewing is done by the **`ux-polish-reviewer`**
agent, which carries its own rubric (consistency, professional presentation, de-LLMing,
hallucinated-claim detection), output format, and project memory of the product's voice and
conventions. Your job here is to scope the work, dispatch that agent, and persist its report —
not to re-implement the review.

**Why a dedicated agent instead of reviewing inline:** a fresh, single-purpose context reviews
copy more sharply than the thread that just wrote it (the author's context rationalizes its own
phrasing), it keeps the main conversation uncluttered, and the agent accumulates the product's
voice/conventions in its own memory so each pass gets more consistent than the last.

**Why document-only:** the report captures findings and concrete before/after rewrites so a human
decides what to apply. Copy is a product decision; surfacing recommendations beats silently
rewriting what users read. Applying fixes is a separate, explicit step the user can ask for.

## When to run

Run after producing or modifying any surface a real person reads — proactively, without waiting
to be asked. Typical triggers: new or edited CLI command output / `--help` text, a tool banner,
error and empty states, form validation messages, notifications and toasts, button/label/tooltip
copy, onboarding and in-app help, README or docs sections, MCP tool/prompt/resource descriptions,
and landing/marketing copy.

**Skip it** when the change is internal-only: logs, code comments, test fixtures, variable names,
or developer-facing diagnostics that no end user sees. If you are unsure whether a string is
user-facing, treat it as in-scope — a false positive costs a short review; a false negative ships
rough copy.

## Workflow

### Step 1 — Scope the surfaces

Decide exactly which reader-facing strings are under review. Prefer the smallest accurate scope —
what just changed, not the whole codebase.

- If surfaces were just produced in this conversation, scope to those files/strings directly.
- Otherwise derive the scope from version control. Inspect the uncommitted changes and the
  current branch's commits, then keep only the files that carry user-facing text:

  ```bash
  git status --porcelain
  git diff --stat            # uncommitted
  git diff --stat main...HEAD  # this branch's commits (adjust base branch if needed)
  ```

  Filter the changed-file list to user-facing surfaces (CLI command/help text, banners, README
  and `docs/**`, UI copy, string/resource files, MCP tool/prompt descriptions). Drop tests,
  logs, comments-only changes, and pure-logic files.

If nothing user-facing changed, say so plainly and stop — do not invent a review.

### Step 2 — Dispatch the ux-polish-reviewer agent

Launch the `ux-polish-reviewer` agent and hand it the scope. Give it the specific files (and, when
useful, the relevant diff or the exact strings) so it reviews the right surfaces and can verify
claims against the surrounding code. Ask it to return its standard severity-grouped report with
before/after rewrites — do not restate its rubric; it owns that.

A good dispatch prompt names the surfaces and the intent, e.g.:

> Review the user-facing copy in these freshly-changed surfaces: `<file list>`. Focus on what
> changed on this branch (see the diff). Return your standard report — one-line state, findings
> grouped by Must fix / Should fix / Consider with before/after rewrites, and a Consistency notes
> section. Verify any factual claims in the copy against the code.

Capture the agent's full report; you will persist it in the next step.

### Step 3 — Write the findings report

Save the agent's report to a dated file so the review is durable and reviewable:

```
docs/reviews/ux-polish/<YYYY-MM-DD>-<short-surface-slug>.md
```

Use the current date (`date +%Y-%m-%d`) and a short slug describing the surface (e.g.
`cli-help`, `readme`, `mcp-tool-descriptions`, `onboarding`). Create the
`docs/reviews/ux-polish/` directory if it does not exist. If a file for the same date and slug
already exists, append a short suffix (`-2`) rather than overwriting a prior review.

Use this structure:

```markdown
# UX Polish Review — <surface, in plain words>

**Date:** <YYYY-MM-DD>
**Scope:** <files / surfaces reviewed>
**Reviewer:** ux-polish-reviewer agent

<the agent's report verbatim: one-line state, then Must fix / Should fix / Consider
findings with before/after rewrites, then Consistency notes>
```

Keep the agent's wording and before/after pairs intact — they are the deliverable. Do not pad or
re-summarize the findings into something vaguer.

### Step 4 — Report back, do not apply

Tell the user where the report lives and give them the headline: the one-line state and the count
of findings per severity (e.g. "2 Must fix, 3 Should fix, 1 Consider — `docs/reviews/ux-polish/
2026-06-21-cli-help.md`"). Do not apply any rewrites as part of this skill. If the user wants the
changes made, that is a follow-up they ask for explicitly — at which point the before/after pairs
in the report are ready to apply.

## Boundaries

- Scope to what changed; don't sweep the whole codebase unless the user asks.
- Document findings; don't edit the reviewed copy in this skill.
- Trust the agent's rubric and output format — this skill scopes, dispatches, and persists.
- Favor simplicity and calm over cleverness — less chaos, more nature.
