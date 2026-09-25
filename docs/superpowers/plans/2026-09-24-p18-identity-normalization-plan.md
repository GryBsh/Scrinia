# P18 — Entry-Identity / Normalization Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-24-p18-identity-normalization-seam.md`
**Phase:** P18 (eighteenth god-store seam extraction; first pure-transform seam).
**Shape:** one new static owner (`ArtifactIdentityMapper`) + four store edits. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/ArtifactIdentityMapper.cs` with the three moved methods.
- W2 — `MapArtifactKey`: repoint `ValidateArtifactKey` → the owner.
- W3 — repoint the ~9 external call sites (`BuildArtifactKey` ×4, `CanonicalizeEntryForScope` ×5, `ValidateArtifactKey` ×1).
- W4 — build + seam test verification.
- W5 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `ArtifactIdentityMapper`

Create `src/Scrinia.Core/ArtifactIdentityMapper.cs` (new file) with the verbatim owner class from the spec (the three pure transforms `BuildArtifactKey`, `CanonicalizeEntryForScope`, `ValidateArtifactKey`; `CanonicalizeEntryForScope` calls `MemoryNaming.FormatQualifiedName`).

## W2 — Store wiring: `MapArtifactKey`

Single edit in `src/Scrinia.Core/FileMemoryStore.cs`. In `MapArtifactKey` (:3738–:3748), change `ValidateArtifactKey(artifactKey);` to `ArtifactIdentityMapper.ValidateArtifactKey(artifactKey);`. Leave the `MemoriesRoot` migration barrier and `PathContainment.IsWithin` check untouched.

## W3 — Store wiring: repoint the call sites

Seven edits in `src/Scrinia.Core/FileMemoryStore.cs`, each a one-line call-site replacement:

- `BuildArtifactKey` → `ArtifactIdentityMapper.BuildArtifactKey`
  - :616 (`ArtifactPath`), :3554 (`BuildDerivedEntry`), :3633 (`ProjectEphemeralEntry`), :4045 (`ImportTopicEntriesCore`)
- `CanonicalizeEntryForScope` → `ArtifactIdentityMapper.CanonicalizeEntryForScope`
  - :1212 (`LoadIndexUnsafe`), :1481 (`EnumerateRawForMigration`), :1711 (`SaveIndexUnsafe`), :1778 (`UpsertCore`), :2008 (`TryUpdateEntry`)
- `ValidateArtifactKey` → `ArtifactIdentityMapper.ValidateArtifactKey`
  - :3740 (`MapArtifactKey`)

Each edit is a one-word prefix change (`ArtifactIdentityMapper.`) on an existing call. Confirm via grep that no remaining bare `CanonicalizeEntryForScope(` / `BuildArtifactKey(` / `ValidateArtifactKey(` call site is left in the file after these edits (the moved definitions at :3703–:3764 are deleted in W4's file cleanup — see W4 below; actually the methods are removed in W4). Note: the internal self-call at :3733 disappears with the method move.

## W4 — Remove the moved methods

Delete the three moved method bodies from `FileMemoryStore.cs`: `BuildArtifactKey` (:3703–:3713), `CanonicalizeEntryForScope` (:3715–:3736), `ValidateArtifactKey` (:3750–:3764). Leave `MapArtifactKey` (:3738–:3748) and `ArtifactPath` (:615–:616) in place.

## W5 — Build + verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ArtifactIdentityMapper|FullyQualifiedName~CanonicalizeEntryForScope|FullyQualifiedName~BuildArtifactKey|FullyQualifiedName~ValidateArtifactKey"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `ArtifactIdentityMapper` owns the entry-identity/normalization group; `FileMemoryStore` retains only `MapArtifactKey`/`ArtifactPath` (which delegate to the owner).
- No new public API; `IMemoryStore` surface unchanged.
- Full suite green.
