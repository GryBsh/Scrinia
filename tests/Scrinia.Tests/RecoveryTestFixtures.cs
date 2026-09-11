using System.Security.Cryptography;
using System.Text;

namespace Scrinia.Tests;

/// <summary>Deterministic bytes and layouts shared by the B2 recovery compatibility tests.</summary>
internal static class RecoveryTestFixtures
{
    internal const string LocalNotesScope = "local:notes";
    internal const string LocalOtherScope = "local:other";
    internal const string CanonicalAddress = "/notes/note";
    internal const string OperationId = "01JOURNAL000000000000000001";

    internal static readonly byte[] OldArtifact = [0x01, 0x02, 0x03, 0x04];
    internal static readonly byte[] NewArtifact = [0x11, 0x12, 0x13, 0x14];
    internal static readonly byte[] OldSidecar = Encoding.UTF8.GetBytes("{\"v\":1}");
    internal static readonly byte[] NewSidecar = Encoding.UTF8.GetBytes("{\"v\":2}");
    internal static readonly byte[] ArchiveArtifact = [0x21, 0x22, 0x23];
    internal static readonly byte[] ArchiveSidecar = Encoding.UTF8.GetBytes("archive-meta");
    internal static readonly byte[] FileEntityBody = Encoding.UTF8.GetBytes("entity body\n");
    internal static readonly byte[] FileEntityMetadata = Encoding.UTF8.GetBytes("{\"createdAt\":\"2026-09-11\"}");

    internal static string RestoreJournalPath(string scopeDirectory) =>
        Path.Combine(scopeDirectory, ".restore-journal.json");

    internal static string AppendJournalPath(string scopeDirectory) =>
        Path.Combine(scopeDirectory, "versions", ".append-journal.json");

    internal static string FileEntityRecoveryPath(string entityDirectory) =>
        Path.Combine(entityDirectory, $".file-entity-{OperationId}.recovery");

    internal static string WriteLegacyRestoreJournal(string scopeDirectory, string state = "prepared") =>
        WriteBytes(RestoreJournalPath(scopeDirectory), BuildLegacyRestoreJournal(state: state));

    internal static string WriteLegacyAppendJournal(string scopeDirectory, string state = "prepared") =>
        WriteBytes(AppendJournalPath(scopeDirectory), BuildLegacyAppendJournal(state: state));

    internal static string WriteLegacyFileEntityRecovery(string entityDirectory, bool committed = false) =>
        WriteBytes(FileEntityRecoveryPath(entityDirectory), BuildLegacyFileEntityRecovery(committed));

    internal static byte[] BuildLegacyRestoreJournal(
        string scope = LocalNotesScope, string state = "prepared") =>
        Encoding.UTF8.GetBytes($$"""
            {"format":"scrinia.restore-journal","version":1,"state":"{{state}}","transactionId":"{{OperationId}}","scope":"{{scope}}","entries":[]}
            """);

    internal static byte[] BuildLegacyAppendJournal(
        string scope = LocalNotesScope, string state = "prepared") =>
        Encoding.UTF8.GetBytes($$"""
            {"format":"scrinia.append-journal","version":1,"operationId":"{{OperationId}}","address":"{{CanonicalAddress}}","scope":"{{scope}}","state":"{{state}}","isCreate":false,"oldArtifact":{"path":"notes.nmp2","existed":true,"payload":"{{Convert.ToBase64String(OldArtifact)}}","sha256":"{{Hash(OldArtifact)}}"},"oldSidecar":{"path":"notes.meta.json","existed":true,"payload":"{{Convert.ToBase64String(OldSidecar)}}","sha256":"{{Hash(OldSidecar)}}"},"newArtifact":{"path":"notes.nmp2","existed":true,"payload":"{{Convert.ToBase64String(NewArtifact)}}","sha256":"{{Hash(NewArtifact)}}"},"newSidecar":{"path":"notes.meta.json","existed":true,"payload":"{{Convert.ToBase64String(NewSidecar)}}","sha256":"{{Hash(NewSidecar)}}"},"archiveArtifactPath":"versions/notes.nmp2","archiveSidecarPath":"versions/notes.meta.json","archiveArtifactStatus":"planned","archiveSidecarStatus":"planned","pruneCandidates":[],"startedAt":"2026-09-11T00:00:00Z","committedHash":null,"detail":null}
            """);

    internal static byte[] BuildLegacyFileEntityRecovery(bool committed = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("SCRINIA-FILE-ENTITY-RECOVERY-1");
        writer.Write((byte)(committed ? 1 : 0));
        WriteSnapshot(writer, "entity.md", FileEntityBody);
        WriteSnapshot(writer, "entity.meta.json", FileEntityMetadata);
        writer.Write(false);
        writer.Write(false);
        writer.Write(committed);
        if (committed)
            WriteSnapshot(writer, "entity.md", FileEntityBody);
        writer.Write(committed);
        if (committed)
            WriteSnapshot(writer, "entity.meta.json", FileEntityMetadata);
        return stream.ToArray();
    }

    internal static IReadOnlyList<(string RelativePath, byte[] Bytes)> SnapshotScope(
        string scopeDirectory)
    {
        if (!Directory.Exists(scopeDirectory))
            return [];

        return Directory.EnumerateFiles(scopeDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(scopeDirectory, path), File.ReadAllBytes(path)))
            .OrderBy(item => item.Item1, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WriteSnapshot(BinaryWriter writer, string path, byte[] bytes)
    {
        writer.Write(path);
        writer.Write(true);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        writer.Write(Hash(bytes));
    }

    private static string WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
