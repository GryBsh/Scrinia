# P18 — Entry-Identity / Normalization Seam — Design Spec

**Parent work:** god-store `FileMemoryStore` seam extraction (Phases P4–P17 complete; read-side and cache/service owners `ListingService`, `SearchService`, `TopicDiscoveryService`, `ScopeLockManager`, `ScopeIndexCache`, `MaintenancePageServiceCache`; durable-write/delete leaves in `DurableWriter` (P9–P16); archive-version leaf in `ArchiveService` (P17)).
**Phase:** P18 (eighteenth seam extraction; first pure-transform seam).
**Goal:** move the entry-identity / normalization group off the store into a new static owner `ArtifactIdentityMapper`, leaving the store's key/path mapping (`MapArtifactKey`, `ArtifactPath`) intact.

## Why this phase

`BuildArtifactKey`, `CanonicalizeEntryForScope`, and `ValidateArtifactKey` (`FileMemoryStore.cs` :3703–:3764) own the single, cohesive responsibility of *deriving and validating the canonical physical identity of an entry within a scope* — the artifact key, its shape validation, and the entry re-keying that keeps the physical leaf decoupled from the qualified logical address. They are pure, side-effect-free transforms: no disk I/O, no exclusive lock, no fault injection, no recovery verification, no cache copy-on-write, and no instance state. `CanonicalizeEntryForScope` is currently an instance method but references no `FileMemoryStore` field. This is the last pure-transform group resident in the god store.

`MapArtifactKey` (:3738) correctly stays resident: it closes over the frozen `MemoriesRoot` migration barrier (:307–:310). After extraction it simply calls the new owner's `ValidateArtifactKey`.

## The seam

Move these three methods verbatim into a new **static** owner `ArtifactIdentityMapper`:

- `BuildArtifactKey(string subject, string scope)` (:3703–:3713)
- `CanonicalizeEntryForScope(ArtifactEntry entry, string scope)` (:3715–:3736)
- `ValidateArtifactKey(string artifactKey)` (:3750–:3764)

`CanonicalizeEntryForScope` currently calls `FormatQualifiedName(scope, subject)` (the store's frozen forwarder); in the owner it becomes `MemoryNaming.FormatQualifiedName(scope, subject)`. All other references are frozen primitives (`MemoryAddress.Parse`, `MemoryAddressKind.Persistent`, `PathParser`).

### New owner: `ArtifactIdentityMapper` (`src/Scrinia.Core/ArtifactIdentityMapper.cs`)

```csharp
using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// Owns canonical physical-identity derivation for a scope: the portable artifact key, its shape
/// validation, and the entry re-keying that keeps the physical leaf decoupled from the qualified
/// logical address. Pure, side-effect-free transforms — no disk, lock, fault injection, recovery,
/// or cache state. Moved off <see cref="FileMemoryStore"/> in P18.
/// </summary>
internal static class ArtifactIdentityMapper
{
    public static string BuildArtifactKey(string subject, string scope)
    {
        string topic = scope.Equals("local", StringComparison.Ordinal)
            ? ""
            : scope.Equals("ephemeral", StringComparison.Ordinal)
                ? "ephemeral/"
                : scope.StartsWith("local:", StringComparison.Ordinal)
                    ? scope["local:".Length..] + "/"
                    : throw new ArgumentException($"Unknown scope: {scope}", nameof(scope));
        return $"artifacts/{topic}{subject}.nmp2";
    }

    public static ArtifactEntry CanonicalizeEntryForScope(ArtifactEntry entry, string scope)
    {
        if (scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
            return entry;

        string subject = entry.Name;
        if (string.IsNullOrWhiteSpace(subject))
            subject = entry.Address.IsValid && entry.Address.Kind == MemoryAddressKind.Persistent
                ? entry.Address.Value.Trim('/').Split('/')[^1]
                : entry.Name;
        else if (subject.Contains('/'))
            subject = subject.Split('/')[^1];
        string address = scope.Equals("local", StringComparison.Ordinal)
            ? $"/{subject}"
            : MemoryNaming.FormatQualifiedName(scope, subject);
        return entry with
        {
            Address = MemoryAddress.Parse(address),
            ArtifactKey = BuildArtifactKey(subject, scope),
            Name = subject,
        };
    }

    public static void ValidateArtifactKey(string artifactKey)
    {
        if (string.IsNullOrWhiteSpace(artifactKey) ||
            artifactKey.Contains('\\') ||
            artifactKey.Contains(':') ||
            artifactKey.StartsWith('/') ||
            !artifactKey.StartsWith("artifacts/", StringComparison.Ordinal) ||
            Uri.TryCreate(artifactKey, UriKind.Absolute, out _))
            throw new ArgumentException("ArtifactKey must be a portable store-relative key.", nameof(artifactKey));

        string relative = artifactKey["artifacts/".Length..];
        string[] segments = relative.Split('/');
        if (segments.Length == 0 || segments.Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("ArtifactKey must not contain traversal segments.", nameof(artifactKey));
    }
}
```

### Store wiring (`FileMemoryStore.cs`)

Four edits:

1. **Remove the three moved methods** (:3703–:3713, :3715–:3736, :3750–:3764).

2. **`MapArtifactKey`** (:3738–:3748) — change its `ValidateArtifactKey(artifactKey)` call to `ArtifactIdentityMapper.ValidateArtifactKey(artifactKey);`. (It keeps its `MemoriesRoot` migration barrier and `PathContainment` check.)

3. **Repoint the ~9 external call sites** to the static owner:
   - `BuildArtifactKey` → `ArtifactIdentityMapper.BuildArtifactKey(...)`
     - :616 (`ArtifactPath`), :3554 (`BuildDerivedEntry`), :3633 (`ProjectEphemeralEntry`), :4045 (`ImportTopicEntriesCore`).
     - (:3733 is the moved method's own internal call — gone with the move.)
   - `CanonicalizeEntryForScope` → `ArtifactIdentityMapper.CanonicalizeEntryForScope(...)`
     - :1212 (`LoadIndexUnsafe`), :1481 (`EnumerateRawForMigration`), :1711 (`SaveIndexUnsafe`), :1778 (`UpsertCore`), :2008 (`TryUpdateEntry`).
     - (:3733 internal call — gone with the move.)
   - `ValidateArtifactKey` → `ArtifactIdentityMapper.ValidateArtifactKey(...)`
     - :3740 (`MapArtifactKey`).

4. **Confirm no other caller** of these three names remains in `FileMemoryStore.cs` after repointing, and that the `ArtifactEntry.cs` duplicate (`ValidateArtifactKey` at :120) is left untouched (the owner now centralizes the store-facing validation; the model's own copy is a separate, pre-existing guard).

## Frozen do-not-touch (carry from prior phases)

`LoadIndex` + recovery barrier, `MemoriesRoot` migration gate (:307–:310), `GetStoreDirForScope`/`PathRouter.ToStoreDir`, `SanitizeName`/`SanitizeNameCore`, `ProjectEphemeralEntry` write path, `MemoryNaming.FormatQualifiedName`, facts glue, `TopicDiscoveryService` (P6), `ScopeLockManager`/`ScopeIndexCache` (P4), `MaintenancePageServiceCache` (P5), TransactionCoordinator family, `FileStoreJsonContext` (private nested), `DurableFile`/`FileRetry`/`AppendFaultPoint`/`InjectAppendFault`/`B0FaultSeam`/`RecoveryEvidenceVerifier`, all `DurableWriter` leaves (P9–P16), `ArchiveService` (P17), NMP/2, `.scrinia/` layout, all public `IMemoryStore`/`IPluginStore`/`IAddressResolver` signatures, the read-side seam owners (`ListingService`, `SearchService`), the frozen `MapArtifactKey` (`MemoriesRoot` barrier) and `ArtifactPath` forwarder, and the existing `ArtifactEntry.cs` `ValidateArtifactKey` copy.

## Acceptance

- `ArtifactIdentityMapper` owns the entry-identity/normalization group; the store retains only the `MapArtifactKey`/`ArtifactPath` forwarders (which delegate to the owner).
- No new public API; `IMemoryStore` surface unchanged.
- No `FileMemoryStore` instance state referenced by the moved methods.
- Full suite green.

## Verification

1. `dotnet build Scrinia.sln` — 0 errors.
2. Targeted seam filter:
   ```
   dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ArtifactIdentityMapper|FullyQualifiedName~CanonicalizeEntryForScope|FullyQualifiedName~BuildArtifactKey|FullyQualifiedName~ValidateArtifactKey"
   ```
   Confirm `Passed!` / 0 failed.
3. Full `dotnet test Scrinia.sln` (single-node `/m:1` if parallel MSBuild hangs on Windows). Report the final summary per project. The known pre-existing embeddings parallel-load flake may fail under full-suite load; if it does, confirm it passes in isolation (zero-overlap with this seam) — do NOT touch the embeddings plugin.
