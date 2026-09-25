using System;
using System.IO;
using System.Text;
using System.Threading;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;

namespace Scrinia.Core;

/// <summary>
/// Durable leaf writer for a single memory sidecar. Extracted from
/// <see cref="FileMemoryStore.WriteSidecarCore"/> (P9). Holds the atomic temp+move+dir-fsync
/// write and the read-only backstop gate; the sanitize and serialize steps stay on the store as
/// injected hooks because they own their own callers and reference store-private types.
/// </summary>
internal static class DurableWriter
{
    /// <summary>Atomically write <paramref name="entry"/>'s normalized sidecar JSON to
    /// <paramref name="storeDir"/>, refusing a read-only scope before touching disk.
    /// <paramref name="operationId"/> routes through the append fault-point variant when set.</summary>
    internal static void WriteSidecar(
        ArtifactEntry entry, string storeDir, string? operationId,
        Func<string, string> sanitizeName, Func<ArtifactEntry, string> serialize)
    {
        // Unconditional backstop, deliberately below every caller. After Task 3's forward-fill
        // skip and this task's two gates it should be unreachable from any path we enumerated --
        // and the previous Critical is precisely what happens when a trigger is closed and the
        // mechanism behind it is left reachable by a route nobody listed.
        if (ArtifactSidecarPolicy.IsReadOnly(entry))
            throw new ScopeReadOnlyException(
                storeDir, entry.Name, entry.SchemaVersion ?? 1, DurableFormats.ArtifactSidecar.Current);

        string metaPath = Path.Combine(storeDir, sanitizeName(entry.Name) + ".meta.json");
        string json = serialize(entry);
        if (operationId is null)
            DurableFile.AtomicWriteAllText(metaPath, json);
        else
            DurableFile.AtomicWriteAllTextForAppend(
                metaPath, json, operationId,
                AppendFaultPoint.LiveSidecarWrite,
                AppendFaultPoint.LiveSidecarFlush);
    }

    /// <summary>Atomically write <paramref name="subject"/>'s <c>.nmp2</c> artifact body to
    /// <see cref="artifactPath"/> under the append lock, refusing cancellation first and
    /// invalidating the artifact LRU cache after the write. <paramref name="operationId"/> routes
    /// through the append fault-point variant when set.</summary>
    internal static async Task WriteArtifact(string subject, string scope, string artifactText,
        string? operationId, Func<string, string, string> artifactPath, Action<string> invalidateArtifactCache,
        Action<string, string?, Action> runUnderAppendLock, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        runUnderAppendLock(scope, operationId, () =>
        {
            string path = artifactPath(subject, scope);
            if (operationId is null)
                DurableFile.AtomicWriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(artifactText));
            else
                DurableFile.AtomicWriteAllBytesForAppend(
                    path, System.Text.Encoding.UTF8.GetBytes(artifactText), operationId,
                    AppendFaultPoint.LiveArtifactWrite,
                    AppendFaultPoint.LiveArtifactFlush);
            invalidateArtifactCache($"{scope}|{subject}|");
        });
        await Task.CompletedTask;
    }

    /// <summary>Atomically copy <paramref name="sourcePath"/> to <paramref name="destPath"/> via a
    /// unique temp file + <see cref="FileRetry.Run"/> (survives NTFS sharing violations). Used by
    /// the archive-version path.</summary>
    internal static void AtomicFileCopy(string sourcePath, string destPath, bool overwrite = true)
    {
        string tmp = $"{destPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileRetry.Run(() => File.Copy(sourcePath, tmp, overwrite: true));
            FileRetry.Run(() => File.Move(tmp, destPath, overwrite: overwrite));
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>Best-effort, non-atomic flush of derived <c>facts.json</c>: a bare
    /// <c>File.WriteAllBytes</c> (no temp/move/fsync — the derived facts cache is rebuildable),
    /// swallowing any failure. <paramref name="writeObserver"/> is the store's optional test hook.</summary>
    internal static (bool Written, byte[] Bytes) TryWriteAllText(string path, string content,
        Action<string, byte[]>? writeObserver)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        try
        {
            File.WriteAllBytes(path, bytes);
            writeObserver?.Invoke(path, bytes);
            return (true, bytes);
        }
        catch
        {
            return (false, []);
        }
    }

    /// <summary>Delete the <c>.nmp2</c> artifact body for <paramref name="subject"/> in
    /// <paramref name="scope"/>, returning whether a file was removed. Refuses nothing: when the
    /// exact path is absent, it falls back to a case-insensitive directory enumeration with an
    /// ordinal tie-break (see below). <paramref name="artifactPath"/> and <paramref name="invalidateArtifactCache"/>
    /// are injected because they touch the store's path helper and artifact-text LRU cache.</summary>
    internal static bool DeleteArtifact(string subject, string scope,
        Func<string, string, string> artifactPath, Action<string> invalidateArtifactCache)
    {
        string path = artifactPath(subject, scope);
        if (!File.Exists(path))
        {
            // RULING (remote-mode-leftovers, final fixes, Finding 2): this fallback is the exact
            // counterpart of the one in Remove above.
            //
            // forget() calls Remove and DeleteArtifact as a pair (MemoryTools.Core.cs). Remove's
            // name match is case-insensitive; this one was not. On a case-sensitive filesystem
            // forget("mixedcase") against a memory stored as "MixedCase" therefore returned
            // removed=true, deleted=false — the caller reported "Forgot: mixedcase." and the tool
            // answered success, while MixedCase.nmp2 stayed on
            // disk with its sidecar gone: orphaned, unreachable through list() or search(), and
            // invisible to the next forget(). Before the sidecar lookup became case-insensitive both
            // halves returned false and the user got a clean "no artifact found"; fixing one half
            // and not the other turned a correct no-op into a partial delete reported as success.
            //
            // Fixed here rather than at the three forget() call sites: DeleteArtifact is a public
            // store primitive, the pairing with Remove is what has to agree, and the forget path
            // path (MemoryTools.Core.cs) sweeps every read scope with a name taken from an address and
            // has no entry in hand to canonicalize from. Aligning the primitive fixes all callers,
            // including any added later; canonicalizing at call sites fixes only the ones edited.
            //
            // Pinned by CaseSensitiveFileStoreTests.DeleteArtifact_DeletesCaseMismatchedName in
            // tests/Scrinia.Tests, which runs against a directory carrying NTFS's per-directory
            // case-sensitivity flag — so this is exercised on Windows, not only on a Linux CI leg.
            // The test skips (never silently passes) on a volume that cannot provide the flag. NTFS
            // folds case by default, so on an ordinary Windows volume the probe above always
            // succeeds and this branch is never entered; the guard test's directory opts out of
            // that default, which is what exercises it.
            //
            // Tie-break: identical to Remove's, and it matters for the same reason — this is a
            // delete, a case-sensitive filesystem holding pre-branch data can hold both
            // "Note.nmp2" and "note.nmp2", and this branch is only entered when the caller asked
            // for neither exactly. Directory enumeration order is not guaranteed, so candidates are
            // sorted ordinally and the first is taken. Ordinal sort puts uppercase first, so
            // "Note.nmp2" wins over "note.nmp2" — the same coin, decided the same way, as Remove's.
            string dir = Path.GetDirectoryName(path)!;
            string wanted = Path.GetFileName(path);
            string? actual = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.nmp2")
                    .Where(p => Path.GetFileName(p).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;
            if (actual is null) return false;
            path = actual;
        }

        File.Delete(path);
        // Invalidate covers the differently-cased key too: ArtifactLruCache.Invalidate matches its
        // prefix with OrdinalIgnoreCase, and read keys are "{scope}|{subject}|{ticks}".
        invalidateArtifactCache($"{scope}|{subject}|");
        return true;
    }

    /// <summary>Delete any <c>*.meta.json</c> in <paramref name="storeDir"/> whose stem is not in
    /// <paramref name="keptStems"/> — the orphan sidecar sweep run by <c>SaveIndex</c>.
    /// <paramref name="keptStems"/> must already be sanitized (the caller owns sanitizing, so the
    /// allow-list matches the physical leaf names the read-only gate and canonicalization produced).
    /// The enumeration is snapshotted with <c>.ToList()</c> before any delete so a sidecar written
    /// by the subsequent publish pass is never re-encountered; membership is case-insensitive to
    /// match the case-insensitive read side.</summary>
    internal static void SweepOrphanSidecars(string storeDir, IEnumerable<string> keptStems)
    {
        var expected = new HashSet<string>(
            keptStems.Select(s => s + ".meta.json"),
            StringComparer.OrdinalIgnoreCase);
        foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json").ToList())
            if (!expected.Contains(Path.GetFileName(metaFile)))
                File.Delete(metaFile);
    }

    /// <summary>Best-effort delete of an abandoned import-staging file: swallow I/O and
    /// access-denial failures. Import holds the scope's exclusive lock, so a present staging file
    /// is abandoned by definition; this never rethrows.</summary>
    internal static void TryDeleteStagingFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Writes the default <c>.scrinia/.gitignore</c> content atomically. Pure durable-write
    /// leaf: fixed content constant, no fault injection, no recovery orchestration. Caller must have
    /// already ensured the parent directory exists.</summary>
    internal static void WriteDefaultGitIgnore(string gitIgnorePath)
    {
        string content = """
            # Lock files (runtime artifacts from cross-process file locking)
            **/*.lock

            # Export bundles (generated, can be re-exported)
            exports/

            # Archived versions (auto-pruned, not source-controlled)
            **/versions/

            # Temporary files from interrupted writes
            **/*.tmp

            # Interrupted topic restore journals (recovered on the next access)
            **/.restore-journal.json

            # Vector indexes (derived from topic content; rebuild with `scri reindex`)
            embeddings/

            # Consolidation bookkeeping (rebuilt from sidecar state on next run)
            .last-consolidation
            .consolidate-progress.json
            """.Replace("            ", "");
        DurableFile.AtomicWriteAllText(gitIgnorePath, content);
    }
}
