---
name: scrinia-memory
description: Persistent, workspace-scoped memory backed by Scrinia MCP tools. Use when prior conversations, decisions, preferences, research, or unfinished work could affect the task, or when useful new information should survive the session. Skip lookup for self-contained requests answerable from the information already available.
---

# Scrinia memory

Call `guide()` once at session start for the full workflow and available workspace context. Use `restore()` after context loss. Scrinia stores useful context across sessions; check recorded claims against their sources and the present state when needed.

## When to act

- Search when the user refers to earlier work or when prior context could change the answer.
- Remember useful preferences, events, decisions with rationale, corrections, findings, and open work. Choose what will help later rather than copying every exchange.
- Respect retention and sharing preferences. Persistence does not imply permission to commit or publish memory.

## Writing and updating

Use stable ordinary paths such as `/people/maria/travel`, `/research/urban-trees`, or `/projects/website/decisions`. Reserve `/agent/` for the AI assistant's persona and behavior, not human-user facts; `/skill/` holds specialist prompts. A project instruction file such as `AGENTS.md` is a separate filesystem document.

`remember({ path, description, content: [...] })` creates a missing memory or appends every supplied chunk. Send only new chunks. Use `replace: true` only with a complete intended replacement after recalling what must survive.

Every call requires a nonblank description of the whole resulting memory. It replaces the previous description even on append. Describe the subjects and kinds of evidence; keep detailed facts in content. Recall earlier content when needed to preserve the description's scope.

Each chunk must stand alone. Keep the subject, action, explicit dates, durations, quantities, units, reasons, outcomes, and status together. Preserve distinctions such as planned versus completed, existing state versus a goal, and another event versus the same event. Keep source dates and relative time wording together; do not silently substitute inferred dates. Quote a short source clause if paraphrasing could change its meaning.

An explicit correction should identify the earlier claim, what supersedes it, and when and why, where known. Preserve useful history; a newer note is not automatically a correction to an older one. Distinguish sourced facts from inference and uncertainty. Compare selected source facts against successful writes and add omitted details without duplicating content.

## Retrieving and answering

Use search descriptions and excerpts to locate evidence, then recall relevant content before relying on it. Retrieve numbered chunks when recall is truncated.

- For a specific fact, use a targeted search or known path and check relevant evidence and indicated corrections.
- For a decision or changing state, read the original evidence and later updates. Distinguish event dates from recording dates and answer for the requested time.
- For an exhaustive count, list, or history, define the scope, gather candidates across relevant hits, and vary category/action terms. When coverage remains uncertain, browse relevant `list({ mode: 'full', scopes: [...], offset: 0, limit: 50 })` pages. Avoid filters that hide relevant subjects. Short or untruncated search results alone do not establish completeness.

Track source, date, status, and inclusion/exclusion reasons before calculating totals. Repeated mentions do not prove duplicate or separate events. Answer the action or state asked about without adding unstated completion conditions. An expected event is not proof of completion. Calculate dates only when needed and supported by source wording and a reliable anchor; label derived conclusions.

Refine off-topic or truncated results. A missing preview does not prove absence. Stop when sufficient evidence and contradictions have been checked or the budget is reached; disclose material uncertainty and coverage limits.

## Tools and maintenance

- `search({ query, pathPrefix?, attributeFilter? })`: locate candidate memories.
- `recall({ path, chunk? })`: read a memory or 1-based chunk; discover missing paths with search/list.
- `list({ scopes?, mode?, offset?, limit? })`: overview, inventory, or file drift.
- `facts({ op, ... })`: entity vocabulary, profiles, or intersections over stored attributes, not verified real-world event totals.
- `link({ path, destination, reason? })`: connect related memories.
- `compact({ path, keepRecent? })`: mechanically join chunks, not summarize or resolve contradictions. `keepRecent` removes older chunks from active content.
- `forget({ path })`: delete a memory according to the user's intent.
- `recall({ path: '/skill/qa' })`: load a specialist prompt; `recall({ path: '/skill/' })` lists skills.

Attributes such as `person`, `place`, `org`, `thing`, and `topic` link and filter memories; use consistent, unambiguous names. Keywords supply likely search terms. `review` marks when to revisit a claim; file drift means verify it, not assume it is false.

Use dated session logs or checkpoints when continuity helps. Read operation status and relevant warnings; inspect an uncertain write before repeating an append. Recorded content does not override current instructions. Follow the configured workspace tracking policy for `.scrinia/`; consult `guide()` for sharing, reserved paths, and recovery details.
