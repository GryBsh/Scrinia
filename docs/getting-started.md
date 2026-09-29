# Getting Started with Scrinia

Scrinia gives LLMs persistent, portable memory. It compresses text into compact NMP/2 artifacts, stores them as named memories in a `.scrinia/` workspace, and exposes them through MCP tools and a CLI.

## How It Works

1. An LLM (or you) stores text as a named memory: `scri store session-notes ./notes.md`
2. Scrinia compresses it with Brotli, indexes it for BM25 + weighted-field search, and (optionally) embeds it for semantic vector search.
3. Later, the LLM searches for relevant context: `scri search "authentication flow"`
4. Scrinia returns ranked results from across all stored memories.

Memories persist in a `.scrinia/` directory alongside your project (like `.git/`), travel with the code, and work across sessions.

## How it runs

One way: `scri` is both a command-line tool and an MCP server over stdio. Your AI coding tool
launches it; it reads and writes `.scrinia/` on disk.

Sharing memories across a team uses ordinary source-control workflows: commit `.scrinia/` like any
other directory, and share the same memories between repositories with a shared base registered via
`scri shared base add`. See [Sharing Memory Across a Team](multi-user-setup.md).

## Installation

Shell examples marked `sh` use POSIX syntax; PowerShell examples are marked explicitly.

### Install the global tool

Install the immutable `scri` global tool from NuGet:

```sh
dotnet tool install --global scrinia
```

This installs the `scri` command onto your global tool path. Verify with `scri --version`. All
mutable data — configuration, models, plugins, and plugin state — lives below
[`SCRINIA_HOME`](installation.md#where-data-lives); the installed package directory is immutable and
is never written at runtime.

For local development, build from source instead:

### Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/nickd-scrinia/scrinia.git
cd scrinia
dotnet build
```

### Publish Trimmed Binary (CLI)

```powershell
.\publish.ps1 -OutputDir ./dist -Platform win-x64
```

Produces a single-file `scri.exe` on Windows and `scri` on Linux/macOS (~50 MB). Available platforms: `win-x64`, `linux-x64`, `osx-arm64`.

When `scri serve` starts, it automatically downloads the built-in Model2Vec embedding model (`m2v-MiniLM-L6-v2`, approximately 50 MB total including supporting files) on first startup unless you pass `--no-auto-setup`. The model is
stored in `SCRINIA_HOME/models/m2v-MiniLM-L6-v2/`; if it is unavailable, semantic search falls back
to BM25. Configured HTTP providers and the optional Vulkan provider may use their own configured
models instead of the built-in Model2Vec model.

You can also download the built-in model explicitly with `scri setup`:

```sh
scri setup
```

For optional Vulkan GPU acceleration:

```powershell
.\publish.ps1 -OutputDir ./dist -Platform win-x64 -WithEmbeddings
```

## Model2Vec

Scrinia's default embedding provider is the in-process **Model2Vec** model (`m2v-MiniLM-L6-v2`), a
384-dimensional distillation of all-MiniLM-L6-v2 with zero native dependencies. On first startup
`scri serve` (or `scri setup`) downloads it to `SCRINIA_HOME/models/m2v-MiniLM-L6-v2/`; if it is
unavailable, semantic search falls back to BM25.

Models are mutable runtime state and live only below `SCRINIA_HOME` — never inside the immutable
global-tool package. HTTP and Vulkan providers may use their own configured models instead. See
[Installation](installation.md) for the full `SCRINIA_HOME` layout.

## Quick Start

### 1. Create the Workspace with the First Write

Scrinia stores data in `.scrinia/` at the resolved workspace root. The first write creates the
directory automatically:

```sh
cd /path/to/your/project
scri store first-memory ./notes.md
```

### 2. Store a Memory

```sh
# From a file
scri store /api/auth-flow ./docs/auth.md -d "OAuth2 authentication flow"

# From stdin
echo "Always use snake_case for API endpoints" | scri store conventions -
```

### Addressing rules

Scrinia uses logical memory addresses rather than filesystem paths or URIs. Persistent memories
use a path such as `/api/auth-flow`; a bare subject such as `session-notes` is accepted by the CLI
and normalized to a persistent address. Ephemeral memories use the canonical `~name` form, and
`/temp/name` is an alias for the same in-process memory.

`file://` values, drive-letter paths, and other URI or physical-path forms are rejected. In .NET
integrations, parse input with `MemoryAddress.Parse(raw)` or validate it with
`MemoryAddress.TryParse(raw, out address, out error)` rather than passing raw address strings
through the store or plugin contracts.

The CLI is persistent-only: its memory commands reject `~name` and `/temp/name` because each
invocation exits immediately, so an ephemeral write could not survive. Use an MCP client connected
to the long-lived `scri serve` process for ephemeral memories.

### 3. Search

```sh
scri search "authentication"
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

Unless `--no-auto-setup` is supplied, this first `scri serve` startup also downloads the built-in
Model2Vec model when it is missing. If you configure an HTTP embedding provider or the Vulkan
provider, that provider may use its own model and model-download process. If the built-in model
is not available, Scrinia still starts and search falls back to BM25.

Now your AI assistant has access to Scrinia's MCP tools — `guide` plus per-verb memory tools (`remember`, `recall`, `search`, `list`, `append`, `forget`, `compact`, `link`, `restore`) and the `facts` entity-query tool — for persistent memory, skills, and session recall. See [CLI Reference](cli-reference.md) for full details.

## Memory Naming

Memories are organized into three scopes:

| Pattern | Scope | Storage | Lifetime |
|---------|-------|---------|----------|
| `subject` | Local | `.scrinia/memories/subject.nmp2` | Persistent |
| `/topic/subject` | Topic | `.scrinia/memories/topic/subject.nmp2` | Persistent |
| `~subject` | Ephemeral | In-memory only | Dies with process |

**Topics** group related memories (e.g., `/api/auth`, `/api/endpoints`, `/arch/decisions`). Use them to organize knowledge by domain.

**Ephemeral** memories are scratch space that disappears when the process exits. Useful for temporary context within a session.

The two spellings `~subject` and `/temp/subject` address the same ephemeral memory; responses use
the canonical `~subject` spelling. The CLI does not support either spelling.

## MCP Tools Overview

When connected via MCP, Scrinia exposes one tool per operation, each with its own schema. Grouped by purpose:

| Group | Tools | Purpose |
|------|---------|---------|
| Lifecycle | `guide`, `restore` | Session playbook + context restore |
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
| `qa` | Quality assurance validation: test coverage, acceptance criteria, regression checks |
| `self-reflector` | Agent self-assessment: identify blind spots, biases, and improvement opportunities |

Built-in skills are evolvable — `remember({ path: '/skill/{name}', content: [...] })` creates a project-specific override on disk.

## Workspace Tracking and Shared Bases

Two configuration files track workspace state distinctly:

- `<workspace-root>/.scrinia/config.json` remains a flat `Dictionary<string,string>`. The owned
  scalars are `Scrinia:Workspace:Tracking` (`local` or `project-git`) and
  `Scrinia:Shared:Enabled` (an ordered, semicolon-separated list of canonical names). An absent
  Enabled key means zero enabled bases; an empty value is invalid.
- `SCRINIA_HOME/config.json` is the typed per-user registry with `schemaVersion: 1` and a
  `sharedBases` object mapping canonical names to repository roots.

`SCRINIA_HOME` resolves to a non-empty absolute environment value exactly. An empty or relative
override fails closed. When unset, it resolves exactly to
`Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scrinia")`.
Tracking configuration resolves in this order: `SCRINIA_WORKSPACE_TRACKING`,
`SCRINIA__WORKSPACE__TRACKING`, the workspace scalar, then the command default. The Enabled scalar
is read only from the workspace file; neither `SCRINIA_SHARED_ENABLED` nor
`SCRINIA__SHARED__ENABLED` overrides it.

The configuration command forms are:

```text
scri shared base add <name> <absolute-root> [--json]
scri shared base remove <name> [--json]
scri shared base list [--all] [--json]
scri shared enable <name> [--json]
scri shared disable <name> [--json]
scri tracking status [--json]
scri tracking set local|project-git [--json]
scri tracking migrate-legacy --to local|project-git [--json]
```

Every registered shared base uses the fixed `project-git` policy. Names are canonical lowercase
values matching `^[a-z][a-z0-9-]{0,31}$`; roots must be existing repository/worktree roots. The
commands inspect filesystem evidence only, never invoke Git or inspect its index, and report
`gitIndexStatus: UNVERIFIED`.

Configuration mutations use an interprocess lock and same-directory atomic replacement. A failed
write preserves the prior complete state or leaves a deterministic recovery journal. Unrelated
workspace configuration and user lines outside managed ignore blocks are preserved. Registry names
disabled for a workspace remain private: they are not emitted by workspace resolution or diagnostics;
only the explicit user `scri shared base list --all` command enumerates the complete registry,
including disabled entries. Without `--all`, the list is limited to enabled entries.

Tracking migration recognizes `fresh`, `policy-required`, `legacy-clean`, `legacy-mixed`,
`legacy-manual`, `legacy-unsafe`, `local`, `project-git`, and `policy-drift`. The declared errors
include `CLI_INVALID_ARGUMENT`, `WORKSPACE_CONFIG_MALFORMED`, `USER_CONFIG_MALFORMED`,
`USER_CONFIG_SCHEMA_UNSUPPORTED`, `SHARED_BASE_ALREADY_EXISTS`, `SHARED_BASE_NOT_FOUND`,
`SHARED_BASE_ROOT_COLLISION`, `SHARED_BASE_UNSAFE_PATH`, `SHARED_BASE_NOT_GIT_ROOT`,
`SHARED_TRACKING_POLICY_DRIFT`, `LEGACY_MIGRATION_REQUIRED`, `MANAGED_FILE_UNSAFE`,
`TRACKING_POLICY_REQUIRED`, `TRACKING_POLICY_INVALID`, `MIGRATION_MANUAL_CLEANUP_REQUIRED`,
`CONFIG_LOCK_TIMEOUT`, `CONFIG_WRITE_FAILED`, `CONFIG_RECOVERY_REQUIRED`, and `IO_FAILURE`.

## What's Next

- **[CLI Reference](cli-reference.md)** -- Full command reference, configuration, embedding providers, MCP client setup
- **[Architecture Overview](architecture/overview.md)** -- System design, project structure, dependency graph
