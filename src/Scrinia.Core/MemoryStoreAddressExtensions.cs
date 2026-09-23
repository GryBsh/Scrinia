using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Internal migration helpers for code that still needs physical scope inspection.</summary>
internal static class MemoryStoreAddressExtensions
{
    private static MemoryAddress Address(string subject, string scope) =>
        MemoryAddress.Parse(scope.Equals("local", StringComparison.OrdinalIgnoreCase)
            ? "/" + subject
            : "/" + scope["local:".Length..] + "/" + subject);

    internal static bool TryUpdateEntry(this IMemoryStore store, string subject, string scope,
        Func<ArtifactEntry, ArtifactEntry?> mutate) =>
        store.TryUpdateEntry(Address(subject, scope), mutate);

    internal static bool Remove(this IMemoryStore store, string subject, string scope) =>
        store.Remove(Address(subject, scope));

    internal static void ArchiveVersion(this IMemoryStore store, string subject, string scope) =>
        store.ArchiveVersion(Address(subject, scope));

    internal static Task WriteArtifactAsync(this IMemoryStore store, string subject, string scope,
        string artifactText, CancellationToken ct = default) =>
        store.WriteArtifactAsync(Address(subject, scope), artifactText, ct);

    internal static void RememberEphemeral(this IMemoryStore store, string key, EphemeralEntry entry) =>
        store.RememberEphemeral(MemoryAddress.Parse("~" + key), entry);

    internal static bool ForgetEphemeral(this IMemoryStore store, string key) =>
        store.ForgetEphemeral(MemoryAddress.Parse("~" + key));

    internal static EphemeralEntry? GetEphemeral(this IMemoryStore store, string key) =>
        store.GetEphemeral(MemoryAddress.Parse("~" + key));

    internal static bool TryUpdateEphemeral(this IMemoryStore store, string key,
        Func<EphemeralEntry, EphemeralEntry?> mutate) =>
        store.TryUpdateEphemeral(MemoryAddress.Parse("~" + key), mutate);

    internal static string ArtifactPath(this IMemoryStore store, string subject, string scope = "local") =>
        store.FindArtifactPath(subject, scope);

    internal static (string Scope, string Subject) ParseQualifiedName(this IMemoryStore store, string name)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store is FileMemoryStore fileStore)
            return fileStore.ParseQualifiedName(name);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));
        if (!name.StartsWith('/'))
        {
            MemoryNaming.RejectLegacyColonSyntax(name);
            return ("local", store.SanitizeName(name.Trim()));
        }

        var parsed = PathParser.Parse(name, new HashSet<string>());
        if (parsed.Segments.Count == 0)
            throw new ArgumentException("Empty path", nameof(name));
        if (parsed.Segments.Count == 1)
            return ("local", MemoryNaming.SanitizeName(parsed.Segments[0].Value));

        string scope = MemoryNaming.BuildScopedTopicScope(
            parsed.Segments.Take(parsed.Segments.Count - 1).Select(segment => segment.Value));
        return (scope, MemoryNaming.SanitizeName(parsed.Segments[^1].Value));
    }
}
