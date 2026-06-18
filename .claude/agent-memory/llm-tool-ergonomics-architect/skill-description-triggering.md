---
name: skill-description-triggering
description: How to write a cross-tool SKILL.md description frontmatter that triggers autonomous loading without over-firing. The description string is the ONLY thing preloaded; body loads only after it matches. Applies to Claude Code, Codex CLI, OpenCode.
metadata:
  type: reference
---

For Scrinia's cross-tool Agent Skill (`scrinia-memory`), only the SKILL.md `description` frontmatter is preloaded into the system prompt across all three target harnesses (Claude Code, Codex CLI, OpenCode). The body loads only after the description triggers. So the description alone is the whole trigger contract.

Key distinction from [[claude-system-reminder-priming]]: a SKILL.md description must be **third-person capability prose** ("Use this skill when…"), NOT imperative second-person. Imperative/runbook framing in a description gets parsed as internal documentation and weakens trigger matching, especially on GPT-5-class (Codex) models. So the priming note's "write imperative content" rule applies to hook/system-reminder output, NOT to skill descriptions. Different surfaces, opposite voice.

Design rules that worked here:
1. **Open with a noun-phrase naming the content type** (decisions, conventions, fixes, cross-session context), not the mechanism ("MCP tools"). Models match against what they need, not what the tool is.
2. **Literal phrase anchors are the highest-precision cross-model trigger** — they don't depend on the model's semantic judgment. But avoid polysemous tokens: bare `"remember"` over-fires ("remember to add a semicolon", "remember this is a draft"). Keep inherently-retrospective phrases ("last time", "as we discussed", "what did we decide", "how did we handle X before").
3. **Add an explicit anti-trigger clause** ("Not for self-contained one-off requests answerable from the code in view — renames, …"). Single highest-leverage change for suppressing over-fire on capable models. Note: small OpenCode models under-weight negation, so it degrades gracefully — a precision bonus, not a load-bearing trigger.
4. **Avoid null-constraint hedges** like "could benefit from stored context" — almost anything could; it does no gating work and pushes toward recall-maximizing over-trigger.
5. **Move operating procedure (search-before/write-after mechanics) OUT of the description into the body.** Description answers "when would I reach for this?", body answers "what are the steps once in it?". Mixing altitudes weakens routing.

**Over-trigger matters because** it trains the model/user to ignore the skill (learned-ignore), the same failure class as the May 2026 bland-hint problem but on a different surface.

Recommended ~85-word description ships precision; a ~58-word lean variant trades precision on the proactive branch for lower per-session context cost. Recommended the longer one — per-session cost is trivial vs. cost of a noisy skill being ignored.
