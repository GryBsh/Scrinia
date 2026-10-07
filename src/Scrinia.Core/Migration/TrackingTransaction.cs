using System.Security.Cryptography;
using System.Text;
using Scrinia.Core.Configuration;

namespace Scrinia.Core.Migration;

/// <summary>Publishes a complete four-member tracking decision under workspace and configuration locks.</summary>
internal static class TrackingTransaction
{
    internal static LegacyMigrationResult CommitLocked(string workspaceRoot, WorkspaceLease lease,
        string operation, IReadOnlyDictionary<string, byte[]?> after)
    {
        workspaceRoot = TrackingRecoveryJournal.ResolveWorkspaceRoot(workspaceRoot);
        lease.AssertValid(workspaceRoot, requireExclusive: true);
        string path = JournalPath(workspaceRoot);
        TrackingRecoveryJournal.ThrowIfPresent(workspaceRoot);
        if (after.Count != TrackingRecoveryJournal.MemberNames.Count ||
            !TrackingRecoveryJournal.MemberNames.All(after.ContainsKey))
            throw new ArgumentException("Tracking requires the complete fixed member set.", nameof(after));

        // Every carrier, before image, after image and bound is checked before evidence is published.
        var journal = new TrackingJournal(1, Guid.NewGuid().ToString("N"), operation, "prepared",
            TrackingRecoveryJournal.MemberNames.Select(name => new TrackingMember(name,
                CaptureState(MemberPath(workspaceRoot, name)), State(after[name]?.ToArray()))).ToArray());
        byte[] prepared = TrackingRecoveryJournal.Serialize(journal);
        if (journal.Members.All(member => Same(member.Before, member.After)))
            return new();

        bool commitAttempted = false;
        try
        {
            DurableFile.AtomicWriteAllBytes(path, prepared);
            foreach (TrackingMember member in journal.Members)
                Apply(MemberPath(workspaceRoot, member.Path), member.After);
            Verify(workspaceRoot, journal, after: true);
            var committed = journal with { Decision = "committed" };
            byte[] committedBytes = TrackingRecoveryJournal.Serialize(committed);
            commitAttempted = true;
            DurableFile.AtomicWriteAllBytes(path, committedBytes);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            try
            {
                if (commitAttempted)
                {
                    // Readability is not durability: only a verified committed replacement may be
                    // republished, and its durable acknowledgement is required before success.
                    TrackingJournal retained = TrackingRecoveryJournal.Read(path, workspaceRoot);
                    RequireSame(journal with { Decision = "committed" }, retained);
                    Verify(workspaceRoot, retained, after: true);
                    DurableFile.AtomicWriteAllBytes(path, TrackingRecoveryJournal.Serialize(retained));
                    Verify(workspaceRoot, retained, after: true);
                }
                else
                {
                    if (TrackingRecoveryJournal.InspectPath(path, file: true))
                    {
                        TrackingJournal retained = TrackingRecoveryJournal.Read(path, workspaceRoot);
                        RequireSame(journal, retained);
                        RestorePrepared(workspaceRoot, retained);
                        RemovePreparedEvidence(path, retained);
                    }
                    else
                        Verify(workspaceRoot, journal, after: false);
                    RefreshCache(workspaceRoot);
                    return new(ConfigurationErrorCode.ConfigWriteFailed, ConfigurationExitClass.WriteFailure,
                        Exception: failure);
                }
            }
            catch (Exception recoveryFailure) when (recoveryFailure is IOException or InvalidDataException or UnauthorizedAccessException
                                                    or ScriniaConfigurationException)
            {
                return new(ConfigurationErrorCode.ConfigRecoveryRequired, ConfigurationExitClass.WriteFailure,
                    Exception: new AggregateException("Tracking write and recovery both failed.", failure,
                        new TrackingRecoveryRequiredException("Tracking decision requires recovery.", path, recoveryFailure)));
            }
        }

        // The committed decision is durable. Cleanup and cache failures cannot roll it back.
        try
        {
            RefreshCache(workspaceRoot);
            DurableFile.DeleteAndSync(path);
            return new(Changed: true);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or ScriniaConfigurationException)
        {
            return new(Changed: true, Warnings: ["Tracking committed; recovery evidence cleanup remains pending."],
                Exception: failure);
        }
    }

    internal static void RecoverLocked(string workspaceRoot, WorkspaceLease lease)
    {
        workspaceRoot = TrackingRecoveryJournal.ResolveWorkspaceRoot(workspaceRoot);
        lease.AssertValid(workspaceRoot, requireExclusive: true);
        string path = JournalPath(workspaceRoot);
        try
        {
            bool journalPresent = TrackingRecoveryJournal.InspectPath(path, file: true);
            // Even without evidence, policy reads must not open unsafe member carriers.
            foreach (string name in TrackingRecoveryJournal.MemberNames)
                TrackingRecoveryJournal.InspectPath(MemberPath(workspaceRoot, name), file: true);
            if (!journalPresent) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var input = TrackingRecoveryJournal.ReadBounded(stream);
            stream.Dispose();
            ReadOnlySpan<byte> bytes = input.GetBuffer().AsSpan(0, checked((int)input.Length));
            if (bytes.StartsWith("prepared\n"u8))
            {
                RestoreLegacy(workspaceRoot, bytes);
                DurableFile.DeleteAndSync(path);
            }
            else
            {
                TrackingJournal journal = TrackingRecoveryJournal.Read(path, workspaceRoot);
                if (journal.Decision == "prepared")
                {
                    RestorePrepared(workspaceRoot, journal);
                    RemovePreparedEvidence(path, journal);
                }
                else
                {
                    Verify(workspaceRoot, journal, after: true);
                    DurableFile.DeleteAndSync(path);
                }
            }
            RefreshCache(workspaceRoot);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException
                                        or FormatException or ArgumentException or ScriniaConfigurationException)
        {
            throw new TrackingRecoveryRequiredException("Tracking evidence could not be recovered.", path, failure);
        }
    }

    internal static TrackingState CaptureState(string path)
    {
        TrackingRecoveryJournal.InspectPath(path, file: true);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > TrackingRecoveryJournal.MaxStateBytes)
                throw new IOException("Tracking member exceeds its state limit.");
            using var buffer = new MemoryStream();
            byte[] block = new byte[64 * 1024];
            int count;
            while ((count = stream.Read(block, 0, (int)Math.Min(block.Length,
                TrackingRecoveryJournal.MaxStateBytes - buffer.Length + 1))) != 0)
            {
                if (buffer.Length + count > TrackingRecoveryJournal.MaxStateBytes)
                    throw new IOException("Tracking member exceeds its state limit.");
                buffer.Write(block, 0, count);
            }
            return State(buffer.ToArray());
        }
        catch (FileNotFoundException) { return State(null); }
        catch (DirectoryNotFoundException) { return State(null); }
    }

    internal static bool Matches(string path, TrackingState state) => Same(CaptureState(path), state);

    internal static void Apply(string path, TrackingState state)
    {
        TrackingRecoveryJournal.InspectPath(path, file: true);
        if (state.Exists) DurableFile.AtomicWriteAllBytes(path, state.Bytes!);
        else DurableFile.DeleteAndSync(path);
    }

    internal static void Verify(string workspaceRoot, TrackingJournal journal, bool after)
    {
        foreach (TrackingMember member in journal.Members)
            if (!Matches(MemberPath(workspaceRoot, member.Path), after ? member.After : member.Before))
                throw new TrackingRecoveryRequiredException("Tracking member does not match the recorded decision.",
                    JournalPath(workspaceRoot));
    }

    private static void RestorePrepared(string workspaceRoot, TrackingJournal journal)
    {
        // Validate all current states before changing the first member.
        foreach (TrackingMember member in journal.Members)
        {
            TrackingState current = CaptureState(MemberPath(workspaceRoot, member.Path));
            if (!Same(current, member.Before) && !Same(current, member.After))
                throw new TrackingRecoveryRequiredException("Tracking member has an unknown third state.",
                    JournalPath(workspaceRoot));
        }
        foreach (TrackingMember member in journal.Members)
            Apply(MemberPath(workspaceRoot, member.Path), member.Before);
        Verify(workspaceRoot, journal, after: false);
    }

    private static void RestoreLegacy(string workspaceRoot, ReadOnlySpan<byte> bytes)
    {
        string[] lines = new UTF8Encoding(false, true).GetString(bytes).Split('\n');
        if (lines.Length != 6 || lines[0] != "prepared" || lines[^1] != "")
            throw new FormatException("Legacy tracking evidence is incomplete.");
        var states = new Dictionary<string, TrackingState>(StringComparer.Ordinal);
        foreach (string line in lines[1..^1])
        {
            string[] fields = line.Split('|');
            if (fields.Length != 2) throw new FormatException("Invalid legacy tracking record.");
            // Matching the exact encoded canonical path also proves strict UTF-8/base64 without
            // allocating or interpreting an attacker-provided path.
            string? name = TrackingRecoveryJournal.MemberNames.FirstOrDefault(name =>
                fields[0] == Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(MemberPath(workspaceRoot, name))));
            if (name is null || !states.TryAdd(name, State(fields[1] == "-" ? null :
                    StrictBase64(fields[1], TrackingRecoveryJournal.MaxStateBytes))))
                throw new FormatException("Legacy tracking paths are not the fixed canonical target set.");
        }
        // Old journals contain no after hashes: carrier/read validation is the maximum available proof.
        foreach (string name in TrackingRecoveryJournal.MemberNames)
            _ = CaptureState(MemberPath(workspaceRoot, name));
        foreach ((string name, TrackingState state) in states)
            Apply(MemberPath(workspaceRoot, name), state);
        foreach ((string name, TrackingState state) in states)
            if (!Matches(MemberPath(workspaceRoot, name), state))
                throw new IOException("Legacy tracking restoration could not be verified.");
    }

    private static byte[] StrictBase64(string encoded, long maximum)
    {
        if (encoded.Length > ((maximum + 2) / 3) * 4)
            throw new FormatException("Legacy tracking payload exceeds its bound.");
        byte[] bytes = Convert.FromBase64String(encoded);
        if (bytes.LongLength > maximum || Convert.ToBase64String(bytes) != encoded)
            throw new FormatException("Legacy tracking payload is not canonical base64.");
        return bytes;
    }

    private static TrackingState State(byte[]? bytes) => bytes is null
        ? new(false, 0, null, null)
        : new(true, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), bytes);

    private static bool Same(TrackingState left, TrackingState right) =>
        left.Exists == right.Exists && left.Length == right.Length &&
        (left.Bytes is null ? right.Bytes is null : right.Bytes is not null && left.Bytes.AsSpan().SequenceEqual(right.Bytes));

    private static void RequireSame(TrackingJournal expected, TrackingJournal actual)
    {
        if (!TrackingRecoveryJournal.Serialize(expected).AsSpan().SequenceEqual(TrackingRecoveryJournal.Serialize(actual)))
            throw new TrackingRecoveryRequiredException("Retained tracking evidence changed.");
    }

    private static void RemovePreparedEvidence(string path, TrackingJournal journal)
    {
        try { DurableFile.DeleteAndSync(path); }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A failed deletion may already have removed the name. Re-publish the same prepared
            // proof so a subsequent process can verify and finish the proven restoration.
            try { DurableFile.AtomicWriteAllBytes(path, TrackingRecoveryJournal.Serialize(journal)); }
            catch (Exception retentionFailure) when (retentionFailure is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw new TrackingRecoveryRequiredException("Tracking rollback cleanup and evidence retention failed.",
                    path, new AggregateException(failure, retentionFailure));
            }
            throw new TrackingRecoveryRequiredException("Tracking rollback cleanup requires recovery.", path, failure);
        }
    }

    private static void RefreshCache(string workspaceRoot)
    {
        WorkspaceConfig.InvalidateCache(workspaceRoot);
        try { WorkspaceConfig.RefreshCache(workspaceRoot, WorkspaceConfig.LoadValuesUncached(workspaceRoot)); }
        catch (ScriniaConfigurationException)
        {
            // A faithfully restored before image may itself be malformed (including an empty file).
            // The old cache was invalidated; fresh loads and uncached inspection report that error.
        }
    }

    private static string MemberPath(string root, string name) => Path.Combine(root, ".scrinia", name);
    private static string JournalPath(string root) => MemberPath(root, TrackingRecoveryJournal.FileName);
}
