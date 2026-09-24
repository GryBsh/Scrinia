# P3 Journal/Transaction Codec Consolidation — Design Spec

**Date:** 2026-09-24
**Phase:** P3 — Journal/Transaction Codec Consolidation
**Owner:** (implementation handoff — coder)
**Status:** Approved for execution under the companion plan.

> **Where this lives:** `docs/superpowers/specs/`. This file is committed to `main`; it is the
> design record, not the change record. The executable plan lives beside it under
> `docs/superpowers/plans/` (gitignored) and is never committed.

---

## Context

### The problem

The three durable recovery codecs each re-implement the same small set of binary encoding
primitives inline. The primitives are tiny, correct, and identical — and they are defined three (or
four) times. This is the classic copy-paste-verify anti-pattern: every duplicate is a place a
future change has to be mirrored by hand, and a place a subtle divergence can hide.

**Canonical lowercase SHA256 validation.** Three predicates, one check, three spellings:

| Site | Definition | Test |
|------|------------|------|
| `AppendJournalCodec.IsSha256` (`AppendJournal.cs:391`) | `value is { Length: 64 } && all hex` via `Uri.IsHexDigit` | length-64, every char a hex digit |
| `RestoreJournalCodec.IsLowercaseSha256` (`RestoreJournal.cs:334`) | `value is { Length: 64 } && all lowercase hex` (`'0'..'9'` / `'a'..'f'`) | length-64, every char lowercase hex |
| `RecoveryVerification.IsSha256` (`RecoveryVerification.cs:239`) | `value.Length == 64 && value.All(Uri.IsHexDigit)` | length-64, every char a hex digit |

`AppendJournal.IsSha256` and `RecoveryVerification.IsSha256` both delegate to `Uri.IsHexDigit`, which
accepts **uppercase** hex as well as lowercase; `RestoreJournal.IsLowercaseSha256` is the only one
that actually enforces lowercase. That is three definitions of one logical predicate with two
different spellings of the lowercase question.

**base64 decode + SHA256 integrity check.** Every durable recovery codec that carries binary members
decodes a base64 payload and re-hashes it to prove the bytes match the digest the journal recorded:

| Site | Location |
|------|----------|
| `AppendJournalMember.DecodePayload` | `AppendJournal.cs:112-130` |
| `RestoreJournalCodec.DecodeAndVerify` | `RestoreJournal.cs:314-332` |
| `RecoveryEnvelopeCodec` member verification | `RecoveryEnvelope.cs:273-274`, `:905-909` |
| `FileEntityTransactionCoordinator` snapshot verify | `FileEntityTransactionCoordinator.cs:187`, `:416` |

Each does the same three steps: `Convert.FromBase64String(...)`,
`Convert.ToHexStringLower(SHA256.HashData(...))`, compare to the expected digest. The decode/verify
*shape* (which exception wraps the `FormatException`, which message, whether a length gate sits
before the decode) differs per codec — that difference is the contract — but the raw base64/SHA256
logic is byte-identical.

**base64 padding math.** `RestoreJournalCodec.GetPaddedBase64Length` / `GetDecodedBase64Length`
(`RestoreJournal.cs:136`, `:368`) duplicate the `(n+2)/3*4` / `(len/4*3)-padding` arithmetic that any
base64 reader needs. `GetDecodedBase64Length` computes the decoded byte length of a canonical
(padded) base64 string by subtracting its trailing padding (`==` → 2, `=` → 1, none → 0) from
`len/4*3`.

### Why this is fragile

Four codecs share five primitives (`IsSha256`/`IsLowercaseSha256`, decode+verify, `PaddedBase64Length`,
`DecodedBase64Length`) but none of them call a shared helper — each rewrites the arithmetic and the
hash from scratch on every pass. A correctness fix to base64 length math (say, an overflow guard, or
a canonical-form edge case) has to be re-derived in four places, and the append and recovery-verify
validators already disagree on the uppercase question. The logic is not coupled — it is duplicated —
and duplication is exactly the kind of drift that a focused regression finds and a focused review
misses.

The two archive-plan shapes add a second, unrelated duplication at one conversion site:

- `ArchivePlan` (`AppendTransaction.cs:44`) carries per-member `ArchiveMemberStatus` (artifact +
  sidecar): the journal-carrying shape.
- `ArchivePairPlan` (`WholeMemoryTransaction.cs:63`) is status-less (paths + prune candidates only):
  the whole-memory write shape.

`WholeMemoryTransactionCoordinator.SerializeJournal` maps an `ArchivePairPlan` → `ArchivePlan` at
`WholeMemoryTransactionCoordinator.cs:197-201` with an inline field-mapping that reconstructs the two
placeholder statuses from the `Required` flag. It is the only site the two shapes interconvert.

---

## Decision

### Chosen approach: extract the pure primitives into `RecoveryPayload`, route every codec through it

Introduce one new internal static helper, `RecoveryPayload`, that owns the five shared primitives
with a single definition each, and rewrite each codec's inline copy to delegate to it. Every codec
keeps its own typed exception, its own failure code, and its own message — the refactor moves only the
**pure, exception-free** raw logic, never the wrapper that turns a `FormatException` into a
recovery-contract error.

### Alternatives considered

| Alternative | Description | Rejected because |
|---|---|---|
| **A. Leave the duplicates, add a parity test** | Add a cross-codec test asserting the four validators produce identical results. | A test pins the behavior but does not remove the duplication; the next fix still has to land in four places. |
| **B. Extract into a shared `RecoveryEncoding` struct with instance state** | A stateful helper carrying scope/journal-path context. | The primitives are pure and context-free; threading scope through them adds coupling the callers do not need and risks touching the B2/B5 recovery anchors. |
| **C. Inline `RecoveryPayload` into each codec as a `using static`** | Keep the methods in `RecoveryPayload` but `using static Scrinia.Core.RecoveryPayload;` at each call site. | Same as the chosen approach for the primitives; `using static` is cosmetic. The chosen approach names the type explicitly in its summary so the ownership of the recovery encoding surface is clear. |
| **D. Extract + unify the archive conversion (chosen)** | `RecoveryPayload` for the five primitives; `ArchivePlan.FromPair` for the single `ArchivePairPlan` → `ArchivePlan` mapping. | — |

### Invariant: the wrappers are the contract; only the primitives move

The consolidation must preserve each codec's exact exception type, failure code, and message — those
are part of the recovery-consumer contract (see the B2 anchors below). The wrappers are thin: they
call the primitive, catch the `FormatException`, and rethrow as their own typed exception with their
own message. The refactor replaces **only** the `Convert.FromBase64String` / `Convert.ToHexStringLower`
/ padding arithmetic inside those wrappers. It never touches the `catch` clauses, the `throw`
expressions, or the message strings.

---

## Architecture & Components

### The new primitive owner: `RecoveryPayload`

New file `src/Scrinia.Core/RecoveryPayload.cs`, `internal static class RecoveryPayload` in
`Scrinia.Core`. It exposes **only** the pure, exception-free primitives:

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

Semantics are pinned exactly: `IsLowercaseSha256` preserves `RestoreJournal.IsLowercaseSha256`
(`:334`) and `DecodedBase64Length` preserves `RestoreJournal.GetDecodedBase64Length` (`:368`). The
class serializes nothing (no source-gen context needs a new entry) and has no instance state — it is
a pure helper, which is why it needs no registration in any `*JsonContext`.

### The five primitives, routed

| Primitive | Before (inline) | After (via `RecoveryPayload`) |
|---|---|---|
| lowercase-SHA256 validation | `AppendJournal.IsSha256` (`:391`), `RestoreJournal.IsLowercaseSha256` (`:334`), `RecoveryVerification.IsSha256` (`:239`) | each delegates to `RecoveryPayload.IsLowercaseSha256` |
| base64 decode | `AppendJournal.DecodePayload` (`:117`), `RestoreJournal.DecodeAndVerify` (`:321`), `RecoveryEnvelope` (`:273`, `:905`), `FileEntity` (`:416`) | `RecoveryPayload.DecodeBase64(...)` |
| SHA256 integrity digest | `AppendJournal` (`:125`), `RestoreJournal` (`:328`), `RecoveryEnvelope` (`:274`, `:909`) | `RecoveryPayload.Sha256HexLower(...)` |
| padded base64 length | `RestoreJournal.GetPaddedBase64Length` (`:136`) | `RecoveryPayload.PaddedBase64Length(...)` |
| decoded base64 length | `RestoreJournal.GetDecodedBase64Length` (`:368`) | `RecoveryPayload.DecodedBase64Length(...)` |

### The archive conversion, unified

`ArchivePlan.FromPair(ArchivePairPlan plan)` is added to `AppendTransaction.cs`, next to
`WithStatuses`:

```csharp
internal static ArchivePlan FromPair(ArchivePairPlan plan) =>
    new(plan.Required, plan.ArtifactPath, plan.SidecarPath,
        plan.Required ? ArchiveMemberStatus.Planned : ArchiveMemberStatus.NotRequired,
        plan.Required ? ArchiveMemberStatus.Planned : ArchiveMemberStatus.NotRequired,
        plan.PruneCandidates);
```

`WholeMemoryTransactionCoordinator.SerializeJournal` (`:197-201`) replaces the inline `new
ArchivePlan(...)` field-mapping with `new ArchivePlan(plan: transaction.Archive)`. The two record
types are **not** merged: `ArchivePairPlan` stays status-less and keeps validating its own
required-path invariant (`WholeMemoryTransaction.cs:73-76`); `ArchivePlan` stays the journal-carrying
shape. The single canonical conversion is `ArchivePlan.FromPair`.

---

## Data flow

### Encoding primitives — before / after

```
BEFORE (four independent copies)
AppendJournal.DecodePayload        Convert.FromBase64String        → Convert.ToHexStringLower(SHA256.HashData)
RestoreJournal.DecodeAndVerify     Convert.FromBase64String        → Convert.ToHexStringLower(SHA256.HashData)
RecoveryEnvelope member verify     Convert.FromBase64String        → Convert.ToHexStringLower(SHA256.HashData)
FileEntity verify/snapshot         Convert.FromBase64String        → Convert.ToHexStringLower(SHA256.HashData)
RestoreJournal padding             (n+2)/3*4                       len/4*3 - padding

AFTER (one definition, four call sites)
RecoveryPayload.IsLowercaseSha256  ← AppendJournal.IsSha256, RestoreJournal.IsLowercaseSha256, RecoveryVerification.IsSha256
RecoveryPayload.Sha256HexLower     ← AppendJournal.DecodePayload, RestoreJournal.DecodeAndVerify, RecoveryEnvelope ×2, FileEntity ×2
RecoveryPayload.DecodeBase64       ← AppendJournal.DecodePayload, RestoreJournal.DecodeAndVerify, RecoveryEnvelope ×2, FileEntity :416
RecoveryPayload.PaddedBase64Length ← RestoreJournal.GetPaddedBase64Length
RecoveryPayload.DecodedBase64Length← RestoreJournal.GetDecodedBase64Length
```

Each codec calls the primitive, then runs its own `catch (FormatException)` / hash-mismatch gate and
throws its own typed exception. The primitive never throws a recovery exception; it only throws the
`FormatException` the wrapper already knows how to translate.

### Archive conversion — before / after

```
BEFORE
SerializeJournal(...)
    └─ new ArchivePlan(
          transaction.Archive.Required,
          transaction.Archive.ArtifactPath,
          transaction.Archive.SidecarPath,
          Required ? Planned : NotRequired,   // artifact status  ← inline recompute
          Required ? Planned : NotRequired,   // sidecar status   ← inline recompute
          transaction.Archive.PruneCandidates)

AFTER
SerializeJournal(...)
    └─ new ArchivePlan(plan: transaction.Archive)      // ArchivePlan.FromPair, one canonical mapping
```

The status recomputation (`Required ? ArchiveMemberStatus.Planned : ArchiveMemberStatus.NotRequired`
for both members) moves from `SerializeJournal` into the single `ArchivePlan.FromPair` definition.

---

## Error handling

- **Each codec keeps its own exception type, failure code, and message verbatim.** The wrappers are
  the recovery-contract boundary; only the primitives below them move:
  - `AppendJournal.DecodePayload` keeps `catch (FormatException) → AppendJournalException(IntegrityFailed)`
    and the `actualHash != expectedHash → AppendJournalException(IntegrityFailed)` wrapping.
  - `RestoreJournalCodec.DecodeAndVerify` keeps `DecodeObserver.Value?.Invoke()` before decode and the
    `catch (FormatException) → Malformed` wrapping.
  - `RecoveryEnvelopeCodec` member verify keeps its own `catch (FormatException) → JsonException`
    (`:278-281`) and the committed-snapshot verify path (`:905-914`).
  - `FileEntityTransactionCoordinator` keeps the surrounding try/catch and its own `InvalidDataException`
    at `:416-417`.
- **`Uri.IsHexDigit` alignment on the append path.** `AppendJournal.IsSha256` currently validates with
  `Uri.IsHexDigit`, which admits **uppercase** hex digits. The shared `RecoveryPayload.IsLowercaseSha256`
  is strictly more conservative (lowercase-only). This is a deliberate, behavior-preserving alignment:
  the append journal already holds **only** lowercase digests (they are written by
  `Convert.ToHexStringLower` in `AppendJournalMember.FromSnapshot` and by `RawMemberSnapshot`, which
  lowercases on capture), so no currently-admitted document stops being admitted. The validation now
  matches `RestoreJournal` and `RecoveryVerification`'s intended lowercase contract; the change only
  removes an admission that never occurred in practice. If a future append ever wrote an uppercase
  digest, the new check would reject it earlier — which is the correct direction for a lowercase-only
  wire format.
- **No new failure paths.** `RecoveryPayload` throws only `FormatException` (from `DecodeBase64`),
  which every caller already knows how to translate. `checked`/overflow behavior on the padding math
  is preserved by matching `RestoreJournal`'s existing arithmetic exactly.

---

## Testing matrix

The existing suite is the regression net. These are the pins; every codec's behavior must decode,
verify, and serialize identically after the move.

| Test family | Pins |
|---|---|
| `AppendJournalCodec` / `RestoreJournalCodec` round-trip tests | base64-decode + sha256-verify still produce byte-identical decoded members. |
| `RecoverySerializationTests` / `RecoveryEnvelope` tests | envelope members decode+verify identically; `ParseVerifiedEnvelope` anchor unchanged. |
| `FileEntityTransaction` / recovery-adapter tests | FileEntity decode/verify unchanged. |
| `WholeMemoryTransactionCoordinator` archive-journal tests | `ArchivePlan.FromPair` produces the same journal the inline mapping did. |
| **Full gate:** `dotnet test Scrinia.sln` | 3997+ tests. Any failure is a behavior regression — do not sweep it under the rug. |

Every new test (if any) uses `TestHelpers.StoreScope`. P3 adds no new tests unless a behavior gap is
found.

---

## Frozen constraints (carry from P1–P2)

- **NMP/2 unchanged** — the encoding format and the digests it carry are untouched.
- **`.scrinia/` layout unchanged** — recovery evidence paths and journal filenames stay put.
- **CLI single-file surface** — no new public entry point; `RecoveryPayload` and `ArchivePlan.FromPair`
  are `internal`.
- **MCP 12 + 11 disjoint** — `guide()`, the nine memory verbs, `facts`, and the eleven shared tools
  are unaffected; the codec consolidation is invisible to the MCP surface.
- **Source-generated JSON only** — no reflection; `RecoveryPayload` serializes nothing, so **no new
  type is registered in any `*JsonContext`** (`AppendJournalJsonContext`, `RestoreJournalJsonContext`,
  `RecoveryEnvelopeJsonContext`, and every other context unchanged).
- **Recovery bypass untouched** — `DiscoverMemoriesRecursive` / `ParseScopeLabel` are not in scope.
- **No new public API** — `RecoveryPayload` (internal static) and `ArchivePlan.FromPair` (internal
  static) are the only additions, and both are `internal`.
- **B2/B5 recovery anchors untouched** — read `/findings/b2-journal-convergence-accepted` before
  touching anything recovery-related. Concretely: the typed `ConsumeParsed` consumers
  (`AppendJournalCodec.ConsumeParsed`, `RestoreJournalCodec.ConsumeParsed`) are B5-owned — do **not**
  change their signatures; `RecoveryEnvelopeCodec.ParseVerifiedEnvelope` (six-arg,
  `RecoveryVerification.cs:299-305`) is the recovery-admission anchor — do **not** change its
  signature or its metadata-contradiction / containment / stable-after-read integrity gate.
- **The four state machines, error codes, and JSON contexts are unchanged** — `AppendJournal` /
  `RestoreJournal` / `RecoveryEnvelope` / `RecoveryDiscovery`, the `McpResponse` `RECOVERY_BLOCKED` /
  `RECOVERY_REQUIRED` error codes, and `AppendJournalJsonContext` / `RestoreJournalJsonContext` /
  `RecoveryEnvelopeJsonContext`.
- **Every new test uses `TestHelpers.StoreScope`.**

---

## Out of scope

- Merging `ArchivePlan` and `ArchivePairPlan` into one type. They stay distinct; only their single
  conversion site is unified.
- Any change to `ConsumeParsed` signatures, `ParseVerifiedEnvelope`, the four state machines, or the
  recovery-bypass discovery (`DiscoverMemoriesRecursive` / `ParseScopeLabel`).
- The `RecoveryVerification` file-level verify path (`:187-220`) beyond routing its `IsSha256` check
  and its single `Convert.ToHexStringLower` (`:216`) through `RecoveryPayload`; its accounting,
  containment, and reparse-point logic is untouched.
- New public API, new MCP tool, new CLI command, or a new source-gen JSON context entry.
- Any non-recovery codec that does not share one of the five primitives.

---

## Execution

See the companion plan
`docs/superpowers/plans/2026-09-24-p3-journal-codec-consolidation-plan.md` (gitignored) for the single
clean execution pass: add `RecoveryPayload` → route the codecs through it → verify + commit. This
spec is the design record; the plan is the work order.
