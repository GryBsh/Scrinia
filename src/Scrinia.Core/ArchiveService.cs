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

    public ArchiveService(
        Func<string, string, string> artifactPath,
        Func<string, string> storeDirForScope,
        Func<string, string> sanitizeName,
        Func<string, string, string> metaPath)
    {
        _artifactPath = artifactPath;
        _storeDirForScope = storeDirForScope;
        _sanitizeName = sanitizeName;
        _metaPath = metaPath;
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
}
