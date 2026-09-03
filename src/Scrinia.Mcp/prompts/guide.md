# Scrinia — Agent Memory Guide

Scrinia is persistent, project-scoped memory and reusable skill prompts, exposed as MCP tools. Memories live in `.scrinia/`, so they travel with the code and are shared with every agent on the repo. This guide covers the model and the tools that operate on it.

## The model

### Memories and paths

A memory is content stored at a **path** like `/findings/SEC-001`. Paths are hierarchical and auto-tagged for search — `/api/auth-flow` is findable by `api`, `auth`, `flow`. Scope a search to a prefix: `search({ query: 'auth', pathPrefix: '/api/' })`.

Memory addresses are logical values, not filesystem paths or URIs. Persistent addresses use `/topic/name` (or a bare name at the tool boundary); ephemeral addresses use the canonical `~name` form. `/temp/name` is accepted as an alias and canonicalized to `~name`. Do not pass `file://`, other URIs, drive-letter paths, or physical filesystem paths.

When writing integrations in .NET, use `MemoryAddress.Parse(raw)` when invalid input should throw, or `MemoryAddress.TryParse(raw, out address, out error)` when it should be reported to the caller. Store and plugin contracts use the typed `MemoryAddress`; artifact locations are opaque `ArtifactKey` values and must not be interpreted as URIs.

### Chunks

A memory's `content` is an **array of chunks**, each stored and retrieved independently. Split a memory along its semantically distinct parts — like the sections of a document — not by size:

```
remember({ path: '/api/auth', content: [
  'OAuth flow: redirect → callback → token exchange',
  'Token refresh: background job every 55 minutes'
]})
```

Retrieve one chunk with `recall({ path: '/api/auth', chunk: 2 })`.

### Attributes

**Attributes** are typed facets — a map of key to values — that both **filter** searches and **link** related memories:

```
attributes: { person: ['maria'], org: ['acme'], review: ['2026-08-01'] }
```

Keys Scrinia understands specially:

- **Entities** — `person`, `place`, `org`, `thing`, `topic`. The recommended vocabulary for the people, places, orgs, things, and themes memories link on.
- **Dependencies** — `file`, `ref`. Recorded (and, for `file`, hashed) so Scrinia flags drift when a referenced file changes.
- **Review** — `review`. An ISO date (`2026-08-01`), a condition (`when auth changes`), or both; flagged memories surface as `[stale]` / `[review?]` in `list`. Scrinia auto-adds one to memories citing specific counts (e.g. "759 tests").

Any key is allowed — these are just the ones with built-in behavior.

### Keywords

**Keywords** are the distinctive terms and likely search phrasings for a memory. They merge with the terms Scrinia extracts from the content and seed the search index: `keywords: ['oauth flow', 'token refresh']`.

### The entity graph

Attributes build a graph linking memories that share a **key and value** — not just entity keys: every key is treated uniformly, so `project:apollo` links memories just as `person:maria` does. A search naming one linked value surfaces related memories that share no query words — a note tagging both `org:acme` and `person:maria` lets a query about Acme reach Maria. Two habits keep links clean:

- **Name a value the same way every time** — `person:maria`, never also `person:maria rodriguez`.
- **Type honestly** — the entity type separates a same-named person and place (`person:paris` vs `place:paris`).

## The tools

### remember — store a memory

`remember({ path, content, attributes?, keywords? })` — `content` is a string array, one chunk per element.

```
remember({ path: '/findings/SEC-001',
  content: ['Login form accepts an unescaped `next` param', 'Fix: allowlist redirect targets'],
  attributes: { topic: ['auth'], file: ['src/Login.tsx'], review: ['when auth changes'] },
  keywords: ['open redirect', 'login next param'] })
```

Use it when you learn something durable — a decision, fix, or convention the next agent should not have to rediscover.

### recall — read a memory

`recall({ path, chunk? })` — the whole memory, or one chunk if you pass `chunk`. `recall({ path: '/skill/qa' })` loads a skill; `recall({ path: '/skill/' })` lists skills.

### search — find memories

`search({ query, pathPrefix?, attributeFilter?, scopes?, context?, limit?, excludeTopics? })` — ranked matches, each with a preview and its attributes, so you can decide what to recall without another call.

```
search({ query: 'oauth flow', pathPrefix: '/findings/', attributeFilter: { topic: ['auth'] } })
```

Use it before non-trivial work; skip it when the request is self-contained.

### list — browse memories

`list({ mode?, scopes?, offset?, limit?, excludeTopics? })` — `mode` is `summary` (default), `full` (a paginated table), or `drift` (a code-reference drift check). Stale and review-flagged memories are marked here.

### append — add to a memory

`append({ path, content, attributes?, keywords? })` — `content` is a single string added as one new chunk (creating the memory if needed). Supplied attributes merge into the existing ones.

### forget — delete a memory

`forget({ path })`.

### compact — merge a memory's chunks

`compact({ path, keepRecent? })` — merges a multi-chunk memory into one and archives the original (except for an ephemeral `~name` memory, or one with no prior index entry); `keepRecent: N` keeps the N most recent chunks.

### link — connect two memories

`link({ path, destination, reason? })` — a bidirectional link between two memories.

### restore — resume your context

`restore()` — reloads your agent profile, patterns, today's session log, skills, and unresolved conflicts. Rarely called directly; `guide()` folds it in at session start.

### facts — query the entity graph

`facts({ op, entity?, entities?, key?, scopes?, excludeTopics?, limit? })` — aggregate and relational questions `search` can't express. Three ops: `entities` lists the known entity vocabulary, `profile` returns one entity's counts, date span, and co-occurrences, and `intersect` returns memories mentioning *all* of the given entities.

```
facts({ op: 'intersect', entities: ['person:jolene', 'place:france'] })
```

### guide — this guide

`guide()` returns this guide and restores your workspace context in one call. Call it once at session start.

## Reference

### Reserved paths

Any path works; a few prefixes carry first-class behavior:

| Path | Purpose |
|------|---------|
| `/skill/...` | Reusable specialist prompts (load via `recall`, override by storing) |
| `/agent/...` | Agent profile and behavioral norms |
| `/patterns/...` | Recurring patterns and conventions |
| `/findings/...` | Audit, review, and QA findings |
| `/learn/...` | Retrospectives and durable lessons |
| `/sessions/...` | Session logs by date (`YYYY-MM-DD`) |
| `/checkpoint/...` | State snapshots |
| `/temp/...` | Ephemeral — dies on process exit. An alias for `~name`; both spell the same memory. |

Any other path is plain memory, grouped by its first segment.

The CLI is persistent-only. Its memory commands reject both `~name` and `/temp/name` with a forbidden-address error because each CLI invocation is a short-lived process. Use an MCP client connected to the long-lived `scri serve` process for ephemeral memories. URI-shaped or physical paths are rejected as unsupported input rather than resolved.

### Reading responses

Every response carries structured fields:

- `instruction` — what to do next; follow it.
- `followUp` — paths worth loading (the response returned a short summary to save tokens).
- `actionNeeded` — blocking warnings (merge conflicts, drift); address these first.
- `status` / `action` — what happened.

### Skills

Skills are specialist prompts you load when you hit a domain you were not optimized for:

```
recall({ path: '/skill/qa' })                          — load a skill
recall({ path: '/skill/' })                            — list skills
remember({ path: '/skill/my-helper', content: [...] }) — create or override
```

Built-ins: `auditor`, `qa`, `debugger`, `chaos-engineer`, `onboarder`, `merge-safety`, `evolutionary`, `self-reflector`. They live at `.scrinia/skills/{name}.md`; a project override takes precedence. Merge one against an updated built-in with `recall({ path: '/skill/{name}', withBuiltin: true })`.

### Sessions

Keep a running log: `append({ path: '/sessions/2026-07-16', content: '...' })`.

### Recovery

After a context reset, `restore()` brings back your profile, patterns, session log, skills, and unresolved conflicts. Read its `actionNeeded` warnings first, then follow `followUp`.

### Workspace

Scrinia writes to `.scrinia/`. Commit those changes — that is how memory travels with the code.
