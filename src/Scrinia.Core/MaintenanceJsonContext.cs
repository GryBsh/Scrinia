using System.Text.Json.Serialization;

namespace Scrinia.Core;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MaintenanceRequest))]
[JsonSerializable(typeof(MaintenanceCandidate))]
[JsonSerializable(typeof(MaintenanceCandidateResult))]
[JsonSerializable(typeof(MaintenanceScopeResult))]
[JsonSerializable(typeof(MaintenanceResult))]
[JsonSerializable(typeof(MaintenanceContinuationPayload))]
[JsonSerializable(typeof(MaintenanceSnapshotCandidate))]
[JsonSerializable(typeof(MaintenanceCandidateKind))]
[JsonSerializable(typeof(MaintenanceProtectionReason))]
internal partial class MaintenanceJsonContext : JsonSerializerContext;
