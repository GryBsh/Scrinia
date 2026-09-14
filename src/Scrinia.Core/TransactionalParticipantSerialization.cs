using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core;

internal sealed record B4ParticipantRecoveryState
{
    [JsonConstructor]
    internal B4ParticipantRecoveryState(string transactionId, string operationId, string scope, string address,
        string participantIdentity, int contractVersion, string orderingKey, string kind, IReadOnlyList<string> capabilities,
        IReadOnlyDictionary<string, string> payload, IReadOnlyDictionary<string, string> metadata,
        TransactionalParticipantLifecycleState lifecycleState, string? preparedHandleReference, string? preparedHandleVersion,
        int retryAttempt, long retryElapsedMilliseconds, bool stateMayHaveChanged, bool ambiguous,
        TransactionalFailureCode? lastFailureCode, string? evidenceReference, string? evidenceKind, string? evidenceVersion,
        TransactionalQuarantineState quarantineState, string? quarantineReason)
    {
        TransactionId = B4Validation.Text(transactionId, nameof(transactionId));
        OperationId = B4Validation.Text(operationId, nameof(operationId));
        Scope = B4Validation.Text(scope, nameof(scope));
        if (!Models.MemoryAddress.TryParse(address, out var parsed, out _) || parsed.Kind != Models.MemoryAddressKind.Persistent)
            throw new ArgumentException("A persistent address is required.", nameof(address));
        Address = parsed.ToString();
        ParticipantIdentity = B4Validation.Text(participantIdentity, nameof(participantIdentity));
        if (contractVersion != 1) throw new ArgumentException("Contract version must be 1.", nameof(contractVersion));
        ContractVersion = contractVersion;
        OrderingKey = B4Validation.OrderingKey(orderingKey);
        Kind = B4Validation.Text(kind, nameof(kind));
        Capabilities = B4Validation.Capabilities(capabilities);
        Payload = B4Validation.Dictionary(payload, 4096, nameof(payload));
        Metadata = B4Validation.Dictionary(metadata, 512, nameof(metadata));
        if (!Enum.IsDefined(lifecycleState)) throw new ArgumentException("Lifecycle state is unsupported.", nameof(lifecycleState));
        if (retryAttempt is < 0 or > 8 || retryElapsedMilliseconds is < 0 or > 300000)
            throw new ArgumentException("Retry metadata is outside its bounds.");
        if ((preparedHandleReference is null) != (preparedHandleVersion is null) ||
            (preparedHandleReference is not null && (!B4Validation.SafeReference(preparedHandleReference) || !B4Validation.SafeReference(preparedHandleVersion!))))
            throw new ArgumentException("Prepared handle must be a safe complete pair.");
        if (evidenceReference is null != (evidenceKind is null) || evidenceReference is null != (evidenceVersion is null) ||
            evidenceReference is not null && !B4Validation.SafeReference(evidenceReference))
            throw new ArgumentException("Evidence must be a safe complete tuple.");
        if (!Enum.IsDefined(quarantineState) || quarantineState == TransactionalQuarantineState.None != (quarantineReason is null) ||
            quarantineReason is not null && !B4Validation.SafeReference(quarantineReason))
            throw new ArgumentException("Quarantine state is inconsistent.");
        if (lastFailureCode is not null && !Enum.IsDefined(lastFailureCode.Value)) throw new ArgumentException("Failure code is unsupported.");
        if (lifecycleState == TransactionalParticipantLifecycleState.Ambiguous && !ambiguous)
            throw new ArgumentException("Ambiguous lifecycle state requires the ambiguous flag.");
        PreparedHandleReference = preparedHandleReference; PreparedHandleVersion = preparedHandleVersion;
        LifecycleState = lifecycleState; RetryAttempt = retryAttempt; RetryElapsedMilliseconds = retryElapsedMilliseconds;
        StateMayHaveChanged = stateMayHaveChanged; Ambiguous = ambiguous; LastFailureCode = lastFailureCode;
        EvidenceReference = evidenceReference; EvidenceKind = evidenceKind; EvidenceVersion = evidenceVersion;
        QuarantineState = quarantineState; QuarantineReason = quarantineReason;
    }
    public string TransactionId { get; } public string OperationId { get; } public string Scope { get; } public string Address { get; }
    public string ParticipantIdentity { get; } public int ContractVersion { get; } public string OrderingKey { get; } public string Kind { get; }
    public IReadOnlyList<string> Capabilities { get; } public IReadOnlyDictionary<string, string> Payload { get; } public IReadOnlyDictionary<string, string> Metadata { get; }
    [JsonConverter(typeof(TransactionalParticipantLifecycleStateJsonConverter))] public TransactionalParticipantLifecycleState LifecycleState { get; }
    public string? PreparedHandleReference { get; } public string? PreparedHandleVersion { get; }
    public int RetryAttempt { get; } public long RetryElapsedMilliseconds { get; } public bool StateMayHaveChanged { get; } public bool Ambiguous { get; }
    [JsonConverter(typeof(TransactionalFailureCodeJsonConverter))] public TransactionalFailureCode? LastFailureCode { get; }
    public string? EvidenceReference { get; } public string? EvidenceKind { get; } public string? EvidenceVersion { get; }
    [JsonConverter(typeof(TransactionalQuarantineStateJsonConverter))] public TransactionalQuarantineState QuarantineState { get; }
    public string? QuarantineReason { get; }
}

internal sealed record B4ParticipantRehydrationRequest
{
    [JsonConstructor]
    internal B4ParticipantRehydrationRequest(B4ParticipantRecoveryState state, MultiMemoryParticipantManifest manifest, IReadOnlyList<string> capabilities)
    {
        State = state ?? throw new ArgumentNullException(nameof(state)); Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Capabilities = B4Validation.Capabilities(capabilities);
        if (!Capabilities.SequenceEqual(State.Capabilities, StringComparer.Ordinal))
            throw new ArgumentException("Rehydration capabilities do not match persisted state.");
        if (!string.Equals(State.Address, Manifest.Subject, StringComparison.Ordinal) || State.OperationId != Manifest.OperationId || State.Kind != Manifest.Kind)
            throw new ArgumentException("Manifest does not match persisted participant state.");
    }
    public B4ParticipantRecoveryState State { get; } public MultiMemoryParticipantManifest Manifest { get; } public IReadOnlyList<string> Capabilities { get; }
}

internal sealed record B4ParticipantRequestMetadata
{
    [JsonConstructor]
    internal B4ParticipantRequestMetadata(string transactionId, string scope, string address, string participantIdentity, int contractVersion,
        string orderingKey, string operationId, string kind, TransactionalFailureCode rejectionCode, string rejectionDetail)
    {
        TransactionId = B4Validation.Text(transactionId, nameof(transactionId)); Scope = B4Validation.Text(scope, nameof(scope));
        if (!Models.MemoryAddress.TryParse(address, out var parsed, out _) || parsed.Kind != Models.MemoryAddressKind.Persistent) throw new ArgumentException("A persistent address is required.", nameof(address));
        Address = parsed.ToString(); ParticipantIdentity = B4Validation.Text(participantIdentity, nameof(participantIdentity));
        if (contractVersion != 1) throw new ArgumentException("Contract version must be 1.", nameof(contractVersion)); ContractVersion = contractVersion;
        OrderingKey = B4Validation.OrderingKey(orderingKey); OperationId = B4Validation.Text(operationId, nameof(operationId)); Kind = B4Validation.Text(kind, nameof(kind));
        if (!Enum.IsDefined(rejectionCode)) throw new ArgumentException("Failure code is unsupported.", nameof(rejectionCode));
        RejectionCode = rejectionCode; RejectionDetail = B4Validation.Text(rejectionDetail, nameof(rejectionDetail));
    }
    public string TransactionId { get; } public string Scope { get; } public string Address { get; } public string ParticipantIdentity { get; }
    public int ContractVersion { get; } public string OrderingKey { get; } public string OperationId { get; } public string Kind { get; }
    [JsonConverter(typeof(TransactionalFailureCodeJsonConverter))] public TransactionalFailureCode RejectionCode { get; } public string RejectionDetail { get; }
}

internal sealed class TransactionalParticipantLifecycleStateJsonConverter : JsonConverter<TransactionalParticipantLifecycleState>
{
    public override TransactionalParticipantLifecycleState Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Enum.TryParse<TransactionalParticipantLifecycleState>(reader.GetString(), true, out var value)
            ? value : throw new JsonException("Lifecycle state is unsupported.");
    public override void Write(Utf8JsonWriter writer, TransactionalParticipantLifecycleState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch { TransactionalParticipantLifecycleState.Prepared => "prepared", TransactionalParticipantLifecycleState.Committed => "committed", TransactionalParticipantLifecycleState.RolledBack => "rolledBack", TransactionalParticipantLifecycleState.Changed => "changed", TransactionalParticipantLifecycleState.Ambiguous => "ambiguous", _ => throw new JsonException("Lifecycle state is unsupported.") });
}

internal sealed class TransactionalQuarantineStateJsonConverter : JsonConverter<TransactionalQuarantineState>
{
    public override TransactionalQuarantineState Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Enum.TryParse<TransactionalQuarantineState>(reader.GetString(), true, out var value)
            ? value : throw new JsonException("Quarantine state is unsupported.");
    public override void Write(Utf8JsonWriter writer, TransactionalQuarantineState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch { TransactionalQuarantineState.None => "none", TransactionalQuarantineState.Quarantined => "quarantined", _ => throw new JsonException("Quarantine state is unsupported.") });
}

internal sealed class TransactionalFailureCodeJsonConverter : JsonConverter<TransactionalFailureCode>
{
    public override TransactionalFailureCode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Enum.TryParse<TransactionalFailureCode>(reader.GetString(), true, out var value)
            ? value : throw new JsonException("Failure code is unsupported.");
    public override void Write(Utf8JsonWriter writer, TransactionalFailureCode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..]);
}
