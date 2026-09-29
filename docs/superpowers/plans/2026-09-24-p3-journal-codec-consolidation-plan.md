# P3 Journal/Transaction Codec Consolidation — Execution Plan

**Date:** 2026-09-24
**Phase:** P3 — Journal/Transaction Codec Consolidation
**Repository:** scrinia (`main`)
**Scope:** `src/Scrinia.Core/` — add `RecoveryPayload.cs`; edit `AppendJournal.cs`, `RestoreJournal.cs`,
`RecoveryEnvelope.cs`, `FileEntityTransactionCoordinator.cs`, `AppendTransaction.cs`,
`WholeMemoryTransactionCoordinator.cs`.
**Design record:** `docs/superpowers/specs/2026-09-24-p3-journal-codec-consolidation-design.md` (committed to `main`).

> This plan is gitignored. It is the work order for the coder; it is **not** committed. Only the
> spec is committed to `main`. Do **not** touch `docs/` beyond the spec, or anything outside the
> six files listed above (plus the one new file).

---

## Execution model: one clean pass

This is a single linear, behavior-preserving refactor — do **not** chunk it into multiple branches.
Three tasks, each ending on a build or test gate, then one commit.

```
Task 1: add RecoveryPayload      → build Scrinia.Core only (0 errors, no callers yet)
Task 2: route codecs + FromPair   → build Scrinia.sln (0 errors, grep-verify the inline mapping gone)
Task 3: verify + commit           → dotnet test Scrinia.sln fully green
```

### Guardrails for the whole pass

- **Preserve each codec's exception type, failure code, and message verbatim.** The wrappers are the
  recovery-contract boundary; only the pure primitives move. `AppendJournal.DecodePayload` keeps its
  `AppendJournalException(IntegrityFailed)`; `RestoreJournalCodec.DecodeAndVerify` keeps its
  `Malformed` wrapping and its `DecodeObserver.Value?.Invoke()` before decode; `RecoveryEnvelopeCodec`
  member verify keeps its `JsonException`; `FileEntity` keeps its `InvalidDataException`.
- **Do NOT change** `ConsumeParsed` signatures (`AppendJournalCodec` / `RestoreJournalCodec`),
  `ParseVerifiedEnvelope` (six-arg, `RecoveryVerification.cs:299-305`), the four state machines, the
  `McpResponse` `RECOVERY_BLOCKED` / `RECOVERY_REQUIRED` codes, or the JSON source-gen contexts.
  Read `/findings/b2-journal-convergence-accepted` first.
- **Do NOT merge `ArchivePlan` and `ArchivePairPlan`.** `ArchivePairPlan` stays status-less and keeps
  validating its required-path invariant; `ArchivePlan` stays the journal-carrying shape. Only the
  single conversion site is unified through `ArchivePlan.FromPair`.
- **Align the append validator, don't widen it.** `AppendJournal.IsSha256` moves from `Uri.IsHexDigit`
  (uppercase-admitting) to the shared lowercase-only check; the append journal only ever holds
  lowercase digests, so admitted documents are unchanged.
- **No new public API, no new source-gen context entry.** `RecoveryPayload` and `ArchivePlan.FromPair`
  are `internal`.

---

## Task 1 — Add `RecoveryPayload`

**Goal:** introduce the primitive owner, with **no callers wired yet.**

1. Create `src/Scrinia.Core/RecoveryPayload.cs`:

    ```csharp
    namespace Scrinia.Core;

    /// <summary>
    /// Shared binary-encoding primitives for the durable recovery codecs (AppendJournal, RestoreJournal,
    /// RecoveryEnvelope, FileEntity). Each codec keeps its own typed exception and message; this type
    /// owns the raw base64/SHA256 logic they all delegate to, so the primitives have a single definition.
    /// </summary>
    internal static class RecoveryPayload
    {
        /// <summary>True when <paramref name="value"/> is a 64-character lowercase hex SHA256 digest.</summary>
        public static bool IsLowercaseSha256(string? value) =>
            value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        /// <summary>Lowercase hex SHA256 of <paramref name="bytes"/>.</summary>
        public static string Sha256HexLower(ReadOnlySpan<byte> bytes) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        /// <summary>Decodes canonical (padded) base64. Throws <see cref="FormatException"/> on malformed input;
        /// each codec catches that and wraps it in its own typed exception and message.</summary>
        public static byte[] DecodeBase64(string encoded) => Convert.FromBase64String(encoded);

        /// <summary>Unpadded-to-padded base64 length of a <paramref name="byteCount"/> payload.</summary>
        public static long PaddedBase64Length(long byteCount) => ((byteCount + 2) / 3) * 4;

        /// <summary>Decoded byte length of a canonical base64 <paramref name="encoded"/> string.</summary>
        public static long DecodedBase64Length(string encoded)
        {
            int padding = encoded.EndsWith("==", StringComparison.Ordinal) ? 2 : encoded.EndsWith('=') ? 1 : 0;
            return ((long)encoded.Length / 4 * 3) - padding;
        }
    }
    ```

2. Verify the arithmetic matches the originals exactly: `IsLowercaseSha256` mirrors
   `RestoreJournal.IsLowercaseSha256` (`:334`); `DecodedBase64Length` mirrors `RestoreJournal`
   `GetDecodedBase64Length` (`:368`); `PaddedBase64Length` mirrors `GetPaddedBase64Length` (`:136`).

**Build:**

```bash
dotnet build src/Scrinia.Core/Scrinia.Core.csproj
```

**Gate:** 0 errors. No callers exist yet — the type is dead code this task, which the compiler allows
(private/internal unused members). Do **not** wire callers in this task.

---

## Task 2 — Route the codecs through it

**Goal:** rewrite each codec's inline primitives to delegate to `RecoveryPayload`, preserving every
wrapper's exception type, failure code, and message, then unify the archive conversion.

Edit these files, keeping each file compiling after each sub-step:

1. **`AppendJournal.cs`**
   - `DecodePayload` (`:112-130`): `Convert.FromBase64String(payload)` → `RecoveryPayload.DecodeBase64(payload)`;
     `Convert.ToHexStringLower(SHA256.HashData(bytes))` → `RecoveryPayload.Sha256HexLower(bytes)`.
     Keep the `catch (FormatException)` → `AppendJournalException(IntegrityFailed)` and the
     `actualHash != expectedHash` → `AppendJournalException(IntegrityFailed)` wrapping **verbatim**.
   - `IsSha256` (`:391`): replace the `Uri.IsHexDigit` loop with `return RecoveryPayload.IsLowercaseSha256(value);`.
   - Remove the now-unused `System.Security.Cryptography` import if it is no longer referenced.

2. **`RestoreJournal.cs`**
   - `DecodeAndVerify` (`:314-332`): `Convert.FromBase64String(encoded)` → `RecoveryPayload.DecodeBase64(encoded)`;
     `Convert.ToHexStringLower(...)` → `RecoveryPayload.Sha256HexLower(...)`; keep
     `DecodeObserver.Value?.Invoke()` before decode and the `Malformed` wrapping verbatim.
   - `IsLowercaseSha256` (`:334`): `return RecoveryPayload.IsLowercaseSha256(digest);`.
   - `GetDecodedBase64Length` (`:368`): `return RecoveryPayload.DecodedBase64Length(value);` (drop the
     local `checked` wrapper only if the shared method is already overflow-correct — match the
     original arithmetic exactly, `((long)value.Length / 4 * 3) - padding`).
   - `GetPaddedBase64Length` (`:136`): `return RecoveryPayload.PaddedBase64Length(byteCount);`.
   - `CreatePrepared` (`:157`, `:160`): `Convert.ToHexStringLower(SHA256.HashData(...))` →
     `RecoveryPayload.Sha256HexLower(...)`.
   - Remove the now-unused `System.Security.Cryptography` import if no longer referenced.

3. **`RecoveryEnvelope.cs`**
   - Member verify (`:273-274`): `Convert.FromBase64String(...)` → `RecoveryPayload.DecodeBase64(...)`;
     `Convert.ToHexStringLower(SHA256.HashData(...))` → `RecoveryPayload.Sha256HexLower(...)`. Keep the
     surrounding `catch (FormatException) → JsonException` verbatim.
   - Committed-snapshot verify (`:905-909`): `Convert.FromBase64String(...)` →
     `RecoveryPayload.DecodeBase64(...)`; `Convert.ToHexStringLower(SHA256.HashData(...))` →
     `RecoveryPayload.Sha256HexLower(...)`.
   - Remove the now-unused `System.Security.Cryptography` import if no longer referenced.

4. **`FileEntityTransactionCoordinator.cs`**
   - `Verify` (`:187`): `Convert.ToHexStringLower(SHA256.HashData(...))` → `RecoveryPayload.Sha256HexLower(...)`.
   - `ToRawSnapshot` (`:416`): `Convert.FromBase64String(...)` → `RecoveryPayload.DecodeBase64(...)`.
     Keep the surrounding `catch (FormatException) → InvalidDataException` verbatim.
   - Remove the now-unused `System.Security.Cryptography` import if no longer referenced.

5. **`AppendTransaction.cs`** — add `ArchivePlan.FromPair` next to `WithStatuses`:

    ```csharp
    internal static ArchivePlan FromPair(ArchivePairPlan plan) =>
        new(plan.Required, plan.ArtifactPath, plan.SidecarPath,
            plan.Required ? ArchiveMemberStatus.Planned : ArchiveMemberStatus.NotRequired,
            plan.Required ? ArchiveMemberStatus.Planned : ArchiveMemberStatus.NotRequired,
            plan.PruneCandidates);
    ```

6. **`WholeMemoryTransactionCoordinator.cs`** — replace the inline `new ArchivePlan(...)` mapping
   (`:197-201`) with `new ArchivePlan(plan: transaction.Archive)`, and drop the `using
   System.Security.Cryptography` import if `ComputeCommittedHash` (`:227`) no longer uses it (it does —
   keep that import).

**Build:**

```bash
dotnet build Scrinia.sln
```

**Gate:** 0 errors. Then confirm the inline archive mapping is gone:

```bash
grep -rn "ArchiveMemberStatus.Planned : ArchiveMemberStatus.NotRequired" src/
```

Expected result: **no matches** outside `ArchivePlan.FromPair` in `AppendTransaction.cs`. If a second
copy of the status-recompute survives, the file still has the old inline mapping — fix it.

---

## Task 3 — Verify + commit

**Test gate:**

```bash
dotnet test Scrinia.sln --nologo
```

**Gate:** fully green, 3997+ tests. **Any failure is a behavior regression — do not sweep it under the
rug.** Walk the failing family against the testing matrix in the spec; the refactor is
behavior-preserving, so a red test means a wrapper's exception type/message drifted, the `DecodeObserver`
seam moved, the append validator widened back to uppercase, or the `ArchivePlan.FromPair` mapping
reordered a field.

**Commit (the six edited files plus the one new file):**

```bash
git commit -am "core: consolidate recovery codec encoding primitives (P3)" -m "AppendJournalCodec, RestoreJournalCodec, and RecoveryEnvelopeCodec each re-implemented canonical-lowercase-SHA256 validation, base64-decode, SHA256-integrity-check, and base64-padding math inline. Extracted those pure primitives into RecoveryPayload (single definition); each codec keeps its own typed exception and message and delegates the raw logic to RecoveryPayload. ArchivePlan.FromPair unifies the ArchivePlan<->ArchivePairPlan conversion at SerializeJournal. B2/B5 recovery anchors (ConsumeParsed, ParseVerifiedEnvelope, state machines, JSON contexts, exception contracts) are unchanged. Behavior-preserving; full suite green."
```

---

## Report back

After Task 3, report:

1. **Build** — Task 1 (`Scrinia.Core`) pass/fail, Task 2 (`Scrinia.sln`) pass/fail.
2. **Tests** — total count per project (or at least the solution total) and whether the suite is green.
3. **Commit hash** — the SHA of the `core:` commit from Task 3.
4. **Scope check** — confirm only the seven files changed (`git status` / `git diff --stat` should show
   exactly `RecoveryPayload.cs` new, and the six edited files).
