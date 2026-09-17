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
        DateTimeOffset updatedAt,
        IReadOnlyList<MultiMemoryParticipantCompletion>? completedParticipants = null,
        IReadOnlyList<B4ParticipantRecoveryState>? transactionalParticipants = null,
        IReadOnlyList<B4ParticipantRequestMetadata>? rejectedTransactionalParticipants = null)
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
        if (completedParticipants is not null && completedParticipants.Any(completion => completion is null))
            throw new ArgumentException("Completed participants cannot contain null values.", nameof(completedParticipants));
        CompletedParticipants = completedParticipants is null
            ? null
            : Array.AsReadOnly(completedParticipants.ToArray());
        if (transactionalParticipants is not null && transactionalParticipants.Any(participant => participant is null))
            throw new ArgumentException("Transactional participants cannot contain null values.", nameof(transactionalParticipants));
        if (rejectedTransactionalParticipants is not null && rejectedTransactionalParticipants.Any(participant => participant is null))
            throw new ArgumentException("Rejected transactional participants cannot contain null values.", nameof(rejectedTransactionalParticipants));
        TransactionalParticipants = transactionalParticipants is null ? null : Array.AsReadOnly(transactionalParticipants.ToArray());
        RejectedTransactionalParticipants = rejectedTransactionalParticipants is null ? null : Array.AsReadOnly(rejectedTransactionalParticipants.ToArray());
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MultiMemoryParticipantCompletion>? CompletedParticipants { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<B4ParticipantRecoveryState>? TransactionalParticipants { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<B4ParticipantRequestMetadata>? RejectedTransactionalParticipants { get; }
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

internal sealed record MultiMemoryParticipantCompletion
{
    public MultiMemoryParticipantCompletion(string subject, string operationId, string kind, string evidencePath)
    {
        Subject = RequireText(subject, nameof(subject));
        OperationId = RequireText(operationId, nameof(operationId));
        Kind = RequireText(kind, nameof(kind));
        if (!MultiMemoryParticipantOutcome.IsSafeEvidencePath(evidencePath))
            throw new ArgumentException("Completion evidence path must be safe.", nameof(evidencePath));
        EvidencePath = evidencePath;
    }

    public string Subject { get; }
    public string OperationId { get; }
    public string Kind { get; }
    public string EvidencePath { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
            : value;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MultiMemoryTransactionRecord))]
[JsonSerializable(typeof(MultiMemoryParticipantManifest))]
[JsonSerializable(typeof(MultiMemoryParticipantCompletion))]
[JsonSerializable(typeof(MultiMemoryTransactionState))]
[JsonSerializable(typeof(MultiMemoryParticipantManifest[]))]
[JsonSerializable(typeof(MultiMemoryParticipantCompletion[]))]
[JsonSerializable(typeof(B4ParticipantRecoveryState))]
[JsonSerializable(typeof(B4ParticipantRecoveryState[]))]
[JsonSerializable(typeof(B4ParticipantRequestMetadata))]
[JsonSerializable(typeof(B4ParticipantRequestMetadata[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(TransactionalParticipantLifecycleState))]
[JsonSerializable(typeof(TransactionalQuarantineState))]
[JsonSerializable(typeof(TransactionalFailureCode))]
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

    internal static MultiMemoryTransactionRecord ParseVerifiedRecord(
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
            record.Scope, state, record.Participants, record.CreatedAt, updatedAt, record.CompletedParticipants,
            record.TransactionalParticipants, record.RejectedTransactionalParticipants);
    }

    internal static MultiMemoryTransactionRecord WithCompletionProof(
        MultiMemoryTransactionRecord record, IReadOnlyList<MultiMemoryParticipantCompletion> completions,
        DateTimeOffset updatedAt) =>
        new(record.Format, record.Version, record.TransactionId, record.Scope, record.State,
            record.Participants, record.CreatedAt, updatedAt, completions,
            record.TransactionalParticipants, record.RejectedTransactionalParticipants);

    internal static string GetPath(string scopeDirectory)
    {
        if (string.IsNullOrWhiteSpace(scopeDirectory))
            throw new ArgumentException("Scope directory is required.", nameof(scopeDirectory));
        string root = PathContainment.ResolveCanonical(scopeDirectory);
        return Path.Combine(root, FileName);
    }

    internal static MultiMemoryTransactionRecord WithTransactionalParticipant(
        MultiMemoryTransactionRecord record, B4ParticipantRecoveryState participant, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(participant);
        var participants = (record.TransactionalParticipants ?? Array.Empty<B4ParticipantRecoveryState>()).ToArray();
        int index = Array.FindIndex(participants, item => item.ParticipantIdentity == participant.ParticipantIdentity);
        if (index < 0) throw new ArgumentException("Participant is not part of the record.", nameof(participant));
        participants[index] = participant;
        return new(record.Format, record.Version, record.TransactionId, record.Scope, record.State,
            record.Participants, record.CreatedAt, updatedAt, record.CompletedParticipants, participants,
            record.RejectedTransactionalParticipants);
    }

    internal static MultiMemoryTransactionRecord WithTransactionalParticipants(
        MultiMemoryTransactionRecord record, IReadOnlyList<B4ParticipantRecoveryState> participants, DateTimeOffset updatedAt) =>
        new(record.Format, record.Version, record.TransactionId, record.Scope, record.State,
            record.Participants, record.CreatedAt, updatedAt, record.CompletedParticipants, participants,
            record.RejectedTransactionalParticipants);

    internal static void Write(string scopeDirectory, MultiMemoryTransactionRecord record)
    {
        string path = GetPath(scopeDirectory);
        DurableFile.AtomicWriteAllBytes(path, Serialize(record));
    }

    internal static void WriteExpected(string scopeDirectory, MultiMemoryTransactionRecord expected,
        MultiMemoryTransactionRecord replacement)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Version != expected.Version || replacement.TransactionId != expected.TransactionId ||
            replacement.Scope != expected.Scope || !SameParticipants(replacement.Participants, expected.Participants) ||
            !SameAdmittedSnapshots(replacement.TransactionalParticipants, expected.TransactionalParticipants) ||
            !SameRejectedSnapshots(replacement.RejectedTransactionalParticipants, expected.RejectedTransactionalParticipants))
            throw new MultiMemoryTransactionRecordException("Replacement changes coordinator identity or participant snapshot.",
                MultiMemoryFailure.Inconsistent, transactionId: expected.TransactionId, expectedScope: expected.Scope);
        string path = GetPath(scopeDirectory);
        MultiMemoryTransactionRecord current = Read(scopeDirectory, expected.Scope);
        if (!Serialize(current).AsSpan().SequenceEqual(Serialize(expected)))
            throw new MultiMemoryTransactionRecordException("Coordinator record changed before replacement.", MultiMemoryFailure.Inconsistent,
                transactionId: expected.TransactionId, expectedScope: expected.Scope);
        DurableFile.AtomicWriteAllBytes(path, Serialize(replacement));
    }

    internal static MultiMemoryTransactionRecord Read(string scopeDirectory, string expectedScope,
        CancellationToken cancellationToken = default)
    {
        string path;
        try
        {
            path = GetPath(scopeDirectory);
            return ParseVerifiedRecord(ReadVerified(path, expectedScope, scopeDirectory, cancellationToken), expectedScope, path, scopeDirectory);
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
        int transactionalCount = record.TransactionalParticipants?.Count ?? 0;
        int rejectedCount = record.RejectedTransactionalParticipants?.Count ?? 0;
        if (transactionalCount + record.Participants.Count > MaximumParticipants || rejectedCount > MaximumParticipants)
            throw new MultiMemoryTransactionRecordException("Transactional participant count is outside its bounds.", MultiMemoryFailure.Malformed);
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

        if (record.CompletedParticipants is not null)
        {
            var completed = new HashSet<(string Subject, string OperationId)>();
            foreach (MultiMemoryParticipantCompletion completion in record.CompletedParticipants)
            {
                RequireBounded(completion.Subject, nameof(completion.Subject));
                RequireBounded(completion.OperationId, nameof(completion.OperationId));
                RequireBounded(completion.Kind, nameof(completion.Kind));
                RequireBounded(completion.EvidencePath, nameof(completion.EvidencePath));
                if (!IsSafeSubject(completion.EvidencePath) || !completed.Add((completion.Subject, completion.OperationId)) ||
                    !record.Participants.Any(participant => participant.Subject == completion.Subject &&
                        participant.OperationId == completion.OperationId && participant.Kind == completion.Kind))
                    throw new MultiMemoryTransactionRecordException("Coordinator completion proof is inconsistent.", MultiMemoryFailure.Inconsistent);
            }
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var orderingKeys = new HashSet<string>(StringComparer.Ordinal);
        string? previousOrderingKey = null;
        foreach (var participant in record.TransactionalParticipants ?? Array.Empty<B4ParticipantRecoveryState>())
        {
            if (!identities.Add(participant.ParticipantIdentity) || !orderingKeys.Add(participant.OrderingKey) ||
                previousOrderingKey is not null && string.CompareOrdinal(previousOrderingKey, participant.OrderingKey) >= 0 ||
                !string.Equals(participant.TransactionId, record.TransactionId, StringComparison.Ordinal) ||
                !string.Equals(participant.Scope, record.Scope, StringComparison.Ordinal))
                throw new MultiMemoryTransactionRecordException("Transactional participant snapshot is inconsistent.", MultiMemoryFailure.Inconsistent);
            previousOrderingKey = participant.OrderingKey;
        }
        previousOrderingKey = null;
        foreach (var participant in record.RejectedTransactionalParticipants ?? Array.Empty<B4ParticipantRequestMetadata>())
        {
            if (!identities.Add(participant.ParticipantIdentity) || !orderingKeys.Add(participant.OrderingKey) ||
                previousOrderingKey is not null && string.CompareOrdinal(previousOrderingKey, participant.OrderingKey) >= 0 ||
                !string.Equals(participant.TransactionId, record.TransactionId, StringComparison.Ordinal) ||
                !string.Equals(participant.Scope, record.Scope, StringComparison.Ordinal))
                throw new MultiMemoryTransactionRecordException("Rejected transactional participant metadata is inconsistent.", MultiMemoryFailure.Inconsistent);
            previousOrderingKey = participant.OrderingKey;
        }

        if (recordPath is not null && expectedStoreDirectory is not null &&
            !PathContainment.IsWithin(expectedStoreDirectory, recordPath))
            throw new MultiMemoryTransactionRecordException("Coordinator record path is outside its scope.", MultiMemoryFailure.Unsafe);
    }

    private static bool SameParticipants(IReadOnlyList<MultiMemoryParticipantManifest> left, IReadOnlyList<MultiMemoryParticipantManifest> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Subject == pair.Second.Subject &&
            pair.First.OperationId == pair.Second.OperationId && pair.First.Kind == pair.Second.Kind);

    private static bool SameAdmittedSnapshots(IReadOnlyList<B4ParticipantRecoveryState>? left,
        IReadOnlyList<B4ParticipantRecoveryState>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.TransactionId == pair.Second.TransactionId && pair.First.OperationId == pair.Second.OperationId &&
            pair.First.Scope == pair.Second.Scope && pair.First.Address == pair.Second.Address &&
            pair.First.ParticipantIdentity == pair.Second.ParticipantIdentity &&
            pair.First.ContractVersion == pair.Second.ContractVersion && pair.First.OrderingKey == pair.Second.OrderingKey &&
            pair.First.Kind == pair.Second.Kind && pair.First.Capabilities.SequenceEqual(pair.Second.Capabilities, StringComparer.Ordinal) &&
            SameDictionary(pair.First.Payload, pair.Second.Payload) && SameDictionary(pair.First.Metadata, pair.Second.Metadata));
    }

    private static bool SameRejectedSnapshots(IReadOnlyList<B4ParticipantRequestMetadata>? left,
        IReadOnlyList<B4ParticipantRequestMetadata>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.TransactionId == pair.Second.TransactionId && pair.First.Scope == pair.Second.Scope &&
            pair.First.Address == pair.Second.Address && pair.First.ParticipantIdentity == pair.Second.ParticipantIdentity &&
            pair.First.ContractVersion == pair.Second.ContractVersion && pair.First.OrderingKey == pair.Second.OrderingKey &&
            pair.First.OperationId == pair.Second.OperationId && pair.First.Kind == pair.Second.Kind &&
            pair.First.RejectionCode == pair.Second.RejectionCode && pair.First.RejectionDetail == pair.Second.RejectionDetail);
    }

    private static bool SameDictionary(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
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

    private static byte[] ReadVerified(string path, string expectedScope, string scopeDirectory,
        CancellationToken cancellationToken)
    {
        string relativeName = Path.GetRelativePath(scopeDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
        var verifier = new RecoveryEvidenceVerifier(new RecoveryScopeBinding(expectedScope, scopeDirectory));
        var request = new RecoveryVerificationRequest(
            new RecoveryScopeBinding(expectedScope, scopeDirectory), RecoveryEvidenceKind.MultiMemoryTransactionRecord,
            relativeName, null, default, null, MaintenanceLimits.Hard);
        RecoveryVerificationResult result = verifier.VerifyAsync(request, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        if (result.Outcome == RecoveryVerificationOutcome.ProtectedMissing)
            throw new FileNotFoundException("Coordinator record is missing.", path);
        if (result.Outcome != RecoveryVerificationOutcome.Verified || result.SourceContent is null)
            throw new MultiMemoryTransactionRecordException(
                result.Outcome == RecoveryVerificationOutcome.ProtectedOversized
                    ? "Coordinator record exceeds its size bound."
                    : "Coordinator record could not be verified.",
                result.Outcome == RecoveryVerificationOutcome.ProtectedOversized
                    ? MultiMemoryFailure.Malformed : MultiMemoryFailure.ReplayIoFailed);
        using (result.SourceContent)
            return result.SourceContent.Bytes.ToArray();
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
