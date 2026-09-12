using System.Security.Cryptography;
using System.Text;

namespace Scrinia.Core;

/// <summary>Publishes one file entity's body and metadata as a recoverable pair.</summary>
internal sealed class FileEntityTransactionCoordinator : IFileEntityTransactionCoordinator
{
    private readonly string _workspaceRoot;
    private readonly object _faultGate = new();
    private readonly Queue<FileEntityFaultPoint> _faultPoints = new();

    internal FileEntityTransactionCoordinator(string workspaceRoot)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    }

    internal void FailOnceAt(FileEntityFaultPoint point)
    {
        lock (_faultGate)
            _faultPoints.Enqueue(point);
    }

    public async Task<FileEntityMutationResult> ExecuteAsync(
        FileEntityMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidatePaths(mutation);
        cancellationToken.ThrowIfCancellationRequested();

        string operationId = Guid.NewGuid().ToString("N");
        bool sidecarOnly = string.Equals(
            Path.GetFullPath(mutation.BodyPath), Path.GetFullPath(mutation.MetaPath),
            StringComparison.OrdinalIgnoreCase);
        string entityDirectory = Path.GetDirectoryName(mutation.BodyPath)!;
        string bodySnapshotPath = mutation.BodyPath;
        using var fileLock = FileLock.AcquireExclusive(Path.Combine(entityDirectory, ".lock"));

        FileEntityMutationResult? recovery = RecoverPendingEvidence(entityDirectory);
        if (recovery is not null)
            return recovery;

        RawMemberSnapshot oldBody = sidecarOnly
            ? RawMemberSnapshot.Capture(bodySnapshotPath, false, [])
            : Capture(mutation.BodyPath, FileEntityFaultPoint.RawBodyRead);
        RawMemberSnapshot oldMeta = Capture(mutation.MetaPath, FileEntityFaultPoint.RawMetaRead);
        bool metadataMutation = mutation.DeleteMeta || mutation.NewMeta is not null;
        string? evidencePath = null;
        string? archiveBody = null;
        string? archiveMeta = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (archiveBody, archiveMeta) = BuildArchivePaths(mutation, oldBody, oldMeta);
            evidencePath = EvidencePath(entityDirectory, operationId);
            WriteEvidence(evidencePath, mutation, oldBody, oldMeta, archiveBody, archiveMeta,
                RecoveryLifecycleState.Prepared, null, null);

            if (!sidecarOnly)
                Archive(oldBody, archiveBody, FileEntityFaultPoint.ArchiveBodyWrite);
            Archive(oldMeta, archiveMeta, FileEntityFaultPoint.ArchiveMetaWrite);
            if (!sidecarOnly)
                Publish(mutation.BodyPath, mutation.DeleteBody, mutation.NewBody, FileEntityFaultPoint.BodyWrite);
            ThrowIfFault(FileEntityFaultPoint.AfterBodyDelete);

            if (metadataMutation)
                Publish(mutation.MetaPath, mutation.DeleteMeta, mutation.NewMeta ?? [], FileEntityFaultPoint.MetaWrite);

            ThrowIfFault(FileEntityFaultPoint.VerificationRead);
            if (!sidecarOnly)
                Verify(mutation.BodyPath, mutation.DeleteBody, mutation.NewBody);
            if (metadataMutation)
                Verify(mutation.MetaPath, mutation.DeleteMeta, mutation.NewMeta!);

            RawMemberSnapshot committedBody = ExpectedSnapshot(
                bodySnapshotPath, sidecarOnly || mutation.DeleteBody, mutation.NewBody);
            RawMemberSnapshot committedMeta = metadataMutation
                ? ExpectedSnapshot(mutation.MetaPath, mutation.DeleteMeta, mutation.NewMeta!)
                : oldMeta;
            WriteEvidence(evidencePath, mutation, oldBody, oldMeta, archiveBody, archiveMeta,
                RecoveryLifecycleState.Committed, committedBody, committedMeta);

            try
            {
                ThrowIfFault(FileEntityFaultPoint.EvidenceCleanup);
                File.Delete(evidencePath);
            }
            catch (Exception cleanupFailure)
            {
                return new FileEntityMutationResult(
                    oldBody.Existed || oldMeta.Existed ? WholeMemoryOutcome.SuccessWithWarning : WholeMemoryOutcome.Created,
                    operationId,
                    "File-entity publication committed, but recovery evidence cleanup requires attention.",
                    new RecoveryInfo(evidencePath, "cleanupPending", "resolve retained evidence", cleanupFailure.Message));
            }

            return new FileEntityMutationResult(
                oldBody.Existed || oldMeta.Existed ? WholeMemoryOutcome.Replaced : WholeMemoryOutcome.Created,
                operationId, null, null);
        }
        catch (Exception failure)
        {
            try
            {
                ThrowIfFault(FileEntityFaultPoint.RollbackWrite);
                Restore(oldBody);
                Restore(oldMeta);
                Delete(archiveBody);
                Delete(archiveMeta);
                if (evidencePath is not null)
                    File.Delete(evidencePath);
            }
            catch (Exception rollbackFailure)
            {
                return new FileEntityMutationResult(
                    WholeMemoryOutcome.RecoveryRequired,
                    operationId,
                    "File-entity publication requires recovery.",
                    new RecoveryInfo(evidencePath!, "recoveryRequired", "resolve retained evidence",
                        rollbackFailure.Message));
            }

            if (failure is OperationCanceledException)
                throw;
            throw new WholeMemoryTransactionException(
                WholeMemoryErrorCode.Internal,
                operationId,
                "File-entity publication failed; the original pair was restored.",
                failure);
        }
    }

    private void ValidatePaths(FileEntityMutation mutation)
    {
        string body = Path.GetFullPath(mutation.BodyPath);
        string meta = Path.GetFullPath(mutation.MetaPath);
        string versions = Path.GetFullPath(mutation.VersionsDirectory);
        if (!PathContainment.IsWithin(_workspaceRoot, body)
            || !PathContainment.IsWithin(_workspaceRoot, meta)
            || !PathContainment.IsWithin(_workspaceRoot, versions))
            throw new ArgumentException("File-entity paths must remain within the workspace root.", nameof(mutation));
    }

    private RawMemberSnapshot Capture(string path, FileEntityFaultPoint point)
    {
        ThrowIfFault(point);
        if (!File.Exists(path))
            return RawMemberSnapshot.Capture(path, false, []);
        return RawMemberSnapshot.Capture(path, true, File.ReadAllBytes(path));
    }

    private void Archive(RawMemberSnapshot snapshot, string? path, FileEntityFaultPoint point)
    {
        if (!snapshot.Existed || path is null)
            return;
        ThrowIfFault(point);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DurableFile.AtomicWriteAllBytes(path, snapshot.Bytes);
    }

    private void Publish(string path, bool delete, byte[] bytes, FileEntityFaultPoint point)
    {
        ThrowIfFault(point);
        if (delete)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DurableFile.AtomicWriteAllBytes(path, bytes);
    }

    private static void Verify(string path, bool deleted, byte[] expected)
    {
        if (deleted)
        {
            if (File.Exists(path))
                throw new IOException($"File-entity member was not deleted: {path}");
            return;
        }

        if (!File.Exists(path))
            throw new IOException($"File-entity member is missing: {path}");
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expected));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"File-entity member verification failed: {path}");
    }

    private static void Restore(RawMemberSnapshot snapshot)
    {
        if (!snapshot.Existed)
        {
            File.Delete(snapshot.Path);
            return;
        }

        // Rollback remains a durable atomic replacement, but publication fault injection must not
        // turn a successful compensation into a second failure. The coordinator's explicit
        // RollbackWrite seam is the deterministic way to test rollback failure.
        Func<DurableWritePhase, string, Exception?>? injector = DurableFile.FailureInjector;
        try
        {
            DurableFile.FailureInjector = null;
            DurableFile.AtomicWriteAllBytes(snapshot.Path, snapshot.Bytes);
        }
        finally
        {
            DurableFile.FailureInjector = injector;
        }
    }

    private static void Delete(string? path)
    {
        if (path is not null)
            File.Delete(path);
    }

    private static (string? Body, string? Meta) BuildArchivePaths(
        FileEntityMutation mutation,
        RawMemberSnapshot oldBody,
        RawMemberSnapshot oldMeta)
    {
        bool sidecarOnly = string.Equals(
            Path.GetFullPath(mutation.BodyPath), Path.GetFullPath(mutation.MetaPath),
            StringComparison.OrdinalIgnoreCase);
        if (!oldBody.Existed && !oldMeta.Existed)
            return (null, null);

        Directory.CreateDirectory(mutation.VersionsDirectory);
        string stem = sidecarOnly
            ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(mutation.MetaPath))
            : Path.GetFileNameWithoutExtension(mutation.BodyPath);
        string extension = sidecarOnly ? ".meta.json" : Path.GetExtension(mutation.BodyPath);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        string body = Path.Combine(mutation.VersionsDirectory, $"{stem}_{timestamp}{extension}");
        int suffix = 1;
        while (File.Exists(body) || (!sidecarOnly && File.Exists(Path.ChangeExtension(body, ".meta.json"))))
            body = Path.Combine(mutation.VersionsDirectory, $"{stem}_{timestamp}-{suffix++}{extension}");
        if (sidecarOnly)
            return (null, oldMeta.Existed ? body : null);
        return (oldBody.Existed ? body : null, oldMeta.Existed ? Path.ChangeExtension(body, ".meta.json") : null);
    }

    private static string EvidencePath(string directory, string operationId) =>
        Path.Combine(directory, $".file-entity-{operationId}.recovery");

    internal RecoveryEnvelopeDocument CreateEnvelope(
        FileEntityMutation mutation, RawMemberSnapshot oldBody, RawMemberSnapshot oldMeta,
        string operationId, string evidencePath, string? archiveBody, string? archiveMeta,
        RecoveryLifecycleState state = RecoveryLifecycleState.Prepared,
        RawMemberSnapshot? committedBody = null, RawMemberSnapshot? committedMeta = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        string entityDirectory = Path.GetFullPath(Path.GetDirectoryName(evidencePath)
            ?? throw new ArgumentException("Evidence path must have a directory.", nameof(evidencePath)));
        bool metadataOnly = string.Equals(mutation.BodyPath, mutation.MetaPath,
            StringComparison.OrdinalIgnoreCase);
        var body = new FileEntityRecoveryBody(mutation.EntityKind, mutation.Name,
            ToRecoverySnapshot(oldBody), ToRecoverySnapshot(oldMeta), archiveBody, archiveMeta,
            metadataOnly ? null : committedBody is null ? null : ToRecoverySnapshot(committedBody),
            committedMeta is null ? null : ToRecoverySnapshot(committedMeta),
            state == RecoveryLifecycleState.Committed ? "committed" : "prepared");
        return RecoveryEnvelopeCodec.FromFileEntity(body, ScopeForEntity(entityDirectory), null,
            operationId, state, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static string ScopeForEntity(string entityDirectory) =>
        Path.GetFileName(Path.GetDirectoryName(entityDirectory) ?? entityDirectory);

    private static RecoveryMemberSnapshot ToRecoverySnapshot(RawMemberSnapshot snapshot) =>
        new(snapshot.Path, snapshot.Existed, Convert.ToBase64String(snapshot.Bytes), snapshot.Sha256);

    private void WriteEvidence(
        string path,
        FileEntityMutation mutation,
        RawMemberSnapshot oldBody,
        RawMemberSnapshot oldMeta,
        string? archiveBody,
        string? archiveMeta,
        RecoveryLifecycleState state,
        RawMemberSnapshot? committedBody,
        RawMemberSnapshot? committedMeta)
    {
        RecoveryEnvelopeDocument envelope = CreateEnvelope(mutation, oldBody, oldMeta,
            ExtractOperationId(path), path, archiveBody, archiveMeta, state, committedBody, committedMeta);
        DurableFile.AtomicWriteAllBytes(path, RecoveryEnvelopeCodec.Serialize(envelope));
    }

    private FileEntityMutationResult? RecoverPendingEvidence(string directory)
    {
        string? evidencePath = Directory.EnumerateFiles(directory, ".file-entity-*.recovery")
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (evidencePath is null)
            return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(evidencePath);
            var adapter = new FileEntityRecoveryAdapter();
            RecoveryEnvelopeDocument envelope = ((IRecoveryAdapter)adapter).ParseAndNormalize(
                bytes, ScopeForEntity(directory), evidencePath, directory);
            RecoverFileEntity(envelope, evidencePath,
                ScopeForEntity(directory), directory, CancellationToken.None);
            return null;
        }
        catch (Exception failure)
        {
            string operationId = ExtractOperationId(evidencePath);
            return new FileEntityMutationResult(
                WholeMemoryOutcome.RecoveryRequired,
                operationId,
                "File-entity recovery evidence could not be applied.",
                new RecoveryInfo(evidencePath, "recoveryRequired", "resolve retained evidence", failure.Message));
        }
    }

    internal RecoveryOutcome RecoverFileEntity(RecoveryEnvelopeDocument document, string evidencePath,
        string expectedScope, string expectedEntityDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapter = new FileEntityRecoveryAdapter();
        RecoveryEnvelopeDocument validated = ((IRecoveryAdapter)adapter).ValidateForMutation(document,
            expectedScope, evidencePath, SubjectOf(document), expectedEntityDirectory);
        FileEntityRecoveryBody body = (FileEntityRecoveryBody)validated.TypedBody!;
        if (validated.State == RecoveryLifecycleState.Committed)
        {
            if (body.CommittedBody is null && body.CommittedMeta is null)
                throw new InvalidDataException("Committed recovery evidence is missing post-commit snapshots.");
            if (body.CommittedBody is not null)
                VerifySnapshot(ToRawSnapshot(body.CommittedBody));
            if (body.CommittedMeta is not null)
                VerifySnapshot(ToRawSnapshot(body.CommittedMeta));
        }
        else
        {
            Restore(ToRawSnapshot(body.OldBody));
            Restore(ToRawSnapshot(body.OldMeta));
        }

        Delete(body.ArchiveBodyPath);
        Delete(body.ArchiveMetaPath);
            File.Delete(evidencePath);
        return new RecoveryOutcome(false, validated.State == RecoveryLifecycleState.Committed,
            false, expectedScope, evidencePath, validated.OperationId, null, null);
    }

    private static string SubjectOf(RecoveryEnvelopeDocument document) =>
        document.TypedBody is FileEntityRecoveryBody body
            ? $"{body.EntityKind}/{body.EntityName}"
            : throw new InvalidDataException("File-entity recovery body is missing.");

    private static string ExtractOperationId(string evidencePath)
    {
        string name = Path.GetFileName(evidencePath);
        const string prefix = ".file-entity-";
        const string suffix = ".recovery";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[prefix.Length..^suffix.Length] : "unknown";
    }

    private static RawMemberSnapshot ToRawSnapshot(RecoveryMemberSnapshot snapshot)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(snapshot.Payload); }
        catch (FormatException exception) { throw new InvalidDataException("Recovery snapshot payload is invalid.", exception); }
        return new RawMemberSnapshot(snapshot.Path, snapshot.Existed, bytes, snapshot.Sha256);
    }

    private static void VerifySnapshot(RawMemberSnapshot expected) =>
        Verify(expected.Path, !expected.Existed, expected.Bytes);

    private static RawMemberSnapshot ExpectedSnapshot(string path, bool deleted, byte[] bytes) =>
        RawMemberSnapshot.Capture(path, !deleted, deleted ? [] : bytes);

    private void ThrowIfFault(FileEntityFaultPoint point)
    {
        lock (_faultGate)
        {
            if (_faultPoints.Count == 0 || _faultPoints.Peek() != point)
                return;
            _faultPoints.Dequeue();
        }

        throw new IOException($"Injected file-entity fault at {point}.");
    }
}
