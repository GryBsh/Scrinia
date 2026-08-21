---
name: feedback-review-method
description: How reviews are run in this repo — falsify every comment-sourced claim, mutation-test each new assertion, and never trust the language server
metadata:
  type: feedback
---

Treat comment-sourced claims as presumptively wrong — including XML doc comments, the
implementer's own task report, and prior review verdicts. Verify each factual claim against
the code it describes before accepting it. For every new test, ask explicitly: *what single
change would make this test pass while the behaviour is broken?* Report the answer.

**Why:** this codebase's doc comments are unusually long and assertive (they encode design
rulings, not just descriptions), which makes a wrong one very persuasive. Prior rounds shipped
false "facts" into execution when claims were taken at face value. Tests that assert only
`NotBeNullOrEmpty` or a status name have repeatedly passed against broken behaviour.

**How to apply:** on any scoped re-review of a fix round, trace the premise of each fix to its
source (server endpoint, store method) rather than to the comment explaining it. Prefer
assertions that name the specific string/field that distinguishes the correct path from the
plausible-but-wrong one.

A recurring pattern worth naming: the implementer's report cites "I ran test X before and after
the change and it passed both times" as evidence the change is correct. That is evidence the
change is *harmless*, never that it is *pinned* — an unchanged verdict from an existing test
usually means that test cannot observe the new branch at all. Convert every such sentence into
a mutation: invert the new branch, rebuild, run the whole solution. A mutant that survives the
full suite is an uncovered behaviour change, and it must be reported even when the
implementation is correct.

For a **consolidation** task ("N call sites now share one helper"), the mutation is not an
inverted branch — it is *reverting each call site to its pre-consolidation expression* and
running the full solution. A site whose revert leaves the suite green was consolidated on
architecture alone, with no test observing the behaviour it gained or lost. On Task 13 this
exposed 4 of 8 claimed sites as unpinned, including two that had a real behaviour delta.

Falsification sections in task reports are themselves a claim: re-run the mutant the report
*describes*, not the one whose output it pastes. On Task 8 the report said it reverted only the
`Success(...)` argument while keeping the shared emitter — that mutant fails 1 test; the pasted
output (2 failures) could only have come from reverting the whole pre-fix chain. The fix was
still genuinely pinned, but the prose and the evidence did not match.

The language server in this workspace emits phantom diagnostics and has been wrong on every
task across multiple branches. Judge by reading the code and by `rtk dotnet build` only —
never by the diagnostic stream. Note that a 2-second `rtk dotnet build` is an incremental
no-op build, so it confirms nothing was broken only if something was actually recompiled.

See also [[project-scrinia-copy-rulings]].
