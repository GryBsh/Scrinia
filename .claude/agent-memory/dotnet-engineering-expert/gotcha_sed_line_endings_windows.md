---
name: gotcha_sed_line_endings_windows
description: sed -i via Git Bash on this Windows repo rewrites a file's line endings to LF even for a single-line edit, silently desyncing it from the repo's CRLF convention (core.autocrlf=true) and flagging it modified in git status with an empty git diff.
metadata:
  type: project
---

This repo (`Scrinia`) uses CRLF line endings with `core.autocrlf=true`. Running `sed -i 's/.../.../ '`
on a `.cs`/`.yaml` file via the Bash tool (Git Bash) rewrites the WHOLE file to LF-only, not just the
touched line. The result: `git diff` shows no textual difference (git normalizes CRLF/LF for
comparison), but `git status` still marks the file modified, because the working-tree bytes no
longer match the CRLF original.

**Symptom:** after a mutation-testing round-trip (edit → run tests → revert edit) using `sed -i`,
files that should be byte-identical to HEAD show up as `M` in `git status` with an empty `git diff`.
This also silently affects files a test harness writes, e.g. golden-test snapshot regeneration
(`SCRINIA_UPDATE_GOLDEN=1`) rewrote three snapshot files with genuinely zero content change, purely
because the writer emits LF.

**How to apply:** after any `sed -i` edit (or any tool/script write) on a tracked file in this repo,
check `git status`; if a file shows modified but `git diff` is empty, either run `unix2dos <file>`
before staging (if there's a real content change to keep) or `git checkout -- <file>` (if the
content is actually unchanged, e.g. after reverting a mutation). Do this BEFORE committing, not
after — cleaning it up post-commit means a second commit or an amend.
