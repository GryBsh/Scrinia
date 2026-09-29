# 0.6.0 release-improvement milestone design

**Date:** 2026-08-27  
**Status:** Approved umbrella design  
**Milestone:** Entity facts, address conformance, publish parity, and 0.6.0 documentation

## Goal

Deliver one bounded, reviewable 0.6.0 milestone that improves entity-query cost and ownership,
mechanically protects address behavior, makes release project selection auditable, and brings current
release documentation into parity—without changing public APIs or storage behavior.

## Scope

The milestone contains five implementation streams and one release gate:

1. **IMP-04:** extract `EntityFactMaterializer` as the bounded entity-fact seam.
2. **IMP-01:** memoize entity-scoring adjacency at the fact-store ownership boundary.
3. **IMP-02:** add complete address-surface conformance coverage while preserving authoritative
   behavior.
4. **IMP-03:** enforce workflow/manifest parity and verify supported archive layouts, including the
   nested embeddings plugin and Vulkan replacement/fallback rules.
5. **0.6.0 versioning/docs:** set exactly `0.6.0`, add the current changelog entry, and correct only
   living documentation.
6. **Release acceptance:** run the required build, tests, publish/archive inspections, version/help
   probes, and documentation checks with platform limitations recorded honestly.

## Non-scope

- No broad `FileMemoryStore` rewrite; IMP-04 is the only approved extraction seam.
- No public API or storage-layout change, NMP/2 change, sidecar schema change, or plugin protocol
  change.
- No new remote/HTTP/REST/web surface.
- No rewriting historical changelog entries, dated specs, plans, or reports.
- No unsupported cross-platform native execution claim.

## Architecture and data flow

The sequence is intentional:

1. Establish characterization coverage for current entity facts and address behavior.
2. Implement IMP-04 so entity-fact construction has one bounded internal owner.
3. Implement IMP-01 against that stable fact-store boundary; invalidate adjacency whenever facts are
   replaced and prove query equivalence.
4. Implement IMP-02 independently against the current address owners; any intentional exception is
   enumerated, not normalized away.
5. Implement IMP-03 with `publish.projects.tsv` as the source of truth. Verify script/workflow parity
   before accepting archives.
6. Apply the 0.6.0 metadata and living-doc corrections after behavior and release surfaces are
   stable. Historical documents remain unchanged.
7. Run release acceptance from a clean, attributable environment and record exact commands, outputs,
   RIDs, archive paths, and limitations.

Entity facts continue to derive from authoritative store entries. Adjacency is a derived in-memory
optimization only. Address tests observe existing canonicalization/refusal. Publish scripts consume
one manifest. Version metadata flows from `Directory.Build.props`.

## Error and risk handling

- If IMP-04 changes fact results, stop the sequence and resolve characterization drift before IMP-01.
- If IMP-01 produces stale adjacency after mutation, invalidate the memo and reject the release
  candidate until equivalence passes.
- If IMP-02 misses an address-taking surface, the inventory is incomplete and the milestone is not
  accepted.
- If workflow/script/manifest selection diverges, publish acceptance fails before release artifacts
  are considered valid.
- If nested plugin files, model placement, or Vulkan replacement do not match local scripts, record
  the exact archive defect; do not substitute a different layout.
- If a platform cannot execute NativeAOT or Vulkan natively, report archive-only verification and do
  not claim runtime coverage.
- If version output is not exactly `0.6.0`, the release gate fails.

## Tests and acceptance criteria

The milestone is accepted only when all of these are true:

- Entity-fact characterization, filtered/legacy/ephemeral cases, materializer tests, adjacency
  invalidation, concurrency, and scorer-equivalence tests pass.
- Address conformance enumerates every current MCP, CLI, and resource surface and catches an
  unrecorded new surface.
- Manifest tests prove exact header/rows and parity with both scripts and the release workflow.
- Host publish succeeds; supported archive inspections prove CLI, merge-driver host restriction,
  optional nested embeddings layout, model path, and Vulkan replacement/fallback outcomes.
- Default solution build/tests and the documented benchmark lane are run and their actual results
  are recorded; unavailable checks are never called passes.
- Version/help probes report exactly `0.6.0`.
- Living docs match current local CLI/stdio MCP and plugin implementation; historical docs are
  unchanged.
- A final diff review proves only the approved implementation/docs/test files changed for the
  milestone, with no accidental API or storage changes.

## Affected files

- Entity streams: `src/Scrinia.Core/Search/EntityFactMaterializer.cs`,
  `EntityFactDerivation.cs`, `EntityFactStore.cs`, `EntityGraphScoreContributor.cs`, and focused
  tests under `tests/Scrinia.Tests/`.
- Address stream: `src/Scrinia.Mcp/MemoryTools.cs`, `ScriniaMcpResources.cs`, CLI command files,
  and address conformance tests.
- Publish stream: `publish.projects.tsv`, `publish.ps1`, `publish.sh`, `.github/workflows/release.yml`,
  and publish/archive tests.
- Release docs: `Directory.Build.props`, `CHANGELOG.md`, `README.md`, living docs and architecture
  pages named in the versioning design.
- Historical `docs/superpowers/` and prior reports: read-only, unchanged.

## Dependencies

- Existing entity derivation, address handling, local stdio MCP, plugin discovery, publish scripts,
  and manifest behavior are authoritative.
- IMP-04 precedes IMP-01; IMP-02 and IMP-03 can proceed independently after characterization.
- Versioning/documentation follows the implementation and publish-surface checks.
- Release acceptance is the final gate and does not authorize unsupported platform claims.

## Explicit no-placeholder content

The milestone has named streams, order, boundaries, files, failure rules, and release gates. Version
is exactly `0.6.0`; `publish.projects.tsv` is the source of truth; nested plugin/Vulkan behavior must
match local scripts; public APIs and storage behavior are preserved; historical docs remain unchanged.
