using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using YamlDotNet.Serialization;

namespace Scrinia.Mcp;

public sealed partial class ScriniaProjectTools
{
    // -- Built-in specialist scaffolds (AGENT-04) --------------------------------
    // Loaded from embedded resources: prompts/scaffolds/{name}.md

    private static readonly Lazy<string> _researcherScaffold = new(() =>
        EmbeddedPrompts.LoadScaffold("researcher")
        ?? throw new InvalidOperationException("Built-in researcher scaffold not found"));

    private static readonly Lazy<string> _reviewerScaffold = new(() =>
        EmbeddedPrompts.LoadScaffold("reviewer")
        ?? throw new InvalidOperationException("Built-in reviewer scaffold not found"));

    private static readonly Lazy<string> _domainExpertScaffold = new(() =>
        EmbeddedPrompts.LoadScaffold("domain-expert")
        ?? throw new InvalidOperationException("Built-in domain-expert scaffold not found"));

    private static string ResearcherScaffold => _researcherScaffold.Value;
    private static string ReviewerScaffold => _reviewerScaffold.Value;
    private static string DomainExpertScaffold => _domainExpertScaffold.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _builtInSkills =
        new(() => EmbeddedPrompts.LoadAllSkills());

    private static IReadOnlyDictionary<string, string> BuiltInSkills => _builtInSkills.Value;

    // -- Subagent creation tools (AGENT-01, AGENT-02, AGENT-03, AGENT-04) -------

    /// <summary>Create a reusable specialist skill prompt and store as skill:* memory.</summary>
    internal static async Task<string> SkillCreate(
        [Description("Skill name slug (e.g. 'api-reviewer', 'auth-researcher').")] string name,
        [Description("Built-in scaffold: researcher, reviewer, domain-expert, or custom.")] string scaffold,
        [Description("Additional context or instructions to embed in the prompt.")] string? instructions = null,
        [Description("Comma-separated tool names the agent should use (for custom scaffold).")] string? tools = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Prerequisite check: project:context must exist
        try
        {
            await ReadMemoryAsync(store, "project:context", cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No project initialized. Run project_init first.").ToYaml();
        }

        // Select prompt template based on scaffold (case-insensitive)
        string promptContent;
        string role;

        string scaffoldLower = scaffold.Trim().ToLowerInvariant();
        switch (scaffoldLower)
        {
            case "researcher":
                promptContent = ResearcherScaffold;
                role = "researcher";
                if (!string.IsNullOrWhiteSpace(instructions))
                    promptContent += $"\n## Additional Instructions\n{instructions}\n";
                break;

            case "reviewer":
                promptContent = ReviewerScaffold;
                role = "reviewer";
                if (!string.IsNullOrWhiteSpace(instructions))
                    promptContent += $"\n## Additional Instructions\n{instructions}\n";
                break;

            case "domain-expert":
                promptContent = DomainExpertScaffold;
                role = "domain-expert";
                if (!string.IsNullOrWhiteSpace(instructions))
                    promptContent += $"\n## Additional Instructions\n{instructions}\n";
                break;

            default:
                // Custom scaffold: build from instructions/tools parameters
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
                    $"## Role: Custom Specialist\n" +
                    toolSection +
                    $"## Instructions\n" +
                    $"{instructionsSection}\n\n" +
                    $"## Fallback Instructions (if Scrinia MCP is not available)\n" +
                    $"Organize findings in markdown. Use standard file operations to persist results.\n";
                break;
        }

        // Build capability list for keywords
        string capabilityList = string.IsNullOrWhiteSpace(tools) ? scaffoldLower : tools;

        // Compute basedOn hash if this skill overrides a built-in
        string? basedOnHash = null;
        if (BuiltInSkills.TryGetValue(name, out string? builtInText))
        {
            basedOnHash = Convert.ToHexStringLower(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builtInText)));
        }

        // Write to disk (.scrinia/skills/{name}.md)
        string baseDir = GetScriniaBaseDir(store);
        string skillsDir = Path.Combine(baseDir, "skills");
        string filePath = Path.Combine(skillsDir, $"{name}.md");
        Directory.CreateDirectory(skillsDir);

        // Archive previous version if file exists
        ArchiveFileVersion(filePath, Path.Combine(skillsDir, "versions"));

        // Write skill content as plain markdown
        await File.WriteAllTextAsync(filePath, promptContent, cancellationToken);

        // Write sidecar metadata
        string now = DateTimeOffset.UtcNow.ToString("o");
        var existingMeta = ReadSidecarMeta(filePath, PlanningJsonContext.Default.SkillFileMeta);
        string[]? capabilities = string.IsNullOrWhiteSpace(tools) ? null
            : tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meta = new SkillFileMeta(
            BasedOn: basedOnHash,
            Role: role,
            Capabilities: capabilities,
            Scaffold: scaffoldLower,
            CreatedAt: existingMeta?.CreatedAt ?? now,
            UpdatedAt: now);
        WriteSidecarMeta(filePath, meta, PlanningJsonContext.Default.SkillFileMeta);

        // MF-C01: check for legacy NMP/2 entry, log migration note if found
        string qualifiedName = $"skill:{name}";
        string migrationNote = "";
        try
        {
            await ReadMemoryAsync(store, qualifiedName, cancellationToken);
            migrationNote = $" Note: a legacy NMP/2 entry for {qualifiedName} still exists — it will be used as fallback but the disk file takes precedence.";
        }
        catch { /* no legacy entry — nothing to note */ }

        // Update project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string? skillGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progressPct = CalculateProgress(store, skillGoalId);

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Skill created: {qualifiedName} (role:{role})",
            blockers: "none",
            nextStep: "use memory('recall', { path: '/skill/' }) to retrieve stored skills",
            cancellationToken);

        var scResponse = ResponseBuilder.Success($"Stored as .scrinia/skills/{name}.md.")
            .WithFileChanges()
            .WithPath($"/skill/{name}")
            .WithAction("created");
        if (!string.IsNullOrEmpty(migrationNote))
            scResponse = scResponse.WithInfo(migrationNote.TrimStart());
        return scResponse.ToYaml();
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
            // List mode: scan disk files, NMP/2 index, and built-in dictionary

            // 1. Disk files (.scrinia/skills/*.md)
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
                    diskMetas[diskName] = ReadSidecarMeta(mdFile, PlanningJsonContext.Default.SkillFileMeta);
                }
            }

            // 2. NMP/2 index entries (legacy)
            var (scope, _) = store.ParseQualifiedName("skill:placeholder");
            IReadOnlyList<ArtifactEntry> entries;
            try { entries = store.LoadIndex(scope); }
            catch { entries = []; }
            var nmpNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

            // 3. Merge: collect all unique names across all three sources
            var allNames = new HashSet<string>(BuiltInSkills.Keys, StringComparer.OrdinalIgnoreCase);
            allNames.UnionWith(diskNames);
            allNames.UnionWith(nmpNames);

            if (allNames.Count == 0)
                return Task.FromResult(ResponseBuilder.Success("No skills available.").WithAction("listed").ToYaml());

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Available skills ({allNames.Count}):");
            sb.AppendLine();

            // Built-in skills first (show source label based on override presence)
            foreach (string skillKey in BuiltInSkills.Keys)
            {
                if (diskNames.Contains(skillKey))
                {
                    // Disk file overrides built-in — check staleness via sidecar
                    string tag = "file";
                    if (diskMetas.TryGetValue(skillKey, out var meta) && meta?.BasedOn is not null)
                    {
                        string currentHash = Convert.ToHexStringLower(
                            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(BuiltInSkills[skillKey])));
                        if (!meta.BasedOn.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                            tag = "stale base";
                    }
                    sb.AppendLine($"- /skill/{skillKey} [{tag}]");
                }
                else if (nmpNames.Contains(skillKey))
                {
                    // NMP/2 override (legacy) — check staleness via keywords
                    var overrideEntry = entries.FirstOrDefault(e => e.Name.Equals(skillKey, StringComparison.OrdinalIgnoreCase));
                    string tag = "override";
                    if (overrideEntry?.Keywords is not null)
                    {
                        var basedOnKw = overrideEntry.Keywords.FirstOrDefault(k => k.StartsWith("basedOn:", StringComparison.Ordinal));
                        if (basedOnKw is not null)
                        {
                            string storedHash = basedOnKw["basedOn:".Length..];
                            string currentHash = Convert.ToHexStringLower(
                                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(BuiltInSkills[skillKey])));
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

            // Non-built-in skills: disk files first, then NMP/2-only
            foreach (string diskName in diskNames)
            {
                if (BuiltInSkills.ContainsKey(diskName))
                    continue; // already listed above

                string roleTag = diskMetas.TryGetValue(diskName, out var fileMeta) && fileMeta?.Role is not null
                    ? $"role:{fileMeta.Role}"
                    : "role:unknown";
                sb.AppendLine($"- /skill/{diskName} [file] [{roleTag}]");

                if (sb.Length > MaxResponseChars - 200)
                {
                    sb.AppendLine("[... truncated to 8KB limit]");
                    break;
                }
            }

            foreach (var entry in entries)
            {
                if (BuiltInSkills.ContainsKey(entry.Name) || diskNames.Contains(entry.Name))
                    continue; // already listed above (built-in or disk takes precedence)

                string roleKw = entry.Keywords?
                    .FirstOrDefault(k => k.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
                    ?? "role:unknown";

                sb.AppendLine($"- /skill/{entry.Name} [override] [{roleKw}]");

                if (sb.Length > MaxResponseChars - 200)
                {
                    sb.AppendLine("[... truncated to 8KB limit]");
                    break;
                }
            }

            return Task.FromResult(ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml());
        }

        // Load mode: async artifact read
        return LoadSkillAsync(store, name, reconcile, cancellationToken);
    }

    private static async Task<string> LoadSkillAsync(
        IMemoryStore store, string skillName, bool reconcile, CancellationToken ct)
    {
        // 1. Disk file (.scrinia/skills/{name}.md)
        string baseDir = GetScriniaBaseDir(store);
        string filePath = Path.Combine(baseDir, "skills", $"{skillName}.md");
        string? diskContent = null;
        if (File.Exists(filePath))
        {
            diskContent = await File.ReadAllTextAsync(filePath, ct);
        }

        // 2. NMP/2 fallback (legacy)
        string? nmpContent = null;
        if (diskContent is null)
        {
            try
            {
                nmpContent = await ReadMemoryAsync(store, $"skill:{skillName}", ct);
            }
            catch (FileNotFoundException)
            {
                // No NMP/2 override exists
            }
        }

        // Determine the override content (disk > NMP/2) and its source label
        string? overrideContent = diskContent ?? nmpContent;
        string sourceLabel = diskContent is not null ? "file" : "project override";

        // Reconcile mode: show both built-in and override side by side
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
            // Fall back to built-in skills
            if (BuiltInSkills.TryGetValue(skillName, out string? builtIn))
                return ResponseBuilder.Success(builtIn).WithPath($"/skill/{skillName}").WithAction("loaded").WithInfo("Loaded from built-in").ToYaml();
            return ResponseBuilder.Error($"Skill '{skillName}' not found. Use memory('recall', {{ path: '/skill/' }}) to list available skills.").ToYaml();
        }

        var slWarnings = new List<string>();

        // Check for stale base — warn if the built-in has changed since this override was created
        if (BuiltInSkills.TryGetValue(skillName, out string? currentBuiltIn))
        {
            string? storedHash = null;

            // Read basedOn hash from sidecar metadata (disk file)
            if (diskContent is not null)
            {
                var meta = ReadSidecarMeta(filePath, PlanningJsonContext.Default.SkillFileMeta);
                storedHash = meta?.BasedOn;
            }
            else if (nmpContent is not null)
            {
                // Fall back to NMP/2 keyword-based basedOn
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
                    SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(currentBuiltIn)));
                if (!storedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    slWarnings.Add($"built-in skill has changed since this override was created. Review with memory('recall', {{ path: '/skill/{skillName}', reconcile: true }})");
                }
            }
        }

        var slResponse = ResponseBuilder.Success(overrideContent)
            .WithPath($"/skill/{skillName}")
            .WithAction("loaded")
            .WithInfo($"Loaded from {sourceLabel}");
        if (slWarnings.Count > 0)
            slResponse = slResponse.WithActionNeeded([.. slWarnings]);
        return slResponse.ToYaml();
    }
}
