# Planning Tools Guide

Scrinia's planning system uses 6 MCP tools with noun('action') dispatch syntax.

## Goal-Driven Lifecycle

```
plan('init') (one-time)
  |
Pre-plan: scan codebase -> concern('add')
  |
goal('add') <--------------------------------------- (next goal)
  |                                                       ^
auto-creates: researcher (wave 0) -> auditor (wave 1)    |
  -> planner (wave 2) seed tasks                         |
  |                                                       |
task('next') -> spawn agent -> task('complete')           |
  |                                                       |
concern('resolve') + gate tasks (QA, self-reflector)      |
  |                                                       |
goal('complete') -----------------------------------------+
```

**Recovery at any point:** `memory('restore')` / `plan('status')`
**Agent behavioral norms:** stored via `memory('store')` in `agent:profile`

### Absorbed capabilities

These old standalone tools no longer exist. Their functionality is provided by other mechanisms:

| Old tool | Replacement |
|---|---|
| `plan_verify` | qa built-in skill (auto-injected gate task) |
| `plan_retrospective` | self-reflector built-in skill (auto-injected gate task) |
| `plan_profile` | `agent:profile` memory (stored via `memory('store')`) |
| `plan_roadmap` | auto-created by `goal('add')` |
| `plan_requirements` | `requirement('add')` |
| `plan_resume` | `memory('restore')` |
| `plan_gaps` | gap tasks created directly via `plan('tasks')` |
| `research_start` / `research_complete` | researcher seed task pattern (auto-created by `goal('add')`) |

### One-time setup

`plan('init', { context: "..." })` — call once per workspace. In an existing codebase, this returns guidance to scan for concerns and build knowledge before setting goals. In an empty workspace, it directs you to set a goal immediately.

### Pre-planning (existing codebases)

Before setting your first goal, scan the codebase to build understanding:
- `concern('add', { description, severity, phaseScope })` — track risks, tech debt, issues

Concerns persist across goals — they accumulate over the project's lifetime and inform future planning.

### The goal cycle

Goals are the top-level unit of work. Each goal auto-creates researcher, auditor, and planner seed tasks. After completing a goal, set the next one. Accumulated concerns and knowledge carry forward.

---

## Tools

### plan

Actions: `init`, `tasks`, `status`

#### plan('init')

Initialize a new project. Creates `project:context` and `project:state` memories.

**Parameters:**
- `context` (string, required) — Free-text describing project goals, context, constraints, and scope

**Example call:**
```
plan('init', { context: "Build a REST API for user management. Must use PostgreSQL. Deploy to AWS." })
```

**What it stores:**
- `project:context` — the full context text
- `project:state` — tracking state (phase, progress, last action, next step)

#### plan('tasks')

Create task definitions from planner output. Auto-injects gate tasks: QA + self-reflector per phase; evolutionary, cartographer, march-reporter on last phase.

**Parameters:**
- `tasks` (string, required) — Free-text task definitions in structured format
- `phaseId` (string, optional) — Two-digit phase number (e.g., `"01"`)

**Task format:**
```
## Task 01
Depends on: none
Action: Create user registration endpoint with email/password validation
Acceptance criteria:
- POST /api/users returns 201 with JWT
- Duplicate email returns 409

## Task 02
Depends on: none
Action: Create JWT middleware for route protection
Acceptance criteria:
- Protected routes return 401 without token
- Valid token passes through

## Task 03
Depends on: 01, 02
Action: Integration test for registration + auth flow
Acceptance criteria:
- Register, login, access protected route in one test
```

**Key concepts:**
- **Waves** are computed from the dependency graph — no need to specify them
- Tasks with no dependencies go to wave 1 (can run in parallel). Tasks depending on wave N go to wave N+1
- **Dependencies** reference task IDs (e.g., `01`, `02`), not qualified names
- Task metadata stored as keywords: `status:pending`, `wave:1`, `phase:01`, `goal:G-14`, `depends_on:01-1-01`
- Gate tasks (QA, self-reflector, etc.) are auto-injected — do not define them manually

#### plan('status')

Show project progress, active goal, blockers, and next action.

**Parameters:** None

**Response format:**
```
Project: my-api-project
Phase: Phase 01 — Auth Foundation
Progress: 50%
Last action: Completed task:01-1-01
Blockers: none
Next: run task('next') to get the next pending task
Roadmap: 2 phase(s) defined
```

---

### task

Actions: `next`, `complete`

#### task('next')

Get the next unblocked task(s) for the active goal. Returns wave information and spawn guidance.

**Parameters:**
- `phaseId` (string, optional) — Two-digit phase number. Auto-detects if omitted.

**Example call:**
```
task('next')
task('next', { phaseId: "01" })
```

**Response:**
```
Phase 01 — Wave 1 — 2 unblocked task(s):

## task:g14-01-1-01
Action: Create user registration endpoint...
Acceptance criteria:
- POST /api/users returns 201 with JWT
...

## task:g14-01-1-02
Action: Create JWT middleware...
```

**How it works:**
- Keyword-only index scan (no artifact decode during filtering) — fast even with hundreds of tasks
- Filters: `phase:{phaseId}` -> `status:pending` -> lowest wave -> unblocked dependencies
- Returns ALL unblocked tasks — the agent decides which to execute and in what order

#### task('complete')

Mark a task as done with outcome metadata.

**Parameters:**
- `taskName` (string, required) — Qualified task name (e.g., `"task:g14-01-1-01"`)
- `outcome` (string, required) — What was done, any deviations

**Example call:**
```
task('complete', { taskName: "task:g14-01-1-01", outcome: "Created registration endpoint at POST /api/users. Added email uniqueness check. Tests pass." })
```

**What happens:**
- Updates `status:pending` -> `status:complete` keyword via record with-expression + `Upsert`
- Appends outcome to execution log as a new chunk
- Updates `project:state` with last action and computed progress

**No-archiving design:** Both `task('complete')` and `project:state` updates deliberately skip `ArchiveVersion`:
- **task('complete')**: Status keyword changes are frequent, mechanical updates — archiving every status flip would create massive version bloat with no useful history.
- **project:state**: Updated by every planning tool call (progress, last action, next step). Archiving would produce dozens of near-identical snapshots per session. State can always be rebuilt from `project:context` + task index via `plan('status')`.

---

### goal

Actions: `add`, `edit`, `complete`, `list`

#### goal('add')

Create a new goal. Auto-creates researcher (wave 0) -> auditor (wave 1) -> planner (wave 2) seed tasks.

**Parameters:**
- `description` (string, required) — Goal description

**Example call:**
```
goal('add', { description: "Implement JWT authentication for all API endpoints" })
```

**What happens:**
- Creates a goal with sequential ID (G-1, G-2, ...)
- Auto-creates three seed tasks: researcher, auditor, planner
- The orchestrator executes these via `task('next')` -> spawn -> `task('complete')`

#### goal('complete')

Complete the active goal. Blocks on open concerns. Auto-appends session log and checkpoint.

**Parameters:**
- `goalId` (string, optional) — Goal ID to complete. Uses active goal if omitted.
- `outcome` (string, optional) — Outcome note

**Example call:**
```
goal('complete', { outcome: "All auth endpoints implemented and tested." })
```

#### goal('list')

List all goals with status.

**Parameters:** None

#### goal('edit')

Update goal description.

**Parameters:**
- `goalId` (string, optional) — Goal ID to update
- `description` (string, optional) — Updated description

---

### concern

Actions: `add`, `resolve`, `list`

#### concern('add')

Track a risk or issue. Sequential IDs by category: SEC-NNN, QAL-NNN, DOC-NNN.

**Parameters:**
- `description` (string, required) — Concern description
- `severity` (string, optional) — `high`, `medium`, or `low`
- `phaseScope` (string, optional) — Phase this concern applies to
- `id` (string, optional) — Custom concern ID (auto-generated if omitted)

**Example call:**
```
concern('add', { description: "No input validation on user registration", severity: "high", phaseScope: "01" })
```

#### concern('resolve')

Mark a concern resolved.

**Parameters:**
- `concernName` (string, required) — Concern name to resolve
- `resolution` (string, optional) — Resolution notes
- `verifiedBy` (string, optional) — Who verified: `debugger`, `qa`, `manual`

**Example call:**
```
concern('resolve', { concernName: "SEC-001", resolution: "Added input validation middleware", verifiedBy: "qa" })
```

#### concern('list')

List active concerns, optionally filtered by phase.

**Parameters:**
- `phaseFilter` (string, optional) — Filter by phase

---

### skill

Actions: `load`, `create`

#### skill('load')

Load a built-in or project skill. Without `name`, lists all available skills.

**Parameters:**
- `name` (string, optional) — Skill name to load. Omit to list available skills.
- `reconcile` (boolean, optional) — Show both built-in and override for reconciliation

**12 built-in skills:** planner, auditor, debugger, chaos-engineer, onboarder, sos-handler, evolutionary, cartographer, march-reporter, merge-safety, qa, self-reflector

**Example calls:**
```
skill('load')                          # list available skills
skill('load', { name: "planner" })     # load a specific skill
```

#### skill('create')

Create a project-specific skill from a scaffold.

**Parameters:**
- `name` (string, required) — Skill name
- `scaffold` (string, optional) — Scaffold type: `researcher`, `reviewer`, `domain-expert`, `custom`
- `instructions` (string, optional) — Additional instructions for the skill
- `tools` (string, optional) — Comma-separated tool names (for `custom` scaffold)

**Example call:**
```
skill('create', { name: "api-reviewer", scaffold: "reviewer", instructions: "Focus on REST conventions and error handling" })
```

**Precedence:** Project skills override built-in skills of the same name. Use `skill('load', { reconcile: true })` to compare.

---

### requirement

Actions: `add`, `resolve`, `list`

#### requirement('add')

Register requirements with REQ-IDs and acceptance criteria.

**Parameters:**
- `requirements` (string, required) — Free-text requirements with REQ-IDs

**Example call:**
```
requirement('add', { requirements: "## Auth\n- AUTH-01: User registration with email/password\n- AUTH-02: JWT-based session management\n\n## API\n- API-01: CRUD endpoints for users" })
```

#### requirement('resolve')

Mark a requirement as fulfilled.

**Parameters:**
- `id` (string, required) — Requirement ID to resolve
- `evidence` (string, optional) — Evidence of fulfillment

**Example call:**
```
requirement('resolve', { id: "AUTH-01", evidence: "Registration endpoint implemented and tested in task:g14-01-1-01" })
```

#### requirement('list')

List all requirements for the active goal.

**Parameters:** None

---

## Planning Topic Conventions

Planning tools use reserved topic prefixes. These are standard scrinia topics — they use the same storage, search, and versioning as knowledge memories.

| Topic Prefix | Scope Resolution | Purpose | Example |
|---|---|---|---|
| `project:*` | `local-topic:project` | Project context, requirements, state | `project:context`, `project:state` |
| `plan:*` | `local-topic:plan` | Roadmaps and phase plans | `plan:roadmap` |
| `task:*` | `local-topic:task` | Individual tasks with keyword metadata | `task:g14-01-1-01`, `task:01-execution-log` |
| `learn:*` | `local-topic:learn` | Execution outcomes and retrospectives | `learn:execution-outcomes` |
| `agent:*` | `local-topic:agent` | Project-level agent behavioral norms | `agent:profile` |
| `research:*` | `local-topic:research` | Investigation findings (goal-prefixed) | `research:g14-auth-flow` |
| `concern:*` | `local-topic:concern` | Tracked risks | `concern:SEC-001` |
| `backlog:*` | `local-topic:backlog` | Deferred work and future ideas | `backlog:nice-to-have` |

### Scope Filtering with excludeTopics

The `list` and `search` memory tools support an `excludeTopics` parameter to filter planning data from knowledge queries:

```
# Knowledge-only query (excludes all planning topics)
memory('list', { excludeTopics: "plan,task,project,learn,backlog" })
memory('search', { query: "authentication", excludeTopics: "plan,task,project,learn,backlog" })

# Default behavior (no excludeTopics) — shows everything including planning
memory('list')
memory('search', { query: "authentication" })
```

**Important:** `learn:*` memories are searchable by default. They are only excluded when explicitly included in `excludeTopics`. This is by design — learned patterns should surface during future planning.

## Task Keyword Metadata

Tasks store structured metadata as keywords on `ArtifactEntry`, queryable without decoding the artifact content:

| Keyword | Purpose | Example |
|---|---|---|
| `status:pending` | Task not yet started | Set by `plan('tasks')` |
| `status:complete` | Task finished | Set by `task('complete')` |
| `wave:1` | Execution wave (parallel group) | Set by `plan('tasks')` |
| `phase:01` | Phase membership | Set by `plan('tasks')` |
| `depends_on:01-1-01` | Dependency on another task (full task name) | Set by `plan('tasks')` |
| `goal:G-14` | Goal scoping (prevents cross-goal collisions) | Set by `plan('tasks')` |
| `provenance:agent` | Content authored by agent | Set by built-in skills |

---

## Recovery

- `memory('restore')` — full context restoration (project state, agent profile, session log, task nudge)
- `plan('status')` — quick progress check
- `memory('search', { query: "agent:" })` — load behavioral norms

## Full Lifecycle Walkthrough

```
# 1. Initialize (one-time)
plan('init', { context: "Build a todo app with React frontend and Express API" })

# 2. Pre-plan: scan codebase for concerns
concern('add', { description: "No test coverage", severity: "medium" })

# 3. Define requirements
requirement('add', { requirements: "## API\n- API-01: CRUD endpoints\n## UI\n- UI-01: Task list view" })

# 4. Set a goal
goal('add', { description: "Implement core API with CRUD endpoints" })
# -> auto-creates researcher, auditor, planner seed tasks

# 5. Execute seed tasks (researcher -> auditor -> planner)
task('next')
# -> spawn researcher agent -> task('complete')
# -> spawn auditor agent -> task('complete')
# -> spawn planner agent (calls plan('tasks')) -> task('complete')

# 6. Execute implementation tasks
task('next')
# -> returns unblocked Wave 1 tasks
# -> spawn agent for each task
# -> agent does the work (write code, run tests, commit)
task('complete', { taskName: "task:g1-01-1-01", outcome: "Created Express server with CRUD routes. Tests pass." })

# 7. Repeat step 6 until all tasks done
# Gate tasks (QA, self-reflector) auto-execute at phase boundaries

# 8. Complete the goal
goal('complete', { outcome: "API CRUD implemented and verified." })

# 9. Set next goal
goal('add', { description: "Build React frontend with task list view" })
# ... repeat ...

# At any point — check status or restore after context loss:
plan('status')
memory('restore')
```
