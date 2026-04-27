using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Mcp;

// ── User entity DTOs ────────────────────────────────────────────────────────

/// <summary>Represents a user-defined entity type loaded from YAML.</summary>
public sealed record UserEntityDefinition(
    string Name,
    string[] States,
    string? DefaultState,
    UserEntityTransition[]? Transitions
);

/// <summary>Represents a transition rule in a user-defined entity type.</summary>
public sealed record UserEntityTransition(
    string From,
    string To,
    string[]? Required,
    string? Prompt
);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserEntityDefinition))]
[JsonSerializable(typeof(UserEntityTransition[]))]
internal partial class UserEntityJsonContext : JsonSerializerContext { }

// ── Loader ──────────────────────────────────────────────────────────────────

/// <summary>
/// Loads user-defined entity type definitions from <c>.scrinia/entities/*.yaml</c> files.
/// Uses AOT-safe YAML-to-JSON-to-source-gen deserialization.
/// </summary>
public static class UserEntityLoader
{
    /// <summary>
    /// Scans <paramref name="scriniaBaseDir"/>/entities/ for YAML files and returns
    /// parsed <see cref="EntityTypeDefinition"/> instances keyed by type name.
    /// Skips files that conflict with built-in type names or fail to parse.
    /// </summary>
    public static Dictionary<string, EntityTypeDefinition> LoadUserDefinedTypes(string scriniaBaseDir)
    {
        var result = new Dictionary<string, EntityTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        string entitiesDir = Path.Combine(scriniaBaseDir, "entities");
        if (!Directory.Exists(entitiesDir)) return result;

        foreach (var file in Directory.EnumerateFiles(entitiesDir, "*.yaml")
            .Concat(Directory.EnumerateFiles(entitiesDir, "*.yml")))
        {
            try
            {
                string yaml = File.ReadAllText(file);

                // AOT-safe: YAML -> JSON -> source-gen deserialize
                var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
                var obj = deserializer.Deserialize<object>(yaml);
                var jsonSerializer = new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build();
                string json = jsonSerializer.Serialize(obj);

                var def = JsonSerializer.Deserialize(json, UserEntityJsonContext.Default.UserEntityDefinition);
                if (def is null || string.IsNullOrWhiteSpace(def.Name)) continue;

                // Skip if it conflicts with a built-in type name
                if (EntityTypeRegistry.IsValidType(def.Name)) continue;

                // Convert to EntityTypeDefinition
                var states = (def.States ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
                var transitions = (def.Transitions ?? []).Select(t => new TransitionDefinition
                {
                    FromState = t.From,
                    ToState = t.To,
                    RequiredParameters = (t.Required ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                }).ToArray();

                result[def.Name] = new EntityTypeDefinition
                {
                    TypeName = def.Name,
                    ValidStates = states,
                    Transitions = transitions,
                    ParameterMappings = FrozenDictionary<string, string>.Empty,
                    DefaultState = def.DefaultState
                };
            }
            catch
            {
                // Skip invalid files silently
            }
        }

        return result;
    }
}
