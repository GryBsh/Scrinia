# P34 — Restore-Journal Empty-Entries Guard — Design Spec

**Parent work:** closing the parked Task-6 bare-`complete` recovery-journal finding (M13, `docs/superpowers/specs/2026-09-06-review-remediation-design.md:113`; plan `docs/superpowers/plans/2026-09-06-wave-2-major.md` Task 11).

**Phase:** P34 (thirty-fourth seam/robustness extraction).
**Goal:** close the only live residual of the "bare-`complete` recovery-journal" finding — a malformed/empty-entries restore recovery envelope that currently escapes recovery discovery as an uncaught `InvalidOperationException` instead of a clean `RecoveryBlock`.

## Why this phase

The M13 half-replay risk ("bare `complete` without `prepared` replay leaves scope mid-recovery") is already closed by the converged envelope-lifecycle architecture: a `complete` restore journal is admitted as `Committed` and routed through `RecoverCommittedCleanup`, which content-verifies every entry and fails closed on any mismatch or missing file (proven intact by `CompletedJournalDestinationIntegrityFailure_IdentifiesTheRetainedJournal`). `RecoverScopeUnsafe` returns for a `Committed`/`CleanupPending` candidate before its `else`/`VerifyJournalDestinationsUnsafe` branch, which is therefore unreachable for a completed restore.

The one residual defect is a malformed restore envelope with **zero entries** (a degenerate "bare complete"). `RecoveryDiscoveryCoordinator.SubjectOf` (`RecoveryDiscovery.cs:330`) computes the restore subject via:

```csharp
RestoreRecoveryBody restore => "/" + restore.Entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).First().Name,
```

`Enumerable.First()` on an empty sequence throws `InvalidOperationException`. In `InspectCandidate` (`RecoveryDiscovery.cs:291-325`) this runs at line 305, **before** `adapter.ValidateForMutation` (`InspectCandidate:306`), which is the method whose `RestoreJournalCodec.ValidateAdmission` (`RestoreJournal.cs:236`, `journal.Entries is not { Count: > 0 }` → `Malformed`) would otherwise reject it. `InspectCandidate`'s catches at lines 312 (`RecoveryEnvelopeException`) and 319 (`IOException`/`UnauthorizedAccessException`/`ArgumentException`) do not match `InvalidOperationException`, and `Discover`'s catch (`RecoveryDiscovery.cs:43`, `IOException`/`UnauthorizedAccessException`/`ArgumentException`/`NotSupportedException`) does not either — so the exception escapes the discovery coordinator entirely as a crash rather than a clean recovery block. A malformed bare-complete restore journal must fail closed as a `Malformed` block, never claim half-replayed state, and never crash discovery.

## The seam

### Fix: `InspectCandidate` (`src/Scrinia.Core/RecoveryDiscovery.cs`)

Add an empty-entries guard for the restore envelope immediately before the `SubjectOf(envelope)` call in `InspectCandidate` (before line 305). The guard throws a `RecoveryEnvelopeException` with `RecoveryEnvelopeFailure.Malformed`; `InspectCandidate`'s existing `catch (RecoveryEnvelopeException exception)` at line 312 converts it into a clean `RecoveryBlock` via `Classify` (`RecoveryDiscovery.cs:428-435` → `RecoveryBlockReason.Malformed`).

```csharp
        try
        {
            RejectReparsePoint(evidencePath);
            RecoveryVerifiedEvidence evidence = ReadVerified(operation, evidencePath, evidenceDirectory, scope, cancellationToken);
            verified = evidence.Envelope;
            verifiedAccounting = evidence.Accounting;
            RecoveryEnvelopeDocument envelope = adapter.ConsumeParsed(in verified);
            if (envelope.TypedBody is RestoreRecoveryBody restore && restore.Entries.Count == 0)
                throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                    "Restore recovery evidence has no entries.", evidencePath);
            string subject = SubjectOf(envelope);
            envelope = adapter.ValidateForMutation(envelope, scope, evidencePath, subject, evidenceDirectory);
            candidates.Add(new RecoveryCandidate(operation, evidencePath, scope, subject,
                envelope.OperationId, envelope, envelope.IsLegacy)
            { Verification = verifiedAccounting });
            return null;
        }
```

`RecoveryEnvelopeException`, `RecoveryEnvelopeFailure`, and `RestoreRecoveryBody` all live in the `Scrinia.Core` namespace (same as `RecoveryDiscoveryCoordinator`), so no `using` is required. No other method, field, or signature in the file changes. The `RecoveryBody` base type and `TypedBody` property are unchanged; only a pre-`SubjectOf` guard for the restore arm is added.

## Frozen do-not-touch

`RecoverScopeUnsafe` / `ApplyPreparedJournalUnsafe` / `VerifyJournalDestinationsUnsafe` / `InvalidateRecoveredScopeUnsafe` / `TryDeleteCompletedJournalUnsafe` (`FileMemoryStore.cs`); `RecoverCommittedCleanup` and `IsLifecycleConsistent` (`RecoveryEnvelope.cs`); `RestoreJournalCodec.CreatePrepared` / `WithCompleted` / `ValidateAdmission` (`RestoreJournal.cs`); the `RestoreJournalState` enum (`Prepared`/`Completed`); `RecoveryDiscoveryResult` / `RecoveryBlock` / `RecoveryBlockReason` / `RecoveryCandidate` types; `AdmitNext` routing (`RecoveryDiscovery.cs:256-279`); the `CompletedJournalDestinationIntegrityFailure` fail-closed behavior asserted by the existing test; NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures; `LoadIndex` + recovery barrier; `GetStoreDirForScope`/`MemoriesRoot` migration gate; and all other durable-format recovery paths.

## Acceptance

- A malformed restore envelope with zero entries is rejected as a clean `Malformed` recovery block during discovery — it neither crashes discovery nor claims half-replayed state.
- A non-empty `complete` restore journal whose content does not match disk still fails closed (unchanged); the existing `CompletedJournalDestinationIntegrityFailure_IdentifiesTheRetainedJournal` test still passes.
- No change to `RecoverScopeUnsafe`, `RecoverCommittedCleanup`, or any public recovery surface.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted recovery filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Restore"
   ```
   Confirm `Passed!` / 0 failed. (Note: VSTest `--filter` does not accept `|`; if a two-sided filter is used, run each side separately or the behavioral `--filter "FullyQualifiedName~Restore"`.)
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
