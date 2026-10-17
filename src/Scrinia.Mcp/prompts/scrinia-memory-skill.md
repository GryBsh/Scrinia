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
  a convention, call `remember({ path, content: [...] })` immediately. If you had to figure it
  out, the next agent shouldn't have to.
- **Restore after a reset.** `restore()` resumes agent context (profile, patterns, today's
  session log, available skills, unresolved merge conflicts).

## Core tools

```
guide()                                           — full primer; call at session start
search({ query })                          — find what you know
remember({ path, content: [...] })         — add chunks (creates a missing memory)
recall({ path })                           — read it back
list({ scopes?, mode? })                   — browse
restore()                                  — resume after context loss
```

## Preserve the details

Past events can be worth remembering even when they do not describe an ongoing preference or convention. When remembering a fact or event, preserve its explicitly stated people, dates, durations, quantities, reasons, and outcomes. Keep units and qualifiers, and distinguish earlier facts from later corrections. Compress wording without dropping these details; do not infer missing values. Before finishing, check that the facts selected for retention appear in successful memory writes, and add any omitted details.

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
