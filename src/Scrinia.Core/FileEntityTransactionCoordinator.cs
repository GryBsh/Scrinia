using System.Security.Cryptography;
using System.Text;

namespace Scrinia.Core;

/// <summary>Publishes one file entity's body and metadata as a recoverable pair.</summary>
internal sealed class FileEntityTransactionCoordinator : IFileEntityTransactionCoordinator
{
    private const string EvidenceMagic = "SCRINIA-FILE-ENTITY-RECOVERY-1";
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
        using var fileLock = FileLock.AcquireExclusive(Path.Combine(entityDirectory, ".lock"));

        FileEntityMutationResult? recovery = RecoverPendingEvidence(entityDirectory);
        if (recovery is not null)
            return recovery;

        RawMemberSnapshot oldBody = sidecarOnly
            ? RawMemberSnapshot.Capture(mutation.BodyPath, false, [])
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
            WriteEvidence(evidencePath, oldBody, oldMeta, archiveBody, archiveMeta,
                RecoveryEvidenceState.Prepared, null, null);

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
                mutation.BodyPath, mutation.DeleteBody, mutation.NewBody);
            RawMemberSnapshot committedMeta = metadataMutation
                ? ExpectedSnapshot(mutation.MetaPath, mutation.DeleteMeta, mutation.NewMeta!)
                : oldMeta;
            WriteEvidence(evidencePath, oldBody, oldMeta, archiveBody, archiveMeta,
                RecoveryEvidenceState.Committed, committedBody, committedMeta);

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

        // Rollback deliberately does not re-enter the publication fault injector. The injected
        // publication failure must not make the compensating write fail as a side effect; the
        // coordinator's explicit RollbackWrite seam is the deterministic way to test that path.
        File.WriteAllBytes(snapshot.Path, snapshot.Bytes);
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

    private static void WriteEvidence(
        string path,
        RawMemberSnapshot oldBody,
        RawMemberSnapshot oldMeta,
        string? archiveBody,
        string? archiveMeta,
        RecoveryEvidenceState state,
        RawMemberSnapshot? committedBody,
        RawMemberSnapshot? committedMeta)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(EvidenceMagic);
            writer.Write((byte)state);
            WriteSnapshot(writer, oldBody);
            WriteSnapshot(writer, oldMeta);
            WriteOptionalPath(writer, archiveBody);
            WriteOptionalPath(writer, archiveMeta);
            WriteOptionalSnapshot(writer, committedBody);
            WriteOptionalSnapshot(writer, committedMeta);
        }

        DurableFile.AtomicWriteAllBytes(path, stream.ToArray());
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
            RecoveryEvidence evidence;
            using (var stream = File.OpenRead(evidencePath))
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false))
            {
                if (!reader.ReadString().Equals(EvidenceMagic, StringComparison.Ordinal))
                    throw new InvalidDataException("Unknown file-entity recovery evidence format.");
                evidence = ReadEvidence(reader);
            }

            ValidateEvidencePaths(evidence, directory);
            if (evidence.State == RecoveryEvidenceState.Committed)
            {
                if (evidence.CommittedBody is null || evidence.CommittedMeta is null)
                    throw new InvalidDataException("Committed recovery evidence is missing post-commit snapshots.");
                VerifySnapshot(evidence.CommittedBody);
                VerifySnapshot(evidence.CommittedMeta);
            }
            else
            {
                Restore(evidence.OldBody);
                Restore(evidence.OldMeta);
            }

            Delete(evidence.ArchiveBody);
            Delete(evidence.ArchiveMeta);
            File.Delete(evidencePath);
            return null;
        }
        catch (Exception failure)
        {
            string operationId = Path.GetFileNameWithoutExtension(evidencePath)[".file-entity-".Length..];
            return new FileEntityMutationResult(
                WholeMemoryOutcome.RecoveryRequired,
                operationId,
                "File-entity recovery evidence could not be applied.",
                new RecoveryInfo(evidencePath, "recoveryRequired", "resolve retained evidence", failure.Message));
        }
    }

    private static void WriteSnapshot(BinaryWriter writer, RawMemberSnapshot snapshot)
    {
        writer.Write(snapshot.Path);
        writer.Write(snapshot.Existed);
        writer.Write(snapshot.Bytes.Length);
        writer.Write(snapshot.Bytes);
        writer.Write(snapshot.Sha256);
    }

    private static RawMemberSnapshot ReadSnapshot(BinaryReader reader)
    {
        string path = reader.ReadString();
        bool existed = reader.ReadBoolean();
        int length = reader.ReadInt32();
        if (length < 0 || length > 128 * 1024 * 1024)
            throw new InvalidDataException("Invalid file-entity recovery member length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return new RawMemberSnapshot(path, existed, bytes, reader.ReadString());
    }

    private static RecoveryEvidence ReadEvidence(BinaryReader reader)
    {
        RecoveryEvidenceState state = reader.ReadByte() switch
        {
            (byte)RecoveryEvidenceState.Prepared => RecoveryEvidenceState.Prepared,
            (byte)RecoveryEvidenceState.Committed => RecoveryEvidenceState.Committed,
            _ => throw new InvalidDataException("Unknown file-entity recovery evidence state.")
        };
        RawMemberSnapshot oldBody = ReadSnapshot(reader);
        RawMemberSnapshot oldMeta = ReadSnapshot(reader);
        string? archiveBody = ReadOptionalPath(reader);
        string? archiveMeta = ReadOptionalPath(reader);
        RawMemberSnapshot? committedBody = ReadOptionalSnapshot(reader);
        RawMemberSnapshot? committedMeta = ReadOptionalSnapshot(reader);
        if (state == RecoveryEvidenceState.Prepared && (committedBody is not null || committedMeta is not null))
            throw new InvalidDataException("Prepared recovery evidence contains post-commit snapshots.");
        return new RecoveryEvidence(state, oldBody, oldMeta, archiveBody, archiveMeta,
            committedBody, committedMeta);
    }

    private void ValidateEvidencePaths(RecoveryEvidence evidence, string entityDirectory)
    {
        string canonicalEntityDirectory = Path.GetFullPath(entityDirectory);
        ValidateEvidencePath(evidence.OldBody.Path, canonicalEntityDirectory);
        ValidateEvidencePath(evidence.OldMeta.Path, canonicalEntityDirectory);
        ValidateEvidencePath(evidence.ArchiveBody, canonicalEntityDirectory);
        ValidateEvidencePath(evidence.ArchiveMeta, canonicalEntityDirectory);
        if (evidence.CommittedBody is not null)
            ValidateEvidencePath(evidence.CommittedBody.Path, canonicalEntityDirectory);
        if (evidence.CommittedMeta is not null)
            ValidateEvidencePath(evidence.CommittedMeta.Path, canonicalEntityDirectory);
    }

    private static void ValidateEvidencePath(string? path, string entityDirectory)
    {
        if (path is null)
            return;
        string canonicalPath = Path.GetFullPath(path);
        if (!PathContainment.IsWithin(entityDirectory, canonicalPath))
            throw new InvalidDataException($"Recovery evidence path escapes the entity directory: {path}");
    }

    private static void VerifySnapshot(RawMemberSnapshot expected) =>
        Verify(expected.Path, !expected.Existed, expected.Bytes);

    private static RawMemberSnapshot ExpectedSnapshot(string path, bool deleted, byte[] bytes) =>
        RawMemberSnapshot.Capture(path, !deleted, deleted ? [] : bytes);

    private static RawMemberSnapshot? ReadOptionalSnapshot(BinaryReader reader) =>
        reader.ReadBoolean() ? ReadSnapshot(reader) : null;

    private static void WriteOptionalSnapshot(BinaryWriter writer, RawMemberSnapshot? snapshot)
    {
        writer.Write(snapshot is not null);
        if (snapshot is not null)
            WriteSnapshot(writer, snapshot);
    }

    private static void WriteOptionalPath(BinaryWriter writer, string? path)
    {
        writer.Write(path is not null);
        if (path is not null)
            writer.Write(path);
    }

    private static string? ReadOptionalPath(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadString() : null;

    private enum RecoveryEvidenceState : byte
    {
        Prepared,
        Committed
    }

    private sealed record RecoveryEvidence(
        RecoveryEvidenceState State,
        RawMemberSnapshot OldBody,
        RawMemberSnapshot OldMeta,
        string? ArchiveBody,
        string? ArchiveMeta,
        RawMemberSnapshot? CommittedBody,
        RawMemberSnapshot? CommittedMeta);

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
