# Task 5 Report — env-aware `Inspect`/`Migrate`/`Set`

## Status
DONE. Tracking-precedence bypass bug fixed; `dotnet test Scrinia.sln` is green.

## What was changed
The migration service read the workspace file directly and ignored `SCRINIA_WORKSPACE_TRACKING` /
`SCRINIA__WORKSPACE__TRACKING`, so `tracking status` could report a policy different from the
effective one. Env is now threaded through `Read` → `InspectLocked` → `Inspect`/`Migrate`/`Set`, and
the CLI computes raw env values and passes them in.

### `src/Scrinia.Core/Migration/LegacyMigrationState.cs`
- `Read(string workspaceRoot)` → `Read(string workspaceRoot, string? envSingle, string? envDouble)`.
  Replaced the direct `LoadValuesUncached` tracking extraction with
  `EffectivePolicy.ResolveEffective(envSingle, envDouble, fileValue)`, mapping `Unset → null` so a
  missing policy is not misread as a real policy downstream.
- `Inspect(string)` → `Inspect(string, string?, string?)`; passes env to `InspectLocked`.
- `InspectLocked(string)` → `InspectLocked(string, string?, string?)`; passes env to `Read`.
- `Migrate(...)` / `Set(...)` gained trailing `string? envSingle = null, string? envDouble = null`
  and forward them to `InspectLocked`. `Recover` calls left unchanged.

### `src/Scrinia/Commands/TrackingCommands.cs`
- `Status` and `Apply` compute `Environment.GetEnvironmentVariable(EffectivePolicy.EnvSingle/EnvDouble)`
  and pass them to `Inspect`, `Migrate`, `Set`.

### Tests
- `tests/Scrinia.Tests/TrackingPrecedenceTests.cs`: three new tests (single-wins-over-double-and-file,
  double-wins-over-missing-file, malformed-env maps to `WorkspaceConfigMalformed`). Env-setters use
  `try/finally` cleanup so process-wide env vars never leak into parallel collections.
- `tests/Scrinia.Tests/LegacyMigrationServiceTests.cs`, `B0CompatibilityHarness.cs`,
  `tests/Scrinia.Tests/TrackingSetTests.cs`: updated `Inspect(...)` callers to pass `(null, null)`
  (file-only, preserving prior behavior). `Migrate`/`Set` callers unchanged (default env params).

## Test results
```
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter TrackingPrecedence
Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7

dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "LegacyMigrationService|TrackingPrecedence|TrackingSet|B0Compatibility"
Passed!  - Failed: 0, Passed: 49, Skipped: 0, Total: 49

dotnet test Scrinia.sln
Scrinia.Tests.dll         Passed!  Failed: 0, Passed: 3711, Total: 3731
Scrinia.Retrieval.Benchmarks.Tests.dll  Passed!  Passed: 272
Scrinia.Plugin.Embeddings.Tests.dll     Passed!  Passed: 63
```

## Commits
- `core: route tracking precedence through EffectivePolicy in migration service` (this task).

## Concerns
- `EffectivePolicy.ResolveEffective` returns `TrackingPolicy.Unset` (not null) for empty input, so the
  `Unset → null` switch in `Read` is required; without it `ClassifyFilesystem` misread "no policy" as
  `ProjectGit` and several `LegacyMigrationServiceTests` failed. This is the one behavioral subtlety of
  the fix.
- `Inspect` catches `ScriniaConfigurationException` and maps it to `WorkspaceConfigMalformed`, so a
  malformed env value surfaces through `Inspect` as an error code rather than a throw; the malformed
  test asserts that mapped outcome. The throw itself is already covered at the `ResolveEffective`
  level by Task 1 (`EffectivePolicyTests`).
