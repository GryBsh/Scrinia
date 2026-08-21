---
name: feedback_falsify_pins_by_mutation
description: In this repo's SDD workflow, every new test/pin must be falsified by mutation (break the fix, show it fail, restore, show it pass) before it is trusted or reported as coverage.
metadata:
  type: feedback
---

Rule: before trusting or reporting a newly added test as a real pin, break the thing it claims to
pin (revert the fix, neuter the mechanism, add a disallowed value, etc.), run the test and paste
the failure, then restore and paste the pass. A test that cannot fail reports coverage that does
not exist.

**Why:** on the `fix/published-surface-coherence` branch, two of Task 9's own tests passed while
the thing they claimed to pin was broken — one seeded its fixture into the very scope the bug
narrowed to (so the bug was invisible to it), the other used single-element list inputs where the
bug only manifested with two-or-more elements. Both looked correct on inspection and only died
under mutation. This was called out as "the branch's most expensive recurring lesson" and made an
explicit, repeated instruction across multiple task briefs (Task 9, Task 10/T10-A, Task 10/T10-B,
Task 10/T10-E all required it).

**How to apply:** for any new xUnit test/pin in this repo — not just on this branch — do the
break/fail/restore/pass cycle and keep the paste-able output before reporting the work as done.
Self-reported "I verified X" without the actual mutation transcript is not sufficient evidence in
this workflow; the controller/reviewer pattern here reproduces falsification results independently
rather than accepting them on faith (see also the branch's recurring theme of self-reported numbers
not matching reproduced numbers).
