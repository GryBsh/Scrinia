using System.Reflection;

namespace Scrinia.Mcp;

/// <summary>
/// Loads embedded resource files (skills, scaffolds, workflows, guide) from the assembly.
/// Resource names use dots as path separators: Scrinia.Mcp.skills.qa.md
/// </summary>
public static class EmbeddedPrompts
{
    private static readonly Assembly _assembly = typeof(EmbeddedPrompts).Assembly;
    const string NamespacePrefix = $"{nameof(Scrinia)}.{nameof(Scrinia.Mcp)}";

    /// <summary>Load an embedded resource by relative path (e.g., "skills/qa.md").</summary>
    public static string? Load(string relativePath)
    {
        // Resource names use dots not slashes: Scrinia.Mcp.skills.qa.md
        // Hyphens in filenames become hyphens in resource names (no conversion needed).
        string resourceName = $"{NamespacePrefix}.{relativePath.Replace('/', '.').Replace('\\', '.')}";
        return LoadResource(resourceName);
    }

    private static string? LoadResource(string resourceName)
    {
        using var stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Get all built-in skill names and content (from embedded skills/*.md resources).</summary>
    public static IReadOnlyDictionary<string, string> LoadAllSkills()
    {
        var skills = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string prefix = $"{NamespacePrefix}.skills.";
        string suffix = ".md";
        foreach (var name in _assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) &&
                name.EndsWith(suffix, StringComparison.Ordinal))
            {
                // Extract skill name: "Scrinia.Mcp.skills.chaos-engineer.md" → "chaos-engineer"
                string skillName = name[prefix.Length..^suffix.Length];
                using var stream = _assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                skills[skillName] = reader.ReadToEnd();
            }
        }
        return skills;
    }

    /// <summary>Load a scaffold template by name (e.g., "researcher", "reviewer", "domain-expert").</summary>
    public static string? LoadScaffold(string name) =>
        Load($"prompts/scaffolds/{name}.md");

    /// <summary>Load the guide text.</summary>
    public static string? LoadGuide() => Load("prompts/guide.md");

    /// <summary>Get all built-in agent files (from embedded prompts/agent/*.md resources).</summary>
    public static IReadOnlyDictionary<string, string> LoadAllAgentFiles()
    {
        var agents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string prefix = $"{NamespacePrefix}.prompts.agent.";
        string suffix = ".md";
        foreach (var name in _assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) &&
                name.EndsWith(suffix, StringComparison.Ordinal))
            {
                string agentName = name[prefix.Length..^suffix.Length];
                using var stream = _assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                agents[agentName] = reader.ReadToEnd();
            }
        }
        return agents;
    }
}
