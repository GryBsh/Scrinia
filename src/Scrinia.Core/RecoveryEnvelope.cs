using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Models;

namespace Scrinia.Core;

internal enum RecoveryEnvelopeFailure
{
    Malformed,
    UnsupportedVersion,
    WrongScope
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
    [property: JsonPropertyName("detail")] string? Detail) : RecoveryBody;

internal sealed record RestoreRecoveryBody(
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("state")] RestoreJournalState State,
    [property: JsonPropertyName("entries")] IReadOnlyList<RestoreJournalEntry> Entries) : RecoveryBody;

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
    [property: JsonPropertyName("state")] string State) : RecoveryBody;

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

    internal static RecoveryEnvelopeDocument Parse(ReadOnlySpan<byte> json, string expectedScope, string evidencePath,
        string expectedEvidenceDirectory)
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
        ValidateEvidenceLocation(document, evidencePath, expectedEvidenceDirectory);
        RecoveryBody typedBody = ParseTypedBody(document, evidencePath);
        return document with { TypedBody = typedBody };
    }

    internal static RecoveryEnvelopeDocument ValidateForOperation(RecoveryEnvelopeDocument document,
        RecoveryOperation expectedOperation, string expectedScope, string evidencePath, string expectedEvidenceDirectory)
    {
        if (document.Operation != expectedOperation)
            throw Malformed("Evidence operation does not match its adapter", evidencePath);
        Validate(document, expectedScope, evidencePath);
        ValidateEvidenceLocation(document, evidencePath, expectedEvidenceDirectory);
        RecoveryBody typedBody = document.TypedBody ?? ParseTypedBody(document, evidencePath);
        return document with { TypedBody = typedBody };
    }

    private static RecoveryBody ParseTypedBody(RecoveryEnvelopeDocument document, string evidencePath)
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
                    ValidateAppendMember(append);
                    return append;
                case RecoveryOperation.Restore:
                    RestoreRecoveryBody restore = JsonSerializer.Deserialize(document.Body.GetRawText(), RecoveryEnvelopeJsonContext.Default.RestoreRecoveryBody)
                        ?? throw new JsonException("Restore recovery body is missing.");
                    if (string.IsNullOrWhiteSpace(restore.TransactionId) || restore.Entries is not { Count: > 0 } || restore.Entries.Any(entry => entry is null))
                        throw new JsonException("Restore recovery body is incomplete.");
                    foreach (RestoreJournalEntry entry in restore.Entries)
                        if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.BodyFile) ||
                            string.IsNullOrWhiteSpace(entry.BodyBase64) || string.IsNullOrWhiteSpace(entry.BodySha256) ||
                            string.IsNullOrWhiteSpace(entry.SidecarFile) || string.IsNullOrWhiteSpace(entry.SidecarBase64) ||
                            string.IsNullOrWhiteSpace(entry.SidecarSha256))
                            throw new JsonException("Restore recovery entry is incomplete.");
                    return restore;
                case RecoveryOperation.FileEntity:
                    FileEntityRecoveryBody fileEntity = JsonSerializer.Deserialize(document.Body.GetRawText(), RecoveryEnvelopeJsonContext.Default.FileEntityRecoveryBody)
                        ?? throw new JsonException("File-entity recovery body is missing.");
                    if (string.IsNullOrWhiteSpace(fileEntity.EntityKind) || string.IsNullOrWhiteSpace(fileEntity.EntityName) ||
                        fileEntity.OldBody is null || fileEntity.OldMeta is null ||
                        (fileEntity.State is not "prepared" and not "committed"))
                        throw new JsonException("File-entity recovery body is incomplete.");
                    ValidateFileEntityMember(fileEntity.OldBody);
                    ValidateFileEntityMember(fileEntity.OldMeta);
                    ValidateOptionalFileEntityMember(fileEntity.CommittedBody);
                    ValidateOptionalFileEntityMember(fileEntity.CommittedMeta);
                    return fileEntity;
                default:
                    throw new JsonException("Recovery envelope operation is invalid.");
            }
        }
        catch (JsonException exception)
        {
            throw Malformed("Recovery envelope body is invalid", evidencePath, exception);
        }
    }

    private static void ValidateAppendMember(AppendRecoveryBody body)
    {
        AppendJournalMember[] members = [body.OldArtifact, body.OldSidecar, body.NewArtifact, body.NewSidecar];
        if (members.Any(member => string.IsNullOrWhiteSpace(member.Path) || string.IsNullOrWhiteSpace(member.Payload) ||
            string.IsNullOrWhiteSpace(member.Sha256)))
            throw new JsonException("Append recovery member is incomplete.");
    }

    private static void ValidateFileEntityMember(RecoveryMemberSnapshot member)
    {
        if (string.IsNullOrWhiteSpace(member.Path) || string.IsNullOrWhiteSpace(member.Payload) || string.IsNullOrWhiteSpace(member.Sha256))
            throw new JsonException("File-entity recovery member is incomplete.");
    }

    private static void ValidateOptionalFileEntityMember(RecoveryMemberSnapshot? member)
    {
        if (member is not null) ValidateFileEntityMember(member);
    }

    private static void ValidateEvidenceLocation(RecoveryEnvelopeDocument document, string evidencePath,
        string expectedEvidenceDirectory)
    {
        ValidateEvidenceDirectory(document.Operation, evidencePath, expectedEvidenceDirectory);
        ValidateEvidenceFilename(document.Operation, document.OperationId, evidencePath);
    }

    internal static void ValidateEvidenceLocationForAdapter(RecoveryOperation operation, string evidencePath,
        string expectedEvidenceDirectory)
    {
        ValidateEvidenceDirectory(operation, evidencePath, expectedEvidenceDirectory);
        ValidateEvidenceFilename(operation, null, evidencePath);
    }

    private static void ValidateEvidenceDirectory(RecoveryOperation operation, string evidencePath,
        string expectedEvidenceDirectory)
    {
        if (string.IsNullOrWhiteSpace(expectedEvidenceDirectory))
            throw Malformed("Expected recovery evidence directory is required", evidencePath);

        string expectedDirectory;
        string actualEvidencePath;
        try
        {
            string operationDirectory = operation == RecoveryOperation.Append
                ? Path.Combine(expectedEvidenceDirectory, "versions")
                : expectedEvidenceDirectory;
            expectedDirectory = PathContainment.ResolveCanonical(operationDirectory);
            actualEvidencePath = PathContainment.ResolveCanonical(evidencePath);
        }
        catch (ArgumentException exception)
        {
            throw Malformed("Recovery evidence location cannot be resolved safely", evidencePath, exception);
        }

        string? actualDirectory = Path.GetDirectoryName(actualEvidencePath);
        if (actualDirectory is null || !PathContainment.IsWithin(expectedDirectory, actualEvidencePath) ||
            !string.Equals(actualDirectory, expectedDirectory,
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            throw Malformed("Recovery evidence is outside its expected directory", evidencePath);
    }

    private static void ValidateEvidenceFilename(RecoveryOperation operation, string? operationId, string evidencePath)
    {
        string fileName = Path.GetFileName(evidencePath);
        bool valid = operation switch
        {
            RecoveryOperation.Append => fileName == AppendJournal.FileName,
            RecoveryOperation.Restore => fileName == ".restore-journal.json",
            RecoveryOperation.FileEntity => fileName.StartsWith(".file-entity-", StringComparison.Ordinal) &&
                fileName.EndsWith(".recovery", StringComparison.Ordinal) &&
                fileName.Length > ".file-entity-.recovery".Length &&
                (operationId is null || string.Equals(fileName[".file-entity-".Length..^".recovery".Length], operationId, StringComparison.Ordinal)),
            _ => false
        };
        if (!valid) throw Malformed("Recovery evidence filename does not match its operation", evidencePath);
    }

    internal static RecoveryEnvelopeDocument ValidateSubject(RecoveryEnvelopeDocument document, string expectedSubject,
        string evidencePath, string expectedEvidenceDirectory)
    {
        if (string.IsNullOrWhiteSpace(expectedSubject)) throw Malformed("Recovery subject is required", evidencePath);
        ValidateEvidenceLocation(document, evidencePath, expectedEvidenceDirectory);
        RecoveryBody body = document.TypedBody ?? ParseTypedBody(document, evidencePath);
        bool matches = document.Operation switch
        {
            RecoveryOperation.Append => string.Equals(document.Address, expectedSubject, StringComparison.Ordinal),
            RecoveryOperation.Restore => body is RestoreRecoveryBody restore && restore.Entries.Any(entry =>
                string.Equals(entry.Name, expectedSubject.Trim('/'), StringComparison.Ordinal)),
            RecoveryOperation.FileEntity => body is FileEntityRecoveryBody fileEntity &&
                (string.Equals(fileEntity.EntityName, expectedSubject, StringComparison.Ordinal) ||
                 string.Equals($"{fileEntity.EntityKind}/{fileEntity.EntityName}", expectedSubject, StringComparison.Ordinal)),
            _ => false
        };
        if (!matches) throw Malformed("Recovery evidence subject does not match its selected target", evidencePath);
        return document with { TypedBody = body };
    }

    internal static bool IsCommonEnvelope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
            return document.RootElement.TryGetProperty("format", out JsonElement format)
                && format.ValueKind == JsonValueKind.String
                && format.GetString() == Format;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static RecoveryEnvelopeDocument FromAppend(AppendJournal journal)
    {
        var body = new AppendRecoveryBody(journal.State, journal.IsCreate,
            journal.OldArtifact, journal.OldSidecar, journal.NewArtifact, journal.NewSidecar,
            journal.ArchiveArtifactPath, journal.ArchiveSidecarPath, journal.ArchiveArtifactStatus,
            journal.ArchiveSidecarStatus, journal.PruneCandidates, journal.CommittedHash, journal.Detail);
        return Create(RecoveryOperation.Append, journal.OperationId, journal.Scope, journal.Address,
            Map(journal.State), journal.StartedAt, journal.StartedAt,
            JsonSerializer.SerializeToElement(body, RecoveryEnvelopeJsonContext.Default.AppendRecoveryBody)) with { TypedBody = body };
    }

    internal static RecoveryEnvelopeDocument FromRestore(RestoreJournal journal)
    {
        var body = new RestoreRecoveryBody(journal.TransactionId, journal.State, journal.Entries);
        return Create(RecoveryOperation.Restore, journal.TransactionId, journal.Scope, null,
            journal.State == RestoreJournalState.Completed ? RecoveryLifecycleState.Committed : RecoveryLifecycleState.Prepared,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(body, RecoveryEnvelopeJsonContext.Default.RestoreRecoveryBody)) with { TypedBody = body };
    }

    internal static RecoveryEnvelopeDocument FromFileEntity(FileEntityRecoveryBody body, string scope, string? address,
        string operationId, RecoveryLifecycleState state, DateTimeOffset createdAt, DateTimeOffset updatedAt) =>
        Create(RecoveryOperation.FileEntity, operationId, scope, address, state, createdAt, updatedAt,
            JsonSerializer.SerializeToElement(body, RecoveryEnvelopeJsonContext.Default.FileEntityRecoveryBody)) with { TypedBody = body };

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
            !string.Equals(document.Scope, expectedScope, StringComparison.Ordinal))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.WrongScope,
                "Recovery envelope scope does not match the selected scope.", evidencePath);
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
    internal RecoveryEnvelopeDocument NormalizeLegacy(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath,
        string expectedEvidenceDirectory)
    {
        EnsureFilename(evidencePath, expectedEvidenceDirectory);
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
    RecoveryEnvelopeDocument IRecoveryAdapter.ParseAndNormalize(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath,
        string expectedEvidenceDirectory) =>
        RecoveryEnvelopeCodec.IsCommonEnvelope(bytes) ? RecoveryEnvelopeCodec.ValidateForOperation(RecoveryEnvelopeCodec.Parse(bytes, expectedScope, evidencePath, expectedEvidenceDirectory), RecoveryOperation.Append, expectedScope, evidencePath, expectedEvidenceDirectory) : NormalizeLegacy(bytes, expectedScope, evidencePath, expectedEvidenceDirectory);
    RecoveryEnvelopeDocument IRecoveryAdapter.ValidateForMutation(RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath,
        string expectedSubject, string expectedEvidenceDirectory) =>
        RecoveryEnvelopeCodec.ValidateSubject(RecoveryEnvelopeCodec.ValidateForOperation(envelope, RecoveryOperation.Append, expectedScope, evidencePath, expectedEvidenceDirectory), expectedSubject, evidencePath, expectedEvidenceDirectory);
    RecoveryOutcome IRecoveryAdapter.RecoverOrCleanup(RecoveryEnvelopeDocument envelope, string evidencePath, CancellationToken cancellationToken) => throw new NotSupportedException("Append replay is owned by the append operation.");
    RecoveryOutcome IRecoveryAdapter.Admit(RecoveryEnvelopeDocument envelope, string evidencePath) =>
        new(false, false, false, envelope.Scope, evidencePath, envelope.OperationId, null, null);

    private static void EnsureFilename(string evidencePath, string expectedEvidenceDirectory) =>
        RecoveryEnvelopeCodec.ValidateEvidenceLocationForAdapter(RecoveryOperation.Append, evidencePath, expectedEvidenceDirectory);
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
    internal RecoveryEnvelopeDocument NormalizeLegacy(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath,
        string expectedEvidenceDirectory)
    {
        EnsureFilename(evidencePath, expectedEvidenceDirectory);
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
    RecoveryEnvelopeDocument IRecoveryAdapter.ParseAndNormalize(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath,
        string expectedEvidenceDirectory) =>
        RecoveryEnvelopeCodec.IsCommonEnvelope(bytes) ? RecoveryEnvelopeCodec.ValidateForOperation(RecoveryEnvelopeCodec.Parse(bytes, expectedScope, evidencePath, expectedEvidenceDirectory), RecoveryOperation.Restore, expectedScope, evidencePath, expectedEvidenceDirectory) : NormalizeLegacy(bytes, expectedScope, evidencePath, expectedEvidenceDirectory);
    RecoveryEnvelopeDocument IRecoveryAdapter.ValidateForMutation(RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath,
        string expectedSubject, string expectedEvidenceDirectory) =>
        RecoveryEnvelopeCodec.ValidateSubject(RecoveryEnvelopeCodec.ValidateForOperation(envelope, RecoveryOperation.Restore, expectedScope, evidencePath, expectedEvidenceDirectory), expectedSubject, evidencePath, expectedEvidenceDirectory);
    RecoveryOutcome IRecoveryAdapter.RecoverOrCleanup(RecoveryEnvelopeDocument envelope, string evidencePath, CancellationToken cancellationToken) => throw new NotSupportedException("Restore replay is owned by the restore operation.");
    RecoveryOutcome IRecoveryAdapter.Admit(RecoveryEnvelopeDocument envelope, string evidencePath) =>
        new(false, false, false, envelope.Scope, evidencePath, envelope.OperationId, null, null);

    private static void EnsureFilename(string evidencePath, string expectedEvidenceDirectory) =>
        RecoveryEnvelopeCodec.ValidateEvidenceLocationForAdapter(RecoveryOperation.Restore, evidencePath, expectedEvidenceDirectory);
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

    internal RecoveryEnvelopeDocument NormalizeLegacy(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath,
        string expectedEvidenceDirectory)
    {
        EnsureFilename(evidencePath, expectedEvidenceDirectory);
        if (bytes.Length == 0)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "File-entity recovery evidence is empty.", evidencePath);
        if (RecoveryEnvelopeCodec.IsCommonEnvelope(bytes))
        {
            RecoveryEnvelopeDocument envelope = RecoveryEnvelopeCodec.Parse(bytes, expectedScope, evidencePath, expectedEvidenceDirectory);
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

    RecoveryEnvelopeDocument IRecoveryAdapter.ParseAndNormalize(ReadOnlySpan<byte> bytes, string expectedScope, string evidencePath,
        string expectedEvidenceDirectory) => NormalizeLegacy(bytes, expectedScope, evidencePath, expectedEvidenceDirectory);
    RecoveryEnvelopeDocument IRecoveryAdapter.ValidateForMutation(RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath,
        string expectedSubject, string expectedEvidenceDirectory) =>
        envelope.Operation == RecoveryOperation.FileEntity
            ? ValidateFileEntityMutation(envelope, expectedScope, evidencePath, expectedSubject, expectedEvidenceDirectory)
            : throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed, "Evidence operation does not match file-entity adapter.", evidencePath);
    RecoveryOutcome IRecoveryAdapter.RecoverOrCleanup(RecoveryEnvelopeDocument envelope, string evidencePath, CancellationToken cancellationToken) => throw new NotSupportedException("File-entity replay is owned by the file-entity operation.");
    RecoveryOutcome IRecoveryAdapter.Admit(RecoveryEnvelopeDocument envelope, string evidencePath) =>
        new(false, false, false, envelope.Scope, evidencePath, envelope.OperationId, null, null);

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
    private static void EnsureFilename(string evidencePath, string expectedEvidenceDirectory)
    {
        RecoveryEnvelopeCodec.ValidateEvidenceLocationForAdapter(RecoveryOperation.FileEntity, evidencePath, expectedEvidenceDirectory);
    }

    private static RecoveryEnvelopeDocument ValidateFileEntityMutation(
        RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath,
        string expectedSubject, string expectedEvidenceDirectory)
    {
        RecoveryEnvelopeDocument validated = RecoveryEnvelopeCodec.ValidateForOperation(
            envelope, RecoveryOperation.FileEntity, expectedScope, evidencePath, expectedEvidenceDirectory);
        FileEntityRecoveryBody body = (FileEntityRecoveryBody)(validated.TypedBody
            ?? throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "File-entity recovery body is missing.", evidencePath));

        string entityName = body.EntityName;
        string entityDirectoryName = Path.GetFileName(Path.GetFullPath(expectedEvidenceDirectory));
        if (!string.Equals(body.EntityKind, entityDirectoryName, StringComparison.OrdinalIgnoreCase))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "File-entity recovery evidence belongs to a different entity directory.", evidencePath);

        string oldBodyPath = CanonicalizeMemberPath(body.OldBody.Path, expectedEvidenceDirectory, entityName,
            MemberRole.Body, allowVersions: false, evidencePath);
        string bodyFileName = Path.GetFileName(oldBodyPath);
        ValidateBodyFileName(bodyFileName, entityName, evidencePath);
        string oldMetaPath = CanonicalizeMemberPath(body.OldMeta.Path, expectedEvidenceDirectory, entityName,
            MemberRole.Meta, allowVersions: false, evidencePath);
        ValidateMetaFileName(Path.GetFileName(oldMetaPath), entityName, evidencePath);

        body = body with
        {
            OldBody = body.OldBody with { Path = oldBodyPath },
            OldMeta = body.OldMeta with { Path = oldMetaPath },
            CommittedBody = CanonicalizeOptionalMember(body.CommittedBody, expectedEvidenceDirectory, entityName,
                MemberRole.Body, evidencePath),
            CommittedMeta = CanonicalizeOptionalMember(body.CommittedMeta, expectedEvidenceDirectory, entityName,
                MemberRole.Meta, evidencePath),
            ArchiveBodyPath = CanonicalizeOptionalArchive(body.ArchiveBodyPath, expectedEvidenceDirectory, entityName,
                MemberRole.Body, bodyFileName, evidencePath),
            ArchiveMetaPath = CanonicalizeOptionalArchive(body.ArchiveMetaPath, expectedEvidenceDirectory, entityName,
                MemberRole.Meta, body.ArchiveBodyPath, evidencePath)
        };

        if (body.CommittedBody is not null &&
            !string.Equals(Path.GetFileName(body.CommittedBody.Path), bodyFileName, StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);
        if (body.CommittedMeta is not null)
            ValidateMetaFileName(Path.GetFileName(body.CommittedMeta.Path), entityName, evidencePath);

        return RecoveryEnvelopeCodec.ValidateSubject(validated with { TypedBody = body }, expectedSubject, evidencePath, expectedEvidenceDirectory);
    }

    private static RecoveryMemberSnapshot? CanonicalizeOptionalMember(RecoveryMemberSnapshot? member, string directory,
        string entityName, MemberRole role, string evidencePath)
    {
        return member is null ? null : member with
        {
            Path = CanonicalizeMemberPath(member.Path, directory, entityName, role, allowVersions: false, evidencePath)
        };
    }

    private static string? CanonicalizeOptionalArchive(string? path, string directory,
        string entityName, MemberRole role, string? relatedPath, string evidencePath)
    {
        if (path is null)
            return null;
        string canonicalPath = CanonicalizeMemberPath(path, Path.Combine(directory, "versions"), entityName,
            role, allowVersions: true, evidencePath);
        string fileName = Path.GetFileName(canonicalPath);
        if (role == MemberRole.Body)
        {
            ValidateArchiveBodyFileName(fileName, entityName, relatedPath!, evidencePath);
        }
        else if (relatedPath is not null)
        {
            string relatedFileName = Path.GetFileName(relatedPath);
            if (!string.Equals(fileName, Path.ChangeExtension(relatedFileName, ".meta.json"),
                    StringComparison.OrdinalIgnoreCase))
                throw UnsafeMemberPath(evidencePath);
        }
        else if (!fileName.StartsWith(entityName + "_", StringComparison.OrdinalIgnoreCase) ||
                 !fileName.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);
        return canonicalPath;
    }

    private static string CanonicalizeMemberPath(string path, string directory, string entityName,
        MemberRole role, bool allowVersions, string evidencePath)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw UnsafeMemberPath(evidencePath);
        string[] segments = path.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment is "." or ".."))
            throw UnsafeMemberPath(evidencePath);

        string canonicalDirectory = Path.GetFullPath(directory);
        string canonicalPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(canonicalDirectory, path));
        if (!PathContainment.IsWithin(canonicalDirectory, canonicalPath))
            throw UnsafeMemberPath(evidencePath);
        string? actualDirectory = Path.GetDirectoryName(canonicalPath);
        if (actualDirectory is null || !string.Equals(actualDirectory, canonicalDirectory,
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);

        string fileName = Path.GetFileName(canonicalPath);
        if (!fileName.StartsWith(entityName + (allowVersions ? "_" : "."), StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);
        if (role == MemberRole.Meta && !fileName.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);
        if (role == MemberRole.Body && fileName.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);

        return canonicalPath;
    }

    private static void ValidateBodyFileName(string fileName, string entityName, string evidencePath)
    {
        if (!fileName.StartsWith(entityName + ".", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);
    }

    private static void ValidateMetaFileName(string fileName, string entityName, string evidencePath)
    {
        if (!string.Equals(fileName, entityName + ".meta.json", StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);
    }

    private static void ValidateArchiveBodyFileName(string fileName, string entityName, string bodyFileName,
        string evidencePath)
    {
        if (!fileName.StartsWith(entityName + "_", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fileName), Path.GetExtension(bodyFileName), StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);
    }

    private enum MemberRole
    {
        Body,
        Meta
    }

    private static RecoveryEnvelopeException UnsafeMemberPath(string evidencePath) =>
        new(RecoveryEnvelopeFailure.Malformed, "File-entity recovery member path is unsafe.", evidencePath);

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
