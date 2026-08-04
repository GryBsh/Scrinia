namespace Scrinia.Core;

/// <summary>
/// Pure static naming utilities for memory scopes and ephemeral prefixes.
/// Extracted from <see cref="FileMemoryStore"/> so they can be used without
/// depending on a concrete store implementation.
/// </summary>
public static class MemoryNaming
{
    /// <summary>
    /// Throws if <paramref name="name"/> uses the removed v1 "topic:subject" colon
    /// syntax. Memory names are paths now ("/topic/subject"); rejecting a bare colon
    /// (rather than letting <c>SanitizeName</c> silently fold it into a mangled local
    /// name) forces callers to migrate and surfaces the exact path to use.
    /// </summary>
    public static void RejectLegacyColonSyntax(string name)
    {
        int colon = name.IndexOf(':');
        if (colon < 0) return;

        string topic = name[..colon].Trim();
        string subject = name[(colon + 1)..].Trim();
        string suggestion = topic.Length > 0 && subject.Length > 0
            ? $"/{topic}/{subject}"
            : "/topic/subject";
        throw new ArgumentException(
            $"Memory names use path syntax now — '{name}' should be '{suggestion}'. " +
            "The 'topic:subject' form was removed.",
            nameof(name));
    }

    /// <summary>
    /// Strips the leading '~' from an ephemeral name.
    /// Returns the input unchanged if it does not start with '~'.
    /// </summary>
    public static string StripEphemeralPrefix(string name) =>
        name.Length > 0 && name[0] == '~' ? name[1..] : name;

    /// <summary>
    /// Builds the full internal scope string used by the storage layer. Every topic — including
    /// the former "entity" (skill) and "agent" namespaces — is flat under the v3 layout:
    /// "local:{topic}". ("local:agent" happens to coincide with the old agent-namespace scope,
    /// since "agent" has no child topic to flatten away.)
    ///
    /// <para>Flattened as part of the v3 cutover (Task 7): once <see cref="PathRouter.ToStoreDir"/>
    /// routes every scope to <c>.scrinia/memories/</c>, a lingering "entity/skill" segment would
    /// resolve to <c>memories/entity/skill/</c> — a location <see cref="StoreLayoutMigration"/>
    /// never writes to (it moves entity/skill/ content to <c>memories/skill/</c>). Keeping this
    /// namespaced would silently orphan every skill memory (and, via
    /// <c>StoreLayoutMigration.MoveRenamedVectorDirs</c>, its vectors too).</para>
    /// </summary>
    public static string BuildScopedTopicScope(string topic) => $"local:{topic}";

    /// <summary>
    /// Returns a human-friendly display label for an internal scope string.
    /// "local" → "local", "local:api" → "api", "local:agent" → "agent",
    /// "ephemeral" → "ephemeral". v3: every topic part is already flat, so there is no
    /// namespace prefix left to strip (see Task 8, which removed the v1/v2 fallback this
    /// used to interoperate with).
    /// </summary>
    public static string FormatScopeLabel(string scope)
    {
        if (scope == "local") return "local";
        if (scope == "ephemeral") return "ephemeral";
        if (scope.StartsWith("local:", StringComparison.Ordinal))
            return scope["local:".Length..];
        return scope;
    }
}
