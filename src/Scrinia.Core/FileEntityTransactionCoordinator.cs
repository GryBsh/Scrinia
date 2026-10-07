using System.Text;

namespace Scrinia.Core;

/// <summary>Publishes one file entity's body and metadata as a recoverable pair.</summary>
internal sealed class FileEntityTransactionCoordinator : IFileEntityTransactionCoordinator
{
    private readonly string _workspaceRoot;

    internal FileEntityTransactionCoordinator(string workspaceRoot)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    }

    internal void FailOnceAt(FileEntityFaultPoint point) =>
        B0FaultSeam.FailOnceAt(B0FaultSeam.MapFileEntityFaultPoint(point), point.ToString());

    public Task<FileEntityMutationResult> ExecuteAsync(
        FileEntityMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateDeletePaths(mutation);
        return TransformAsync(
            new FileEntityTarget(mutation.EntityKind, mutation.Name, mutation.BodyPath,
                mutation.MetaPath, mutation.VersionsDirectory),
            (_, _) => ValueTask.FromResult(mutation), cancellationToken);
    }

    public async Task<FileEntityMutationResult> TransformAsync(
        FileEntityTarget target,
        Func<FileEntityCurrentState, CancellationToken, ValueTask<FileEntityMutation>> prepare,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prepare);
        ValidatePaths(target);
        cancellationToken.ThrowIfCancellationRequested();

        var admission = new WorkspaceAdmission(_workspaceRoot,
            (lease, ct) => Bundles.BundleRestoreCoordinator.RecoverUnderLease(lease, Bundles.BundleReadLimits.Default, ct));
        using WorkspaceLease workspaceLease = admission.EnterMutation(TimeSpan.FromMinutes(5), cancellationToken);
        string entityDirectory = Path.GetDirectoryName(target.BodyPath)!;
        using var fileLock = FileLock.AcquireExclusive(Path.Combine(entityDirectory, ".lock"),
            TimeSpan.FromSeconds(5), cancellationToken);
        FileEntityMutationResult? recovery = RecoverPendingEvidence(entityDirectory,
            target.EntityKind, target.Name, cancellationToken);
        if (recovery is not null)
            return recovery;

        bool sidecarOnly = AreSamePath(target.BodyPath, target.MetaPath,
            Path.GetDirectoryName(Path.GetFullPath(target.BodyPath))!);
        RawMemberSnapshot oldBody = sidecarOnly
            ? RawMemberSnapshot.Capture(target.BodyPath, false, [])
            : Capture(target.BodyPath, FileEntityFaultPoint.RawBodyRead, cancellationToken);
        RawMemberSnapshot oldMeta = Capture(target.MetaPath, FileEntityFaultPoint.RawMetaRead, cancellationToken);
        FileEntityMutation mutation = await prepare(new FileEntityCurrentState(oldBody, oldMeta), cancellationToken);
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.EntityKind != target.EntityKind || mutation.Name != target.Name ||
            mutation.BodyPath != target.BodyPath || mutation.MetaPath != target.MetaPath ||
            mutation.VersionsDirectory != target.VersionsDirectory)
            throw new ArgumentException("The prepared mutation must retain the exact file-entity target.", nameof(prepare));
        ValidateDeletePaths(mutation);
        RawMemberSnapshot[] oldDeletePaths = mutation.DeletePaths
            .Select(path => Capture(path, FileEntityFaultPoint.RawMetaRead, cancellationToken))
            .ToArray();
        return PublishUnderLock(mutation, oldBody, oldMeta, oldDeletePaths, cancellationToken);
    }

    private FileEntityMutationResult PublishUnderLock(
        FileEntityMutation mutation, RawMemberSnapshot oldBody, RawMemberSnapshot oldMeta,
        RawMemberSnapshot[] oldDeletePaths, CancellationToken cancellationToken)
    {
        string operationId = Guid.NewGuid().ToString("N");
        string entityDirectory = Path.GetDirectoryName(mutation.BodyPath)!;
        string bodySnapshotPath = mutation.BodyPath;
        bool sidecarOnly = AreSamePath(mutation.BodyPath, mutation.MetaPath,
            Path.GetDirectoryName(Path.GetFullPath(mutation.BodyPath))!);
        byte[]? sourceBodyToArchive = mutation.SourceBodyToArchive;
        if (sourceBodyToArchive is not null && (oldBody.Existed || sidecarOnly || mutation.DeleteBody))
            throw new ArgumentException("A logical source archive requires an absent physical body being published.", nameof(mutation));
        bool metadataMutation = mutation.DeleteMeta || mutation.NewMeta is not null;
        string? evidencePath = null;
        string? archiveBody = null;
        string? archiveMeta = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (archiveBody, archiveMeta) = BuildArchivePaths(mutation, oldBody, oldMeta, sourceBodyToArchive is not null);
            evidencePath = EvidencePath(entityDirectory, operationId);
            WriteEvidence(evidencePath, mutation, oldBody, oldMeta, archiveBody, archiveMeta,
                RecoveryLifecycleState.Prepared, null, null, oldDeletePaths);

            if (!sidecarOnly)
                Archive(sourceBodyToArchive ?? (oldBody.Existed ? oldBody.Bytes : null),
                    archiveBody, FileEntityFaultPoint.ArchiveBodyWrite);
            Archive(oldMeta, archiveMeta, FileEntityFaultPoint.ArchiveMetaWrite);
            if (!sidecarOnly)
                Publish(mutation.BodyPath, mutation.DeleteBody, mutation.NewBody, FileEntityFaultPoint.BodyWrite);
            ThrowIfFault(FileEntityFaultPoint.AfterBodyDelete);

            if (metadataMutation)
                Publish(mutation.MetaPath, mutation.DeleteMeta, mutation.NewMeta ?? [], FileEntityFaultPoint.MetaWrite);
            foreach (string path in mutation.DeletePaths)
                Publish(path, true, [], FileEntityFaultPoint.MetaWrite);

            ThrowIfFault(FileEntityFaultPoint.VerificationRead);
            if (!sidecarOnly)
                Verify(entityDirectory, mutation.BodyPath, mutation.DeleteBody, mutation.NewBody, cancellationToken);
            if (metadataMutation)
                Verify(entityDirectory, mutation.MetaPath, mutation.DeleteMeta, mutation.NewMeta!, cancellationToken);
            foreach (string path in mutation.DeletePaths)
                Verify(entityDirectory, path, true, [], cancellationToken);

            RawMemberSnapshot committedBody = ExpectedSnapshot(
                bodySnapshotPath, sidecarOnly || mutation.DeleteBody, mutation.NewBody);
            RawMemberSnapshot committedMeta = metadataMutation
                ? ExpectedSnapshot(mutation.MetaPath, mutation.DeleteMeta, mutation.NewMeta!)
                : oldMeta;
            WriteEvidence(evidencePath, mutation, oldBody, oldMeta, archiveBody, archiveMeta,
                RecoveryLifecycleState.Committed, committedBody, committedMeta, oldDeletePaths);

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
                foreach (RawMemberSnapshot snapshot in oldDeletePaths)
                    Restore(snapshot);
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

    private void ValidatePaths(FileEntityTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target.EntityKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Name);
        string body = Path.GetFullPath(target.BodyPath);
        string meta = Path.GetFullPath(target.MetaPath);
        string versions = Path.GetFullPath(target.VersionsDirectory);
        if (!PathContainment.IsWithin(_workspaceRoot, body)
            || !PathContainment.IsWithin(_workspaceRoot, meta)
            || !PathContainment.IsWithin(_workspaceRoot, versions))
            throw new ArgumentException("File-entity paths must remain within the workspace root.", nameof(target));
    }

    private void ValidateDeletePaths(FileEntityMutation mutation)
    {
        foreach (string path in mutation.DeletePaths)
        {
            if (!PathContainment.IsWithin(_workspaceRoot, path) ||
                !PathContainment.IsWithin(mutation.VersionsDirectory, path))
                throw new ArgumentException("File-entity delete paths must remain within the versions directory.", nameof(mutation));
        }
    }

    private RawMemberSnapshot Capture(string path, FileEntityFaultPoint point, CancellationToken cancellationToken)
    {
        ThrowIfFault(point);
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("workspace", _workspaceRoot), RecoveryEvidenceKind.FileEntityJournal,
            path, default, MaintenanceLimits.Hard, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        if (verification.Outcome == RecoveryVerificationOutcome.ProtectedMissing)
            return RawMemberSnapshot.Capture(path, false, []);
        if (verification.Outcome != RecoveryVerificationOutcome.Verified || verification.SourceContent is null)
            throw new IOException($"File-entity member could not be captured safely: {path}");
        using (verification.SourceContent)
            return RawMemberSnapshot.Capture(path, true, verification.SourceContent.Bytes.ToArray());
    }

    private void Archive(RawMemberSnapshot snapshot, string? path, FileEntityFaultPoint point) =>
        Archive(snapshot.Existed ? snapshot.Bytes : null, path, point);

    private void Archive(byte[]? bytes, string? path, FileEntityFaultPoint point)
    {
        if (bytes is null || path is null)
            return;
        ThrowIfFault(point);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DurableFile.AtomicWriteAllBytes(path, bytes);
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

    private static void Verify(string entityDirectory, string path, bool deleted, byte[] expected, CancellationToken cancellationToken)
    {
        if (deleted)
        {
            RecoveryAbsenceVerifier.RequireMissing(entityDirectory, path, cancellationToken);
            return;
        }

        string expectedHash = RecoveryPayload.Sha256HexLower(expected);
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("workspace", Path.GetFullPath(Path.GetDirectoryName(path)!)),
            RecoveryEvidenceKind.FileEntityJournal, Path.GetFullPath(path),
            new RecoveryExpectedHash(expectedHash), MaintenanceLimits.Hard, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        if (verification.Outcome != RecoveryVerificationOutcome.Verified)
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
        RawMemberSnapshot oldMeta,
        bool hasSourceBodyToArchive)
    {
        bool sidecarOnly = AreSamePath(mutation.BodyPath, mutation.MetaPath,
            Path.GetDirectoryName(Path.GetFullPath(mutation.BodyPath))!);
        bool archiveBody = oldBody.Existed || hasSourceBodyToArchive;
        if (!archiveBody && !oldMeta.Existed)
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
        return (archiveBody ? body : null, oldMeta.Existed ? Path.ChangeExtension(body, ".meta.json") : null);
    }

    private static string EvidencePath(string directory, string operationId) =>
        Path.Combine(directory, $".file-entity-{operationId}.recovery");

    internal RecoveryEnvelopeDocument CreateEnvelope(
        FileEntityMutation mutation, RawMemberSnapshot oldBody, RawMemberSnapshot oldMeta,
        string operationId, string evidencePath, string? archiveBody, string? archiveMeta,
        RecoveryLifecycleState state = RecoveryLifecycleState.Prepared,
        RawMemberSnapshot? committedBody = null, RawMemberSnapshot? committedMeta = null,
        IReadOnlyList<RawMemberSnapshot>? deletedMembers = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        string entityDirectory = Path.GetFullPath(Path.GetDirectoryName(evidencePath)
            ?? throw new ArgumentException("Evidence path must have a directory.", nameof(evidencePath)));
        bool metadataOnly = AreSamePath(mutation.BodyPath, mutation.MetaPath, entityDirectory);
        RecoveryMemberSnapshot oldBodySnapshot = ToRecoverySnapshot(metadataOnly ? oldMeta : oldBody);
        var body = new FileEntityRecoveryBody(mutation.EntityKind, mutation.Name,
            oldBodySnapshot, ToRecoverySnapshot(oldMeta), archiveBody, archiveMeta,
            metadataOnly ? null : committedBody is null ? null : ToRecoverySnapshot(committedBody),
            committedMeta is null ? null : ToRecoverySnapshot(committedMeta),
            state == RecoveryLifecycleState.Committed ? "committed" : "prepared",
            deletedMembers?.Select(ToRecoverySnapshot).ToArray());
        return RecoveryEnvelopeCodec.FromFileEntity(body, ScopeForEntity(entityDirectory), null,
            operationId, state, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static string ScopeForEntity(string entityDirectory) =>
        Path.GetFileName(Path.GetDirectoryName(entityDirectory) ?? entityDirectory);

    private static RecoveryMemberSnapshot ToRecoverySnapshot(RawMemberSnapshot snapshot) =>
        new(snapshot.Path, snapshot.Existed,
            snapshot.Existed ? Convert.ToBase64String(snapshot.Bytes) : null, snapshot.Sha256);

    private void WriteEvidence(
        string path,
        FileEntityMutation mutation,
        RawMemberSnapshot oldBody,
        RawMemberSnapshot oldMeta,
        string? archiveBody,
        string? archiveMeta,
        RecoveryLifecycleState state,
        RawMemberSnapshot? committedBody,
        RawMemberSnapshot? committedMeta,
        IReadOnlyList<RawMemberSnapshot>? deletedMembers = null)
    {
        RecoveryEnvelopeDocument envelope = CreateEnvelope(mutation, oldBody, oldMeta,
            ExtractOperationId(path), path, archiveBody, archiveMeta, state, committedBody, committedMeta, deletedMembers);
        DurableFile.AtomicWriteAllBytes(path, RecoveryEnvelopeCodec.Serialize(envelope));
    }

    internal FileEntityMutationResult? RecoverPendingEvidence(string directory, string entityKind, string entityName,
        CancellationToken cancellationToken)
    {
        string[] evidencePaths = Directory.EnumerateFiles(directory, ".file-entity-*.recovery")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (evidencePaths.Length == 0)
            return null;

        var matches = new List<(string Path, RecoveryEnvelopeDocument Envelope)>();
        foreach (string evidencePath in evidencePaths)
        {
            try
            {
                var adapter = new FileEntityRecoveryAdapter();
                RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
                    new RecoveryScopeBinding(ScopeForEntity(directory), directory), RecoveryEvidenceKind.FileEntityJournal,
                    evidencePath, default, MaintenanceLimits.Hard, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                if (verification.Outcome != RecoveryVerificationOutcome.Verified || verification.SourceContent is null)
                    throw new IOException("File-entity recovery evidence could not be verified.");
                using RecoveryVerificationContent verifiedContent = verification.SourceContent;
                RecoveryVerifiedFileMetadata metadata = verifiedContent.Metadata;
                RecoveryScopeBinding scopeBinding = new(ScopeForEntity(directory), directory);
                RecoveryVerifiedEnvelope verifiedEnvelope = RecoveryEnvelopeCodec.ParseVerifiedEnvelope(
                    in verifiedContent, in scopeBinding, evidencePath, directory, RecoveryEvidenceKind.FileEntityJournal, in metadata);
                RecoveryEnvelopeDocument envelope = ((IRecoveryAdapter)adapter).ConsumeParsed(in verifiedEnvelope);
                if (envelope.TypedBody is not FileEntityRecoveryBody body)
                    throw new InvalidDataException("File-entity recovery body is missing.");
                bool kindMatches = string.Equals(body.EntityKind, entityKind, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(body.EntityKind, "skill", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entityKind, "skills", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(body.EntityKind, "skills", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entityKind, "skill", StringComparison.OrdinalIgnoreCase);
                if (kindMatches &&
                    string.Equals(body.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
                    matches.Add((evidencePath, envelope));
            }
            catch (Exception failure)
            {
                string operationId = ExtractOperationId(evidencePath);
                return new FileEntityMutationResult(WholeMemoryOutcome.RecoveryRequired, operationId,
                    "File-entity recovery evidence could not be applied.",
                    new RecoveryInfo(evidencePath, "recoveryRequired", "resolve retained evidence", failure.Message));
            }
        }

        if (matches.Count == 0)
            return null;
        if (matches.Count > 1)
            return new FileEntityMutationResult(WholeMemoryOutcome.RecoveryRequired, "ambiguous",
                "Multiple file-entity recovery records claim the requested entity.",
                new RecoveryInfo(matches[0].Path, "recoveryRequired", "resolve retained evidence", "Ambiguous recovery evidence."));

        try
        {
            RecoverFileEntity(matches[0].Envelope, matches[0].Path, ScopeForEntity(directory), directory,
                cancellationToken);
            return null;
        }
        catch (Exception failure)
        {
            string operationId = ExtractOperationId(matches[0].Path);
            return new FileEntityMutationResult(WholeMemoryOutcome.RecoveryRequired, operationId,
                "File-entity recovery evidence could not be applied.",
                new RecoveryInfo(matches[0].Path, "recoveryRequired", "resolve retained evidence", failure.Message));
        }
    }

    private static bool AreSamePath(string left, string right, string baseDirectory) =>
        string.Equals(Path.GetFullPath(Path.IsPathRooted(left) ? left : Path.Combine(baseDirectory, left)),
            Path.GetFullPath(Path.IsPathRooted(right) ? right : Path.Combine(baseDirectory, right)),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

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
                VerifySnapshot(expectedEntityDirectory, ToRawSnapshot(body.CommittedBody), cancellationToken);
            if (body.CommittedMeta is not null)
                VerifySnapshot(expectedEntityDirectory, ToRawSnapshot(body.CommittedMeta), cancellationToken);
        }
        else
        {
            Restore(ToRawSnapshot(body.OldBody));
            Restore(ToRawSnapshot(body.OldMeta));
            foreach (RecoveryMemberSnapshot snapshot in body.DeletedMembers ?? [])
                Restore(ToRawSnapshot(snapshot));
            Delete(body.ArchiveBodyPath);
            Delete(body.ArchiveMetaPath);
        }

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
        try { bytes = RecoveryPayload.DecodeBase64(snapshot.Payload ?? string.Empty); }
        catch (FormatException exception) { throw new InvalidDataException("Recovery snapshot payload is invalid.", exception); }
        return new RawMemberSnapshot(snapshot.Path, snapshot.Existed, bytes, snapshot.Sha256);
    }

    private static void VerifySnapshot(string entityDirectory, RawMemberSnapshot expected, CancellationToken cancellationToken) =>
        Verify(entityDirectory, expected.Path, !expected.Existed, expected.Bytes, cancellationToken);

    private static RawMemberSnapshot ExpectedSnapshot(string path, bool deleted, byte[] bytes) =>
        RawMemberSnapshot.Capture(path, !deleted, deleted ? [] : bytes);

    private void ThrowIfFault(FileEntityFaultPoint point)
    {
        Exception? failure = B0FaultSeam.ThrowIfInjectedOrNull(
            B0FaultSeam.MapFileEntityFaultPoint(point), point.ToString());
        if (failure is not null)
            throw failure;
    }
}
