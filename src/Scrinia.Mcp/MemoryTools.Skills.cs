using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using Scrinia.Core;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Mcp;

/// <summary>Metadata sidecar for a skill file on disk.</summary>
public record SkillFileMeta(
    string? BasedOn,
    string? Role,
    string[]? Capabilities,
    string? Scaffold,
    string? CreatedAt,
    string? UpdatedAt,
    [property: JsonPropertyName("v")] int? SchemaVersion = null)
{
    /// <summary>
    /// Keys present in the sidecar that this build has no member for, preserved verbatim so a
    /// read-write cycle cannot delete them. Same rationale and the same mutability constraint as
    /// <see cref="ArtifactEntry.Unknown"/>: STJ refuses an init-only extension-data property on a
    /// record with a parameterized constructor, so this cannot be <c>{ get; init; }</c>.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>Metadata sidecar for an agent config file on disk.</summary>
public record AgentFileMeta(
    string? CreatedAt,
    string? UpdatedAt,
    [property: JsonPropertyName("v")] int? SchemaVersion = null)
{
    /// <summary>Same extension bucket, same constraint. See <see cref="SkillFileMeta.Unknown"/>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

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
    /// metadata, so the host must marshal them via source generation — compose the SDK defaults
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

    /// <summary>
    /// Reads a JSON sidecar (.meta.json) next to <paramref name="filePath"/>. Null if missing or
    /// corrupted.
    ///
    /// <para>Deliberately does NOT gate on <see cref="DurableFormats.SkillMeta"/> /
    /// <see cref="DurableFormats.AgentMeta"/>. Unlike <c>ReadSidecarRaw</c>'s blanket catch, this
    /// method catches only <see cref="JsonException"/> — an <see cref="IOException"/> already
    /// escapes it today, so a version-disposition throw added here would escape the same way. A
    /// read must never throw on a version; the refusal belongs solely to
    /// <see cref="WriteSidecarMeta"/>, exactly as <c>WriteSidecarCore</c> is the only gate for
    /// the artifact sidecar.</para>
    /// </summary>
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

    /// <summary>
    /// The contract that governs a sidecar type, and the version the instance declares.
    ///
    /// <para>Chosen from <typeparamref name="T"/> rather than passed in, so no caller can pair a
    /// type with the wrong contract. A THIRD type must not fall through: before this threw, an
    /// unregistered sidecar type got AgentMeta's contract and a null version, which AbsentMeans
    /// resolves to Load — silently ungated. Registering a format is not optional, and the throw
    /// makes forgetting it loud at the first write rather than invisible forever.</para>
    /// </summary>
    private static (FormatContract Contract, int? Declared) ContractAndVersion<T>(T meta) =>
    (
        typeof(T) == typeof(SkillFileMeta) ? DurableFormats.SkillMeta :
        typeof(T) == typeof(AgentFileMeta) ? DurableFormats.AgentMeta :
        throw new ArgumentOutOfRangeException(
            nameof(meta),
            $"No DurableFormats contract is registered for sidecar type {typeof(T).Name}."),
        meta switch
        {
            SkillFileMeta skillMeta => skillMeta.SchemaVersion,
            AgentFileMeta agentMeta => agentMeta.SchemaVersion,
            _ => null
        }
    );

    /// <summary>
    /// Reads the existing sidecar and REFUSES immediately if a newer build wrote it — before the
    /// caller performs any side effect.
    ///
    /// <para><b>Why this exists rather than relying on <see cref="WriteSidecarMeta{T}"/>'s throw.</b>
    /// Both rewrite paths archive the previous file and overwrite the body BEFORE they touch the
    /// sidecar, and neither has a rollback. Refusing only at the write meant a rejected rewrite
    /// still destroyed the previous content, left metadata describing content that no longer
    /// existed, and deposited a spurious versions/ entry — while reporting failure to the user.
    /// That is the content/metadata divergence this whole change exists to prevent, and it is the
    /// same "refuse before the first side effect" rule already applied to StoreAsync and
    /// ImportTopicEntries. WriteSidecarMeta's throw stays as the backstop beneath it.</para>
    /// </summary>
    internal static T? ReadSidecarMetaOrRefuse<T>(string filePath, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        var existing = ReadSidecarMeta(filePath, typeInfo);
        if (existing is null) return null;

        var (contract, declared) = ContractAndVersion(existing);
        if (FormatVersion.Evaluate(contract, declared) == FormatDisposition.LoadReadOnly)
            throw new ScopeReadOnlyException(
                Path.GetDirectoryName(filePath) ?? filePath,
                Path.GetFileNameWithoutExtension(filePath),
                declared ?? contract.Current,
                contract.Current);

        return existing;
    }

    /// <summary>
    /// Writes a JSON sidecar (.meta.json) next to <paramref name="filePath"/>. Refuses when
    /// <paramref name="meta"/>'s own declared version disposes to <see cref="FormatDisposition.LoadReadOnly"/>,
    /// mirroring the artifact sidecar's <c>WriteSidecarCore</c> backstop, which gates on the
    /// entry object being written rather than re-reading the file it is about to replace. Reads
    /// still return the object regardless (see <see cref="ReadSidecarMeta{T}"/>).
    /// </summary>
    internal static void WriteSidecarMeta<T>(string filePath, T meta, JsonTypeInfo<T> typeInfo)
    {
        // Two types, two contracts, chosen here rather than passed in so no caller can pair a
        // type with the wrong contract.
        //
        // A THIRD type must not fall through. Before this threw, an unregistered sidecar type got
        // AgentMeta's contract and a null `declared`, which AbsentMeans resolves to Load -- so it
        // was silently ungated while this comment claimed otherwise. Registering a format is not
        // optional; the throw makes forgetting it loud at the first write instead of invisible.
        var (contract, declared) = ContractAndVersion(meta);

        if (FormatVersion.Evaluate(contract, declared) == FormatDisposition.LoadReadOnly)
            throw new ScopeReadOnlyException(
                // The DIRECTORY, matching what WriteSidecarCore passes. Passing the .meta.json
                // path here rendered "Scope 'C:\...oo.meta.json' is read-only", which names a
                // file as a scope and reads as nonsense in a message a user will actually see.
                Path.GetDirectoryName(filePath) ?? filePath,
                Path.GetFileNameWithoutExtension(filePath),
                declared ?? contract.Current,
                contract.Current);

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

        // BEFORE the archive and the body write, not after. Both are side effects with no
        // rollback, so a refusal raised later leaves the previous prompt destroyed and its
        // metadata describing content that no longer exists.
        var existingMeta = ReadSidecarMetaOrRefuse(filePath, ScriniaMcpJsonContext.Default.SkillFileMeta);

        ArchiveFileVersion(filePath, Path.Combine(skillsDir, "versions"));

        await File.WriteAllTextAsync(filePath, promptContent, cancellationToken);

        string now = DateTimeOffset.UtcNow.ToString("o");
        string[]? capabilities = string.IsNullOrWhiteSpace(tools) ? null
            : tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meta = new SkillFileMeta(
            BasedOn: basedOnHash,
            Role: role,
            Capabilities: capabilities,
            Scaffold: scaffoldLower,
            CreatedAt: existingMeta?.CreatedAt ?? now,
            UpdatedAt: now,
            // Carried forward, exactly as CreatedAt above is. Without this the extension bucket
            // is decorative: every skill rewrite builds a FRESH record with an empty bucket and a
            // null version, so the first rewrite after versioning shipped would delete the unknown
            // fields versioning exists to preserve -- and, declaring no version, it would sail
            // past the write gate while doing it. Same defect BuildDerivedEntry closes for the
            // artifact sidecar; same fix.
            SchemaVersion: existingMeta?.SchemaVersion)
        {
            // COPIED, never aliased -- same rule as ArtifactEntry.Unknown.
            Unknown = existingMeta?.Unknown is { } u
                ? new Dictionary<string, System.Text.Json.JsonElement>(u)
                : null
        };
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
