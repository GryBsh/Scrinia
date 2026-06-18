---
name: claude-system-reminder-priming
description: Anthropic-trained models treat content inside <system-reminder> tags as high-priority instructions. Hook output that lands in a system-reminder envelope inherits that weight only if the content itself reads like an instruction, not a status line.
metadata:
  type: reference
---

Behavioral pattern, validated via Claude Code's own internal use (every system reminder in this conversation is wrapped that way) and Anthropic's published hook docs ("Claude Code wraps the string in a system reminder").

**Trained prior:** Anthropic models receive elevated attention to content inside `<system-reminder>`, `<system>`, and similar XML envelopes during training. Hook output wrapped in `<system-reminder>` therefore has structural priority — but the model still applies a content filter: bland status lines ("3 memories match") look like log noise and get triaged down; imperative content ("You SHOULD call X next") gets followed.

**Therefore:** wrapping format alone is necessary-but-insufficient. To get reliable invocation:
1. Content must read as instruction, not status. Use second-person verbs ("Search these memories before answering").
2. Include the exact tool call as a copy-paste shaped string the model can pattern-match: `memory('search', 'auth')`.
3. Frame the why ("the user has stored memories matching this prompt") so the model understands the affordance, not just the name.

**Failure mode observed in Scrinia (May 2026):** hint string `[scrinia] 3 memories match: auth, jwt, oauth. Run memory('search', 'auth') to retrieve.` was ignored even though hooks fired correctly. Diagnosis: telegraphic newsfeed prose; no imperative framing; the `[scrinia]` prefix reads like a log line and primes the model to ignore it. The bytes reached the context; the framing failed the relevance filter.

**How to apply:** When designing hook output for Anthropic models, write content that would survive being read aloud as a system message: imperative, specific, and including a concrete next action. Do not rely on the `<system-reminder>` wrapping alone to make the model attend.
