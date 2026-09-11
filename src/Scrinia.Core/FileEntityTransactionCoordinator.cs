using System.Security.Cryptography;

namespace Scrinia.Core;

/// <summary>Publishes one file entity's body and metadata as a recoverable pair.</summary>
internal sealed class FileEntityTransactionCoordinator : IFileEntityTransactionCoordinator
{
    private readonly string _workspaceRoot;
    private readonly object _faultGate = new();
    private FileEntityFaultPoint? _faultPoint;

    internal FileEntityTransactionCoordinator(string workspaceRoot)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    }

    internal void FailOnceAt(FileEntityFaultPoint point)
    {
        lock (_faultGate)
            _faultPoint = point;
    }

    public async Task<FileEntityMutationResult> ExecuteAsync(
        FileEntityMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidatePaths(mutation);
        cancellationToken.ThrowIfCancellationRequested();

        string operationId = Guid.NewGuid().ToString("N");
        string lockPath = Path.Combine(Path.GetDirectoryName(mutation.BodyPath)!, ".lock");
        using var fileLock = FileLock.AcquireExclusive(lockPath);

        RawMemberSnapshot oldBody = Capture(mutation.BodyPath, FileEntityFaultPoint.RawBodyRead);
        RawMemberSnapshot oldMeta = Capture(mutation.MetaPath, FileEntityFaultPoint.RawMetaRead);
        string? archiveBody = null;
        string? archiveMeta = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (oldBody.Existed || oldMeta.Existed)
            {
                (archiveBody, archiveMeta) = BuildArchivePaths(mutation);
                Archive(oldBody, archiveBody, FileEntityFaultPoint.ArchiveBodyWrite);
                Archive(oldMeta, archiveMeta, FileEntityFaultPoint.ArchiveMetaWrite);
            }

            Publish(mutation.BodyPath, mutation.DeleteBody, mutation.NewBody, FileEntityFaultPoint.BodyWrite);
            ThrowIfFault(FileEntityFaultPoint.AfterBodyDelete);
            Publish(mutation.MetaPath, mutation.DeleteMeta, mutation.NewMeta ?? [], FileEntityFaultPoint.MetaWrite);
            ThrowIfFault(FileEntityFaultPoint.VerificationRead);
            Verify(mutation.BodyPath, mutation.DeleteBody, mutation.NewBody);
            Verify(mutation.MetaPath, mutation.DeleteMeta, mutation.NewMeta ?? []);

            return new FileEntityMutationResult(
                oldBody.Existed || oldMeta.Existed ? WholeMemoryOutcome.Replaced : WholeMemoryOutcome.Created,
                operationId, null, null);
        }
        catch (Exception failure)
        {
            try
            {
                Restore(oldBody);
                Restore(oldMeta);
                Delete(archiveBody);
                Delete(archiveMeta);
            }
            catch (Exception rollbackFailure)
            {
                string evidencePath = Path.Combine(Path.GetDirectoryName(mutation.BodyPath)!,
                    $".file-entity-{operationId}.recovery");
                return new FileEntityMutationResult(
                    WholeMemoryOutcome.RecoveryRequired,
                    operationId,
                    "File-entity publication requires recovery.",
                    new RecoveryInfo(evidencePath, "recoveryRequired", "resolve retained evidence",
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

    private void Archive(RawMemberSnapshot snapshot, string path, FileEntityFaultPoint point)
    {
        if (!snapshot.Existed)
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

        if (!File.Exists(path) || !Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
                .Equals(Convert.ToHexStringLower(SHA256.HashData(expected)), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"File-entity member verification failed: {path}");
    }

    private static void Restore(RawMemberSnapshot snapshot)
    {
        if (!snapshot.Existed)
        {
            File.Delete(snapshot.Path);
            return;
        }

        DurableFile.AtomicWriteAllBytes(snapshot.Path, snapshot.Bytes);
    }

    private static void Delete(string? path)
    {
        if (path is not null)
            File.Delete(path);
    }

    private (string Body, string Meta) BuildArchivePaths(FileEntityMutation mutation)
    {
        Directory.CreateDirectory(mutation.VersionsDirectory);
        string stem = Path.GetFileNameWithoutExtension(mutation.BodyPath);
        string extension = Path.GetExtension(mutation.BodyPath);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        string body = Path.Combine(mutation.VersionsDirectory, $"{stem}_{timestamp}{extension}");
        int suffix = 1;
        while (File.Exists(body) || File.Exists(Path.ChangeExtension(body, ".meta.json")))
            body = Path.Combine(mutation.VersionsDirectory, $"{stem}_{timestamp}-{suffix++}{extension}");
        return (body, Path.ChangeExtension(body, ".meta.json"));
    }

    private void ThrowIfFault(FileEntityFaultPoint point)
    {
        lock (_faultGate)
        {
            if (_faultPoint != point)
                return;
            _faultPoint = null;
        }

        throw new IOException($"Injected file-entity fault at {point}.");
    }
}
