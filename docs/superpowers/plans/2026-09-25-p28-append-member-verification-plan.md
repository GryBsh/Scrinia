# P28 — Append-Member-Verification Seam — Implementation Plan

**Parent spec:** `docs/superpowers/specs/2026-09-25-p28-append-member-verification-seam.md`
**Phase:** P28 (twenty-eighth god-store seam extraction; append recovery-orchestration phase).
**Shape:** new instance owner + field + constructor wiring + one method deletion + four call-site repoints. Single wave, sequential.

## Workstreams

- W1 — create `src/Scrinia.Core/AppendMemberVerifier.cs` with the instance owner.
- W2 — declare the `_memberVerifier` field in `FileMemoryStore.cs`.
- W3 — construct `_memberVerifier` in the store constructor.
- W4 — delete `VerifyAppendMemberAsync` from `FileMemoryStore.cs`.
- W5 — repoint the four external call sites to `_memberVerifier.VerifyAsync(...)`.
- W6 — build + seam test verification.
- W7 — full `dotnet test Scrinia.sln` verification.

W1 → W2 → W3 → W4 → W5 → W6 → W7 sequential. No parallelism benefit — execute sequentially in one clean pass.

## W1 — Create `AppendMemberVerifier`

Create `src/Scrinia.Core/AppendMemberVerifier.cs` (new file) with the verbatim instance owner class from the parent spec (the `VerifyAsync` method: `_injectAppendFault` hook field + constructor, `VerifyAsync` body with `VerifyAppendMemberAsync`'s `InjectAppendFault(...)` → `_injectAppendFault(...)`).

## W2 — Declare the field

Edit `src/Scrinia.Core/FileMemoryStore.cs`. After the `_appendJournalWriter` field declaration (:155), declare:

```csharp
    private readonly AppendMemberVerifier _memberVerifier;
```

## W3 — Construct in the store constructor

Edit `src/Scrinia.Core/FileMemoryStore.cs`. After the `_appendJournalWriter` construction block (:319–:321), add:

```csharp
        _memberVerifier = new AppendMemberVerifier(
            InjectAppendFault);
```

## W4 — Delete the verification method

Delete `VerifyAppendMemberAsync` from `src/Scrinia.Core/FileMemoryStore.cs` (:3299–:3313).

## W5 — Repoint the external call sites

Repoint the four external call sites in `src/Scrinia.Core/FileMemoryStore.cs`:
- :2258 `await VerifyAppendMemberAsync(createTransaction.NewArtifact, appendOperationId!, ct);` → `await _memberVerifier.VerifyAsync(createTransaction.NewArtifact, appendOperationId!, ct);`
- :2259 `await VerifyAppendMemberAsync(createTransaction.NewSidecar, appendOperationId!, ct);` → `await _memberVerifier.VerifyAsync(createTransaction.NewSidecar, appendOperationId!, ct);`
- :3107 `await VerifyAppendMemberAsync(transaction.NewArtifact, transaction.OperationId, ct);` → `await _memberVerifier.VerifyAsync(transaction.NewArtifact, transaction.OperationId, ct);`
- :3108 `await VerifyAppendMemberAsync(transaction.NewSidecar, transaction.OperationId, ct);` → `await _memberVerifier.VerifyAsync(transaction.NewSidecar, transaction.OperationId, ct);`

Confirm via grep that no remaining bare `VerifyAppendMemberAsync(` call site is left in the file after the edit.

## W6 — Build + seam verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AppendMemberVerifier"
   ```
   Confirm `Passed!` / 0 failed. (VSTest `--filter` does not accept `|`; if a two-sided filter is used, run each side separately or the behavioral `--filter "FullyQualifiedName~Append"` instead.)

## W7 — Full verification (mandatory)

Full `dotnet test Scrinia.sln` (run single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final `Passed!`/`Failed!` summary line for each of the three test projects. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.

## Acceptance

- `AppendMemberVerifier` owns the append member verification leaf; `FileMemoryStore` retains only the repointed call sites within its write-path methods.
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the owner.
- Full suite green.
