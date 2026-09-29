# SDD Review Package — Task 9

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**BASE:** `755de18`  
**HEAD:** `f641a54`  
**Scope:** Task 9 source-generated JSON registration and serialization compatibility.

## Files Reviewed

- `src/Scrinia.Core/Bundles/BundleFormatService.cs`
- `src/Scrinia.Core/FileMemoryStore.cs`
- `src/Scrinia.Mcp/MemoryTools.Skills.cs`
- `src/Scrinia/Commands/CliJsonContext.cs`
- `src/Scrinia/Commands/ConfigJsonContext.cs`
- `src/Scrinia/Services/PluginClientJsonContext.cs`
- `tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs`
- Task 9 requirements in the specified plan
- Complete `755de18..f641a54` diff

## Overall Assessment

**REQUEST_CHANGES**

The change registers the new typed contracts in several production contexts and adds focused
round-trip coverage. The new test suite is internally inconsistent with the context registrations:
it requires every context to expose every Core contract even though the production contexts are
purpose-specific, so the claimed serialization gate cannot pass as written.

## Findings

### Major issues

#### T9-001 — Serialization test requires contracts from contexts that do not register them

**Confidence:** 100%  
**Location:** `tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs:16-30, 102-111` (HEAD);
context declarations in `src/Scrinia.Mcp/MemoryTools.Skills.cs:15-28`,
`src/Scrinia/Commands/ConfigJsonContext.cs:3-16`, and
`src/Scrinia/Services/PluginClientJsonContext.cs:3-16`

`AllContexts()` supplies five purpose-specific contexts, then
`Production_contexts_register_all_typed_memory_contracts` asks each one for all eight Core
contracts. `ScriniaMcpJsonContext` registers skill/agent metadata, the attributes dictionary, and
`ArtifactEntry`, but not `MemoryError`, `MemoryWrite`, or the request types. `ConfigJsonContext`
registers the configuration dictionary plus the new types listed in the diff, but not the full
bundle/index surface; `PluginClientJsonContext` is likewise a score-payload context. The first
test therefore receives null type metadata for at least the MCP context's missing contracts and
fails before validating any production serialization path. The later round-trip test repeats the
same over-broad assumption through `TypeInfo<T>`.

Define the expected contract set per production context, or expose a deliberately shared context
whose registration is actually required by every caller. Keep the test aligned with the plan's
"applicable source-generated contexts" requirement rather than making unrelated contexts register
unused types solely to satisfy a blanket loop.

### Minor issues

#### T9-002 — The test does not exercise the private FileMemoryStore serialization context

**Confidence:** 95%  
**Location:** `tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs:102-111` and
`src/Scrinia.Core/FileMemoryStore.cs:189-204` (HEAD)

`FileStoreJsonContext` is the context used for durable `ArtifactEntry` serialization, but it is
private and absent from `AllContexts()`. The new test consequently cannot prove that the actual
index/sidecar context round-trips the typed address and opaque artifact key; it only exercises
`BundleJsonContext` for the artifact case. Add a store-level serialization assertion through the
existing FileMemoryStore read/write path, or expose an internal test seam, without coupling tests
to reflection-based serialization.

## Positive Observations

- `MemoryAddress` is registered through source-generated contexts rather than introducing a
  reflection fallback.
- The bundle context preserves its explicit resolver and now includes the typed bundle contracts.
- The address converter is exercised for persistent, `/temp/...`, and `~...` inputs, including
  rejection of legacy `file://` values.
- The artifact assertions explicitly check for `address`/`artifactKey` and reject legacy URI
  property names.

## Verification Evidence

- Reviewed the complete `755de18..f641a54` diff and Task 9 requirements in the specified plan.
- `git diff --check 755de18 f641a54`: passed.
- The review was performed against the supplied commit range; unrelated working-tree files were
  not included in the reviewed diff.
- Source and test implementations were not modified by package generation.

## Four-Layer Review

- **Correctness:** **FAIL** — the new all-context registration/round-trip test requests metadata
  that purpose-specific contexts do not provide.
- **Security:** **PASS** — the change adds generated metadata and retains the strict address
  converter; no reflection fallback or unsafe input echo was introduced in the reviewed delta.
- **Performance:** **PASS** — registrations are static generated metadata and add no runtime scans
  or store traversal.
- **Style & maintainability:** **PARTIAL** — context additions are straightforward, but the test
  couples unrelated serialization contexts to a single global contract list and omits the actual
  private durable-store context.

## Philosophy Compliance

- Early exit: **PARTIAL** — the converter rejects malformed values at the JSON boundary, but the
  test's null metadata path is not guarded with a context-specific expectation.
- Parse don't validate: **PASS** — address parsing remains centralized in `MemoryAddress` and its
  JSON converter.
- Atomic predictability: **PARTIAL** — generated contexts are deterministic, while the blanket
  test contract makes the suite fail for valid context specialization.
- Fail fast: **FAIL** for T9-001 — the serialization gate fails on missing metadata before useful
  round-trip coverage can run.
- Intentional naming: **PARTIAL** — `AllContexts` and `coreContracts` are clear names, but their
  relationship incorrectly implies every context owns every contract.
