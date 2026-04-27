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
    /// Builds the full internal scope string used by the storage layer.
    /// Entity topics → "local-topic:entity/{topic}",
    /// Agent topic  → "local-topic:agent",
    /// Memory topics → "local-topic:memory/{topic}".
    /// </summary>
    public static string BuildScopedTopicScope(string topic)
    {
        var ns = ClassifyTopic(topic);
        return ns switch
        {
            "agent" => "local-topic:agent",
            _ => $"local-topic:{ns}/{topic}"
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
    /// "local" → "local", "local-topic:entity/task" → "task",
    /// "local-topic:memory/api" → "api", "local-topic:agent" → "agent",
    /// "ephemeral" → "ephemeral".
    /// </summary>
    public static string FormatScopeLabel(string scope)
    {
        if (scope == "local") return "local";
        if (scope == "ephemeral") return "ephemeral";
        if (scope.StartsWith("local-topic:", StringComparison.Ordinal))
            return StripNamespacePrefix(scope["local-topic:".Length..]);
        return scope;
    }
}
