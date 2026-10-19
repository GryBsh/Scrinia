# UX Polish Review — LongMemEval V2 agent prompts

**Date:** 2026-10-19
**Scope:** benchmarks/Scrinia.LongMemEvalV2.Worker/Learning/learning-instructions.txt; benchmarks/Scrinia.LongMemEvalV2.Worker/Retrieval/retrieval-instructions.txt
**Reviewer:** ux-polish-reviewer agent

The prompts preserve the V2 role boundaries and use restrained language; one output-schema ambiguity needs fixing, and two contract details should be clearer.

**Must fix**

1. [retrieval-instructions.txt:3](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/Scrinia.LongMemEvalV2.Worker/Retrieval/retrieval-instructions.txt:3) — “Or an exact path” can be read as permitting a bare string reference. `EvidenceSelection.Parse` requires every memory reference to contain `kind`, `path`, and `chunks`.

   **Before:** “A memory reference is {"kind":"memory","path":"/derived/...","chunks":[1]} or an exact /source/... path. Chunk indices are one-based; recall the memory to identify the appropriate chunks.”

   **After:** “A memory reference is {"kind":"memory","path":"/derived/...","chunks":[1]}. For a /source/... memory, use the same object shape with its exact path. Chunk indices are one-based; recall the memory to identify the appropriate chunks.”

**Should fix**

1. [learning-instructions.txt:1](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/Scrinia.LongMemEvalV2.Worker/Learning/learning-instructions.txt:1) — Explain the accepted note name and citation shape. The guard rejects names containing spaces, underscores, or additional slashes, and requires `attributes.source` to be a nonempty array.

   **Before:** “Store useful factual notes only with remember under /derived/<trajectorySourceHash>/<note-name>. Supply a nonblank description of the resulting note and attributes.source containing one or more exact source IDs from this batch.”

   **After:** “Store useful factual notes only with remember under /derived/<trajectorySourceHash>/<note-name>. Use only ASCII letters, digits and hyphens in <note-name>. Supply a nonblank description and set attributes.source to a nonempty array of exact source IDs from this batch.”

2. [retrieval-instructions.txt:4](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/Scrinia.LongMemEvalV2.Worker/Retrieval/retrieval-instructions.txt:4) — State that `stateIndex` must belong to the referenced source. Explaining its numbering alone leaves an avoidable validation failure: the host rejects a valid source ID paired with another state’s index.

   **Before:** “stateIndex is zero-based.”

   **After:** “stateIndex is zero-based and must match the referenced source record; recall that record to verify the value.”

**Consider**

None.

**Consistency notes**

- Keep tool names and JSON field names exact.
- Distinguish memory references (`path` and `chunks`) from original-source references (`sourceId` and `stateIndex`).
- Preserve the existing direct, imperative voice.
- The read-only retrieval tools, immutable source boundary, and host materialization claims match the inspected contracts. No unsupported capability claims found.
