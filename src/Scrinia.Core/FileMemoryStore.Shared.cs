using Scrinia.Core.Models;

namespace Scrinia.Core;

public sealed partial class FileMemoryStore
{
    internal static FileMemoryStore CreateShared(string root) => new(root)
    {
        // Legacy namespaces belong to the shared authoritative tree. Shared operations never
        // opt into the local store's automatic layout migration, including during mutation.
        _layoutChecked = true
    };

    internal WorkspaceLease EnterSharedRead(CancellationToken ct = default)
    {
        // Validate descendant topology before the ordinary store's recovery/discovery code can
        // inspect it. This checks names and attributes only, never authoritative contents.
        Configuration.SharedBaseRuntime.ValidateTopology(_workspaceRoot);
        PrepareSharedSnapshot();
        WorkspaceLease lease = _workspaceAdmission.EnterRead(ImportLockTimeout, ct);
        try
        {
            foreach (string kind in new[] { "skill", "agent" })
            {
                string directory = PathRouter.ToFileEntityDir(kind, _workspaceRoot);
                if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, ".file-entity-*.recovery").Any())
                    throw new Configuration.SharedOperationException("SHARED_BASE_UNAVAILABLE");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal void PrepareSharedSnapshot()
    {
        // This shared instance suppresses layout migration. The normal read boundary still
        // owns retained scope evidence and verified read preparation.
        // Materializing this inventory does not become the shared server's live read source.
        _ = ListScoped();
    }

    internal ArtifactEntry StoreShared(WorkspaceLease lease, MemoryWrite write, CancellationToken ct)
    {
        lease.AssertValid(_workspaceRoot);
        write.ValidateOrThrow(_writeValidationOptions);
        try { return StorePersistentAsync(write, ct, admittedLease: lease).GetAwaiter().GetResult(); }
        catch (WholeMemoryTransactionException exception) when (exception.Code == WholeMemoryErrorCode.Internal)
        { throw new Configuration.SharedOperationException("DURABLE_WRITE_FAILED"); }
    }

    internal ArtifactEntry AppendShared(WorkspaceLease lease, MemoryWrite write, CancellationToken ct)
    {
        lease.AssertValid(_workspaceRoot);
        var result = AppendAdmittedAsync(write, lease, ct).GetAwaiter().GetResult();
        return result.Entry ?? throw new Configuration.SharedOperationException("DURABLE_WRITE_FAILED", Configuration.SharedMutationCertainty.Unknown);
    }

    internal void DeleteShared(WorkspaceLease lease, MemoryAddress address, CancellationToken ct)
    {
        lease.AssertValid(_workspaceRoot);
        var (scope, subject) = ResolveAddress(address);
        WithExclusiveLock(lease, scope, () =>
        {
            RemoveUnderLease(lease, subject, scope);
            return DeleteArtifactUnsafe(subject, scope);
        }, cancellationToken: ct);
    }

    internal (string Body, string Meta) SharedPaths(MemoryAddress address)
    {
        var (scope, subject) = ResolveAddress(address);
        return (ArtifactPath(subject, scope), MetaPath(subject, scope));
    }
}
