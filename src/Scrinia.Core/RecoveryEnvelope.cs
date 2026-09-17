using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Core;

internal enum RecoveryEnvelopeFailure
{
    Malformed,
    UnsupportedVersion,
    WrongScope,
    Unsafe,
    Inconsistent
}

internal sealed class RecoveryEnvelopeException : IOException
{
    internal RecoveryEnvelopeException(RecoveryEnvelopeFailure failure, string message, string evidencePath,
        Exception? inner = null, int? unsupportedVersion = null, string? operationId = null) : base(message, inner)
    {
        Failure = failure;
        EvidencePath = evidencePath;
        UnsupportedVersion = unsupportedVersion;
        OperationId = operationId;
    }

    internal RecoveryEnvelopeFailure Failure { get; }
    internal string EvidencePath { get; }
    internal int? UnsupportedVersion { get; }
    internal string? OperationId { get; }

    internal RecoveryEnvelopeException WithOperationId(string operationId) =>
        new(Failure, Message, EvidencePath, InnerException, UnsupportedVersion, operationId);
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
    [property: JsonPropertyName("payload")] string? Payload,
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

internal static partial class RecoveryEnvelopeCodec
{
    internal const string Format = "scrinia.recovery";
    internal const int CurrentVersion = 1;
    internal const int CurrentBodyVersion = 1;

    internal static byte[] Serialize(RecoveryEnvelopeDocument document)
    {
        Validate(document, document.Scope, "<new recovery envelope>");
        return JsonSerializer.SerializeToUtf8Bytes(document, RecoveryEnvelopeJsonContext.Default.RecoveryEnvelopeDocument);
    }

    private static RecoveryEnvelopeDocument ParseEnvelopeDocument(ReadOnlySpan<byte> json, string expectedScope, string evidencePath,
        string expectedEvidenceDirectory, bool validateLocation = true, RecoveryOperation? expectedOperation = null)
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
        try
        {
            Validate(document, expectedScope, evidencePath);
            if (expectedOperation is not null && document.Operation != expectedOperation)
                throw Inconsistent("Evidence operation does not match its expected recovery kind", evidencePath);
            if (validateLocation)
                ValidateEvidenceLocation(document, evidencePath, expectedEvidenceDirectory);
            RecoveryBody typedBody = ParseTypedBodyDocument(document, evidencePath);
            return document with { TypedBody = typedBody };
        }
        catch (RecoveryEnvelopeException exception)
        {
            throw exception.WithOperationId(document.OperationId);
        }
    }

    internal static RecoveryEnvelopeDocument ValidateTypedEnvelope(RecoveryEnvelopeDocument document,
        RecoveryOperation expectedOperation, string expectedScope, string evidencePath, string expectedEvidenceDirectory)
    {
        if (document.Operation != expectedOperation)
            throw Inconsistent("Evidence operation does not match its adapter", evidencePath);
        Validate(document, expectedScope, evidencePath);
        ValidateEvidenceLocation(document, evidencePath, expectedEvidenceDirectory);
        RecoveryBody typedBody = document.TypedBody
            ?? throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Typed recovery envelope body is required at this boundary.", evidencePath);
        return document with { TypedBody = typedBody };
    }

    private static RecoveryBody ParseTypedBodyDocument(RecoveryEnvelopeDocument document, string evidencePath)
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
                    EnsureSupportedFileEntityLifecycle(document, evidencePath);
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
                    if ((document.State == RecoveryLifecycleState.Committed) != (fileEntity.State == "committed"))
                        throw new JsonException("File-entity envelope and body lifecycle states are inconsistent.");
                    ValidateFileEntitySnapshotShape(fileEntity, document.State);
                    return fileEntity;
                default:
                    throw new JsonException("Recovery envelope operation is invalid.");
            }
        }
        catch (JsonException exception)
        {
            throw Malformed($"Recovery envelope body is invalid ({exception.Message})", evidencePath, exception);
        }
    }

    private static void ValidateAppendMember(AppendRecoveryBody body)
    {
        AppendJournalMember[] members = [body.OldArtifact, body.OldSidecar, body.NewArtifact, body.NewSidecar];
        if (members.Any(member => string.IsNullOrWhiteSpace(member.Path) || member.Payload is null ||
            string.IsNullOrWhiteSpace(member.Sha256)))
            throw new JsonException("Append recovery member is incomplete.");
    }

    private static void ValidateFileEntityMember(RecoveryMemberSnapshot member)
    {
        if (string.IsNullOrWhiteSpace(member.Path) || string.IsNullOrWhiteSpace(member.Sha256) ||
            (!member.Existed && member.Payload is not null) ||
            (member.Existed && string.IsNullOrWhiteSpace(member.Payload)))
            throw new JsonException("File-entity recovery member is incomplete.");
        try
        {
            byte[] payload = Convert.FromBase64String(member.Payload ?? string.Empty);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(payload)), member.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new JsonException("File-entity recovery member hash is invalid.");
        }
        catch (FormatException exception)
        {
            throw new JsonException("File-entity recovery member payload is invalid.", exception);
        }
    }

    private static void ValidateOptionalFileEntityMember(RecoveryMemberSnapshot? member)
    {
        if (member is not null) ValidateFileEntityMember(member);
    }

    private static void ValidateFileEntitySnapshotShape(FileEntityRecoveryBody body,
        RecoveryLifecycleState envelopeState)
    {
        bool metadataOnly = CanonicalPathEquals(body.OldBody.Path, body.OldMeta.Path);
        if (metadataOnly)
        {
            if (!string.Equals(body.EntityKind, "sidecar", StringComparison.OrdinalIgnoreCase) ||
                body.CommittedBody is not null ||
                (envelopeState == RecoveryLifecycleState.Committed && body.CommittedMeta is null) ||
                (envelopeState == RecoveryLifecycleState.Prepared &&
                    (body.CommittedBody is not null || body.CommittedMeta is not null)))
                throw new JsonException("Metadata-only recovery evidence has an invalid snapshot shape.");
            return;
        }

        if (envelopeState == RecoveryLifecycleState.Prepared &&
            (body.CommittedBody is not null || body.CommittedMeta is not null))
            throw new JsonException("Prepared recovery evidence contains committed snapshots.");
        if (envelopeState == RecoveryLifecycleState.Committed &&
            (body.CommittedBody is null || body.CommittedMeta is null))
            throw new JsonException("Committed recovery evidence is missing a member snapshot.");
    }

    private static void EnsureSupportedFileEntityLifecycle(RecoveryEnvelopeDocument document,
        string evidencePath)
    {
        if (document.State is RecoveryLifecycleState.Prepared or RecoveryLifecycleState.Committed)
            return;

        throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Inconsistent,
            "File-entity recovery evidence has an unsupported lifecycle state.", evidencePath);
    }

    private static bool CanonicalPathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
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
        if (!valid) throw Inconsistent("Recovery evidence filename does not match its operation", evidencePath);
    }

    internal static RecoveryEnvelopeDocument ValidateSubject(RecoveryEnvelopeDocument document, string expectedSubject,
        string evidencePath, string expectedEvidenceDirectory)
    {
        if (string.IsNullOrWhiteSpace(expectedSubject)) throw Malformed("Recovery subject is required", evidencePath);
        ValidateEvidenceLocation(document, evidencePath, expectedEvidenceDirectory);
        RecoveryBody body = document.TypedBody
            ?? throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Typed recovery envelope body is required at this boundary.", evidencePath);
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
        if (!matches) throw Inconsistent("Recovery evidence subject does not match its selected target", evidencePath);
        return document with { TypedBody = body };
    }

    internal static bool IsCommonEnvelope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return false;
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        return text.Contains("\"format\"", StringComparison.Ordinal) &&
            text.Contains(Format, StringComparison.Ordinal);
    }

    internal static void EnsureOperation(ReadOnlySpan<byte> bytes, RecoveryOperation expectedOperation,
        string evidencePath)
    {
        // Operation identity is checked by ParseVerifiedEnvelope after the bounded read.
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
        if (document.Operation == RecoveryOperation.FileEntity)
            EnsureSupportedFileEntityLifecycle(document, evidencePath);
        if (document.UpdatedAt < document.CreatedAt) throw Malformed("Recovery envelope timestamps are invalid", evidencePath);
        if (document.Body.ValueKind != JsonValueKind.Object) throw Malformed("Recovery envelope body is missing", evidencePath);
    }

    private static RecoveryLifecycleState Map(AppendJournalState state) => state switch
    {
        AppendJournalState.Prepared => RecoveryLifecycleState.Prepared,
        AppendJournalState.ArchiveInProgress or AppendJournalState.Archived or AppendJournalState.NewPairInProgress => RecoveryLifecycleState.Applying,
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

    private static RecoveryEnvelopeException Inconsistent(string message, string path) =>
        new(RecoveryEnvelopeFailure.Inconsistent, $"{message}: {path}", path);

    private static RecoveryEnvelopeException Unsupported(string message, string path) =>
        new(RecoveryEnvelopeFailure.UnsupportedVersion, $"{message}: {path}", path);
}

internal sealed class AppendRecoveryAdapter : IRecoveryAdapter
{
    internal RecoveryOperation Operation => RecoveryOperation.Append;
    RecoveryOperation IRecoveryAdapter.Operation => Operation;
    internal bool IsCandidateFile(string fileName) => fileName is AppendJournal.FileName or ".append-journal.json";
    bool IRecoveryAdapter.IsCandidateFile(string fileName) => IsCandidateFile(fileName);

    internal RecoveryEnvelopeDocument ToEnvelope(AppendJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return RecoveryEnvelopeCodec.FromAppend(journal);
    }

    internal AppendJournal FromEnvelope(RecoveryEnvelopeDocument document, string evidencePath)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Operation != Operation)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Evidence operation does not match the append adapter.", evidencePath);
        AppendRecoveryBody body = document.TypedBody as AppendRecoveryBody
            ?? throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Append recovery body is missing.", evidencePath);
        AppendJournal journal = new(
            "scrinia.append-journal", DurableFormats.AppendJournal.Current,
            document.OperationId, document.Address!, document.Scope, body.State, body.IsCreate,
            body.OldArtifact, body.OldSidecar, body.NewArtifact, body.NewSidecar,
            body.ArchiveArtifactPath, body.ArchiveSidecarPath, body.ArchiveArtifactStatus,
            body.ArchiveSidecarStatus, body.PruneCandidates, document.CreatedAt,
            body.CommittedHash, body.Detail);
        try
        {
            string? versionsDirectory = Path.GetDirectoryName(evidencePath);
            string? storeDirectory = !Path.IsPathRooted(evidencePath) || versionsDirectory is null
                ? null
                : Path.GetDirectoryName(versionsDirectory);
            AppendJournalCodec.ValidateForRecovery(journal, evidencePath, storeDirectory);
        }
        catch (AppendJournalException exception)
        {
            throw exception.WithOperationId(journal.OperationId);
        }
        return journal;
    }

    internal RecoveryOutcome RecoverAppend(RecoveryEnvelopeDocument document, string evidencePath,
        string expectedScope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = FromEnvelope(document, evidencePath);
        if (!string.Equals(document.Scope, expectedScope, StringComparison.Ordinal))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.WrongScope,
                "Append recovery evidence belongs to another scope.", evidencePath);
        return new RecoveryOutcome(false, false, false, expectedScope, evidencePath,
            document.OperationId, null, null);
    }
    RecoveryEnvelopeDocument IRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope envelope)
    {
        _ = AppendJournalCodec.ConsumeParsed(in envelope);
        return envelope.Document;
    }
    RecoveryEnvelopeDocument IRecoveryAdapter.ValidateForMutation(RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath,
        string expectedSubject, string expectedEvidenceDirectory) =>
        RecoveryEnvelopeCodec.ValidateSubject(RecoveryEnvelopeCodec.ValidateTypedEnvelope(envelope, RecoveryOperation.Append, expectedScope, evidencePath, expectedEvidenceDirectory), expectedSubject, evidencePath, expectedEvidenceDirectory);
    RecoveryOutcome IRecoveryAdapter.RecoverOrCleanup(RecoveryEnvelopeDocument envelope, string evidencePath, CancellationToken cancellationToken) =>
        RecoverCommittedCleanup(envelope, evidencePath, cancellationToken);
    RecoveryOutcome IRecoveryAdapter.Admit(RecoveryEnvelopeDocument envelope, string evidencePath) =>
        new(false, false, false, envelope.Scope, evidencePath, envelope.OperationId, null, null);

    private static void EnsureFilename(string evidencePath, string expectedEvidenceDirectory) =>
        RecoveryEnvelopeCodec.ValidateEvidenceLocationForAdapter(RecoveryOperation.Append, evidencePath, expectedEvidenceDirectory);
    private static RecoveryOutcome RecoverCommittedCleanup(RecoveryEnvelopeDocument envelope,
        string evidencePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendJournal journal = new AppendRecoveryAdapter().FromEnvelope(envelope, evidencePath);
        if (envelope.State is not (RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending) ||
            journal.State is not (AppendJournalState.Committed or AppendJournalState.CleanupPending))
            return Blocked(envelope, evidencePath, "Append recovery evidence is not committed cleanup evidence.");

        AppendJournalMember[] committedMembers = [journal.NewArtifact, journal.NewSidecar];
        foreach (AppendJournalMember member in committedMembers)
        {
            string path = ResolveAppendPath(evidencePath, member.Path);
            string storeDirectory = Path.GetDirectoryName(Path.GetDirectoryName(evidencePath)!)!;
            RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
                new RecoveryScopeBinding(envelope.Scope, storeDirectory), RecoveryEvidenceKind.AppendJournal,
                path, new RecoveryExpectedHash(member.Sha256), MaintenanceLimits.Hard, cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            bool verified = verification.Outcome == RecoveryVerificationOutcome.Verified &&
                verification.SourceContent is not null;
            verification.SourceContent?.Dispose();
            if (!verified)
                return Blocked(envelope, evidencePath, "Committed append live state failed verification.");
        }

        List<string> failures = [];
        IEnumerable<string> cleanupPaths = new[] { journal.ArchiveArtifactPath, journal.ArchiveSidecarPath }
            .Concat(journal.PruneCandidates)
            .Where(path => path is not null)
            .Select(path => ResolveAppendPath(evidencePath, path!));
        foreach (string path in cleanupPaths)
            TryDelete(path, failures);
        TryDelete(evidencePath, failures);
        return failures.Count == 0
            ? new RecoveryOutcome(false, true, false, envelope.Scope, evidencePath, envelope.OperationId, null, null)
            : new RecoveryOutcome(false, true, true, envelope.Scope, evidencePath, envelope.OperationId, null,
                $"Append cleanup remains pending: {string.Join("; ", failures)}");
    }

    private static string ResolveAppendPath(string evidencePath, string path)
    {
        string versionsDirectory = Path.GetDirectoryName(evidencePath)!;
        string storeDirectory = Path.GetDirectoryName(versionsDirectory)!;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(storeDirectory, path));
    }

    private static void TryDelete(string path, List<string> failures)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { failures.Add($"{path}: {exception.GetType().Name}"); }
    }

    private static RecoveryOutcome Blocked(RecoveryEnvelopeDocument envelope, string evidencePath, string detail) =>
        new(true, false, false, envelope.Scope, evidencePath, envelope.OperationId,
            new RecoveryBlock(envelope.Scope, evidencePath, RecoveryOperation.Append, envelope.OperationId,
                RecoveryBlockReason.Inconsistent, detail), null);
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

    internal RecoveryEnvelopeDocument ToEnvelope(RestoreJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return RecoveryEnvelopeCodec.FromRestore(journal);
    }

    internal RestoreJournal FromEnvelope(RecoveryEnvelopeDocument document, string evidencePath)
    {
        ArgumentNullException.ThrowIfNull(document);
        RecoveryEnvelopeDocument validated = RecoveryEnvelopeCodec.ValidateTypedEnvelope(
            document, Operation, document.Scope, evidencePath,
            Path.GetDirectoryName(evidencePath) ?? throw new ArgumentException(
                "Restore evidence path must have a directory.", nameof(evidencePath)));
        RestoreRecoveryBody body = validated.TypedBody as RestoreRecoveryBody
            ?? throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Restore recovery body is missing.", evidencePath);
        if (!string.Equals(validated.OperationId, body.TransactionId, StringComparison.Ordinal))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Restore transaction identity is inconsistent.", evidencePath);
        if (!IsLifecycleConsistent(validated.State, body.State))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Restore envelope and body lifecycle states are inconsistent.", evidencePath);

        RestoreJournal journal = new(
            "scrinia.restore-journal", DurableFormats.RestoreJournal.Current, body.State,
            body.TransactionId, validated.Scope, body.Entries);
        try
        {
            RestoreJournalCodec.ValidateLegacyParsed(journal, validated.Scope, evidencePath);
        }
        catch (RestoreRecoveryException exception)
        {
            throw new RecoveryEnvelopeException(
                exception.Failure == RestoreRecoveryFailure.UnsupportedVersion
                    ? RecoveryEnvelopeFailure.UnsupportedVersion
                    : RecoveryEnvelopeFailure.Malformed,
                exception.Message, evidencePath, exception, exception.UnsupportedVersion);
        }
        return journal;
    }

    private static bool IsLifecycleConsistent(RecoveryLifecycleState envelopeState, RestoreJournalState bodyState) =>
        (envelopeState, bodyState) switch
        {
            (RecoveryLifecycleState.Prepared, RestoreJournalState.Prepared) => true,
            (RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending,
                RestoreJournalState.Completed) => true,
            _ => false
        };
    RecoveryEnvelopeDocument IRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope envelope)
    {
        _ = RestoreJournalCodec.ConsumeParsed(in envelope);
        return envelope.Document;
    }
    RecoveryEnvelopeDocument IRecoveryAdapter.ValidateForMutation(RecoveryEnvelopeDocument envelope, string expectedScope,
        string evidencePath, string expectedSubject, string expectedEvidenceDirectory)
    {
        RecoveryEnvelopeDocument validated = RecoveryEnvelopeCodec.ValidateSubject(
            RecoveryEnvelopeCodec.ValidateTypedEnvelope(envelope, RecoveryOperation.Restore, expectedScope,
                evidencePath, expectedEvidenceDirectory), expectedSubject, evidencePath, expectedEvidenceDirectory);
        RestoreJournal journal = FromEnvelope(validated, evidencePath);
        return validated with
        {
            TypedBody = new RestoreRecoveryBody(journal.TransactionId, journal.State, journal.Entries)
        };
    }
    RecoveryOutcome IRecoveryAdapter.RecoverOrCleanup(RecoveryEnvelopeDocument envelope, string evidencePath, CancellationToken cancellationToken) =>
        RecoverCommittedCleanup(envelope, evidencePath, cancellationToken);
    RecoveryOutcome IRecoveryAdapter.Admit(RecoveryEnvelopeDocument envelope, string evidencePath) =>
        new(false, false, false, envelope.Scope, evidencePath, envelope.OperationId, null, null);

    private static void EnsureFilename(string evidencePath, string expectedEvidenceDirectory) =>
        RecoveryEnvelopeCodec.ValidateEvidenceLocationForAdapter(RecoveryOperation.Restore, evidencePath, expectedEvidenceDirectory);
    private static RecoveryOutcome RecoverCommittedCleanup(RecoveryEnvelopeDocument envelope,
        string evidencePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreJournal journal = new RestoreRecoveryAdapter().FromEnvelope(envelope, evidencePath);
        if (envelope.State is not (RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending) ||
            journal.State != RestoreJournalState.Completed)
            return new RecoveryOutcome(true, false, false, envelope.Scope, evidencePath, envelope.OperationId,
                new RecoveryBlock(envelope.Scope, evidencePath, RecoveryOperation.Restore, envelope.OperationId,
                    RecoveryBlockReason.Inconsistent, "Restore recovery evidence is not committed cleanup evidence."), null);

        string storeDirectory = Path.GetDirectoryName(evidencePath)!;
        foreach (RestoreJournalEntry entry in journal.Entries)
        {
            (byte[] body, byte[] sidecar) = RestoreJournalCodec.DecodeAndVerifyPayloads(entry, journal.Scope, evidencePath);
            VerifyFile(Path.Combine(storeDirectory, entry.BodyFile), body, evidencePath, journal.Scope, storeDirectory, cancellationToken);
            VerifyFile(Path.Combine(storeDirectory, entry.SidecarFile), sidecar, evidencePath, journal.Scope, storeDirectory, cancellationToken);
        }

        try
        {
            FileMemoryStore.ThrowIfRestoreJournalTransitionFails(
                RestoreJournalTransition.BeforeCleanupDelete, evidencePath);
            File.Delete(evidencePath);
            return new RecoveryOutcome(false, true, false, envelope.Scope, evidencePath, envelope.OperationId, null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RecoveryOutcome(false, true, true, envelope.Scope, evidencePath, envelope.OperationId, null,
                $"Restore cleanup remains pending: {exception.GetType().Name}");
        }
    }

    private static void VerifyFile(string path, byte[] expected, string evidencePath, string scope, string storeDirectory,
        CancellationToken cancellationToken)
    {
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expected));
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding(scope, storeDirectory), RecoveryEvidenceKind.RestoreJournal,
            path, new RecoveryExpectedHash(expectedHash), MaintenanceLimits.Hard, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        verification.SourceContent?.Dispose();
        if (verification.Outcome != RecoveryVerificationOutcome.Verified)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Inconsistent,
                "Committed restore destination failed verification.", evidencePath);
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

internal sealed class FileEntityRecoveryAdapter : IRecoveryAdapter
{
    internal RecoveryOperation Operation => RecoveryOperation.FileEntity;
    RecoveryOperation IRecoveryAdapter.Operation => Operation;
    internal bool IsCandidateFile(string fileName) => fileName.StartsWith(".file-entity-", StringComparison.Ordinal) && fileName.EndsWith(".recovery", StringComparison.Ordinal);
    bool IRecoveryAdapter.IsCandidateFile(string fileName) => IsCandidateFile(fileName);

    internal FileEntityRecoveryBody ConsumeParsed(in RecoveryVerifiedEnvelope envelope) =>
        envelope.TypedBody as FileEntityRecoveryBody
            ?? throw new ArgumentException("Verified envelope does not contain a file-entity body.", nameof(envelope));

    RecoveryEnvelopeDocument IRecoveryAdapter.ConsumeParsed(in RecoveryVerifiedEnvelope envelope)
    {
        _ = ConsumeParsed(in envelope);
        return envelope.Document;
    }
    RecoveryEnvelopeDocument IRecoveryAdapter.ValidateForMutation(RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath,
        string expectedSubject, string expectedEvidenceDirectory) =>
        envelope.Operation == RecoveryOperation.FileEntity
            ? ValidateFileEntityMutation(envelope, expectedScope, evidencePath, expectedSubject, expectedEvidenceDirectory)
            : throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Inconsistent, "Evidence operation does not match file-entity adapter.", evidencePath);
    RecoveryOutcome IRecoveryAdapter.RecoverOrCleanup(RecoveryEnvelopeDocument envelope, string evidencePath, CancellationToken cancellationToken) =>
        RecoverCommittedCleanup(envelope, evidencePath, cancellationToken);
    RecoveryOutcome IRecoveryAdapter.Admit(RecoveryEnvelopeDocument envelope, string evidencePath) =>
        new(false, false, false, envelope.Scope, evidencePath, envelope.OperationId, null, null);

    private static RecoveryEnvelopeDocument ValidateFileEntityMutation(
        RecoveryEnvelopeDocument envelope, string expectedScope, string evidencePath,
        string expectedSubject, string expectedEvidenceDirectory)
    {
        RecoveryEnvelopeDocument validated = RecoveryEnvelopeCodec.ValidateTypedEnvelope(
            envelope, RecoveryOperation.FileEntity, expectedScope, evidencePath, expectedEvidenceDirectory);
        FileEntityRecoveryBody body = (FileEntityRecoveryBody)(validated.TypedBody
            ?? throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "File-entity recovery body is missing.", evidencePath));

        string entityName = body.EntityName;
        string entityDirectoryName = Path.GetFileName(Path.GetFullPath(expectedEvidenceDirectory));
        bool kindMatchesDirectory = string.Equals(body.EntityKind, entityDirectoryName, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(body.EntityKind, "skill", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entityDirectoryName, "skills", StringComparison.OrdinalIgnoreCase))
            || string.Equals(body.EntityKind, "sidecar", StringComparison.OrdinalIgnoreCase);
        if (!kindMatchesDirectory)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Inconsistent,
                "File-entity recovery evidence belongs to a different entity directory.", evidencePath);

        bool metadataOnly = string.Equals(body.EntityKind, "sidecar", StringComparison.OrdinalIgnoreCase);
        string oldBodyPath = CanonicalizeMemberPath(body.OldBody.Path, expectedEvidenceDirectory, entityName,
            metadataOnly ? MemberRole.Meta : MemberRole.Body, allowVersions: false, evidencePath);
        string bodyFileName = Path.GetFileName(oldBodyPath);
        if (!metadataOnly)
            ValidateBodyFileName(bodyFileName, entityName, evidencePath);
        string oldMetaPath = CanonicalizeMemberPath(body.OldMeta.Path, expectedEvidenceDirectory, entityName,
            MemberRole.Meta, allowVersions: false, evidencePath);
        ValidateMetaFileName(Path.GetFileName(oldMetaPath), entityName, evidencePath);

        body = body with
        {
            OldBody = body.OldBody with { Path = oldBodyPath },
            OldMeta = body.OldMeta with { Path = oldMetaPath },
            CommittedBody = CanonicalizeOptionalMember(body.CommittedBody, expectedEvidenceDirectory, entityName,
                metadataOnly ? MemberRole.Meta : MemberRole.Body, evidencePath),
            CommittedMeta = CanonicalizeOptionalMember(body.CommittedMeta, expectedEvidenceDirectory, entityName,
                MemberRole.Meta, evidencePath),
            ArchiveBodyPath = CanonicalizeOptionalArchive(body.ArchiveBodyPath, expectedEvidenceDirectory, entityName,
                metadataOnly ? MemberRole.Meta : MemberRole.Body, bodyFileName, evidencePath),
            ArchiveMetaPath = CanonicalizeOptionalArchive(body.ArchiveMetaPath, expectedEvidenceDirectory, entityName,
                MemberRole.Meta, body.ArchiveBodyPath, evidencePath)
        };

        ValidateFileEntitySnapshotShape(body, validated.State, evidencePath);

        if (!metadataOnly && body.CommittedBody is not null &&
            !string.Equals(Path.GetFileName(body.CommittedBody.Path), bodyFileName, StringComparison.OrdinalIgnoreCase))
            throw UnsafeMemberPath(evidencePath);
        if (body.CommittedMeta is not null)
            ValidateMetaFileName(Path.GetFileName(body.CommittedMeta.Path), entityName, evidencePath);

        return RecoveryEnvelopeCodec.ValidateSubject(validated with { TypedBody = body }, expectedSubject, evidencePath, expectedEvidenceDirectory);
    }

    private static RecoveryOutcome RecoverCommittedCleanup(RecoveryEnvelopeDocument envelope,
        string evidencePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string evidenceDirectory = Path.GetDirectoryName(evidencePath)!;
        FileEntityRecoveryBody sourceBody = envelope.TypedBody as FileEntityRecoveryBody
            ?? throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "File-entity recovery body is missing.", evidencePath);
        RecoveryEnvelopeDocument validated = ValidateFileEntityMutation(envelope, envelope.Scope, evidencePath,
            $"{sourceBody.EntityKind}/{sourceBody.EntityName}", evidenceDirectory);
        if (validated.State is not (RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending))
            return new RecoveryOutcome(true, false, false, envelope.Scope, evidencePath, envelope.OperationId,
                new RecoveryBlock(envelope.Scope, evidencePath, RecoveryOperation.FileEntity, envelope.OperationId,
                    RecoveryBlockReason.Inconsistent, "File-entity recovery evidence is not committed cleanup evidence."), null);

        FileEntityRecoveryBody body = (FileEntityRecoveryBody)validated.TypedBody!;
        bool metadataOnly = string.Equals(body.EntityKind, "sidecar", StringComparison.OrdinalIgnoreCase);
        if (!metadataOnly)
            VerifyCommittedSnapshot(body.CommittedBody, evidencePath, envelope.Scope, cancellationToken);
        VerifyCommittedSnapshot(body.CommittedMeta, evidencePath, envelope.Scope, cancellationToken);
        List<string> failures = [];
        if (!metadataOnly)
            TryDelete(body.ArchiveBodyPath, failures);
        TryDelete(body.ArchiveMetaPath, failures);
        TryDelete(evidencePath, failures);
        return failures.Count == 0
            ? new RecoveryOutcome(false, true, false, envelope.Scope, evidencePath, envelope.OperationId, null, null)
            : new RecoveryOutcome(false, true, true, envelope.Scope, evidencePath, envelope.OperationId, null,
                $"File-entity cleanup remains pending: {string.Join("; ", failures)}");
    }

    private static void VerifyCommittedSnapshot(RecoveryMemberSnapshot? snapshot, string evidencePath, string scope,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Inconsistent,
                "Committed file-entity evidence is missing a snapshot.", evidencePath);
        byte[] expected = Convert.FromBase64String(snapshot.Payload ?? string.Empty);
        string directory = Path.GetDirectoryName(evidencePath)!;
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding(scope, directory), RecoveryEvidenceKind.FileEntityJournal,
            snapshot.Path, new RecoveryExpectedHash(Convert.ToHexStringLower(SHA256.HashData(expected))),
            MaintenanceLimits.Hard, cancellationToken).AsTask().GetAwaiter().GetResult();
        verification.SourceContent?.Dispose();
        if (verification.Outcome != RecoveryVerificationOutcome.Verified)
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Inconsistent,
                "Committed file-entity destination failed verification.", evidencePath);
    }

    private static void TryDelete(string? path, List<string> failures)
    {
        if (path is null)
            return;
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { failures.Add($"{path}: {exception.GetType().Name}"); }
    }

    private static void ValidateFileEntitySnapshotShape(FileEntityRecoveryBody body,
        RecoveryLifecycleState envelopeState, string evidencePath)
    {
        bool metadataOnly = string.Equals(body.EntityKind, "sidecar", StringComparison.OrdinalIgnoreCase);
        if (metadataOnly)
        {
            if (body.CommittedBody is not null ||
                (envelopeState == RecoveryLifecycleState.Prepared && body.CommittedMeta is not null) ||
                (envelopeState == RecoveryLifecycleState.Committed && body.CommittedMeta is null))
                throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                    "Metadata-only recovery evidence has an invalid snapshot shape.", evidencePath);
            return;
        }

        if (envelopeState == RecoveryLifecycleState.Prepared &&
            (body.CommittedBody is not null || body.CommittedMeta is not null))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Prepared recovery evidence contains committed snapshots.", evidencePath);
        if (envelopeState == RecoveryLifecycleState.Committed &&
            (body.CommittedBody is null || body.CommittedMeta is null))
            throw new RecoveryEnvelopeException(RecoveryEnvelopeFailure.Malformed,
                "Committed recovery evidence is missing a member snapshot.", evidencePath);
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
        new(RecoveryEnvelopeFailure.Unsafe, "File-entity recovery member path is unsafe.", evidencePath);

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
