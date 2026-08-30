# Task 7 Report

## Status

Implemented and committed in isolated worktree `unified-memory-addressing-task-1`.

Commit: `3b8e8b3` (`feat: enforce typed CLI address policy`)

## Changes

- Replaced the six CLI memory-command string-to-address call sites with one boundary helper that trims, canonicalizes, parses through `MemoryAddress`, and passes typed addresses to the internal Core/MCP operation path.
- Enforced persistent-only policy consistently for `store`, `show`, `forget`, `append`, `compact`, and both ends of `link`.
- Rejected URI/physical-path syntax before plugin loading, store access, or mutation with `UnsupportedInputForm`.
- Rejected malformed/traversal/rooted address forms with `MalformedAddress`.
- Rejected `~name` and `/temp/name` after canonicalization with `ForbiddenAddressKind`.
- Extended CLI JSON errors with stable category/code and validation detail fields.
- Classified CLI append/store write-limit refusals as `InvalidWriteContent` while retaining the shared Core validator and limits.
- Preserved ordinary content-file handling for `store` and `append`.
- Added focused CLI address-category regression tests and strengthened ephemeral refusal coverage.

The pre-existing untracked `src/Scrinia.Server/` files were not staged or modified. Bundle/docs work was not implemented.

## Verification

- Red test run before implementation: `CliAddressValidationTests` failed with uncaught `MemoryAddressException` for malformed and URI-shaped inputs.
- Focused tests after implementation: 24 passed, 0 failed, 0 skipped.
- Solution build: 15 projects, 0 errors, 0 warnings.
- `git diff --check`: passed.

## Concerns

- The complete solution test suite was not run; focused CLI tests and the solution build were run as requested.
- The repository-level copy of this report was also written because the requested worktree-local brief/report directory was absent; the existing brief was found at the repository-level path.
