# SDD Review Package — Task 4

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**BASE:** `96c25e7`  
**HEAD:** `a6cfb61`  
**Scope:** Task 4, obsolete URI resolution removal and `FileMemoryStore` artifact-key boundary

## Files Reviewed

- `src/Scrinia.Core/FileMemoryStore.cs`
- `tests/Scrinia.Tests/ArtifactKeyBoundaryTests.cs`
- `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs`
- Task 4 requirements in the specified plan and the unified-addressing design spec
- Complete `96c25e7..a6cfb61` diff

## Overall Assessment

**REQUEST_CHANGES**

The change adds store-relative key generation and a physical mapping guard, but Task 4's
breaking cleanup and boundary coverage are incomplete. A typed artifact read still converts
the address back into a raw qualified-name string, and the artifact-key validation used by
`ArtifactEntry` still accepts traversal segments that the new boundary tests do not exercise.

## Findings

### Major issues

#### T4-001 — Typed artifact reads re-enter the removed raw-name resolution path

**Location:** `src/Scrinia.Core/FileMemoryStore.cs:2218-2222` (HEAD)

`ReadArtifactAsync(MemoryAddress address)` calls `ParseQualifiedName(address.ToString())` and
then resolves the artifact through the raw `subject`/`scope` path. Task 4 requires typed read
paths and removal of duplicate URI/name resolution; the typed address should be resolved once
and passed directly to the physical boundary. Re-stringifying and reparsing weakens the
canonical-address contract and leaves a raw address-bearing route in the store implementation.

#### T4-002 — Artifact-key validation still permits traversal keys

**Location:** `src/Scrinia.Core/Models/ArtifactEntry.cs:120-126` (HEAD),
`src/Scrinia.Core/FileMemoryStore.cs:2190-2203` (HEAD)

The `ArtifactEntry` validator rejects rooted, drive-qualified, backslash, and absolute-URI
values, but does not reject `.` or `..` path segments. The `FileMemoryStore` mapper does reject
those segments, yet externally constructed entries can still carry an unsafe key and pass the
model boundary. Task 4 explicitly requires traversal-key rejection and boundary validation;
the focused test only checks rooted, drive, UNC, backslash, and `file://` examples. Add cases
such as `artifacts/../outside.nmp2` and `artifacts/topic/./note.nmp2`, and enforce the same
portable-key grammar at every entry/import boundary before any side effect.

## Positive Observations

- `MapArtifactKey` validates again immediately before mapping and uses `PathContainment` to
  keep the resulting physical path beneath `MemoriesRoot`.
- Generated persistent and ephemeral keys use forward slashes and do not expose workspace
  roots; ephemeral projections now return the canonical `~name` address.
- The new focused test verifies the key shape for both persistent and ephemeral store writes.

## Verification Evidence

- Reviewed the complete `96c25e7..a6cfb61` diff and the Task 4 requirements in the plan/spec.
- `git diff --check 96c25e7 a6cfb61`: passed.
- Repository search at HEAD still finds obsolete URI terminology in living architecture
  guidance and legacy/test surfaces; those remaining matches are outside this three-file diff
  and require the planned migration cleanup rather than being silently retained.
- This package adds review artifacts only; source and test implementation files were not
  modified.

## Philosophy Check

- Early exit: **PASS** for the key mapper's validation-before-mapping flow.
- Parse don't validate: **NOT MET**; the typed read converts a canonical address back to a raw
  string and reparses it.
- Atomic predictability: **NOT MET**; model-level artifact keys can remain traversal-shaped even
  though one physical mapping path rejects them.
- Fail fast: **PARTIAL**; physical mapping fails closed, but entry construction does not reject
  all forbidden key forms.
- Intentional naming: **PARTIAL**; `ArtifactKey` is clearly named, but raw `subject`/`scope`
  resolution remains on the typed artifact-read path.
