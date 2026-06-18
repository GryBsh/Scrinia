---
name: project-scrinia
description: Scrinia is a workspace-scoped memory tool with auto-install hooks across Claude Code, Codex CLI, Copilot CLI. Three canonical events: SessionStart→restore, SessionEnd→consolidate, UserPromptSubmit→hint.
metadata:
  type: project
---

Scrinia (this repo) is a workspace-scoped persistent memory system. Shipped: auto-install of hooks across three agent CLIs (Claude Code, OpenAI Codex CLI, GitHub Copilot CLI).

Canonical events / scri commands:
- SessionStart → `scri restore` — print recent memory context dump for the model
- SessionEnd → `scri consolidate --auto` — Tier-2 consolidation; output NOT for the model
- UserPromptSubmit → `scri hint` — single-line relevance hint, e.g. `[scrinia] 3 memories match: auth, jwt, oauth. Run memory('search', 'auth') to retrieve.`

Hook installers live at `src/Scrinia/Commands/Hooks/` ([[ClaudeCodeHookInstaller]], [[CodexHookInstaller]], Copilot equivalent). The hint command is at `src/Scrinia/Commands/Hint.cs` and currently emits plain text via `HintCommand.FormatPlain`.

**Why:** User wants the model to actually call `memory('search', ...)` after seeing the hint. Empirical problem (May 2026): hooks fire and stdout reaches the CLIs, but the model ignores the content on Claude Code — does not acknowledge stored memories, does not call `memory('search', ...)`.

**How to apply:** When suggesting output-format changes, respect that (a) Codex needs JSON `hookSpecificOutput.additionalContext` to inject context; (b) Claude Code accepts both but wraps in a system-reminder; (c) Copilot CLI's `userPromptSubmitted` output is dropped — only `sessionStart` and `notification` accept `additionalContext`. See [[hook-output-format-matrix]].
