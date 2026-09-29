# B3 Task 6 report — MCP and CLI aggregate mapping

## Status

Implemented and hardened the internal MCP and CLI mappings for typed multi-memory aggregate
outcomes without adding a command, tool, facade DTO, or alternate lifecycle path. The checkout has
no supported public multi-memory entry point and `FileMemoryStore.ExecuteMultiMemoryAsync` has no
facade caller; the approved boundary is therefore an internal MCP emitter and an internal CLI
emitter that accept the typed Core result. No public route is invented, and existing single-memory
delegation paths remain unchanged.

## Changes

- `src/Scrinia.Mcp/MemoryTools.Core.cs` maps committed, cleanup-pending, rolled-back, recovery,
  blocked, and rejected aggregates through `McpResponse`, preserving coordinator IDs, scope,
  safe typed aggregate details, participant-specific reasons with aggregate fallback, participant
  summaries, and safe evidence filenames. `EmitMultiMemoryResult` is the internal MCP call seam.
- `src/Scrinia/Commands/ScriniaCommands.Memory.cs` independently applies the same mapping through
  `EmitMultiMemoryResult`, then delegates to the existing `EmitMcpResult` path, preserving plain
  output, `CliMcpOutput`, and exit classes.
- `src/Scrinia/Commands/ScriniaCommands.cs` keeps aggregate invalid/rejected errors in the existing
  `CliMcpOutput` wrapper so coordinator and participant correlation is not discarded by generic
  CLI error handling.
- Aggregate warnings/details and participant reasons cross the boundary only when they are stable
  text; the allowlist admits only explicit stable punctuation, alphanumeric characters, and ASCII
  space. CR, LF, tab, all other control characters, and non-printing whitespace are redacted.
  Rooted Unix/Windows paths, traversal, URI/path-like values, foreign roots, and raw exception
  details remain redacted.
- `tests/Scrinia.Tests/MultiMemoryTransactionFacadeTests.cs` covers the MCP emitter, all outcome
  classes, typed aggregate details, participant-specific reason fallback, privacy, and evidence
  sanitization.
- `tests/Scrinia.Tests/CliMultiMemoryTransactionTests.cs` covers the CLI emitter, plain output,
  exit codes, rejected JSON wrapper correlation, source-generated round trips, and MCP/CLI parity.

No core lifecycle, B2 adapter, event, restore, append, file-entity, public tool/command, or
unrelated worktree files were changed. No new directly serialized DTO was needed: CLI JSON uses
the existing source-generated `CliMcpOutput` wrapper.

## Verification

- TDD red run: focused tests failed because both mapping helpers were absent.
- Focused facade/CLI lane: **39 passed, 0 failed, 0 skipped**.
- Control-whitespace regression lane: **14 passed, 0 failed, 0 skipped** across MCP and CLI,
  covering CR, LF, tab, vertical tab, NEL, no-break space, and figure space.
- Full B3/B2 regression lane: **784 passed, 0 failed, 0 skipped**.
- Release solution build: **0 errors**; existing analyzer warnings remain.
- Full solution test invocation exceeded the available five-minute timeout without reporting a
  failure; focused and B3/B2 lanes completed successfully.
- Commit and staging were not performed; repository git operations are owned by the orchestrator.
