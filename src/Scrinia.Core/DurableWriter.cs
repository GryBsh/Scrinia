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
}
