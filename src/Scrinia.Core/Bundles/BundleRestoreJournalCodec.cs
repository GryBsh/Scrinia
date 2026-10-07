using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using TextEncoding = System.Text.Encoding;
using System.Text.Json;
using Scrinia.Core.Formats;

namespace Scrinia.Core.Bundles;

internal static class BundleRestoreJournalCodec
{
    internal static BundleRestoreJournal ReadValidated(string journalPath, string workspaceRoot, BundleReadLimits limits)
    {
        try
        {
            long cap = ReadCap(limits);
            RequireRegularFile(journalPath);
            using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Require(stream.Length <= cap, "Journal exceeds its byte limit.");
            using var buffer = new MemoryStream();
            byte[] block = new byte[8192];
            while (buffer.Length < cap)
            {
                int read = stream.Read(block, 0, (int)Math.Min(block.Length, cap - buffer.Length));
                if (read == 0) break;
                buffer.Write(block, 0, read);
            }
            Require(stream.Length <= cap, "Journal exceeds its byte limit.");
            using JsonDocument document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            RequireKeys(root, "format", "version", "operationId", "workspaceBinding", "state", "members", "manifestSha256");
            Require(root.GetProperty("version").TryGetInt32(out int version)
                && FormatVersion.Evaluate(DurableFormats.BundleRestoreJournal, version) == FormatDisposition.Load,
                "Unknown journal version.");
            JsonElement members = root.GetProperty("members");
            Require(members.ValueKind == JsonValueKind.Array && members.GetArrayLength() <= limits.JournalMembers,
                "Invalid journal members.");
            foreach (JsonElement member in members.EnumerateArray())
                RequireKeys(member, "relativePath", "kind", "beforeExists", "beforeBytes", "beforeSha256", "afterExists", "afterSha256");
            BundleRestoreJournal? journal = JsonSerializer.Deserialize(root, BundleRecoveryJsonContext.Default.BundleRestoreJournal);
            Require(journal is not null, "Missing journal.");
            Validate(journal!, workspaceRoot, limits);
            return journal!;
        }
        catch (Exception exception) when (IsInvalidEvidence(exception))
        {
            throw Recovery(exception, journalPath);
        }
    }

    internal static byte[] SerializeValidated(BundleRestoreJournal journal, string workspaceRoot, BundleReadLimits limits)
    {
        try
        {
            Validate(journal, workspaceRoot, limits);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, BundleRecoveryJsonContext.Default.BundleRestoreJournal);
            Require(bytes.LongLength <= ReadCap(limits), "Journal exceeds its byte limit.");
            return bytes;
        }
        catch (Exception exception) when (IsInvalidEvidence(exception)) { throw Recovery(exception); }
    }

    internal static void PreflightCurrentState(BundleRestoreJournal journal, string workspaceRoot, CancellationToken ct)
    {
        try
        {
            Validate(journal, workspaceRoot, BundleReadLimits.Default);
            foreach (BundleJournalMember member in journal.Members)
            {
                ct.ThrowIfCancellationRequested();
                string path = Authorize(member, workspaceRoot);
                bool exists;
                try { exists = (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
                catch (FileNotFoundException) { exists = false; }
                catch (DirectoryNotFoundException) { exists = false; }
                string? hash = null;
                if (exists)
                {
                    RequireRegularFile(path);
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] block = new byte[8192];
                    int count;
                    while ((count = stream.Read(block)) != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        hasher.AppendData(block, 0, count);
                    }
                    hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
                }
                bool before = exists == member.BeforeExists && hash == member.BeforeSha256;
                bool after = exists == member.AfterExists && hash == member.AfterSha256;
                Require(journal.State switch
                {
                    "prepared" => before || after,
                    "committed" => after,
                    "rolled-back" => before,
                    _ => false
                }, "A journal member has an unrecorded current state.");
            }
        }
        catch (Exception exception) when (IsInvalidEvidence(exception)) { throw Recovery(exception); }
    }

    internal static BundleRestoreJournal CreatePrepared(string operationId, string workspaceRoot, BundleJournalMember[] members, BundleReadLimits limits)
    {
        try
        {
            _ = ReadCap(limits);
            ValidateMembers(members, workspaceRoot, limits);
            var journal = new BundleRestoreJournal(DurableFormats.BundleRestoreJournal.Name,
                DurableFormats.BundleRestoreJournal.Current, operationId, WorkspaceBinding(workspaceRoot),
                "prepared", members!, ManifestHash(members!));
            _ = SerializeValidated(journal, workspaceRoot, limits);
            return journal;
        }
        catch (Exception exception) when (IsInvalidEvidence(exception)) { throw Recovery(exception); }
    }

    private static void Validate(BundleRestoreJournal journal, string workspaceRoot, BundleReadLimits limits)
    {
        _ = ReadCap(limits);
        Require(journal is not null, "Missing journal.");
        Require(journal!.Format == DurableFormats.BundleRestoreJournal.Name
            && FormatVersion.Evaluate(DurableFormats.BundleRestoreJournal, journal.Version) == FormatDisposition.Load,
            "Unknown journal format or version.");
        Require(!string.IsNullOrWhiteSpace(journal.OperationId) && TextEncoding.UTF8.GetByteCount(journal.OperationId) <= 64,
            "Invalid operation ID.");
        Require(IsHash(journal.WorkspaceBinding) && journal.WorkspaceBinding == WorkspaceBinding(workspaceRoot),
            "Journal belongs to a different workspace.");
        Require(journal.State is "prepared" or "committed" or "rolled-back", "Unknown journal state.");
        ValidateMembers(journal.Members, workspaceRoot, limits);
        Require(IsHash(journal.ManifestSha256) && journal.ManifestSha256 == ManifestHash(journal.Members!),
            "Invalid journal manifest hash.");
    }

    private static void ValidateMembers(BundleJournalMember[] members, string workspaceRoot, BundleReadLimits limits)
    {
        Require(members is not null && members.Length <= limits.JournalMembers, "Invalid member count.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long priorBytes = 0;
        foreach (BundleJournalMember member in members!)
        {
            Require(member is not null, "Missing member.");
            string path = Authorize(member!, workspaceRoot);
            Require(paths.Add(path), "Duplicate or aliased journal member.");
            priorBytes = checked(priorBytes + (member!.BeforeBytes?.LongLength ?? 0));
            Require(priorBytes <= limits.PriorBytes, "Prior member payloads exceed their byte limit.");
            Require(member!.BeforeExists
                ? member.BeforeBytes is not null && IsHash(member.BeforeSha256) && Hash(member.BeforeBytes) == member.BeforeSha256
                : member.BeforeBytes is null && member.BeforeSha256 is null,
                $"Invalid prior member payload: {member.RelativePath}.");
            Require(member.AfterExists ? IsHash(member.AfterSha256) : member.AfterSha256 is null, "Invalid after state.");
        }
    }

    internal static string Authorize(BundleJournalMember member, string workspaceRoot)
    {
        string relative = member.RelativePath;
        Require(!string.IsNullOrEmpty(relative) && TextEncoding.UTF8.GetByteCount(relative) <= 4096
            && !relative.Contains('\\') && !Path.IsPathRooted(relative), "Invalid relative path.");
        string[] parts = relative.Split('/');
        Require(parts.Length >= 3 && parts[0] == ".scrinia"
            && parts.All(p => p.Length > 0 && p is not "." and not ".." && !p.Contains(':')),
            "Invalid journal path segments.");
        string fileName = parts[^1];
        string expected;
        if (member.Kind is "memory-body" or "memory-meta")
        {
            Require(parts.Length >= 3 && parts[1] == "memories", "Invalid memory target.");
            Require(!parts[2..^1].Any(p => string.Equals(p, "versions", StringComparison.OrdinalIgnoreCase)),
                "Archive directories are not live journal targets.");
            string suffix = member.Kind == "memory-body" ? ".nmp2" : ".meta.json";
            Require(fileName.EndsWith(suffix, StringComparison.Ordinal) && fileName.Length > suffix.Length,
                "Invalid memory member extension.");
            string name = fileName[..^suffix.Length];
            Require(MemoryNaming.SanitizeName(name) == name, "Invalid memory member name.");
            string scope = parts.Length == 3 ? "local" : "local:" + string.Join('/', parts[2..^1]);
            expected = Path.Combine(PathRouter.ToStoreDir(scope, workspaceRoot), fileName);
        }
        else
        {
            Require(member.Kind is "skill-body" or "skill-meta" or "agent-body" or "agent-meta", "Unknown member kind.");
            string kind = member.Kind.StartsWith("skill-", StringComparison.Ordinal) ? "skill" : "agent";
            string suffix = member.Kind.EndsWith("-body", StringComparison.Ordinal) ? ".md" : ".meta.json";
            Require(parts.Length == 3 && fileName.EndsWith(suffix, StringComparison.Ordinal) && fileName.Length > suffix.Length,
                "Invalid file entity member.");
            if (!PathRouter.TryToFileEntityPath(kind, fileName[..^suffix.Length], workspaceRoot, out string body))
                throw Recovery(new InvalidDataException("Invalid or uncontained file entity target."));
            expected = suffix == ".md" ? body : Path.ChangeExtension(body, ".meta.json");
        }
        string full = Path.GetFullPath(Path.Combine(workspaceRoot, relative));
        Require(string.Equals(full, Path.GetFullPath(expected), StringComparison.Ordinal)
            && PathContainment.IsWithin(workspaceRoot, full), "Unauthorized journal target.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        string current = full;
        bool target = true;
        while (true)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw Recovery(new InvalidDataException("Journal target traverses a symbolic link."));
                if (target ? (attributes & FileAttributes.Directory) != 0 : (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Journal target is not a regular contained file.");
                if (target) RequireRegularFile(current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (current == root) break;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Missing workspace parent.");
            target = false;
        }
        return full;
    }

    private static void RequireRegularFile(string path)
    {
        Require((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0,
            "Recovery content is not a regular file.");
        if (OperatingSystem.IsWindows()) return;

        // System.Native normalizes stat layouts across Unix platforms/architectures.
        // ABI and mode constants: dotnet/runtime v10.0.0, src/native/libs/System.Native/pal_io.h
        // https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_io.h
        try
        {
            if (LStat(path, out UnixFileStatus status) != 0)
                throw new IOException($"Cannot classify recovery content (errno {Marshal.GetLastPInvokeError()}).");
            const int fileTypeMask = 0xF000; // PAL_S_IFMT
            const int regularFile = 0x8000; // PAL_S_IFREG
            Require((status.Mode & fileTypeMask) == regularFile, "Recovery content is not a regular file.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or PlatformNotSupportedException)
        {
            throw new InvalidDataException("Regular-file classification is unavailable.", exception);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixFileStatus
    {
        internal int Flags;
        internal int Mode;
        internal uint Uid;
        internal uint Gid;
        internal long Size;
        internal long ATime;
        internal long ATimeNsec;
        internal long MTime;
        internal long MTimeNsec;
        internal long CTime;
        internal long CTimeNsec;
        internal long BirthTime;
        internal long BirthTimeNsec;
        internal long Dev;
        internal long RDev;
        internal long Ino;
        internal uint UserFlags;
    }

    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    [SuppressMessage("Globalization", "CA2101", Justification = "System.Native paths use explicitly marshalled UTF-8, not UTF-16.")]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixFileStatus status);

    private static string WorkspaceBinding(string workspaceRoot)
    {
        string canonical = Path.TrimEndingDirectorySeparator(PathContainment.ResolveCanonical(workspaceRoot));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        return Hash(TextEncoding.UTF8.GetBytes(canonical));
    }

    private static string ManifestHash(BundleJournalMember[] members) => Hash(JsonSerializer.SerializeToUtf8Bytes(
        members.OrderBy(m => m.RelativePath, StringComparer.Ordinal).ToArray(), BundleRecoveryJsonContext.Default.BundleJournalMemberArray));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static long ReadCap(BundleReadLimits limits)
    {
        Require(limits.PriorBytes >= 0 && limits.JournalMembers >= 0, "Invalid journal limits.");
        return checked(4 * ((limits.PriorBytes + 2) / 3) + (long)limits.JournalMembers * (6 * 4096 + 1024) + 4096);
    }

    private static void RequireKeys(JsonElement element, params string[] required)
    {
        Require(element.ValueKind == JsonValueKind.Object, "Expected a journal object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            Require(names.Add(property.Name), "Duplicate journal property.");
        Require(required.All(names.Contains), "Missing required journal property.");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }

    private static bool IsInvalidEvidence(Exception exception) =>
        exception is InvalidDataException or IOException or JsonException or ArgumentException or InvalidOperationException or OverflowException
        && exception is not BundleImportException;

    private static BundleImportException Recovery(Exception exception, string? journalPath = null) =>
        new(BundleImportFailure.RecoveryRequired, exception, journalPath);
}
