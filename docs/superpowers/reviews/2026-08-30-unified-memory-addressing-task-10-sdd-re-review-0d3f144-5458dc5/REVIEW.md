# Scoped SDD Re-review — Task 10

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `0d3f144`  
**HEAD:** `5458dc5928f98e826a5847c24469bd9cd5ce560d`  
**Scope:** Task 10 exhaustive migration, public guidance, and release-note fix delta, plus the Task 10 contract.

## Files Reviewed

- `src/Scrinia.Mcp/MemoryTools.Core.cs`
- `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`
- `src/Scrinia.Core/Models/MemoryWrite.cs`
- `src/Scrinia.Core/FileMemoryStore.cs`
- `tests/Scrinia.Benchmarks/HintReadPathBenchmarks.cs`
- `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs`
- `tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs`
- Task 10 requirements in the specified plan
- Complete `0d3f144..5458dc5928f98e826a5847c24469bd9cd5ce560d` fix delta
- Repository search results for obsolete URI and raw-write surfaces at HEAD

## Overall Assessment

**REQUEST_CHANGES**

The fix delta updates the remaining MCP write paths to construct UTF-8, typed `MemoryWrite` values and
adds useful bundle-key portability coverage. It does not complete Task 10: null content can fail before
the categorized core validator, numerous repository consumers still use the old raw write constructor,
and public `FileMemoryStore` helpers continue to expose raw subject/scope address operations.

## Findings

### Major issues

#### T10-R001 — MCP content conversion bypasses categorized null-element validation

**Confidence:** 100%  
**Location:** `src/Scrinia.Mcp/MemoryTools.Core.cs:248,356-357` (HEAD)

`MemoryRemember` converts every content element with `content.Select(ToUtf8)` before constructing the
validated `MemoryWrite`. `ToUtf8` calls `Encoding.UTF8.GetBytes(content)` directly, so a null content
element raises `ArgumentNullException` before `MemoryWrite` can produce the required
`InvalidWriteContent` category and before the adapter's `InvalidMemoryWriteException` catch executes.
Task 2's shared policy and Task 10's adapter contract require null elements to be rejected through the
categorized validator with no mutation. The same conversion pattern is used by `MemoryAppend` at
`src/Scrinia.Mcp/MemoryTools.Core.cs:1053` and by compact at
`src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:1331`.

Convert nullable/raw content at a boundary that preserves the shared validation error, or pass raw
content through a single core factory that performs the conversion and categorization. Add an MCP
regression for a null content element asserting `InvalidWriteContent` and no store mutation.

#### T10-R002 — Raw `MemoryWrite` constructor usage remains repository-wide

**Confidence:** 100%  
**Location:** `src/Scrinia.Core/Models/MemoryWrite.cs:73-93` (HEAD); representative consumers
`tests/Scrinia.Benchmarks/HintReadPathBenchmarks.cs:82`,
`tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs:57-84`, and
`tests/Scrinia.Tests/Store/TransactionalRestoreTests.cs:28-30` (HEAD)

Task 10 Step 3 explicitly requires every old `MemoryWrite` constructor and fixture to migrate to
`MemoryAddress.Parse` plus typed content. The internal constructor accepting `string Name`, `string
Scope`, and `string[] Content` remains, and the HEAD tree still contains many calls such as
`new MemoryWrite("...", "local", ["..."])` across tests and benchmark fixtures. The scoped fix only
updates the MCP production call sites; it does not satisfy the repository-wide migration or the
Task 10 search/conformance requirement.

Remove the compatibility constructor after all legitimate derivation callers are migrated, update the
remaining fixtures and benchmark seeds to typed addresses/UTF-8 content, and add the planned
`NoObsoleteAddressSurfaceTests` / `AllAddressSurfaceConformanceTests` coverage (or equivalent) so a
future raw constructor call cannot re-enter the repository.

#### T10-R003 — Public `FileMemoryStore` helpers still expose raw address contracts

**Confidence:** 95%  
**Location:** `src/Scrinia.Core/FileMemoryStore.cs:282-325,550-556,1520,2080-2113` (HEAD)

Although the primary store operations now accept `MemoryAddress`, the public `FileMemoryStore` surface
still exposes raw subject/scope address operations including `ParseQualifiedName`, `FormatQualifiedName`,
`IsEphemeral`, `ArtifactPath(string name, ...)`, `FindArtifactPath(string subject, ...)`, `Remove(string
name, ...)`, `WriteArtifactAsync(string subject, string scope, ...)`, and
`DeleteArtifact(string subject, string scope)`. These are memory-facing methods on the public store
implementation and contradict Task 10 Step 1's requirement that every public memory-facing method have
no raw memory-address string contract. They also leave alternate paths around the canonical
`MemoryAddress` boundary.

Classify any genuinely physical-only helper as internal/private and route address-bearing operations
through typed addresses. If a helper is intentionally retained for a non-address concern, document and
test that narrow exception in the conformance inventory rather than leaving it indistinguishable from a
public memory API.

## Positive Observations

- The MCP remember, append, and compact paths in the fix delta now use the typed `MemoryAddress`
  constructor and UTF-8 content rather than the former raw name/scope write shape.
- The bundle regression verifies that an imported foreign artifact key is replaced with a destination-
  local key.
- The living architecture guidance now describes `Address` and opaque `ArtifactKey` instead of the
  removed public `Uri` property.
- The focused address/resource/bundle regression lane passed, and the solution build remained clean.

## Verification Evidence

- Reviewed the complete `0d3f144..5458dc5928f98e826a5847c24469bd9cd5ce560d` diff and Task 10 requirements.
- `git diff --check 0d3f144 5458dc5928f98e826a5847c24469bd9cd5ce560d`: passed.
- `dotnet build Scrinia.sln --no-restore`: passed, 15 projects, 0 errors, 0 warnings.
- Focused test command covering MCP address/resource, address-surface, and bundle portability/round-trip
  tests: passed, 36 tests.
- HEAD search still finds legacy `file://`/URI literals in explicit historical-storage fixtures and
  negative-input tests; these are not by themselves findings, but the remaining raw constructor and
  public helper matches above are active Task 10 migration surfaces.
- This package adds review artifacts only; source and test files were not modified.

## Four-Layer Review

- **Correctness:** **FAIL** — T10-R001 can escape the categorized validation path, while T10-R002 and
  T10-R003 leave alternate raw-address construction/operation surfaces in the repository.
- **Security:** **FAIL** — T10-R003 preserves public bypasses around the canonical address boundary;
  no new filesystem escape was found in the reviewed fix delta.
- **Performance:** **PASS** — the fix introduces no new unbounded scans, indexing changes, or hot-path
  allocation pattern beyond the required UTF-8 conversion.
- **Style & maintainability:** **FAIL** — the migration has multiple competing write/address contracts,
  and the planned exhaustive conformance tests are absent from HEAD.

## Philosophy Compliance

- Early exit: **FAIL** — null content can throw during adapter conversion before the categorized guard.
- Parse don't validate: **FAIL** — raw `MemoryWrite` and `FileMemoryStore` helper contracts still allow
  address data to bypass the canonical typed boundary.
- Atomic predictability: **PARTIAL** — typed MCP writes are deterministic, but null input has an
  uncategorized exception path and alternate raw surfaces remain.
- Fail fast: **FAIL** for T10-R001 — the adapter does not fail with the specified core error category.
- Intentional naming: **PARTIAL** — the new typed constructor calls are clear, but legacy public helper
  names and raw constructor shape continue to describe obsolete addressing semantics.
