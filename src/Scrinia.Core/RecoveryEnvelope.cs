using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Models;

namespace Scrinia.Core;

internal enum RecoveryEnvelopeFailure
{
    Malformed,
    UnsupportedVersion
}

internal sealed class RecoveryEnvelopeException : IOException
{
    internal RecoveryEnvelopeException(RecoveryEnvelopeFailure failure, string message, string evidencePath,
        Exception? inner = null) : base(message, inner)
    {
        Failure = failure;
        EvidencePath = evidencePath;
    }

    internal RecoveryEnvelopeFailure Failure { get; }
    internal string EvidencePath { get; }
}

internal sealed record AppendRecoveryBody(
    [property: JsonPropertyName("state")] AppendJournalState State,
    [property: JsonPropertyName("isCreate")] bool IsCreate,
    [property: JsonPropertyName("oldArtifact")] AppendJournalMember OldArtifact,
    [property: JsonPropertyName("oldSidecar")] AppendJournalMember OldSidecar,
    [property: JsonPropertyName("newArtifact")] AppendJournalMember NewArtifact,
    [property: JsonPropertyName("newSidecar")] AppendJournalMember NewSidecar,
    [property: JsonPropertyName("archiveArtifactPath")] string? ArchiveArtifactPath,
    [property: JsonPropertyName("archiveSidecarPath")] string? ArchiveSidecarPath,
    [property: JsonPropertyName("archiveArtifactStatus")] ArchiveMemberStatus ArchiveArtifactStatus,
    [property: JsonPropertyName("archiveSidecarStatus")] ArchiveMemberStatus ArchiveSidecarStatus,
    [property: JsonPropertyName("pruneCandidates")] IReadOnlyList<string> PruneCandidates,
    [property: JsonPropertyName("committedHash")] string? CommittedHash,
    [property: JsonPropertyName("detail")] string? Detail);

internal sealed record RestoreRecoveryBody(
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("state")] RestoreJournalState State,
    [property: JsonPropertyName("entries")] IReadOnlyList<RestoreJournalEntry> Entries);

internal sealed record RecoveryMemberSnapshot(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("existed")] bool Existed,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record FileEntityRecoveryBody(
    [property: JsonPropertyName("entityKind")] string EntityKind,
    [property: JsonPropertyName("entityName")] string EntityName,
    [property: JsonPropertyName("oldBody")] RecoveryMemberSnapshot OldBody,
    [property: JsonPropertyName("oldMeta")] RecoveryMemberSnapshot OldMeta,
    [property: JsonPropertyName("archiveBodyPath")] string? ArchiveBodyPath,
    [property: JsonPropertyName("archiveMetaPath")] string? ArchiveMetaPath,
    [property: JsonPropertyName("committedBody")] RecoveryMemberSnapshot? CommittedBody,
    [property: JsonPropertyName("committedMeta")] RecoveryMemberSnapshot? CommittedMeta,
    [property: JsonPropertyName("state")] string State);

internal sealed class RecoveryOperationJsonConverter : JsonConverter<RecoveryOperation>
{
    public override RecoveryOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "restore" => RecoveryOperation.Restore,
            "append" => RecoveryOperation.Append,
            "fileEntity" => RecoveryOperation.FileEntity,
            _ => throw new JsonException("Recovery operation is invalid.")
        } : throw new JsonException("Recovery operation must be a string.");

    public override void Write(Utf8JsonWriter writer, RecoveryOperation value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            RecoveryOperation.Restore => "restore",
            RecoveryOperation.Append => "append",
            RecoveryOperation.FileEntity => "fileEntity",
            _ => throw new JsonException("Recovery operation is invalid.")
        });
}

internal sealed class RecoveryLifecycleStateJsonConverter : JsonConverter<RecoveryLifecycleState>
{
    private static readonly Dictionary<string, RecoveryLifecycleState> Values =
        new Dictionary<string, RecoveryLifecycleState>(StringComparer.Ordinal)
        {
            ["prepared"] = RecoveryLifecycleState.Prepared,
            ["applying"] = RecoveryLifecycleState.Applying,
            ["committed"] = RecoveryLifecycleState.Committed,
            ["cleanupPending"] = RecoveryLifecycleState.CleanupPending,
            ["cleaned"] = RecoveryLifecycleState.Cleaned,
            ["recoveryRequired"] = RecoveryLifecycleState.RecoveryRequired,
            ["rolledBack"] = RecoveryLifecycleState.RolledBack
        };

    public override RecoveryLifecycleState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out RecoveryLifecycleState value)
            ? value
            : throw new JsonException("Recovery lifecycle state is invalid.");

    public override void Write(Utf8JsonWriter writer, RecoveryLifecycleState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Values.FirstOrDefault(pair => pair.Value == value).Key
            ?? throw new JsonException("Recovery lifecycle state is invalid."));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RecoveryEnvelopeDocument))]
[JsonSerializable(typeof(RecoveryOperation))]
[JsonSerializable(typeof(RecoveryLifecycleState))]
[JsonSerializable(typeof(AppendRecoveryBody))]
[JsonSerializable(typeof(RestoreRecoveryBody))]
[JsonSerializable(typeof(FileEntityRecoveryBody))]
[JsonSerializable(typeof(RecoveryMemberSnapshot))]
[JsonSerializable(typeof(AppendJournalMember))]
[JsonSerializable(typeof(AppendJournalState))]
[JsonSerializable(typeof(RestoreJournalState))]
[JsonSerializable(typeof(RestoreJournalEntry))]
[JsonSerializable(typeof(RecoveryBlock))]
[JsonSerializable(typeof(RecoveryOutcome))]
internal partial class RecoveryEnvelopeJsonContext : JsonSerializerContext;

internal static class RecoveryEnvelopeCodec
{
    internal const string Format = "scrinia.recovery";
    internal const int CurrentVersion = 1;
    internal const int CurrentBodyVersion = 1;

    internal static byte[] Serialize(RecoveryEnvelopeDocument document)
    {
        Validate(document, document.Scope, "<new recovery envelope>");
        return JsonSerializer.SerializeToUtf8Bytes(document, RecoveryEnvelopeJsonContext.Default.RecoveryEnvelopeDocument);
    }

    internal static RecoveryEnvelopeDocument Parse(ReadOnlySpan<byte> json, string expectedScope, string evidencePath)
    {
        if (json.IsEmpty)
            throw Malformed("Recovery envelope is empty", evidencePath);
        try
        {
            if (HasDuplicateIdentityMember(json))
                throw Malformed("Recovery envelope contains duplicate identity members", evidencePath);
        }
        catch (JsonException exception)
        {
            throw Malformed("Recovery envelope is malformed", evidencePath, exception);
        }

        RecoveryEnvelopeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, RecoveryEnvelopeJsonContext.Default.RecoveryEnvelopeDocument);
        }
        catch (JsonException exception)
        {
            throw Malformed("Recovery envelope is malformed", evidencePath, exception);
        }

        if (document is null)
            throw Malformed("Recovery envelope must be a JSON object", evidencePath);
        Validate(document, expectedScope, evidencePath);
        ValidateBody(document, evidencePath);
        return document;
    }

    internal static RecoveryEnvelopeDocument ValidateForOperation(RecoveryEnvelopeDocument document,
        RecoveryOperation expectedOperation, string expectedScope, string evidencePath)
    {
        if (document.Operation != expectedOperation)
            throw Malformed("Evidence operation does not match its adapter", evidencePath);
        Validate(document, expectedScope, evidencePath);
        ValidateBody(document, evidencePath);
        return document;
    }

    private static void ValidateBody(RecoveryEnvelopeDocument document, string evidencePath)
    {
        try
        {
            switch (document.Operation)
            {
                case RecoveryOperation.Append:
                    AppendRecoveryBody append = JsonSerializer.Deserialize(document.Body.GetRawText(), RecoveryEnvelopeJsonContext.Default.AppendRecoveryBody)
                        ?? throw new JsonException("Append recovery body is missing.");
                    if (append.OldArtifact is null || append.OldSidecar is null || append.NewArtifact is null ||
                        append.NewSidecar is null || append.PruneCandidates is null || !Enum.IsDefined(append.State) ||
                        !Enum.IsDefined(append.ArchiveArtifactStatus) || !Enum.IsDefined(append.ArchiveSidecarStatus))
                        throw new JsonException("Append recovery body is incomplete.");
                    break;
                case RecoveryOperation.Restore:
                    RestoreRecoveryBody restore = JsonSerializer.Deserialize(document.Body.GetRawText(), RecoveryEnvelopeJsonContext.Default.RestoreRecoveryBody)
                        ?? throw new JsonException("Restore recovery body is missing.");
                    if (string.IsNullOrWhiteSpace(restore.TransactionId) || restore.Entries is not { Count: > 0 } || restore.Entries.Any(entry => entry is null))
                        throw new JsonException("Restore recovery body is incomplete.");
                    break;
                case RecoveryOperation.FileEntity:
                    FileEntityRecoveryBody fileEntity = JsonSerializer.Deserialize(document.Body.GetRawText(), RecoveryEnvelopeJsonContext.Default.FileEntityRecoveryBody)
                        ?? throw new JsonException("File-entity recovery body is missing.");
                    if (string.IsNullOrWhiteSpace(fileEntity.EntityKind) || string.IsNullOrWhiteSpace(fileEntity.EntityName) ||
                        fileEntity.OldBody is null || fileEntity.OldMeta is null ||
                        (fileEntity.State is not "prepared" and not "committed"))
                        throw new JsonException("File-entity recovery body is incomplete.");
                    break;
                default:
                    throw new JsonException("Recovery envelope operation is invalid.");
            }
        }
        catch (JsonException exception)
        {
            throw Malformed("Recovery envelope body is invalid", evidencePath, exception);
        }
    }

    internal static RecoveryEnvelopeDocument FromAppend(AppendJournal journal) =>
        Create(RecoveryOperation.Append, journal.OperationId, journal.Scope, journal.Address,
            Map(journal.State), journal.StartedAt, journal.StartedAt,
            JsonSerializer.SerializeToElement(new AppendRecoveryBody(journal.State, journal.IsCreate,
                journal.OldArtifact, journal.OldSidecar, journal.NewArtifact, journal.NewSidecar,
                journal.ArchiveArtifactPath, journal.ArchiveSidecarPath, journal.ArchiveArtifactStatus,
                journal.ArchiveSidecarStatus, journal.PruneCandidates, journal.CommittedHash, journal.Detail),
                RecoveryEnvelopeJsonContext.Default.AppendRecoveryBody));

    internal static RecoveryEnvelopeDocument FromRestore(RestoreJournal journal) =>
        Create(RecoveryOperation.Restore, journal.TransactionId, journal.Scope, null,
            journal.State == RestoreJournalState.Completed ? RecoveryLifecycleState.Committed : RecoveryLifecycleState.Prepared,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new RestoreRecoveryBody(journal.TransactionId, journal.State, journal.Entries),
                RecoveryEnvelopeJsonContext.Default.RestoreRecoveryBody));

    internal static RecoveryEnvelopeDocument FromFileEntity(FileEntityRecoveryBody body, string scope, string? address,
        string operationId, RecoveryLifecycleState state, DateTimeOffset createdAt, DateTimeOffset updatedAt) =>
        Create(RecoveryOperation.FileEntity, operationId, scope, address, state, createdAt, updatedAt,
            JsonSerializer.SerializeToElement(body, RecoveryEnvelopeJsonContext.Default.FileEntityRecoveryBody));

    private static RecoveryEnvelopeDocument Create(RecoveryOperation operation, string operationId, string scope,
        string? address, RecoveryLifecycleState state, DateTimeOffset createdAt, DateTimeOffset updatedAt, JsonElement body) =>
        new(Format, CurrentVersion, operation, CurrentBodyVersion, operationId, scope, address, state, createdAt, updatedAt, body);

    private static void Validate(RecoveryEnvelopeDocument document, string expectedScope, string evidencePath)
    {
        if (!string.Equals(document.Format, Format, StringComparison.Ordinal)) throw Malformed("Recovery envelope format is invalid", evidencePath);
        if (document.Version > CurrentVersion) throw Unsupported("Recovery envelope version is newer than this build", evidencePath);
        if (document.Version != CurrentVersion || document.BodyVersion <= 0) throw Malformed("Recovery envelope version is invalid", evidencePath);
        if (document.BodyVersion > CurrentBodyVersion) throw Unsupported("Recovery envelope body version is newer than this build", evidencePath);
        if (string.IsNullOrWhiteSpace(document.OperationId) || string.IsNullOrWhiteSpace(document.Scope) ||
            !string.Equals(document.Scope, expectedScope, StringComparison.Ordinal)) throw Malformed("Recovery envelope identity is invalid", evidencePath);
        if (document.Operation == RecoveryOperation.Append)
        {
            if (string.IsNullOrWhiteSpace(document.Address)) throw Malformed("Append recovery envelope address is required", evidencePath);
            try { _ = MemoryAddress.Parse(document.Address); } catch (ArgumentException exception) { throw Malformed("Recovery envelope address is invalid", evidencePath, exception); }
        }
        else if (document.Address is not null) throw Malformed("Recovery envelope address is not valid for this operation", evidencePath);
        if (document.UpdatedAt < document.CreatedAt) throw Malformed("Recovery envelope timestamps are invalid", evidencePath);
        if (document.Body.ValueKind != JsonValueKind.Object) throw Malformed("Recovery envelope body is missing", evidencePath);
    }

    private static RecoveryLifecycleState Map(AppendJournalState state) => state switch
    {
        AppendJournalState.Prepared or AppendJournalState.ArchiveInProgress or AppendJournalState.Archived or AppendJournalState.NewPairInProgress => RecoveryLifecycleState.Applying,
        AppendJournalState.Committed => RecoveryLifecycleState.Committed,
        AppendJournalState.CleanupPending => RecoveryLifecycleState.CleanupPending,
        AppendJournalState.RecoveryRequired => RecoveryLifecycleState.RecoveryRequired,
        AppendJournalState.RolledBack => RecoveryLifecycleState.RolledBack,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static bool HasDuplicateIdentityMember(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && !names.Add(reader.GetString()!))
                return true;
        return false;
    }

    private static RecoveryEnvelopeException Malformed(string message, string path, Exception? inner = null) =>
        new(RecoveryEnvelopeFailure.Malformed, $"{message}: {path}", path, inner);

    private static RecoveryEnvelopeException Unsupported(string message, string path) =>
        new(RecoveryEnvelopeFailure.UnsupportedVersion, $"{message}: {path}", path);
}

internal sealed class AppendRecoveryAdapter : IRecoveryAdapter
{
    internal RecoveryOperation Operation => RecoveryOperation.Append;
    RecoveryOperation IRecoveryAdapter.Operation => Operation;
    internal bool IsCandidateFile(string fileName) => fileName is AppendJournal.FileName or ".append-journal.json";
    bool IRecoveryAdapter.IsCandidateFile(string fileName) => IsCandidateFile(fileName);
    internal RecoveryEnvelopeDocument NormalizeLegacy(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath)
    {
        EnsureFilename(evidencePath);
        try
        {
            return RecoveryEnvelopeCodec.FromAppend(AppendJournalCodec.ParseAndValidate(bytes, expectedScope, evidencePath)) with { IsLegacy = true };
        }
        catch (AppendJournalException exception)
        {
            throw new RecoveryEnvelopeException(exception.Failure == AppendJournalFailure.UnsupportedVersion
                ? RecoveryEnvelopeFailure.UnsupportedVersion : RecoveryEnvelopeFailure.Malformed,
                exception.Message, evidencePath, exception);
        }
    }
    RecoveryEnvelopeDocument IRecoveryAdapter.ParseAndNormalize(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath) =>
        IsJson(bytes) ? RecoveryEnvelopeCodec.ValidateForOperation(RecoveryEnvelopeCodec.Parse(bytes, expectedScope, evidencePath), RecoveryOperation.Append, expectedScope, evidencePath) : NormalizeLegacy(bytes, expectedScope, evidencePath);
    RecoveryEnvelopeDocument IRecoveryAdapter.ValidateForMutation(RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath, string expectedSubject) =>
        RecoveryEnvelopeCodec.ValidateForOperation(envelope, RecoveryOperation.Append, expectedScope, evidencePath);
    RecoveryOutcome IRecoveryAdapter.RecoverOrCleanup(RecoveryEnvelopeDocument envelope, string evidencePath, CancellationToken cancellationToken) => throw new NotSupportedException("Append replay is owned by the append operation.");

    private static void EnsureFilename(string evidencePath) { if (Path.GetFileName(evidencePath) != AppendJournal.FileName) throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "Evidence filename does not match append operation.", evidencePath); }
    private static bool IsJson(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return value == (byte)'{';
        }
        return false;
    }
}

internal sealed class RestoreRecoveryAdapter : IRecoveryAdapter
{
    internal RecoveryOperation Operation => RecoveryOperation.Restore;
    RecoveryOperation IRecoveryAdapter.Operation => Operation;
    internal bool IsCandidateFile(string fileName) => fileName == ".restore-journal.json";
    bool IRecoveryAdapter.IsCandidateFile(string fileName) => IsCandidateFile(fileName);
    internal RecoveryEnvelopeDocument NormalizeLegacy(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath)
    {
        EnsureFilename(evidencePath);
        try
        {
            return RecoveryEnvelopeCodec.FromRestore(RestoreJournalCodec.ParseAndValidate(bytes, expectedScope, evidencePath)) with { IsLegacy = true };
        }
        catch (RestoreRecoveryException exception)
        {
            throw new RecoveryEnvelopeException(exception.Failure == RestoreRecoveryFailure.UnsupportedVersion
                ? RecoveryEnvelopeFailure.UnsupportedVersion : RecoveryEnvelopeFailure.Malformed,
                exception.Message, evidencePath, exception);
        }
    }
    RecoveryEnvelopeDocument IRecoveryAdapter.ParseAndNormalize(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath) =>
        IsJson(bytes) ? RecoveryEnvelopeCodec.ValidateForOperation(RecoveryEnvelopeCodec.Parse(bytes, expectedScope, evidencePath), RecoveryOperation.Restore, expectedScope, evidencePath) : NormalizeLegacy(bytes, expectedScope, evidencePath);
    RecoveryEnvelopeDocument IRecoveryAdapter.ValidateForMutation(RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath, string expectedSubject) =>
        RecoveryEnvelopeCodec.ValidateForOperation(envelope, RecoveryOperation.Restore, expectedScope, evidencePath);
    RecoveryOutcome IRecoveryAdapter.RecoverOrCleanup(RecoveryEnvelopeDocument envelope, string evidencePath, CancellationToken cancellationToken) => throw new NotSupportedException("Restore replay is owned by the restore operation.");

    private static void EnsureFilename(string evidencePath) { if (Path.GetFileName(evidencePath) != ".restore-journal.json") throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "Evidence filename does not match restore operation.", evidencePath); }
    private static bool IsJson(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return value == (byte)'{';
        }
        return false;
    }
}

internal sealed class FileEntityRecoveryAdapter : IRecoveryAdapter
{
    internal RecoveryOperation Operation => RecoveryOperation.FileEntity;
    RecoveryOperation IRecoveryAdapter.Operation => Operation;
    internal bool IsCandidateFile(string fileName) => fileName.StartsWith(".file-entity-", StringComparison.Ordinal) && fileName.EndsWith(".recovery", StringComparison.Ordinal);
    bool IRecoveryAdapter.IsCandidateFile(string fileName) => IsCandidateFile(fileName);

    internal RecoveryEnvelopeDocument NormalizeLegacy(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath)
    {
        EnsureFilename(evidencePath);
        if (bytes.Length == 0)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "File-entity recovery evidence is empty.", evidencePath);
        if (IsJson(bytes))
        {
            RecoveryEnvelopeDocument envelope = RecoveryEnvelopeCodec.Parse(bytes, expectedScope, evidencePath);
            return envelope.Operation == RecoveryOperation.FileEntity
                ? envelope
                : throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "Evidence operation does not match file-entity adapter.", evidencePath);
        }

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
            RecoveryMemberSnapshot oldBody = ReadSnapshot(reader);
            RecoveryMemberSnapshot oldMeta = ReadSnapshot(reader);
            string? archiveBody = ReadOptionalPath(reader);
            string? archiveMeta = ReadOptionalPath(reader);
            RecoveryMemberSnapshot? committedBody = ReadOptionalSnapshot(reader);
            RecoveryMemberSnapshot? committedMeta = ReadOptionalSnapshot(reader);
            if (stream.Position != stream.Length)
                throw new InvalidDataException("File-entity recovery evidence has trailing data.");
            string entityName = Path.GetFileNameWithoutExtension(oldBody.Path);
            string entityKind = Path.GetFileName(Path.GetDirectoryName(evidencePath) ?? string.Empty);
            var body = new FileEntityRecoveryBody(entityKind, entityName, oldBody, oldMeta, archiveBody, archiveMeta,
                committedBody, committedMeta, committed ? "committed" : "prepared");
            string operationId = ExtractOperationId(evidencePath);
            return RecoveryEnvelopeCodec.FromFileEntity(body, expectedScope, null, operationId,
                committed ? RecoveryLifecycleState.Committed : RecoveryLifecycleState.Prepared,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) with { IsLegacy = true };
        }
        catch (Exception exception) when (exception is not RecoveryEnvelopeException)
        {
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                $"File-entity recovery evidence is malformed: {evidencePath}", evidencePath, exception);
        }
    }

    RecoveryEnvelopeDocument IRecoveryAdapter.ParseAndNormalize(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath) => NormalizeLegacy(bytes, expectedScope, evidencePath);
    RecoveryEnvelopeDocument IRecoveryAdapter.ValidateForMutation(RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath, string expectedSubject) =>
        envelope.Operation == RecoveryOperation.FileEntity
            ? RecoveryEnvelopeCodec.ValidateForOperation(envelope, RecoveryOperation.FileEntity, expectedScope, evidencePath)
            : throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "Evidence operation does not match file-entity adapter.", evidencePath);
    RecoveryOutcome IRecoveryAdapter.RecoverOrCleanup(RecoveryEnvelopeDocument envelope, string evidencePath, CancellationToken cancellationToken) => throw new NotSupportedException("File-entity replay is owned by the file-entity operation.");

    private static RecoveryMemberSnapshot ReadSnapshot(BinaryReader reader)
    {
        string path = reader.ReadString();
        bool existed = reader.ReadBoolean();
        int length = reader.ReadInt32();
        if (length < 0 || length > 128 * 1024 * 1024) throw new InvalidDataException("Invalid file-entity recovery member length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return new RecoveryMemberSnapshot(path, existed, Convert.ToBase64String(bytes), reader.ReadString());
    }

    private static RecoveryMemberSnapshot? ReadOptionalSnapshot(BinaryReader reader) => reader.ReadBoolean() ? ReadSnapshot(reader) : null;
    private static string? ReadOptionalPath(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
    private static string ExtractOperationId(string evidencePath)
    {
        string name = Path.GetFileName(evidencePath);
        const string prefix = ".file-entity-";
        return name[prefix.Length..^".recovery".Length];
    }
    private static void EnsureFilename(string evidencePath)
    {
        string fileName = Path.GetFileName(evidencePath);
        if (!fileName.StartsWith(".file-entity-", StringComparison.Ordinal) || !fileName.EndsWith(".recovery", StringComparison.Ordinal))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "Evidence filename does not match file-entity operation.", evidencePath);
    }

    private static bool IsJson(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return value == (byte)'{';
        }
        return false;
    }
}
