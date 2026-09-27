# P34 — Restore-Journal Empty-Entries Guard — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-27-p34-restore-empty-entries-guard.md`
**Phase:** P34 (thirty-fourth robustness seam; close the parked Task-6 bare-`complete` recovery-journal finding).
**Shape:** one pre-`SubjectOf` guard in `InspectCandidate` + one regression test. Single wave, sequential.

## Workstreams

- W1 — add the empty-entries restore guard in `src/Scrinia.Core/RecoveryDiscovery.cs` `InspectCandidate`.
- W2 — add a regression test in `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs`.
- W3 — build + targeted recovery-verification.
- W4 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Add the guard in InspectCandidate

Edit `src/Scrinia.Core/RecoveryDiscovery.cs`, `InspectCandidate` (lines 291-325). Insert, immediately after the `adapter.ConsumeParsed` line and before the `SubjectOf(envelope)` line:

```csharp
            if (envelope.TypedBody is RestoreRecoveryBody restore && restore.Entries.Count == 0)
                throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                    "Restore recovery evidence has no entries.", evidencePath);
```

This throws before `SubjectOf`'s `.First()` can run on an empty sequence. `RecoveryEnvelopeException` derives from `IOException` and is caught by the existing `catch (RecoveryEnvelopeException exception)` at line 312 → `Classify(Malformed)` → `RecoveryBlockReason.Malformed` (line 434). No other lines in the file change.

## W2 — Regression test

Add a `[Fact]` to `tests/Scrinia.Tests/Store/JournaledRestoreRecoveryTests.cs` that writes a **completed** restore journal whose `entries` array is empty and asserts recovery blocks cleanly rather than crashing:

- Build a scope via `new TestHelpers.StoreScope()`; derive `storeDirectory = store.GetStoreDirForScope(Scope)` and `journalPath = Path.Combine(storeDirectory, ".restore-journal.json")`.
- Serialize a valid completed restore journal with an empty `entries` list to `journalPath` (reuse `RestoreJournalCodec` + `RestoreJournalJsonContext`/`RecoveryEnvelopeCodec` to emit `{"format":"scrinia.restore-journal","version":1,"state":"completed","transactionId":"<guid>","scope":<Scope>,"entries":[]}` with valid `RecoveryEvidenceVerifier`-acceptable bytes; mirror the fixture layout in `RecoveryTestFixtures.BuildLegacyRestoreJournal` but with `"entries":[]`).
- `Action load = () => store.LoadIndex(Scope);` assert `load.Should().Throw<RestoreRecoveryException>()` (or the recovered block type) and that it does **not** throw `InvalidOperationException` / `Exception` — i.e. it fails closed as a malformed-block recovery exception, never crashes.

Prefer constructing the journal through the codec so the `RecoveryEvidenceVerifier` integrity boundary is exercised exactly as with a real journal. If the existing test class's helpers (`WritePreparedJournal`, `ReadRestoreEvidence`, `CreateEntry`) can't produce an empty-entries completed journal directly, add a small local helper in the test. Keep the test within the `JournaledRestoreRecoveryTests` class so it shares `Scope`, `TestHelpers.Store`, and the fixture helpers.

## W3 — Build + targeted recovery verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Restore"`.
   Confirm `Passed!` / 0 failed (this includes the new test and the pre-existing `CompletedJournalDestinationIntegrityFailure` / `ImportTopicEntries` completed-journal tests).

## W4 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- A malformed/empty-entries `complete` restore journal fails closed as a clean recovery block during discovery — never an uncaught exception, never a half-replayed claim.
- `RecoverScopeUnsafe`, `RecoverCommittedCleanup`, and all public recovery surfaces unchanged; `CompletedJournalDestinationIntegrityFailure_IdentifiesTheRetainedJournal` still passes.
- Full suite green.
