namespace Scrinia.Core;

/// <summary>
/// Pure static naming utilities for memory scopes and ephemeral prefixes.
/// Extracted from <see cref="FileMemoryStore"/> so they can be used without
/// depending on a concrete store implementation.
/// </summary>
public static class MemoryNaming
{
    /// <summary>
    /// Topic names classified as "entity" (structural). Currently only "skill"
    /// is treated as an entity topic so legacy NMP/2 skill data under
    /// entity/skill/ remains discoverable. Everything else lives under memory/.
    /// </summary>
    public static readonly IReadOnlySet<string> EntityTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "skill"
    };

    private static readonly HashSet<string> AgentTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent"
    };

    /// <summary>
    /// Namespace directory names used in the scoped topic layout.
    /// Useful for deduplication when discovering topics on disk.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedNamespaceDirs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "entity", "memory", "agent" };

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
    /// Classifies a topic name into one of three namespaces:
    /// "entity" for planning/structural topics, "agent" for agent-related topics,
    /// or "memory" for everything else.
    /// </summary>
    public static string ClassifyTopic(string topic)
    {
        if (EntityTopics.Contains(topic)) return "entity";
        if (AgentTopics.Contains(topic)) return "agent";
        return "memory";
    }

    /// <summary>
    /// Builds the full internal scope string used by the storage layer. This must
    /// match how a "/topic/subject" path resolves, so ordinary topics are flat —
    /// only skill (entity) and agent keep a namespace:
    /// Entity topics → "local:entity/{topic}",
    /// Agent topic  → "local:agent",
    /// Ordinary topics → "local:{topic}".
    /// (The legacy "local:memory/{topic}" layout is still read via fallback.)
    /// </summary>
    public static string BuildScopedTopicScope(string topic)
    {
        var ns = ClassifyTopic(topic);
        return ns switch
        {
            "agent" => "local:agent",
            "entity" => $"local:entity/{topic}",
            _ => $"local:{topic}"
        };
    }

    /// <summary>
    /// Strips the namespace prefix from an internal topic directory part.
    /// "entity/task" → "task", "memory/api" → "api", "agent" → "agent".
    /// Passes through unchanged if no recognised namespace prefix is present.
    /// </summary>
    public static string StripNamespacePrefix(string topicPart)
    {
        var slashIndex = topicPart.IndexOf('/');
        if (slashIndex >= 0)
        {
            var prefix = topicPart[..slashIndex];
            if (ReservedNamespaceDirs.Contains(prefix))
                return topicPart[(slashIndex + 1)..];
        }
        return topicPart;
    }

    /// <summary>
    /// Returns a human-friendly display label for an internal scope string.
    /// "local" → "local", "local:entity/task" → "task",
    /// "local:memory/api" → "api", "local:agent" → "agent",
    /// "ephemeral" → "ephemeral".
    /// </summary>
    public static string FormatScopeLabel(string scope)
    {
        if (scope == "local") return "local";
        if (scope == "ephemeral") return "ephemeral";
        if (scope.StartsWith("local:", StringComparison.Ordinal))
            return StripNamespacePrefix(scope["local:".Length..]);
        return scope;
    }
}
