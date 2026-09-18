using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>The single typed member carrier B1-B6 read at the result boundary. Distinct from
/// RawMemberSnapshot (append/whole capture) and RecoveryMemberSnapshot (recovery body). NEVER
/// written to disk: it is not a durable format or recovery outcome.</summary>
internal sealed record RecoveryMember(
    MemoryAddress Address,
    string Scope,
    byte[] ArtifactBytes,
    string ArtifactSha256,
    string? SidecarPath,
    string? SidecarSha256);
