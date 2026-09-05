# Task 11 Review: Shared Surface Prompt Isolation and No-Git-Process

**Scope check — Task 11 only, no later handler scope.**
Confirmed against `docs/superpowers/plans/2026-09-01-shared-runtime-and-mcp.md`. Task 11 gates
"Shared MCP inputs contain no prompt or correlation field; operation-based guidance is independent
of prompt text" and "No Git process" (coverage matrix). No Phase 4 wording, no Task 12 documentation
sections, and no handler-scope wording were touched. The two `Modify` targets received only
documentation locks; handler bodies remain the pre-existing `Unsupported()` stubs owned by Phase 3/5.

**Exact rules followed.**
1. Shared MCP inputs contain no prompt/correlation field — verified by reflection in the named test;
   the eleven `shared_*` handlers take only operation arguments. The `shared_search` `context`
   parameter is a legitimate search input, not a prompt, and is correctly not forbidden.
2. Operation-based guidance is independent of prompt text — `GuidanceForMutation`/`Render` depend on
   `(registeredRoot, manifest)` only.
3. `scri shared serve` implemented — registered in `Program.cs` (`app.Add<SharedServeCommands>("shared")`),
   fails before transport on no enabled base.
4. Exactly eleven tools registered — `WithTools<SharedTools>`; both the pre-existing
   `SharedToolCountTests` and the new reflection assertion require eleven.
5. All prompt transport removed from shared handlers — handlers are stubs with no prompt transport;
   the lock now forbids introducing it in Phase 3/5.
6. Complete shared call graph audited for process launches — `SharedNoGitProcessTests` scans all five
   shared surface source files; zero process APIs. The executable's local-serve Git merge-driver
   references in `ScriniaCommands.Setup.cs` are outside the shared surface.
7. No Git process starts — print-only guidance confirmed.

**TDD.** Both named tests were written first and compile-clean; they pass against the current surface,
which already satisfied the contract from Tasks 1-10 (retrofit guards, not a red-then-green change
of behavior). No behavior was altered to make them pass.

**Philosophy (code-philosophy: 5 Laws).**
- Early Exit — the no-base startup gate remains fail-before-transport.
- Parse Don't Validate — shared address validation (ephemeral refusal, canonical names) is untouched.
- Atomic Predictability — `GuidanceForMutation` is a pure function of operation inputs.
- Fail Fast — `NO_SHARED_BASES_ENABLED` before transport is preserved.
- Intentional Naming — the two test method names match the plan's required names exactly.

**Verification.**
- `dotnet build Scrinia.sln` — 0 errors.
- `dotnet test … --filter ~Shared` — 215 passed (includes both new named tests).
- `dotnet test Scrinia.sln` — 0 failed (Scrinia.Tests 2564, Retrieval.Benchmarks 272, Embeddings 32).

**Concerns.**
- The named tests are static/reflection/source guards, not a live stdio server start; they assert the
  surface contract rather than a running server.
- The worktree already carried two uncommitted Task 8 ledger/report docs; left untouched and not part
  of this commit scope. No commit was made; exact paths are reported in the brief.
