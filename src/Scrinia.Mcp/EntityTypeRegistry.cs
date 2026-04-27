using System.Collections.Frozen;

namespace Scrinia.Mcp;

// ── Entity-type metadata ────────────────────────────────────────────────────

/// <summary>Defines the valid states, transitions, and parameter mappings for an entity type.</summary>
public record EntityTypeDefinition
{
    public required string TypeName { get; init; }
    public required IReadOnlySet<string> ValidStates { get; init; }
    public required IReadOnlyList<TransitionDefinition> Transitions { get; init; }

    /// <summary>Maps legacy/alias parameter names to their canonical names (old → new).</summary>
    public required IReadOnlyDictionary<string, string> ParameterMappings { get; init; }

    public string? DefaultState { get; init; }
}

/// <summary>Defines a single allowed state transition for an entity type.</summary>
public record TransitionDefinition
{
    /// <summary>Source state. Use <c>"*"</c> to match any current state.</summary>
    public required string FromState { get; init; }

    public required string ToState { get; init; }
    public required IReadOnlySet<string> RequiredParameters { get; init; }
    public IReadOnlySet<string>? OptionalParameters { get; init; }
}

// ── Registry ────────────────────────────────────────────────────────────────

/// <summary>
/// Static registry of per-type metadata for all supported entity types.
/// Adding a new entity type = adding one dictionary entry.
/// </summary>
public static class EntityTypeRegistry
{
    public static readonly IReadOnlyDictionary<string, EntityTypeDefinition> Types =
        new Dictionary<string, EntityTypeDefinition>
        {
            ["goal"] = new EntityTypeDefinition
            {
                TypeName = "goal",
                ValidStates = FrozenSet("active", "complete"),
                DefaultState = "active",
                Transitions =
                [
                    new TransitionDefinition
                    {
                        FromState = "*",
                        ToState = "active",
                        RequiredParameters = FrozenSet("description"),
                        OptionalParameters = FrozenSet("workflowRef"),
                    },
                    new TransitionDefinition
                    {
                        FromState = "active",
                        ToState = "complete",
                        RequiredParameters = FrozenSet("outcome"),
                        OptionalParameters = FrozenSet("goalId"),
                    },
                ],
                ParameterMappings = new Dictionary<string, string>
                {
                    ["goalId"] = "id",
                }.ToFrozenDictionary(),
            },

            ["concern"] = new EntityTypeDefinition
            {
                TypeName = "concern",
                ValidStates = FrozenSet("active", "resolved"),
                DefaultState = "active",
                Transitions =
                [
                    new TransitionDefinition
                    {
                        FromState = "*",
                        ToState = "active",
                        RequiredParameters = FrozenSet("description", "severity"),
                        OptionalParameters = FrozenSet("phaseScope", "id"),
                    },
                    new TransitionDefinition
                    {
                        FromState = "active",
                        ToState = "resolved",
                        RequiredParameters = FrozenSet("id", "resolution", "verifiedBy"),
                    },
                ],
                ParameterMappings = new Dictionary<string, string>
                {
                    ["concernName"] = "id",
                    ["phaseScope"] = "phase",
                }.ToFrozenDictionary(),
            },

            ["requirement"] = new EntityTypeDefinition
            {
                TypeName = "requirement",
                ValidStates = FrozenSet("pending", "fulfilled"),
                DefaultState = "pending",
                Transitions =
                [
                    new TransitionDefinition
                    {
                        FromState = "*",
                        ToState = "pending",
                        RequiredParameters = FrozenSet("requirements"),
                    },
                    new TransitionDefinition
                    {
                        FromState = "pending",
                        ToState = "fulfilled",
                        RequiredParameters = FrozenSet("id", "evidence"),
                    },
                ],
                ParameterMappings = new Dictionary<string, string>().ToFrozenDictionary(),
            },

            ["project"] = new EntityTypeDefinition
            {
                TypeName = "project",
                ValidStates = FrozenSet("initialized", "active"),
                DefaultState = "initialized",
                Transitions =
                [
                    new TransitionDefinition
                    {
                        FromState = "*",
                        ToState = "initialized",
                        RequiredParameters = FrozenSet("description"),
                    },
                ],
                ParameterMappings = new Dictionary<string, string>
                {
                    ["context"] = "description",
                }.ToFrozenDictionary(),
            },

            ["workflow"] = new EntityTypeDefinition
            {
                TypeName = "workflow",
                ValidStates = FrozenSet("defined"),
                DefaultState = "defined",
                Transitions =
                [
                    new TransitionDefinition
                    {
                        FromState = "*",
                        ToState = "defined",
                        RequiredParameters = FrozenSet("definition"),
                    },
                ],
                ParameterMappings = new Dictionary<string, string>().ToFrozenDictionary(),
            },

            ["file"] = new EntityTypeDefinition
            {
                TypeName = "file",
                ValidStates = FrozenSet<string>.Empty,
                Transitions = [],
                ParameterMappings = FrozenDictionary<string, string>.Empty,
                DefaultState = null
            },

            ["phase"] = new EntityTypeDefinition
            {
                TypeName = "phase",
                ValidStates = FrozenSet("active", "complete"),
                Transitions =
                [
                    new TransitionDefinition
                    {
                        FromState = "*",
                        ToState = "active",
                        RequiredParameters = FrozenSet("phaseId"),
                    }
                ],
                ParameterMappings = FrozenDictionary<string, string>.Empty,
                DefaultState = "active"
            },

            ["task"] = new EntityTypeDefinition
            {
                TypeName = "task",
                ValidStates = FrozenSet("pending", "active", "complete", "skipped"),
                Transitions =
                [
                    new TransitionDefinition
                    {
                        FromState = "*",
                        ToState = "pending",
                        RequiredParameters = FrozenSet("description"),
                    }
                ],
                ParameterMappings = FrozenDictionary<string, string>.Empty,
                DefaultState = "pending"
            },
        }.ToFrozenDictionary();

    /// <summary>Looks up a type definition by name (case-insensitive).</summary>
    public static EntityTypeDefinition? Get(string typeName) =>
        Types.GetValueOrDefault(typeName.ToLowerInvariant());

    /// <summary>Returns whether the given type name is a built-in registered type.</summary>
    public static bool IsValidType(string typeName) =>
        Types.ContainsKey(typeName.ToLowerInvariant());

    /// <summary>
    /// Returns built-in types merged with any user-defined entity types from
    /// <c>.scrinia/entities/*.yaml</c>. Built-in types always win on name conflicts.
    /// </summary>
    public static IReadOnlyDictionary<string, EntityTypeDefinition> GetMergedTypes(string? scriniaBaseDir)
    {
        if (scriniaBaseDir is null) return Types;
        var userTypes = UserEntityLoader.LoadUserDefinedTypes(scriniaBaseDir);
        if (userTypes.Count == 0) return Types;
        var merged = new Dictionary<string, EntityTypeDefinition>(Types, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, def) in userTypes)
            merged.TryAdd(name, def); // built-in wins on conflict
        return merged;
    }

    /// <summary>
    /// Checks whether the given type name is valid in the merged registry (built-in + user-defined).
    /// </summary>
    public static bool IsValidMergedType(string typeName, string? scriniaBaseDir) =>
        GetMergedTypes(scriniaBaseDir).ContainsKey(typeName.ToLowerInvariant());

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static FrozenSet<string> FrozenSet(params string[] values) =>
        values.ToFrozenSet(StringComparer.Ordinal);
}
