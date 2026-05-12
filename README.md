# scrinia

[![License: BSD-3-Clause](https://img.shields.io/badge/License-BSD--3--Clause-blue.svg)](LICENSE)

Persistent, portable memory for LLMs. Compresses text into NMP/2 artifacts, stores them locally, and exposes 2 MCP tools (`guide` and `memory`) so agents can remember, search, recall, and load reusable specialist skills across sessions. Built-in semantic search via Model2Vec (384-dim, ~22MB, zero native deps). Cross-process safe via OS-enforced file locks. Zero infrastructure required.

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

> **Note**: All mcp tools are availabl via cli, so they can be used in hooks, etc.

```bash
scri serve                          # start MCP server (stdio)
scri guide                          # print the agent guide (run once per session)
scri store notes ./notes.md         # store a file as memory
scri store api:auth ./auth.md       # store under a topic
scri append notes ./more.md         # add a new chunk to an existing memory
scri compact notes --keep-recent 3  # merge old chunks; keep the 3 newest
scri link notes api:auth -r "see also" # bidirectional cross-reference
scri list                           # list summary (topics, keywords, stats)
scri list --summary=false           # list all memories (full listing)
scri list --offset 0 --limit 50    # paginated full listing
scri search "auth"                  # hybrid BM25 + semantic search
scri show api:auth                  # display memory content
scri forget api:auth                # delete a memory
scri restore                        # resume agent context (profile, patterns, session log)
scri reconcile                      # scan .scrinia/ for merge conflicts
scri export api                     # export topic to .scrinia-bundle
scri import ./bundle.scrinia-bundle # import a bundle
scri bundle docs *.md               # bundle raw files
scri setup                          # download embedding model
scri config                         # list workspace settings
scri config plugins:embeddings      # get a setting
scri config plugins:embeddings val  # set a setting
scri migrate --dry-run              # migrate v1 (topic:name) store to v2 (path) layout
```

All commands accept `--workspace-root` to override the workspace directory and `--json` for machine-parseable JSON output.

## Memory naming

| Pattern | Scope | Example |
|---|---|---|
| `subject` | Local store | `scri store session-notes file.md` |
| `topic:subject` | Topic group | `scri store api:auth file.md` |
| `~subject` | Ephemeral (in-memory) | Dies with process |

## MCP tools

2 tools available via `scri serve`.

| Tool | Actions | Description |
|---|---|---|
| `guide` | *(none — standalone)* | Returns the embedded agent guide. Call once per session. |
| `memory` | `remember`/`store`, `recall`/`show`, `forget`, `search`, `list`, `append`, `compact`, `link`, `restore`, `reconcile` | Unified memory dispatcher. Skill paths (`/skill/...`) are routed through it. |

### Built-in skills

Eight skills ship with scrinia and load via `memory('recall', { path: '/skill/{name}' })`:

| Skill | Purpose |
|-------|---------|
| `auditor` | Systematic code, security, and documentation review with sequenced finding IDs |
| `qa` | Test-and-build verification with command-output evidence |
| `debugger` | Scientific-method debugging: observe, hypothesize, isolate, verify |
| `chaos-engineer` | Probe operational resilience: failure domains, blast radius, recovery gaps |
| `onboarder` | Build a codebase mental model for new agents and developers |
| `merge-safety` | Multi-user `.scrinia/` merge conflict prevention and resolution |
| `evolutionary` | Prune stale memories, surface drift, keep skills aligned with practice |
| `self-reflector` | Compare plan vs reality after a unit of work, persist durable lessons |

Projects can override any built-in by writing to `/skill/{name}` — the on-disk version takes precedence and is reusable across sessions.

Plans, retrospectives, agent norms, and findings are all just memories — searchable via `memory('search')`, organized via reserved paths (`/findings/`, `/learn/`, `/agent/`, `/patterns/`, `/sessions/`). No separate database, no separate tools.

## Documentation

### User Guides
- **[Getting Started](docs/getting-started.md)** — overview, installation, quick start
- **[CLI Reference](docs/cli-reference.md)** — commands, configuration, embedding providers, MCP client setup
- **[Server Administration](docs/server-admin.md)** — deployment, authentication, REST API, web UI, Docker
- **[Web UI](docs/web-ui-guide.md)** — React SPA component architecture, dev setup, deployment
- **[Troubleshooting](docs/troubleshooting.md)** — common issues, plugin failures, recovery

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
dotnet test tests/Scrinia.Tests             # 1,206 CLI + MCP + planning + embeddings tests
dotnet test tests/Scrinia.Server.Tests      # 86 server + 18 merge tests
dotnet test tests/Scrinia.Plugin.Embeddings.Tests  # 12 Vulkan plugin + benchmark tests
```

## Running benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) is used for measuring hot-path performance
(BM25 corpus stats, HNSW search). Run from the repo root:

```bash
# Run everything (a full pass takes several minutes)
dotnet run -c Release --project tests/Scrinia.Benchmarks

# Run a subset and emit a machine-readable JSON summary
dotnet run -c Release --project tests/Scrinia.Benchmarks -- \
    --filter "*Bm25*" --exporters json
```

JSON exports land in `tests/Scrinia.Benchmarks/BenchmarkDotNet.Artifacts/` and can be diffed
against a committed baseline to gate regressions in CI.

## License

BSD-3-Clause. Copyright (c) 2026 Nick Daniels.
