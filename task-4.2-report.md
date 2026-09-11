# Task 4.2 report — onboarding and obsolete address surface

## Status

Focused onboarding and obsolete-address coverage is green. Historical artifacts were not edited.

## Root-cause and scan evidence

- The active README, contributor guide, and CLI architecture descriptions still advertised the
  retired `reconcile` command or deleted merge project/skill surfaces.
- Address-facing onboarding did not have a regression covering the current shared-base command,
  fixed `project-git` policy, and canonical persistent/ephemeral address examples across all four
  surfaces.
- The tracked-text obsolete-address scan reached its assertions and reported no unclassified
  active match. Explicit rejection guidance, negative tests/fixtures, source compatibility text,
  and historical documents remain classified by exact path in
  `NoObsoleteAddressSurfaceTests`; the scan expression and allowlist were not broadened.

## TDD evidence

- RED: the new `HookAdvisoryOnboardingTests.AddressFacingOnboarding_UsesCurrentCommandsAndCanonicalForms`
  failed because the embedded guide used `/topic/name`, while the initial assertion required
  `/topic/subject`.
- GREEN: the assertion now accepts the canonical `/topic/name` or `/topic/subject` examples, and
  the focused suite passes.
- Required negative examples remain in address rejection guidance and negative contract fixtures;
  no historical artifact was rewritten.

## Verification

- Focused command: **12 passed, 0 failed, 0 skipped**.
- Repository scan: **0 unclassified active matches**.
- Historical SHA-256 checks: `docs/multi-user-setup.md` `4ffb120fe214463e82bfe95257346b5ac37c2f79d724954f1ea3df74cd7cdd82`;
  protected merge-retirement plan `272df673359ca1087ab2fdb995a30055c1e681a4b614a95b282dc82b6780dd13`;
  shared-memory design `f61e2fbfe3b268cbd0097ec3a7efb2ba191d0c12f8895c618631776dba555f8a`;
  specification review `3d1364da2278ee765c6e4a0ebb86ab7344344b10568f6f00a`;
  `CHANGELOG.md` `22181b7765314dd7aae8453aabe785bd3ac6ad335f5b2e5070d2d383a82aa071`.
- `git diff --check`: passed.
- `dotnet build Scrinia.sln --no-restore`: **13 projects, 0 errors, 55 warnings**.
- Normal solution test run was started but timed out after the known model-relocation group failed
  with `UnauthorizedAccessException` at `tests/Scrinia.Tests/ModelRelocationTests.cs:80`.

## Changed-file scope

- `README.md`, `AGENTS.md`, `docs/architecture/cli.md`, `docs/architecture/overview.md`:
  removed retired active command/project descriptions and retained current shared-base guidance.
- `docs/security.md`: added canonical address and shared-base guidance.
- `tests/Scrinia.Tests/HookAdvisoryOnboardingTests.cs`: added exact onboarding/address regressions.
- No protected historical artifact was changed. The complete 28-row Phase A ledger remains in scope;
  no unrelated failure was reclassified or dismissed.

No commit was created.
