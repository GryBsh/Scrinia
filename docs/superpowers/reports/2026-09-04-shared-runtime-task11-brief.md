# Task 11 Brief: Add the Command, Prompt Isolation, and Complete Named Test Run

BASE: `ed0022f`

Scope is limited to Task 11 of `docs/superpowers/plans/2026-09-01-shared-runtime-and-mcp.md`. Tasks
1-10 already produced the Core shared contracts, the eleven-tool surface, selectors/provenance, the
privacy/no-base gates, the envelope serialization, the durable matrix, freshness, manifests, the
classifier/registry, and the removal of the temporary adapter with local/shared disjointness. Task
11 adds the two named gates that lock the final boundary contract — prompt isolation (§7.3) and the
categorical no-Git-process prohibition (§7.1) — and confirms the `scri shared serve` command is
fully wired with exactly eleven tools and no prompt transport.

## What the named tests assert

- `SharedNoPromptCorrelationTests.Shared_responses_are_independent_of_initiating_prompt_or_correlation`
  reflects over the eleven `SharedTools` handlers and every `SharedServer` method and asserts that
  none of them carries a `prompt`, `correlation`, `session`, or otherwise correlation/session
  identifier parameter. It also asserts the operation-based `SharedServer.GuidanceForMutation` is a
  pure function of its operation inputs (identical manifest yields identical guidance) and that the
  guidance text never references a prompt.
- `SharedNoGitProcessTests.Shared_surface_never_starts_a_git_process` reads the five source files that
  make up the shared call graph (`SharedTools.cs`, `SharedServer.cs`, `SharedGitGuidance.cs`,
  `ScriniaCommands.SharedServe.cs`, `SharedServeHostAdapter.cs`) and asserts none of them contains a
  `Process.Start`, `new Process`, `ProcessStartInfo`, or `System.Diagnostics.Process` token. It then
  asserts `SharedGitGuidance.Render` is print-only: it returns a string and takes exactly the two
  operation inputs `registeredroot` and `manifest` — no process, no prompt.

## Outcome

The shared surface already satisfied both contracts from Tasks 1-10, so the two named tests are green
on first run: 215 shared tests pass, and the full non-benchmark suite passes (2564 in
`Scrinia.Tests`, 272 in `Scrinia.Retrieval.Benchmarks.Tests`, 32 in the embeddings tests, zero
failures). No process-launch or prompt-transport residue existed to remove; the implementation is the
`scri shared serve` command registered in `Program.cs` via `app.Add<SharedServeCommands>("shared")`,
which registers exactly the eleven `shared_*` tools through `WithTools<SharedTools>` and fails before
transport with `NO_SHARED_BASES_ENABLED` when no base is enabled.

## Modification (contract lock, not behavior change)

Both named tests gate a contract that Phase 3/5 will later back with real handler bodies, so the two
`Modify` targets received class/method XML-doc locks stating the guarantees:

- `src/Scrinia.Mcp/Shared/SharedTools.cs` — a class-level summary documenting that the eleven tools
  carry no prompt/correlation/session field, that responses are derived from operation inputs only,
  that the surface launches no process, and that the stubbed handlers must preserve both guards when
  Phase 3/5 implements them.
- `src/Scrinia/Commands/ScriniaCommands.SharedServe.cs` — a `Serve` summary documenting the exact
  eleven-tool registration, the no-base startup gate, and the prompt-isolated/process-free audit that
  the two named tests enforce.

## Verification

- `dotnet build Scrinia.sln` — 0 errors.
- `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter ~Shared` — 215 passed.
- `dotnet test Scrinia.sln` (benchmarks excluded) — 0 failed across all test projects.

## Concerns

- The named tests are static/reflection guards, not end-to-end runs of `scri shared serve` over the
  stdio transport; they verify the surface contract rather than a live server start.
- The two pre-existing uncommitted Task 8 ledger/report docs in the worktree are unrelated to Task 11
  and were left untouched. No commit was made (leaf-agent boundary); exact paths are reported below.

## Exact paths

- Modify: `src/Scrinia.Mcp/Shared/SharedTools.cs` (class-level contract lock)
- Modify: `src/Scrinia/Commands/ScriniaCommands.SharedServe.cs` (Serve contract lock)
- Create: `tests/Scrinia.Tests/Shared/SharedNoPromptCorrelationTests.cs`
- Create: `tests/Scrinia.Tests/Shared/SharedNoGitProcessTests.cs`
