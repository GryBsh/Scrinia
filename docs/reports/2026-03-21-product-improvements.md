# March Report: G-2 — Scrinia Product Improvements

**Goal:** Implement product improvements identified by the product-evolutionary skill
**Dates:** 2026-03-21
**Status:** Complete (all 4 phases verified ALL_PASS)
**Tests:** 821 total (760 core + 61 server)

---

## 1. Summary

9 new MCP tools and 1 planning infrastructure improvement shipped across 4 phases,
addressing all 5 PEA proposals plus 4 prior backlog items. The scrinia toolset is now
significantly more capable for knowledge base maintenance, drift detection, and
cross-domain connection indexing.

## 2. New Tools

### Phase 01: Foundation
| Tool | Purpose |
|------|---------|
| update_meta | Modify keywords/description/review conditions without re-encoding artifact |
| backlog_promote | Convert backlog:* entry to goal via goal_update(add) |
| plan_tasks file-conflict | Auto-detect same-wave file overlaps, warn in response |

### Phase 02: Reverse-Reference Index
| Tool | Purpose |
|------|---------|
| references(target) | Find all memories referencing a file or memory name |
| link(from, to, reason) | Bidirectional ref: keywords between memories |
| ReferenceExtractor | Auto-extract file:path and ref:name keywords during store/append |

### Phase 03: Drift Detection
| Tool | Purpose |
|------|---------|
| codeRefs on store() | Record SHA-256 hashes of referenced files at store time |
| check_drift() | Compare stored hashes to current files, report DRIFT/MISSING |
| list() [drift] markers | Flag drifted memories in full listing |

### Phase 04: Compaction + Patterns
| Tool | Purpose |
|------|---------|
| compact(name, keepRecent?) | Merge multi-chunk memories, archive original |
| suggest_patterns() | Detect 3+ keyword overlap in concerns, suggest pattern memories |

## 3. PEA Proposals Addressed

| PEA | Proposal | Status |
|-----|----------|--------|
| PEA-001 | update_meta | Implemented (Phase 01) |
| PEA-002 | codeRefs + check_drift | Implemented (Phase 03) |
| PEA-003 | compact | Implemented (Phase 04) |
| PEA-004 | Reverse-reference index | Implemented (Phase 02) |
| PEA-005 | plan_tasks file-conflict | Implemented (Phase 01) |

## 4. Backlog Items Addressed

| Item | Status |
|------|--------|
| Native link/relationship tool | Implemented as link() (Phase 02) |
| Backlog promotion workflow | Implemented as backlog_promote (Phase 01) |
| Stale memory auto-flagging | Implemented via check_drift + list [drift] (Phase 03) |
| Recurring pattern auto-detection | Implemented as suggest_patterns (Phase 04) |

## 5. Test Impact

| Suite | Before | After | Delta |
|-------|--------|-------|-------|
| Scrinia.Tests | 738 | 760 | +22 |
| Scrinia.Server.Tests | 61 | 61 | 0 |
| **Total** | **799** | **821** | **+22** |

## 6. Architecture Decisions

- **Refs as keywords**: File and memory references stored as prefixed keywords (file:path, ref:name) — piggybacks on existing BM25 search, no new index structures
- **compact is mechanical**: Merges chunks, agents decide when/what to compact. LLM summarization stays in the agent workflow (evolutionary skill), not in the tool.
- **codeRefs on ArtifactEntry**: Dictionary<string, string> maps file path → SHA-256 hash. Backward compatible (null default for existing memories).
- **link() reuses update_meta**: Bidirectional keyword insertion, no new data structures.

## 7. Deferred

- Agent MCP tool (high complexity, deferred)
- Auto-detect test counts (evolutionary handles it for now)
- Skill override precedence (document-only fix may suffice)
