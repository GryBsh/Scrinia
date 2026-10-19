# Required memory descriptions implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Require useful retrieval descriptions on MCP remember calls, preserve detailed evidence, and evaluate guidance on the six diagnosed failures.
**Architecture:** Forward descriptions through existing transactional writes. Preserve legacy/CLI excerpt fallback. Search presents memory descriptions separately from chunk excerpts without ranking changes. Teach evidence-preserving learning and bounded full retrieval.
**Tech Stack:** C#/.NET 10, MCP SDK, xUnit, existing LongMemEval runner.
**Spec:** User-approved discussion in this chat, 2026-10-18: required description; stronger learning/retrieval guidance; test failed cases; full benchmark remains paused.

## Global constraints
- Keep existing worktree changes and all frozen runs/stores/grades intact.
- Required nonblank description on workspace and shared MCP remember, including reserved paths.
- Description describes resulting whole memory; append retains old content and replaces description.
- No new ranking algorithm or storage format migration; existing descriptions/excerpt fallback remain readable.
- Agent/judge Ornith Q4_K_M, existing sampling and 16K limits unchanged.
- Diagnostic replays are separate artifacts, not baseline grades.

## Review focus
- Missing/null/whitespace description must not mutate any route.
- Append and explicit replacement must persist description with content in the same transaction.
- Chunk hits must expose the parent description without suppressing correction context.
- Search must still find content omitted from description.
- Existing CLI callers and old stores must keep working.

### Task 1: Required description contract and storage
Files: MemoryTools.cs, ReservedPathDispatcher.cs, MemoryTools.Agent.cs, MemoryTools.Skills.cs, file metadata models, SharedTools.cs, SharedOperations.cs; RememberDescriptionTests.cs and existing callers.
- [x] Write schema and route validation/persistence tests using real tools and stores.
- [x] Run tests and observe failure on missing public parameter.
- [x] Add required string description after content; reject blank before mutation; route through existing writes and sidecar transactions.
- [x] Update existing direct callers/scripted MCP fixtures.
- [x] Run focused tests.

### Task 2: Retrieval presentation and guidance
Files: SearchResultRenderer.cs, guide.md, public examples/docs, AgentInstructions.cs, benchmark README.
- [x] Test description visibility on chunk hits and content search unaffected by descriptions.
- [x] Present parent description separately from partial chunk excerpts, with recall instruction.
- [x] Update learning: self-contained evidence, source date plus relative time, completed/planned status, short quotes, no inferred retirement.
- [x] Update answering: retrieve full relevant content, resolve truncation, evidence inventory for aggregates, bounded focused followups.
- [x] Update public required-parameter examples and migration note; review copy.

### Task 3: Verification and diagnostic replays
- [x] Run solution tests and build dedicated immutable payload/runner. Eight assertions/guide failures corrected; all passed targeted rechecks. See the results report for raw suite totals and limitations.
- [x] Replay answer-only against copies of all six unchanged stores.
- [x] Replay relevant source sessions into fresh stores for all six cases and answer; preserve ambiguity in Q87/Q90.
- [x] Record provenance, tool errors, descriptions, recall coverage, grades and factual evidence; verify original artifacts unchanged and full run paused.
- [x] Obtain independent code/copy review (no blocking code findings after fixes); diagnostic limitations are recorded in docs/reviews/2026-10-18-required-memory-descriptions.md.
