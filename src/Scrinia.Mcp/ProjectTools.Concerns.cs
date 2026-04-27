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
    // ── Concern tracking tools (CONC-01, CONC-02, CONC-03) ───────────────────

    /// <summary>Internal dispatcher for concern operations — delegates to ConcernAdd/ConcernResolve/ConcernList. Exposed via memory() dispatcher.</summary>
    public async Task<string> ConcernDispatch(
        string action = "list",
        string? description = null,
        string? severity = null,
        string? phaseScope = null,
        string? id = null,
        string? concernName = null,
        string? resolution = null,
        string? verifiedBy = null,
        string? phaseFilter = null,
        CancellationToken cancellationToken = default)
    {
        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "add":
                if (string.IsNullOrWhiteSpace(description))
                    return ResponseBuilder.Error("concern('add') requires 'description' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(severity))
                    return ResponseBuilder.Error("concern('add') requires 'severity' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(phaseScope))
                    return ResponseBuilder.Error("concern('add') requires 'phaseScope' parameter.").ToYaml();
                return await ConcernAdd(description, severity, phaseScope, id, cancellationToken);

            case "resolve":
                if (string.IsNullOrWhiteSpace(concernName))
                    return ResponseBuilder.Error("concern('resolve') requires 'concernName' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(resolution))
                    return ResponseBuilder.Error("concern('resolve') requires 'resolution' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(verifiedBy))
                    return ResponseBuilder.Error("concern('resolve') requires 'verifiedBy' parameter.").ToYaml();
                return await ConcernResolve(concernName, resolution, verifiedBy, cancellationToken);

            case "list":
                return await ConcernList(phaseFilter, statusFilter: null, cancellationToken);

            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: 'add', 'resolve', 'list'.").ToYaml();
        }
    }

    /// <summary>Track a risk or concern with severity and phase scope.</summary>
    internal static async Task<string> ConcernAdd(
        [Description("Concern description.")] string description,
        [Description("Severity: high, medium, or low.")] string severity,
        [Description("Phase scope, e.g. '06' or 'all'.")] string phaseScope,
        [Description("Optional readable ID; auto-generated if omitted (e.g. 'auth-risk').")] string? id = null,
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

        // Generate ID if not provided
        string concernId = id ?? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

        // Build content
        string content =
            $"## Concern: {concernId}\n" +
            $"**Description:** {description}\n" +
            $"**Severity:** {severity}\n" +
            $"**Phase:** {phaseScope}\n" +
            $"**Added:** {DateTimeOffset.UtcNow:o}\n";

        string qualifiedName = $"concern:{concernId}";

        // Extract keywords from description and merge with explicit keywords
        var (autoKeywords, _) = TextAnalysis.AnalyzeText(description);
        string[] explicitKeywords = ["status:active", $"severity:{severity}", $"phase:{phaseScope}"];
        string[] mergedKeywords = TextAnalysis.MergeKeywords(explicitKeywords, autoKeywords);

        await WritePlanningMemoryAsync(store, qualifiedName, content,
            archiveExisting: false,
            keywords: mergedKeywords,
            cancellationToken);

        // Detect concern keyword patterns
        string patternSuggestion = "";
        try
        {
            var (concernScope, _) = store.ParseQualifiedName("concern:placeholder");
            var allConcerns = store.LoadIndex(concernScope);

            // Noise prefixes to exclude from pattern matching
            var noisePrefixes = new[] { "status:", "severity:", "phase:", "provenance:",
                "goal:", "ref:", "file:", "wave:", "depends_on:", "basedOn:", "type:" };

            // Count keyword frequency across active concerns
            var keywordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in allConcerns)
            {
                if (entry.Keywords is null) continue;
                if (!entry.Keywords.Any(k => k.Equals("status:active", StringComparison.OrdinalIgnoreCase)))
                    continue; // only count active concerns

                foreach (var kw in entry.Keywords)
                {
                    if (kw.Equals("orphan", StringComparison.OrdinalIgnoreCase)) continue;
                    if (noisePrefixes.Any(p => kw.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                    keywordCounts.TryGetValue(kw, out int count);
                    keywordCounts[kw] = count + 1;
                }
            }

            // Find keywords shared by 3+ concerns
            var patterns = keywordCounts
                .Where(kv => kv.Value >= 3)
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .ToList();

            if (patterns.Count > 0)
            {
                patternSuggestion = "\n" + string.Join("\n", patterns.Select(p =>
                    $"Pattern detected: {p.Value} concerns share keyword '{p.Key}'. Consider creating a patterns:{p.Key} memory."));
            }
        }
        catch { /* best-effort */ }

        // Update project state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string? concernGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progressPct = CalculateProgress(store, concernGoalId);

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Concern added: {qualifiedName} (severity:{severity})",
            blockers: "none",
            nextStep: "run memory('list', { path: '/concern/' }) to list active concerns, or memory('transition', { path: '/concern/...', to: 'resolved' }) when addressed",
            cancellationToken);

        var caResponse = ResponseBuilder.Success($"Stored as {qualifiedName}.")
            .WithFileChanges()
            .WithPath($"/concern/{concernId}")
            .WithAction("created");
        if (!string.IsNullOrEmpty(patternSuggestion))
            caResponse = caResponse.WithInfo(patternSuggestion.Trim());
        return caResponse.ToYaml();
    }

    /// <summary>Resolve a tracked concern with resolution notes.</summary>
    internal static async Task<string> ConcernResolve(
        [Description("Concern name (e.g. 'concern:auth-risk' or 'concern:20260319-143022').")] string concernName,
        [Description("Resolution notes.")] string resolution,
        [Description("Who verified the resolution: 'debugger', 'qa', or 'manual'.")] string verifiedBy,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        var validVerifiers = new[] { "debugger", "qa", "manual" };
        if (!validVerifiers.Contains(verifiedBy, StringComparer.OrdinalIgnoreCase))
            return ResponseBuilder.Error($"verifiedBy must be 'debugger', 'qa', or 'manual'. Got: '{verifiedBy}'.").ToYaml();

        // Parse name to get scope and subject
        var (scope, subject) = store.ParseQualifiedName(concernName);

        // Load index and find existing entry
        var allEntries = store.LoadIndex(scope);
        var existing = allEntries.FirstOrDefault(e =>
            string.Equals(e.Name, subject, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            return ResponseBuilder.Error($"Concern '{concernName}' not found.").ToYaml();

        // Extract existing severity and phase keywords (to preserve them)
        string severityKw = existing.Keywords?
            .FirstOrDefault(k => k.StartsWith("severity:", StringComparison.OrdinalIgnoreCase))
            ?? "severity:unknown";
        string phaseKw = existing.Keywords?
            .FirstOrDefault(k => k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase))
            ?? "phase:unknown";

        // Read existing content
        string existingContent;
        try
        {
            existingContent = await ReadMemoryAsync(store, concernName, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            existingContent = $"(original content not found for {concernName})";
        }

        // Build updated content with resolution appended
        string timestamp = DateTimeOffset.UtcNow.ToString("o");
        string updatedContent =
            existingContent.TrimEnd() +
            $"\n\n## Resolution\n{resolution}\n**Resolved at:** {timestamp}\n";

        // Write updated content with resolved status (no archiving)
        string[] resolvedKeywords = ["status:resolved", severityKw, phaseKw, $"verified_by:{verifiedBy.ToLowerInvariant()}"];
        await WritePlanningMemoryAsync(store, concernName, updatedContent,
            archiveExisting: false,
            keywords: resolvedKeywords,
            cancellationToken);

        // Update project state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";
        string? resolveGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progressPct = CalculateProgress(store, resolveGoalId);

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Concern resolved: {concernName}",
            blockers: "none",
            nextStep: "run concern to check remaining active concerns",
            cancellationToken);

        return ResponseBuilder.Success($"Concern '{concernName}' resolved.")
            .WithFileChanges()
            .WithPath($"/concern/{subject}")
            .WithAction("resolved")
            .ToYaml();
    }

    /// <summary>List tracked concerns by status and phase (index-only, no artifact decoding).</summary>
    internal static Task<string> ConcernList(
        [Description("Filter by phase (e.g. '06'); omit for all phases.")] string? phaseFilter = null,
        [Description("Filter by status; defaults to 'active'.")] string? statusFilter = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        string effectiveStatus = statusFilter ?? "active";

        // Load index via keyword-only scan
        IReadOnlyList<ArtifactEntry> allEntries;
        try
        {
            var (scope, _) = store.ParseQualifiedName("concern:placeholder");
            allEntries = store.LoadIndex(scope);
        }
        catch
        {
            return Task.FromResult(ResponseBuilder.Success("No active concerns.").WithAction("listed").ToYaml());
        }

        // Filter by status
        var filtered = allEntries
            .Where(e => HasKeyword(e, $"status:{effectiveStatus}"))
            .ToList();

        // Filter by phase if provided
        if (!string.IsNullOrWhiteSpace(phaseFilter))
        {
            filtered = filtered
                .Where(e => HasKeyword(e, $"phase:{phaseFilter}"))
                .ToList();
        }

        if (filtered.Count == 0)
        {
            string phaseNote = phaseFilter is not null ? $" (phase:{phaseFilter})" : "";
            return Task.FromResult(ResponseBuilder.Success($"No active concerns{phaseNote}.").WithAction("listed").ToYaml());
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Active concerns ({filtered.Count}):");
        sb.AppendLine();

        foreach (var entry in filtered)
        {
            string sevKw = entry.Keywords?
                .FirstOrDefault(k => k.StartsWith("severity:", StringComparison.OrdinalIgnoreCase))
                ?? "severity:unknown";
            string phaseKw = entry.Keywords?
                .FirstOrDefault(k => k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase))
                ?? "phase:unknown";

            sb.AppendLine($"- /concern/{entry.Name} [{sevKw}] [{phaseKw}]");

            if (sb.Length > MaxResponseChars - 200)
            {
                sb.AppendLine("[... truncated to 8KB limit]");
                break;
            }
        }

        return Task.FromResult(ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("listed").ToYaml());
    }
}
