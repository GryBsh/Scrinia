# Task 9 Brief: Durable Classifier Matrix and Core Registry Provenance

BASE: `047037e`

Scope is limited to Task 9 of `2026-09-01-shared-runtime-and-mcp.md`: prove the single ordered
`SharedClassifier` is the sole source for managed ignore patterns, fingerprint exclusions, and static
cleanup pathspecs, and prove `ISharedBaseRegistry` validates roots by filesystem-only Git evidence. The
classifier and registry already exist (Tasks 2/6/8); this task adds the exhaustive `SharedClassifierEquivalenceTests`
and `SharedBaseRegistryTests` gates and no production change, because the tests are the deliverable.

Round-1 scope is limited to directory/singleton/descendant/reserved-suffix classification equivalence,
first-match precedence under Git-pattern overlap, and registry acceptance of `.git` directories and exact
`gitdir:` worktree markers with rejection of ancestor-only, malformed, workspace-internal, nested, and
overlapping roots. There is no Git invocation or index inspection anywhere in the tests. Acceptance is the
two named Task 9 gates plus the shared and full non-benchmark suite building and passing.
