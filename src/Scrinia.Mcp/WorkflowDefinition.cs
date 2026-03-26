using System.Text.Json;
using YamlDotNet.Serialization;

namespace Scrinia.Mcp;

// ── File metadata records ───────────────────────────────────────────────────

/// <summary>Metadata sidecar for a skill file on disk.</summary>
public record SkillFileMeta(
    string? BasedOn,
    string? Role,
    string[]? Capabilities,
    string? Scaffold,
    string? CreatedAt,
    string? UpdatedAt);

/// <summary>Metadata sidecar for a workflow file on disk.</summary>
public record WorkflowFileMeta(
    string? BasedOn,
    string? CreatedAt,
    string? UpdatedAt);

/// <summary>Metadata sidecar for an agent config file on disk.</summary>
public record AgentFileMeta(
    string? CreatedAt,
    string? UpdatedAt);

// ── Workflow definition records ─────────────────────────────────────────────

/// <summary>Validation check performed when a gate task is completed.</summary>
public record GateValidation(
    string CheckType,              // "memory-exists", "index-prefix", "index-no-gate", "filesystem-glob"
    string Target,                 // e.g., "qa:latest", "learn:retro-{goalShort}-*", "research:{goalShort}*"
    string ErrorTemplate,          // error description only (no instruction prefix)
    string? InstructionTemplate = null // instruction to emit via WithInstruction()
);

/// <summary>A single activity in a workflow definition.</summary>
public record WorkflowActivity(
    string Id,                      // e.g., "researcher", "qa-gate"
    string? Phase,                  // "00" for seeds, null for post-plan (assigned dynamically)
    int? Wave,                      // 0, 1, 2 for seeds; null for post-plan (computed by topo sort)
    string? Skill,                  // e.g., "builtin:researcher", null if no skill
    string[] DependsOn,             // activity IDs; "*" means "all user tasks"
    string Tag,                     // keyword value: "researcher", "qa", etc.
    string Prompt,                  // the task instruction text
    GateValidation? Validation,     // null for activities that don't gate on completion
    GateValidation[]? RequiredOutputs = null, // outputs the activity must produce before completing
    string Type = "agent",          // "agent", "spawner", or "system"
    string Role = "seed",           // "seed" or "post-plan"
    Dictionary<string, string>? Config = null // configuration for system activities
);

/// <summary>Declarative workflow: activities define the full pipeline from goal creation through completion.</summary>
public record WorkflowDefinition(
    string Name,
    WorkflowActivity[] Activities   // all activities: seeds (phase 00) and post-plan (injected after planning)
)
{
    // ── Computed helpers ─────────────────────────────────────────────────────

    /// <summary>Activities with role "seed" — created at goal creation (phase 00).</summary>
    public WorkflowActivity[] SeedActivities => Activities.Where(a => string.Equals(a.Role, "seed", StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>Activities with role "post-plan" — injected by PlanTasks after planning.</summary>
    public WorkflowActivity[] PostPlanActivities => Activities.Where(a => string.Equals(a.Role, "post-plan", StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>The spawner activity (type "spawner"), if any.</summary>
    public WorkflowActivity? SpawnerActivity => Activities.FirstOrDefault(a => string.Equals(a.Type, "spawner", StringComparison.OrdinalIgnoreCase));

    // ── Built-in workflow loading from embedded YAML ──────────────────────────

    private static WorkflowDefinition LoadEmbeddedWorkflow(string name)
    {
        string yaml = EmbeddedPrompts.Load($"workflows/{name}.yaml")
            ?? throw new InvalidOperationException($"Built-in {name} workflow not found in embedded resources");
        // AOT pipeline: YAML → object → JSON string → source-gen deserialize
        var deserializer = new DeserializerBuilder().Build();
        var obj = deserializer.Deserialize<object>(yaml);
        var jsonSerializer = new SerializerBuilder().JsonCompatible().Build();
        string json = jsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowDefinition)
            ?? throw new InvalidOperationException($"Failed to deserialize {name} workflow");
    }

    private static readonly Lazy<WorkflowDefinition> _defaultGoalWorkflow = new(() =>
        LoadEmbeddedWorkflow("goal-execution"));

    private static readonly Lazy<WorkflowDefinition> _quickFixWorkflow = new(() =>
        LoadEmbeddedWorkflow("quick-fix"));

    /// <summary>The default goal-execution workflow encoding the current pipeline.</summary>
    public static WorkflowDefinition DefaultGoalWorkflow => _defaultGoalWorkflow.Value;

    /// <summary>Lightweight workflow for bug fixes and quick changes — skips auditor and heavy gates.</summary>
    public static WorkflowDefinition QuickFixWorkflow => _quickFixWorkflow.Value;

    // ── Validation ──────────────────────────────────────────────────────────

    private static readonly HashSet<string> ValidCheckTypes =
        ["memory-exists", "index-prefix", "index-no-gate", "filesystem-glob"];

    private static readonly HashSet<string> ValidTypes = ["agent", "spawner", "system"];
    private static readonly HashSet<string> ValidRoles = ["seed", "post-plan"];

    /// <summary>
    /// Validates a workflow definition, returning a list of error messages.
    /// An empty list means the definition is valid.
    /// </summary>
    public static List<string> Validate(WorkflowDefinition wf)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(wf.Name))
            errors.Add("Name is required and must be non-empty.");

        if (wf.Activities is null || wf.Activities.Length == 0)
            errors.Add("Activities is required and must contain at least one activity.");

        var allActivities = wf.Activities ?? [];

        // At least one seed activity
        if (!allActivities.Any(a => string.Equals(a.Role, "seed", StringComparison.OrdinalIgnoreCase)))
            errors.Add("At least one activity with role 'seed' is required.");

        // At most one spawner
        int spawnerCount = allActivities.Count(a => string.Equals(a.Type, "spawner", StringComparison.OrdinalIgnoreCase));
        if (spawnerCount > 1)
            errors.Add($"At most one activity with type 'spawner' is allowed (found {spawnerCount}).");

        // Per-activity required fields
        for (int i = 0; i < allActivities.Length; i++)
        {
            var a = allActivities[i];
            string label = $"Activities[{i}]";

            if (string.IsNullOrWhiteSpace(a.Id))
                errors.Add($"{label}: Id is required and must be non-empty.");
            if (string.IsNullOrWhiteSpace(a.Prompt))
                errors.Add($"{label} ('{a.Id}'): Prompt is required and must be non-empty.");
            if (string.IsNullOrWhiteSpace(a.Tag))
                errors.Add($"{label} ('{a.Id}'): Tag is required and must be non-empty.");
            if (!ValidTypes.Contains(a.Type?.ToLowerInvariant() ?? ""))
                errors.Add($"{label} ('{a.Id}'): Type '{a.Type}' is invalid. Must be one of: agent, spawner, system.");
            if (!ValidRoles.Contains(a.Role?.ToLowerInvariant() ?? ""))
                errors.Add($"{label} ('{a.Id}'): Role '{a.Role}' is invalid. Must be one of: seed, post-plan.");
        }

        // ID uniqueness
        var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in allActivities)
        {
            if (!string.IsNullOrWhiteSpace(a.Id) && !allIds.Add(a.Id))
                duplicates.Add(a.Id);
        }
        foreach (var dup in duplicates)
            errors.Add($"Duplicate activity ID '{dup}' — all IDs must be unique across all activities.");

        // Structural: Seeds must have Phase+Wave, post-plan must not
        for (int i = 0; i < allActivities.Length; i++)
        {
            var a = allActivities[i];
            if (string.Equals(a.Role, "seed", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(a.Phase))
                    errors.Add($"Activities[{i}] ('{a.Id}'): Phase is required for seed activities.");
                if (a.Wave is null)
                    errors.Add($"Activities[{i}] ('{a.Id}'): Wave is required for seed activities.");
            }
            else if (string.Equals(a.Role, "post-plan", StringComparison.OrdinalIgnoreCase))
            {
                if (a.Phase is not null)
                    errors.Add($"Activities[{i}] ('{a.Id}'): Phase must be null for post-plan activities (assigned dynamically).");
                if (a.Wave is not null)
                    errors.Add($"Activities[{i}] ('{a.Id}'): Wave must be null for post-plan activities (computed by topo sort).");
            }
        }

        // DependsOn refs must resolve to known IDs or "*"
        foreach (var a in allActivities)
        {
            if (a.DependsOn is null) continue;
            foreach (var dep in a.DependsOn)
            {
                if (dep == "*") continue;
                if (!allIds.Contains(dep))
                    errors.Add($"Activity '{a.Id}': DependsOn references unknown ID '{dep}'.");
            }
        }

        // CheckType validation
        foreach (var a in allActivities)
        {
            if (a.Validation is null) continue;
            if (!ValidCheckTypes.Contains(a.Validation.CheckType))
                errors.Add($"Activity '{a.Id}': Validation.CheckType '{a.Validation.CheckType}' is invalid. " +
                           $"Must be one of: {string.Join(", ", ValidCheckTypes.Order())}.");
        }

        // RequiredOutputs validation
        foreach (var a in allActivities)
        {
            if (a.RequiredOutputs is null or { Length: 0 }) continue;
            for (int j = 0; j < a.RequiredOutputs.Length; j++)
            {
                var ro = a.RequiredOutputs[j];
                if (!ValidCheckTypes.Contains(ro.CheckType))
                    errors.Add($"Activity '{a.Id}': RequiredOutputs[{j}].CheckType '{ro.CheckType}' is invalid. " +
                               $"Must be one of: {string.Join(", ", ValidCheckTypes.Order())}.");
                if (string.IsNullOrWhiteSpace(ro.Target))
                    errors.Add($"Activity '{a.Id}': RequiredOutputs[{j}].Target must be non-empty.");
                if (string.IsNullOrWhiteSpace(ro.ErrorTemplate))
                    errors.Add($"Activity '{a.Id}': RequiredOutputs[{j}].ErrorTemplate must be non-empty.");
            }
        }

        // DAG cycle detection (topological sort)
        var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in allActivities)
        {
            if (string.IsNullOrWhiteSpace(a.Id)) continue;
            adj[a.Id] = (a.DependsOn ?? [])
                .Where(d => d != "*" && allIds.Contains(d))
                .ToList();
        }

        var reverseAdj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in adj.Keys) reverseAdj[id] = [];
        foreach (var (node, deps) in adj)
            foreach (var dep in deps)
                if (reverseAdj.ContainsKey(dep))
                    reverseAdj[dep].Add(node);

        var inDeg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in adj.Keys)
            inDeg[id] = adj[id].Count;

        var queue = new Queue<string>();
        foreach (var (id, deg) in inDeg)
            if (deg == 0) queue.Enqueue(id);

        int visited = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            visited++;
            foreach (var dependent in reverseAdj[current])
            {
                inDeg[dependent]--;
                if (inDeg[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (visited < adj.Count)
            errors.Add("Dependency cycle detected — DependsOn references form a cycle. All dependencies must form a DAG.");

        return errors;
    }
}
