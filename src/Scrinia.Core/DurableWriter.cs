using System;
using System.IO;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

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
}
