# scrinia

[![License: BSD-3-Clause](https://img.shields.io/badge/License-BSD--3--Clause-blue.svg)](LICENSE)

Persistent, portable memory for LLMs. Compresses text into NMP/2 artifacts, stores them locally, and exposes 17 MCP tools for agents to remember, search, and share knowledge across sessions. Zero infrastructure required.

## Install

Build from source (.NET 10 SDK required):

```bash
git clone https://github.com/nickd-scrinia/scrinia
cd scrinia
dotnet build

# Publish trimmed single-file binary
.\publish.ps1 -OutputDir ./dist -Platform win-x64

# With semantic search (embeddings plugin)
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithEmbeddings
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
scri list                           # list all memories
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

All commands accept `--workspace-root` to override the workspace directory.

## Memory naming

| Pattern | Scope | Example |
|---|---|---|
| `subject` | Local store | `scri store session-notes file.md` |
| `topic:subject` | Topic group | `scri store api:auth file.md` |
| `~subject` | Ephemeral (in-memory) | Dies with process |

## MCP tools

18 tools available via `scri serve`:

| Tool | Description |
|---|---|
| `guide` | Session playbook (call once per session) |
| `store` / `append` | Persist or incrementally add to memories |
| `show` / `get_chunk` | Retrieve full content or individual chunks |
| `list` / `search` | Browse and search with BM25 + semantic scoring |
| `copy` / `forget` | Move between scopes or delete |
| `export` / `import` | Portable .scrinia-bundle files |
| `encode` / `chunk_count` | Low-level NMP/2 encoding |
| `ka` / `kt` | Knowledge analysis and transfer |
| `budget` / `reflect` / `ingest` | Session lifecycle tools |

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
dotnet test tests/Scrinia.Tests             # 342 CLI + MCP tests
dotnet test tests/Scrinia.Server.Tests      # 53 server tests
dotnet test tests/Scrinia.Plugin.Embeddings.Tests  # 59 plugin tests
```

## License

BSD-3-Clause. Copyright (c) 2026 Nick Daniels.
