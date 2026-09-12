using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Formats;

namespace Scrinia.Core;

internal sealed record MultiMemoryTransactionRecord
{
    public MultiMemoryTransactionRecord(
        string format,
        int version,
        string transactionId,
        string scope,
        MultiMemoryTransactionState state,
        IReadOnlyList<MultiMemoryParticipantManifest> participants,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Format = RequireText(format, nameof(format));
        Version = version;
        TransactionId = RequireText(transactionId, nameof(transactionId));
        Scope = RequireText(scope, nameof(scope));
        State = state;
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        if (participants.Any(participant => participant is null))
            throw new ArgumentException("Participants cannot contain null values.", nameof(participants));

        Participants = Array.AsReadOnly(participants.ToArray());
        CreatedAt = RequireUtc(createdAt, nameof(createdAt));
        UpdatedAt = RequireUtc(updatedAt, nameof(updatedAt));
    }

    public string Format { get; }
    public int Version { get; }
    public string TransactionId { get; }
    public string Scope { get; }
    [JsonConverter(typeof(MultiMemoryTransactionStateJsonConverter))]
    public MultiMemoryTransactionState State { get; }
    public IReadOnlyList<MultiMemoryParticipantManifest> Participants { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException("Timestamp must use UTC.", parameterName);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MultiMemoryTransactionRecord))]
[JsonSerializable(typeof(MultiMemoryParticipantManifest))]
[JsonSerializable(typeof(MultiMemoryTransactionState))]
[JsonSerializable(typeof(MultiMemoryParticipantManifest[]))]
internal partial class MultiMemoryTransactionJsonContext : JsonSerializerContext;

internal sealed class MultiMemoryTransactionStateJsonConverter : JsonConverter<MultiMemoryTransactionState>
{
    public override MultiMemoryTransactionState Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Transaction state must be a string.");

        return reader.GetString() switch
        {
            "prepared" => MultiMemoryTransactionState.Prepared,
            "committed" => MultiMemoryTransactionState.Committed,
            "recoveryRequired" => MultiMemoryTransactionState.RecoveryRequired,
            _ => throw new JsonException("Transaction state is unsupported.")
        };
    }

    public override void Write(Utf8JsonWriter writer, MultiMemoryTransactionState value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            MultiMemoryTransactionState.Prepared => "prepared",
            MultiMemoryTransactionState.Committed => "committed",
            MultiMemoryTransactionState.RecoveryRequired => "recoveryRequired",
            _ => throw new JsonException("Transaction state is unsupported.")
        });
}

internal static class MultiMemoryTransactionRecordCodec
{
    internal const string FormatName = "scrinia.multi-memory-transaction";
    internal const string FileName = ".multi-memory-transaction.json";
    private const int CurrentVersion = 1;
    private const int MaximumDocumentBytes = 1024 * 1024;
    private const int MaximumParticipants = 4096;
    private const int MaximumIdentityLength = 512;

    internal static byte[] Serialize(MultiMemoryTransactionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record, record.Scope, null, null);
        return JsonSerializer.SerializeToUtf8Bytes(record, MultiMemoryTransactionJsonContext.Default.MultiMemoryTransactionRecord);
    }

    internal static MultiMemoryTransactionRecord ParseAndValidate(
        ReadOnlySpan<byte> json,
        string expectedScope,
        string recordPath,
        string? expectedStoreDirectory = null)
    {
        if (json.IsEmpty)
            throw new MultiMemoryTransactionRecordException("Coordinator record is empty.", MultiMemoryFailure.Malformed);
        if (json.Length > MaximumDocumentBytes)
            throw new MultiMemoryTransactionRecordException("Coordinator record exceeds its size bound.", MultiMemoryFailure.Malformed);

        MultiMemoryTransactionRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(json, MultiMemoryTransactionJsonContext.Default.MultiMemoryTransactionRecord);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new MultiMemoryTransactionRecordException("Coordinator record is malformed.", MultiMemoryFailure.Malformed, exception);
        }

        if (record is null)
            throw new MultiMemoryTransactionRecordException("Coordinator record must be a JSON object.", MultiMemoryFailure.Malformed);
        try
        {
            Validate(record, expectedScope, recordPath, expectedStoreDirectory);
        }
        catch (MultiMemoryTransactionRecordException exception)
        {
            throw exception.Scope is null ? exception.WithScope(expectedScope) : exception;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            throw new MultiMemoryTransactionRecordException("Coordinator record is unsafe or inconsistent.", MultiMemoryFailure.Unsafe, exception);
        }
        return record;
    }

    internal static MultiMemoryTransactionRecord WithState(
        MultiMemoryTransactionRecord record, MultiMemoryTransactionState state, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new MultiMemoryTransactionRecord(record.Format, record.Version, record.TransactionId,
            record.Scope, state, record.Participants, record.CreatedAt, updatedAt);
    }

    internal static string GetPath(string scopeDirectory)
    {
        if (string.IsNullOrWhiteSpace(scopeDirectory))
            throw new ArgumentException("Scope directory is required.", nameof(scopeDirectory));
        string root = PathContainment.ResolveCanonical(scopeDirectory);
        return Path.Combine(root, FileName);
    }

    internal static void Write(string scopeDirectory, MultiMemoryTransactionRecord record)
    {
        string path = GetPath(scopeDirectory);
        DurableFile.AtomicWriteAllBytes(path, Serialize(record));
    }

    internal static MultiMemoryTransactionRecord Read(string scopeDirectory, string expectedScope)
    {
        string path;
        try
        {
            path = GetPath(scopeDirectory);
            return ParseAndValidate(ReadBounded(path), expectedScope, path, scopeDirectory);
        }
        catch (MultiMemoryTransactionRecordException)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new MultiMemoryTransactionRecordException("Coordinator record is missing.",
                MultiMemoryFailure.ReplayIoFailed, exception, expectedScope: expectedScope);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MultiMemoryTransactionRecordException("Coordinator record cannot be accessed safely.",
                MultiMemoryFailure.Unsafe, exception, expectedScope: expectedScope);
        }
        catch (IOException exception)
        {
            throw new MultiMemoryTransactionRecordException("Coordinator record could not be read.",
                MultiMemoryFailure.ReplayIoFailed, exception, expectedScope: expectedScope);
        }
        catch (ArgumentException exception)
        {
            throw new MultiMemoryTransactionRecordException("Coordinator record path is unsafe.",
                MultiMemoryFailure.Unsafe, exception, expectedScope: expectedScope);
        }
    }

    internal static void Delete(string scopeDirectory)
    {
        string path = GetPath(scopeDirectory);
        File.Delete(path);
    }

    private static void Validate(MultiMemoryTransactionRecord record, string expectedScope,
        string? recordPath, string? expectedStoreDirectory)
    {
        if (!string.Equals(record.Format, FormatName, StringComparison.Ordinal))
            throw new MultiMemoryTransactionRecordException("Coordinator record format is unsupported.", MultiMemoryFailure.Malformed);
        if (record.Version != CurrentVersion)
            throw new MultiMemoryTransactionRecordException(
                "Coordinator record version is unsupported.", MultiMemoryFailure.UnsupportedVersion);
        if (string.IsNullOrWhiteSpace(expectedScope) || !string.Equals(record.Scope, expectedScope, StringComparison.Ordinal))
            throw new MultiMemoryTransactionRecordException("Coordinator record scope is inconsistent.", MultiMemoryFailure.Inconsistent);
        if (record.Participants.Count is 0 or > MaximumParticipants)
            throw new MultiMemoryTransactionRecordException("Coordinator participant count is outside its bounds.", MultiMemoryFailure.Malformed);
        if (record.UpdatedAt < record.CreatedAt)
            throw new MultiMemoryTransactionRecordException("Coordinator timestamps are inconsistent.", MultiMemoryFailure.Inconsistent);
        RequireBounded(record.TransactionId, nameof(record.TransactionId));
        RequireBounded(record.Scope, nameof(record.Scope));
        if (recordPath is not null &&
            (HasTraversalSegment(recordPath) ||
             Path.IsPathRooted(recordPath) && expectedStoreDirectory is null ||
             !string.Equals(Path.GetFileName(recordPath), FileName, StringComparison.Ordinal)))
            throw new MultiMemoryTransactionRecordException("Coordinator record path is unsafe.", MultiMemoryFailure.Unsafe);

        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        string? previousSubject = null;
        string? previousOperation = null;
        foreach (MultiMemoryParticipantManifest participant in record.Participants)
        {
            RequireBounded(participant.Subject, nameof(participant.Subject));
            RequireBounded(participant.OperationId, nameof(participant.OperationId));
            RequireBounded(participant.Kind, nameof(participant.Kind));
            if (!subjects.Add(participant.Subject) || !operationIds.Add(participant.OperationId))
                throw new MultiMemoryTransactionRecordException("Coordinator participant identity is duplicated.", MultiMemoryFailure.Inconsistent);
            if (!IsSafeSubject(participant.Subject))
                throw new MultiMemoryTransactionRecordException("Coordinator participant subject is unsafe.", MultiMemoryFailure.Unsafe);
            if (previousSubject is not null &&
                (string.CompareOrdinal(previousSubject, participant.Subject) > 0 ||
                 string.Equals(previousSubject, participant.Subject, StringComparison.Ordinal) &&
                 string.CompareOrdinal(previousOperation, participant.OperationId) > 0))
                throw new MultiMemoryTransactionRecordException("Coordinator participants are not deterministically ordered.", MultiMemoryFailure.Inconsistent);
            previousSubject = participant.Subject;
            previousOperation = participant.OperationId;
        }

        if (recordPath is not null && expectedStoreDirectory is not null &&
            !PathContainment.IsWithin(expectedStoreDirectory, recordPath))
            throw new MultiMemoryTransactionRecordException("Coordinator record path is outside its scope.", MultiMemoryFailure.Unsafe);
    }

    private static void RequireBounded(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdentityLength)
            throw new MultiMemoryTransactionRecordException($"Coordinator {name} is outside its bounds.", MultiMemoryFailure.Malformed);
    }

    private static bool IsSafeSubject(string subject) =>
        !Path.IsPathRooted(subject) && !subject.Contains('\\') && !subject.Contains(':') &&
        subject.Split('/', StringSplitOptions.None).All(segment => segment is not ("." or ".."));

    private static bool HasTraversalSegment(string path) =>
        path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.None).Any(segment => segment is "." or "..");

    private static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length + read > MaximumDocumentBytes)
                throw new MultiMemoryTransactionRecordException("Coordinator record exceeds its size bound.", MultiMemoryFailure.Malformed);
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }
}

internal sealed class MultiMemoryTransactionRecordException : Exception
{
    internal MultiMemoryTransactionRecordException(string message, MultiMemoryFailure failure, Exception? inner = null,
        string? transactionId = null, string? expectedScope = null)
        : base(message, inner)
    {
        Failure = failure;
        TransactionId = transactionId;
        Scope = expectedScope;
    }

    internal MultiMemoryFailure Failure { get; }
    internal string? TransactionId { get; }
    internal string? Scope { get; }

    internal MultiMemoryTransactionRecordException WithScope(string scope) =>
        new(Message, Failure, InnerException, TransactionId, scope);
}
