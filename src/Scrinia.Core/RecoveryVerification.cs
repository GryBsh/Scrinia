using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Scrinia.Core;

internal readonly record struct RecoveryScopeBinding(string Scope, string CanonicalScopeDirectory);

internal enum RecoveryEvidenceKind : byte
{
    AppendJournal = 1,
    RestoreJournal = 2,
    FileEntityJournal = 3,
    MultiMemoryTransactionRecord = 4
}

internal readonly record struct RecoveryExpectedHash(string? Value);

internal readonly record struct RecoveryVerifiedFileMetadata(
    long Length, string Sha256, bool RegularFile, bool Contained, bool StableAfterRead);

internal sealed class RecoveryVerificationContent : IDisposable
{
    private byte[]? _bytes;

    internal RecoveryVerificationContent(byte[] bytes, RecoveryVerifiedFileMetadata metadata)
    {
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        Metadata = metadata;
    }

    internal ReadOnlyMemory<byte> Bytes => _bytes ?? ReadOnlyMemory<byte>.Empty;
    internal RecoveryVerifiedFileMetadata Metadata { get; }

    public void Dispose()
    {
        byte[]? bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }
}

internal enum RecoveryVerificationOutcome : byte
{
    Verified = 1,
    ProtectedOversized = 2,
    RecoveryRequiredTimeout = 3,
    RecoveryRequiredCancellation = 4,
    ProtectedBlocked = 5,
    ProtectedMissing = 6,
    ProtectedHashMismatch = 7,
    ProtectedUnsafe = 8,
    ProtectedIoFailure = 9,
    ProtectedInvalidLimits = 10
}

internal readonly record struct RecoveryVerificationRequest(
    RecoveryScopeBinding Scope,
    RecoveryEvidenceKind Kind,
    string SourceRelativeName,
    string? DestinationRelativeName,
    RecoveryExpectedHash SourceHash,
    RecoveryExpectedHash? DestinationHash,
    MaintenanceLimits Limits);

internal readonly record struct RecoveryVerificationResult(
    RecoveryVerificationOutcome Outcome,
    long BytesRead,
    int OpenOperations,
    int StatOperations,
    int ReadOperations,
    bool FinalZeroByteRead,
    bool DeadlineObserved,
    bool CancellationObserved,
    RecoveryVerificationContent? SourceContent,
    RecoveryVerificationContent? DestinationContent);

internal static class RecoveryVerificationResultExtensions
{
    internal static RecoveryVerificationAccounting Accounting(this RecoveryVerificationResult result) =>
        new(result.Outcome, result.BytesRead, result.OpenOperations, result.StatOperations,
            result.ReadOperations, result.FinalZeroByteRead, result.DeadlineObserved,
            result.CancellationObserved);
}

internal interface IRecoveryEvidenceVerifier
{
    ValueTask<RecoveryVerificationResult> VerifyAsync(
        RecoveryVerificationRequest request, CancellationToken cancellationToken);
}

internal sealed class RecoveryEvidenceVerifier : IRecoveryEvidenceVerifier
{
    private readonly RecoveryScopeBinding _scope;

    internal RecoveryEvidenceVerifier(RecoveryScopeBinding scope) => _scope = scope;

    internal static async ValueTask<RecoveryVerificationResult> VerifySingleAsync(
        RecoveryScopeBinding scope, RecoveryEvidenceKind kind, string path,
        RecoveryExpectedHash expectedHash, MaintenanceLimits limits, CancellationToken cancellationToken)
    {
        string relativeName = Path.GetRelativePath(scope.CanonicalScopeDirectory, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        var verifier = new RecoveryEvidenceVerifier(scope);
        var request = new RecoveryVerificationRequest(
            scope, kind, relativeName, null, expectedHash, null, limits);
        return await verifier.VerifyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RecoveryVerificationResult> VerifyAsync(
        RecoveryVerificationRequest request, CancellationToken cancellationToken)
    {
        var accounting = new VerificationAccounting(request.Limits, cancellationToken);
        if (!MaintenanceLimits.IsValid(request.Limits))
            return accounting.Result(RecoveryVerificationOutcome.ProtectedInvalidLimits);
        if (request.Scope != _scope || !IsValidKind(request.Kind))
            return accounting.Result(RecoveryVerificationOutcome.ProtectedUnsafe);
        if (request.DestinationRelativeName is not null && request.DestinationHash is null)
            return accounting.Result(RecoveryVerificationOutcome.ProtectedUnsafe);

        string? sourcePath = ResolveContainedPath(request.Scope, request.SourceRelativeName);
        string? destinationPath = request.DestinationRelativeName is null
            ? null : ResolveContainedPath(request.Scope, request.DestinationRelativeName);
        if (sourcePath is null || (request.DestinationRelativeName is not null && destinationPath is null))
            return accounting.Result(RecoveryVerificationOutcome.ProtectedUnsafe);

        RecoveryVerificationContent? source = null;
        RecoveryVerificationContent? destination = null;
        bool ownershipTransferred = false;
        try
        {
            source = await ReadMemberAsync(sourcePath, request.SourceHash, accounting).ConfigureAwait(false);
            if (source is null) return accounting.Result(accounting.Failure);
            if (destinationPath is not null)
            {
                destination = await ReadMemberAsync(destinationPath, request.DestinationHash!.Value, accounting).ConfigureAwait(false);
                if (destination is null) return accounting.Result(accounting.Failure);
            }
            ownershipTransferred = true;
            return accounting.Result(RecoveryVerificationOutcome.Verified, source, destination);
        }
        catch (OperationCanceledException)
        {
            source?.Dispose(); destination?.Dispose();
            return accounting.Result(RecoveryVerificationOutcome.RecoveryRequiredCancellation);
        }
        catch (TimeoutException)
        {
            source?.Dispose(); destination?.Dispose();
            return accounting.Result(RecoveryVerificationOutcome.RecoveryRequiredTimeout);
        }
        catch (UnauthorizedAccessException)
        {
            source?.Dispose(); destination?.Dispose();
            return accounting.Result(RecoveryVerificationOutcome.ProtectedBlocked);
        }
        catch (FileNotFoundException)
        {
            source?.Dispose(); destination?.Dispose();
            return accounting.Result(RecoveryVerificationOutcome.ProtectedMissing);
        }
        catch (IOException)
        {
            return accounting.Result(accounting.Failure == RecoveryVerificationOutcome.ProtectedOversized
                ? accounting.Failure : RecoveryVerificationOutcome.ProtectedIoFailure);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                source?.Dispose();
                destination?.Dispose();
            }
        }
    }

    private async ValueTask<RecoveryVerificationContent?> ReadMemberAsync(
        string path, RecoveryExpectedHash expectedHash, VerificationAccounting accounting)
    {
        accounting.BeforeOperation();
        accounting.StatOperations++;
        FileInfo before = new(path);
        if (!before.Exists) { accounting.Failure = RecoveryVerificationOutcome.ProtectedMissing; return null; }
        if ((before.Attributes & FileAttributes.Directory) != 0)
        { accounting.Failure = RecoveryVerificationOutcome.ProtectedUnsafe; return null; }
        if (before.Length > accounting.Limits.MaximumBytes - accounting.BytesRead)
        { accounting.Failure = RecoveryVerificationOutcome.ProtectedOversized; return null; }
        if (expectedHash.Value is not null && !IsSha256(expectedHash.Value))
        { accounting.Failure = RecoveryVerificationOutcome.ProtectedUnsafe; return null; }

        accounting.BeforeOperation();
        accounting.OpenOperations++;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            accounting.Limits.ReadBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!RecoveryHandleIdentityVerifier.TryCapture(stream, path, _scope.CanonicalScopeDirectory,
                out RecoveryHandleIdentity identity) || !RecoveryHandleIdentityVerifier.IsSafe(identity, _scope.CanonicalScopeDirectory))
        {
            accounting.Failure = RecoveryVerificationOutcome.ProtectedUnsafe;
            return null;
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[accounting.Limits.ReadBufferBytes];
        using var content = new MemoryStream((int)Math.Min(before.Length, accounting.Limits.MaximumBytes));
        while (true)
        {
            accounting.BeforeOperation();
            int read = await stream.ReadAsync(buffer.AsMemory(), accounting.Token).ConfigureAwait(false);
            accounting.ReadOperations++;
            if (read == 0) { accounting.FinalZeroByteRead = true; break; }
            accounting.AddBytes(read);
            hash.AppendData(buffer, 0, read);
            content.Write(buffer, 0, read);
        }
        accounting.BeforeOperation();
        accounting.StatOperations++;
        FileInfo after = new(path);
        string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (after.Length != before.Length ||
            (expectedHash.Value is not null && !string.Equals(actualHash, expectedHash.Value, StringComparison.OrdinalIgnoreCase)))
        { accounting.Failure = after.Length != before.Length ? RecoveryVerificationOutcome.ProtectedUnsafe : RecoveryVerificationOutcome.ProtectedHashMismatch; return null; }
        return new RecoveryVerificationContent(content.ToArray(), new(after.Length, actualHash, true, true, true));
    }

    private string? ResolveContainedPath(RecoveryScopeBinding scope, string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName) || Path.IsPathRooted(relativeName) ||
            relativeName.Contains('\\') || relativeName.Split('/').Any(segment => segment is "" or "." or "..")) return null;
        string root = LexicalPath(scope.CanonicalScopeDirectory);
        string path = LexicalPath(Path.Combine(root, relativeName));
        if (!PathContainment.IsWithin(root, path) || HasReparsePoint(path, root))
            return null;
        return IsStrictlyWithin(root, path) ? path : null;
    }

    private static string LexicalPath(string path) => Path.GetFullPath(path);
    private static bool IsStrictlyWithin(string root, string path) =>
        !string.Equals(root, path, StringComparisonForPaths()) && PathContainment.IsWithin(root, path);
    private static StringComparison StringComparisonForPaths() => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private static bool IsValidKind(RecoveryEvidenceKind kind) => Enum.IsDefined(kind);
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool HasReparsePoint(string path, string root)
    {
        string current = root;
        string relative = Path.GetRelativePath(root, path);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment)) continue;
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        return false;
    }

    private sealed class VerificationAccounting
    {
        internal readonly MaintenanceLimits Limits;
        internal readonly CancellationToken Token;
        internal readonly long DeadlineTicks;
        internal long BytesRead;
        internal int OpenOperations;
        internal int StatOperations;
        internal int ReadOperations;
        internal bool FinalZeroByteRead;
        internal bool DeadlineObserved;
        internal bool CancellationObserved;
        internal RecoveryVerificationOutcome Failure = RecoveryVerificationOutcome.ProtectedIoFailure;

        internal VerificationAccounting(MaintenanceLimits limits, CancellationToken token)
        { Limits = limits; Token = token; DeadlineTicks = Stopwatch.GetTimestamp() + (long)(limits.MaximumDuration.TotalSeconds * Stopwatch.Frequency); }
        internal void BeforeOperation()
        {
            if (Token.IsCancellationRequested) { CancellationObserved = true; throw new OperationCanceledException(Token); }
            if (Stopwatch.GetTimestamp() >= DeadlineTicks) { DeadlineObserved = true; throw new TimeoutException(); }
            if (OpenOperations >= Limits.MaximumOpenOperations || StatOperations >= Limits.MaximumStatOperations || ReadOperations >= Limits.MaximumReadOperations)
            { Failure = RecoveryVerificationOutcome.ProtectedOversized; throw new IOException("Recovery verification operation budget was exceeded."); }
        }
        internal void AddBytes(int bytes) { if (bytes > Limits.MaximumBytes - BytesRead) { Failure = RecoveryVerificationOutcome.ProtectedOversized; throw new IOException("Recovery evidence exceeds its bounded byte budget."); } BytesRead += bytes; }
        internal RecoveryVerificationResult Result(RecoveryVerificationOutcome outcome, RecoveryVerificationContent? source = null, RecoveryVerificationContent? destination = null) =>
            new(outcome, BytesRead, OpenOperations, StatOperations, ReadOperations, FinalZeroByteRead, DeadlineObserved, CancellationObserved, source, destination);
    }
}

internal sealed record RecoveryVerifiedEnvelope(RecoveryEnvelopeDocument Document, RecoveryVerifiedFileMetadata Metadata)
{
    internal RecoveryBody TypedBody => Document.TypedBody ?? throw new InvalidOperationException("Recovery envelope body was not parsed.");
}

internal static partial class RecoveryEnvelopeCodec
{
    internal static RecoveryVerifiedEnvelope ParseVerifiedEnvelope(
        in RecoveryVerificationContent content,
        in RecoveryScopeBinding expectedScope,
        string evidencePath,
        string expectedEvidenceDirectory,
        RecoveryEvidenceKind evidenceKind,
        in RecoveryVerifiedFileMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(content);
        string actualContentHash = Convert.ToHexStringLower(SHA256.HashData(content.Bytes.Span));
        if (!metadata.RegularFile || !metadata.Contained || !metadata.StableAfterRead ||
            metadata.Length != content.Bytes.Length || !string.Equals(metadata.Sha256, actualContentHash, StringComparison.OrdinalIgnoreCase))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Unsafe, "Recovery evidence metadata is contradictory.", evidencePath);
        string scopeDirectory = Path.GetFullPath(expectedScope.CanonicalScopeDirectory);
        string evidenceDirectory = Path.GetFullPath(expectedEvidenceDirectory);
        string path = Path.GetFullPath(evidencePath);
        if (!PathContainment.IsWithin(scopeDirectory, evidenceDirectory) || !PathContainment.IsWithin(evidenceDirectory, path))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Unsafe, "Recovery evidence location is unsafe.", evidencePath);
        RecoveryOperation expectedOperation = evidenceKind switch
        {
            RecoveryEvidenceKind.AppendJournal => RecoveryOperation.Append,
            RecoveryEvidenceKind.RestoreJournal => RecoveryOperation.Restore,
            RecoveryEvidenceKind.FileEntityJournal => RecoveryOperation.FileEntity,
            RecoveryEvidenceKind.MultiMemoryTransactionRecord => throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Inconsistent, "Coordinator evidence is not an envelope.", evidencePath),
            _ => throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Unsafe, "Recovery evidence kind is invalid.", evidencePath)
        };
        RecoveryEnvelopeDocument document;
        try
        {
            document = RecoveryEnvelopeCodec.IsCommonEnvelope(content.Bytes.Span)
                ? ParseEnvelopeDocument(content.Bytes.Span, expectedScope.Scope, evidencePath, expectedEvidenceDirectory,
                    validateLocation: false, expectedOperation: expectedOperation)
                : ParseLegacyEnvelope(content, expectedScope.Scope, evidencePath,
                    expectedEvidenceDirectory, evidenceKind);
        }
        catch (RecoveryEnvelopeException exception) when (exception.OperationId is null)
        {
            string? operationId = ExtractEnvelopeOperationId(content.Bytes.Span);
            throw operationId is null ? exception : exception.WithOperationId(operationId);
        }
        if (document.Operation != expectedOperation)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Inconsistent, "Recovery evidence kind does not match its envelope.", evidencePath);
        RecoveryEnvelopeCodec.ValidateEvidenceLocationForAdapter(document.Operation, evidencePath, expectedEvidenceDirectory);
        return new RecoveryVerifiedEnvelope(document, metadata);
    }

    private static RecoveryEnvelopeDocument ParseLegacyEnvelope(RecoveryVerificationContent content, string scope,
        string evidencePath, string evidenceDirectory, RecoveryEvidenceKind kind)
    {
        try
        {
            return kind switch
            {
                RecoveryEvidenceKind.AppendJournal => ParseLegacyAppend(content.Bytes.Span, scope, evidencePath, evidenceDirectory),
                RecoveryEvidenceKind.RestoreJournal => ParseLegacyRestore(content.Bytes.Span, scope, evidencePath),
                RecoveryEvidenceKind.FileEntityJournal => ParseLegacyFileEntity(
                    content.Bytes.Span, scope, evidencePath, evidenceDirectory),
                _ => throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                    "Recovery evidence format is invalid.", evidencePath)
            };
        }
        catch (AppendJournalException exception)
        {
            throw new RecoveryEnvelopeException(
                exception.Failure == AppendJournalFailure.UnsupportedVersion
                    ? RecoveryEnvelopeFailure.UnsupportedVersion : RecoveryEnvelopeFailure.Malformed,
                exception.Message, evidencePath, exception, operationId: exception.OperationId);
        }
        catch (RestoreRecoveryException exception)
        {
            throw new RecoveryEnvelopeException(
                exception.Failure == RestoreRecoveryFailure.UnsupportedVersion
                    ? RecoveryEnvelopeFailure.UnsupportedVersion : RecoveryEnvelopeFailure.Malformed,
                exception.Message, evidencePath, exception, exception.UnsupportedVersion);
        }
    }

    private static RecoveryEnvelopeDocument ParseLegacyAppend(ReadOnlySpan<byte> bytes, string scope,
        string evidencePath, string evidenceDirectory)
    {
        if (bytes.IsEmpty || !RestoreJournalCodec.IsJournalDocumentLengthAdmitted(bytes.Length))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Append journal is empty or exceeds the maximum admitted size.", evidencePath);
        AppendJournal? journal;
        try { journal = JsonSerializer.Deserialize(bytes, AppendJournalJsonContext.Default.AppendJournal); }
        catch (JsonException exception)
        { throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "Append journal is malformed.", evidencePath, exception); }
        if (journal is null)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Append journal must be a JSON object.", evidencePath);
        AppendJournalCodec.ValidateLegacyParsed(journal, scope, evidencePath);
        return RecoveryEnvelopeCodec.FromAppend(journal) with { IsLegacy = true };
    }

    private static RecoveryEnvelopeDocument ParseLegacyRestore(ReadOnlySpan<byte> bytes, string scope,
        string evidencePath)
    {
        if (bytes.StartsWith(System.Text.Encoding.UTF8.Preamble))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Restore journal must not include a UTF-8 byte-order mark.", evidencePath);
        if (!HasJsonProperty(bytes, "state"))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Restore journal state is required.", evidencePath);
        RestoreJournal? journal;
        try { journal = JsonSerializer.Deserialize(bytes, RestoreJournalJsonContext.Default.RestoreJournal); }
        catch (JsonException exception)
        { throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "Restore journal is malformed.", evidencePath, exception); }
        if (journal is null)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Restore journal must be a JSON object.", evidencePath);
        RestoreJournalCodec.ValidateLegacyParsed(journal, scope, evidencePath);
        return RecoveryEnvelopeCodec.FromRestore(journal) with { IsLegacy = true };
    }

    private static RecoveryEnvelopeDocument ParseLegacyFileEntity(ReadOnlySpan<byte> bytes,
        string expectedScope, string evidencePath, string expectedEvidenceDirectory)
    {
        ValidateEvidenceLocationForAdapter(RecoveryOperation.FileEntity, evidencePath, expectedEvidenceDirectory);
        if (bytes.Length == 0)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "File-entity recovery evidence is empty.", evidencePath);
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = new BinaryReader(stream);
            if (!reader.ReadString().Equals("SCRINIA-FILE-ENTITY-RECOVERY-1", StringComparison.Ordinal))
                throw new InvalidDataException("Unknown file-entity recovery evidence format.");
            bool committed = reader.ReadByte() switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidDataException("Unknown file-entity recovery evidence state.")
            };
            RecoveryMemberSnapshot oldBody = ReadLegacyFileEntitySnapshot(reader);
            RecoveryMemberSnapshot oldMeta = ReadLegacyFileEntitySnapshot(reader);
            string? archiveBody = ReadLegacyOptionalPath(reader);
            string? archiveMeta = ReadLegacyOptionalPath(reader);
            RecoveryMemberSnapshot? committedBody = ReadLegacyOptionalSnapshot(reader);
            RecoveryMemberSnapshot? committedMeta = ReadLegacyOptionalSnapshot(reader);
            ValidateFileEntityMember(oldBody);
            ValidateFileEntityMember(oldMeta);
            ValidateOptionalFileEntityMember(committedBody);
            ValidateOptionalFileEntityMember(committedMeta);
            if (stream.Position != stream.Length)
                throw new InvalidDataException("File-entity recovery evidence has trailing data.");

            string entityName = Path.GetFileNameWithoutExtension(oldMeta.Path);
            if (entityName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                entityName = entityName[..^".meta".Length];
            bool metadataOnly = Path.GetFileNameWithoutExtension(oldBody.Path)
                .StartsWith(".file-entity-body-omitted", StringComparison.OrdinalIgnoreCase);
            if (metadataOnly)
            {
                oldBody = oldBody with { Path = oldMeta.Path };
                committedBody = null;
            }

            string entityKind = Path.GetFileName(Path.GetDirectoryName(evidencePath) ?? string.Empty);
            if (entityKind.Equals("skills", StringComparison.OrdinalIgnoreCase)) entityKind = "skill";
            else if (entityKind.Equals("agents", StringComparison.OrdinalIgnoreCase)) entityKind = "agent";
            if (metadataOnly) entityKind = "sidecar";

            var body = new FileEntityRecoveryBody(entityKind, entityName, oldBody, oldMeta,
                archiveBody, archiveMeta, committedBody, committedMeta,
                committed ? "committed" : "prepared");
            string operationId = ExtractLegacyFileEntityOperationId(evidencePath);
            return FromFileEntity(body, expectedScope, null, operationId,
                committed ? RecoveryLifecycleState.Committed : RecoveryLifecycleState.Prepared,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) with { IsLegacy = true };
        }
        catch (Exception exception) when (exception is not RecoveryEnvelopeException)
        {
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                $"File-entity recovery evidence is malformed: {evidencePath}", evidencePath, exception);
        }
    }

    private static RecoveryMemberSnapshot ReadLegacyFileEntitySnapshot(BinaryReader reader)
    {
        string path = reader.ReadString();
        bool existed = reader.ReadBoolean();
        int length = reader.ReadInt32();
        if (length < 0 || length > 128 * 1024 * 1024)
            throw new InvalidDataException("Invalid file-entity recovery member length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        string hash = reader.ReadString();
        return new RecoveryMemberSnapshot(path, existed, existed ? Convert.ToBase64String(bytes) : null, hash);
    }

    private static RecoveryMemberSnapshot? ReadLegacyOptionalSnapshot(BinaryReader reader) =>
        reader.ReadBoolean() ? ReadLegacyFileEntitySnapshot(reader) : null;

    private static string? ReadLegacyOptionalPath(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadString() : null;

    private static string ExtractLegacyFileEntityOperationId(string evidencePath)
    {
        string name = Path.GetFileName(evidencePath);
        const string prefix = ".file-entity-";
        return name[prefix.Length..^".recovery".Length];
    }

    private static bool HasJsonProperty(ReadOnlySpan<byte> bytes, string propertyName)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName &&
                    string.Equals(reader.GetString(), propertyName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
        catch (JsonException exception)
        {
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Recovery evidence is malformed JSON.", "<legacy>", exception);
        }
    }

    private static string? ExtractEnvelopeOperationId(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName ||
                    !string.Equals(reader.GetString(), "operationId", StringComparison.Ordinal) ||
                    !reader.Read() || reader.TokenType != JsonTokenType.String)
                    continue;
                return reader.GetString();
            }
        }
        catch (JsonException)
        {
            // The enclosing verified parser reports the original malformed evidence.
        }

        return null;
    }

    private static bool IsStrictlyWithin(string root, string path)
    {
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(root, path, comparison)) return false;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }
}
