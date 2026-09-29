# Codebase Leanness Cleanup — Design

**Date:** 2026-06-18
**Status:** Approved (design); pending implementation plan
**Branch context:** work proceeds from `main` (currently at the per-op MCP auth fix)

## Goal

Verify the Scrinia codebase is lean and free of needless complexity, and correct
what is found. The audit (three parallel read-only explorations + direct
verification) concluded the codebase is **fundamentally healthy**: no dead types,
no `TODO`/`HACK`/`#if false`/`[Obsolete]` cruft, no commented-out code, MCP and
REST already share `MemoryOrchestrator`, and the `Nmp2Strategy` /
`Nmp2ChunkedEncoder` split is correct. The work below is a small, bounded set of
deletions and flattenings — not a refactor.

## Guiding principle

**Deletion and flattening only. No new abstractions, no new indirection.** Every
change either removes something unused or makes an existing file easier to read
**without changing behavior or public surface**. Suggestions that trade
duplication for indirection were explicitly rejected (see Out of Scope).

This reflects the project motto — *less chaos, more nature* — and YAGNI: an
abstraction earns its place only when a real second case exists.

## In scope

### 1. Remove stale `-WithLlm` path from `publish.ps1`

`publish.ps1` references `src/Scrinia.Plugin.Llm.Cli/Scrinia.Plugin.Llm.Cli.csproj`,
which does not exist. `publish.ps1 -WithLlm` therefore fails at runtime;
`publish.sh` has no equivalent flag.

- Remove the `[switch]$WithLlm` parameter (line 7).
- Remove `$LlmProject = '...'` (line 19).
- Remove the two `if ($WithLlm) { ... }` publish blocks (~lines 80–87 and ~118–122).

No project is rebuilt — the LLM CLI plugin was never built and is not planned.

### 2. Wire `tests/Scrinia.Benchmarks` into the solution

`tests/Scrinia.Benchmarks` (BenchmarkDotNet micro-benchmarks for `Bm25Scorer` and
`HnswIndex`) is not referenced in `Scrinia.sln` — it builds for no one and runs in
no CI. It is distinct from `benchmarks/Scrinia.Retrieval.Benchmarks` (the retrieval
eval harness, which *is* in the solution). Last touched in the v0.5.x release-prep
commit (2026-05-12); it is legitimate, just orphaned.

- Add the project to `Scrinia.sln`: the `Project(...) = "Scrinia.Benchmarks", ...`
  entry with a fresh project GUID, and the matching `GlobalSection`
  build-configuration rows (Debug/Release × Any CPU) so it builds with the solution.
- Verify it compiles against current `Scrinia.Core` (it may have drifted while
  out of the solution; fixing any compile drift is part of this item).

### 3. Delete the vestigial `IMemorySearcher` interface

`IMemorySearcher` is `internal`, has a single `internal sealed` implementation
(`WeightedFieldScorer`), is never swapped, and does not even capture the real
contract (the `SearchAll` overloads the store actually calls are not on the
interface). It is a premature seam.

- Delete the `internal interface IMemorySearcher { ... }` declaration.
- Change `internal sealed class WeightedFieldScorer : IMemorySearcher` to
  `internal sealed class WeightedFieldScorer`.
- Update the `<see cref="Search.IMemorySearcher"/>` doc-comment reference in
  `src/Scrinia.Core/Embeddings/TextChunker.cs` to point at `WeightedFieldScorer`
  (avoids a dangling cref / build warning).

### 4. Split `IMemorySearcher.cs` (766 lines) by content

The file is ~95% the `WeightedFieldScorer` implementation living under an
interface name. Split into focused files (namespace `Scrinia.Core.Search`
unchanged):

- `WeightedFieldScorer.cs` — the scorer class (post-#3, no interface).
- `SearchResults.cs` — the result records: `SearchResult`, `EntryResult`,
  `TopicResult`, `ChunkEntryResult`, `ScoredArtifact`, `TopicInfo`.
- The original `IMemorySearcher.cs` is removed (its remaining content moved out).

Pure mechanical move; no logic changes.

### 5. Split `ScriniaCommands.cs` (2267 lines) into partial files by command group

A genuine god-file holding ~18 command concerns. Flatten by splitting the single
class into **partial-class files grouped by command area** — same class name, same
ConsoleAppFramework registration, no DI or handler classes introduced.

- Provisional grouping (final boundaries from a method inventory at plan time):
  - `ScriniaCommands.Serve.cs` — MCP/stdio serve entrypoint.
  - `ScriniaCommands.Memory.cs` — list, search, show, store, append, forget.
  - `ScriniaCommands.Setup.cs` — config, setup, Ollama, model download, reindex.
  - `ScriniaCommands.Migration.cs` — migrate, consolidate.
  - `ScriniaCommands.Hooks.cs` — hook install/uninstall flows.
  - `ScriniaCommands.cs` — remaining shared/private helpers + guide/restore.
- Each file is `public sealed partial class ScriniaCommands`.
- No method signatures, attributes, or behavior change — only physical location.

### 6. Dedup the shared hook-installer JSON helpers

`ClaudeCodeHookInstaller` and `CodexHookInstaller` contain the same JSON helper
methods verbatim (`GetOrCreateObject`, `GetOrCreateArray`, `FindOrCreateManagedBlock`,
`IsScriniaBlock`, `WriteManagedBlock`).

- Extract them into one `internal static` helper class (e.g.
  `HookInstallerJson`) under `src/Scrinia/Commands/Hooks/`.
- Both installers call the shared helpers; the private duplicates are removed.
- Confirm byte-for-byte equivalence before extracting; if any helper differs
  between the two, parameterize the difference rather than forking — but do not
  introduce a base class. This is a flat dedup, not a new seam.

## Out of scope (deliberately rejected)

These were surfaced by the audit but **add indirection** and are explicitly not done:

- **Chat-provider base class** (`Anthropic`/`Gemini`/`OpenAi`) — the proposed
  shared streaming method takes async-enumerable-returning callbacks; the
  abstraction is harder to follow than the duplication it removes.
- **`FileMemoryStore` encoding-strategy extraction** (`IEncodingStrategy` /
  `IEncodingOrchestrator`) — speculative generality for a single encoder.
- **Command-handler classes + DI** for the CLI — partial files (item 5) achieve
  the readability win without the machinery.
- **Ephemeral 1-liner extension methods** — extracting trivial 1-line dictionary
  ops adds a file for negligible gain.
- **`archive/` directory** — already `.gitignored`, so not part of the committed
  codebase. At most an optional local `rm`; not part of this work or any commit.

## Risks / notes

- **`Scrinia.Benchmarks` compile drift:** being out of the solution, it may not
  compile against current `Scrinia.Core`. Item 2 includes fixing such drift; if
  the drift is large, surface it rather than silently rewriting benchmark logic.
- **Partial-class split correctness:** the only risk is a method landing in two
  files or none. The build (one class, all members must resolve) catches this.
- **Hook helper equivalence:** verify identical before merging; a subtle
  difference silently changing one installer's behavior is the only real hazard.
- **Public surface:** none of these items change public APIs, CLI flags (other
  than removing the already-broken `-WithLlm`), MCP tools, or REST endpoints.

## Verification

1. `dotnet build` of `Scrinia.sln` is clean (now including `Scrinia.Benchmarks`),
   zero new warnings (especially no dangling-cref warning from item 3).
2. Full test suite green: `Scrinia.Tests`, `Scrinia.Server.Tests`,
   `Scrinia.Merge.Tests`, `Scrinia.Plugin.Embeddings.Tests`,
   `Scrinia.Retrieval.Benchmarks.Tests`.
3. `Scrinia.Benchmarks` builds (and, if quick, `--list` of its benchmarks runs).
4. Spot-check that splits are behavior-preserving: no diff in any method body,
   only relocation; `git diff` for items 4–6 shows moves/extractions, not edits.
5. `publish.ps1` parses and runs without `-WithLlm`; `-WithLlm` is gone.

## Success criteria

- Solution builds clean with `Scrinia.Benchmarks` included; all tests green.
- `IMemorySearcher` interface gone; `IMemorySearcher.cs` replaced by
  `WeightedFieldScorer.cs` + `SearchResults.cs`.
- `ScriniaCommands.cs` is split into focused partials, none oversized.
- Hook JSON helpers exist in exactly one place.
- `publish.ps1` has no dead project reference.
- No public API, CLI (besides `-WithLlm`), MCP, or REST behavior changed.
