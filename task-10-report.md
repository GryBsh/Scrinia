# Task 10 Report: Remove Temporary Adapters and Lock Local/Shared Disjointness

## What changed

Task 10 removed the last temporary adapter seam from the shared-serve startup path and locked the
local/shared boundary. Three files changed; the local serve is byte-for-byte untouched.

- `src/Scrinia/Commands/SharedServeHostAdapter.cs` — `OpenEnabledBases()` now wires to Core instead of
  returning an empty list. It validates the enabled key (fail before transport with
  `NO_SHARED_BASES_ENABLED` when absent), then opens every enabled base that `ISharedBaseRegistry`
  resolves and `ISharedBaseRuntimeFactory` can validate. A base whose root is unavailable (deleted,
  non-absolute, or otherwise rejected by the factory) is skipped rather than failing startup, which is
  the Task 4 "allow unavailable bases" gate re-expressed through the real factory instead of a stub.
- `src/Scrinia.Mcp/Shared/SharedServer.cs` — removed the unused `using Scrinia.Mcp;` import so the shared
  orchestration surface keeps consuming only Core interfaces and shared DTOs.
- `tests/Scrinia.Tests/Shared/SharedNoAdapterTests.cs` (new) and
  `tests/Scrinia.Tests/Shared/SharedLocalDisjointnessTests.cs` (new) — the two Task 10 gates.

## What did NOT change (in scope)

- `src/Scrinia/Commands/ScriniaCommands.Serve.cs` (local `scri serve`): unchanged. `WithTools<ScriniaMcpTools>`
  and the single local `.scrinia` store path are intact.
- `src/Scrinia.Mcp/Shared/SharedTools.cs`: still returns `Unsupported()` for all eleven tools. Handler
  wording/envelope construction is Phase 3/5 scope; Task 10 owns only adapter removal and disjointness,
  so no handler was implemented.
- The eleven tool names, Core ownership, forbidden surfaces, and error/envelope terms from Tasks 1-9.

## Verification

- Focused Task 10 gates: **2 passed, 0 failed** (`SharedNoAdapterTests`, `SharedLocalDisjointnessTests`).
- Adapter-consumption regression lane: `NoBaseStartupTests` + `McpCoreReferenceTests` = **13 passed, 0 failed**.
- Full shared suite: **213 passed, 0 failed** (211 pre-existing + 2 new).
- Solution build: **0 errors** (pre-existing analyzer warnings only).

## Concerns / follow-up

- The skip-on-failure guard in `OpenOpenableBases()` catches any exception from `Open` so a single bad
  base cannot halt startup; it is intentionally broad and documented. If a base fails to open in
  production, it is silent at startup and re-fails loudly at operation time (as `Unavailable` /
  `SHARED_BASE_STALE`), which is the designed fail-closed-per-operation behavior.
- `ScriniaCommands.SharedServe.cs` still discards the opened-runtime list; the returned `SharedBaseRuntime[]`
  is a startup validation today and will be consumed by the Phase 3 handlers once `SharedTools` stops
  returning `Unsupported()`.

## Evidence

```
$ dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj \
    --filter "FullyQualifiedName~SharedNoAdapterTests|FullyQualifiedName~SharedLocalDisjointnessTests"
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2

$ dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Shared"
Passed!  - Failed: 0, Passed: 213, Skipped: 0, Total: 213

$ dotnet build Scrinia.sln
0 Error(s)
```
