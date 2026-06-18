---
name: hook-output-format-matrix
description: How each agent CLI (Claude Code, Codex, Copilot) handles hook stdout — what the model actually sees, what gets dropped, and what gets wrapped.
metadata:
  type: reference
---

Hook stdout handling per CLI, verified from primary docs (May 2026). See also [[claude-system-reminder-priming]] and [[project-scrinia]].

## Claude Code (code.claude.com/docs/en/hooks)

Per event:
- **SessionStart**: plain stdout → added to context. JSON `additionalContext` also supported.
- **UserPromptSubmit**: plain stdout → added to context AND shown in transcript. JSON `additionalContext` → added "more discretely" (not in transcript). Both paths reach the model.
- **SessionEnd**: plain stdout goes to **debug log only**, NOT the model. Must use JSON `additionalContext` if you want to inject — but model rarely needs anything at session end.

Key verbatim: "Claude Code wraps the string in a system reminder and inserts it into the conversation at the point where the hook fired." → both plain stdout AND additionalContext get wrapped in `<system-reminder>` tags internally. This is the same tag class Anthropic uses for its highest-priority system messages.

## OpenAI Codex CLI (developers.openai.com/codex/hooks)

Per event:
- **SessionStart**: plain stdout OR JSON `hookSpecificOutput.additionalContext` → "added as extra developer context"
- **UserPromptSubmit**: same as above; "Plain text on stdout is added as extra developer context"
- **No SessionEnd** — Codex has `Stop` (per-turn), no session-end event. Installer already skips SessionEnd for Codex.

Wrapping behavior (issue openai/codex#16933): Codex CLI currently renders UserPromptSubmit `additionalContext` as a **visible developer-role message** in the transcript. Hindsight reports this as a regression; the documented contract said "invisible to transcript, visible to Codex." Whichever wins, the model treats it as a developer message — high priority.

## GitHub Copilot CLI (docs.github.com/en/copilot/reference/hooks-reference)

Per event (from the official "Output processed?" table):
- **sessionStart**: Optional — CAN inject `additionalContext` into session via JSON
- **sessionEnd**: No — output not processed
- **userPromptSubmitted**: **No — output ignored**. Cannot inject context. Open issue github/copilot-cli#1139 explicitly asks for Claude-Code-style hook context injection; not yet implemented.
- **notification**: Optional — can inject `additionalContext`
- **postToolUseFailure**: Yes — `additionalContext` for recovery guidance
- **preToolUse**: control only (allow/deny/modify)

JSON shape (Copilot): `additionalContext` is "Injected into the session as a user message" (per docs on notification hook). Use `jq -c` / `ConvertTo-Json -Compress` for compact single-line JSON output.

## Prior art surveyed

- **Hindsight** (hindsight.vectorize.io) — Codex hook wraps memories in `<hindsight_memories>...</hindsight_memories>` inside `additionalContext`. Reference Claude Code hook (rajeevramani gist) uses plain stdout with a markdown header `## Hindsight: Previous Session Context`.
- **GSD** (github.com/gsd-build/get-shit-done/hooks) — every hook script (`gsd-context-monitor.js`, `gsd-session-state.sh`, `gsd-prompt-guard.js`) uniformly emits `{ "hookSpecificOutput": { "hookEventName": "<event>", "additionalContext": "..." } }`. Switches `hookEventName` to "AfterTool" for Gemini, "PostToolUse" otherwise. No XML tags inside the additionalContext payload.

## Application

For Scrinia: emit JSON `hookSpecificOutput.additionalContext` form for all three CLIs (uniform, durable, future-proof). Wrap memory payloads in semantic tags (`<scrinia-hint>`, `<scrinia-memory>`) so they survive any internal "system reminder" wrapping and remain visually distinct from prose. Stop relying on Copilot's `userPromptSubmitted` — move hint to `sessionStart` + `notification` on that CLI until #1139 lands.
