---
name: project_superpowers_sdd_gitignored
description: The .superpowers/ SDD task-brief/report tree is entirely gitignored in this repo — report edits never land in commits.
metadata:
  type: project
---

`.superpowers/` (which holds the SDD task briefs and running reports, e.g.
`.superpowers/sdd/2026-08-18-published-surface-coherence/task-9-report.md`) is excluded by
`.gitignore:70` (`.superpowers/`). Confirmed via `git check-ignore -v` and `git log --oneline --
<report-path>` returning nothing even though the file has been edited across at least three
fix rounds.

**Why this matters:** when a task brief says "one commit covers all fixes" and separately "append
a report section," the report edit is NOT part of that commit and never will be — there is no
conflict to resolve and no need to hunt for where the report got committed. Commit the code/test
changes first, get the real SHA from that commit, then write the SHA into the report afterward
with a plain `Edit` — no amend, no second commit, no `git add` for the report file (it would
silently no-op or need `-f`).

**How to apply:** on any future Task 9 (or other SDD-numbered) fix round in this repo, don't
`git add` anything under `.superpowers/`, and don't expect `git log`/`git blame` on report files
to show history — they only ever exist in the working tree.
