# Scrinia — Memory Toolkit for Agents

Scrinia is your single source of truth for project knowledge. All durable findings, decisions, and skills go through scrinia.

## Quick Start

```
memory('search', { query: "..." })       — find what you know
memory('remember', { path: "...", content: [...] }) — store something
memory('recall', { path: "..." })        — read it back
memory('append', { path: "...", appendContent: "..." }) — add to it
memory('forget', { path: "..." })        — delete it
memory('restore')                        — resume after context loss
```

## Search before non-trivial work

For new tasks, joining a project, or questions about prior decisions, `memory('search')` first. It prevents redundant investigation and keeps your work consistent with what's already been decided.

Skip the search when the request is fully self-contained (rename a variable, reformat a function, answer a question with the code already in front of you) or when you're storing a trivial fact you just learned. A round trip you don't need is friction.

## Store proactively

When you learn something, fix a bug, get corrected, or discover a convention — store it immediately. If you had to figure it out, the next agent shouldn't have to.

Don't store: transient working state (use `/temp/`), things derivable from code or git history (the commit message is the record), or content already covered by an existing memory (append or update instead).

## Paths

Paths organize memories hierarchically and are auto-tagged for search:

- `/api/auth-flow` → searchable by `[api, auth, flow]`
- `/patterns/retry` → searchable by `[patterns, retry]`

Scope a search to a path prefix: `memory('search', { query: "auth", path: "/api/" })`.

### Reserved paths

| Path | Purpose |
|------|---------|
| `/skill/...` | reusable specialist prompts (load via `/skill/{name}`, override by storing) |
| `/agent/...` | agent profile and behavioral norms |
| `/patterns/...` | recurring patterns and conventions |
| `/findings/...` | audit, review, and QA findings |
| `/learn/...` | retrospectives and durable lessons |
| `/sessions/...` | session logs by date |
| `/checkpoint/...` | state snapshots |
| `/temp/...` | ephemeral (dies on process exit) |

Any other path you use is treated as plain memory and grouped by its first segment.

## Chunks

Each element in the `content` array becomes one independently searchable chunk. Design chunks around concepts, not size.

```
memory('remember', { path: "/api/auth", content: [
  "OAuth flow: redirect → callback → token exchange",
  "Token refresh: background job every 55 minutes"
]})
```

Retrieve a specific chunk: `memory('recall', { path: "/api/auth", chunk: 2 })`.

## Skills

Skills are reusable specialist prompts — load one mid-conversation when you hit a domain you weren't optimized for.

```
memory('recall', { path: "/skill/qa" })       — load a skill prompt
memory('recall', { path: "/skill/" })         — list available skills
memory('remember', { path: "/skill/my-helper", content: [...] }) — create or override
```

Built-in skills that ship with scrinia:

- `auditor` — security/quality/doc review with sequenced finding IDs
- `qa` — test-and-build verification with command-output evidence
- `debugger` — observe → hypothesize → isolate → verify
- `chaos-engineer` — failure domains, blast radius, recovery gaps
- `onboarder` — build a codebase mental model
- `merge-safety` — multi-user `.scrinia/` merge conflict handling
- `evolutionary` — prune stale memories, surface drift, keep skills aligned with practice
- `self-reflector` — compare plan vs reality after a unit of work, persist lessons

Skills are persisted as `.scrinia/skills/{name}.md`. Built-ins are reused from the binary unless you override them. To merge a project override against an updated built-in: `memory('recall', { path: '/skill/{name}', reconcile: true })`.

## Reading responses

Every response includes structured fields the next call should respect:

- `instruction` — what you should do next (e.g. "Call memory('recall') for each item in followUp"). Follow it.
- `followUp` — paths or memories worth loading; the response only returned a short summary to save tokens.
- `actionNeeded` — warnings that block normal operation (unresolved merge conflicts, drifted memories). Address these before doing other work.
- `status` and `action` — what happened.

## Review markers

Flag memories for staleness review:

```
memory('remember', { ..., reviewAfter: "2026-06-01" })
memory('remember', { ..., reviewWhen: "when auth changes" })
```

Memories with these markers surface as `[stale]` or `[review?]` in `memory('list')` so you know to re-check them. The store also auto-flags memories that mention specific counts (e.g. "759 tests") with a `reviewWhen: "when counts in this memory change"` marker.

## Session logs

Maintain a session log: `memory('append', { path: "/sessions/2026-04-08", appendContent: "..." })`.

## Recovery

`memory('restore')` resumes agent context — agent profile, patterns, today's session log, available skills, and any unresolved merge conflicts. Read its `actionNeeded` warnings first, then follow the `followUp` list to load detailed context.

## Workspace

Scrinia writes to `.scrinia/`. Include those changes in your commits.

## Scrinia vs platform auto-memory

If your platform also has its own auto-memory (Claude Code's per-user agent memory, for example), don't duplicate. Scrinia is workspace-scoped: it lives in `.scrinia/` and travels with the code, so it's the right home for project context — architecture, decisions, sessions, findings, skills. Platform auto-memory is user-scoped and travels with the user, so it's the right home for cross-project preferences and how-to-collaborate notes. When in doubt, project context belongs in Scrinia.

## Every task, ask yourself

- What do I know about this already? (search first if non-trivial)
- What should I remember for next time? (store immediately if non-obvious)
- What patterns am I following? (capture as `/patterns/...` once they recur)
- What can I make easier next time? (store conventions, override skills)
- What can I share with others? (skills and patterns are the durable artifacts)
