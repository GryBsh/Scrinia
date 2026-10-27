using System.Globalization;

namespace Scrinia.Core;

/// <summary>
/// Owns the single-memory version-archive leaf moved off <see cref="FileMemoryStore"/> in P17.
/// Holds no <see cref="FileMemoryStore"/> instance — only the injected path/name hooks the
/// store exposes as frozen primitives. Runs under the caller's exclusive lock.
/// </summary>
internal sealed class ArchiveService
{
    private readonly Func<string, string, string> _artifactPath;
    private readonly Func<string, string> _storeDirForScope;
    private readonly Func<string, string> _sanitizeName;
    private readonly Func<string, string, string> _metaPath;
    private readonly Func<AppendFaultPoint, string, string, Exception?> _injectAppendFault;

    public ArchiveService(
        Func<string, string, string> artifactPath,
        Func<string, string> storeDirForScope,
        Func<string, string> sanitizeName,
        Func<string, string, string> metaPath,
        Func<AppendFaultPoint, string, string, Exception?> injectAppendFault)
    {
        _artifactPath = artifactPath;
        _storeDirForScope = storeDirForScope;
        _sanitizeName = sanitizeName;
        _metaPath = metaPath;
        _injectAppendFault = injectAppendFault;
    }

    /// <summary>Archive the current version of one memory's artifact (and best-effort its sidecar)
    /// into <c>versions/</c> under a millisecond-stamped, never-overwriting name, then prune stale
    /// bodies/sidecars beyond the 10-most-recent retention.</summary>
    public void ArchiveVersionUnsafe(string subject, string scope)
    {
        string currentPath = _artifactPath(subject, scope);
        if (!File.Exists(currentPath))
            return;

        string storeDir = _storeDirForScope(scope);
        string versionsDir = Path.Combine(storeDir, "versions");
        Directory.CreateDirectory(versionsDir);

        // Stamped to the MILLISECOND, and never overwriting. A second-granularity stamp copied
        // with overwrite:true meant two archives of one subject inside the same second silently
        // replaced each other — losing a prior body from the only directory backing the promise
        // that a write never destroys what was already there. Two overwrites in a row is the
        // ordinary shape of an agent correcting itself mid-turn, not a contrived race.
        // Milliseconds still collide under a tight loop, so the counter is a floor, not a
        // flourish: the invariant is that this method never replaces an existing archive.
        string safe = _sanitizeName(subject);
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string baseName = $"{safe}_{stamp}";
        string archivePath = Path.Combine(versionsDir, $"{baseName}.nmp2");
        for (int n = 2; File.Exists(archivePath); n++)
            archivePath = Path.Combine(versionsDir, $"{baseName}-{n}.nmp2");

        DurableWriter.AtomicFileCopy(currentPath, archivePath, overwrite: false);

        // The sidecar travels WITH the body. Archiving the artifact alone made the documented
        // recovery in docs/server-admin.md corrupting rather than restoring: copying a body back
        // left the .meta.json describing the newer content, and StoreAsync's own comment records
        // where that lands — a later append reads the stale ChunkEntries and numbers the appended
        // chunk from the real count, yielding non-contiguous indices. Best-effort: a missing or
        // unreadable sidecar must not fail the write this archive is protecting.
        string metaPath = _metaPath(subject, scope);
        if (File.Exists(metaPath))
        {
            try { DurableWriter.AtomicFileCopy(metaPath, Path.ChangeExtension(archivePath, null) + ".meta.json", overwrite: false); }
            catch (IOException) { /* body is archived; metadata is a best-effort companion */ }
            catch (UnauthorizedAccessException) { }
        }

        // Prune old versions — keep only the 10 most recent, and take each body's sidecar with it
        // so pruning cannot leave a .meta.json describing an artifact that is gone.
        var stale = Directory.GetFiles(versionsDir, $"{safe}_*.nmp2")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Skip(10)
            .ToList();
        foreach (var old in stale)
        {
            try { File.Delete(old); } catch { /* best-effort cleanup */ }
            try { File.Delete(Path.ChangeExtension(old, null) + ".meta.json"); } catch { }
        }
    }

    /// <summary>Prune the stale archive members a committed append planned for deletion. Shared
    /// durable-write prune conductor moved off <see cref="FileMemoryStore"/> in P30; holds no store
    /// instance — only the injected append-fault seam the store exposes as the static
    /// <c>InjectAppendFault</c>.</summary>
    public ArchiveCleanupResult PruneCommittedArchives(AppendTransaction transaction, CancellationToken ct)
    {
        var failures = new List<string>();
        if (ct.IsCancellationRequested)
            return new ArchiveCleanupResult(true, ["archive cleanup cancelled."]);

        Exception? cleanupFailure = _injectAppendFault(
            AppendFaultPoint.ArchiveCleanup, transaction.OperationId, transaction.Scope);
        if (cleanupFailure is not null)
            return new ArchiveCleanupResult(
                true,
                [$"archive cleanup: {cleanupFailure.Message}"]);

        string versionsRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(transaction.Archive.ArtifactArchivePath ?? transaction.NewArtifact.Path)!, "..", "versions"));
        string liveArtifact = Path.GetFullPath(transaction.NewArtifact.Path);
        string liveSidecar = Path.GetFullPath(transaction.NewSidecar.Path);
        string? currentArtifact = transaction.Archive.ArtifactArchivePath is null
            ? null : Path.GetFullPath(transaction.Archive.ArtifactArchivePath);
        string? currentSidecar = transaction.Archive.SidecarArchivePath is null
            ? null : Path.GetFullPath(transaction.Archive.SidecarArchivePath);

        foreach (string candidate in transaction.Archive.PruneCandidates)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                string fullCandidate = Path.GetFullPath(candidate);
                if (!IsSafePruneCandidate(versionsRoot, fullCandidate,
                    liveArtifact, liveSidecar, currentArtifact, currentSidecar))
                {
                    failures.Add($"refused unsafe archive candidate: {candidate}");
                    break;
                }
                Exception? injected = _injectAppendFault(
                    AppendFaultPoint.PruningDelete, transaction.OperationId, fullCandidate);
                if (injected is not null)
                    throw injected;
                File.Delete(fullCandidate);
            }
            catch (OperationCanceledException exception)
            {
                failures.Add($"pruning cancelled: {exception.Message}");
                break;
            }
            catch (Exception exception)
            {
                failures.Add($"{candidate}: {exception.Message}");
                break;
            }
        }

        return new ArchiveCleanupResult(failures.Count > 0, failures.ToArray());
    }

    // Callers resolve every path before applying the same retention boundary during commit or recovery.
    internal static bool IsSafePruneCandidate(string versionsRoot, string candidate,
        string liveArtifact, string liveSidecar, string? currentArtifact, string? currentSidecar) =>
        PathContainment.IsWithin(versionsRoot, candidate) &&
        !string.Equals(candidate, liveArtifact, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(candidate, liveSidecar, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(candidate, currentArtifact, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(candidate, currentSidecar, StringComparison.OrdinalIgnoreCase);
}

internal sealed record ArchiveCleanupResult(bool Failed, string[] Failures)
{
    internal string Detail => Failures.Length == 0
        ? "Archive cleanup did not complete."
        : $"Archive cleanup did not complete: {string.Join("; ", Failures)}";
}
