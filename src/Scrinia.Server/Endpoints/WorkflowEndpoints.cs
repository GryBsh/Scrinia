using System.Text.Json;
using System.Text.RegularExpressions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.Server.Auth;
using Scrinia.Server.Models;
using Scrinia.Server.Sse;
using Scrinia.Server.Services;
using YamlDotNet.Serialization;

namespace Scrinia.Server.Endpoints;

public static class WorkflowEndpoints
{
    // Reuse the same goal-ID regex patterns that ProjectTools uses
    private static readonly Regex BracketedGoalIdPattern =
        new(@"\[G-(\d+(?:-[a-fA-F0-9]+)?)\]", RegexOptions.Compiled);

    private static readonly Regex GoalsSectionPattern =
        new(@"^#{0,4}\s*Goals\s*:?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GoalsSectionAltPattern =
        new(@"^Goals\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SectionHeadingPattern =
        new(@"^#{1,4}\s+\S", RegexOptions.Compiled);

    private static readonly Regex SafeNamePattern =
        new(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/stores/{store}/workflows")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/", ListWorkflows);
        group.MapGet("/{name}", GetWorkflow);
        group.MapPut("/{name}", UpdateWorkflow);
        group.MapGet("/goals", ListGoals);
        group.MapGet("/goals/{goalId}", GetGoalDetail);
        group.MapGet("/goals/{goalId}/tasks", GetGoalTasks);
        group.MapGet("/goals/{goalId}/events", StreamGoalEvents);
    }

    // ── 1. GET / — List workflows ───────────────────────────────────────────

    private static Task<IResult> ListWorkflows(string store, RequestContext ctx)
    {
        if (!ctx.HasPermission("read"))
            return Task.FromResult<IResult>(
                Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403));

        var workflows = new List<WorkflowSummary>();

        // Built-in workflows
        var defaultWf = WorkflowDefinition.DefaultGoalWorkflow;
        workflows.Add(new WorkflowSummary(
            defaultWf.Name,
            defaultWf.SeedActivities.Length,
            defaultWf.GateActivities.Length,
            IsBuiltIn: true));

        var quickFixWf = WorkflowDefinition.QuickFixWorkflow;
        workflows.Add(new WorkflowSummary(
            quickFixWf.Name,
            quickFixWf.SeedActivities.Length,
            quickFixWf.GateActivities.Length,
            IsBuiltIn: true));

        // Scan .scrinia/workflows/ for overrides
        string baseDir = GetScriniaBaseDir(ctx.Store!);
        string workflowsDir = Path.Combine(baseDir, "workflows");
        if (Directory.Exists(workflowsDir))
        {
            foreach (string file in Directory.GetFiles(workflowsDir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not ".json" and not ".yaml" and not ".yml")
                    continue;

                string name = Path.GetFileNameWithoutExtension(file);

                // Skip if we already added a built-in with this name (override replaces it)
                if (workflows.Any(w =>
                    string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    // Replace the built-in entry with the override info
                    workflows.RemoveAll(w =>
                        string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
                }

                // Try to parse for activity counts
                try
                {
                    string content = File.ReadAllText(file);
                    WorkflowDefinition? parsed = null;

                    if (ext is ".yaml" or ".yml")
                    {
                        var yamlDeserializer = new DeserializerBuilder().Build();
                        var yamlObj = yamlDeserializer.Deserialize<object>(content);
                        var jsonSerializer = new SerializerBuilder().JsonCompatible().Build();
                        string json = jsonSerializer.Serialize(yamlObj);
                        parsed = JsonSerializer.Deserialize(json,
                            PlanningJsonContext.Default.WorkflowDefinition);
                    }
                    else
                    {
                        parsed = JsonSerializer.Deserialize(content,
                            PlanningJsonContext.Default.WorkflowDefinition);
                    }

                    if (parsed is not null)
                    {
                        workflows.Add(new WorkflowSummary(
                            parsed.Name,
                            parsed.SeedActivities?.Length ?? 0,
                            parsed.GateActivities?.Length ?? 0,
                            IsBuiltIn: false));
                        continue;
                    }
                }
                catch
                {
                    // Fall through to add with zero counts
                }

                workflows.Add(new WorkflowSummary(name, 0, 0, IsBuiltIn: false));
            }
        }

        return Task.FromResult<IResult>(
            Results.Ok(new WorkflowListResponse(workflows.ToArray())));
    }

    // ── 2. GET /{name} — Get workflow ───────────────────────────────────────

    private static async Task<IResult> GetWorkflow(
        string store, string name, RequestContext ctx, CancellationToken ct)
    {
        if (!ctx.HasPermission("read"))
            return Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403);

        if (!SafeNamePattern.IsMatch(name))
            return Results.BadRequest(new ErrorResponse("Invalid workflow name."));

        string baseDir = GetScriniaBaseDir(ctx.Store!);
        string workflowsDir = Path.Combine(baseDir, "workflows");

        // Try disk files: YAML first, then JSON
        foreach (string ext in new[] { ".yaml", ".yml" })
        {
            string path = Path.Combine(workflowsDir, $"{name}{ext}");
            if (File.Exists(path))
            {
                string yamlContent = await File.ReadAllTextAsync(path, ct);
                return Results.Ok(new WorkflowContent(name, yamlContent));
            }
        }

        {
            string jsonPath = Path.Combine(workflowsDir, $"{name}.json");
            if (File.Exists(jsonPath))
            {
                string jsonContent = await File.ReadAllTextAsync(jsonPath, ct);
                // Convert JSON to YAML for consistent response format
                string yamlContent = ConvertJsonToYaml(jsonContent);
                return Results.Ok(new WorkflowContent(name, yamlContent));
            }
        }

        // Check built-in workflows
        WorkflowDefinition? builtIn = name switch
        {
            "goal-execution" => WorkflowDefinition.DefaultGoalWorkflow,
            "quick-fix" => WorkflowDefinition.QuickFixWorkflow,
            _ => null
        };

        if (builtIn is not null)
        {
            string json = JsonSerializer.Serialize(builtIn,
                PlanningJsonContext.Default.WorkflowDefinition);
            string yamlContent = ConvertJsonToYaml(json);
            return Results.Ok(new WorkflowContent(builtIn.Name, yamlContent));
        }

        return Results.NotFound(new ErrorResponse($"Workflow '{name}' not found."));
    }

    // ── 3. PUT /{name} — Update workflow ────────────────────────────────────

    private static async Task<IResult> UpdateWorkflow(
        string store, string name, WorkflowUpdateRequest req,
        RequestContext ctx, CancellationToken ct)
    {
        if (!ctx.HasPermission("store"))
            return Results.Json(new ErrorResponse("Permission 'store' required."), statusCode: 403);

        if (!SafeNamePattern.IsMatch(name))
            return Results.BadRequest(new ErrorResponse("Invalid workflow name."));

        if (string.IsNullOrWhiteSpace(req.YamlContent))
            return Results.BadRequest(new ErrorResponse("yamlContent is required."));

        if (System.Text.Encoding.UTF8.GetByteCount(req.YamlContent) > 65_536)
            return Results.BadRequest(new ErrorResponse("YAML content exceeds 64 KB limit."));

        // Parse YAML/JSON to validate
        WorkflowDefinition? parsed;
        try
        {
            var yamlDeserializer = new DeserializerBuilder().Build();
            var yamlObj = yamlDeserializer.Deserialize<object>(req.YamlContent);
            var jsonSerializer = new SerializerBuilder().JsonCompatible().Build();
            string json = jsonSerializer.Serialize(yamlObj);
            parsed = JsonSerializer.Deserialize(json,
                PlanningJsonContext.Default.WorkflowDefinition);

            if (parsed is null)
                return Results.BadRequest(new ErrorResponse("Workflow definition deserialized to null."));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new ErrorResponse($"Failed to parse workflow: {ex.Message}"));
        }

        // Validate
        var errors = WorkflowDefinition.Validate(parsed);
        if (errors.Count > 0)
            return Results.BadRequest(new ErrorResponse(
                $"Validation failed: {string.Join("; ", errors)}"));

        // Write to .scrinia/workflows/{name}.json
        string baseDir = GetScriniaBaseDir(ctx.Store!);
        string workflowsDir = Path.Combine(baseDir, "workflows");
        Directory.CreateDirectory(workflowsDir);

        string filePath = Path.Combine(workflowsDir, $"{name}.json");
        string jsonOutput = JsonSerializer.Serialize(parsed,
            PlanningJsonContext.Default.WorkflowDefinition);
        await File.WriteAllTextAsync(filePath, jsonOutput, ct);

        return Results.Ok(new { message = $"Workflow '{name}' saved." });
    }

    // ── 4. GET /goals — List goals ──────────────────────────────────────────

    private static async Task<IResult> ListGoals(
        string store, RequestContext ctx, CancellationToken ct)
    {
        if (!ctx.HasPermission("read"))
            return Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403);

        string contextText;
        try
        {
            contextText = await ReadMemoryAsync(ctx.Store!, "project:context", ct);
        }
        catch (FileNotFoundException)
        {
            return Results.Ok(new GoalListResponse([]));
        }

        var goalLines = ParseGoalLines(contextText);
        var goals = new List<GoalSummary>();

        foreach (string line in goalLines)
        {
            var match = BracketedGoalIdPattern.Match(line);
            if (!match.Success) continue;

            string goalId = $"G-{match.Groups[1].Value}";

            // Extract status: [active], [complete], [abandoned], etc.
            string status = "unknown";
            if (line.Contains("[active]", StringComparison.OrdinalIgnoreCase))
                status = "active";
            else if (line.Contains("[complete]", StringComparison.OrdinalIgnoreCase))
                status = "complete";
            else if (line.Contains("[abandoned]", StringComparison.OrdinalIgnoreCase))
                status = "abandoned";

            // Extract description: everything after status markers
            string description = ExtractGoalDescription(line);

            // Calculate progress
            int progress = int.TryParse(
                ScriniaProjectTools.CalculateProgress(ctx.Store!, goalId), out int p) ? p : 0;

            // Resolve workflow reference
            string? workflowRef = ScriniaProjectTools.ResolveGoalWorkflowName(ctx.Store!, goalId);

            goals.Add(new GoalSummary(goalId, description, status, workflowRef, progress));
        }

        return Results.Ok(new GoalListResponse(goals.ToArray()));
    }

    // ── 5. GET /goals/{goalId} — Goal detail with phase-grouped tasks ───────

    private static async Task<IResult> GetGoalDetail(
        string store, string goalId, RequestContext ctx, CancellationToken ct)
    {
        if (!ctx.HasPermission("read"))
            return Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403);

        // Load goal metadata from context
        string description = "";
        string status = "unknown";
        string? workflowRef = null;

        try
        {
            string contextText = await ReadMemoryAsync(ctx.Store!, "project:context", ct);
            var goalLines = ParseGoalLines(contextText);
            foreach (string line in goalLines)
            {
                var match = BracketedGoalIdPattern.Match(line);
                if (!match.Success) continue;
                string lineGoalId = $"G-{match.Groups[1].Value}";
                if (!string.Equals(lineGoalId, goalId, StringComparison.OrdinalIgnoreCase))
                    continue;

                description = ExtractGoalDescription(line);
                if (line.Contains("[active]", StringComparison.OrdinalIgnoreCase))
                    status = "active";
                else if (line.Contains("[complete]", StringComparison.OrdinalIgnoreCase))
                    status = "complete";
                else if (line.Contains("[abandoned]", StringComparison.OrdinalIgnoreCase))
                    status = "abandoned";
                break;
            }
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound(new ErrorResponse($"Goal '{goalId}' not found."));
        }

        // Load tasks for this goal
        var (taskScope, _) = ctx.Store!.ParseQualifiedName("task:placeholder");
        var allTasks = ctx.Store!.LoadIndex(taskScope);
        var goalTasks = allTasks
            .Where(e => ScriniaProjectTools.HasKeyword(e, $"goal:{goalId}"))
            .ToList();

        if (goalTasks.Count == 0 && string.IsNullOrEmpty(description))
            return Results.NotFound(new ErrorResponse($"Goal '{goalId}' not found."));

        // Group by phase
        var phaseGroups = new Dictionary<string, List<TaskSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in goalTasks)
        {
            string phaseId = entry.Keywords?
                .FirstOrDefault(k => k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase))
                ?["phase:".Length..] ?? "unknown";

            if (!phaseGroups.ContainsKey(phaseId))
                phaseGroups[phaseId] = [];

            phaseGroups[phaseId].Add(MapTaskEntry(entry));
        }

        var phases = phaseGroups
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PhaseGroup(kv.Key, kv.Value.ToArray()))
            .ToArray();

        int progress = int.TryParse(
            ScriniaProjectTools.CalculateProgress(ctx.Store!, goalId), out int p) ? p : 0;
        workflowRef = ScriniaProjectTools.ResolveGoalWorkflowName(ctx.Store!, goalId);

        return Results.Ok(new GoalDetailResponse(
            goalId, description, status, workflowRef, progress, phases));
    }

    // ── 6. GET /goals/{goalId}/tasks — Flat task list ───────────────────────

    private static Task<IResult> GetGoalTasks(
        string store, string goalId, RequestContext ctx)
    {
        if (!ctx.HasPermission("read"))
            return Task.FromResult<IResult>(
                Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403));

        var (taskScope, _) = ctx.Store!.ParseQualifiedName("task:placeholder");
        var allTasks = ctx.Store!.LoadIndex(taskScope);
        var goalTasks = allTasks
            .Where(e => ScriniaProjectTools.HasKeyword(e, $"goal:{goalId}"))
            .Select(MapTaskEntry)
            .ToArray();

        return Task.FromResult<IResult>(
            Results.Ok(new TaskListResponse(goalTasks)));
    }

    // ── 7. GET /goals/{goalId}/events — SSE stream ──────────────────────────

    private static Task<IResult> StreamGoalEvents(
        string store, string goalId,
        RequestContext ctx, TaskEventBroadcaster broadcaster)
    {
        if (!ctx.HasPermission("read"))
            return Task.FromResult<IResult>(
                Results.Json(new ErrorResponse("Permission 'read' required."), statusCode: 403));

        string subId = broadcaster.Subscribe();
        var reader = broadcaster.GetReader(subId);

        IResult result = new SseResult(async writer =>
        {
            try
            {
                await foreach (var evt in reader.ReadAllAsync())
                {
                    // Filter events to the requested goal
                    if (!string.Equals(evt.GoalId, goalId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string json = JsonSerializer.Serialize(evt,
                        ServerJsonContext.Default.TaskEvent);
                    await writer.WriteAsync($"data: {json}\n\n");
                    await writer.FlushAsync();
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                broadcaster.Unsubscribe(subId);
            }
        });

        return Task.FromResult(result);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TaskSummary MapTaskEntry(ArtifactEntry entry)
    {
        string status = entry.Keywords?
            .FirstOrDefault(k => k.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
            ?["status:".Length..] ?? "pending";

        int wave = 0;
        string? waveStr = entry.Keywords?
            .FirstOrDefault(k => k.StartsWith("wave:", StringComparison.OrdinalIgnoreCase));
        if (waveStr is not null && int.TryParse(waveStr["wave:".Length..], out int w))
            wave = w;

        string? skill = entry.Keywords?
            .FirstOrDefault(k => k.StartsWith("skill:", StringComparison.OrdinalIgnoreCase))
            ?["skill:".Length..];

        string? gateType = entry.Keywords?
            .FirstOrDefault(k => k.StartsWith("gate:", StringComparison.OrdinalIgnoreCase))
            ?["gate:".Length..];

        var dependsOn = entry.Keywords?
            .Where(k => k.StartsWith("depends:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k["depends:".Length..])
            .ToArray() ?? [];

        return new TaskSummary(
            entry.Name,
            status,
            wave,
            skill,
            dependsOn,
            gateType,
            entry.Description);
    }

    private static async Task<string> ReadMemoryAsync(
        IMemoryStore store, string qualifiedName, CancellationToken ct)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName, ct);
        byte[] decoded = Nmp2Strategy.Instance.Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    private static List<string> ParseGoalLines(string contextText)
    {
        var goals = new List<string>();
        var lines = contextText.Split('\n');
        bool inGoalsSection = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (!inGoalsSection)
            {
                if (GoalsSectionPattern.IsMatch(trimmed) ||
                    GoalsSectionAltPattern.IsMatch(trimmed))
                {
                    inGoalsSection = true;
                }
            }
            else
            {
                if (SectionHeadingPattern.IsMatch(trimmed))
                    break;

                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                    goals.Add(trimmed);
            }
        }

        return goals;
    }

    private static string ExtractGoalDescription(string goalLine)
    {
        // Remove leading "- " or "* "
        string text = goalLine.TrimStart('-', '*', ' ');

        // Remove bracketed tokens like [G-59-72c] [active] etc.
        text = Regex.Replace(text, @"\[[^\]]*\]", "").Trim();

        // Clean up extra whitespace
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return text;
    }

    private static string GetScriniaBaseDir(IMemoryStore store)
    {
        string storeDir = store.GetStoreDirForScope("local");
        var dir = new DirectoryInfo(storeDir);
        while (dir is not null && dir.Name != ".scrinia")
            dir = dir.Parent;
        return dir?.FullName ?? Path.GetDirectoryName(storeDir) ?? storeDir;
    }

    private static string ConvertJsonToYaml(string json)
    {
        var yamlDeserializer = new DeserializerBuilder().Build();
        var yamlSerializer = new SerializerBuilder().Build();
        // Parse JSON via YamlDotNet (it handles JSON as a YAML subset)
        var obj = yamlDeserializer.Deserialize<object>(json);
        return yamlSerializer.Serialize(obj ?? new object());
    }
}
