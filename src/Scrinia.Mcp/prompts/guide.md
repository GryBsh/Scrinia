# Scrinia — Memory Toolkit for Agents

Scrinia is your single source of truth. All project knowledge goes through scrinia, not platform-specific memory systems.

## Quick Start

```
memory('search', { query: "..." })       — find what you know
memory('remember', { path: "...", content: [...] }) — store something
memory('recall', { path: "..." })        — read it back
memory('append', { path: "...", appendContent: "..." }) — add to it
memory('forget', { path: "..." })        — delete it
memory('restore')                        — resume after context loss
```

## Before You Start Work

Always `memory('search')` first. Check prior sessions, existing knowledge, established patterns. This prevents redundant investigation and keeps you consistent.

## Store Proactively

When you learn something, fix a bug, get corrected, or discover a convention — store it immediately. If you had to figure it out, store it.

Don't store: transient working state (use `/temp/`), things derivable from code or git.

## Paths

Paths organize memories hierarchically and are auto-tagged for search:
- `/api/auth-flow` → searchable by `[api, auth, flow]`
- `/patterns/retry` → searchable by `[patterns, retry]`

Scope searches: `memory('search', { query: "auth", path: "/api/" })`

### Reserved paths

| Path | Purpose |
|------|---------|
| `/skill/...` | reusable specialist prompts (use `/skill/{name}` to load or override) |
| `/agent/...` | agent profile and behavioral norms |
| `/patterns/...` | recurring patterns and conventions |
| `/sessions/...` | session logs by date |
| `/checkpoint/...` | state snapshots |
| `/temp/...` | ephemeral (dies on process exit) |

Any other path you use is treated as plain memory and grouped by its first segment for search and listing.

## Chunks

Each element in the `content` array becomes one independently searchable chunk. Design chunks around concepts, not size.

```
memory('remember', { path: "/api/auth", content: [
  "OAuth flow: redirect → callback → token exchange",
  "Token refresh: background job every 55 minutes"
]})
```

Retrieve a specific chunk: `memory('recall', { path: "/api/auth", chunk: 2 })`

## Skills

Load a built-in or custom skill:

```
memory('recall', { path: "/skill/qa" })       — load a skill prompt
memory('recall', { path: "/skill/" })         — list all available skills
memory('remember', { path: "/skill/my-helper", content: [...] }) — create or override
```

Skills are persisted as `.scrinia/skills/{name}.md`. Built-in skills are reused from the binary unless you override them.

## Review Dates

Flag memories for staleness review:
```
memory('remember', { ..., reviewAfter: "2026-06-01" })
memory('remember', { ..., reviewWhen: "when auth changes" })
```

## Session Logs

Maintain a session log: `memory('append', { path: "/sessions/2026-04-08", appendContent: "..." })`

## Recovery

`memory('restore')` resumes agent context — agent profile, patterns, today's session log, and the list of available skills. Follow the `followUp` list to load detailed context.

## Workspace

Scrinia writes to `.scrinia/`. Include those changes in your commits.
