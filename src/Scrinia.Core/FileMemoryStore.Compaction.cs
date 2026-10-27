using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Core;

public sealed partial class FileMemoryStore : IAtomicMemoryCompactor
{
    /// <inheritdoc/>
    public async Task<CompactMemoryResult> CompactAsync(MemoryAddress address, int keepRecent = 0,
        CancellationToken cancellationToken = default)
    {
        if (!address.IsValid)
            throw new ArgumentException("The memory address must be valid.", nameof(address));
        cancellationToken.ThrowIfCancellationRequested();
        if (address.Kind == MemoryAddressKind.Ephemeral)
            return CompactEphemeral(address, keepRecent, cancellationToken);

        var (scope, subject) = ResolveAddress(address);
        if (subject.Length > MemoryWrite.MaxPersistentSubjectLength)
            throw new InvalidMemoryWriteException(MemoryWrite.MaxPersistentSubjectLength, subject.Length);

        using WorkspaceLease lease = EnterWorkspaceMutation(ct: cancellationToken);
        EnsureMaterialized(scope);
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope), TimeSpan.FromSeconds(5), cancellationToken);
        string storeDirectory = GetStoreDirUnderLease(scope);
        bool retained = RecoverScopeUnsafe(lease, scope, storeDirectory, cancellationToken: cancellationToken);
        RefuseMutationWithRetainedEvidence(scope, storeDirectory, retained, cancellationToken);
        await _recoveryConductor.RecoverAppendUnsafeAsync(scope, storeDirectory, "compact-recovery", cancellationToken)
            .ConfigureAwait(false);

        ArtifactEntry? existing = LoadIndexUnderExclusiveAppendLock(scope)
            .FirstOrDefault(entry => MemoryNaming.NameEquals(entry.Name, subject));
        if (existing is not null)
        {
            subject = existing.Name;
            address = existing.Address;
        }
        string path = ArtifactPath(subject, scope);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Memory '{address}' not found.", path);
        string artifact = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        if (chunkCount <= 1 || keepRecent > 0 && keepRecent >= chunkCount)
            return new(null, [], chunkCount, false);

        MemoryWrite write = BuildCompactionWrite(address, scope, artifact, existing, keepRecent);
        write.ValidateOrThrow(_writeValidationOptions);
        PrecomputedStore pre = PrecomputeForStore(write, isEphemeral: false);
        WholeMemoryResult? transaction = null;
        ArtifactEntry stored = await StorePersistentUnderLockAsync(lease, write, pre, scope, subject, existing,
            result => transaction = result, cancellationToken).ConfigureAwait(false);
        return new(stored, write.RawContent, chunkCount, existing is not null) { Transaction = transaction };
    }

    private CompactMemoryResult CompactEphemeral(MemoryAddress address, int keepRecent, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var current = GetEphemeral(address);
            if (current is null)
                throw new FileNotFoundException($"Memory '{address}' not found.");
            int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(current.Artifact);
            if (chunkCount <= 1 || keepRecent > 0 && keepRecent >= chunkCount)
                return new(null, [], chunkCount, false);

            MemoryWrite write = BuildCompactionWrite(address, "ephemeral", current.Artifact,
                current.ToArtifactEntry(), keepRecent);
            write.ValidateOrThrow(_writeValidationOptions);
            PrecomputedStore pre = PrecomputeForStore(write, isEphemeral: true);
            EphemeralEntry updated = BuildEphemeralEntry(current.Name, pre.Artifact, pre.OriginalBytes,
                pre.ChunkCount, pre.Derived, current.CreatedAt, DateTimeOffset.UtcNow);
            ct.ThrowIfCancellationRequested();
            if (!_ephemeralStore.TryUpdate(address.Value, updated, current))
                continue;
            InvalidateEntityFacts();
            return new(ProjectEphemeralEntry(updated), write.RawContent, chunkCount, false);
        }
    }

    private static MemoryWrite BuildCompactionWrite(MemoryAddress address, string scope,
        string artifact, ArtifactEntry? existing, int keepRecent)
    {
        (string[] priorChunks, _) = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact);
        string[] compacted = keepRecent <= 0 ? [string.Concat(priorChunks)] : priorChunks[^keepRecent..];
        // Only curated keywords travel back as input; derived keywords must not gain agent weight.
        string[]? curated = existing?.AgentKeywords
            ?? MemoryDerivation.CuratedKeywords(existing?.Keywords, priorChunks);
        return MemoryWrite.Create(address, scope, compacted, description: existing?.ContentPreview,
            attributes: existing?.Attributes, keywords: curated);
    }
}
