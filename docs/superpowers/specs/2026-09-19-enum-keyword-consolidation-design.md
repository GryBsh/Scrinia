# Design Spec: Enum & Keyword Consolidation (Tasks C + G)

**Date:** 2026-09-19
**Status:** Approved design — implementation plan is the next step
**Scope:** Within-assembly enum/keyword deduplication (C) and published status/availability vocabulary consistency (G). Both are design-cost remediations from the design review carried out 2026-09-19. Task A (doc-comment landmine) and Task B (housekeeping) are already committed separately and are out of scope here.

## Context

The design review found that failure/availability/state concepts are re-declared as near-duplicate enums and keyword helpers inside `Scrinia.Core`, with no single source of truth and no compiler enforcement. This review deliberately EXCLUDES:
- Cross-assembly enum pairs (Core↔Abstractions): these are boundary-forced (Abstractions references Core, Core cannot reference Abstractions without a cycle) and are handled by a drift-invariant test, not a merge.
- The five overlapping recovery enums (`RecoveryBlockReason`, `RecoveryEnvelopeFailure`, `MaintenanceProtectionReason`, `RestoreRecoveryFailure`, `MultiMemoryFailure`): they span different lifecycle layers; merging them is the "consolidate on sight" trap and is explicitly out of scope.
- The different-concept retry `3`s (`RetryOptions.MaxRetries`, `McpPluginHost.MaxRestarts`, `FileMemoryStore` local const): intentionally NOT merged.

### The dependency-direction correction (important)
The earlier design review assumed the cross-assembly duplication was a defect. Verification showed the opposite: `Scrinia.Plugin.Abstractions` depends on `Scrinia.Core` (bare `using Scrinia.Core;` and `using Scrinia.Core.Models;` in `ITransactionalMemoryParticipant.cs` and `HookContexts.cs`). Core is the foundation. So each concept is *internal-in-Core / public-in-Abstractions* on purpose. The only genuinely identical cross-assembly pair is `TransactionalFailureCode` (13 members, same order) in both assemblies, bridged by `(int)` casts in `src/Scrinia/Services/TransactionalParticipantAdapter.cs:78,199-200,207,282`. `TransactionalParticipantLifecycleState` (Core, 5 members incl. `Prepared`) vs `TransactionalParticipantState` (Abstractions, 4 members, no `Prepared`) genuinely differ and must stay two enums.

---

## Task C — Within-assembly enum / keyword deduplication

### C1 — Collapse `RequireText` (11 definitions → 1)
Verified by grep: `RequireText` is defined 11 times across 4 files, all byte-identical:

```csharp
private static string RequireText(string value, string parameterName) =>
    string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
        : value;
```

Definition sites:
- `src/Scrinia.Core/MultiMemoryTransaction.cs:123,195,295,348,461` (5)
- `src/Scrinia.Core/MultiMemoryTransactionRecord.cs:65,93` (2)
- `src/Scrinia.Core/RecoveryContracts.cs:108,263` (2)
- `src/Scrinia.Core/WholeMemoryTransaction.cs:59,214` (2)

33 call sites reference these local definitions (e.g. `MultiMemoryTransaction.cs:81-84`, `RecoveryContracts.cs:91-94`, `WholeMemoryTransaction.cs:20-22,196-199,244-248`).

**Design:** Add one `internal static string RequireText(string value, string parameterName)` to `Scrinia.Core` (a new small file `src/Scrinia.Core/ContractValidation.Text.cs` or fold into the existing `ContractValidation` class referenced at `ITransactionalMemoryParticipant.cs`). Since all call sites are already in namespace `Scrinia.Core` (MultiMemoryTransaction.cs:4, MultiMemoryTransactionRecord.cs, RecoveryContracts.cs, WholeMemoryTransaction.cs all `namespace Scrinia.Core`), the call sites need no `using` change — they resolve to the single static method by unqualified name. Delete the 11 local definitions. Keep the call sites pointing at the shared method.

**Risk:** Zero behavioral change (identical implementations). Verify all 11 definition sites are truly in `namespace Scrinia.Core` and that no call site is in a different namespace needing a `using`. If any call site is in another namespace, add a `using Scrinia.Core;` there.

### C2 — Hoist the transactional-retry default `3` (2 sites → 1)
Two `TransactionalRetryPolicy` overloads both default `maxAttempts = 3`:
- `src/Scrinia.Core/TransactionalRetry.cs:10` (internal, Core)
- `src/Scrinia.Plugin.Abstractions/ITransactionalMemoryParticipant.cs:41` (public, Abstractions)

Both represent the same concept: the default attempt budget for a transactional retry policy.

**Design:** Add a single public const in Core: `public const int DefaultMaxAttempts = 3;` in namespace `Scrinia.Core` (e.g. in `TransactionalRetry.cs`). Change both defaults to `int maxAttempts = DefaultMaxAttempts`. Core's overload references it directly (same assembly). Abstractions references it via the existing `using Scrinia.Core;` (Core is the base assembly; Abstractions can consume Core's public const; default-parameter values must be compile-time constants and public const satisfies this).

Do NOT touch:
- `src/Scrinia.Core/Resilience/RetryPolicy.cs:13` — `RetryOptions(MaxRetries = 3)` — embedding/HTTP retry, different concept.
- `src/Scrinia/Services/McpPluginHost.cs:49` — `MaxRestarts = 3` — process restarts, different concept.
- `src/Scrinia.Core/FileMemoryStore.cs:1100` — `const int maxAttempts = 3` — store-local, different concept.

**Risk:** Low. Confirm the validation guard `maxAttempts is < 1 or > 8` (Core, line 13) still reads cleanly with the const.

### C3 — Compile-time drift-invariant test for the one identical cross-assembly pair
The only identical cross-assembly enum is `TransactionalFailureCode` (13 members, same order in both `Scrinia.Core` and `Scrinia.Plugin.Abstractions`), bridged by `(int)` casts. Adding a member to one without the other desyncs silently at the cast site.

**Design:** Add one test in `tests/Scrinia.Tests/TransactionalParticipantContractTests.cs` (existing file already uses `Enum.GetValues<TransactionalParticipantState>()` at lines 104, 116 as a template):

```csharp
[Fact]
public void TransactionalFailureCode_Core_And_Abstractions_AreIdentical()
{
    Assert.Equal(
        Enum.GetNames<Scrinia.Plugin.Abstractions.TransactionalFailureCode>(),
        Enum.GetNames<Scrinia.Core.TransactionalFailureCode>());
}
```

This is the minimal, correct enforcement of the one rule the assembly boundary requires — without attempting to merge enums that have different lifecycles.

**Risk:** Low. The test pins the invariant; if a future dev adds a member to one side, the build fails.

---

## Task G — Published status/availability vocabulary consistency

Blast radius note: `McpResponse` and `SharedEnvelope` are PUBLISHED wire surfaces that LLM consumers read. String spellings on them are contracts. This task changes spellings only where it is safe (no pinned test) and adds predicates rather than renaming wire strings that are already consumed.

### G1 — Collapse the duplicated availability enum
`SharedAvailability` (`src/Scrinia.Mcp/Shared/SharedDtoContracts.cs:5`) and `SharedBaseAvailability` (`src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs:14`) are the identical 4-member set `{ Available, Unavailable, Stale, Conflicted }`. The DTO carries `[JsonStringEnumMemberName]` wire spellings; the Core one does not.

**Design:** Make `SharedBaseAvailability` the single source of truth. Add `[JsonStringEnumMemberName("available")]` etc. attributes to Core's `SharedBaseAvailability` (matching the current DTO spellings), then remove `SharedAvailability` from `SharedDtoContracts.cs` and repoint `SharedBaseSummary.Availability` (line 37) and `RefreshResult.Availability` (line 58) to `SharedBaseAvailability`. Update the two consumers in `SharedServer.cs:104,106` (`SharedAvailability.Unavailable` → `SharedBaseAvailability.Unavailable`).

**Risk:** Medium — this changes a published DTO type name (`SharedAvailability` → `SharedBaseAvailability`). Confirm no external plugin or golden fixture references `SharedAvailability`. The verification grep showed no `Status.Should().Be("partial")`-style pin on the availability enum, so test risk is contained. If any golden YAML fixture serializes the availability spelling, it stays identical (JSON member name unchanged), only the CLR type changes.

### G2 — Add status predicates to `SharedEnvelope`
`SharedEnvelope` (`src/Scrinia.Mcp/Shared/SharedServer.cs:123-156`) has a `Status` string (`"success"` default, `"partial"`, `"error"`) but NO `IsError`/`IsWarning`/`IsSuccess` predicate, whereas `McpResponse` has `IsError` (line 66) and `IsWarning` (line 74). Consumers of both envelopes must spell status themselves.

**Design:** Add to `SharedEnvelope`:
```csharp
public bool IsError => Status is "error";
public bool IsWarning => Status is "partial";
```
This mirrors `McpResponse`'s predicate shape without renaming any wire string. The `Partial(...)` factory at line 142 already sets `Status = "partial"`; `IsWarning` now expresses that intent. Do NOT rename "partial" to "success_with_warning" — that is a published wire string in use.

**Risk:** Low. Purely additive; no existing behavior changes.

### Out of scope (explicitly deferred)
- Renaming `SharedEnvelope.Partial`'s `"partial"` to `"success_with_warning"` (or vice versa): these are two separate published envelopes; unifying the term is a wire-contract decision best taken as a follow-up with a migration plan, not this pass.
- Merging the five overlapping Core recovery enums (see scope exclusion above).
- `ConfigurationJsonResult.Status` stringly-typed downgrade: correct pattern already exists via `Code()`; the CLI JSON downgrade is a separate, larger change.

---

## Verification (post-implementation)
1. `dotnet build Scrinia.sln` must pass.
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` must pass — especially the new C3 invariant test and all existing `TransactionalParticipantContractTests`.
3. Grep-confirm zero remaining `RequireText` local definitions outside the one shared method.
4. Grep-confirm no remaining references to `SharedAvailability` (the DTO enum) after G1.
5. Confirm the three out-of-scope `3`s in Task C2 are untouched.

## Success criteria
- `RequireText` has exactly one definition in `Scrinia.Core`.
- The transactional-retry default `3` has exactly one source (`DefaultMaxAttempts`).
- The identical cross-assembly `TransactionalFailureCode` pair is enforced by a compile-time invariant test.
- `SharedEnvelope` has `IsError`/`IsWarning` predicates; `SharedAvailability` no longer exists as a duplicate of `SharedBaseAvailability`.
- No behavioral change to any recovery/transaction path.
