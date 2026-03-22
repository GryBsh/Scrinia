# March Report: G-34 — Post-Goal Workflow Enforcement

**Goal:** Replace advisory post-goal hints with structural enforcement
**Dates:** 2026-03-22
**Status:** Complete
**Tests:** 847 total (786 core + 61 server)

---

## 1. Summary

Post-goal workflow steps (session logging, march reports, evolutionary/cartographer scans) were consistently skipped despite being documented as "mandatory" in hints. This goal replaces advisory text with structural enforcement — the tools themselves check preconditions and warn when steps are missed.

## 2. Changes

### Automatic Actions
| Action | Mechanism |
|--------|-----------|
| Session log update | goal_update(complete) auto-appends to sessions:YYYY-MM-DD — zero agent effort |

### Warning Gates (in goal_update complete response)
| Gate | Trigger | Warning |
|------|---------|---------|
| March report | No .md file in docs/reports/ matching today | "Produce one: skill_load('march-reporter')" |
| Evolutionary | 3+ goals completed today without cartography:* activity | "Run skill_load('evolutionary')" |
| Cartographer | 10+ memories created since last cartography:* entry | "Run skill_load('cartographer')" |

### Branch-Safe ID Propagation
- Task/research/retro names now use full branch-safe goal ID (g34-6d3-01-1-01)
- 4 regex extraction sites updated to capture hex suffix in group

### plan_retrospective Enhancement
- CheckCartographerNeeded helper called after each phase retro
- Warns when memory growth exceeds cartographer threshold

## 3. Design Decisions

- **Warnings, not hard blocks**: Gates warn but don't prevent goal completion. A failed check should never block real work.
- **Best-effort wrapped**: All checks in try-catch — a broken workspace root derivation or missing scope doesn't crash goal_update.
- **Auto over manual**: Session log is fully automatic. March report and evolutionary/cartographer are warned because they require agent judgment.
- **Shared helper**: CheckCartographerNeeded extracted for reuse across goal_update and plan_retrospective.

## 4. Test Impact

| Suite | Before | After | Delta |
|-------|--------|-------|-------|
| Scrinia.Tests | 783 | 786 | +3 |
| Scrinia.Server.Tests | 61 | 61 | 0 |
| **Total** | **844** | **847** | **+3** |
