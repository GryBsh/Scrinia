# Workflow Authoring Guide

## 1. Overview

Workflows define the pipeline that executes when you create a goal in Scrinia. A workflow consists of **seed activities** (created when the goal starts) and **gate activities** (injected after the planner produces execution tasks). Together they form a directed acyclic graph (DAG) that controls agent orchestration.

Scrinia ships two built-in workflows:

- **goal-execution** (default) -- full pipeline with agent-specialist, researcher, auditor, planner seeds and QA, self-reflector, evolutionary, cartographer, march-reporter gates.
- **quick-fix** -- lightweight pipeline with researcher and planner seeds and a single QA gate.

You can customize workflows when:

- Your project needs a different set of seed or gate activities.
- You want to skip heavy gates (e.g., evolutionary, cartographer) for certain goal types.
- You want to add custom validation checks or required outputs.
- You want to define an entirely new pipeline shape.

## 2. Quick Start

Create a file at `.scrinia/workflows/my-pipeline.yaml`:

```yaml
name: my-pipeline
seedActivities:
  - id: researcher
    phase: "00"
    wave: 0
    skill: "builtin:researcher"
    dependsOn: []
    gateType: researcher
    contentTemplate: >
      ## Researcher Task
      Investigate the goal scope. Store findings via memory('store').
    validation:
      checkType: index-prefix
      target: "research:{goalShort}"
      errorTemplate: "No research:* memories found."
    requiredOutputs:
      - checkType: index-prefix
        target: "research:{goalShort}"
        errorTemplate: "No research findings stored."
        instructionTemplate: "Store findings via memory('store') before completing."
  - id: planner
    phase: "00"
    wave: 1
    skill: "builtin:planner"
    dependsOn:
      - researcher
    gateType: planner
    contentTemplate: >
      ## Planner Task
      Read research findings and call plan('tasks').
    validation:
      checkType: index-no-gate
      target: "task:"
      errorTemplate: "No execution tasks found."
gateActivities:
  - id: qa-gate
    skill: "builtin:qa"
    dependsOn:
      - "*"
    gateType: qa
    contentTemplate: >
      ## QA Gate
      Run tests and write qa:latest.
    validation:
      checkType: memory-exists
      target: "qa:latest"
      errorTemplate: "qa:latest memory not found."
```

Use the workflow when creating a goal:

```
entity('create', { type: "goal", description: "...", workflowRef: "my-pipeline" })
```

## 3. Field Reference

### WorkflowDefinition

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Unique workflow name (e.g., `"goal-execution"`, `"quick-fix"`). Used as the `workflowRef` value. |
| `seedActivities` | WorkflowActivity[] | Yes | Activities created when a goal starts (phase 00). Must contain at least one activity. |
| `gateActivities` | WorkflowActivity[] | Yes | Activities injected by `plan('tasks')` after execution phases. May be an empty array. Must not be null. |

### WorkflowActivity

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | Yes | Unique identifier across all activities (seeds + gates). E.g., `"researcher"`, `"qa-gate"`. |
| `phase` | string or null | Seeds: required; Gates: must be null | Two-digit phase code. Seeds use `"00"`. Gates get their phase assigned dynamically. |
| `wave` | integer or null | Seeds: required; Gates: must be null | Execution order within a phase. Lower waves run first. Gates are ordered by topological sort. |
| `skill` | string or null | No | Skill to load for this activity. E.g., `"builtin:researcher"`, `"builtin:qa"`. |
| `dependsOn` | string[] | No | Activity IDs this activity depends on. Use `"*"` to depend on all user tasks. |
| `gateType` | string | Yes | Keyword identifying the activity type. E.g., `"researcher"`, `"qa"`, `"planner"`. |
| `contentTemplate` | string | Yes | Instruction text for the task. Supports `{goalShort}` template variable substitution. |
| `validation` | GateValidation or null | No | Check performed when a gate task is completed. Null for seeds that have no completion gate. |
| `requiredOutputs` | GateValidation[] or null | No | Outputs the activity must produce before it can be marked complete. |

### GateValidation

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `checkType` | string | Yes | One of the four check types (see Section 5). |
| `target` | string | Yes | The target to check. Supports `{goalShort}` substitution. E.g., `"qa:latest"`, `"research:{goalShort}"`. |
| `errorTemplate` | string | Yes | Error message shown when the check fails (description only, no instruction prefix). |
| `instructionTemplate` | string or null | No | Instruction emitted via `WithInstruction()` to guide the agent on how to fix the failure. |

## 4. Validation Rules

The `WorkflowDefinition.Validate()` method enforces 16 rules. A workflow must pass all of them before it can be saved.

### Required fields (rules 1-3)

| # | Rule | Error message |
|---|------|---------------|
| 1 | `Name` must be non-empty | "Name is required and must be non-empty." |
| 2 | `SeedActivities` must be non-null with at least one activity | "SeedActivities is required and must contain at least one activity." |
| 3 | `GateActivities` must not be null (empty array is allowed) | "GateActivities must not be null (use an empty array if no gates are needed)." |

### Per-activity required fields (rules 4-6)

| # | Rule | Error message |
|---|------|---------------|
| 4 | Each activity `Id` must be non-empty | "{label}: Id is required and must be non-empty." |
| 5 | Each activity `ContentTemplate` must be non-empty | "{label} ('{id}'): ContentTemplate is required and must be non-empty." |
| 6 | Each activity `GateType` must be non-empty | "{label} ('{id}'): GateType is required and must be non-empty." |

### ID uniqueness (rule 7)

| # | Rule | Error message |
|---|------|---------------|
| 7 | All activity IDs must be unique (case-insensitive) across seeds and gates | "Duplicate activity ID '{id}' -- all IDs must be unique across SeedActivities and GateActivities." |

### Structural constraints (rules 8-11)

| # | Rule | Error message |
|---|------|---------------|
| 8 | Seed activities must have a non-empty `Phase` | "SeedActivities[{i}] ('{id}'): Phase is required for seed activities." |
| 9 | Seed activities must have a non-null `Wave` | "SeedActivities[{i}] ('{id}'): Wave is required for seed activities." |
| 10 | Gate activities must have null `Phase` | "GateActivities[{i}] ('{id}'): Phase must be null for gate activities (assigned dynamically)." |
| 11 | Gate activities must have null `Wave` | "GateActivities[{i}] ('{id}'): Wave must be null for gate activities (computed by topo sort)." |

### DependsOn resolution (rule 12)

| # | Rule | Error message |
|---|------|---------------|
| 12 | Each `DependsOn` entry must reference a known activity ID or `"*"` | "Activity '{id}': DependsOn references unknown ID '{dep}'." |

### CheckType validation (rule 13)

| # | Rule | Error message |
|---|------|---------------|
| 13 | `Validation.CheckType` must be one of the four valid check types | "Activity '{id}': Validation.CheckType '{type}' is invalid. Must be one of: filesystem-glob, index-no-gate, index-prefix, memory-exists." |

### RequiredOutputs validation (rules 14-16)

| # | Rule | Error message |
|---|------|---------------|
| 14 | `RequiredOutputs[j].CheckType` must be a valid check type | "Activity '{id}': RequiredOutputs[{j}].CheckType '{type}' is invalid." |
| 15 | `RequiredOutputs[j].Target` must be non-empty | "Activity '{id}': RequiredOutputs[{j}].Target must be non-empty." |
| 16 | `RequiredOutputs[j].ErrorTemplate` must be non-empty | "Activity '{id}': RequiredOutputs[{j}].ErrorTemplate must be non-empty." |

### DAG cycle detection (implicit rule)

Kahn's algorithm verifies that the dependency graph forms a DAG. If a cycle exists:

> "Dependency cycle detected -- DependsOn references form a cycle. All dependencies must form a DAG."

## 5. Check Types

| CheckType | Description | Target example | Behavior |
|-----------|-------------|----------------|----------|
| `memory-exists` | Verifies that a specific memory exists in the store | `"qa:latest"`, `"project:requirements"` | Looks up the exact memory name. Fails if not found. |
| `index-prefix` | Verifies that at least one memory with the given prefix exists in the index | `"research:{goalShort}"`, `"learn:retro-{goalShort}-"`, `"cartography:"` | Scans the index for entries whose name starts with the target prefix. Fails if none match. |
| `index-no-gate` | Verifies that at least one non-gate task exists in the index with the given prefix | `"task:"` | Scans the task index for entries matching the prefix, excluding gate tasks. Used by the planner to verify execution tasks were created. |
| `filesystem-glob` | Verifies that at least one file matches the glob pattern on the filesystem | `"docs/reports/*.md"` | Expands the glob relative to the project root. Fails if no files match. |

### Examples

```yaml
# Check that qa:latest memory was written
validation:
  checkType: memory-exists
  target: "qa:latest"
  errorTemplate: "qa:latest memory not found."
  instructionTemplate: "Spawn a QA agent via skill('load', { name: \"qa\" }) -- it must write qa:latest."

# Check that research findings exist for the current goal
validation:
  checkType: index-prefix
  target: "research:{goalShort}"
  errorTemplate: "No research:* memories found."

# Check that execution tasks were created (excluding gate tasks)
validation:
  checkType: index-no-gate
  target: "task:"
  errorTemplate: "No execution tasks found."

# Check that a march report file exists
validation:
  checkType: filesystem-glob
  target: "docs/reports/*.md"
  errorTemplate: "No march report found in docs/reports/."
```

## 6. Template Variables

The `{goalShort}` variable is substituted at runtime in the following fields:

- `target` (in both `validation` and `requiredOutputs`)
- `errorTemplate`
- `instructionTemplate`

The `{goalShort}` value is derived from the active goal ID. For example, if the goal is `G-59-72c`, then `{goalShort}` resolves to `g59-72c`.

Usage:

```yaml
validation:
  checkType: index-prefix
  target: "research:{goalShort}"           # becomes "research:g59-72c"
  errorTemplate: "No research findings for goal {goalShort}."
```

## 7. Override Mechanism

Custom workflows are stored as files in `.scrinia/workflows/`. The resolution precedence is:

| Priority | Source | Path / Location |
|----------|--------|-----------------|
| 1 (highest) | YAML file | `.scrinia/workflows/{name}.yaml` or `.yml` |
| 2 | JSON file | `.scrinia/workflows/{name}.json` |
| 3 | NMP/2 memory (legacy) | `workflow:{name}` in the memory store |
| 4 (lowest) | Built-in | `WorkflowDefinition.DefaultGoalWorkflow` or `QuickFixWorkflow` |

### How it works

1. When a goal is created with `workflowRef: "my-pipeline"`, the resolver looks for `.scrinia/workflows/my-pipeline.yaml` first.
2. If no YAML file is found, it checks for `.scrinia/workflows/my-pipeline.json`.
3. If no disk file is found, it checks the NMP/2 memory store for `workflow:my-pipeline` (legacy support).
4. If nothing matches, it falls back to the built-in workflow (`quick-fix` name maps to `QuickFixWorkflow`; everything else maps to `DefaultGoalWorkflow`).

### Creating an override

Option A -- YAML file (recommended):

```bash
# Create or edit directly
vim .scrinia/workflows/goal-execution.yaml
```

Option B -- via the MCP entity tool:

```
entity('create', { type: "workflow", definition: '{"name":"my-pipeline",...}' })
```

This writes to `.scrinia/workflows/my-pipeline.json` with full validation.

Option C -- via the Web UI editor (see Section 9).

### Corrupted overrides

If a disk file or NMP/2 memory fails to parse, the resolver falls back to the built-in default and emits a warning:

> WARNING: YAML workflow 'goal-execution.yaml' could not be parsed: {error message}

## 8. Built-in Workflows

### goal-execution (default)

The full pipeline with 4 seed activities and 5 gate activities.

```yaml
name: goal-execution
seedActivities:
  # Wave 0: Agent Specialist -- assesses skill fit
  - id: agent-specialist
    phase: "00"
    wave: 0
    skill: "builtin:agent-specialist"
    dependsOn: []
    gateType: agent-specialist
    contentTemplate: >
      ## Agent Specialist Task
      Action: Load the agent-specialist skill. Assess whether the current skill
      set is appropriate for this goal. Scan available skills and environment for
      external agents. Propose agent assignments for each workflow activity.
      Acceptance criteria:
      - Skill fit assessment completed
      - Agent assignments proposed (or confirmed as default)
    validation:
      checkType: memory-exists
      target: "project:context"
      errorTemplate: "No project context found."
      instructionTemplate: "The agent-specialist must complete its assessment."
    requiredOutputs:
      - checkType: memory-exists
        target: "project:context"
        errorTemplate: "No project context found."
        instructionTemplate: "Complete the skill fit assessment before completing."

  # Wave 1: Researcher -- investigates scope
  - id: researcher
    phase: "00"
    wave: 1
    skill: "builtin:researcher"
    dependsOn:
      - agent-specialist
    gateType: researcher
    contentTemplate: >
      ## Researcher Task
      Action: Investigate the goal scope by exploring the codebase and existing
      memories. Understand what exists, what needs to change, and what risks are
      present. Store findings via memory('store', { name: "research:...", content: [...] }).
      Acceptance criteria:
      - Research findings stored as research:* memories
      - Scope and implementation approach documented
    validation:
      checkType: index-prefix
      target: "research:{goalShort}"
      errorTemplate: "No research:* memories found."
      instructionTemplate: "The researcher must store findings before this gate can complete."
    requiredOutputs:
      - checkType: index-prefix
        target: "research:{goalShort}"
        errorTemplate: "No research findings stored."
        instructionTemplate: "Store findings via memory('store') before completing."

  # Wave 2: Auditor -- reads research, creates requirements + concerns
  - id: auditor
    phase: "00"
    wave: 2
    skill: "builtin:auditor"
    dependsOn:
      - researcher
    gateType: auditor
    contentTemplate: >
      ## Auditor Task
      Action: Load the auditor skill. Read the research findings. Call
      entity('create', { type: "requirement" }) for each requirement and
      entity('create', { type: "concern" }) for each risk.
      Acceptance criteria:
      - Requirements captured via entity('create', { type: "requirement" })
      - Concerns raised via entity('create', { type: "concern" })
    validation:
      checkType: memory-exists
      target: "project:requirements"
      errorTemplate: "No requirements found."
      instructionTemplate: "The auditor must call entity('create', { type: 'requirement' }) before this gate can complete."
    requiredOutputs:
      - checkType: memory-exists
        target: "project:requirements"
        errorTemplate: "No requirements found."
        instructionTemplate: "Call entity('create', { type: 'requirement' }) before completing."

  # Wave 3: Planner -- reads research + requirements, calls plan('tasks')
  - id: planner
    phase: "00"
    wave: 3
    skill: "builtin:planner"
    dependsOn:
      - auditor
    gateType: planner
    contentTemplate: >
      ## Planner Task
      Action: Load the planner skill. Read research findings and requirements.
      Produce task definitions and call plan('tasks') directly.
      Acceptance criteria:
      - plan('tasks') called with task definitions
      - Tasks created with proper dependencies and acceptance criteria
    validation:
      checkType: index-no-gate
      target: "task:"
      errorTemplate: "No execution tasks found."
      instructionTemplate: "Spawn a planner agent -- it must call plan('tasks') before this gate can complete."
    requiredOutputs:
      - checkType: index-no-gate
        target: "task:"
        errorTemplate: "No execution tasks found."
        instructionTemplate: "Call plan('tasks') before completing."

gateActivities:
  # QA gate -- runs after all user tasks in every phase
  - id: qa-gate
    skill: "builtin:qa"
    dependsOn:
      - "*"
    gateType: qa
    contentTemplate: >
      ## QA Gate
      Action: Spawn a QA agent. Run the full test suite, verify the build,
      check acceptance criteria, and write qa:latest memory with results.
      Acceptance criteria:
      - qa:latest memory exists with current test pass/fail counts
      - Build passes with 0 errors
      - All phase acceptance criteria verified
    validation:
      checkType: memory-exists
      target: "qa:latest"
      errorTemplate: "qa:latest memory not found."
      instructionTemplate: "Spawn a QA agent via skill('load', { name: \"qa\" }) -- it must write qa:latest."

  # Self-reflector gate -- retrospective after QA
  - id: self-reflector-gate
    skill: "builtin:self-reflector"
    dependsOn:
      - qa-gate
    gateType: self-reflector
    contentTemplate: >
      ## Self-Reflector Gate
      Action: Spawn a self-reflector agent. Read execution logs and QA findings.
      Compare plan vs reality. Store retrospective and belief updates.
      Acceptance criteria:
      - Retrospective stored as learn:retro-*
      - Beliefs updated if applicable
    validation:
      checkType: index-prefix
      target: "learn:retro-{goalShort}-"
      errorTemplate: "No learn:retro-{goalShort}-* memory found."
      instructionTemplate: "Spawn a self-reflector agent -- it must store a retrospective."

  # Evolutionary gate -- knowledge base scan
  - id: evolutionary-gate
    skill: "builtin:evolutionary"
    dependsOn:
      - qa-gate
      - self-reflector-gate
    gateType: evolutionary
    contentTemplate: >
      ## Evolutionary Gate
      Action: Spawn an evolutionary agent. Run a knowledge base scan to update
      stale memories, detect skill drift, and surface emergent patterns.
      Acceptance criteria:
      - Evolutionary scan completed
      - Stale memories updated
      - Session stored as sessions:evolutionary-gNN
    validation:
      checkType: index-prefix
      target: "sessions:evolutionary-"
      errorTemplate: "No sessions:evolutionary-* memory found."
      instructionTemplate: "Spawn an evolutionary agent -- it must complete its scan."

  # Cartographer gate -- maps knowledge connections
  - id: cartographer-gate
    skill: "builtin:cartographer"
    dependsOn:
      - qa-gate
      - self-reflector-gate
    gateType: cartographer
    contentTemplate: >
      ## Cartographer Gate
      Action: Spawn a cartographer agent. Map knowledge connections, link
      orphans, identify gaps.
      Acceptance criteria:
      - Cartography scan completed
      - New links created for orphaned memories
      - Report stored as cartography:YYYY-MM-DD
    validation:
      checkType: index-prefix
      target: "cartography:"
      errorTemplate: "No cartography:* memory found."
      instructionTemplate: "Spawn a cartographer agent -- it must complete its scan."

  # March report gate -- produces goal summary
  - id: march-gate
    skill: "builtin:march-reporter"
    dependsOn:
      - qa-gate
      - self-reflector-gate
    gateType: march
    contentTemplate: >
      ## March Report Gate
      Action: Spawn a march reporter agent. Produce a goal summary document
      in docs/reports/.
      Acceptance criteria:
      - March report written to docs/reports/
      - Session log updated
    validation:
      checkType: filesystem-glob
      target: "docs/reports/*.md"
      errorTemplate: "No march report found in docs/reports/."
      instructionTemplate: "Spawn a march reporter agent -- it must produce a report."
```

### quick-fix

A lightweight pipeline with 2 seed activities and 1 gate activity. Skips the auditor and the heavy end-of-goal gates.

```yaml
name: quick-fix
seedActivities:
  # Wave 0: Researcher -- focused investigation
  - id: researcher
    phase: "00"
    wave: 0
    skill: "builtin:researcher"
    dependsOn: []
    gateType: researcher
    contentTemplate: >
      ## Researcher Task (Quick Fix)
      Action: Investigate the bug or change scope. Focus on root cause, affected
      files, and existing test coverage. Store findings via memory('store').
      Acceptance criteria:
      - Root cause or change scope identified and stored as research:* memory
      - Affected files and risk areas documented
    validation:
      checkType: index-prefix
      target: "research:{goalShort}"
      errorTemplate: "No research:* memories found."
      instructionTemplate: "The researcher must store findings before this gate can complete."
    requiredOutputs:
      - checkType: index-prefix
        target: "research:{goalShort}"
        errorTemplate: "No research findings stored."
        instructionTemplate: "Store findings via memory('store') before completing."

  # Wave 1: Planner -- focused plan
  - id: planner
    phase: "00"
    wave: 1
    skill: "builtin:planner"
    dependsOn:
      - researcher
    gateType: planner
    contentTemplate: >
      ## Planner Task (Quick Fix)
      Action: Load the planner skill. Read research findings and produce a
      focused task plan. Call plan('tasks') with targeted fix tasks.
      Acceptance criteria:
      - plan('tasks') called with fix tasks
      - Each task has clear acceptance criteria
    validation:
      checkType: index-no-gate
      target: "task:"
      errorTemplate: "No execution tasks found."
      instructionTemplate: "Spawn a planner agent -- it must call plan('tasks')."
    requiredOutputs:
      - checkType: index-no-gate
        target: "task:"
        errorTemplate: "No execution tasks found."
        instructionTemplate: "Call plan('tasks') before completing."

gateActivities:
  # QA gate -- single verification gate
  - id: qa-gate
    skill: "builtin:qa"
    dependsOn:
      - "*"
    gateType: qa
    contentTemplate: >
      ## QA Gate (Quick Fix)
      Action: Spawn a QA agent. Run the test suite, verify the fix, and write
      qa:latest with results.
      Acceptance criteria:
      - qa:latest memory exists with pass/fail counts
      - Build passes with 0 errors
      - Fix verified against acceptance criteria
    validation:
      checkType: memory-exists
      target: "qa:latest"
      errorTemplate: "qa:latest memory not found."
      instructionTemplate: "Spawn a QA agent -- it must write qa:latest."
```

## 9. Web UI Editor

The Scrinia web UI includes a workflow editor accessible at `/workflows/editor`.

### Accessing the editor

1. Navigate to `/workflows` in the web UI to see the list of available workflows (both built-in and custom overrides).
2. Click a workflow to view its detail page at `/workflows/editor/{name}`.
3. The editor page at `/workflows/editor` allows creating a new workflow or editing an existing one.

### Editor features

- **YAML editing** -- Workflows are displayed and edited in YAML format for readability.
- **Validation** -- The server validates the workflow definition on save (PUT `/api/v1/stores/{store}/workflows/{name}`), returning validation errors if the definition is invalid.
- **Save** -- Saves the workflow as `.scrinia/workflows/{name}.json` on the server.

### API endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/v1/stores/{store}/workflows/` | List all workflows (built-in + overrides) |
| GET | `/api/v1/stores/{store}/workflows/{name}` | Get workflow YAML content |
| PUT | `/api/v1/stores/{store}/workflows/{name}` | Save/update workflow (validates before saving) |

### Goal tracking

The web UI also provides goal tracking views:

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/v1/stores/{store}/workflows/goals` | List all goals with status and progress |
| GET | `/api/v1/stores/{store}/workflows/goals/{goalId}` | Goal detail with phase-grouped tasks |
| GET | `/api/v1/stores/{store}/workflows/goals/{goalId}/tasks` | Flat task list for a goal |
| GET | `/api/v1/stores/{store}/workflows/goals/{goalId}/events` | SSE stream of task events |

## 10. JSON Schema

A JSON Schema (draft 2020-12) for workflow definitions is available at `.scrinia/workflows/schema.json`. Use it for IDE validation and autocompletion.

### YAML with schema reference

In VS Code with the YAML extension, add this to the top of your workflow file:

```yaml
# yaml-language-server: $schema=../../.scrinia/workflows/schema.json
name: my-workflow
seedActivities:
  ...
```

### JSON with schema reference

```json
{
  "$schema": ".scrinia/workflows/schema.json",
  "name": "my-workflow",
  "seedActivities": [...]
}
```

### Schema location

The schema file lives at `.scrinia/workflows/schema.json` within your project. It covers the full `WorkflowDefinition` structure including `WorkflowActivity`, `GateValidation`, and all validation constraints documented in this guide.
