---
name: scrinia-memory
description: Persistent, workspace-scoped project memory (past decisions, conventions, prior fixes, and context from earlier sessions) backed by the Scrinia MCP tools. Use when the user refers to earlier work or asks what was decided ("last time", "as we discussed", "what did we decide", "how did we handle X before"), or before starting a non-trivial or unfamiliar task in this project where prior decisions or conventions likely exist. Also use right after establishing a decision, convention, or non-obvious fix worth persisting. Not for self-contained one-off requests answerable from the code in view (renames, "what does this function do", simple syntax or lookup questions).
---

# Scrinia memory

This workspace uses **Scrinia** for persistent, project-scoped memory, exposed as MCP tools. Scrinia
is the single source of truth for project knowledge — decisions, conventions, findings, and skills.

## When to act

- **Search first.** For a new task, joining the project, or any question about prior decisions, call
  `search({ query: "..." })` before doing the work. Skip it only for fully self-contained
  requests (rename a variable, answer from code already in front of you).
- **Store proactively.** When you learn something non-obvious, fix a bug, get corrected, or discover
  a convention, call `remember({ path, description, content: [...] })` immediately. If you had to figure it
  out, the next agent shouldn't have to.
- **Restore after a reset.** `restore()` resumes agent context (profile, patterns, today's
  session log, available skills, unresolved merge conflicts).

## Core tools

```
guide()                                           — full primer; call at session start
search({ query })                          — find what you know
remember({ path, description, content: [...] })         — add chunks (creates a missing memory)
recall({ path })                           — read it back
list({ scopes?, mode? })                   — browse
restore()                                  — resume after context loss
```

## Preserve the details

Past events can be worth remembering even when they do not describe an ongoing preference or convention. When remembering a fact or event, preserve its explicitly stated people, dates, durations, quantities, reasons, and outcomes. Keep units and qualifiers attached to the facts they modify. Preserve distinctions such as another event versus the same event, planned versus completed, and still true versus no longer true. Keep earlier facts and later corrections distinct; do not infer that separate events conflict or refer to the same occurrence. Compress wording without changing these relationships or inferring missing values. Separate what is already true from what someone plans or hopes to change. Preserve the wording that connects them (such as already, another, still, or hoping to improve); when paraphrasing could change that meaning, include the short source clause verbatim alongside the summary. Before finishing, compare the selected source facts with successful memory writes, including their qualifiers and relationships, and add any omitted details.

## Conventions

- **Paths** are hierarchical and auto-tagged for search (`/api/auth-flow` → `[api, auth, flow]`).
  Reserved prefixes: `/skill/`, `/agent/`, `/patterns/`, `/findings/`, `/learn/`, `/sessions/`,
  `/checkpoint/`, `/temp/` (ephemeral).
- **Attributes** — typed facets (`person:`, `place:`, `org:`, `thing:`, `topic:`) — link memories
  across the entity graph. Pass them in the `attributes` map (same-shaped `key:value` keywords route
  there too), and name an entity the same way every time.
- **`.scrinia/` changes are part of your commits.**

Call `guide()` for the full primer — chunks, skills, review markers, and recovery details.

`remember` appends by default, preserving earlier content. Send only new chunks. Use `replace: true` only with the complete intended replacement.


## Descriptions and evidence retrieval

Every MCP `remember` call requires a nonblank `description`, including appends and reserved paths. It replaces the prior description of the whole memory. Existing stores and CLI writes retain their content-excerpt fallback; no migration is required.

### Writing memories

1. Write a self-contained chunk for each event or closely related group of facts. Keep the actor, action, object, explicit quantities, and completed/planned/ongoing status together.
2. Keep the source conversation date and original relative time phrase together. When storing memories, retain relative dates as stated instead of adding inferred calendar dates to content or descriptions. Preserve completed actions separately from expected later steps; an expected date does not prove that an event occurred.
3. An old item is not necessarily disposed of, and examples of actual purchases are not hypothetical purchases. Preserve the short source clause when these distinctions matter.
4. Supply a concise description of the whole resulting memory on every remember call: describe its subjects and kinds of evidence, not just the new chunk or an answer inferred from it. Recall an existing memory when needed to update that description without losing its earlier scope. The description does not replace detailed content.

### Retrieving evidence

Do not repeat inferred calendar dates from memory summaries. Use the original relative phrase and source conversation date. Calculate a date only if the question needs it and it can be verified from that anchor; otherwise keep the range or uncertainty. An expected date does not prove occurrence. Count the requested action: an order placed to buy an item counts as buying unless contradicted or cancelled. Delivery and possession are separate; require evidence of either only when asked about that state. Plans are not placed orders. Do not add unstated completion conditions.

1. Use search descriptions and excerpts to locate evidence, not as a substitute for reading it. Recall the full relevant memories before answering; if a recall is truncated, retrieve the relevant chunks individually and read any dates, qualifications, or corrections needed to interpret them.
2. For counts, totals, and lists, gather candidates from all relevant hits, not only the top result. Track each candidate with its source, date, status, and reason for inclusion or exclusion before calculating.
3. For counts, totals, and lists, a short or untruncated search is not proof of complete coverage. Before calculating, search separately for related categories and alternative action words, and browse the memory inventory with list({ mode: 'full', limit: 50 }) to find relevant paths absent from the hits. Use descriptions to select full recalls. Check further inventory pages within the request budget; do not claim an exhaustive count if relevant areas remain unchecked. If search is truncated or results are dominated by an unrelated meaning, make focused follow-up searches for the subject and relevant activities, using paths or attributes when useful. Do not treat an absent preview as evidence that a fact does not exist.
4. Stop when relevant candidates and contradictions have been checked or the available request budget is reached; state remaining uncertainty. Repeated mentions are not automatically separate events or automatically the same event.
