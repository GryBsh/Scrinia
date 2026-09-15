using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Scrinia.Core;

internal enum MaintenanceCandidateKind
{
    ArchivePair = 1,
    CompletedRecoveryEvidence = 2
}

internal readonly record struct MaintenanceWorkspaceKey(Guid Value);
internal readonly record struct MaintenanceRunId(Guid Value);
internal readonly record struct MaintenanceSnapshotId(Guid Value);
internal readonly record struct MaintenanceScopeBinding(Guid Value);
internal readonly record struct MaintenanceCandidateKey(Guid Value);

internal readonly struct MaintenanceHash256 : IEquatable<MaintenanceHash256>
{
    internal MaintenanceHash256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32)
            throw new ArgumentException("A maintenance hash must contain exactly 32 bytes.", nameof(bytes));
        Bytes = bytes.ToArray();
    }

    internal ReadOnlyMemory<byte> Bytes { get; }

    public bool Equals(MaintenanceHash256 other) => Bytes.Span.SequenceEqual(other.Bytes.Span);
    public override bool Equals(object? obj) => obj is MaintenanceHash256 other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new System.HashCode();
        foreach (byte value in Bytes.Span) hash.Add(value);
        return hash.ToHashCode();
    }
}

internal enum MaintenanceCursorPhase : byte
{
    Discover = 1,
    Complete = 2
}

internal readonly record struct MaintenanceCursor(
    byte Version,
    MaintenanceWorkspaceKey Workspace,
    MaintenanceRunId Run,
    MaintenanceSnapshotId Snapshot,
    MaintenanceScopeBinding Scopes,
    ushort ScopeOrdinal,
    MaintenanceCursorPhase Phase,
    MaintenanceCandidateKey AfterCandidate,
    uint PageSequence,
    MaintenanceHash256 PolicyHash,
    ulong ExpiresUnixSeconds);

internal readonly record struct MaintenanceCandidateIdentity(
    MaintenanceCandidateKind Kind,
    MaintenanceWorkspaceKey Workspace,
    ushort ScopeOrdinal,
    MaintenanceHash256 SubjectIdentity,
    ulong ImmutableEntryOrdinal);

internal static class MaintenanceLimits
{
    internal const int CandidatePageSize = 128;
    internal const int UnsafeScanLimit = 32;
    internal const int LookaheadSize = 1;
    internal const int MetadataReadLimit = CandidatePageSize;
    internal const int ReturnedProjectionLimit = 64;
    internal const int JournalBudgetMaximum = 50;
    internal const int TerminalVerificationMaximumBytes = 8 * 1024 * 1024;
    internal const int TerminalVerificationMaximumMilliseconds = 250;
}

internal enum MaintenanceVerificationStatus { Verified, ProtectedEvidence }
internal sealed record MaintenanceVerificationResult(MaintenanceVerificationStatus Status, MaintenanceProtectionReason Reason)
{
    internal bool IsVerified => Status == MaintenanceVerificationStatus.Verified;
}

internal sealed record MaintenanceCandidateProjection(
    MaintenanceCandidateKey CandidateKey,
    MaintenanceCandidateKind Kind,
    string Scope,
    string Subject,
    string SafeRelativeBodyPath,
    string? SafeRelativeSidecarPath,
    MaintenanceProtectionReason? Protection,
    bool Eligible);

internal sealed record MaintenanceDiscoveryPage(
    ImmutableArray<MaintenanceCandidateProjection> Projections,
    MaintenanceCursor? Continuation,
    int Scanned,
    int Projected,
    int MetadataReads,
    int Returned,
    int Lookahead,
    bool HasMore,
    int UnsafeScanned,
    int UnsafeJournalScanned,
    int UnsafeNonJournalScanned,
    int JournalBudget,
    int JournalBudgetConsumed,
    int JournalInspected,
    int JournalDeleted,
    int NonJournalInspected,
    bool JournalCapReached,
    int JournalDeferred);

internal static class MaintenanceCursorCodec
{
    private const int PayloadLength = 128;
    private const int MacLength = 32;
    private const string Prefix = "b5c2.";
    private static readonly byte[] CandidateDomain = "scrinia/b5/candidate-key/v2"u8.ToArray();
    private static readonly byte[] WorkspaceDomain = "scrinia/b5/workspace-key/v2"u8.ToArray();

    internal static string Encode(MaintenanceCursor cursor, ReadOnlySpan<byte> signingKey)
    {
        ValidateCursor(cursor);
        byte[] payload = new byte[PayloadLength];
        payload[0] = cursor.Version;
        WriteGuid(payload.AsSpan(1), cursor.Workspace.Value);
        WriteGuid(payload.AsSpan(17), cursor.Run.Value);
        WriteGuid(payload.AsSpan(33), cursor.Snapshot.Value);
        WriteGuid(payload.AsSpan(49), cursor.Scopes.Value);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(65), cursor.ScopeOrdinal);
        payload[67] = (byte)cursor.Phase;
        WriteGuid(payload.AsSpan(68), cursor.AfterCandidate.Value);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(84), cursor.PageSequence);
        cursor.PolicyHash.Bytes.Span.CopyTo(payload.AsSpan(88));
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(120), cursor.ExpiresUnixSeconds);
        byte[] authenticated = PrefixBytes().Concat(payload).ToArray();
        byte[] mac = HMACSHA256.HashData(signingKey, authenticated);
        return Prefix + Convert.ToBase64String(payload.Concat(mac).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryDecode(string token, ReadOnlySpan<byte> signingKey,
        out MaintenanceCursor cursor, out string? error)
    {
        cursor = default;
        error = null;
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return Fail(out error, "Continuation token has an invalid prefix.");
        string encoded = token[Prefix.Length..];
        if (encoded.Length == 0 || encoded.Contains('=') || encoded.Any(char.IsWhiteSpace))
            return Fail(out error, "Continuation token is not canonical base64url.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4)); }
        catch (FormatException) { return Fail(out error, "Continuation token is not canonical base64url."); }
        if (bytes.Length != PayloadLength + MacLength)
            return Fail(out error, "Continuation token has an invalid length.");
        string canonical = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!string.Equals(canonical, encoded, StringComparison.Ordinal))
            return Fail(out error, "Continuation token is not canonical base64url.");
        byte[] expected = HMACSHA256.HashData(signingKey, PrefixBytes().Concat(bytes.AsSpan(0, PayloadLength).ToArray()).ToArray());
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(PayloadLength)))
            return Fail(out error, "Continuation token authentication failed.");
        try
        {
            ReadOnlySpan<byte> payload = bytes.AsSpan(0, PayloadLength);
            cursor = new MaintenanceCursor(payload[0],
                new MaintenanceWorkspaceKey(ReadGuid(payload[1..17])),
                new MaintenanceRunId(ReadGuid(payload[17..33])),
                new MaintenanceSnapshotId(ReadGuid(payload[33..49])),
                new MaintenanceScopeBinding(ReadGuid(payload[49..65])),
                BinaryPrimitives.ReadUInt16BigEndian(payload[65..67]),
                (MaintenanceCursorPhase)payload[67],
                new MaintenanceCandidateKey(ReadGuid(payload[68..84])),
                BinaryPrimitives.ReadUInt32BigEndian(payload[84..88]),
                new MaintenanceHash256(payload[88..120]),
                BinaryPrimitives.ReadUInt64BigEndian(payload[120..128]));
            ValidateCursor(cursor);
            if (cursor.ExpiresUnixSeconds <= (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Fail(out error, "Continuation token has expired.");
            return true;
        }
        catch (ArgumentException exception) { return Fail(out error, exception.Message); }
    }

    internal static string Encode(MaintenanceCursor cursor, MaintenanceRunRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!registry.TryGet(cursor.Run, out MaintenanceRunBinding? binding) || binding is null || !binding.Matches(cursor))
            throw new InvalidOperationException("Invalid continuation.");
        return Encode(cursor, binding.SigningKey.Span);
    }

    internal static bool TryDecode(string token, MaintenanceRunRegistry registry,
        out MaintenanceCursor cursor, out string? error)
    {
        cursor = default;
        error = null;
        if (!TryReadRunId(token, out MaintenanceRunId runId))
            return Fail(out error, "Invalid continuation.");
        if (!registry.TryGet(runId, out MaintenanceRunBinding? binding) || binding is null)
            return Fail(out error, "Invalid continuation.");
        if (!TryDecode(token, binding.SigningKey.Span, out cursor, out _))
            return Fail(out error, "Invalid continuation.");
        if (!binding.Matches(cursor))
        {
            cursor = default;
            return Fail(out error, "Invalid continuation.");
        }
        return true;
    }

    private static bool TryReadRunId(string token, out MaintenanceRunId runId)
    {
        runId = default;
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string encoded = token[Prefix.Length..];
        if (encoded.Length != 214) return false;
        try
        {
            byte[] bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "==");
            if (bytes.Length != PayloadLength + MacLength) return false;
            runId = new MaintenanceRunId(ReadGuid(bytes.AsSpan(17, 16)));
            return true;
        }
        catch (FormatException) { return false; }
    }

    internal static MaintenanceCandidateKey ComputeCandidateKey(MaintenanceWorkspaceKey workspace,
        MaintenanceCandidateIdentity identity, ReadOnlySpan<byte> signingKey)
    {
        using var input = new MemoryStream();
        input.Write(CandidateDomain);
        WriteGuid(input, workspace.Value);
        Span<byte> scalar = stackalloc byte[11];
        BinaryPrimitives.WriteUInt16BigEndian(scalar, identity.ScopeOrdinal);
        scalar[2] = (byte)identity.Kind;
        BinaryPrimitives.WriteUInt64BigEndian(scalar[3..], identity.ImmutableEntryOrdinal);
        input.Write(scalar[..3]);
        input.Write(identity.SubjectIdentity.Bytes.Span);
        input.Write(scalar[3..]);
        return new MaintenanceCandidateKey(ReadRawGuid(HMACSHA256.HashData(signingKey, input.ToArray()).AsSpan(0, 16)));
    }

    internal static MaintenanceWorkspaceKey ComputeWorkspaceKey(ReadOnlySpan<byte> canonicalWorkspaceBinding,
        ReadOnlySpan<byte> signingKey)
    {
        if (canonicalWorkspaceBinding.Length > 1_048_576)
            throw new ArgumentException("Workspace binding is too large.", nameof(canonicalWorkspaceBinding));
        byte[] input = new byte[WorkspaceDomain.Length + 4 + canonicalWorkspaceBinding.Length];
        WorkspaceDomain.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(WorkspaceDomain.Length), (uint)canonicalWorkspaceBinding.Length);
        canonicalWorkspaceBinding.CopyTo(input.AsSpan(WorkspaceDomain.Length + 4));
        return new MaintenanceWorkspaceKey(ReadRawGuid(HMACSHA256.HashData(signingKey, input).AsSpan(0, 16)));
    }

    private static void ValidateCursor(MaintenanceCursor cursor)
    {
        if (cursor.Version != 2) throw new ArgumentException("Unsupported maintenance cursor version.");
        if (!Enum.IsDefined(cursor.Phase)) throw new ArgumentException("Invalid maintenance cursor phase.");
        if (cursor.PolicyHash.Bytes.Length != 32) throw new ArgumentException("Invalid maintenance policy hash.");
        if (cursor.Phase == MaintenanceCursorPhase.Complete &&
            (cursor.AfterCandidate.Value != Guid.Empty || cursor.PageSequence != 0))
            throw new ArgumentException("A complete cursor must have zero continuation state.");
    }

    private static byte[] PrefixBytes() => System.Text.Encoding.ASCII.GetBytes(Prefix);
    private static bool Fail(out string? error, string message) { error = message; return false; }
    private static void WriteGuid(Span<byte> destination, Guid value) => value.TryWriteBytes(destination, bigEndian: true, out _);
    private static void WriteGuid(Stream stream, Guid value) { Span<byte> bytes = stackalloc byte[16]; WriteGuid(bytes, value); stream.Write(bytes); }
    private static Guid ReadGuid(ReadOnlySpan<byte> bytes) => new(bytes, bigEndian: true);
    private static Guid ReadRawGuid(ReadOnlySpan<byte> bytes) => new(bytes);
}

internal sealed class MaintenanceRunBinding
{
    private readonly byte[] _signingKey;

    internal MaintenanceRunBinding(MaintenanceWorkspaceKey workspace, MaintenanceRunId run,
        MaintenanceSnapshotId snapshot, MaintenanceScopeBinding scopes, MaintenanceHash256 policyHash,
        ulong expiresUnixSeconds, byte[] signingKey)
    {
        Workspace = workspace;
        Run = run;
        Snapshot = snapshot;
        Scopes = scopes;
        PolicyHash = policyHash;
        ExpiresUnixSeconds = expiresUnixSeconds;
        _signingKey = signingKey;
    }

    internal MaintenanceWorkspaceKey Workspace { get; }
    internal MaintenanceRunId Run { get; }
    internal MaintenanceSnapshotId Snapshot { get; }
    internal MaintenanceScopeBinding Scopes { get; }
    internal MaintenanceHash256 PolicyHash { get; }
    internal ulong ExpiresUnixSeconds { get; }
    internal ReadOnlyMemory<byte> SigningKey => _signingKey.ToArray();

    internal bool Matches(MaintenanceCursor cursor) =>
        cursor.Workspace == Workspace && cursor.Run == Run && cursor.Snapshot == Snapshot &&
        cursor.Scopes == Scopes && cursor.PolicyHash.Equals(PolicyHash) &&
        cursor.ExpiresUnixSeconds == ExpiresUnixSeconds;

    internal void Clear() => CryptographicOperations.ZeroMemory(_signingKey);
}

internal sealed class MaintenanceRunRegistry : IDisposable
{
    private readonly ConcurrentDictionary<MaintenanceRunId, MaintenanceRunBinding> _runs = new();
    private readonly byte[] _processKey = RandomNumberGenerator.GetBytes(32);
    private int _disposed;

    internal MaintenanceRunBinding Create(MaintenanceWorkspaceKey workspace, MaintenanceRunId run,
        MaintenanceSnapshotId snapshot, MaintenanceScopeBinding scopes, MaintenanceHash256 policyHash,
        DateTimeOffset expiresAt)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ulong expiry = checked((ulong)expiresAt.ToUnixTimeSeconds());
        if (expiry <= (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new ArgumentException("Maintenance run expiry must be in the future.", nameof(expiresAt));
        byte[] input = new byte[16];
        run.Value.TryWriteBytes(input, bigEndian: true, out _);
        byte[] key = HMACSHA256.HashData(_processKey, input);
        var binding = new MaintenanceRunBinding(workspace, run, snapshot, scopes, policyHash, expiry, key);
        _runs.AddOrUpdate(run, binding, (_, replaced) =>
        {
            replaced.Clear();
            return binding;
        });
        return binding;
    }

    internal bool TryGet(MaintenanceRunId run, out MaintenanceRunBinding? binding)
    {
        binding = null;
        if (Volatile.Read(ref _disposed) != 0) return false;
        if (!_runs.TryGetValue(run, out MaintenanceRunBinding? found)) return false;
        if (found.ExpiresUnixSeconds <= (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            Remove(run);
            return false;
        }
        binding = found;
        return true;
    }

    internal bool Remove(MaintenanceRunId run)
    {
        if (!_runs.TryRemove(run, out MaintenanceRunBinding? removed)) return false;
        removed.Clear();
        return true;
    }

    internal int EvictExpired()
    {
        ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int removed = 0;
        foreach ((MaintenanceRunId run, MaintenanceRunBinding binding) in _runs)
            if (binding.ExpiresUnixSeconds <= now && Remove(run)) removed++;
        return removed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (MaintenanceRunId run in _runs.Keys) Remove(run);
        CryptographicOperations.ZeroMemory(_processKey);
    }
}

internal enum JournalBoundaryLocation : byte { Restore = 1, AppendVersions = 2, FileEntity = 3 }
internal enum JournalBoundaryNameClass : byte { RestoreJournal = 1, AppendJournal = 2, FileEntityRecovery = 3 }
internal enum JournalDirectoryEntryKind : byte { File = 1, Directory = 2 }
internal enum JournalBoundaryProvenance : byte { BufferedDirectoryEntryMetadataOnly = 1 }
internal enum JournalBoundaryRejection : byte
{
    None = 0, WrongEntryKind, UnsafeOrMismatchedSafeRoot, WrongRun, WrongSnapshot,
    WrongEnumerator, BoundaryMismatch, CanonicalEntryKeyMismatch, NonJournalSyntax,
    MalformedMetadata, NonMetadataProvenance, InvalidPresence, StaleOrdinal,
    JournalBudgetRemaining, PostInspection
}

internal readonly record struct MaintenanceEnumeratorId(Guid Value);
internal readonly record struct JournalBoundaryValidationContext(
    MaintenanceRunId ActiveRunId,
    MaintenanceSnapshotId ActiveSnapshotId,
    MaintenanceScopeBinding ActiveScopeBinding,
    MaintenanceEnumeratorId ActiveEnumeratorId,
    ushort ActiveScopeOrdinal,
    MaintenanceCandidateKey? LastAdmission,
    uint CurrentPageSequence,
    MaintenanceCandidateKey ExpectedCanonicalEntryKey,
    JournalDirectoryEntryKind ExpectedEntryKind,
    int JournalBudgetRemaining,
    ulong LastDirectoryEntryOrdinal = 0);

internal sealed class JournalEnumeratorBoundary
{
    private readonly JournalBoundaryCapability _capability;

    private JournalEnumeratorBoundary(JournalBoundaryCapability capability) => _capability = capability;
    internal MaintenanceRunId RunId => _capability.RunId;
    internal MaintenanceSnapshotId SnapshotId => _capability.SnapshotId;
    internal MaintenanceScopeBinding SafeRootBinding => _capability.SafeRootBinding;
    internal MaintenanceEnumeratorId EnumeratorId => _capability.EnumeratorId;
    internal ushort ScopeOrdinal => _capability.ScopeOrdinal;
    internal MaintenanceCandidateKey? AfterCandidate => _capability.AfterCandidate;
    internal uint PageSequence => _capability.PageSequence;
    internal ulong DirectoryEntryOrdinal => _capability.DirectoryEntryOrdinal;
    internal MaintenanceCandidateKey CanonicalEntryKey => _capability.CanonicalEntryKey;
    internal JournalBoundaryLocation Location => _capability.Location;
    internal JournalBoundaryNameClass NameClass => _capability.NameClass;
    internal JournalDirectoryEntryKind EntryKind => _capability.EntryKind;
    internal JournalBoundaryProvenance Provenance => JournalBoundaryProvenance.BufferedDirectoryEntryMetadataOnly;
    internal bool IsLive => _capability.IsLive;
    internal void Consume() => _capability.Consume();

    internal static JournalEnumeratorBoundary FromCapability(JournalBoundaryCapability capability) =>
        new(capability);
}

internal abstract class JournalBoundaryCapability
{
    private protected JournalBoundaryCapability() { }
    internal abstract MaintenanceRunId RunId { get; }
    internal abstract MaintenanceSnapshotId SnapshotId { get; }
    internal abstract MaintenanceScopeBinding SafeRootBinding { get; }
    internal abstract MaintenanceEnumeratorId EnumeratorId { get; }
    internal abstract ushort ScopeOrdinal { get; }
    internal abstract MaintenanceCandidateKey? AfterCandidate { get; }
    internal abstract uint PageSequence { get; }
    internal abstract ulong DirectoryEntryOrdinal { get; }
    internal abstract MaintenanceCandidateKey CanonicalEntryKey { get; }
    internal abstract JournalBoundaryLocation Location { get; }
    internal abstract JournalBoundaryNameClass NameClass { get; }
    internal abstract JournalDirectoryEntryKind EntryKind { get; }
    internal abstract bool IsLive { get; }
    internal abstract void Consume();
}

internal sealed class JournalBoundaryEnumerator : IDisposable
{
    private sealed class EnumeratorCapability : JournalBoundaryCapability
    {
        private bool _isLive = true;
        internal EnumeratorCapability(MaintenanceRunId runId, MaintenanceSnapshotId snapshotId,
            MaintenanceScopeBinding safeRootBinding, MaintenanceEnumeratorId enumeratorId, ushort scopeOrdinal,
            MaintenanceCandidateKey? afterCandidate, uint pageSequence, ulong ordinal,
            MaintenanceCandidateKey key, JournalBoundaryLocation location,
            JournalBoundaryNameClass nameClass, JournalDirectoryEntryKind kind)
        { RunId = runId; SnapshotId = snapshotId; SafeRootBinding = safeRootBinding; EnumeratorId = enumeratorId; ScopeOrdinal = scopeOrdinal; AfterCandidate = afterCandidate; PageSequence = pageSequence; DirectoryEntryOrdinal = ordinal; CanonicalEntryKey = key; Location = location; NameClass = nameClass; EntryKind = kind; }
        internal override MaintenanceRunId RunId { get; }
        internal override MaintenanceSnapshotId SnapshotId { get; }
        internal override MaintenanceScopeBinding SafeRootBinding { get; }
        internal override MaintenanceEnumeratorId EnumeratorId { get; }
        internal override ushort ScopeOrdinal { get; }
        internal override MaintenanceCandidateKey? AfterCandidate { get; }
        internal override uint PageSequence { get; }
        internal override ulong DirectoryEntryOrdinal { get; }
        internal override MaintenanceCandidateKey CanonicalEntryKey { get; }
        internal override JournalBoundaryLocation Location { get; }
        internal override JournalBoundaryNameClass NameClass { get; }
        internal override JournalDirectoryEntryKind EntryKind { get; }
        internal override bool IsLive => _isLive;
        internal override void Consume() => _isLive = false;
    }

    private readonly MaintenanceRunId _runId;
    private readonly MaintenanceSnapshotId _snapshotId;
    private readonly MaintenanceScopeBinding _root;
    private readonly MaintenanceEnumeratorId _id = new(Guid.NewGuid());
    private readonly ushort _scopeOrdinal;
    private readonly uint _pageSequence;
    private readonly MaintenanceCandidateKey? _after;
    private ulong _lastOrdinal;
    private JournalEnumeratorBoundary? _current;
    internal MaintenanceEnumeratorId Id => _id;

    private JournalBoundaryEnumerator(MaintenanceRunId runId, MaintenanceSnapshotId snapshotId,
        MaintenanceScopeBinding root, ushort scopeOrdinal, uint pageSequence, MaintenanceCandidateKey? after)
    { _runId = runId; _snapshotId = snapshotId; _root = root; _scopeOrdinal = scopeOrdinal; _pageSequence = pageSequence; _after = after; }

    internal static JournalBoundaryEnumerator Create(MaintenanceRunId runId, MaintenanceSnapshotId snapshotId,
        MaintenanceScopeBinding root, ushort scopeOrdinal, uint pageSequence, MaintenanceCandidateKey? after) =>
        new(runId, snapshotId, root, scopeOrdinal, pageSequence, after);

    internal JournalEnumeratorBoundary? BufferJournal(ulong ordinal, MaintenanceCandidateKey key,
        JournalBoundaryLocation location, JournalBoundaryNameClass nameClass, JournalDirectoryEntryKind kind)
    {
        if (ordinal <= _lastOrdinal || ordinal == 0) return null;
        _current?.Consume();
        _lastOrdinal = ordinal;
        _current = JournalEnumeratorBoundary.FromCapability(new EnumeratorCapability(
            _runId, _snapshotId, _root, _id, _scopeOrdinal, _after, _pageSequence, ordinal, key, location, nameClass, kind));
        return _current;
    }

    internal void Advance() => _current?.Consume();
    public void Dispose() => Advance();
}

internal sealed class JournalBoundaryProof
{
    private JournalBoundaryProof(JournalEnumeratorBoundary boundary)
    {
        RunId = boundary.RunId;
        SnapshotId = boundary.SnapshotId;
        SafeRootBinding = boundary.SafeRootBinding;
        EnumeratorId = boundary.EnumeratorId;
        ScopeOrdinal = boundary.ScopeOrdinal;
        AfterCandidate = boundary.AfterCandidate;
        PageSequence = boundary.PageSequence;
        DirectoryEntryOrdinal = boundary.DirectoryEntryOrdinal;
        CanonicalEntryKey = boundary.CanonicalEntryKey;
        Location = boundary.Location;
        NameClass = boundary.NameClass;
        EntryKind = boundary.EntryKind;
        Provenance = boundary.Provenance;
    }

    internal MaintenanceRunId RunId { get; }
    internal MaintenanceSnapshotId SnapshotId { get; }
    internal MaintenanceScopeBinding SafeRootBinding { get; }
    internal MaintenanceEnumeratorId EnumeratorId { get; }
    internal ushort ScopeOrdinal { get; }
    internal MaintenanceCandidateKey? AfterCandidate { get; }
    internal uint PageSequence { get; }
    internal ulong DirectoryEntryOrdinal { get; }
    internal JournalBoundaryLocation Location { get; }
    internal JournalBoundaryNameClass NameClass { get; }
    internal JournalDirectoryEntryKind EntryKind { get; }
    internal JournalBoundaryProvenance Provenance { get; }
    internal MaintenanceCandidateKey CanonicalEntryKey { get; }

    internal static bool TryCaptureFromBufferedMetadata(JournalEnumeratorBoundary boundary,
        out JournalBoundaryProof proof)
    {
        proof = null!;
        if (!boundary.IsLive || boundary.Provenance != JournalBoundaryProvenance.BufferedDirectoryEntryMetadataOnly ||
            boundary.EntryKind != JournalDirectoryEntryKind.File || !IsFixedJournalPair(boundary.Location, boundary.NameClass))
            return false;
        proof = new JournalBoundaryProof(boundary);
        boundary.Consume();
        return true;
    }

    internal static bool ValidateJournalBoundaryProof(in JournalBoundaryProof proof,
        in JournalBoundaryValidationContext context, out JournalBoundaryRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(proof);
        rejection = JournalBoundaryRejection.None;
        if (proof.EntryKind != context.ExpectedEntryKind) return Reject(ref rejection, JournalBoundaryRejection.WrongEntryKind);
        if (proof.SafeRootBinding != context.ActiveScopeBinding) return Reject(ref rejection, JournalBoundaryRejection.UnsafeOrMismatchedSafeRoot);
        if (proof.RunId != context.ActiveRunId) return Reject(ref rejection, JournalBoundaryRejection.WrongRun);
        if (proof.SnapshotId != context.ActiveSnapshotId) return Reject(ref rejection, JournalBoundaryRejection.WrongSnapshot);
        if (proof.EnumeratorId != context.ActiveEnumeratorId) return Reject(ref rejection, JournalBoundaryRejection.WrongEnumerator);
        if (proof.ScopeOrdinal != context.ActiveScopeOrdinal || proof.PageSequence != context.CurrentPageSequence ||
            proof.AfterCandidate != context.LastAdmission) return Reject(ref rejection, JournalBoundaryRejection.BoundaryMismatch);
        if (proof.CanonicalEntryKey != context.ExpectedCanonicalEntryKey) return Reject(ref rejection, JournalBoundaryRejection.CanonicalEntryKeyMismatch);
        if (!IsFixedJournalPair(proof.Location, proof.NameClass)) return Reject(ref rejection, JournalBoundaryRejection.NonJournalSyntax);
        if (proof.Provenance != JournalBoundaryProvenance.BufferedDirectoryEntryMetadataOnly) return Reject(ref rejection, JournalBoundaryRejection.NonMetadataProvenance);
        if (proof.DirectoryEntryOrdinal == 0) return Reject(ref rejection, JournalBoundaryRejection.MalformedMetadata);
        if (context.LastAdmission is not null && proof.CanonicalEntryKey == context.LastAdmission.Value)
            return Reject(ref rejection, JournalBoundaryRejection.StaleOrdinal);
        if (proof.DirectoryEntryOrdinal <= context.LastDirectoryEntryOrdinal)
            return Reject(ref rejection, JournalBoundaryRejection.StaleOrdinal);
        if (context.JournalBudgetRemaining != 0) return Reject(ref rejection, JournalBoundaryRejection.JournalBudgetRemaining);
        return true;
    }

    private static bool Reject(ref JournalBoundaryRejection rejection, JournalBoundaryRejection value)
    {
        rejection = value;
        return false;
    }

    private static bool IsFixedJournalPair(JournalBoundaryLocation location, JournalBoundaryNameClass nameClass) =>
        (location, nameClass) is
            (JournalBoundaryLocation.Restore, JournalBoundaryNameClass.RestoreJournal) or
            (JournalBoundaryLocation.AppendVersions, JournalBoundaryNameClass.AppendJournal) or
            (JournalBoundaryLocation.FileEntity, JournalBoundaryNameClass.FileEntityRecovery);
}

internal enum MaintenanceProtectionReason
{
    Current,
    Active,
    Referenced,
    ExplicitlyProtected,
    UnresolvedRecovery,
    IncompletePair,
    Malformed,
    UnsupportedVersion,
    Ambiguous,
    Unsafe,
    Inaccessible,
    Unverifiable,
    YoungerThanMinimumAge,
    WithinRetentionLimit,
    MissingExpectedMember,
    InvalidContinuation,
    VerificationBudgetExceeded,
    DeletionFailed
}

internal sealed record MaintenanceRequest
{
    internal MaintenanceRequest(IReadOnlyList<string>? scopes, int? batchSizeOverride,
        IReadOnlySet<string>? explicitlyProtectedRelativePaths, string? continuationToken)
    {
        Scopes = scopes is null ? null : MaintenanceContractValidation.CopyScopes(scopes);
        BatchSizeOverride = ValidateBatchSize(batchSizeOverride);
        ExplicitlyProtectedRelativePaths = explicitlyProtectedRelativePaths is null
            ? MaintenanceScopes.EmptyProtectionSet
            : MaintenanceContractValidation.CopyRelativePathSet(explicitlyProtectedRelativePaths);
        ContinuationToken = continuationToken is null
            ? null
            : MaintenanceContractValidation.RequireToken(continuationToken, nameof(continuationToken));
    }

    public IReadOnlyList<string>? Scopes { get; }
    public int? BatchSizeOverride { get; }
    public IReadOnlySet<string> ExplicitlyProtectedRelativePaths { get; }
    public string? ContinuationToken { get; }

    private static int? ValidateBatchSize(int? value)
    {
        if (value is not (null or >= 1 and <= 50))
            throw new ArgumentException("Batch size override must be between 1 and 50.", nameof(value));

        return value;
    }
}

internal static class MaintenanceScopes
{
    internal const string DefaultLocalScope = "local";
    internal static IReadOnlySet<string> EmptyProtectionSet { get; } =
        new HashSet<string>(StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);
}

internal sealed record MaintenanceCandidate
{
    internal MaintenanceCandidate(string scope, string subject, MaintenanceCandidateKind kind,
        string bodyPath, string? sidecarPath, DateTimeOffset? candidateTimestamp, string? operationId,
        MaintenanceProtectionReason? protection, string relativePath)
    {
        Scope = MaintenanceContractValidation.RequireSafeText(scope, nameof(scope));
        Subject = MaintenanceContractValidation.RequireSafeText(subject, nameof(subject));
        Kind = kind;
        BodyPath = MaintenanceContractValidation.RequireRelativePath(bodyPath, nameof(bodyPath));
        SidecarPath = sidecarPath is null ? null : MaintenanceContractValidation.RequireRelativePath(sidecarPath, nameof(sidecarPath));
        CandidateTimestamp = candidateTimestamp;
        OperationId = operationId is null ? null : MaintenanceContractValidation.RequireSafeText(operationId, nameof(operationId));
        Protection = protection;
        RelativePath = MaintenanceContractValidation.RequireRelativePath(relativePath, nameof(relativePath));
    }

    public string Scope { get; }
    public string Subject { get; }
    public MaintenanceCandidateKind Kind { get; }
    public string BodyPath { get; }
    public string? SidecarPath { get; }
    public DateTimeOffset? CandidateTimestamp { get; }
    public string? OperationId { get; }
    public MaintenanceProtectionReason? Protection { get; }
    public string RelativePath { get; }
}

internal sealed record MaintenanceCandidateResult
{
    internal MaintenanceCandidateResult(MaintenanceCandidate candidate, bool deleted,
        MaintenanceProtectionReason? reason, string? warning)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Deleted = deleted;
        Reason = reason;
        Warning = warning;
    }

    public MaintenanceCandidate Candidate { get; }
    public bool Deleted { get; }
    public MaintenanceProtectionReason? Reason { get; }
    public string? Warning { get; }
}

internal sealed record MaintenanceScopeResult
{
    internal MaintenanceScopeResult(string scope, int inspected, int deletedArchives, int deletedRecoveryEvidence,
        int retained, bool lockContended, bool moreWork, string? continuationToken,
        IReadOnlyList<MaintenanceCandidateResult> candidates, IReadOnlyList<string> warnings)
    {
        Scope = MaintenanceContractValidation.RequireSafeText(scope, nameof(scope));
        Inspected = RequireNonNegative(inspected, nameof(inspected));
        DeletedArchives = RequireNonNegative(deletedArchives, nameof(deletedArchives));
        DeletedRecoveryEvidence = RequireNonNegative(deletedRecoveryEvidence, nameof(deletedRecoveryEvidence));
        Retained = RequireNonNegative(retained, nameof(retained));
        LockContended = lockContended;
        MoreWork = moreWork;
        ContinuationToken = continuationToken is null ? null : MaintenanceContractValidation.RequireToken(continuationToken, nameof(continuationToken));
        Candidates = MaintenanceContractValidation.CopyCandidates(candidates);
        Warnings = MaintenanceContractValidation.CopyStrings(warnings, nameof(warnings));
    }

    public string Scope { get; }
    public int Inspected { get; }
    public int DeletedArchives { get; }
    public int DeletedRecoveryEvidence { get; }
    public int Retained { get; }
    public bool LockContended { get; }
    public bool MoreWork { get; }
    public string? ContinuationToken { get; }
    public IReadOnlyList<MaintenanceCandidateResult> Candidates { get; }
    public IReadOnlyList<string> Warnings { get; }

    private static int RequireNonNegative(int value, string name) => value < 0
        ? throw new ArgumentException("Counts cannot be negative.", name)
        : value;
}

internal sealed record MaintenanceResult
{
    internal MaintenanceResult(int inspected, int deletedArchives, int deletedRecoveryEvidence, bool completed,
        bool moreWork, bool recoveryRetained, bool lockContended, bool policyInvalid, bool partialFailure,
        bool hasWarnings, string? continuationToken, IReadOnlyList<MaintenanceScopeResult> scopes,
        IReadOnlyList<string> warnings, string? policyError)
    {
        Inspected = RequireNonNegative(inspected, nameof(inspected));
        DeletedArchives = RequireNonNegative(deletedArchives, nameof(deletedArchives));
        DeletedRecoveryEvidence = RequireNonNegative(deletedRecoveryEvidence, nameof(deletedRecoveryEvidence));
        Completed = completed;
        MoreWork = moreWork;
        RecoveryRetained = recoveryRetained;
        LockContended = lockContended;
        PolicyInvalid = policyInvalid;
        PartialFailure = partialFailure;
        ContinuationToken = continuationToken is null ? null : MaintenanceContractValidation.RequireToken(continuationToken, nameof(continuationToken));
        Scopes = MaintenanceContractValidation.CopyScopesResults(scopes);
        Warnings = MaintenanceContractValidation.CopyStrings(warnings, nameof(warnings));
        HasWarnings = Warnings.Count > 0;
        PolicyError = policyError;
    }

    public int Inspected { get; }
    public int DeletedArchives { get; }
    public int DeletedRecoveryEvidence { get; }
    public bool Completed { get; }
    public bool MoreWork { get; }
    public bool RecoveryRetained { get; }
    public bool LockContended { get; }
    public bool PolicyInvalid { get; }
    public bool PartialFailure { get; }
    public bool HasWarnings { get; }
    public string? ContinuationToken { get; }
    public IReadOnlyList<MaintenanceScopeResult> Scopes { get; }
    public IReadOnlyList<string> Warnings { get; }
    public string? PolicyError { get; }

    private static int RequireNonNegative(int value, string name) => value < 0
        ? throw new ArgumentException("Counts cannot be negative.", name)
        : value;
}

internal static class MaintenanceContractValidation
{
    internal static string RequireSafeText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("Value must be nonblank and contain no control characters.", parameterName);

        return value;
    }

    internal static string RequireRelativePath(string value, string parameterName)
    {
        RequireSafeText(value, parameterName);
        if (Path.IsPathRooted(value) || Uri.TryCreate(value, UriKind.Absolute, out _) ||
            value.Contains('\\') || value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Path must be a safe relative non-URI path.", parameterName);

        return value;
    }

    internal static IReadOnlyList<string> CopyScopes(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is < 1 or > 128)
            throw new ArgumentException("Scopes must contain between 1 and 128 values.", nameof(values));
        var copy = values.Select(value => RequireSafeText(value, nameof(values))).ToArray();
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Scopes must not contain duplicates.", nameof(values));
        return Array.AsReadOnly(copy);
    }

    internal static IReadOnlySet<string> CopyRelativePathSet(IReadOnlySet<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 4096)
            throw new ArgumentException("Protection paths cannot exceed 4096 values.", nameof(values));
        return values.Select(value => RequireRelativePath(value, nameof(values)))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    internal static string RequireToken(string value, string parameterName)
    {
        RequireSafeText(value, parameterName);
        if (value.Length > 32 * 1024 || value.Any(char.IsWhiteSpace))
            throw new ArgumentException("Continuation token must be nonblank, whitespace-free, and at most 32 KiB.", parameterName);
        return value;
    }

    internal static IReadOnlyList<MaintenanceCandidateResult> CopyCandidates(IReadOnlyList<MaintenanceCandidateResult> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyCollection<MaintenanceCandidateResult>(values.Select(value => value ?? throw new ArgumentException("Candidates cannot contain null elements.", nameof(values))).ToArray());
    }

    internal static IReadOnlyList<MaintenanceScopeResult> CopyScopesResults(IReadOnlyList<MaintenanceScopeResult> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyCollection<MaintenanceScopeResult>(values.Select(value => value ?? throw new ArgumentException("Scopes cannot contain null elements.", nameof(values))).ToArray());
    }

    internal static IReadOnlyList<string> CopyStrings(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return new ReadOnlyCollection<string>(values.Select(value => RequireSafeText(value, parameterName)).ToArray());
    }
}
