# Task 4.3 report — shared envelope goldens

## Status

Complete. Shared envelope goldens and related shared-surface checks remain green; Git guidance now
quotes dynamic repository roots safely at the single rendering boundary.

## Root-cause and TDD evidence

- Existing shared envelope coverage was green at 38/38, but `QuoteRoot` doubled embedded quotes and
  left backslashes, `$`, and backticks unescaped inside a shell double-quoted argument.
- RED: `SharedGitGuidanceTests.Root_escapes_double_quote_backslash_and_command_substitution_characters`
  failed because the rendered root was not shell-safe.
- GREEN: `QuoteRoot` now escapes shell-sensitive characters while preserving the established command
  shape. The focused regression passes.
- No envelope DTO order, wire name, null/empty behavior, golden snapshot, or source-generated context
  required alteration; existing duplicate-key and scalar round-trip coverage remains intact.

## Verification

- Focused regression: **1 passed, 0 failed**.
- Shared suite (`SharedEnvelopeGoldensTests`, `SharedSchemaTests`, `SharedPrivacyTests`,
  `SharedNoGitProcessTests`, `SharedNoPromptCorrelationTests`): **43 passed, 0 failed, 0 skipped**.
- `dotnet build Scrinia.sln --no-restore`: **13 projects, 0 errors, 55 warnings**.
- `git diff --check`: passed.

## Scope

- `src/Scrinia.Mcp/Shared/SharedGitGuidance.cs`: hardened root shell quoting.
- `tests/Scrinia.Tests/Shared/SharedGitGuidanceTests.cs`: added the red/green regression.
- `task-4.3-report.md`: recorded evidence.
- No unrelated historical artifact was changed. No commit was created.
