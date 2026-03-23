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
git clone https://github.com/nickd-scrinia/scrinia.git
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

Now your AI assistant has access to 9 MCP tools for persistent memory and project planning (3 memory + 6 planning). See [CLI Reference](cli-reference.md) for full details.

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

**Topics** group related memories (e.g., `api:auth`, `api:endpoints`, `arch:decisions`). Use them to organize project knowledge by domain. The planning tools use reserved topic prefixes: `project:*`, `plan:*`, `task:*`, `learn:*`, and `agent:*`.

**Ephemeral** memories are scratch space that disappears when the process exits. Useful for temporary context within a session.

## MCP Tools Overview

When connected via MCP, Scrinia exposes 9 tools using a noun-action pattern: 3 memory tools and 6 planning tools.

### Memory Tools (3)

| Tool | Actions | Purpose |
|------|---------|---------|
| `memory` | `store`, `show`, `append`, `list`, `search`, `forget`, `copy`, `restore`, `reconcile`, `resolve` | Core memory operations: persist, retrieve, search, and manage memories |
| `guide` | *(none — standalone)* | Session playbook (call once per session) |
| `bundle` | `export`, `import` | Export/import memory bundles for portability |

### Planning Tools (6)

| Tool | Actions | Purpose |
|------|---------|---------|
| `plan` | `init`, `tasks`, `verify`, `gaps`, `retro`, `profile`, `status` | Project lifecycle: initialize, decompose, verify, and review |
| `requirement` | `add`, `list` | Define and list categorized requirements with REQ-IDs |
| `goal` | `add`, `complete`, `list` | Manage project goals |
| `task` | `next`, `complete` | Execute tasks: get unblocked work and mark complete |
| `concern` | `add`, `resolve`, `list` | Track and resolve project concerns |
| `skill` | `create`, `load` | Create and load reusable specialist skills |

**Built-in skills** (12) ship with scrinia and are always available via `skill('load')`:

| Skill | Purpose |
|-------|---------|
| `planner` | Wave-aware execution planning: file conflict detection, agent specs, merge strategy |
| `auditor` | Systematic code, security, and documentation review with sequential finding IDs |
| `debugger` | Scientific method debugging: observe, hypothesize, isolate, verify, store |
| `chaos-engineer` | Probe operational resilience: failure domains, blast radius, recovery gaps |
| `onboarder` | Build a codebase mental model for new agents and developers |
| `sos-handler` | Triage agent SOS signals: spawn specialists, create skills, replan waves |
| `evolutionary` | Proactive knowledge and skill improvement, stale memory scanning |
| `cartographer` | Cross-domain connection indexing and bridge discovery |
| `march-reporter` | Produce human-readable goal summary documents for audit trails |
| `merge-safety` | Multi-user merge conflict prevention and resolution guidance |
| `qa` | Quality assurance validation: test coverage, acceptance criteria, regression checks |
| `self-reflector` | Agent self-assessment: identify blind spots, biases, and improvement opportunities |

Built-in skills are evolvable — `skill('create')` with the same name creates a project-specific override.

Planning tools use dedicated topic conventions: `project:*` for project state, `plan:*` for roadmaps, `task:*` for individual tasks, `learn:*` for retrospective outcomes, and `agent:*` for agent behavioral norms. The `memory('list')` and `memory('search')` actions support `excludeTopics` to filter planning topics out of general queries.

## Planning Quick Start

Scrinia's 6 planning tools (with their actions) let an agent manage a full project lifecycle. Here's a minimal flow:

```
# 1. Initialize a project
plan('init', context: "Build a REST API for user management with JWT auth")

# 2. Define requirements
requirement('add', requirements: "## v1\n- AUTH-01: User registration\n- AUTH-02: JWT login\n- API-01: CRUD endpoints")

# 3. Set a goal (auto-creates researcher → auditor → planner seed tasks)
goal('add', title: "Build auth and API", description: "Implement user management with JWT")

# 4. Decompose Phase 1 into tasks
plan('tasks', phaseId: "01", tasks: "## Task 01\nDepends on: none\nAction: Create registration endpoint\nAcceptance criteria:\n- POST /users returns 201")

# 5. Execute: get task → do the work → mark complete
task('next', phaseId: "01")      # returns unblocked tasks
task('complete', taskName: "task:01-1-01", outcome: "Registration endpoint created. Tests pass.")

# 6. Check project status anytime
plan('status')                   # current phase, progress, blockers

# 7. Resume anytime after context loss
memory('restore')                # restores full project state
```

See [Planning Tools Guide](planning-tools.md) for full documentation of all 6 planning tools.

## What's Next

- **[Planning Tools Guide](planning-tools.md)** -- Full planning lifecycle reference with examples
- **[CLI Reference](cli-reference.md)** -- Full command reference, configuration, embedding providers, MCP client setup
- **[Server Administration](server-admin.md)** -- Deployment, authentication, REST API, Web UI, Docker
- **[Architecture Overview](architecture/overview.md)** -- System design, project structure, dependency graph
