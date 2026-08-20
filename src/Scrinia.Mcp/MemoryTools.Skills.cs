using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using Scrinia.Core;
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
// MCP tool params attributes/attributeFilter are Dictionary<string,string[]>; registered here so the
// trimmed `scri serve` publish can marshal them source-gen (no reflection fallback). See CreateToolSerializerOptions.
[JsonSerializable(typeof(Dictionary<string, string[]>))]
// Registered so tests can deserialize a persisted ".meta.json" sidecar directly (bypassing
// FileMemoryStore's read-time forward-fill) without a contextless JsonSerializer call — see
// WriterCutoverTests.ReadSidecarDirect.
[JsonSerializable(typeof(ArtifactEntry))]
public partial class ScriniaMcpJsonContext : JsonSerializerContext;

public sealed partial class ScriniaMcpTools
{
    /// <summary>
    /// JSON options for MCP tool parameter marshalling. The tool surface exposes
    /// <c>Dictionary&lt;string, string[]&gt;</c> parameters (<c>attributes</c> / <c>attributeFilter</c> on
    /// remember/search/append). The trimmed single-file <c>scri serve</c> publish has no reflection
    /// metadata, so the server must marshal them via source generation — compose the SDK defaults
    /// (primitives, arrays, MCP protocol types) with <see cref="ScriniaMcpJsonContext"/>, which supplies
    /// the attribute-dictionary metadata. Pass to <c>WithTools&lt;ScriniaMcpTools&gt;(...)</c>; without it,
    /// tool construction throws <see cref="System.NotSupportedException"/> at server startup under trimming.
    /// </summary>
    public static JsonSerializerOptions CreateToolSerializerOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Insert(0, ScriniaMcpJsonContext.Default);
        return options;
    }

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

    /// <summary>Create a reusable specialist skill prompt and persist as .scrinia/skills/{name}.md.</summary>
    internal static async Task<McpResponse> SkillCreate(
        [Description("Skill name slug (e.g. 'api-reviewer', 'auth-researcher').")] string name,
        [Description("Built-in scaffold: researcher, reviewer, domain-expert, or custom.")] string scaffold,
        [Description("Additional context or instructions to embed in the prompt.")] string? instructions = null,
        [Description("Comma-separated tool names the agent should use (for custom scaffold).")] string? tools = null,
        CancellationToken cancellationToken = default)
    {
        if (!FileEntityName.IsValid(name))
            return ResponseBuilder.Error(
                $"Invalid skill name '{name}'. Use letters, digits, '-', '_' or '.' only.",
                ErrorCodes.InvalidParameter,
                "remember({ path: '/skill/my-helper', content: [...] })");

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

        string skillsDir = PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot);
        if (!PathRouter.TryToFileEntityPath("skill", name, store.WorkspaceRoot, out string filePath))
            return ResponseBuilder.Error(
                $"Invalid skill name '{name}'.",
                ErrorCodes.InvalidParameter,
                "remember({ path: '/skill/my-helper', content: [...] })");
        FileMemoryStore.EnsureGitIgnore(store.WorkspaceRoot);
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

        var response = ResponseBuilder.Success($"Stored as .scrinia/skills/{name}.md.")
            .WithFileChanges()
            .WithPath($"/skill/{name}")
            .WithAction("created");
        return response;
    }

    /// <summary>
    /// Delete a stored skill (.scrinia/skills/{name}.md) and its metadata sidecar.
    /// <para>Only a skill on disk can be forgotten. A built-in ships inside the assembly, so it
    /// answers with its own error rather than <see cref="ErrorCodes.NotFound"/> — NOT_FOUND would
    /// be a false statement about a skill <see cref="SkillLoad"/> will happily return. A disk
    /// skill that shadows a built-in of the same name deletes normally, and the built-in resolves
    /// again afterwards; the response says so.</para>
    /// <para>The file is archived into skills/versions/ first, the same way
    /// <see cref="SkillCreate"/> archives before it overwrites, so a mistaken forget of
    /// hand-authored instructions is recoverable from the workspace.</para>
    /// </summary>
    internal static Task<McpResponse> SkillDelete(
        [Description("Skill name to forget (e.g. 'api-reviewer').")] string name,
        CancellationToken cancellationToken = default)
    {
        if (!FileEntityName.IsValid(name))
            return Task.FromResult(ResponseBuilder.Error(
                $"Invalid skill name '{name}'. Use letters, digits, '-', '_' or '.' only.",
                ErrorCodes.InvalidParameter,
                "forget({ path: '/skill/my-helper' })"));

        var store = CurrentStore;

        if (!PathRouter.TryToFileEntityPath("skill", name, store.WorkspaceRoot, out string filePath))
            return Task.FromResult(ResponseBuilder.Error(
                $"Invalid skill name '{name}'.",
                ErrorCodes.InvalidParameter,
                "forget({ path: '/skill/my-helper' })"));

        bool isBuiltIn = BuiltInSkills.ContainsKey(name);

        if (!File.Exists(filePath))
        {
            if (isBuiltIn)
                return Task.FromResult(ResponseBuilder.Error(
                    $"'{name}' is a built-in skill that ships with Scrinia, not a file in this "
                    + "workspace, so it cannot be forgotten.",
                    ErrorCodes.InvalidParameter,
                    $"recall({{ path: '/skill/{name}' }}) still loads it.",
                    $"remember({{ path: '/skill/{name}', content: [...] }}) writes a project "
                    + "override that shadows the built-in; forget that override to fall back."));

            return Task.FromResult(ResponseBuilder.Error(
                $"Skill '{name}' not found.",
                ErrorCodes.NotFound,
                "recall({ path: '/skill/' }) to list available skills"));
        }

        string skillsDir = PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot);
        ArchiveFileVersion(filePath, Path.Combine(skillsDir, "versions"));
        File.Delete(filePath);

        string metaPath = Path.ChangeExtension(filePath, ".meta.json");
        if (File.Exists(metaPath))
            File.Delete(metaPath);

        var response = ResponseBuilder.Success(
                $"Forgot /skill/{name}. Deleted .scrinia/skills/{name}.md; the previous text is "
                + "archived under .scrinia/skills/versions/.")
            .WithFileChanges()
            .WithPath($"/skill/{name}")
            .WithAction("forgotten");

        if (isBuiltIn)
            response = response.WithInfo(
                $"'{name}' was a project override of a built-in skill. The built-in resolves "
                + $"again — recall({{ path: '/skill/{name}' }}) still returns instructions.");

        return Task.FromResult(response);
    }

    /// <summary>List or load stored specialist skills.</summary>
    internal static Task<McpResponse> SkillLoad(
        [Description("Skill name to load (e.g. 'api-reviewer'). Omit to list all skills.")] string? name = null,
        [Description("Set to true to show both built-in and override side-by-side for merging.")] bool withBuiltin = false,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        if (string.IsNullOrWhiteSpace(name))
        {
            string skillsDir = PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot);
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

            var allNames = new HashSet<string>(BuiltInSkills.Keys, StringComparer.OrdinalIgnoreCase);
            allNames.UnionWith(diskNames);

            if (allNames.Count == 0)
                return Task.FromResult(ResponseBuilder.Success("No skills available.").WithAction("listed"));

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

            return Task.FromResult(ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed"));
        }

        return LoadSkillAsync(store, name, withBuiltin, cancellationToken);
    }

    private static async Task<McpResponse> LoadSkillAsync(
        IMemoryStore store, string skillName, bool withBuiltin, CancellationToken ct)
    {
        if (!PathRouter.TryToFileEntityPath("skill", skillName, store.WorkspaceRoot, out string filePath))
            return ResponseBuilder.Error(
                $"Invalid skill name '{skillName}'.", ErrorCodes.NotFound,
                "recall({ path: '/skill/' }) to list available skills");
        string? diskContent = null;
        if (File.Exists(filePath))
        {
            diskContent = await File.ReadAllTextAsync(filePath, ct);
        }

        string? overrideContent = diskContent;
        string sourceLabel = "file";

        if (withBuiltin && overrideContent is not null && BuiltInSkills.TryGetValue(skillName, out string? reconBuiltIn))
        {
            string reconContent = $"## Current Built-in\n{reconBuiltIn}\n\n" +
                $"## Your Project Override ({sourceLabel})\n{overrideContent}";
            return ResponseBuilder.Success(reconContent)
                .WithPath($"/skill/{skillName}")
                .WithAction("loaded")
                .WithInstruction("Merge your project-specific additions with the updated built-in base, then call remember({ path: '/skill/...' }) to save the merged version.")
                .WithTruncationAdvice(SkillContentTruncationAdvice);
        }

        if (overrideContent is null)
        {
            if (BuiltInSkills.TryGetValue(skillName, out string? builtIn))
                return ResponseBuilder.Success(builtIn).WithPath($"/skill/{skillName}").WithAction("loaded").WithInfo("Loaded from built-in")
                    .WithTruncationAdvice(SkillContentTruncationAdvice);
            return ResponseBuilder.Error(
                $"Skill '{skillName}' not found.",
                ErrorCodes.NotFound,
                "recall({ path: '/skill/' }) to list available skills");
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

            if (storedHash is not null)
            {
                string currentHash = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(currentBuiltIn)));
                if (!storedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"built-in skill has changed since this override was created. Review with recall({{ path: '/skill/{skillName}', withBuiltin: true }})");
                }
            }
        }

        var response = ResponseBuilder.Success(overrideContent)
            .WithPath($"/skill/{skillName}")
            .WithAction("loaded")
            .WithInfo($"Loaded from {sourceLabel}")
            .WithTruncationAdvice(SkillContentTruncationAdvice);
        if (warnings.Count > 0)
            response = response.WithActionNeeded([.. warnings]);
        return response;
    }
}
