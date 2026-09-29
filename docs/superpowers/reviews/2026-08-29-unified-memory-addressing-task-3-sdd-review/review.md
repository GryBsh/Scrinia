# SDD Review Package — Task 3

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**BASE:** `dc72a53`  
**HEAD:** `9c14922`  
**Scope:** Task 3, `ArtifactEntry` and core contract typing

## Overall assessment

**REQUEST_CHANGES**

The change adds typed address members and request types, but it does not complete the breaking contract migration required by Task 3. Public legacy URI/name constructors and raw callback/resolver contracts remain, and the new address-filter properties are discarded by the adapter overloads.

## Findings

### Major issues

#### T3-001 — Public `ArtifactEntry` URI/name compatibility surface remains

**Location:** `src/Scrinia.Core/Models/ArtifactEntry.cs:76-103` (HEAD)

The public compatibility constructor still accepts raw `Name` and `Uri`, and the public `Name` and `Uri` properties remain available. This directly contradicts the Task 3 requirement to replace rather than alias the old members and to expose no public `ArtifactEntry.Uri`. It also allows callers to construct an entry with independently supplied logical identity and artifact data, undermining the single `MemoryAddress`/`ArtifactKey` contract. The contract test does not check `ArtifactEntry` for `Uri`, so it cannot catch this regression.

#### T3-002 — `IPluginStore` still exposes raw and URI-resolving contracts

**Location:** `src/Scrinia.Core/IPluginStore.cs:21-28` (HEAD)

`ResolveArtifactAsync(string nameOrArtifact)` and the raw `(string subject, string scope)` `ReadArtifactAsync` remain public, while the new typed read method is only a default adapter back to the raw method. Task 3 explicitly requires plugin read callbacks to accept `MemoryAddress` and removes URI resolver contracts. Any plugin can therefore continue to consume URI-shaped or independently scoped strings, and the typed contract does not force implementations to migrate. This is an incomplete migration, not merely an implementation detail reserved for Task 4.

#### T3-003 — Event sinks remain required to implement raw string callbacks

**Location:** `src/Scrinia.Core/IMemoryEventSink.cs:12-20` (HEAD)

The new `MemoryAddress` overloads are default methods that immediately convert the address back to a string and delegate to the old abstract callbacks. Implementations are still required to implement the raw `qualifiedName` methods and receive no typed value. Consequently, Task 3's required typed event contract is not enforced, and canonical address information is reintroduced as an untyped string at the event boundary.

#### T3-004 — Typed address filters are silently ignored

**Location:** `src/Scrinia.Core/Models/MemorySearchRequest.cs:4-14` and `src/Scrinia.Core/IMemoryStore.cs:213-217` (HEAD)

`MemorySearchRequest.Address` and `MemoryListRequest.Prefix` are declared as typed filters, but the `IMemoryStore` adapter methods discard them: `SearchAll(MemorySearchRequest)` constructs `new SearchQuery(request.Query)` and `ListAsync(MemoryListRequest)` forwards only scopes and excluded topics. A caller using either new address-bearing request shape receives an unfiltered result, violating the contract while appearing to use the typed API. The contract tests only inspect property types and do not exercise propagation.

## Positive observations

- `ArtifactEntry` now has the intended `MemoryAddress Address` and opaque `string ArtifactKey` properties.
- `MemorySearchRequest` and `MemoryListRequest` centralize the new request shapes instead of duplicating anonymous filter parameters.
- `ArtifactEntrySerializationTests` cover the new serialized address/key shape and preserve the existing fields-to-attributes migration behavior.
- The change adds focused reflection tests for several typed store and event members.

## Verification evidence

- Reviewed the complete `dc72a53..9c14922` diff and the Task 3 requirements in the specified plan.
- `git diff --check dc72a53..9c14922`: passed.
- The range changes nine files, including the planned core models/contracts and focused tests.
- No source or test files were modified by this review package; it adds only this review artifact.

## Philosophy check

- Early exit: not applicable to the contract-only findings.
- Parse don't validate: not met; raw compatibility constructors and resolver methods remain public.
- Atomic predictability: not met; typed request filters are accepted but ignored.
- Fail fast: not met; default contract bridges permit incomplete implementations and silently return unfiltered/default results.
- Intentional naming: not met for the coexistence of `ArtifactKey` with a public `Uri` alias.
