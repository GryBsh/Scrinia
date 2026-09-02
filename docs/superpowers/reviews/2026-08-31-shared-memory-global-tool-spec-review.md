# Shared Memory and Global Tool Architecture — Review Resolution

**Created:** 2026-08-31  
**Last revised:** 2026-09-01  
**Specification:** [`../specs/2026-08-31-shared-memory-global-tool-design.md`](../specs/2026-08-31-shared-memory-global-tool-design.md)  
**Status:** `Proposed` — reviewer approval pending

## Provenance rules

This record preserves objections and their disposition. “Closed” on an individual finding means the
corrected specification incorporates the stated ruling; it does not mean the specification passed
its approval gate. The user selected the product decisions recorded here but has not approved the
final corrected specification. No external reviewer identity or `APPROVE` result was supplied.

Each ruling uses one or more of these required source labels:

- **User design dialogue 2026-08-31** — a product, policy, contract, or scope decision selected by
  the user.
- **Repository fact investigation 2026-08-31** — a factual ruling grounded in the repository's
  current formats, paths, or behavior.
- **User design dialogue 2026-09-01** — a product, policy, contract, or scope decision selected by
  the user in the dated approval-blocker rounds.
- **Repository fact investigation 2026-09-01** — a factual ruling grounded in the repository state
  examined during the dated approval-blocker rounds.
- **Spec review 2026-09-01** — an objection raised by the dated specification review in Round 6.
- **Repository resolution 2026-09-01** — the resulting contract correction after reconciling that
  objection with repository behavior and the governing product boundary.

## Round 1 — Context and contract gaps

**Date:** 2026-08-31

The 2026-08-31 design dialogue retained the proposed global-tool, `SCRINIA_HOME`, named-base,
disjoint-server, explicit-refresh, no-Git-process, and separate-plugin direction as user-supplied
context. It did not record a separate reviewer approval. The unresolved objections concerned output totality,
zero-base representation, and prompt parsing.

### R1-01 — Complete tool and response contracts

**Reviewer objection:** The draft named eleven tools but left output payloads behind empty-object
placeholders, relied on local-model implications, and did not define exact conditional envelope,
privacy, pagination, or truncation behavior.

**Resolution / ruling:**

> Define standalone exact input and output types for all eleven tools. At minimum define
> `SharedBaseSummary`, `SearchHit`, `ListItem`, `RecallResult`, the three discriminated facts DTOs,
> `MutationResult`, and `RefreshResult`, including every required enum, nullability rule, timestamp,
> digest, provenance field, and operation-conditional field. Define exact YAML success, partial, and
> error shapes. Omit top-level `base` when name syntax or enable membership fails, never echo a
> disabled name, omit `results` from a zero-success aggregate error, and include warnings only for
> enabled bases. Replace every empty-object payload placeholder. Define global pagination and truncation.

**Rationale:** A public MCP contract must be serializable and testable without consulting the local
surface or implementation. Conditional provenance is also part of the disabled-base privacy
boundary.

**Source:** User design dialogue 2026-08-31  
**Closure:** Closed in specification §§5.1–5.3 and §14.5–14.6.

### R1-02 — Zero enabled bases

**Reviewer objection:** The draft made an empty Enabled scalar invalid but did not define how a
workspace represents zero enabled bases or how disabling the final base persists that state.

**Resolution / ruling:**

> Absence of the `Scrinia:Shared:Enabled` key means zero enabled bases. `scri shared disable` of the
> final enabled base removes the key atomically. An empty scalar remains invalid. Shared-server
> startup fails with `NO_SHARED_BASES_ENABLED` when the key is absent.

**Rationale:** One canonical empty-set representation avoids two encodings with different parser or
startup behavior.

**Source:** User design dialogue 2026-08-31  
**Closure:** Closed in specification §§3.2, 3.4, 8.1, and 14.1.

### R1-03 — Exact hint parser and token rules

**Reviewer objection:** The prompt advisory section did not settle read size, timeout, BOM and
whitespace handling, malformed JSON fallback, inflections, negation, or unsupported-client behavior.

**Resolution / ruling:**

> Use a supplied positional prompt; otherwise read at most 65,537 UTF-8 bytes from stdin with a
> two-second timeout. More than 65,536 bytes, invalid UTF-8, timeout, or read error emits nothing and
> exits 0. Strip one UTF-8 BOM and trim Unicode whitespace; empty emits nothing. If the first
> character is `{`, parse exactly one JSON object with only trailing whitespace, accept extra
> properties, and require the exact case-sensitive string property `prompt`; malformed, nonobject,
> missing, or nonstring input emits nothing and never falls back to raw JSON. Otherwise use trimmed
> raw text. Normalize NFKC and tokenize. Commit tokens are `commit`, `commits`, `committed`, and
> `committing`; push tokens are `push`, `pushes`, `pushed`, and `pushing`. Negation does not suppress
> a reminder. Unsupported clients and clients without a hook receive no advisory. Shared MCP has no
> prompt or correlation field. Phase 4 changes OpenCode from `save` to `hint`.

**Rationale:** Fail-silent advisory code must have deterministic byte, syntax, and lexical boundaries
and must not become an implicit MCP transport.

**Source:** User design dialogue 2026-08-31  
**Closure:** Closed in specification §7.3 and §14.8.

## Round 2 — Safety and repository-boundary objections

**Date:** 2026-08-31

### R2-01 — Freshness race and residual risk

**Reviewer objection:** The draft's race language could be read as guaranteeing that all concurrent
external mutations are detected, despite a portable gap between pre-write fingerprint F0 and
post-write fingerprint F1.

**Resolution / ruling:**

> The pre-write fingerprint check is fail-closed. External filesystem or Git mutation after F0 and
> before operation completion is forbidden and unsupported and cannot be made race-free portably;
> it may be overwritten or incorporated into F1. After write initiation, validate every
> authoritative path and file and recompute F1. Invalid format or path produces mutation `unknown`,
> marks the base stale, and blocks further writes; an ordered marker triplet marks it conflicted. A
> valid concurrent change can be accepted into F1. State the residual data-loss risk explicitly and
> require operational serialization. Tests cover detectable drift and post-write invalidity, not
> impossible race prevention.

**Rationale:** The design must distinguish a useful fail-closed precondition from a filesystem
transaction guarantee that the supported platforms cannot provide.

**Source:** User design dialogue 2026-08-31  
**Closure:** Closed in specification §§6.2–6.3, §12, and §14.7.

### R2-02 — Conflict taxonomy

**Reviewer objection:** Vague conflict filenames, duplicate artifacts, and reconciliation scratch
conventions made whole-base conflict status depend on removed merge-subsystem behavior.

**Resolution / ruling:**

> A base is conflicted only when an authoritative UTF-8 text file contains, in order, a line
> beginning `<<<<<<< `, a later line exactly `=======`, and a later line beginning `>>>>>>> `.
> Scan NMP, JSON, Markdown, and sidecars as text before parsing. Do not inspect `.git` or infer
> conflict from filenames, duplicates, or scratch conventions. Embedded substrings that are not an
> ordered line-start triplet are not conflicts. The stable code is `SHARED_BASE_CONFLICTED`.

**Rationale:** One content-based predicate is stable after merge-driver retirement and avoids false
positives from ordinary marker-like text.

**Source:** User design dialogue 2026-08-31  
**Closure:** Closed in specification §4.4, §6.3, §11, and §14.4.

### R2-03 — Static Git cleanup contract

**Reviewer objection:** The draft printed one example cleanup path and deferred the rest to
filesystem discovery, which could leave already-indexed derived paths behind when absent from the
worktree.

**Resolution / ruling:**

> Print static registry-derived `git rm -r --cached --ignore-unmatch -- <pathspec>` commands for
> every derived class regardless of file existence. Define the command as argv, define reversible
> path/root quoting, and print both exact merge-config unset commands. Scrinia does not inspect the
> index and cannot verify the result.

**Rationale:** Current repository facts show rebuildable facts caches, embeddings/indexes, exports,
archived `versions`, lock files, atomic-write temporary files, and restore journals. Static
pathspecs cover absent worktree files that can still exist in the index; `--ignore-unmatch` keeps
the cleanup idempotent.

**Source:** Repository fact investigation 2026-08-31  
**Closure:** Closed in specification §§4.1, 8.2–8.3, and §14.9.

## Round 3 — Ownership, provenance, and closure objections

**Date:** 2026-08-31

### R3-01 — Exclusive phase ownership

**Reviewer objection:** Phase 3 and Phase 4 both claimed shared response wording; Phase 2 retained
an undefined old-model diagnostic for Phase 5 removal; living documentation could be edited by
multiple phases.

**Resolution / ruling:**

> Phase 3 owns the complete shared response contracts, including operation-based Git guidance, and
> removes every temporary adapter before exit. Phase 4 owns only prompt-hint transport, agent
> artifacts, and expanded onboarding/multi-user replacement; it does not alter shared response
> wording. Remove the undefined old-model diagnostic and Phase 5 removal. Missing tracking policy
> permanently returns `TRACKING_POLICY_REQUIRED` until configured; it is not an adapter. Assign each
> changed living document once: Phase 1 minimal merge removal/manual cleanup, Phase 2
> config/tracking docs, Phase 3 shared-MCP technical docs, Phase 4 onboarding/hints/multi-user
> replacement, and Phase 5 installation/global-package docs.

**Rationale:** Exclusive artifacts make plans independently reviewable and prevent later phases from
silently changing a public contract already accepted at an earlier exit gate.

**Source:** User design dialogue 2026-08-31  
**Closure:** Closed in specification §13 and §14.10.

### R3-02 — Review provenance

**Reviewer objection:** The specification's ledger pointed to itself, did not preserve review rounds
or objections, and could be mistaken for self-approval.

**Resolution / ruling:**

> Create this separate review record with dated round 1, 2, and 3 findings, exact rulings,
> rationale, source labels, objections, and closure. Use `User design dialogue 2026-08-31` for
> user-selected decisions and `Repository fact investigation 2026-08-31` for factual rulings. Make
> the specification ledger link to this record and section IDs. Do not invent approvals.

**Rationale:** A decision is auditable only when its source and the objection it resolved remain
separate from the artifact governed by that decision.

**Source:** User design dialogue 2026-08-31  
**Closure:** Closed by this record and specification §15.

### R3-03 — NMP text scan order

**Reviewer objection:** Calling NMP bodies binary-like while scanning them for text markers would
make conflict detection internally contradictory.

**Resolution / ruling:**

> NMP artifacts are UTF-8 NMP text. Conflict-marker scanning precedes NMP decode and format parsing.

**Rationale:** The NMP/2 artifact stored by the repository is textual framing and can be scanned as
UTF-8 before its encoded payload is decoded.

**Source:** Repository fact investigation 2026-08-31  
**Closure:** Closed in specification §4.4 and §14.4.

### R3-04 — Eleven complete tool contracts

**Reviewer objection:** Mutation outputs and shared skill behavior remained implicit even after the
read DTOs were enumerated.

**Resolution / ruling:**

> Complete every input and output schema rather than saying “same as local.” Preserve the selected
> defaults, ranges, and conditional arguments. Specify success fields for `shared_forget`,
> `shared_compact`, `shared_link`, `shared_append`, and `shared_remember`. Shared skill recall,
> remember, append, and forget never use an embedded built-in fallback.

**Rationale:** Mutations and reserved skill paths are high-risk places for local behavior to leak
into the disjoint shared server.

**Source:** User design dialogue 2026-08-31  
**Closure:** Closed in specification §§5.1–5.3 and §14.5.

### R3-05 — Acceptance and final self-review

**Reviewer objection:** The acceptance matrix did not explicitly lock the new privacy, parser,
serialization, race-limit, conflict, cleanup, ownership, and traceability rulings.

**Resolution / ruling:**

> Add acceptance tests for disabled privacy, zero-key configuration, every hint-parser boundary,
> DTO serialization and YAML goldens, zero-success aggregation, documented concurrent-race limits
> and post-write invalidation, marker-triplet detection and non-detection, static cleanup commands
> for absent indexed paths, exclusive phase artifact ownership, and decision-trace links. Complete a
> final scan for placeholders, empty-object payloads, optional or modal normative wording, schema/example
> mismatch, undeclared codes, and contradictions.

**Rationale:** These are contract boundaries where a representative happy-path test would not catch
privacy leaks, ambiguous serialization, or guarantees stronger than the implementation can make.

**Source:** User design dialogue 2026-08-31  
**Closure:** Closed in specification §§14 and 17.

## Round 4 — Approval-gate blockers

**Date:** 2026-09-01

The user supplied binding corrections after the prior review record was written. These are
user-selected product decisions and repository-fact corrections, not approval of the resulting
document. The reviewer outcome remains pending.

### R4-01 — Fixed shared tracking and one-to-one derived policy

**Reviewer objection:** Shared tracking remained workspace-configurable; base registration did not
transactionally establish a safe Git-root/ignore policy; recursive ignore and cleanup patterns could
drift or miss deleted-but-indexed derived files.

**Resolution / ruling:**

> Shared bases are always `project-git`; only the local workspace retains `local|project-git`.
> `scri shared base add` requires the supplied root itself to be a Git repository or worktree by a
> `.git` directory or valid `gitdir:` text marker, without invoking Git. Before registry commit it
> creates or validates `.scrinia` and an exact marked, UTF-8-no-BOM, LF-terminated derived-only block.
> Preserve unrelated lines; after registration refuse a missing block, and always refuse drifted,
> duplicate, or unsafe marker content without overwrite. Revalidate the block on every shared startup, refresh, and mutation; make drifted bases
> unavailable with `SHARED_TRACKING_POLICY_DRIFT` and emit no staging guidance. Generate anchored
> ignore patterns, fingerprint exclusions, and root-relative static `git rm --cached
> --ignore-unmatch` pathspecs one-to-one from the same canonical ordered registry. Test exact
> equivalence, including deleted-but-indexed paths.

**Rationale:** Scrinia cannot safely suggest staging until it has proved that every derived class is
excluded. Filesystem-only Git-root evidence and static cleanup guidance preserve the categorical
no-Git-process/no-index boundary.

**Source:** User design dialogue 2026-09-01; Repository fact investigation 2026-09-01  
**Closure:** Closed in specification §§3.3–3.4, 4.1–4.3, 7.1, 8.1–8.3, 11, and 14.

### R4-02 — Error-stage and `shared_bases` contracts

**Reviewer objection:** Error envelopes exposed result DTOs and rejected paths, base provenance could
appear before membership validation, and the general zero-success aggregate rule contradicted the
purpose of `shared_bases`.

**Resolution / ruling:**

> `result` and `results`, including `MutationResult.path`, exist only on success or partial success,
> never validation/error. Parameter/schema failure omits scalar `base`; after schema succeeds,
> syntax and enabled-membership must both succeed before scalar base can be echoed. Never echo a
> disabled name. Define `status`, `warnings`, `errorCode`, `error`, `instruction`, `followUp`, and
> `mutation` presence at every stage. `shared_bases` always returns all enabled summaries, including
> unavailable, stale, and conflicted; it is success only when all are available and partial
> otherwise, with no zero-success conversion. Zero enabled bases prevents server startup. Other
> aggregate tools retain the zero-success error rule.

**Rationale:** Conditional provenance is an information boundary. The inventory tool must describe
degraded enabled bases even when none is usable, while ordinary aggregate reads cannot claim a
successful result when none contributed.

**Source:** User design dialogue 2026-09-01  
**Closure:** Closed in specification §§5.2–5.3, 11, and 14.6.

### R4-03 — Exact compatibility and CLI errors

**Reviewer objection:** Compatibility shorthand did not state durable behavior, and CLI JSON failures
lacked an exhaustive command/code/precedence/exit mapping.

**Resolution / ruling:**

> Enumerate the actual `ArtifactEntry`, skill-meta, and agent-meta missing/null defaults; JSON name
> casing; legacy `fields` precedence; malformed/missing/wrong-shape and I/O behavior; v1/newer
> read/write/delete/derived-update behavior; unknown-field survival; rewrite timestamps; semantic
> indexing exclusions; and exact UTF-8/BOM/JSON/newline bytes. Replace every compatibility shorthand.
> Declare an exhaustive CLI table including `CLI_INVALID_ARGUMENT`, workspace/user config failures,
> locks/writes, shared registration/root/policy failures, local tracking/migration failures, and
> otherwise-classified `IO_FAILURE`. Define command mapping, precedence, exit class, exact runtime
> code reuse, and warning-only exit 0. Emit no undeclared catch-all.

**Rationale:** Compatibility and automation surfaces must remain testable after implementation
changes; behavior shorthand and generic I/O failures are not stable contracts.

**Source:** Repository fact investigation 2026-09-01; User design dialogue 2026-09-01  
**Closure:** Closed in specification §§4.4–4.5, 8.1.1, 11, and 14.

### R4-04 — Documentation ownership and approval provenance

**Reviewer objection:** Phase ownership still overlapped in shared living files, omitted plugin
authoring, and incorrectly presented the architecture/review as approved or closed.

**Resolution / ruling:**

> Phase 5 owns every plugin, global-tool, model-relocation, setup, and plugin-diagnostic section in
> `docs/getting-started.md`, `docs/troubleshooting.md`, `docs/architecture/cli.md`,
> `docs/plugin-authoring.md`, `docs/security.md`, and `docs/cli-reference.md`. Phase 1 owns only
> explicitly named merge/reconcile removal and the troubleshooting heading for manual Git conflict
> cleanup. Phase 4 owns shared onboarding replacement. Assign overlapping files by disjoint heading
> or line subject. Test and grep living docs and source help for stale `{exeDir}/plugins` and
> `{exeDir}/models`; exempt dated history. Mark the specification `Proposed — reviewer approval
> pending`; record that the user selected product decisions but has not approved the corrected spec;
> keep reviewer outcome pending until an external `APPROVE` result exists.

**Rationale:** File-level ownership is insufficient when independent phases must edit different
subjects in one guide. Product decisions are not the same event as reviewer approval.

**Source:** User design dialogue 2026-09-01; Repository fact investigation 2026-09-01  
**Closure:** Closed in specification §§13–17 and this review record.

## Round 5 — Authoritative staging, classifier, sidecar, and migration blockers

**Date:** 2026-09-01

The user supplied four further binding approval-blocker corrections. They supersede Round 4 where
Round 4 described pattern-to-class one-to-one generation or broad store staging. Incorporation closes
the findings below but is not reviewer approval; the specification remains proposed and the reviewer
outcome remains pending.

### R5-01 — Exact authoritative mutation manifests

**Reviewer objection:** Broad store staging could stage a pretracked derived file, did not describe
deletions, and did not identify both link endpoints or the exact compact archive classification.

**Resolution / ruling:**

> Every successful shared mutation returns a deterministic manifest containing only the exact
> root-relative authoritative files that operation created, modified, or deleted. Remember and append
> list their body and sidecar; forget lists deleted authoritative paths; link lists both entries;
> compact lists its rewritten body and sidecar and lists a version archive only if the registry
> classifies versions as authoritative. Skill and agent writes list Markdown plus sidecar. The only
> control-file exception is `.scrinia/.gitignore` when successful `shared base add` actually changes
> it. Registered-base drift has no modifying command. No derived path, wildcard, or directory pathspec enters a manifest. Staging guidance is
> exactly `git -C <registered-root> add -A -- <each individually shell-quoted manifest path>`. Remove
> broad store staging and test that a pretracked derived file cannot appear in mutation staging
> guidance.

**Rationale:** Exact `-A` pathspecs stage writes and deletions without sweeping already tracked
derived state into the next commit.

**Source:** User design dialogue 2026-09-01  
**Closure:** Closed in specification §§5.2–5.3, 7.2, 8.1–8.3, 14.6, and 17.

### R5-02 — Ordered derived classifier and exact set equivalence

**Reviewer objection:** The derived registry duplicated the root lock, matched temporary files only
at the root, and incorrectly required a pattern-to-class bijection even though Git patterns can
overlap.

**Resolution / ruling:**

> Remove the redundant `/.lock` rule because `/*.lock` covers the root lock. Classify every file or
> directory whose final segment ends in `.tmp` at any depth as a temporary tree, including descendants
> by ancestry. Classify every root-child file or directory ending in `.lock` as a lock tree, including
> descendants by ancestry. Prohibit authoritative files, directories, and path segments from matching
> those reserved suffix rules. Define ordered first-match classification: canonical root derived
> directories first, canonical root singleton derived files next, recursive temporary trees next,
> and root lock trees last. Each filesystem path receives at most one semantic class by precedence.
> Managed ignore and static cleanup patterns cover exactly the same derived set but may overlap.
> Fingerprinting excludes exactly the classifier union. Static cleanup uses safe root-plus-recursive
> Git glob pathspecs so absent or indexed temporary paths at every depth are covered.

**Rationale:** Semantic classification must be unambiguous, but equivalent Git coverage does not
require each semantic class to have exactly one nonoverlapping pattern.

**Source:** User design dialogue 2026-09-01; Repository fact investigation 2026-09-01  
**Closure:** Closed in specification §§4.1–4.2, 8.2–8.3, 13–14, and 17.

### R5-03 — Corrupt memory-sidecar identity is never visible

**Reviewer objection:** The compatibility text made invalid-address behavior modal rather than
deterministic and did not make missing or blank `artifactKey` behavior equally deterministic.

**Resolution / ruling:**

> A missing, null, blank, or invalid memory-sidecar `address` makes that entry unavailable and
> skipped with `SHARED_ENTRY_CORRUPT`. A missing, null, or blank `artifactKey` has the same result.
> Neither state ever yields a visible search, list, facts, or recall result. Other valid entries in
> the base remain usable.

**Rationale:** Identity is required to expose an entry safely; corruption must not become a
best-effort visibility decision.

**Source:** User design dialogue 2026-09-01; Repository fact investigation 2026-09-01  
**Closure:** Closed in specification §§4.4–4.5, 11, 14.4, and 17.

### R5-04 — Recognized legacy state requires explicit migration

**Reviewer objection:** `tracking set` was allowed to proceed in recognized legacy states, leaving
the CLI state machine without a deterministic migration-required outcome and overloading unsafe-file
errors.

**Resolution / ruling:**

> Add CLI code `LEGACY_MIGRATION_REQUIRED`. In recognized `legacy-clean` or `legacy-mixed` state,
> `scri tracking set ...` makes no change, fails with that code and exit class 3, and directs
> `scri tracking migrate-legacy --to <requested-policy>`. `MANAGED_FILE_UNSAFE` remains reserved for
> unattributable or unsafe managed content. Argument and workspace-config validation precede legacy
> classification; unsafe/unattributable content precedes recognized legacy state; recognized legacy
> state precedes lock or write attempts. Map and test every state, code, precedence, exit class, exact
> remediation, and no-write result.

**Rationale:** A recognized obsolete layout needs an explicit migration command, while unsafe bytes
need a distinct refusal that does not imply automatic cleanup is available.

**Source:** User design dialogue 2026-09-01  
**Closure:** Closed in specification §§8.1–8.3, 11, 14.9, 14.13, and 17.

## Round 6 — Persistent scope, filesystem trees, transitions, and provenance

**Date:** 2026-09-01

This round records the dated specification review and repository resolution that followed Round 5.
Its closures mean only that the corrections are incorporated. They do not approve the specification;
the external reviewer outcome remains pending.

### R6-01 — Nullable durable timestamps

**Reviewer objection:** `ListItem` and `RecallResult` required timestamps even though readable
skill/agent metadata and memory `updatedAt` can validly be null, forcing an undeclared synthetic date.

**Resolution / ruling:**

> Make `ListItem.created`, `ListItem.updated`, `RecallResult.created`, and `RecallResult.updated`
> nullable ISO 8601 strings. Preserve durable null as null for readable skill/agent metadata and null
> `updatedAt`; never synthesize a date. Any other timestamp exposed from a nullable durable source
> must declare the same nullability; this includes nullable facts-profile `firstSeen` and `lastSeen`.
> Add source-generated serialization and YAML golden tests.

**Rationale:** Output contracts must preserve durable compatibility semantics rather than inventing
provenance absent from storage.

**Source:** Spec review 2026-09-01; Repository resolution 2026-09-01  
**Closure:** Closed in specification §§5.2–5.3, 14.6, and 17.

### R6-02 — Shared bases are persistent-only

**Reviewer objection:** The shared schema admitted ephemeral list items and did not reject every
existing Ephemeral spelling before storage, despite shared mutations requiring durable Git artifacts.

**Resolution / ruling:**

> Shared bases support persistent entries only. After current `MemoryAddress`/`PathParser` parsing,
> reject every Ephemeral `path`, `destination`, or `pathPrefix`, including `~...` and `/temp/...`,
> with `SHARED_EPHEMERAL_NOT_SUPPORTED` before storage. Search, list, and facts never return ephemeral
> entries; recall, remember, append, forget, compact, and either link endpoint cannot target them.
> Remove ephemeral from `ListItem.kind`. Every applied shared mutation therefore has authoritative
> files. Add schema, parser-table, no-storage, inventory-exclusion, and manifest tests.

**Rationale:** Process-lifetime data has no authoritative filesystem state for freshness,
fingerprinting, or exact Git staging.

**Source:** Spec review 2026-09-01; Repository resolution 2026-09-01  
**Closure:** Closed in specification §§5.1–5.3, 7.2, 11, 14.5–14.6, and 17.

### R6-03 — Temporary and lock suffixes define trees

**Reviewer objection:** The classifier covered regular files only, allowing directories with reserved
suffixes and their descendants to disagree across ignore, cleanup, fingerprint, and safety consumers.

**Resolution / ruling:**

> Under the existing ordered precedence, any file or directory at any depth whose final segment ends
> in `.tmp` is a derived temporary tree and every descendant is derived by ancestry. Any root-child
> file or directory ending in `.lock` is a derived lock tree and every descendant is derived by
> ancestry. Forbid authoritative files, directories, and path segments matching those suffix rules.
> Keep ignore, static cleanup, fingerprint, equivalence text, and exhaustive file/directory/descendant
> tests aligned with that exact union.

**Rationale:** Git ignore patterns already have directory-tree effects; the semantic classifier and
all consumers must describe the same filesystem set.

**Source:** Spec review 2026-09-01; Repository resolution 2026-09-01  
**Closure:** Closed in specification §§4.1–4.2, 8.2–8.3, 14.9, and 17. This extends R5-02; R5-02
remains the classifier decision and does not govern shared tracking/base registration.

### R6-04 — Complete tracking transition matrix

**Reviewer objection:** `migrate-legacy` behavior after migration and several command/state cells were
implicit, including same-policy no-ops, cross-policy changes, managed-file atomicity, and errors.

**Resolution / ruling:**

> Define every state against `tracking status`, both `tracking set` targets, and both
> `migrate-legacy --to` targets. `local`→`local` and `project-git`→`project-git` migration are exit-0
> no-ops; both opposite-policy migrations are deliberate atomic policy changes. Name the managed
> files, preserve unrelated config entries and lines, retain exact error precedence and exit classes,
> and report Git state as `UNVERIFIED`.

**Rationale:** An explicit migration verb must remain deterministic when rerun or used to select a
new policy after legacy cleanup.

**Source:** Spec review 2026-09-01; Repository resolution 2026-09-01  
**Closure:** Closed in specification §§8.1–8.3, 14.9, 14.13, and 17.

### R6-05 — No automatic tracking-block repair

**Reviewer objection:** The mutation-manifest text named an undeclared automatic repair path,
contradicting fail-closed drift handling and the enumerated CLI surface.

**Resolution / ruling:**

> Remove the repair operation entirely. `.scrinia/.gitignore` appears in an authoritative mutation
> manifest only when successful `shared base add` changed it. An existing registered base with a
> missing or drifted block is refused; the operator manually restores the exact documented block,
> preserving unrelated lines, then invokes `shared_refresh` or restarts. Scrinia emits no manifest or
> Git-state claim for that manual edit.

**Rationale:** Recovery cannot rely on a command absent from the public surface, and automatic
overwrite would weaken the managed-policy ownership boundary.

**Source:** Spec review 2026-09-01; Repository resolution 2026-09-01  
**Closure:** Closed in specification §§7.2, 8.1–8.2, 14.6, 14.9, and 17.

### R6-06 — Complete source labels and decision references

**Reviewer objection:** The provenance declaration omitted labels used by this dated round, and the
fixed shared-project-Git/base-registration ledger row incorrectly cited classifier-only R5-02.

**Resolution / ruling:**

> Declare every source-label category actually used, including `Spec review 2026-09-01` and
> `Repository resolution 2026-09-01`. Normalize capitalization. Cite R4-01 for fixed shared
> project-Git and transactional base registration; retain R5-02 only for ordered classifier and exact
> derived-set equivalence. Do not record or imply final approval.

**Rationale:** A trace is auditable only when its label vocabulary is declared and each decision
points to the ruling that actually established it.

**Source:** Spec review 2026-09-01; Repository resolution 2026-09-01  
**Closure:** Closed in this record and specification §§15–17.

## Current reviewer outcome

Rounds 1–6 are incorporated into the corrected proposal. Finding closure records incorporation only.
The user selected the governing product decisions but has not approved the final corrected
specification. **Outcome: pending external review.** Do not change the specification to approved or
begin implementation planning until an external reviewer returns `APPROVE`.

## Prior ruling excerpts

The following text preserves the 2026-08-31 dialogue for audit history, with DTO shorthand normalized
to the current R6-01/R6-02 contract so it cannot be mistaken for an active contradictory schema. The numbered
entries map to the earlier resolution sections above. Where its envelope, aggregation, tracking,
compatibility, cleanup, ownership, persistence, timestamp, or approval wording differs from Rounds 4–6, the later ruling
supersedes it; this historical quotation is not a current normative contract.

1. **R1-01 — User design dialogue 2026-08-31**

   > Complete output DTOs/envelopes: define standalone exact types and conditional presence for all eleven tools. At minimum:
   > - SharedBaseSummary: base string, root absolute string (`shared_bases` only), availability enum `available|unavailable|stale|conflicted`, generation nullable 64-char lowercase SHA-256.
   > - SearchHit: base,path,kind `entry|chunk`,chunk nullable >=1,score finite double,preview string,attributes map<string,string[]>.
   > - ListItem: base,path,kind `persistent`,created/updated nullable ISO8601,description nullable,attributes,keywords; drift mode additionally drift enum `unchanged|changed|missing` with file refs.
   > - RecallResult: base,path,chunks string[],attributes,keywords,created/updated nullable ISO8601.
   > - Facts DTO unions: `entities` rows(entity,count), `profile`(entity,count,nullable firstSeen/lastSeen,coOccurrences,timeline paths), `intersect` paths; all top-level base.
   > - MutationResult: base, conditional path/destination fields, mutation `applied|none`, a required nonnull generation on success, and a manifest of authoritative paths. Mutation errors have no result DTO; only the top-level envelope may report mutation `unknown`.
   > - RefreshResult: base,previousGeneration,newGeneration,availability,warnings.
   > - `shared_bases` returns enabled bases only.
   > Define exact YAML top-level success/partial/error shapes. `base` omitted when name syntax or enable-membership fails; disabled names never echoed. Zero-success aggregate error has no results and warnings only for enabled bases. Replace content:{} placeholders. Define pagination and truncation.

2. **R1-02 — User design dialogue 2026-08-31**

   > Zero enabled representation: absent `Scrinia:Shared:Enabled` key means zero enabled. `shared disable` of last base removes the key atomically. Empty scalar remains invalid. Server startup fails NO_SHARED_BASES_ENABLED.

3. **R1-03 — User design dialogue 2026-08-31**

   > Hint parser exact algorithm:
   > - positional prompt if supplied else stdin;
   > - read max 65,537 UTF-8 bytes with 2-second timeout; >65,536, invalid UTF-8, timeout, read error => no output, exit0;
   > - strip UTF-8 BOM, trim Unicode whitespace; empty => no output;
   > - if first char is `{`, parse exactly one JSON object with only trailing whitespace allowed; extra properties accepted; exact case-sensitive string `prompt` required; malformed/nonobject/missing/nonstring => no output (never raw JSON fallback);
   > - otherwise use trimmed raw text;
   > - NFKC normalize and tokenize; advisory commit tokens commit|commits|committed|committing and push tokens push|pushes|pushed|pushing; negation does not suppress reminder;
   > - unsupported clients/no hook => no advisory. No shared MCP prompt field/correlation. Phase 4 fixes OpenCode save->hint.

4. **R2-01 — User design dialogue 2026-08-31**

   > Freshness concurrent race: be honest. Pre-write fingerprint check is fail-closed. External filesystem/Git mutation concurrent after F0 and before operation completion is forbidden/unsupported and cannot be made race-free portably; it may be overwritten or incorporated into F1. After write, validate every authoritative file and recompute F1; any invalid format/path => mutation unknown, base stale, further writes blocked, but a valid concurrent change can be accepted into F1. State this residual data-loss risk explicitly and require operational serialization. Do not claim stronger guarantees. Tests cover detectable drift and postwrite invalidity, not impossible race prevention.

5. **R2-02 — User design dialogue 2026-08-31**

   > Conflict taxonomy: remove vague conflict filenames/reconciliation artifacts. Base is conflicted only when an authoritative UTF-8 text file contains an ordered Git marker triplet at line starts: `<<<<<<< `, a later line exactly `=======`, and a later `>>>>>>> `; NMP/JSON/Markdown/sidecars are scanned as text before parse. No `.git` inspection or scratch conventions. Ordinary embedded substrings not at line start/triplet are not conflict. Stable SHARED_BASE_CONFLICTED.

6. **R2-03 — Repository fact investigation 2026-08-31**

   > Git cleanup: print static registry-derived pathspec commands with `--ignore-unmatch` for every derived class regardless file existence, e.g. `git -C <root> rm -r --cached --ignore-unmatch -- .scrinia/embeddings .scrinia/exports .scrinia/versions ...`; define argv/path quoting and both exact merge config unset commands. Scrinia cannot verify index result. List all derived pathspecs from authoritative/derived registry.

7. **R3-01 — User design dialogue 2026-08-31**

   > Exclusive phase ownership: Phase 3 owns complete shared response contracts including operation-based Git guidance and removes every temporary adapter before exit. Phase 4 owns ONLY prompt hint transport, agent artifacts, and expanded onboarding docs; it does not alter shared response wording. Remove undefined old-model diagnostic/Phase5 removal. Tracking missing-policy behavior is permanent TRACKING_POLICY_REQUIRED until configured, not an adapter. Assign each living doc once: Phase1 minimal merge removal/manual cleanup; Phase2 config/tracking docs; Phase3 shared MCP technical docs; Phase4 onboarding/hints/multi-user replacement; Phase5 installation/global package docs.

8. **R3-02 — User design dialogue 2026-08-31**

   > Review provenance: create the separate review record containing dated round-1/2/3 findings, exact resolution/ruling text, rationale, and source as `User design dialogue 2026-08-31` for user-selected decisions and `Repository fact investigation 2026-08-31` for factual rulings. Spec decision ledger references this review file and section IDs, not itself. Record reviewer objections and closure. No invented approvals.

9. **R3-03 — Repository fact investigation 2026-08-31**

   > Ensure durable conflict scanning does not contradict binary-like NMP text: artifacts are UTF-8 NMP text and marker scan precedes decode.

10. **R3-04 — User design dialogue 2026-08-31**

    > Complete each of eleven tool input and output schemas, not “same as local”. Include exact defaults/ranges/conditional args already decided. Specify `shared_forget`, `shared_compact`, `shared_link`, `shared_append`, `shared_remember` success fields and no built-in skill fallback.

11. **R3-05 — User design dialogue 2026-08-31**

    > Add acceptance tests for disabled privacy, zero-key config, hint parser all boundaries, output DTO serialization/YAML goldens, zero-success aggregate, concurrent-race documented limits/postwrite invalidation, marker triplet detection/non-detection, static cleanup commands for absent indexed paths, exclusive phase artifact ownership, and decision trace links.
    >
    > Run final self-review: no placeholders, no `content:{}`, no optional/modal normative wording, examples match schemas, all codes declared, no contradiction.
