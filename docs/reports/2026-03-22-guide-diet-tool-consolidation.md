# March Report: G-36 — Guide Diet + Tool Consolidation + QA Skill

**Goal:** Revolutionary reorientation of the scrinia toolset before shipping
**Dates:** 2026-03-22
**Status:** Complete (all 3 phases)
**Tests:** 833 total (760 core + 61 server + 12 embeddings)

---

## 1. Summary

Scrinia's toolset was audited for usability and intuition from the agent perspective. Three problems were identified: the guide was too long (25K chars), there were too many tools (43), and verification was a rubber stamp. All three were fixed in one goal.

## 2. Phase 01: Tool Consolidation (43 → 35)

8 tools removed, 3 merged:
- show() absorbs get_chunk + chunk_count (optional chunk parameter)
- reconcile() absorbs resolve_conflict (conflictId/choice params)
- list() absorbs check_drift (mode="drift")
- Removed: encode, setup_hooks, backlog_promote, suggest_patterns

25 compiled regex fields replace 30+ inline patterns. GoalIdCore const composes into all variants. Fixed goal ID case-sensitivity bug (G-34 reuse).

Net: -509 lines of code.

## 3. Phase 02: Guide Diet (25K → 6.9K, 73% reduction)

Guide rewritten from 333 lines to 114 lines. Detailed methodology extracted into skills:
- merge-safety skill (new built-in): full multi-user merge conflict methodology
- Existing skills already cover: planner, auditor, evolutionary, cartographer, march-reporter

Guide now teaches fundamentals. Skills teach methodology. Tools enforce workflow.

Net: -176 lines.

## 4. Phase 03: QA Skill + Verification Gates

Built-in QA skill with 5-step methodology: run tests, verify build, check criteria, check regressions, validate task. plan_verify warns when evidence lacks test output. QA is step 0 in goal completion (before march report).

## 5. Tool Count

| Category | Before | After |
|----------|--------|-------|
| Memory tools | 21 | 15 |
| Planning tools | 22 | 20 |
| **Total** | **43** | **35** |

## 6. Test Impact

| Suite | Before | After |
|-------|--------|-------|
| Scrinia.Tests | 759 | 760 |
| Scrinia.Server.Tests | 61 | 61 |
| **Total** | **832** | **833** |

Note: test count decreased from prior goals' 859 because 27 tests for removed tools were deleted while 1 new QA gate test was added.
