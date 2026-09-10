# Phase B Umbrella Roadmap and Specification

**Status:** Approved roadmap; Phase B open
**Date:** 2026-09-10
**Predecessor:** `docs/superpowers/specs/2026-09-08-append-hardening-design.md`
**Scope:** Roadmap-level decomposition only; implementation requires a separate approved specification and plan for each subproject.

## 1. Authority and phase status

Phase A is complete. Its approved append-hardening design was implemented, reviewed, and
verified against the reconciled final package. The authoritative ordinary solution test
transcript records exit 0, 3,205 passed, 19 skipped, 0 failed, 3,224 total, with no hang,
crash, or aborted run. Phase A was committed as `0142b36 fix: complete phase a remediation`;
the subsequent cleanup commit was `a051387 chore: remove accidental evidence artifact`.

Phase B is open. It is not a continuation of unfinished Phase A work and must not be used to
silently revise the Phase A contract. This document is the umbrella roadmap for the explicitly
deferred work. It does not authorize implementation by itself: every B1–B6 change requires an
approved child specification and implementation plan, with the gates in this document intact.

## 2. Goal and non-goals

### Goal

Extend the integrity guarantees established for append to the remaining write, recovery,
archive, and operational boundaries without breaking existing memories, callers, package
surfaces, or local-only deployment. The outcome should be a deliberately layered transaction
model: common primitives first, single-memory operations next, coordinated recovery after that,
then cross-memory and external-system guarantees, and format evolution last.

### Non-goals

- Do not reopen or broaden Phase A's append contract merely for consistency.
- Do not change NMP/2, sidecar schemas, archive naming, or migration behavior before B6 has an
  approved compatibility design.
- Do not restore an HTTP/REST server, remote CLI, web UI, Aspire host, or API-key surface.
- Do not invoke Git or inspect a Git index as part of shared-base behavior.
- Do not make external plugins transactional by assertion; their boundary must be specified and
  tested before any guarantee is advertised.
- Do not combine unrelated cleanup, refactoring, benchmark work, or historical-document edits
  with a child project.

## 3. Deferred areas carried forward from the append specification

The following seven areas are the exact Phase B deferral and remain the scope boundary:

1. Apply the append transaction protocol to `remember`, `compact`, import, migration, bundle
   writes, or arbitrary sidecar mutations.
2. Unify append and restore journals into a shared transaction format.
3. Support cross-memory or multi-scope transactions.
4. Define transactional guarantees for external plugin/event systems.
5. Provide a general archive/pruning service.
6. Provide background journal garbage collection and fleet-wide recovery reporting.
7. Change NMP/2 or sidecar formats as required to support richer transaction metadata.

The first six are implementation work. The seventh is deliberately last because it is a format
and compatibility decision, not a prerequisite to proving stronger behavior with the current
format.

## 4. B0–B6 subproject boundaries

### B0 — Transaction foundation and compatibility harness

**Boundary:** Define reusable transaction records, journal interfaces, raw-member evidence,
state-transition rules, durable replacement seams, recovery result vocabulary, operation
correlation, and deterministic fault-injection/test fixtures. B0 may add internal abstractions
and test infrastructure, but it must not change the behavior of `remember`, `compact`, import,
migration, bundle writes, sidecar mutation, or append.

**Deliverable:** A documented, versioned foundation that can represent the existing append and
restore evidence without forcing either journal to migrate yet; a compatibility matrix and
golden fixtures for current artifacts, sidecars, archives, ephemeral memories, and public
facades.

**Required decisions:** Choose the common transaction vocabulary and state ownership; decide
whether raw bytes remain the universal recovery currency; define journal versioning and unknown
state handling; define how operation IDs, recovery evidence, and warnings cross Core, MCP, and
CLI; and define the fault-seam contract.

### B1 — Single-memory write coverage

**Boundary:** Apply the approved transaction discipline to `remember`, `compact`, import of one
memory, migration of one memory, bundle writes that publish one memory, and arbitrary sidecar
mutations. Each operation must retain its existing semantic contract while gaining explicit
commit, rollback, recovery, and warning behavior.

**Exclusions:** No cross-memory atomicity, shared journal migration, external plugin guarantee,
or NMP/2/sidecar schema change.

**Required decisions:** Decide the operation-specific commit boundary; whether import and bundle
publication use staging plus per-entry commit or one entry transaction; how compact archives
and prunes; how sidecar-only changes protect artifact/sidecar pairing; which failures are typed
warnings versus recovery-required outcomes; and how existing migration states and legacy data
are preserved.

### B2 — Append/restore journal convergence

**Boundary:** Replace the two independent journal models with the B0 shared transaction format,
or establish a rigorously versioned adapter if physical convergence is unsafe. B2 covers startup
discovery, parsing, recovery, blocking, cleanup, archival evidence, and retry semantics for
append and restore journals.

**Exclusions:** No multi-memory transaction and no format change to NMP/2 or ordinary sidecars.

**Required decisions:** Decide whether journals share one file, one directory family, or a
common envelope with operation-specific bodies; define precedence when append and restore
evidence coexist; define mixed-version recovery; decide retention and operator-resolution
rules; and specify whether a committed journal can be compacted or garbage-collected by B5.

### B3 — Cross-memory and multi-scope coordination

**Boundary:** Add atomicity and recovery for operations that intentionally touch more than one
memory or scope, consuming the single-memory guarantees from B1 and the converged journal
contract from B2. Define participant admission, prepare/commit, durable decision records,
partial-participant recovery, scope locking, and read visibility.

**Required decisions:** Define the supported participant set and maximum; choose coordinator
placement and lifetime; decide whether cross-scope operations are local-process only or durable
across restart; define deadlock ordering and lock failure behavior; define isolation and read
visibility; and decide whether a failed participant can be compensated or only requires
operator recovery.

### B4 — External plugin and event transaction boundary

**Boundary:** Define what, if anything, can be guaranteed when plugin hooks, embedding/index
providers, event sinks, or other external processes observe or participate in a transaction.
B4 consumes the stable single-memory and coordination contracts from B1–B3.

**Required decisions:** Decide which integrations are observers, participants, or explicitly
non-transactional; define idempotency keys and replay behavior; define before/after ordering and
failure mapping; decide whether an external participant may veto commit; define timeout,
cancellation, retry, and quarantine behavior; and define what public guarantees are safe when a
plugin is unavailable or malicious.

### B5 — Archive/pruning and recovery operations

**Boundary:** Build the general archive/pruning service and background journal garbage
collection/recovery reporting. It consumes the transaction records and participant outcomes
from B1–B3; it may consume B4's observability contract but must not make external plugins a
prerequisite for local cleanup.

**Required decisions:** Define retention policy, archive eligibility, and proof required before
deletion; decide how current state, rollback evidence, unresolved journals, and sidecars are
protected; define scheduling, concurrency, backpressure, and cancellation; define local versus
fleet-wide reporting without leaking memory content; and decide operator actions for stuck or
ambiguous recovery.

### B6 — NMP/2 and sidecar metadata evolution

**Boundary:** Only after B0–B5 are accepted, decide whether richer transaction metadata requires
an NMP/2 revision, sidecar schema revision, or both. Specify encoding, decoding, negotiation,
forward/backward compatibility, migration, rollback, package behavior, and mixed-version
operation.

**Required decisions:** Decide whether metadata belongs in NMP/2, the sidecar, journals, or a
new companion artifact; assign format/version identifiers; define old-reader behavior; define
lossless migration and downgrade behavior; define malformed/unknown-version handling; and decide
whether a format change is necessary at all. B6 must not be used to justify an earlier format
break.

## 5. Dependencies and sequencing

The required dependency graph is:

```text
                         ┌── B1 ──┐
B0 ───────────────────────┤        ├── B3 ──┬── B4 ──┐
                         └── B2 ──┘        └── B5 ──┴── B6
```

- B1 and B2 both follow B0. They may proceed in parallel only after B0's contract and fixtures
  are accepted; neither may redefine B0 independently.
- B3 consumes B1 and B2 and cannot begin implementation before both child acceptance gates pass.
- B4 and B5 consume B1–B3. Their specifications may be drafted in parallel, but integration
  must not claim completion until B3 is accepted.
- B6 is last. It consumes the accepted behavior and evidence from B0–B5 and is the only
  subproject permitted to propose NMP/2 or sidecar format changes.
- Shared-base integration is separately gated. It is not a hidden dependency, completion
  criterion, or implementation lane inside B0–B6. Any shared-base work must retain the fixed
  `project-git` policy, local-only stdio surface, no-Git boundary, privacy rules, and its own
  design/review/verification gate.

## 6. Current code ownership and call-site map

The following map is the starting ownership record for child specifications. Each child must
refresh it against its chosen base and cite exact symbols/lines in its own plan; a stale map is a
planning blocker.

| Concern | Current owner | Relevant callers or surfaces |
|---|---|---|
| Store contract and filesystem writes | `src/Scrinia.Core/IMemoryStore.cs`, `FileMemoryStore.cs` | CLI commands, MCP tools, lifecycle/bundle paths, tests through `TestHelpers.StoreScope` |
| Append transaction, journal, recovery | `src/Scrinia.Core/AppendTransaction.cs`, `AppendJournal.cs`, `FileMemoryStore.cs` | `MemoryTools.Core.cs`, `ScriniaCommands.Memory.cs`, store initialization/recovery, append recovery tests |
| Restore journal and migration | `src/Scrinia.Core/RestoreJournal.cs`, `StoreLayoutMigration.cs` | `MemoryTools.Lifecycle.cs`, bundle import/lifecycle code, migration commands |
| Artifact encoding and format | `src/Scrinia.Core/Encoding/`, `NMP_SPEC.md`, `Formats/` | `FileMemoryStore`, bundle services, package and format tests |
| Sidecar derivation and persistence | `Search/Attribute*`, `MemoryDerivation`-related store paths, MCP sidecar helpers | `MemoryTools.Core.cs`, `MemoryTools.Skills.cs`, agent/skill writes, import and migration |
| Search/index/facts consequences | `Search/`, `EntityFactStore.cs`, `MemoryVectorIndexer` and related embedding code | `FileMemoryStore` index publication, search/facts tools, maintenance/event sinks |
| Archive and pruning | `FileMemoryStore` archive/version helpers and append archive plan | append, compact, restore/import cleanup, archive tests |
| Public response mapping | `src/Scrinia.Mcp/McpResponse.cs`, `MemoryTools*.cs` | MCP tool registration and response renderers |
| CLI facade | `src/Scrinia/Commands/ScriniaCommands*.cs` | `scri` memory, lifecycle, bundle, migration, tracking, and serve commands |
| Local server/bootstrap | `src/Scrinia/Program.cs`, `Commands/WorkspaceSetup.cs`, `Commands/*Serve.cs` | local stdio MCP process, workspace/store/plugin setup |
| Plugins and event hooks | `src/Scrinia.Plugin.Abstractions/`, `src/Scrinia.Plugin.Embeddings/`, `src/Scrinia/Services/` | Core event pipeline, embedding child process, plugin tests |
| JSON serialization | `ScriniaMcpJsonContext`, `CliJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext` | all trimmed/AOT-sensitive response, config, sidecar, and plugin paths |

No child may introduce a second facade transaction, duplicate derivation, or raw reflection JSON
serialization. `WithTools<T>()` exposes every public method on a tool type, so helper APIs remain
internal/private unless intentionally part of the MCP contract.

## 7. Compatibility constraints

- **NMP/2:** Preserve `nmp/2`, current encoding/decoding, headers, chunk semantics, and artifact
  readability through B5. Format proposals belong only to B6 and require migration evidence.
- **Sidecars and migrations:** Preserve current sidecar fields, legacy `name`/`uri` handling,
  forward-fill and `fields`→`attributes` migration, skill/agent sidecar lookup, and malformed
  sidecar safety. Use `ReadSidecarMeta`/`WriteSidecarMeta` and source-generated contexts.
- **Archive layout:** Preserve existing version directories, names, collision rules, artifact/
  sidecar pairing, and retention semantics until a child specification explicitly supersedes
  them with compatibility tests. Journals must remain distinguishable from restore evidence.
- **Ephemeral memories:** Keep `~name` and `/temp/name` in-process and outside persistent
  archive/journal guarantees. The persistent-only CLI continues to reject ephemeral addresses;
  the long-lived local stdio MCP process remains the ephemeral surface.
- **CLI/MCP:** Facades translate one Core result contract; neither derives or sequences writes
  independently. Public error codes, operation IDs, warnings, recovery details, and content
  privacy remain stable unless a child spec records an approved change.
- **Source-generated JSON:** Register every new serialized type in the appropriate JSON context;
  reflection-based `JsonSerializer` use is incompatible with trimming and is prohibited.
- **Deployment surface:** `scri serve` over local stdio is the only server surface. Do not add
  HTTP/REST, remote CLI, API-key, web, or Aspire guidance.
- **Shared privacy and no Git:** Shared-base integration is filesystem-only, fixed to
  `project-git`, reports `UNVERIFIED` index state, never starts or inspects Git, and does not
  disclose disabled registry entries except through explicit `shared base list --all`.
- **Historical docs:** `docs/planning/`, `docs/superpowers/`, dated reviews, and released
  changelog entries are historical records. Do not rewrite them to describe later behavior.
  Update living reference docs only when a child actually changes the current surface.

## 8. Acceptance gates

Every child project must pass all applicable gates before its dependents start implementation:

1. An approved child specification names goals, non-goals, invariants, ownership, compatibility
   impact, public result mapping, migration/recovery behavior, tests, and explicit decisions.
2. A scoped implementation plan identifies exact files, call sites, test lanes, and exclusions.
3. Focused tests pass, including deterministic failure, cancellation, restart/recovery, malformed
   input, privacy, and compatibility cases relevant to the child.
4. `dotnet build Scrinia.sln` passes with zero errors and zero warnings attributable to the
   child.
5. The ordinary full solution command, `dotnet test Scrinia.sln`, exits 0 with zero failures;
   no test-host hang, crash, abort, or timeout is accepted. The final run must record pass,
   skipped, failed, total, exit status, and elapsed/no-hang evidence.
6. Relevant package, trimming/source-generation, CLI/MCP contract, and historical-doc checks
   pass when the child touches those surfaces.
7. A scoped diff check passes, and the child commit contains no unrelated source, test, evidence,
   generated, or historical-document changes.
8. An independent review records APPROVE (or a resolved REQUEST_CHANGES cycle) against the exact
   child diff and verification transcript.

## 9. Evidence, ledger, and review protocol

Each child owns a dated evidence directory under `.superpowers/sdd/` and a status ledger. The
ledger must map every requirement and acceptance row to implementation paths, tests, evidence,
review disposition, and commit. Evidence must distinguish authoritative raw transcripts from
derived summaries and preserve hashes when copied.

The protocol is:

1. Record the base commit and clean/scope inventory before implementation.
2. Maintain one row per requirement, decision, failure mode, and compatibility constraint.
3. Capture focused red/green evidence and the ordinary full-suite verification separately.
4. Re-run the full solution test after the last code or test change; a stale transcript is not
   evidence. Investigate any hang or abort rather than accepting a partial run.
5. Have a reviewer inspect the exact diff, call-site map, ledger, raw transcript, and generated
   package/format contents where applicable.
6. Reconcile the ledger and path dispositions before declaring the child complete. Do not count
   evidence-only files as implementation, and do not hide unrelated user changes.
7. Record the child commit and the dependency gate it unlocks. A child is not complete merely
   because focused tests pass.

## 10. When to split a subproject into its own spec and plan

Split B0–B6 into a separate child specification and plan whenever any one of these is true:

- it changes a public Core, CLI, MCP, plugin, format, or shared-base contract;
- it introduces a new durable file, journal state, migration, archive rule, or recovery outcome;
- it crosses a project, process, scope, or lock boundary;
- it requires a distinct compatibility matrix or source-generated JSON registration;
- it needs a separate fault model, restart test, no-hang test, or privacy review;
- it can be reviewed, committed, and reverted independently; or
- its implementation would make another subproject's acceptance evidence ambiguous.

Do not split merely to evade a dependency gate. A split must retain one owning ledger, explicit
interfaces between child specs, and a parent/child traceability link.

## 11. Phase B blocker checklist

Phase B implementation is blocked until the following are checked:

- [ ] Phase A completion package remains authoritative and Phase A is recorded as complete.
- [ ] This umbrella roadmap is approved and committed without source/test scope creep.
- [ ] B0 child specification defines the common vocabulary, seams, fixtures, and compatibility
      harness.
- [ ] B0 acceptance gate passes the full solution build and ordinary full test with no hang.
- [ ] B1 and B2 specifications identify exact current call sites and do not duplicate facade
      sequencing.
- [ ] B1 and B2 both pass before B3 implementation begins.
- [ ] B3 specifies lock ordering, participant recovery, visibility, and restart behavior.
- [ ] B4 explicitly limits external plugin guarantees and defines replay/idempotency behavior.
- [ ] B5 protects current state, sidecars, archives, and unresolved evidence from cleanup.
- [ ] B6 is not started until B0–B5 are accepted and a format change is shown necessary.
- [ ] Shared-base integration has its own approval, no-Git/privacy review, and verification gate.
- [ ] Every child has a reconciled ledger, authoritative transcript, independent review, and
      scoped commit.

Until all applicable boxes are checked, the next child may be designed but must not be presented
as implemented or release-ready.

## 12. Self-review record

This roadmap was reviewed for placeholders, contradictory sequencing, and ambiguous ownership.
The dependency statements are intentionally repeated in Sections 4, 5, and 11: B1/B2 follow
B0; B3 consumes them; B4/B5 consume B1–B3; B6 is last; shared-base work is separately gated.
The seven deferred areas retain the exact approved meaning from the Phase A specification.
No implementation, test, existing specification, plan, or historical document is authorized or
changed by this file.
