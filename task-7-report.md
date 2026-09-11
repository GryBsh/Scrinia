# Task 7 review findings

## Current authoritative verification

The latest runs supersede earlier partial or stale reports. Counts below are the authoritative
results for this checkout:

- Focused append contract, journal, transaction, recovery, facade, cleanup, rollback, durability,
  and response-status tests: **197 passed, 0 failed, 0 skipped**. This includes exact before-event
  ordering/no-mutation coverage, null-entry facade mapping, and constructor enum validation.
- Golden response suite: **17 passed, 1 failed**. The append golden passed; the remaining compact
  failure is pre-existing unrelated response-shape drift and was not re-baselined.
- Full relevant suite: `Scrinia.Tests` **2856 passed, 21 failed, 19 skipped, 2896 total**;
  `Scrinia.Retrieval.Benchmarks.Tests` **272 passed**; `Scrinia.Plugin.Embeddings.Tests`
  **41 passed**. The 21 failures are unrelated contamination/environment failures (retired
  documentation and packaging expectations, recovery/compact pre-existing drift, and the Windows
  model-relocation access issue); none are Task 7 append failures.
- Solution build: **13 projects, 0 errors, 55 warnings**.

The exact remaining full-suite failures are captured in
`.superpowers/sdd/append-hardening/remaining-full-suite-failures.md`; the fresh command outputs are
in the sibling `fresh-*-evidence` files.

## Scope completed

- `success_with_warning` is classified by `McpResponse.IsWarning`, with focused regression coverage.
- The append golden now includes the normalized `operationId`; only the append snapshot changed.
- Append store, journal, transaction, and public contract XML documentation describes result types,
  target behavior, archive semantics, correlation, warnings, and recovery outcomes.
- Persistent missing-target `AppendAsync` now admits and recovers the append journal while holding
  the scope lock before entering `StoreAsyncCore` creation; retained `RecoveryRequired` evidence
  blocks or deterministically recovers instead of attempting a new mutation.
- CLI living documentation records create-via-append's lack of a prior-version archive and the
  structured operation correlation and warning outcomes.
- `BeforeEvent` fault injection now runs before `OnBeforeAppendAsync` and store invocation;
  before-event failure/cancellation maps to structured MCP errors without journal or live mutation.
- Non-recovery null entries map to structured `INTERNAL` responses with the store result's
  operation ID instead of escaping through generic invocation handling.
- `AppendStoreException` rejects enum values outside the approved `RecoveryRequired`/`Internal`
  contract.

Unrelated contamination files and the Phase B deferral remain untouched.

Fresh final-fix command summaries are recorded in
`.superpowers/sdd/append-hardening/final-fix-*evidence.txt`.
