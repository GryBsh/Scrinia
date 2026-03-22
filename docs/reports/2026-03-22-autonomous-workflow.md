# March Report: G-38 through G-46 — Autonomous Workflow Sprint

**Goals:** 9 goals shipping release audit fixes, behavioral nudges, blocking gates, goal editing, background maintenance, spawned specialists, QA enforcement, gates-as-tasks, and context recovery
**Date:** 2026-03-22
**Status:** Complete (all 9 goals)
**Tests:** 835 start, 911 finish (+76 net)

---

## 1. Summary

This session transformed Scrinia from a system that *advises* agents into one that *enforces* workflow structurally. The arc moved through three phases: (1) fix audit findings and add auto-populate features (G-38/G-39), (2) convert advisory warnings to blocking gates and enforce specialist spawning (G-40 through G-44), and (3) embed all gates and specialist roles into the task graph itself, making the orchestrator a simple `task_next -> spawn -> task_complete` loop (G-45/G-46). By the end, an orchestrator agent needs no knowledge of scrinia internals -- it just processes the next task.

## 2. Goal-by-Goal Changes

### G-38-5bc: Release Audit v3
- `skill_load`/`skill_create` parameter renamed from `skillName` to `name`
- Custom scaffold duplicate-name bug fixed
- AGENTS.md tool table updated to match actual tool surface
- `chaos-engineer` built-in skill text corrected
- `store()` auto-detects `.cs`/`.json`/`.md` file paths in content and records them as `codeRefs` for drift detection

### G-39-667: Behavioral Nudges
- `checkpoint:latest` auto-created on `goal_update(complete)` for instant session recovery
- `plan_status`/`plan_resume` surface drift and staleness alerts automatically
- `store()` auto-sets `reviewWhen` when content contains count patterns (N tests/tools/skills) or version patterns
- `goal_update(add)` searches and shows 0-3 matching backlog items inline
- 33 new tests across 4 test files

### G-40-A6B: Warnings to Gates + Skill-Load Enforcement
- March/evolutionary/cartographer advisory warnings converted to **blocking gates** with `skipGates` + `skipReason` parameters
- Gates fire before goal marking -- blocked = goal stays active
- Thresholds: march (roadmap-only), evolutionary (5+ goals since last run), cartographer (25+ new memories)
- Skip requires a human-confirmed reason string, logged to session
- Spawn protocol preamble added to evolutionary, cartographer, and march-reporter built-in skills enforcing `skill_load()` as methodology source
- 7 new gate tests

### G-41-DAF: Goal Editing
- New `goal_update(action:'edit', goalId, description)` action
- Updates goal descriptions preserving status, outcome, and timestamps
- 5 new tests

### G-42-DFB: Background Knowledge Maintenance
- **Event-triggered** (on store/append): `CompositeEventSink` chains embeddings + `MaintenanceEventSink`. Auto-linking creates bidirectional `ref:` keywords. Orphan detection adds/removes `orphan` keyword. Registered in both CLI and Server.
- **Concern pattern detection**: `concern_add` suggests patterns when 3+ concerns share keywords.
- **Timer-driven**: `MaintenanceCacheService` (`IHostedService`, every 5 min) pre-computes drift/staleness/orphan counts to `.scrinia/cache/maintenance.json`. `plan_status`/`plan_resume` read cache first, fall back to live scan. CLI unchanged.
- 25 new tests across 7 files

### G-43-080: Planner as Spawned Specialist
- Planner built-in skill rewritten with spawn protocol preamble and MANDATORY section directing orchestrators to spawn rather than plan inline
- Output format section matches `plan_tasks` input format exactly
- `research_complete` hint directs to spawn planner agent
- `plan_tasks` description mentions planner agent requirement
- No code changes -- skill text and tool hint updates only

### G-44-ACA: QA Gate Enforcement
- `plan_verify` gates on `qa:latest` memory written by a spawned QA agent
- Blocks without it, passes with it
- `skipQa` parameter with human-confirmed reason for bypass
- QA built-in skill has spawn protocol preamble and writes `qa:latest`
- 4 new tests, 3 updated tests

### G-45-7A2: Gates as Tasks + Planner-as-Task Workflow
- `plan_tasks` auto-injects QA gate task to final wave of every phase
- Evolutionary/cartographer/march tasks auto-injected for last phase only, with `gate:` keyword markers
- `research_complete` auto-creates a wave-0 planner seed task -- planner agent calls `plan_tasks` directly with MCP access
- Orchestrator workflow reduced to: `task_next` -> spawn -> `task_complete` -> loop
- Removed ~155 lines of skip gate code and 11 skip-related tests from `goal_update(complete)` and `plan_verify`
- Concern gate preserved (concerns still block goal completion)

### G-46-600: Context Resume
- `plan_resume` renamed to `context_resume` across all source, guides, and docs
- Inlines `agent:profile` content directly instead of hinting agent to search for it
- Includes active goal description in response
- Includes today's session log if it exists
- Ends with "call `task_next` to continue" -- rational lensing nudges the agent into the task loop
- 2 new tests, 3 updated tests

## 3. Files Touched (Summary)

| Area | Key Files |
|------|-----------|
| MCP tools | `MemoryTools.cs`, `ProjectTools.cs` |
| Core | `MaintenanceEventSink.cs` (new), `CompositeEventSink.cs` (new), `MaintenanceCacheService.cs` (new) |
| Built-in skills | Planner, QA, evolutionary, cartographer, march-reporter, merge-safety (all in `ProjectTools.cs`) |
| Guides | `CLAUDE.md`, `AGENTS.md`, `guide()` raw string in `MemoryTools.cs` |
| Tests | `GateTests.cs`, `GoalEditTests.cs`, `MaintenanceEventSinkTests.cs`, `MaintenanceCacheServiceTests.cs`, `ConcernPatternTests.cs`, `PlannerSeedTaskTests.cs`, `GateTaskInjectionTests.cs`, `QaGateTests.cs`, `ContextResumeTests.cs`, and others |
| Reports | `docs/reports/2026-03-22-autonomous-workflow.md` (this file) |

## 4. Findings

No new concerns were raised during G-38 through G-46. All 54 historical concerns remain resolved.

| ID | Description | Severity | Status | Resolution |
|----|-------------|----------|--------|------------|
| DOC-040 | AGENTS.md stale after G-36 tool consolidation | Medium | Resolved | Updated in G-38 |

## 5. Test Impact

| Suite | Before (G-37) | After (G-46) | Delta |
|-------|---------------|--------------|-------|
| Scrinia.Tests | 760 | 836 | +76 |
| Scrinia.Server.Tests | 63 | 63 | 0 |
| Scrinia.Plugin.Embeddings.Tests | 12 | 12 | 0 |
| **Total** | **835** | **911** | **+76** |

Test progression through the sprint:

| Goal | Total | Delta | Notes |
|------|-------|-------|-------|
| G-38 | 835 | 0 | Audit fixes, no new tests |
| G-39 | 868 | +33 | Staleness, drift, auto-reviewWhen, organic adoption |
| G-40 | 875 | +7 | Gate enforcement tests |
| G-41 | 905 | +30* | Goal editing (5), plus G-42 server tests (+25) |
| G-42 | 900 | -5** | Completion order: G-42 finished before G-41 |
| G-43 | 905 | +5 | Skill text only, counts from G-41 |
| G-44 | 909 | +4 | QA gate tests |
| G-45 | 906 | -3 | +auto-inject tests, -11 removed skip gate tests |
| G-46 | 911 | +5 | Context resume rename tests |

*G-41 and G-42 ran concurrently; combined they added 30 tests.
**Completion timestamps show G-42 (15:19) finished before G-41 (15:30).

## 6. Security Posture

- **No new security concerns.** All 11 SEC-* findings from prior audits remain resolved.
- **Concern gate preserved**: G-45 removed skip mechanisms for march/evolutionary/cartographer gates but explicitly preserved the concern gate. Open high/medium concerns still block `goal_update(complete)`.
- **Skip gate removal** (G-45): The `skipGates`/`skipReason` parameters added in G-40 were removed just two goals later. Gates became tasks, eliminating the bypass surface entirely. This is a security improvement -- there is no mechanism to skip QA, evolutionary, cartographer, or march tasks.

## 7. Configuration Changes

No new configuration settings were introduced. All changes are internal workflow enforcement.

| Change | Impact |
|--------|--------|
| `plan_resume` renamed to `context_resume` | Agents calling `plan_resume` will get an error. Update any custom scripts or agent prompts referencing this tool name. |
| `qa:latest` memory required by `plan_verify` | QA agents must write this memory. Existing QA workflows that do not use the spawned QA skill will be blocked. |
| `plan_tasks` auto-injects gate tasks | Task counts per phase will be higher than manually specified. Agents should not be surprised by QA/evolutionary/cartographer/march tasks appearing. |

## 8. Architectural Significance

This sprint completed the transition from **agent-dependent workflow** to **structurally enforced workflow**:

1. **Before G-38**: The agent needed to remember to run QA, evolutionary scans, cartography, and march reports. Hints in tool responses were the only enforcement.

2. **G-40**: Hints became blocking gates with skip mechanisms. Better, but still relied on the orchestrator to invoke specialists correctly.

3. **G-43/G-44**: Planner and QA became spawned specialists with skill_load() as the methodology source. The orchestrator passes methodology, not paraphrased instructions.

4. **G-45**: Gates became tasks in the task graph itself. The orchestrator's entire workflow collapsed to a single loop: `task_next` -> spawn -> `task_complete`. No inline planning, no skill loading, no remembering steps.

5. **G-46**: Session recovery (`context_resume`) now inlines everything the agent needs -- profile, goal, session log -- and ends with "call `task_next` to continue." A fresh agent with zero context can resume mid-goal.

The result: orchestrator complexity dropped to near zero. All methodology lives in skills. All enforcement lives in the task graph. All recovery lives in `context_resume`.
