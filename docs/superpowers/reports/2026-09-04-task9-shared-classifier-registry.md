# Task 9 Report — Durable Classifier Matrix and Core Registry Provenance

## Implemented

Task 9 is a proof task: Tasks 1–8 already own the single ordered classifier and the
filesystem-only registry validation, so this task adds the exhaustive tests that lock the
contract and reveal no implementation gap. No production source was changed.

- `tests/Scrinia.Tests/Shared/SharedClassifierEquivalenceTests.cs` —
  `Classifier_sets_match_for_every_directory_file_and_descendant_case` asserts, for every
  canonical-root directory, singleton, any-depth `.tmp`, root-child `.lock`, descendant,
  first-match overlap, reserved-suffix, and authoritative path, that the classifier, the
  fingerprint exclusion, the managed ignore block, and the static cleanup pathspecs all agree
  on derived-vs-authoritative. Two further facts prove the managed block and cleanup argv are
  built from the one ordered rule set, that Git-pattern overlap keeps first-match precedence
  (`embeddings/cache.tmp` is `EmbeddingIndexes`, not `TemporaryTree`), and that classification is
  a pure string function (filesystem-independent, so deleted-but-indexed paths classify
  identically). The test ships a faithful gitignore and `:(glob)` pathspec matcher so the three
  views are compared semantically, not just structurally.
- `tests/Scrinia.Tests/Shared/SharedBaseRegistryTests.cs` —
  `Registry_accepts_git_directory_or_exact_utf8_worktree_marker_and_rejects_ancestor_only_nested_and_overlapping_roots`
  proves `.git` directory evidence, LF/CRLF `gitdir:` worktree markers with absolute and relative
  existing targets, the ancestor-only rejection, nested and parent-overlap collisions, and a
  fixed `project-git` policy. Theory cases cover malformed markers (missing space, wrong case,
  embedded newline, empty target, BOM) and workspace/`.scrinia` containment rejection.
  `Collisions_reports_overlapping_registered_roots` uses a seeded store to assert the collision
  surface. Every check is filesystem-only — no Git process and no index inspection.

## Verification

- `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Scrinia.Tests.Shared.SharedClassifierEquivalenceTests|FullyQualifiedName~Scrinia.Tests.Shared.SharedBaseRegistryTests`: 48 passed, 0 failed.
- `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Scrinia.Tests.Shared|DerivedClassifierEquivalenceTests`: 211 passed (no regression to the Phase 2 `DerivedClassifierEquivalenceTests`).
- Full suite `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`: 2560 passed, 20 skipped (benchmark), 0 failed.
- `dotnet build Scrinia.sln`: build succeeded, 0 errors.

## Follow-up / Concerns

- The classifier equivalence test reimplements gitignore and `:(glob)` matching semantics
  (directory-subtree coverage, `**/` → optional prefix, anchored vs basename). It mirrors the
  production `ManagedIgnoreBlock`/`WorkspaceConfig` usage only for these fixed patterns; if the
  classifier gains a new rule, the representative table and the matcher must grow together to
  preserve the §4.1 "new durable file type is classified before release" invariant.
- `SharedClassifierEquivalenceTests` and the Phase 2 `DerivedClassifierEquivalenceTests` overlap;
  the former is the Task 9 exhaustive equivalence/gateway proof, the latter the Phase 2 ordering
  proof. Both are retained because they assert different invariants.
