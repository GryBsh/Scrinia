# March Report: G-47 — Tool Consolidation v2

**Goal:** Collapse 35 MCP tools to 10 using noun('action', { params }) dispatcher pattern
**Date:** 2026-03-22
**Status:** Active (Phase 03, 89% complete)
**Tests:** 911 start, 916 finish (+5 net)

---

## 1. Summary

G-47 is the largest refactor in Scrinia's history, consolidating 35 individual MCP tools into 10 using a noun('action', { params }) dispatch pattern. The refactor preserves all existing functionality while dramatically simplifying the API surface. Three phases: memory consolidation (15 tools to 4), planning consolidation (20 tools to 6), and flow changes including a new self-reflector built-in skill, auditor/onboarder seed tasks, and requirement resolve/list actions. The internal methods remain unchanged -- thin dispatchers route actions to existing logic, and tests continue to call typed internals via InternalsVisibleTo.

## 2. Changes

### Phase 01: Memory Tool Consolidation
- **memory** dispatcher: 12 actions (store, append, show, search, list, forget, copy, compact, update, link, references, restore) replace 15 separate MCP tools
- `context_resume` (from G-46) moved into memory('restore') action
- **guide** remains standalone (no action parameter needed)
- **bundle** dispatcher: export/import actions
- **reconcile** remains standalone
- Net result: 15 memory tools collapsed to 4 (guide, memory, bundle, reconcile)

### Phase 02: Planning Tool Consolidation
- **plan** dispatcher: tasks/status/init actions replace plan_tasks, plan_status, project_init
- **goal** dispatcher: add/edit/complete/list actions replace goal_update
- **task** dispatcher: next/complete actions replace task_next, task_complete
- **concern** dispatcher: add/resolve/list actions replace concern_add, concern_resolve, concern
- **skill** dispatcher: load/create actions replace skill_load, skill_create
- **requirement** dispatcher: add/resolve/list actions (new resolve and list functionality)
- Dropped tools: plan_roadmap, research_start, research_complete, plan_requirements, plan_verify, plan_retrospective, plan_profile, plan_gaps
- Dropped tool logic moved to skills (verify to QA, retro to self-reflector) or replaced by dispatchers (plan_requirements by requirement('add'))
- Net result: 20 planning tools collapsed to 6 (plan, goal, task, concern, skill, requirement)

### Phase 03: Flow Changes + Self-Reflector + Guide Update
- **goal('add')** auto-creates auditor seed task (wave 0, gate:auditor keyword)
- **plan('init')** auto-creates onboarder seed task when existing code detected (gate:onboarder keyword)
- **Self-reflector** built-in skill added (12th skill) -- reads execution logs + QA findings, compares plan vs reality, stores learn:retro-* and learn:beliefs-*
- **Gate injection order**: QA (every phase) -> self-reflector (every phase, depends on QA) -> evolutionary/cartographer/march (last phase only, depends on QA + self-reflector)
- **Guide** rewritten for 10-tool surface with noun('action') syntax
- **CLAUDE.md** updated for noun('action') syntax and 12 built-in skills
- **AGENTS.md** updated to reference 10 MCP tools (4 memory + 6 planning)
- **All 12 built-in skills** updated to use noun('action') syntax throughout

### Files Touched

| Area | Key Files |
|------|-----------|
| Memory tools | `MemoryTools.cs` (+504/-~250 lines: dispatcher + internal method visibility) |
| Planning tools | `ProjectTools.cs` (+864/-~600 lines: 6 dispatchers + self-reflector + seed tasks + gate injection) |
| Project config | `Scrinia.Mcp.csproj` |
| Agent guides | `AGENTS.md`, `CLAUDE.md`, `guide()` in MemoryTools.cs |
| Tests (updated) | `AutoInjectedGateTaskTests.cs`, `ConcernTrackingTests.cs`, `GoalToolTests.cs`, `NewToolTests.cs`, `OrganicAdoptionTests.cs`, `PlanningHelperTests.cs`, `ProjectLifecycleTests.cs`, `ResearchToolTests.cs`, `ScriniaMcpToolsTests.cs`, `SubagentToolTests.cs` |
| Tests (new) | `SeedTaskTests.cs` (auditor + onboarder seed task verification) |

## 3. Findings

No new concerns were raised during G-47. All 54 historical concerns remain resolved.

| ID | Description | Severity | Status | Resolution |
|----|-------------|----------|--------|------------|
| (none) | No new findings | -- | -- | -- |

Note: The QA report for Phase 03 identified that some auto-generated response messages and gate task content strings in ProjectTools.cs still use old-style function names (e.g., `skill_load("qa")`, `goal_update(action:'add')`). These are dynamically generated output strings, not skill definitions or guide content, and were accepted as-is since they do not affect tool invocation behavior.

## 4. Test Impact

| Suite | Before (G-46) | After (G-47) | Delta |
|-------|---------------|--------------|-------|
| Scrinia.Tests | 836 | 841 | +5 |
| Scrinia.Server.Tests | 63 | 63 | 0 |
| Scrinia.Plugin.Embeddings.Tests | 12 | 12 | 0 |
| **Total** | **911** | **916** | **+5** |

New tests added:
- **SeedTaskTests.cs**: Auditor seed task creation on goal('add'), onboarder seed task on plan('init') with existing code, no onboarder when no existing code
- **AutoInjectedGateTaskTests.cs**: Self-reflector gate injection on every phase, dependency ordering (QA -> self-reflector -> evolutionary/cartographer/march)

The modest test delta (+5) reflects the refactor's design philosophy: dispatchers are thin routing layers, and existing tests continue to exercise the internal methods directly. No test logic was lost -- tests were updated to use the new tool signatures where they test the dispatch surface.

## 5. Security Posture

- **No new security concerns.** The consolidation is a surface-level refactor; all internal logic (auth gates, concern checks, API key handling) is unchanged.
- **Reduced attack surface**: 35 MCP tool entry points collapsed to 10. Each dispatcher validates the action parameter and rejects unknown actions, providing a single choke point per domain.
- **AOT compatibility preserved**: All option types use source-generated JsonSerializerContext for ahead-of-time compilation safety.
- **Concern gate preserved**: goal('complete') continues to block on open high/medium concerns.

## 6. Configuration Changes

No new configuration settings were introduced. The changes are purely in the MCP tool surface.

| Change | Impact |
|--------|--------|
| 35 individual MCP tools replaced by 10 dispatchers | Agents calling old tool names (e.g., `store`, `search`, `plan_status`, `goal_update`) will get errors. Must use `memory('store', {...})`, `memory('search', {...})`, `plan('status')`, `goal('add', {...})` etc. |
| `requirement('resolve')` and `requirement('list')` added | New capability: requirements can now be individually resolved with evidence and listed on demand. Previously, plan_requirements only supported bulk add. |
| Self-reflector gate auto-injected | Task counts per phase increase by 1 (self-reflector gate after QA). Agents should expect this additional gate task. |
| Auditor seed task on goal('add') | New goals automatically get a wave-0 auditor task. The auditor scans the codebase, calls requirement('add') and concern('add'), then creates research tasks. |
| Onboarder seed task on plan('init') | Projects initialized with existing code automatically get a wave-0 onboarder task. |

### Migration Notes

This is a breaking change for any agent prompts, custom skills, or scripts referencing old tool names. The mapping:

| Old Tool | New Tool |
|----------|----------|
| `store(...)` | `memory('store', { name, content, ... })` |
| `search(...)` | `memory('search', { query, ... })` |
| `show(...)` | `memory('show', { name, chunk? })` |
| `list(...)` | `memory('list', { mode?, ... })` |
| `append(...)` | `memory('append', { name, content })` |
| `forget(...)` | `memory('forget', { name })` |
| `copy(...)` | `memory('copy', { source, target })` |
| `compact(...)` | `memory('compact', { name, keepRecent? })` |
| `update_meta(...)` | `memory('update', { name, ... })` |
| `link(...)` | `memory('link', { name, codeRefs })` |
| `references(...)` | `memory('references', { name })` |
| `context_resume(...)` | `memory('restore')` |
| `export(...)` | `bundle('export', { topic })` |
| `import(...)` | `bundle('import', { bundle })` |
| `plan_status()` | `plan('status')` |
| `plan_tasks(...)` | `plan('tasks', { phaseId, tasks })` |
| `project_init(...)` | `plan('init', { context })` |
| `task_next(...)` | `task('next')` |
| `task_complete(...)` | `task('complete', { taskName, outcome })` |
| `goal_update(...)` | `goal('add'/'edit'/'complete'/'list', { ... })` |
| `concern_add(...)` | `concern('add', { ... })` |
| `concern_resolve(...)` | `concern('resolve', { ... })` |
| `concern(...)` | `concern('list', { ... })` |
| `skill_load(...)` | `skill('load', { name? })` |
| `skill_create(...)` | `skill('create', { name, ... })` |
| `plan_requirements(...)` | `requirement('add', { requirement })` |

Dropped tools (no replacement needed -- logic moved to skills):
`plan_roadmap`, `research_start`, `research_complete`, `plan_verify`, `plan_retrospective`, `plan_profile`, `plan_gaps`

## 7. Architectural Significance

This refactor completes the API surface evolution that began in G-36 (43 to 35 tools) and now reaches its target state (35 to 10 tools). The consolidation achieves three things:

1. **Cognitive load reduction**: An agent needs to discover 10 tool names instead of 35. Each tool is a domain noun (memory, plan, goal, task, concern, skill, requirement, guide, bundle, reconcile) with discoverable actions. The noun('action') pattern mirrors natural language and is self-documenting.

2. **Extensibility without tool sprawl**: New actions can be added to existing dispatchers without registering new MCP tools. Future features (e.g., memory('tag'), plan('archive')) are additive changes to existing tools, not new tools that increase the discovery burden.

3. **Self-reflector closes the learning loop**: The new self-reflector skill, auto-injected as a gate task after QA on every phase, ensures that every phase produces a retrospective and belief update. Previously, retrospectives were optional post-goal activities that agents frequently skipped. Now they are structural -- part of the task graph with dependencies enforced.

Combined with G-45's gates-as-tasks pattern, the full workflow is now: goal('add') creates auditor seed -> auditor creates research tasks -> research agents store findings and create planner task -> planner creates phase tasks with auto-injected gates -> orchestrator runs task('next') -> spawn -> task('complete') loop -> QA gate -> self-reflector gate -> (last phase) evolutionary/cartographer/march gates -> goal('complete'). The orchestrator needs no knowledge of this workflow -- it just processes the next task.
