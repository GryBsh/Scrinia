# P0 Naming/Casing Design Spec (Approach A: MemoryNaming as Single Home)

> Status: proposed 2026-09-23. Approach A — `MemoryNaming` as single home for naming/casing.

## Context

Naming and casing logic is currently split across `FileMemoryStore`, `MemoryNaming`,
`PathParser`, and `MemoryAddress`:

- `FileMemoryStore.ResolveAddress` (`src/Scrinia.Core/FileMemoryStore.cs:436`) and
  `FormatQualifiedName` (`:428`) own address→(scope, subject) resolution and display.
- `SanitizeName` / `SanitizeNameCore` (`:450` / `:512`) delegates to
  `MemoryNaming.SanitizeSegment`, but case-insensitive matching is re-implemented
  inline at every call site (`SanitizeName(x).Equals(SanitizeName(y), OrdinalIgnoreCase)`).
- `MemoryNaming` (`src/Scrinia.Core/MemoryNaming.cs`) owns scope construction
  (`TryBuild` / `BuildScopedTopicScope`), segment sanitization (`TrySanitizeSegment`),
  legacy-colon gate (`RejectLegacyColonSyntax`), ephemeral helpers, and the
  recovery bypass contract (`DiscoverMemoriesRecursive` / `ParseScopeLabel`, `:149-161`, `:371-397`).
- `PathParser.Parse` (`src/Scrinia.Core/PathParser.cs:51`) validates (`\`, `:`, `..`,
  control chars) and matches entity types via a caller-supplied set
  (`entityTypes.Contains(current)`, `:91`) with no comparer contract.
- `MemoryAddress` (`src/Scrinia.Core/Models/MemoryAddress.cs`) validates
  (`TryParse`, `IsLegalSegment`, `ContainsPhysicalOrUriSyntax`) and canonicalizes
  (`~x`, `/temp/x` → ephemeral), but duplicates URI/physical-syntax clauses
  already enforced by `PathParser`.

Result: two sanitizers that can drift, N case-fold call sites with no single
comparer, two URI-guard clause lists, and entity matching whose casing depends on
whatever set the caller happened to pass.

## Decision

**Approach A: `MemoryNaming` as single home.** Move the bodies of
`ResolveAddress`, `FormatQualifiedName`, and `SanitizeName` into `MemoryNaming`;
add `NameEquals`, a shared `Comparer`, and `ResolveExistingName`; narrow
`MemoryAddress` to validation-only; unify error handling; keep recovery bypasses
and frozen surfaces intact.

Alternatives rejected: B (freeze duplicates in place), C (new NamingService
abstraction — over-engineering; a pure static home already exists).

## Phased track context

- **P0 (this spec):** naming/casing single home. Prerequisite for everything below.
- **P1:** address-surface conformance on top of P0 primitives.
- **P2:** read/write path unification through P0 resolution.
- **P3:** bundle/migration round-trip using P0 canonical forms.
- **P4:** plugin/event boundary adopts P0 comparer + address kinds.
- **P5:** retention/maintenance sweeps reuse P0 recovery-safe matching.

P0 changes no wire, disk, or JSON shapes (source-only: moved bodies + new
`MemoryNaming` members) so P1–P5 can land independently.

## Frozen constraints

- **NMP/2** artifact format unchanged (`NMP_SPEC.md`).
- **`.scrinia/` layout** unchanged (`memories/`, `skills/`, `agent/`, sidecars).
- **CLI** stays single-file surface (`src/Scrinia/Commands/ScriniaCommands.cs`,
  `--workspace-root` pattern; no remote mode).
- **MCP 12+11 disjoint:** 12 tools (`guide` + remember/recall/search/list/append/
  forget/compact/link/restore/facts) stay disjoint from the 11 CLI command forms.
- **Source-gen JSON** only (`ScriniaMcpJsonContext`, `CliJsonContext`,
  `ConfigJsonContext`, `PluginClientJsonContext`); no reflection serialization.

---

## 1. Architecture + Components

`MemoryNaming` (`src/Scrinia.Core/MemoryNaming.cs`) becomes the sole owner of
naming/casing. `FileMemoryStore` keeps thin delegating members (interface
stability); `MemoryAddress` narrows to validation-only.

| Component | Role after change |
|---|---|
| `MemoryNaming.ResolveAddress` (new; body moved from `FileMemoryStore:436-448`) | Sole address→(scope, subject) resolver |
| `MemoryNaming.FormatQualifiedName` (new; body moved from `:428-431`) | Sole display formatter (`local:x` → `/x/subject`) |
| `MemoryNaming.NameEquals` + `MemoryNaming.Comparer` (new) | Sole case rule: `NameEquals(a, b)` sanitizes both sides via `TrySanitizeSegment` then compares with `StringComparer.OrdinalIgnoreCase`; `Comparer` is that same `static readonly` instance, also used for any dictionary/`HashSet` construction over names |
| `MemoryNaming.ResolveExistingName` (new; two overloads: `(IReadOnlyList<ArtifactEntry> entries, MemoryAddress address)` and `(IReadOnlyList<ArtifactEntry> entries, string subject)`) | Sole read-back matcher: first entry in the supplied list's iteration order where `NameEquals(entry.Name, subject)` holds; null when none. No sorting, no last-match. |
| `MemoryAddress` (narrowed) | Validation-only: `TryParse`/`Parse`/`IsValid`/`Kind`; no scope building, no sanitizing |
| `SanitizeName` 3 categories (clarified; this row only is no behavior change — the `PathParser` wrap in §3 is an intentional behavior change) | (1) shared char rule, (2) scope-only structural rejections, (3) leaf leniency — see below |
| `FileMemoryStore.ResolveAddress` / `FormatQualifiedName` / `SanitizeName` | Delegates to `MemoryNaming`; kept for `IMemoryStore` compat |

### SanitizeName 3 categories

1. **Shared char rule** — `TrySanitizeSegment`: strip `..`, fold `/` `\` to `_`,
   map portable-invalid chars to `_`, `Path.GetFileName` final. Scope segments
   and leaf/subject both call this one method directly (no forked copy)
   (Task 13 invariant, kept).
2. **Scope-only structural rejections** — live in `TryBuild`, never in the leaf:
   `:` / `\` / `.` / `..` raw segments, empty topic, trailing `.`/` ` (filesystem
   trims),    `NUL` device name. Substitution cannot repair aliasing, so these throw
   `ArgumentException` on the write path (`BuildScopedTopicScope`) versus
   return-false ("matches nothing") on the filter-listing path
   (`TryBuildScopedTopicScope` — see §2 leniency boundary).
3. **Leaf leniency** — `SanitizeNameCore` stays throw-only-on-empty
   (`ArgumentException` via `SanitizeSegment`, same as today). No trailing-dot/
   NUL rejection: artifact paths append `.nmp2`/`.meta.json` after sanitizing
   (`notes.` → `notes..nmp2`, distinct — see the measured note on
   `FileMemoryStore.SanitizeNameCore`, `:495-512`), and leaf matching runs against
   names read back from disk, where throwing would poison a whole scope read or
   abort the orphan sweep mid-`SaveIndex`.

### Call-site rewrites (mechanical)

- Every `SanitizeName(a).Equals(SanitizeName(b), OrdinalIgnoreCase)` becomes
  `MemoryNaming.NameEquals(a, b)` (`IMemoryStore:132-134`, `EntityTypingEventHandler:52-53`,
  `FileMemoryStore` entry matchers `:2135`, `:2206`, `:2335`, read barrier `:467-468`).
- Every ad-hoc `entry.Address == address || entry.Name.Equals(subject, OrdinalIgnoreCase)`
  read matcher becomes `MemoryNaming.ResolveExistingName(entries, address)` (or the
  `string subject` overload where only a subject is in hand).

---

## 2. Data flow

### Write (`remember` / `append` / `WriteAsync`)

1. Facade parses raw string → `MemoryAddress` (validation-only).
2. `MemoryNaming.ResolveAddress` → (scope, subject) via `TryBuild` + leaf sanitize.
   Unconstructible topic throws (write must not resolve to something else).
3. Store writes sidecar + artifact under the single scope key. No second key per
   directory (the Task 13 aliasing fix, preserved).

### Read (`recall` / `search` / `ReadAsync`)

1. `MemoryNaming.ResolveAddress` → (scope, subject).
2. Index lookup via `MemoryNaming.ResolveExistingName` (stored casing travels
   forward; `SanitizeName` never folds case, so the comparer is the casing rule).
3. Leniency boundary: single-address `ResolveAddress` throws `ArgumentException`
   on an unconstructible topic (write must not resolve to something else; a
   validated `MemoryAddress` that still fails scope construction is a caller bug).
   The never-throws `TryBuildScopedTopicScope` → "matches nothing" (empty list /
   null read, never an exception) applies ONLY to scope-filter listing paths
   (`ListScoped` filters, exclude lists), never to single-address resolve.

### Display

`MemoryNaming.FormatQualifiedName` only: `local` → bare subject, `local:{topic}` →
`/{topic}/{subject}`, `ephemeral` → bare subject with NO `~` (facade prepends `~`,
existing convention kept — mirrors `FileMemoryStore:428-431` verbatim move). No other renderer concatenates scope strings.

### Recovery (bypass preserved)

- `FileMemoryStore.DiscoverMemoriesRecursive` (`:4512`) and
  `MemoryNaming.ParseScopeLabel` (`:392`) **stay outside** `TryBuild` validation.
  Both recover scopes already on disk / already listed — canonical by construction —
  and both feed `DiscoverTopics → ResolveReadScopes` plus the forget-by-URI delete
  loop. Validating there would make a memory in a rejected scope **undeletable**
  (loop silently skips dropped scopes). Do not tighten these two sites.

---

## 3. Error handling

- **`PathParser` `OrdinalIgnoreCase` wrap (intentional behavior change):** `Parse(rawPath, entityTypes)` copies
  the caller set once (`new HashSet<string>(entityTypes, StringComparer.OrdinalIgnoreCase)`,
  caller set untouched) before the pairwise walk. Entity matching (`goal/G-5`) is then case-insensitive
  regardless of what the caller passed; tags/leaf keep original casing.
- **`IsUriLike` 6-clause unify:** single `MemoryNaming.IsUriLike(value)` predicate —
  `\`, `:`, `?`, `#`, `%`, leading `//` (+ drive-letter `X:` subsumed by `:`) —
  consumed by both `PathParser.Parse` and `MemoryAddress.ContainsPhysicalOrUriSyntax`
  (which becomes a one-line delegate). One clause list, no drift.
- **Legacy colon gate:** `RejectLegacyColonSyntax` stays the single gate for bare
  `topic:subject`; `PathParser` (`:` reject, `:63-64`) and `MemoryAddress`
  (`:` reject via `IsUriLike` + `IsLegalSegment`) keep their positional guards.
  Gate message keeps the `/{topic}/{subject}` suggestion.
- **Frozen surfaces:** `MemoryAddress` JSON converter, `MaxUtf8Bytes`, ephemeral
  alias canonicalization (`IsEphemeralAlias`/`CanonicalizeEphemeralAlias` prefix-only
  folding), `FormatScopeLabel`/`ParseScopeLabel` round-trip (incl. known
  `local:local` / `local:ephemeral` collision), `SplitCommaSeparated` /
  `NormalizeFilterList` null-shape contract — all unchanged.

---

## 4. Testing + rollout

### Matrix

| Test | Pins |
|---|---|
| Round-trip: `ResolveAddress` → `FormatQualifiedName` → `Parse` → `ResolveAddress` for `local`, `local:{topic}`, nested `local:a/b`, ephemeral `~x`/`/temp/x` | Canonical display preserved |
| Comparer: `NameEquals` vs. old inline `Sanitize+OrdinalIgnoreCase` over fixtures incl. mixed-case, sanitized-char variants, unicode | Single rule, no behavior delta |
| `ResolveExistingName`: hit (stored casing returned), miss (null), multi-candidate first-match | Read matcher parity |
| `PathParser` entity match `GOAL/g-5` vs `goal/G-5` with case-sensitive caller set | `OrdinalIgnoreCase` wrap |
| `IsUriLike` 6-clause table (`\ : ? # % //` + `C:`) through both `Parse` and `TryParse` | Unified rejection |
| Legacy colon `topic:subject` through facade/store/parser | Gate message + suggestion |
| Keep `CaseSensitive*` pins green (`CaseSensitiveFileStoreTests`, `CaseSensitiveMcpAppendTests` on `CaseSensitiveDirectory` volumes) | No casing regression on case-sensitive FS |
| Keep `DualModeParseTests` green (leading/trailing/`//` collapse) | No traversal-rule regression |

### Rollout (mechanical, no JSON changes)

1. **Move-body:** copy `ResolveAddress` / `FormatQualifiedName` / `SanitizeNameCore`
   bodies into `MemoryNaming`; add `NameEquals`/`Comparer`/`ResolveExistingName`/`IsUriLike`.
2. **Delegate:** `FileMemoryStore` members become one-line delegates; `PathParser`
   and `MemoryAddress` call `IsUriLike`; matchers call `NameEquals`/`ResolveExistingName`.
3. **Swap:** update `IMemoryStore` default matcher (`:132-134`), `EntityTypingEventHandler`,
   read barrier, sidecar/orphan-sweep matchers to the new home.
4. **Test:** full matrix above + `dotnet build Scrinia.sln` + `dotnet test Scrinia.sln`
   green. `TestHelpers.StoreScope` isolation for all new tests (AsyncLocal
   workspace/store/ephemeral/`SessionBudget` redirect). No JSON shape changes —
   source-gen contexts untouched, `MemoryAddressJsonConverter` untouched.

## Out of scope

- Changing `.scrinia/` on-disk layout, NMP/2 encoding, or vector-dir placement.
- Changing MCP/CLI surface shapes or adding tools/commands.
- Case-folding stored names on disk (matching stays case-insensitive; storage stays
  case-preserving).
- Fixing the `FormatScopeLabel` `local:local` collision (documented, kept).
