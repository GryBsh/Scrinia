# UX Polish Review — LongMemEval V2 final contract fixes

**Date:** 2026-10-19
**Scope:** Learning and retrieval prompts; README storage/saved-history contract; SYSTEM_DESCRIPTION.template.md final paragraphs, changed after a048cb15
**Reviewer:** ux-polish-reviewer agent

The final prompt and storage/history changes match the inspected contracts; all three original prompt findings are resolved, with no new findings.

**Must fix**

None.

**Should fix**

None.

**Consider**

None within this technical-fix scope.

**Consistency notes**

- [Learning prompt:2](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/Scrinia.LongMemEvalV2.Worker/Learning/learning-instructions.txt:2) resolves the earlier note-name and source-array omissions. Its 7,900 UTF-16 code-unit maximum matches the guard; oversized elements are rejected before dispatch.
- [Retrieval prompt:3](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/Scrinia.LongMemEvalV2.Worker/Retrieval/retrieval-instructions.txt:3) resolves the bare-path ambiguity. Line 4 resolves the `stateIndex` omission and identifies zero as an example.
- Host source subdivision preserves complete serialized strings and Unicode scalar boundaries. Original-source materialization checks every storage chunk through public recall before returning original evidence.
- [README storage contract:64](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/longmemeval-v2/README.md:64) correctly distinguishes bounded derived elements from host-split sources. The documented 7,992-unit recall budget includes the chunk label.
- Model identity is checked from the first response before model-directed tool dispatch. Loaded history requires an independent ordered binding that includes screenshot content in its logical hashes.
- Recovery wording accurately limits marker retirement to proof from the verified current generation under lock. Unproven markers block saving.
- The [system description’s final paragraphs](C:/Users/nickd/.codex/worktrees/longmemeval-agent-runner/Scrinia/benchmarks/longmemeval-v2/SYSTEM_DESCRIPTION.template.md:17) agree with these contracts.
