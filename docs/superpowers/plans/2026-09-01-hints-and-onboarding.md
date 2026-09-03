# Phase 4 — Hooks and Prompt-Hint Advisory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the fail-silent `scri hint` commit/push advisory, update supported hook and agent artifacts, and replace the retired shared onboarding without changing Phase 3's final response contract.

**Architecture:** A pure `PromptHintTransport` selects a positional prompt or performs one bounded stdin transport, then parses and classifies it deterministically. `scri hint` gates output on a supported hook client, emits reminders in commit-then-push order, and returns exit code 0 on every path. OpenCode, the generated agent primer, and the explicitly owned onboarding sections all point to this hook-only advisory.

**Tech Stack:** .NET 10 (`net10.0`), xUnit, FluentAssertions, `dotnet test`, `dotnet build`. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md` §7.3, §9, §13, §13.1, §14.8, §14.10; `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md` R1-03, R3-01, R3-05, R4-04, R5-01.

## Global Constraints

- Phase 4 depends on Phase 3's final shared response contract and does not edit `McpResponse`, `ErrorCodes`, the eleven tool schemas, YAML envelopes, or §7.2 mutation-manifest guidance.
- Phase 4 owns only prompt-hint transport, supported hooks, agent artifacts, and shared onboarding/multi-user replacement; it does not alter shared response wording.
- Positional input wins. If absent, stdin is read until EOF or the 65,537-byte bound is reached; the accepted maximum is 65,536 bytes.
- Stdin uses strict UTF-8, a two-second deadline, one BOM strip, and Unicode whitespace trim. Read failure, timeout, invalid UTF-8, or oversize input emits no output and exits 0.
- If the first trimmed character is `{`, parse exactly one JSON object with trailing whitespace only, allow extra properties, and require the exact case-sensitive string property `prompt`; malformed, nonobject, missing, or nonstring input emits no output and never falls back to raw text.
- Otherwise use trimmed raw text. Normalize NFKC, tokenize maximal Unicode letter/digit runs, and match exactly `commit`, `commits`, `committed`, `committing`, `push`, `pushes`, `pushed`, and `pushing`. Negation does not suppress either reminder.
- Unsupported clients and clients without a hook emit nothing. Scrinia never launches Git and advisory text contains no base, root, or Git-state claim.
- The OpenCode hook changes `save` to `hint`; the shared MCP surface has no prompt or correlation field.
- Durable text is UTF-8 without BOM with LF endings. Never edit dated plans/specs/reviews or released changelog history; never stage `cmds.txt`.

---

## Files and Ownership

### Modified

- `src/Scrinia/Commands/ScriniaCommands.HintCommand.cs` — advisory branch only; preserve relevance `Hint()` and `ExtractPromptFromStdin`.
- `src/Scrinia/Commands/Hint.cs` — `IsSupportedAdvisoryClient` and exact reminder constants.
- `src/Scrinia.Mcp/plugins/opencode/scrinia.ts` — `scri hint --client opencode` in `transform`.
- `src/Scrinia.Mcp/ScriniaPrimer.cs` — `MarkdownBody` onboarding only; `McpInstructions` byte-for-byte unchanged.
- `docs/multi-user-setup.md` — whole-file shared-base collaboration replacement.
- `README.md` — Phase 4 shared-onboarding heading only.
- `docs/getting-started.md` — Phase 4 shared-onboarding heading only.
- `docs/security.md` — Phase 4 shared-threat heading only.
- `src/Scrinia.Mcp/prompts/guide.md` — Phase 4 shared-onboarding heading only.

### Added

- `src/Scrinia/Commands/PromptHintTransport.cs`
- `src/Scrinia/Commands/IBoundedStdinReader.cs`
- `tests/Scrinia.Tests/PromptHintTransportTests.cs`
- `tests/Scrinia.Tests/HintAdvisoryTransportTests.cs`
- `tests/Scrinia.Tests/PluginOnboardingTemplateTests.cs`
- `tests/Scrinia.Tests/HookAdvisoryOnboardingTests.cs`
- `tests/Scrinia.Tests/OnboardingOwnershipTests.cs`

### Explicitly not owned

- `src/Scrinia.Mcp/McpResponse.cs`, `ErrorCodes`, tool schemas, and shared response text — Phase 3.
- Phase 5's installation, global-tool, plugin, model, setup, and plugin-diagnostic headings in the named living docs.
- Phase 1 merge/reconcile removal, merge skills, and the manual Git conflict-cleanup heading.
- `docs/superpowers/**`, `docs/reviews/**`, and released `CHANGELOG.md`.

## Interfaces

### Consumed

- `HookClient` (`Default`, `Codex`, `ClaudeCode`, `OpenCode`, `Copilot`) and `HintCommand.ParseClient`.
- `AgentHookSetup.DefaultHookSpecs` / `StampClient` and `IAgentArtifactInstaller`.
- Phase 3's final `McpResponse` and `ScriniaPrimer.McpInstructions`, unchanged.

### Produced

- `public sealed record PromptTransportResult(bool Present, string? Prompt, bool CommitIntent, bool PushIntent)` with `public static PromptTransportResult Silent`.
- `public static string? PromptHintTransport.SelectPrompt(string? positionalPrompt, string? stdinRaw)`.
- `public static PromptTransportResult PromptHintTransport.Classify(string? prompt)`.
- `public static PromptTransportResult PromptHintTransport.Parse(string? positionalPrompt, string? stdinRaw)`.
- `public interface IBoundedStdinReader { Task<string?> ReadAsync(CancellationToken cancellationToken); }`.
- `public static bool HintCommand.IsSupportedAdvisoryClient(HookClient client)`.
- `public const string HintAdvisory.CommitReminder` and `public const string HintAdvisory.PushReminder`.

## Test Plan and Implementation Steps

### Task 1: Exact pure parser

**Files:** Create `src/Scrinia/Commands/PromptHintTransport.cs`; test `tests/Scrinia.Tests/PromptHintTransportTests.cs`.

**Interfaces:** Produces the three exact parser signatures and `PromptTransportResult` above. Use `Regex Token = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled)` and the eight exact tokens.

- [ ] **Step 1: Write failing tests** for positional-wins, raw input, one BOM, Unicode trim, empty input, exact JSON with trailing whitespace, extra properties, malformed JSON, nonobject JSON, missing/nonstring/case-mismatched `prompt`, trailing non-whitespace, NFKC fullwidth, punctuation boundaries, all eight inflections, case-insensitivity, negation, near misses (`commence`, `pushpin`, `recommit`, `committees`), both intents, and commit-before-push intent flags.
- [ ] **Step 2: Run** `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~PromptHintTransportTests`; **Expected:** FAIL because the transport type is absent.
- [ ] **Step 3: Implement** trim/BOM/empty handling; first-character `{` exact-object parsing with `JsonDocument` and required case-sensitive string `prompt`; raw otherwise; NFKC plus maximal letter/digit tokenization and exact token membership.
- [ ] **Step 4: Run** the same filter; **Expected:** PASS for every parser boundary.
- [ ] **Step 5: Commit** `src/Scrinia/Commands/PromptHintTransport.cs` and its test with `feat(hint): add exact prompt transport parser`.

### Task 2: Bounded stdin transport

**Files:** Create `src/Scrinia/Commands/IBoundedStdinReader.cs`; test `tests/Scrinia.Tests/HintAdvisoryTransportTests.cs`.

**Interfaces:** `ConsoleStdinReader` implements `ReadAsync(CancellationToken)`; it reads repeatedly until EOF or 65,537 bytes, rejects over 65,536, decodes with `new UTF8Encoding(false, true)`, and applies a linked two-second cancellation deadline. `null` represents oversize, invalid UTF-8, timeout, or read failure.

- [ ] **Step 1: Write failing transport tests** using an injectable reader for exactly 65,536 accepted bytes, 65,537 rejected bytes, invalid UTF-8, a delayed two-second timeout, `IOException`, repeated partial reads until EOF, and positional input proving the reader is never called.
- [ ] **Step 2: Run** `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~HintAdvisoryTransportTests`; **Expected:** FAIL because the reader contract is absent.
- [ ] **Step 3: Implement** repeated reads with a hard 65,537-byte ceiling, strict UTF-8 decoding only after EOF/limit, linked cancellation with `TimeSpan.FromSeconds(2)`, and fail-silent exception handling.
- [ ] **Step 4: Run** the same filter; **Expected:** PASS, including read-until-EOF and timeout boundaries.
- [ ] **Step 5: Commit** the reader and tests with `feat(hint): add bounded fail-silent stdin transport`.

### Task 3: Advisory command and hook gate

**Files:** Modify `src/Scrinia/Commands/ScriniaCommands.HintCommand.cs` and `src/Scrinia/Commands/Hint.cs`; test `tests/Scrinia.Tests/HintAdvisoryTransportTests.cs`.

**Interfaces:** Consume `PromptHintTransport`, `IBoundedStdinReader`, and `HookClient`; produce `IsSupportedAdvisoryClient`, `HintAdvisory.CommitReminder`, and `HintAdvisory.PushReminder`.

- [ ] **Step 1: Write failing tests** for `Default` and unknown clients, supported commit, supported push, both reminders in commit-then-push order, no intent, malformed input, timeout/read error, and unconditional exit 0. Assert reminders contain no base/root/Git-state claim.
- [ ] **Step 2: Run** the advisory test filter; **Expected:** FAIL.
- [ ] **Step 3: Implement** an advisory-only branch that parses positional input or the injected bounded reader, gates on `IsSupportedAdvisoryClient(ParseClient(client))`, emits only the exact applicable constants in order, and returns 0 on every path. Do not alter relevance hint computation or Phase 3 text.
- [ ] **Step 4: Run** the filter; **Expected:** PASS.
- [ ] **Step 5: Commit** with `feat(hint): emit fail-silent commit and push advisories`.

### Task 4: OpenCode save-to-hint

**Files:** Modify `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`; test `tests/Scrinia.Tests/PluginOnboardingTemplateTests.cs`.

- [ ] **Step 1:** Add a failing assertion that `transform` invokes `scri hint --client opencode`, sends the prompt through stdin, and contains no `scri save`.
- [ ] **Step 2:** Run the focused test; **Expected:** FAIL.
- [ ] **Step 3:** Replace the save invocation and remove the obsolete alternate spawn block.
- [ ] **Step 4:** Run the focused test; **Expected:** PASS.
- [ ] **Step 5:** Commit with `feat(hint): route OpenCode transform through hint`.

### Task 5: Agent artifacts and disjoint onboarding

**Files:** Modify `src/Scrinia.Mcp/ScriniaPrimer.cs`, `docs/multi-user-setup.md`, `README.md`, `docs/getting-started.md`, `docs/security.md`, `src/Scrinia.Mcp/prompts/guide.md`; tests `HookAdvisoryOnboardingTests.cs` and `OnboardingOwnershipTests.cs`.

- [ ] **Step 1: Write failing tests** for the primer's `scri hint --client <host>` hook-only wording, unchanged `McpInstructions`, `UserPromptSubmit` ending in `hint`, shared-base collaboration, fixed shared `project-git`, and each doc's explicit disjoint Phase 4/Phase 5 heading ownership.
- [ ] **Step 2: Run** both focused filters; **Expected:** FAIL.
- [ ] **Step 3: Update** only the owned headings. Replace `docs/multi-user-setup.md` with shared-base guidance; remove `scri-merge`, `reconcile`, and `setup --multi-user` workflow claims. Preserve Phase 3's exact staging command `git -C <root> add -A --` with individually quoted manifest paths. Leave Phase 5 headings untouched.
- [ ] **Step 4: Run** both filters plus assertions that shared MCP has no prompt/correlation field, no Phase 4 Git process exists, and dated-history hashes are unchanged; **Expected:** PASS.
- [ ] **Step 5: Commit** explicit paths with `docs: add Phase 4 hint and shared-base onboarding`.

### Task 6: Independent Phase 4 gate

- [ ] **Step 1:** Run the parser, transport, template, artifact, and ownership filters independently; **Expected:** all PASS.
- [ ] **Step 2:** Run `dotnet build Scrinia.sln`; **Expected:** clean.
- [ ] **Step 3:** Run `dotnet test Scrinia.sln`; **Expected:** all non-benchmark tests PASS.
- [ ] **Step 4:** Verify no `Process.Start`, `System.Diagnostics.Process`, or `child_process` spawn was introduced and no dated artifact changed.
- [ ] **Step 5:** Commit any remaining Phase 4 files by explicit path; do not push.

## Exit Gate

Phase 4 is complete only when each independent gate passes: pure parser boundaries; repeated bounded stdin with strict UTF-8 and two-second deadline; fail-silent advisory and client gate; OpenCode `hint`; hook/primer artifacts; disjoint onboarding ownership; Phase 3 contract and §7.2 unchanged; no Git process; full build/test; and dated-history hashes unchanged.

## Coverage Matrix

| Citation | Requirement | Task |
|---|---|---|
| §7.3, R1-03 | Exact prompt transport, byte bound, timeout, JSON, normalization, tokens, silence | 1–3 |
| §7.3 | OpenCode `save`→`hint`, hook-only advisory | 4 |
| §13, R3-01 | Phase 3 final response contract immutable; Phase 4 scope exclusive | 3, 5, 6 |
| §13.1, R4-04 | Disjoint living-document ownership | 5 |
| §14.8, §14.10, R3-05 | Concrete parser/transport tests and ownership gate | 1–6 |
| R5-01 | Exact mutation staging guidance preserved | 5 |

## Citations

- `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md` R1-03, R3-01, R3-05, R4-04, R5-01.
- `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md` §7.3, §9, §13, §13.1, §14.8, §14.10.
