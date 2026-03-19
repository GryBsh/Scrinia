# Planning Tools Guide

Scrinia includes 12 MCP tools for structured project planning, execution, and learning. Plans are stored as standard scrinia memories using reserved topic conventions — no separate database or file format.

## Lifecycle Overview

```
project_init → plan_requirements → plan_roadmap → plan_tasks
                                                       ↓
                              plan_retrospective ← plan_verify ← task_complete ← task_next
                                                       ↓ (if gaps)
                                                   plan_gaps → task_next → ...
```

Recovery at any point: `plan_resume` / `plan_status`
User preferences: `plan_profile`

---

## Project Setup

### project_init

Initialize a project with goals, context, and constraints.

**Parameters:**
- `context` (string, required) — Free-text describing project goals, context, constraints, and scope

**Example call:**
```
project_init(context: "# My API Project\n\n## Goals\nBuild a REST API for user management\n\n## Constraints\n- Must use PostgreSQL\n- Deploy to AWS")
```

**Response:**
```
Initialized project 'my-api-project'. Stored: project:context, project:state.
Files in .scrinia/ were updated — these are your changes.
```

**What it stores:**
- `project:context` — the full context text
- `project:state` — tracking state (phase, progress, last action, next step)

---

### plan_requirements

Define project requirements with categories and REQ-IDs.

**Parameters:**
- `requirements` (string, required) — Requirements organized by category with REQ-IDs and v1/v2 scope labels

**Prerequisite:** `project_init` must be called first.

**Example call:**
```
plan_requirements(requirements: "## v1 Requirements\n\n### Auth\n- AUTH-01: User registration with email/password\n- AUTH-02: JWT-based session management\n\n### API\n- API-01: CRUD endpoints for users\n- API-02: Rate limiting (100 req/min)")
```

**Response:**
```
Stored: project:requirements. Files in .scrinia/ were updated — these are your changes.
```

**Tips:**
- Use `v1` / `v2` labels to scope requirements across milestones
- Every REQ-ID must appear in exactly one phase in the roadmap (validated by `plan_roadmap`)
- Format: `PREFIX-NN: description` (e.g., `AUTH-01`, `API-02`)

---

### plan_roadmap

Create a phased execution plan that maps every requirement to a phase.

**Parameters:**
- `roadmap` (string, required) — Phased roadmap referencing REQ-IDs

**Prerequisite:** `plan_requirements` must be called first.

**Validation:**
- Every REQ-ID from `project:requirements` must appear in exactly one phase
- Duplicate REQ-IDs across phases are rejected
- Extra REQ-IDs (in roadmap but not in requirements) produce a warning but are accepted

**Example call:**
```
plan_roadmap(roadmap: "## Phase 1: Auth Foundation\nRequirements: AUTH-01, AUTH-02\nGoal: Users can register and authenticate\nSuccess Criteria:\n1. Registration endpoint returns JWT\n2. Protected routes reject unauthenticated requests\n\n## Phase 2: API Layer\nRequirements: API-01, API-02\nGoal: Full CRUD with rate limiting")
```

**Response:**
```
Stored: plan:roadmap. Files in .scrinia/ were updated — these are your changes.
```

---

## Execution

### plan_tasks

Decompose a phase into individual tasks with dependencies and wave grouping.

**Parameters:**
- `phaseId` (string, required) — Two-digit phase number (e.g., `"01"`)
- `tasks` (string, required) — Free-text task definitions in structured format

**Prerequisite:** `plan_roadmap` must be called first.

**Task format:**
```
## Task 01
Wave: 1
Depends on: none
Action: Create user registration endpoint with email/password validation
Acceptance criteria:
- POST /api/users returns 201 with JWT
- Duplicate email returns 409

## Task 02
Wave: 1
Depends on: none
Action: Create JWT middleware for route protection
Acceptance criteria:
- Protected routes return 401 without token
- Valid token passes through

## Task 03
Wave: 2
Depends on: 01-1-01, 01-1-02
Action: Integration test for registration + auth flow
Acceptance criteria:
- Register, login, access protected route in one test
```

**Response:**
```
Created 3 task(s) for phase 01 in 2 wave(s).
Tasks stored: task:01-1-01, task:01-1-02, task:01-2-03
```

**Key concepts:**
- **Waves** group tasks that can run in parallel (Wave 1 tasks are independent)
- **Dependencies** reference task subjects (e.g., `01-1-01`), not qualified names
- Task metadata stored as keywords: `status:pending`, `wave:1`, `phase:01`, `depends_on:01-1-01`

---

### task_next

Get all unblocked tasks in the current wave.

**Parameters:**
- `phaseId` (string, required) — Two-digit phase number

**Example call:**
```
task_next(phaseId: "01")
```

**Response:**
```
Phase 01 — Wave 1 — 2 unblocked task(s):

## task:01-1-01
Action: Create user registration endpoint...
Acceptance criteria:
- POST /api/users returns 201 with JWT
...

## task:01-1-02
Action: Create JWT middleware...
```

**How it works:**
- Keyword-only index scan (no artifact decode during filtering) — fast even with hundreds of tasks
- Filters: `phase:{phaseId}` → `status:pending` → lowest wave → unblocked dependencies
- Returns ALL unblocked tasks — the agent decides which to execute and in what order

---

### task_complete

Mark a task as done with outcome metadata.

**Parameters:**
- `taskName` (string, required) — Qualified task name (e.g., `"task:01-1-01"`)
- `outcome` (string, required) — What was done, any deviations

**Example call:**
```
task_complete(taskName: "task:01-1-01", outcome: "Created registration endpoint at POST /api/users. Added email uniqueness check. Tests pass.")
```

**Response:**
```
Task 'task:01-1-01' marked complete. Execution log updated. Run task_next for next task.
```

**What happens:**
- Updates `status:pending` → `status:complete` keyword (no version archiving — prevents bloat)
- Appends outcome to `task:{phaseId}-execution-log` as a new chunk
- Updates `project:state` with last action

---

## Verification

### plan_verify

Check whether a phase achieved its goal using success criteria from the roadmap.

**Parameters:**
- `phaseId` (string, required) — Two-digit phase number

**Example call:**
```
plan_verify(phaseId: "01")
```

**Response:**
```
Phase 01 Verification — 2 criteria:

1. PASS — Registration endpoint returns JWT
   Evidence: task:01-1-01 completed — "Created registration endpoint"

2. FAIL — Protected routes reject unauthenticated requests
   Evidence: No completed task addresses this criterion
```

**Key behavior:**
- Reads success criteria from `plan:roadmap` (scoped to the target phase)
- Checks task completion status and execution log for evidence
- Returns structured PASS/FAIL per criterion — not a narrative assessment
- Can be called before execution starts (for plan quality check — all criteria will FAIL)

---

### plan_gaps

Create fix tasks for failed verification criteria.

**Parameters:**
- `phaseId` (string, required) — Two-digit phase number
- `failedCriteria` (string, required) — Description of what failed and needs fixing

**Example call:**
```
plan_gaps(phaseId: "01", failedCriteria: "## Gap 01\nFailed: Protected routes reject unauthenticated requests\nFix: Add integration test covering unauthenticated access returns 401")
```

**Response:**
```
Created 1 gap closure task(s) for phase 01.
```

**What happens:**
- Creates `task:{phaseId}-gap-{id}` memories with `gap_closure:true` keyword
- Re-opens the phase status in `project:state`
- New tasks appear in `task_next` — the agent can execute them and re-verify

**The gap closure loop:**
```
plan_verify → finds gaps → plan_gaps → task_next → execute → task_complete → plan_verify again
```

---

## Recovery

### plan_resume

Restore project context after context loss (new session, context window compaction).

**Parameters:** None

**Example call:**
```
plan_resume()
```

**Response:**
```
Project: my-api-project
ID: my-api-project
Phase: Phase 01 — Auth Foundation
Progress: 50%
Last action: Completed task:01-1-01
Blockers: none
Next: run task_next to get the next pending task
```

**Key behavior:**
- Returns structured summary within 8KB response cap
- If `project:state` is missing/corrupted, rebuilds from `project:context` + `plan:roadmap` + task memories
- Includes concrete next-step suggestion

---

### plan_status

Quick status query — lighter than plan_resume.

**Parameters:** None

**Response format:**
```
Project: my-api-project
Phase: Phase 01 — Auth Foundation
Progress: 50%
Last action: Completed task:01-1-01
Blockers: none
Next: run task_next to get the next pending task
Roadmap: 2 phase(s) defined
```

---

## Learning

### plan_retrospective

Record what worked, what failed, and lessons learned after a phase completes.

**Parameters:**
- `phaseId` (string, required) — Two-digit phase number
- `whatWorked` (string, required) — What went well
- `whatFailed` (string, required) — What was problematic
- `lessons` (string, required) — Lessons for future phases

**Example call:**
```
plan_retrospective(
  phaseId: "01",
  whatWorked: "TDD approach caught two edge cases early. JWT library was straightforward.",
  whatFailed: "Forgot to add rate limiting middleware — had to retrofit.",
  lessons: "Always check non-functional requirements before marking phase complete. Add middleware early."
)
```

**Response:**
```
Phase 01 retrospective stored in learn:execution-outcomes.
Searchable via standard search. Use get_chunk() to retrieve individual phase retrospectives.
```

**Key behavior:**
- Appends to `learn:execution-outcomes` as a new chunk (outcomes accumulate across phases)
- Tagged with `provenance:agent` keyword — distinguishes agent-authored from external content
- Searchable via standard `search()` — surfaces automatically in future planning sessions

---

### plan_profile

Store user preferences for agent behavior.

**Parameters:**
- `profile` (string, required) — Key-value preferences, one per line

**Example call:**
```
plan_profile(profile: "autonomy_level: high\nreview_depth: detailed\ncommunication_style: concise\npreferred_testing: tdd")
```

**Response:**
```
User profile stored in user:profile. Preferences persist across sessions and are searchable via standard search.
```

**Key behavior:**
- Full overwrite on each call (not merge)
- Persists in `user:profile` across sessions
- Tagged with `provenance:agent` keyword

---

## Planning Topic Conventions

Planning tools use 5 reserved topic prefixes. These are standard scrinia topics — they use the same storage, search, and versioning as knowledge memories.

| Topic Prefix | Scope Resolution | Purpose | Example |
|---|---|---|---|
| `project:*` | `local-topic:project` | Project context, requirements, state | `project:context`, `project:state` |
| `plan:*` | `local-topic:plan` | Roadmaps and phase plans | `plan:roadmap` |
| `task:*` | `local-topic:task` | Individual tasks with keyword metadata | `task:01-1-01`, `task:01-execution-log` |
| `learn:*` | `local-topic:learn` | Execution outcomes and retrospectives | `learn:execution-outcomes` |
| `user:*` | `local-topic:user` | Agent behavior preferences | `user:profile` |

### Scope Filtering with excludeTopics

The `list` and `search` memory tools support an `excludeTopics` parameter to filter planning data from knowledge queries:

```
# Knowledge-only query (excludes all planning topics)
list(excludeTopics: "plan,task,project,learn")
search(query: "authentication", excludeTopics: "plan,task,project,learn")

# Default behavior (no excludeTopics) — shows everything including planning
list()
search(query: "authentication")
```

**Important:** `learn:*` memories are searchable by default. They are only excluded when explicitly included in `excludeTopics`. This is by design — learned patterns should surface during future planning.

### Task Keyword Metadata

Tasks store structured metadata as keywords on `ArtifactEntry`, queryable without decoding the artifact content:

| Keyword | Purpose | Example |
|---|---|---|
| `status:pending` | Task not yet started | Set by `plan_tasks` |
| `status:complete` | Task finished | Set by `task_complete` |
| `wave:1` | Execution wave (parallel group) | Set by `plan_tasks` |
| `phase:01` | Phase membership | Set by `plan_tasks` |
| `depends_on:01-1-01` | Dependency on another task | Set by `plan_tasks` |
| `gap_closure:true` | Task created by `plan_gaps` | Set by `plan_gaps` |
| `provenance:agent` | Content authored by agent | Set by `plan_retrospective`, `plan_profile` |

---

## Full Lifecycle Walkthrough

Here's a complete flow from project initialization through learning:

```
# 1. Initialize
project_init(context: "Build a todo app with React frontend and Express API")

# 2. Define requirements
plan_requirements(requirements: "## v1\n### API\n- API-01: CRUD endpoints\n### UI\n- UI-01: Task list view")

# 3. Create roadmap
plan_roadmap(roadmap: "## Phase 1: API\nRequirements: API-01\n...\n## Phase 2: UI\nRequirements: UI-01\n...")

# 4. Decompose Phase 1 into tasks
plan_tasks(phaseId: "01", tasks: "## Task 01\nWave: 1\nDepends on: none\nAction: Create Express server with CRUD routes\n...")

# 5. Get next task
task_next(phaseId: "01")
# → Returns all unblocked Wave 1 tasks

# 6. Execute the task (agent does the work)
# ... write code, run tests, commit ...

# 7. Mark complete
task_complete(taskName: "task:01-1-01", outcome: "Created Express server with GET/POST/PUT/DELETE routes. Tests pass.")

# 8. Repeat 5-7 until all tasks done

# 9. Verify phase goal
plan_verify(phaseId: "01")
# → PASS/FAIL per criterion

# 10. If gaps found:
plan_gaps(phaseId: "01", failedCriteria: "## Gap 01\nFailed: ...\nFix: ...")
# → Creates fix tasks, re-opens phase
# → Back to step 5

# 11. Record lessons
plan_retrospective(phaseId: "01", whatWorked: "...", whatFailed: "...", lessons: "...")

# 12. Move to Phase 2
plan_tasks(phaseId: "02", tasks: "...")
# ... repeat ...

# At any point — check status or resume after context loss:
plan_status()
plan_resume()
```
