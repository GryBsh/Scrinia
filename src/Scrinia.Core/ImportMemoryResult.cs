using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Minimal per-memory import result adopted at the result boundary so import can map through
/// <see cref="B0Result"/> the same way remember and compact do. Reuses the existing
/// <see cref="WholeMemoryOutcome"/> vocabulary. Internal Core result only — carried at the result
/// boundary, NEVER written to disk; it is not a durable format, a bundle member, a sidecar, or a
/// JSON-serialized outcome.</summary>
internal sealed record ImportMemoryResult(
    WholeMemoryOutcome Outcome,
    ArtifactEntry? Entry,
    string OperationId,
    string? Warning,
    RecoveryInfo? Recovery);
