# Getting Started with Scrinia

Scrinia gives LLMs persistent, portable memory. It compresses text into compact NMP/2 artifacts, stores them as named memories in a `.scrinia/` workspace, and exposes them through MCP tools, a CLI, an HTTP API, and a web UI.

## How It Works

1. An LLM (or you) stores text as a named memory: `scri store session-notes ./notes.md`
2. Scrinia compresses it with Brotli, indexes it for BM25 + weighted-field search, and (optionally) embeds it for semantic vector search.
3. Later, the LLM searches for relevant context: `scri search "authentication flow"`
4. Scrinia returns ranked results from across all stored memories.

Memories persist in a `.scrinia/` directory alongside your project (like `.git/`), travel with the code, and work across sessions.

## Deployment Modes

| Mode | Best for | Transport |
|------|----------|-----------|
| **CLI + MCP** | Single developer, local AI coding tools | stdio |
| **HTTP API Server** | Teams, multi-user, remote access | HTTP REST + MCP over HTTP |
| **Docker** | Production deployment | HTTP (containerized) |

## Installation

### Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/yourusername/scrinia.git
cd scrinia
dotnet build
```

### Publish Trimmed Binary (CLI)

```powershell
.\publish.ps1 -OutputDir ./dist -Platform win-x64
```

Produces a single-file `scri.exe` (~50 MB). Available platforms: `win-x64`, `linux-x64`, `osx-arm64`.

Then download the Model2Vec embedding model (~22MB) for semantic search:

```bash
scri setup
```

Semantic search is built-in -- no plugins needed. For optional Vulkan GPU acceleration:

```powershell
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithVulkan
```

### Docker (Server)

```bash
docker compose up -d
```

See [Server Administration](server-admin.md) for full deployment options.

## Quick Start: CLI + MCP

### 1. Initialize a Workspace

Scrinia stores data in `.scrinia/` at your project root. It's created automatically on first use:

```bash
cd /path/to/your/project
scri list    # creates .scrinia/ if needed
```

### 2. Store a Memory

```bash
# From a file
scri store api:auth-flow ./docs/auth.md -d "OAuth2 authentication flow"

# From stdin
echo "Always use snake_case for API endpoints" | scri store conventions -
```

### 3. Search

```bash
scri search "authentication"
```

### 4. Connect an MCP Client

Add to your MCP client configuration (e.g., `.mcp.json` for Claude Code):

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

Now your AI assistant has access to 30 MCP tools for persistent memory and project planning. See [CLI Reference](cli-reference.md) for full details.

## Quick Start: HTTP API Server

### 1. Start the Server

```bash
dotnet run --project src/Scrinia.Server
```

The server starts on `http://localhost:5000`. A bootstrap API key is written to `BOOTSTRAP_KEY` in the data directory on first run.

### 2. Authenticate

```bash
# Read the bootstrap key
cat $LOCALAPPDATA/scrinium/BOOTSTRAP_KEY

# Use it to create a scoped key
curl -X POST http://localhost:5000/api/v1/keys \
  -H "Authorization: Bearer <bootstrap-key>" \
  -H "Content-Type: application/json" \
  -d '{"userId": "dev", "stores": ["default"], "permissions": ["read","search","store","append","forget"]}'
```

### 3. Store and Search via API

```bash
KEY="scri_..."

# Store
curl -X POST http://localhost:5000/api/v1/stores/default/memories \
  -H "Authorization: Bearer $KEY" \
  -H "Content-Type: application/json" \
  -d '{"name": "notes", "content": ["My project notes..."], "description": "Project notes"}'

# Search
curl "http://localhost:5000/api/v1/stores/default/search?query=project" \
  -H "Authorization: Bearer $KEY"
```

See [Server Administration](server-admin.md) for full API reference.

## Memory Naming

Memories are organized into three scopes:

| Pattern | Scope | Storage | Lifetime |
|---------|-------|---------|----------|
| `subject` | Local | `.scrinia/store/subject.nmp2` | Persistent |
| `topic:subject` | Topic | `.scrinia/topics/topic/subject.nmp2` | Persistent |
| `~subject` | Ephemeral | In-memory only | Dies with process |

**Topics** group related memories (e.g., `api:auth`, `api:endpoints`, `arch:decisions`). Use them to organize project knowledge by domain. The planning tools use reserved topic prefixes: `project:*`, `plan:*`, `task:*`, `learn:*`, and `user:*`.

**Ephemeral** memories are scratch space that disappears when the process exits. Useful for temporary context within a session.

## MCP Tools Overview

When connected via MCP, Scrinia exposes 30 tools across two tool classes: 18 memory tools (`ScriniaMcpTools`) and 12 planning tools (`ScriniaProjectTools`).

### Memory Tools (18)

| Tool | Purpose |
|------|---------|
| `store` | Compress and persist text as a named memory |
| `show` | Retrieve and decode a memory's full content |
| `append` | Add a chunk to an existing memory |
| `list` | Summary of memories (default) or paginated full listing |
| `search` | Hybrid BM25 + semantic search across memories |
| `forget` | Delete a memory |
| `copy` | Copy a memory between scopes |
| `encode` | Compress text to an NMP/2 artifact (inline) |
| `chunk_count` | Count chunks in a multi-chunk artifact |
| `get_chunk` | Decode a specific chunk |
| `export` | Export topics to a portable bundle |
| `import` | Import memories from a bundle |
| `budget` | Show token consumption for this session |
| `guide` | Session playbook (call once per session) |
| `reflect` | End-of-session reflection prompt |
| `ingest` | Full knowledge ingestion playbook |
| `ka` | Knowledge analysis -- inventory, gap analysis, report to user |
| `kt` | Knowledge transfer -- runs ka(), then produces per-topic KT documents |

### Planning Tools (12)

| Tool | Purpose |
|------|---------|
| `project_init` | Initialize a project with goals, context, and constraints |
| `plan_requirements` | Store categorized requirements with REQ-IDs |
| `plan_roadmap` | Store a phased roadmap mapping requirements to phases |
| `plan_tasks` | Decompose a phase into task memories with wave/dependency metadata |
| `plan_resume` | Resume project context after context loss |
| `plan_status` | Query current project status, phase, and blockers |
| `task_next` | Get all unblocked tasks in the current wave for a phase |
| `task_complete` | Mark a task complete with outcome metadata |
| `plan_verify` | Verify a phase achieved its goal using success criteria |
| `plan_gaps` | Create gap closure tasks for failed verification criteria |
| `plan_retrospective` | Store a structured phase retrospective in `learn:execution-outcomes` |
| `plan_profile` | Store or update user preferences for agent behavior |

Planning tools use dedicated topic conventions: `project:*` for project state, `plan:*` for roadmaps, `task:*` for individual tasks, `learn:*` for retrospective outcomes, and `user:*` for user preferences. The `list` and `search` memory tools support `excludeTopics` to filter planning topics out of general queries.

## What's Next

- **[CLI Reference](cli-reference.md)** -- Full command reference, configuration, embedding providers, MCP client setup
- **[Server Administration](server-admin.md)** -- Deployment, authentication, REST API, Web UI, Docker
- **[Architecture Overview](architecture/overview.md)** -- System design, project structure, dependency graph
