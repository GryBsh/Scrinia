# Getting Started with Scrinia

Scrinia gives LLMs persistent, portable memory. It compresses text into compact NMP/2 artifacts, stores them as named memories in a `.scrinia/` workspace, and exposes them through MCP tools and a CLI.

## How It Works

1. An LLM (or you) stores text as a named memory: `scri memory store session-notes ./notes.md`
2. Scrinia compresses it with Brotli, indexes it for BM25 + weighted-field search, and (optionally) embeds it for semantic vector search.
3. Later, the LLM searches for relevant context: `scri memory search "authentication flow"`
4. Scrinia returns ranked results from across all stored memories.

Memories persist in a `.scrinia/` directory alongside your project (like `.git/`), travel with the code, and work across sessions.

## How it runs

One way: `scri` is both a command-line tool and an MCP server over stdio. Your AI coding tool
launches it; it reads and writes `.scrinia/` on disk.

Sharing memories with other people is a git question, not a deployment one — `.scrinia/` is
committed like any other directory, and a merge driver resolves what conflicts. See
[Multi-User Setup](multi-user-setup.md).

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

The embedding model (`m2v-MiniLM-L6-v2`, ~22MB) downloads automatically on first `scri serve` — semantic search is built in, no plugins needed. To pre-download (e.g., before going offline or in CI):

```bash
scri setup
```

For optional Vulkan GPU acceleration:

```powershell
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithEmbeddings
```

## Quick Start

### 1. Initialize a Workspace

Scrinia stores data in `.scrinia/` at your project root. It's created automatically on first use:

```bash
cd /path/to/your/project
scri memory list    # creates .scrinia/ if needed
```

### 2. Store a Memory

```bash
# From a file
scri store /api/auth-flow ./docs/auth.md -d "OAuth2 authentication flow"

# From stdin
echo "Always use snake_case for API endpoints" | scri memory store conventions -
```

### 3. Search

```bash
scri memory search "authentication"
```

### 4. Connect an MCP Client

Add to your MCP client configuration (e.g., `.mcp.json` for Claude Code):

```json
{
  "mcpServers": {
    "scrinia": {
      "command": "scri",
      "args": ["serve", "--workspace-root", "/path/to/your/project"],
      "transport": "stdio"
    }
  }
}
```

`scri serve` resolves its workspace from the nearest enclosing git repository relative to
its working directory; passing `--workspace-root` explicitly makes the server independent
of whatever directory the client launches it from.

Now your AI assistant has access to Scrinia's MCP tools — `guide` plus per-verb memory tools (`remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`, `reconcile`) and the `facts` entity-query tool — for persistent memory, skills, and session recall. See [CLI Reference](cli-reference.md) for full details.

## Memory Naming

Memories are organized into three scopes:

| Pattern | Scope | Storage | Lifetime |
|---------|-------|---------|----------|
| `subject` | Local | `.scrinia/store/subject.nmp2` | Persistent |
| `/topic/subject` | Topic | `.scrinia/topics/topic/subject.nmp2` | Persistent |
| `~subject` | Ephemeral | In-memory only | Dies with process |

**Topics** group related memories (e.g., `/api/auth`, `/api/endpoints`, `/arch/decisions`). Use them to organize knowledge by domain.

**Ephemeral** memories are scratch space that disappears when the process exits. Useful for temporary context within a session.

## MCP Tools Overview

When connected via MCP, Scrinia exposes one tool per operation, each with its own schema. Grouped by purpose:

| Group | Tools | Purpose |
|------|---------|---------|
| Lifecycle | `guide`, `restore`, `reconcile` | Session playbook + context restore, and merge-conflict resolution |
| Memory | `remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link` | Store, read, find, and maintain memories |
| Facts | `facts` | Aggregate/relational entity queries (`entities`, `profile`, `intersect`) |

`guide` is standalone — call it once per session. Skill paths (`/skill/...`) are routed through `remember` (create/override) and `recall` (load/list), not a separate tool.

### Reserved Paths

A small set of paths have first-class behavior; everything else is plain memory grouped by its first segment.

| Path | Purpose |
|------|---------|
| `/skill/...` | Reusable specialist prompts (built-in or override). Stored at `.scrinia/skills/{name}.md`. |
| `/agent/...` | Agent profile and behavioral norms. |
| `/patterns/...` | Recurring patterns and conventions. |
| `/sessions/...` | Session logs by date (`YYYY-MM-DD`). |
| `/checkpoint/...` | State snapshots. |
| `/temp/...` | Ephemeral — an alias for `~subject`; both spell the same memory. |

### Built-in Skills

The following ship with scrinia and load via `recall({ path: '/skill/{name}' })`:

| Skill | Purpose |
|-------|---------|
| `auditor` | Systematic code, security, and documentation review with sequential finding IDs |
| `debugger` | Scientific method debugging: observe, hypothesize, isolate, verify, store |
| `chaos-engineer` | Probe operational resilience: failure domains, blast radius, recovery gaps |
| `onboarder` | Build a codebase mental model for new agents and developers |
| `evolutionary` | Proactive knowledge and skill improvement, stale memory scanning |
| `merge-safety` | Multi-user merge conflict prevention and resolution guidance |
| `qa` | Quality assurance validation: test coverage, acceptance criteria, regression checks |
| `self-reflector` | Agent self-assessment: identify blind spots, biases, and improvement opportunities |

Built-in skills are evolvable — `remember({ path: '/skill/{name}', content: [...] })` creates a project-specific override on disk.

## What's Next

- **[CLI Reference](cli-reference.md)** -- Full command reference, configuration, embedding providers, MCP client setup
- **[Multi-User Setup](multi-user-setup.md)** -- Sharing a workspace through git, and the merge driver
- **[Architecture Overview](architecture/overview.md)** -- System design, project structure, dependency graph
