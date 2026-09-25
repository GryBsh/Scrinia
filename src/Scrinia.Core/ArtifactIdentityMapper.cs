using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// Owns canonical physical-identity derivation for a scope: the portable artifact key, its shape
/// validation, and the entry re-keying that keeps the physical leaf decoupled from the qualified
/// logical address. Pure, side-effect-free transforms — no disk, lock, fault injection, recovery,
/// or cache state. Moved off <see cref="FileMemoryStore"/> in P18.
/// </summary>
internal static class ArtifactIdentityMapper
{
    public static string BuildArtifactKey(string subject, string scope)
    {
        string topic = scope.Equals("local", StringComparison.Ordinal)
            ? ""
            : scope.Equals("ephemeral", StringComparison.Ordinal)
                ? "ephemeral/"
                : scope.StartsWith("local:", StringComparison.Ordinal)
                    ? scope["local:".Length..] + "/"
                    : throw new ArgumentException($"Unknown scope: {scope}", nameof(scope));
        return $"artifacts/{topic}{subject}.nmp2";
    }

    public static ArtifactEntry CanonicalizeEntryForScope(ArtifactEntry entry, string scope)
    {
        if (scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
            return entry;

        string subject = entry.Name;
        if (string.IsNullOrWhiteSpace(subject))
            subject = entry.Address.IsValid && entry.Address.Kind == MemoryAddressKind.Persistent
                ? entry.Address.Value.Trim('/').Split('/')[^1]
                : entry.Name;
        else if (subject.Contains('/'))
            subject = subject.Split('/')[^1];
        string address = scope.Equals("local", StringComparison.Ordinal)
            ? $"/{subject}"
            : MemoryNaming.FormatQualifiedName(scope, subject);
        return entry with
        {
            Address = MemoryAddress.Parse(address),
            ArtifactKey = BuildArtifactKey(subject, scope),
            Name = subject,
        };
    }

    public static void ValidateArtifactKey(string artifactKey)
    {
        if (string.IsNullOrWhiteSpace(artifactKey) ||
            artifactKey.Contains('\\') ||
            artifactKey.Contains(':') ||
            artifactKey.StartsWith('/') ||
            !artifactKey.StartsWith("artifacts/", StringComparison.Ordinal) ||
            Uri.TryCreate(artifactKey, UriKind.Absolute, out _))
            throw new ArgumentException("ArtifactKey must be a portable store-relative key.", nameof(artifactKey));

        string relative = artifactKey["artifacts/".Length..];
        string[] segments = relative.Split('/');
        if (segments.Length == 0 || segments.Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("ArtifactKey must not contain traversal segments.", nameof(artifactKey));
    }
}
