# scrinia

[![License: BSD-3-Clause](https://img.shields.io/badge/License-BSD--3--Clause-blue.svg)](LICENSE)

Persistent, portable memory for LLMs. Compresses text into NMP/2 artifacts, stores them locally, and exposes 30 MCP tools — 18 for memory and 12 for project planning — so agents can remember, search, plan, execute, and learn across sessions. Built-in semantic search via Model2Vec (384-dim, ~22MB, zero native deps). Cross-process safe via OS-enforced file locks. Zero infrastructure required.

## Benchmarks

How does a structured memory system compare to simpler approaches? We built a [runnable benchmark suite](tests/Scrinia.Tests/Benchmarks/) that quantitatively compares three strategies:

- **Scrinia** — NMP/2 compressed artifacts, BM25+weighted field search, chunked retrieval
- **Flat-file** — all knowledge in one string (AGENTS.md-style), always fully loaded, substring search
- **Auto memory** — 200-line index always loaded, per-topic files loaded on demand (Claude-style)

### Token efficiency (avg tokens per query)

| Corpus size | Scrinia | Flat-file | Auto memory | Scrinia savings |
|---|---|---|---|---|
| 10 facts | 162 | 557 | 426 | 71% fewer tokens |
| 50 facts | 281 | 2,735 | 989 | 90% fewer tokens |
| 100 facts | 278 | 5,464 | 1,534 | 95% fewer tokens |
| 500 facts | 274 | 27,324 | 5,905 | 99% fewer tokens |

### Scaling (growth rate from 10 to 500 facts)

| System | Growth factor | Pattern |
|---|---|---|
| Scrinia | 1.7x | Near-constant |
| Auto memory | 13.8x | Sublinear |
| Flat-file | 49.1x | Linear |

### Cold start (tokens consumed before first query)

| System | 10 facts | 100 facts | 500 facts |
|---|---|---|---|
| Scrinia | 0 | 0 | 0 |
| Auto memory | 135 | 440 | 1,780 |
| Flat-file | 557 | 5,464 | 27,324 |

### Search recall

All three systems achieve 100% recall on exact-term and natural-language queries. Scrinia's advantage is not accuracy — it's doing it at 1-5% of the token cost.

### Cross-topic isolation

| System | Isolation ratio | Meaning |
|---|---|---|
| Scrinia | 100% | Only loads matching memories |
| Auto memory | 80% | Loads index + routed topic |
| Flat-file | 20% | Always loads all 5 topics |

### First query cost (cold start + query, 100 facts)

| System | Cold start | Query | Total |
|---|---|---|---|
| Scrinia | 0 | 282 | **282** |
| Auto memory | 440 | 1,564 | 2,004 |
| Flat-file | 5,464 | 5,464 | 10,928 |

### Where each system wins

| Dimension | Winner | Why |
|---|---|---|
| Very small corpus (<20 facts) | Flat-file | Negligible overhead, everything fits |
| Token efficiency at scale | Scrinia | Selective retrieval, zero cold start |
| Recall on exact terms | Tie | All systems find substring matches |
| Ranked precision | Scrinia | BM25 + weighted fields produce ranked results |
| Cross-topic isolation | Scrinia | Only loads matching memories |
| Setup simplicity | Flat-file | Just a string, no tools needed |
| Staleness management | Scrinia | Only system with review markers |

Run the benchmarks yourself:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~Benchmarks"
```

## Install

Build from source (.NET 10 SDK required):

```bash
git clone https://github.com/nickd-scrinia/scrinia
cd scrinia
dotnet build

# Publish trimmed single-file binary
.\publish.ps1 -OutputDir ./dist -Platform win-x64

# Download embedding model for semantic search (~22MB)
scri setup

# Optional: with Vulkan GPU-accelerated embeddings plugin
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithVulkan
```

## MCP setup

Add to your MCP client config (Claude Code, Cursor, Copilot, etc.):

```json
{
  "mcpServers": {
    "scrinia": {
      "command": "scri",
      "args": ["serve"],
      "transport": "stdio"
    }
  }
}
```

For HTTP transport via the API server, see [Server Administration](docs/server-admin.md).

## CLI quick reference

```bash
scri serve                          # start MCP server (stdio)
scri store notes ./notes.md         # store a file as memory
scri store api:auth ./auth.md       # store under a topic
scri list                           # list summary (topics, keywords, stats)
scri list --summary=false           # list all memories (full listing)
scri list --offset 0 --limit 50    # paginated full listing
scri search "auth"                  # hybrid BM25 + semantic search
scri show api:auth                  # display memory content
scri forget api:auth                # delete a memory
scri export api                     # export topic to .scrinia-bundle
scri import ./bundle.scrinia-bundle # import a bundle
scri bundle docs *.md               # bundle raw files
scri setup                          # download embedding model
scri config                         # list workspace settings
scri config plugins:embeddings      # get a setting
scri config plugins:embeddings val  # set a setting
```

All commands accept `--workspace-root` to override the workspace directory and `--json` for machine-parseable JSON output.

## Memory naming

| Pattern | Scope | Example |
|---|---|---|
| `subject` | Local store | `scri store session-notes file.md` |
| `topic:subject` | Topic group | `scri store api:auth file.md` |
| `~subject` | Ephemeral (in-memory) | Dies with process |

## MCP tools

30 tools available via `scri serve` — 18 memory tools and 12 project planning tools.

### Memory tools

| Tool | Description |
|---|---|
| `guide` | Session playbook (call once per session) |
| `store` / `append` | Persist or incrementally add to memories |
| `show` / `get_chunk` | Retrieve full content or individual chunks |
| `list` / `search` | Browse (summary by default) and search with BM25 + semantic scoring |
| `copy` / `forget` | Move between scopes or delete |
| `export` / `import` | Portable .scrinia-bundle files |
| `encode` / `chunk_count` | Low-level NMP/2 encoding |
| `ka` / `kt` | Knowledge analysis and transfer |
| `budget` / `reflect` / `ingest` | Session lifecycle tools |

### Planning tools (ScriniaProjectTools)

Full project lifecycle — ideation, planning, execution, verification, and learning.

| Tool | Description |
|---|---|
| `project_init` | Initialize a new project with goals and scope |
| `plan_requirements` | Capture and refine project requirements |
| `plan_roadmap` | Generate a phased roadmap from requirements |
| `plan_resume` | Resume a previously started plan |
| `plan_status` | View current plan progress and health |
| `plan_tasks` | List tasks with filtering and status |
| `task_next` | Get the next actionable task |
| `task_complete` | Mark a task as done with outcome details |
| `plan_verify` | Verify plan completion and quality |
| `plan_gaps` | Identify gaps and risks in the plan |
| `plan_retrospective` | Record execution outcomes and lessons learned |
| `plan_profile` | Store and retrieve user/agent preferences |

Plans are stored as topic-scoped memories (`plan:*`, `task:*`, `project:*`, `learn:*`, `user:*`) — no separate database. All planning data is searchable via the standard `search` tool. The `excludeTopics` parameter on `list` and `search` lets agents separate knowledge from planning data when needed.

Agent learning is built in: `plan_retrospective` stores execution outcomes with the `provenance:agent` keyword, and `plan_profile` stores user preferences — both discoverable via standard search.

## Documentation

### User Guides
- **[Getting Started](docs/getting-started.md)** — overview, installation, quick start
- **[CLI Reference](docs/cli-reference.md)** — commands, configuration, embedding providers, MCP client setup
- **[Server Administration](docs/server-admin.md)** — deployment, authentication, REST API, web UI, Docker

### Architecture
- **[Overview](docs/architecture/overview.md)** — system design, project structure, dependency graph
- **[CLI Architecture](docs/architecture/cli.md)** — workspace discovery, plugin host, MCP tools
- **[Server Architecture](docs/architecture/server.md)** — startup, middleware, auth, multi-store, plugins
- **[Core Internals](docs/architecture/core.md)** — IMemoryStore, NMP/2 encoding, search algorithms
- **[Embeddings Architecture](docs/architecture/embeddings.md)** — providers, vector store, HNSW, hybrid scoring

### Specification
- **[NMP/2 Spec](NMP_SPEC.md)** — encoding format specification

## Running tests

```bash
dotnet test tests/Scrinia.Tests             # ~567 CLI + MCP + planning + embeddings tests
dotnet test tests/Scrinia.Server.Tests      # 53 server tests
dotnet test tests/Scrinia.Plugin.Embeddings.Tests  # 12 Vulkan plugin + benchmark tests
```

## License

BSD-3-Clause. Copyright (c) 2026 Nick Daniels.
