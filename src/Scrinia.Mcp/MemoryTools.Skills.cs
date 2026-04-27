using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Mcp;

/// <summary>Metadata sidecar for a skill file on disk.</summary>
public record SkillFileMeta(
    string? BasedOn,
    string? Role,
    string[]? Capabilities,
    string? Scaffold,
    string? CreatedAt,
    string? UpdatedAt);

/// <summary>Metadata sidecar for an agent config file on disk.</summary>
public record AgentFileMeta(
    string? CreatedAt,
    string? UpdatedAt);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SkillFileMeta))]
[JsonSerializable(typeof(AgentFileMeta))]
public partial class ScriniaMcpJsonContext : JsonSerializerContext;

public sealed partial class ScriniaMcpTools
{
    private const int SkillResponseLimit = 8 * 1024;

    private static readonly Lazy<string> _researcherScaffold = new(() =>
        EmbeddedPrompts.LoadScaffold("researcher")
        ?? throw new InvalidOperationException("Built-in researcher scaffold not found"));

    private static readonly Lazy<string> _reviewerScaffold = new(() =>
        EmbeddedPrompts.LoadScaffold("reviewer")
        ?? throw new InvalidOperationException("Built-in reviewer scaffold not found"));

    private static readonly Lazy<string> _domainExpertScaffold = new(() =>
        EmbeddedPrompts.LoadScaffold("domain-expert")
        ?? throw new InvalidOperationException("Built-in domain-expert scaffold not found"));

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _builtInSkills =
        new(() => EmbeddedPrompts.LoadAllSkills());

    private static IReadOnlyDictionary<string, string> BuiltInSkills => _builtInSkills.Value;

    // ── Shared file helpers (used by skill, agent, and other markdown-on-disk paths) ─

    /// <summary>
    /// Resolves the .scrinia/ base directory by walking up from the local store directory.
    /// </summary>
    internal static string GetScriniaBaseDir(IMemoryStore store)
    {
        string storeDir = store.GetStoreDirForScope("local");
        var dir = new DirectoryInfo(storeDir);
        while (dir is not null && dir.Name != ".scrinia")
            dir = dir.Parent;
        return dir?.FullName ?? Path.GetDirectoryName(storeDir) ?? storeDir;
    }

    /// <summary>
    /// Archives an existing file into <paramref name="versionsDir"/> with a UTC
    /// timestamp suffix before it gets overwritten. No-op if the file does not exist.
    /// </summary>
    internal static void ArchiveFileVersion(string filePath, string versionsDir)
    {
        if (!File.Exists(filePath)) return;
        Directory.CreateDirectory(versionsDir);
        string name = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        File.Copy(filePath, Path.Combine(versionsDir, $"{name}_{timestamp}{ext}"));
    }

    /// <summary>Reads a JSON sidecar (.meta.json) next to <paramref name="filePath"/>. Null if missing or corrupted.</summary>
    internal static T? ReadSidecarMeta<T>(string filePath, JsonTypeInfo<T> typeInfo) where T : class
    {
        string metaPath = Path.ChangeExtension(filePath, ".meta.json");
        if (!File.Exists(metaPath)) return null;
        try
        {
            string json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes a JSON sidecar (.meta.json) next to <paramref name="filePath"/>.</summary>
    internal static void WriteSidecarMeta<T>(string filePath, T meta, JsonTypeInfo<T> typeInfo)
    {
        string metaPath = Path.ChangeExtension(filePath, ".meta.json");
        string json = JsonSerializer.Serialize(meta, typeInfo);
        File.WriteAllText(metaPath, json);
    }

    private static async Task<string> ReadLegacySkillNmpAsync(IMemoryStore store, string skillName, CancellationToken ct)
    {
        string artifact = await store.ResolveArtifactAsync($"skill:{skillName}", ct);
        byte[] decoded = Nmp2Strategy.Instance.Decode(artifact);
        return Encoding.UTF8.GetString(decoded);
    }

    /// <summary>Create a reusable specialist skill prompt and persist as .scrinia/skills/{name}.md.</summary>
    internal static async Task<string> SkillCreate(
        [Description("Skill name slug (e.g. 'api-reviewer', 'auth-researcher').")] string name,
        [Description("Built-in scaffold: researcher, reviewer, domain-expert, or custom.")] string scaffold,
        [Description("Additional context or instructions to embed in the prompt.")] string? instructions = null,
        [Description("Comma-separated tool names the agent should use (for custom scaffold).")] string? tools = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        string promptContent;
        string role;
        string scaffoldLower = scaffold.Trim().ToLowerInvariant();
        switch (scaffoldLower)
        {
            case "researcher":
                promptContent = _researcherScaffold.Value;
                role = "researcher";
                if (!string.IsNullOrWhiteSpace(instructions))
                    promptContent += $"\n## Additional Instructions\n{instructions}\n";
                break;

            case "reviewer":
                promptContent = _reviewerScaffold.Value;
                role = "reviewer";
                if (!string.IsNullOrWhiteSpace(instructions))
                    promptContent += $"\n## Additional Instructions\n{instructions}\n";
                break;

            case "domain-expert":
                promptContent = _domainExpertScaffold.Value;
                role = "domain-expert";
                if (!string.IsNullOrWhiteSpace(instructions))
                    promptContent += $"\n## Additional Instructions\n{instructions}\n";
                break;

            default:
                role = "custom";
                string toolSection = "";
                if (!string.IsNullOrWhiteSpace(tools))
                {
                    var toolList = tools
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => $"- {t}: use as needed");
                    toolSection =
                        "## Tools Available (if Scrinia MCP is active)\n" +
                        string.Join("\n", toolList) + "\n\n";
                }

                string instructionsSection = string.IsNullOrWhiteSpace(instructions)
                    ? "(no custom instructions provided)"
                    : instructions;

                promptContent =
                    "## Role: Custom Specialist\n" +
                    toolSection +
                    "## Instructions\n" +
                    $"{instructionsSection}\n\n" +
                    "## Fallback Instructions (if Scrinia MCP is not available)\n" +
                    "Organize findings in markdown. Use standard file operations to persist results.\n";
                break;
        }

        string? basedOnHash = null;
        if (BuiltInSkills.TryGetValue(name, out string? builtInText))
        {
            basedOnHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(builtInText)));
        }

        string baseDir = GetScriniaBaseDir(store);
        string skillsDir = Path.Combine(baseDir, "skills");
        string filePath = Path.Combine(skillsDir, $"{name}.md");
        Directory.CreateDirectory(skillsDir);

        ArchiveFileVersion(filePath, Path.Combine(skillsDir, "versions"));

        await File.WriteAllTextAsync(filePath, promptContent, cancellationToken);

        string now = DateTimeOffset.UtcNow.ToString("o");
        var existingMeta = ReadSidecarMeta(filePath, ScriniaMcpJsonContext.Default.SkillFileMeta);
        string[]? capabilities = string.IsNullOrWhiteSpace(tools) ? null
            : tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meta = new SkillFileMeta(
            BasedOn: basedOnHash,
            Role: role,
            Capabilities: capabilities,
            Scaffold: scaffoldLower,
            CreatedAt: existingMeta?.CreatedAt ?? now,
            UpdatedAt: now);
        WriteSidecarMeta(filePath, meta, ScriniaMcpJsonContext.Default.SkillFileMeta);

        string migrationNote = "";
        try
        {
            await ReadLegacySkillNmpAsync(store, name, cancellationToken);
            migrationNote = $"Note: a legacy NMP/2 entry for skill:{name} still exists — disk file takes precedence.";
        }
        catch { /* no legacy entry */ }

        var response = ResponseBuilder.Success($"Stored as .scrinia/skills/{name}.md.")
            .WithFileChanges()
            .WithPath($"/skill/{name}")
            .WithAction("created");
        if (migrationNote.Length > 0)
            response = response.WithInfo(migrationNote);
        return response.ToYaml();
    }

    /// <summary>List or load stored specialist skills.</summary>
    internal static Task<string> SkillLoad(
        [Description("Skill name to load (e.g. 'api-reviewer'). Omit to list all skills.")] string? name = null,
        [Description("Set to true to show both built-in and override for reconciliation.")] bool reconcile = false,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        if (string.IsNullOrWhiteSpace(name))
        {
            string baseDir = GetScriniaBaseDir(store);
            string skillsDir = Path.Combine(baseDir, "skills");
            var diskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var diskMetas = new Dictionary<string, SkillFileMeta?>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(skillsDir))
            {
                foreach (string mdFile in Directory.GetFiles(skillsDir, "*.md"))
                {
                    string diskName = Path.GetFileNameWithoutExtension(mdFile);
                    diskNames.Add(diskName);
                    diskMetas[diskName] = ReadSidecarMeta(mdFile, ScriniaMcpJsonContext.Default.SkillFileMeta);
                }
            }

            var (scope, _) = store.ParseQualifiedName("skill:placeholder");
            IReadOnlyList<ArtifactEntry> entries;
            try { entries = store.LoadIndex(scope); }
            catch { entries = []; }
            var nmpNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

            var allNames = new HashSet<string>(BuiltInSkills.Keys, StringComparer.OrdinalIgnoreCase);
            allNames.UnionWith(diskNames);
            allNames.UnionWith(nmpNames);

            if (allNames.Count == 0)
                return Task.FromResult(ResponseBuilder.Success("No skills available.").WithAction("listed").ToYaml());

            var sb = new StringBuilder();
            sb.AppendLine($"Available skills ({allNames.Count}):");
            sb.AppendLine();

            foreach (string skillKey in BuiltInSkills.Keys)
            {
                if (diskNames.Contains(skillKey))
                {
                    string tag = "file";
                    if (diskMetas.TryGetValue(skillKey, out var meta) && meta?.BasedOn is not null)
                    {
                        string currentHash = Convert.ToHexStringLower(
                            SHA256.HashData(Encoding.UTF8.GetBytes(BuiltInSkills[skillKey])));
                        if (!meta.BasedOn.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                            tag = "stale base";
                    }
                    sb.AppendLine($"- /skill/{skillKey} [{tag}]");
                }
                else if (nmpNames.Contains(skillKey))
                {
                    var overrideEntry = entries.FirstOrDefault(e => e.Name.Equals(skillKey, StringComparison.OrdinalIgnoreCase));
                    string tag = "override";
                    if (overrideEntry?.Keywords is not null)
                    {
                        var basedOnKw = overrideEntry.Keywords.FirstOrDefault(k => k.StartsWith("basedOn:", StringComparison.Ordinal));
                        if (basedOnKw is not null)
                        {
                            string storedHash = basedOnKw["basedOn:".Length..];
                            string currentHash = Convert.ToHexStringLower(
                                SHA256.HashData(Encoding.UTF8.GetBytes(BuiltInSkills[skillKey])));
                            if (!storedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                                tag = "stale base";
                        }
                    }
                    sb.AppendLine($"- /skill/{skillKey} [{tag}]");
                }
                else
                {
                    sb.AppendLine($"- /skill/{skillKey} [built-in]");
                }
            }

            foreach (string diskName in diskNames)
            {
                if (BuiltInSkills.ContainsKey(diskName))
                    continue;

                string roleTag = diskMetas.TryGetValue(diskName, out var fileMeta) && fileMeta?.Role is not null
                    ? $"role:{fileMeta.Role}"
                    : "role:unknown";
                sb.AppendLine($"- /skill/{diskName} [file] [{roleTag}]");

                if (sb.Length > SkillResponseLimit - 200)
                {
                    sb.AppendLine("[... truncated to 8KB limit]");
                    break;
                }
            }

            foreach (var entry in entries)
            {
                if (BuiltInSkills.ContainsKey(entry.Name) || diskNames.Contains(entry.Name))
                    continue;

                string roleKw = entry.Keywords?
                    .FirstOrDefault(k => k.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
                    ?? "role:unknown";

                sb.AppendLine($"- /skill/{entry.Name} [override] [{roleKw}]");

                if (sb.Length > SkillResponseLimit - 200)
                {
                    sb.AppendLine("[... truncated to 8KB limit]");
                    break;
                }
            }

            return Task.FromResult(ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml());
        }

        return LoadSkillAsync(store, name, reconcile, cancellationToken);
    }

    private static async Task<string> LoadSkillAsync(
        IMemoryStore store, string skillName, bool reconcile, CancellationToken ct)
    {
        string baseDir = GetScriniaBaseDir(store);
        string filePath = Path.Combine(baseDir, "skills", $"{skillName}.md");
        string? diskContent = null;
        if (File.Exists(filePath))
        {
            diskContent = await File.ReadAllTextAsync(filePath, ct);
        }

        string? nmpContent = null;
        if (diskContent is null)
        {
            try { nmpContent = await ReadLegacySkillNmpAsync(store, skillName, ct); }
            catch (FileNotFoundException) { /* no legacy entry */ }
        }

        string? overrideContent = diskContent ?? nmpContent;
        string sourceLabel = diskContent is not null ? "file" : "project override";

        if (reconcile && overrideContent is not null && BuiltInSkills.TryGetValue(skillName, out string? reconBuiltIn))
        {
            string reconContent = $"## Current Built-in\n{reconBuiltIn}\n\n" +
                $"## Your Project Override ({sourceLabel})\n{overrideContent}";
            return ResponseBuilder.Success(reconContent)
                .WithPath($"/skill/{skillName}")
                .WithAction("loaded")
                .WithInstruction("Merge your project-specific additions with the updated built-in base, then call memory('remember', { path: '/skill/...' }) to save the reconciled version.")
                .ToYaml();
        }

        if (overrideContent is null)
        {
            if (BuiltInSkills.TryGetValue(skillName, out string? builtIn))
                return ResponseBuilder.Success(builtIn).WithPath($"/skill/{skillName}").WithAction("loaded").WithInfo("Loaded from built-in").ToYaml();
            return ResponseBuilder.Error($"Skill '{skillName}' not found. Use memory('recall', {{ path: '/skill/' }}) to list available skills.").ToYaml();
        }

        var warnings = new List<string>();

        if (BuiltInSkills.TryGetValue(skillName, out string? currentBuiltIn))
        {
            string? storedHash = null;

            if (diskContent is not null)
            {
                var meta = ReadSidecarMeta(filePath, ScriniaMcpJsonContext.Default.SkillFileMeta);
                storedHash = meta?.BasedOn;
            }
            else if (nmpContent is not null)
            {
                var (scope, subject) = store.ParseQualifiedName($"skill:{skillName}");
                var entries = store.LoadIndex(scope);
                var entry = entries.FirstOrDefault(e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
                if (entry?.Keywords is not null)
                {
                    var basedOnKw = entry.Keywords.FirstOrDefault(k => k.StartsWith("basedOn:", StringComparison.Ordinal));
                    if (basedOnKw is not null)
                        storedHash = basedOnKw["basedOn:".Length..];
                }
            }

            if (storedHash is not null)
            {
                string currentHash = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(currentBuiltIn)));
                if (!storedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"built-in skill has changed since this override was created. Review with memory('recall', {{ path: '/skill/{skillName}', reconcile: true }})");
                }
            }
        }

        var response = ResponseBuilder.Success(overrideContent)
            .WithPath($"/skill/{skillName}")
            .WithAction("loaded")
            .WithInfo($"Loaded from {sourceLabel}");
        if (warnings.Count > 0)
            response = response.WithActionNeeded([.. warnings]);
        return response.ToYaml();
    }
}
