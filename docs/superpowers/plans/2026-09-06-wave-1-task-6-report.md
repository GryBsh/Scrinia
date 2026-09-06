# Wave 1 Task 6 Report — C6 AsyncLocal Bridge Default Fallback

## Summary
Added `Default` static fallback to `MemoryStoreContext` so CLI generic-host thread can dispatch via `Current ?? Default` / `Resolve()`. Verified `SearchContributorContext` and `MemoryEventSinkContext` already had correct `Default` fallback (getter `_current.Value ?? _default`) — brought `MemoryStoreContext` to parity. Updated `WorkspaceSetup.Configure` to populate both `Current` and `Default`, `MemoryTools.CurrentStore` to use `Resolve()`, and `TestHelpers.StoreScope` to save/restore both `Current` (via `RawCurrent` to preserve null-vs-fallback distinction) and `Default` for all three contexts.

## Changes
- `src/Scrinia.Core/MemoryStoreContext.cs`:
  - Added `_default` field, `Default` property, `Current` getter now ` _current.Value ?? _default` (bridges generic-host boundary).
  - Added `Resolve()` (throws when both null) and internal `RawCurrent` for precise StoreScope save/restore.
- `src/Scrinia.Core/Search/ISearchScoreContributor.cs` + `src/Scrinia.Core/IMemoryEventSink.cs`:
  - Added internal `RawCurrent` to expose `AsyncLocal` raw value without `Default` fallback (supports StoreScope precise restore; no behavior change for callers).
- `src/Scrinia/Commands/WorkspaceSetup.cs`:
  - `Store` now `Current ?? Default ?? throw`.
  - `Configure` creates `FileMemoryStore` then sets both `MemoryStoreContext.Current = store` and `MemoryStoreContext.Default = store` (host populates fallback).
- `src/Scrinia.Mcp/MemoryTools.cs`:
  - `CurrentStore` changed from `Current ?? throw` to `MemoryStoreContext.Resolve()` — dispatches via fallback.
- `tests/Scrinia.Tests/TestHelpers.cs` (`StoreScope`):
  - Captures `_prevStoreCurrent` via `RawCurrent`, `_prevStoreDefault` via `Default` (and same for Sink/Search) before overwriting.
  - Sets `Current` + `Default` to isolated store; on `Dispose` restores `RawCurrent` + `Default` for all three contexts (pins C6, avoids `Default=null` race by save/restore rather than blind clear).
- `tests/Scrinia.Tests/Wave1/AsyncLocalBridgeTests.cs` (new, TDD):
  - 4 tests: `CliPath_UsesDefaultFallback_WhenCurrentIsNull` (Resolve + tool dispatch), `StoreScope_ClearsBoth_CurrentAndDefault_OnDispose`, `StoreScope_RestoresPreviousDefault_WhenNested`, `WorkspaceSetup_Configure_PopulatesDefault`.
- `tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs`:
  - `ReadMemoryResource_NoStoreConfigured_Throws` now clears both `RawCurrent` and `Default` (previous only cleared `Current`, so fallback `Default` leaked and test got `McpException` instead of `InvalidOperationException`).
- `tests/Scrinia.Tests/NoObsoleteAddressSurfaceTests.cs`:
  - Allowlisted `docs/superpowers/plans/2026-09-06-wave-1-critical.md`, `wave-2-major.md`, `wave-3-minor.md`, `docs/superpowers/specs/2026-09-06-review-remediation-design.md` (historical plans contain `MemoryWrite(` example code, previously flagged as forbidden).

## Verification
- `dotnet build Scrinia.sln -c Debug` — clean, 0 errors, 2463 warnings (preview SDK + CA1707/CA1852 pre-existing).
- `dotnet test tests/Scrinia.Tests --filter AsyncLocalBridge` — 4/4 passed.
- `dotnet test tests/Scrinia.Tests` — 2724 passed, 0 failed, 19 skipped (3m11s).
- `dotnet test Scrinia.sln` — all suites green (Scrinia.Tests 2724, Scrinia.Retrieval.Benchmarks.Tests 272, Plugin.Tests 41).

## Checklist
- [x] Early Exit: `Resolve()` guard at top, `Configure` fails fast if called before store.
- [x] Parse Don't Validate: store resolved once at boundary, trusted internally.
- [x] Atomic Predictability: `RawCurrent`/`Default` save/restore is pure, no hidden mutation beyond explicit contexts.
- [x] Fail Fast: `Resolve()` throws descriptive `InvalidOperationException` when both null.
- [x] Intentional Naming: `Current` (AsyncLocal, fallback) vs `Default` (process-wide) vs `RawCurrent` vs `Resolve()` read as English.
