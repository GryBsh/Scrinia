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
    /// <summary>Thin dispatcher for task operations — delegates to TaskNext/TaskComplete.</summary>
    [McpServerTool(Name = "task"), Description(
        "Task operations. Actions: 'next' (get next pending task), " +
        "'complete' (mark task done), 'plan' (decompose phase into tasks). " +
        "Use path parameter: next '/goal/G-1', complete '/task/g1-01-1-01', plan '/goal/G-1/phase/01'.")]
    public async Task<string> TaskDispatch(
        [Description("Action: 'next', 'complete', or 'plan'.")] string action,
        [Description("Path (next: '/goal/G-1', complete: '/task/g1-01-1-01', plan: '/goal/G-1/phase/01').")] string? path = null,
        [Description("Phase ID (legacy — prefer path).")] string? phaseId = null,
        [Description("Task name to complete (legacy — prefer path).")] string? taskName = null,
        [Description("Outcome description (complete).")] string? outcome = null,
        [Description("Free-text task definitions (plan).")] string? tasks = null,
        CancellationToken cancellationToken = default)
    {
        // Path resolution: extract goalId, phaseId, or taskName from path
        if (path is not null)
        {
            string p = path.TrimStart('/');

            if (p.StartsWith("task/", StringComparison.OrdinalIgnoreCase))
            {
                // /task/g1-01-1-01 → taskName = "task:g1-01-1-01"
                taskName ??= "task:" + p["task/".Length..];
            }
            else if (p.StartsWith("goal/", StringComparison.OrdinalIgnoreCase))
            {
                string remainder = p["goal/".Length..];
                // Check for /goal/G-X/phase/NN
                int phaseIdx = remainder.IndexOf("/phase/", StringComparison.OrdinalIgnoreCase);
                if (phaseIdx >= 0)
                {
                    phaseId ??= remainder[(phaseIdx + "/phase/".Length)..];
                }
                // The goal ID part is used to scope task('next') — store it for TaskNext
                // For now, TaskNext already uses GetActiveGoalIdAsync() which reads project:state
                // The path provides the goal context but TaskNext's internal logic handles scoping
            }
        }

        string act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "next":
                if (string.IsNullOrWhiteSpace(phaseId))
                {
                    // Auto-detect phase from active goal's pending tasks
                    try
                    {
                        var store = CurrentStore;
                        string? activeGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
                        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
                        var allEntries = store.LoadIndex(taskScope);
                        var pendingEntries = allEntries
                            .Where(e => HasKeyword(e, "status:pending"))
                            .Where(e => activeGoalId is null || HasKeyword(e, $"goal:{activeGoalId}"));

                        // Find first phase with pending tasks
                        foreach (var entry in pendingEntries)
                        {
                            var phaseKw = entry.Keywords?.FirstOrDefault(k =>
                                k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase));
                            if (phaseKw is not null)
                            {
                                phaseId = phaseKw["phase:".Length..];
                                break;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(phaseId))
                            return ResponseBuilder.Success("No pending tasks found for the active goal.").WithAction("listed").ToYaml();
                    }
                    catch { return ResponseBuilder.Error("Could not auto-detect phase. Provide phaseId parameter.").ToYaml(); }
                }
                return await TaskNext(phaseId, cancellationToken);

            case "complete":
                if (string.IsNullOrWhiteSpace(taskName))
                    return ResponseBuilder.Error("task('complete') requires 'taskName' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(outcome))
                    return ResponseBuilder.Error("task('complete') requires 'outcome' parameter.").ToYaml();
                return await TaskComplete(taskName, outcome, cancellationToken);

            case "plan":
                if (string.IsNullOrWhiteSpace(phaseId))
                    return ResponseBuilder.Error("task('plan') requires 'phaseId' parameter.").ToYaml();
                if (string.IsNullOrWhiteSpace(tasks))
                    return ResponseBuilder.Error("task('plan') requires 'tasks' parameter.").ToYaml();
                return await PlanTasks(phaseId, tasks, cancellationToken);

            default:
                return ResponseBuilder.Error($"Unknown action '{action}'. Valid actions: 'next', 'complete', 'plan'.").ToYaml();
        }
    }

    /// <summary>Returns all unblocked tasks in the current wave for a phase.</summary>
    internal static async Task<string> TaskNext(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Get task scope via ParseQualifiedName — "local-topic:task" scope
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");

        // Keyword-only scan — no ResolveArtifactAsync during filtering
        var allEntries = store.LoadIndex(taskScope);

        // Resolve active goal to scope tasks
        string? activeGoalId = await GetActiveGoalIdAsync(store, cancellationToken);

        // Filter to tasks for this phase, scoped to active goal if one exists
        var phaseEntries = allEntries
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
            .Where(e => activeGoalId is null || HasKeyword(e, $"goal:{activeGoalId}"))
            .ToList();

        if (phaseEntries.Count == 0)
            return ResponseBuilder.Success($"No pending tasks for phase {phaseId}.").WithAction("listed").ToYaml();

        // Find pending entries
        var pendingEntries = phaseEntries
            .Where(e => HasKeyword(e, "status:pending"))
            .ToList();

        if (pendingEntries.Count == 0)
            return ResponseBuilder.Success($"No pending tasks for phase {phaseId}.").WithAction("listed").ToYaml();

        // Find the lowest wave among pending entries
        int currentWave = pendingEntries.Min(e => ParseWave(e));

        // Filter pending to current wave only
        var currentWaveEntries = pendingEntries
            .Where(e => ParseWave(e) == currentWave)
            .ToList();

        // Build set of completed task names for dependency checking
        var completedNames = allEntries
            .Where(e => HasKeyword(e, "status:complete"))
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filter to unblocked: all depends_on names must be in completedNames
        var unblockedEntries = currentWaveEntries
            .Where(e => GetDependencies(e).All(dep => completedNames.Contains(dep)))
            .ToList();

        if (unblockedEntries.Count == 0)
            return ResponseBuilder.Success($"No unblocked tasks for phase {phaseId} in wave {currentWave}. Some tasks may be waiting on dependencies.").WithAction("listed").ToYaml();

        // Build response: read artifact content only for unblocked tasks
        string tnInstruction = unblockedEntries.Count > 1
            ? $"these {unblockedEntries.Count} tasks are independent — spawn a parallel agent for each task."
            : "spawn an agent for this task — keep the primary agent available for SOS and user interaction.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Phase {phaseId} — Wave {currentWave} — {unblockedEntries.Count} unblocked task(s):");
        sb.AppendLine();

        foreach (var entry in unblockedEntries)
        {
            string qualifiedName = $"task:{entry.Name}";
            string content;
            try
            {
                content = await ReadMemoryAsync(store, qualifiedName, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                content = "(content not found)";
            }

            sb.AppendLine($"## {qualifiedName}");
            sb.AppendLine(content);
            sb.AppendLine();

            // Truncate early if getting close to limit
            if (sb.Length > MaxResponseChars - 200)
            {
                sb.AppendLine("[... truncated to 8KB limit]");
                break;
            }
        }

        // Update project state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? $"Phase {phaseId}";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: CalculateProgress(store, activeGoalId),
            lastAction: $"task('next') called for phase {phaseId} wave {currentWave}",
            blockers: "none",
            nextStep: unblockedEntries.Count > 1
                ? $"spawn {unblockedEntries.Count} parallel agents for wave {currentWave} tasks, then call task('complete', {{ path: '/task/...', outcome: '...' }}) for each"
                : $"spawn agent for wave {currentWave} task, then call task('complete', {{ path: '/task/...', outcome: '...' }})",
            cancellationToken);

        return ResponseBuilder.Success(sb.ToString().TrimEnd())
            .WithAction("listed")
            .WithInstruction(tnInstruction)
            .ToYaml();
    }

    /// <summary>Mark a task complete with outcome metadata. Appends to execution log.</summary>
    internal static async Task<string> TaskComplete(
        [Description("Qualified task name (e.g. 'task:01-1-01').")] string taskName,
        [Description("Free-text describing what was done, any deviations or outcomes.")] string outcome,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Parse task name to get scope and subject
        var (scope, subject) = store.ParseQualifiedName(taskName);

        // Load index and find the existing task entry
        var allEntries = store.LoadIndex(scope);
        var existing = allEntries.FirstOrDefault(e => e.Name == subject);

        if (existing is null)
            return ResponseBuilder.Error($"Task '{taskName}' not found.").ToYaml();

        // Gate task validation — data-driven dispatch from WorkflowDefinition (override-aware)
        if (existing.Keywords is not null)
        {
            string? activeGoal = await GetActiveGoalIdAsync(store, cancellationToken);
            string goalWorkflowName = ResolveGoalWorkflowName(store, activeGoal);
            var (workflow, _) = await ResolveWorkflowAsync(store, goalWorkflowName, cancellationToken);

            // Compute goalShort once for template substitution
            string goalShort = "";
            if (activeGoal is not null)
            {
                var gm = GoalIdPattern.Match(activeGoal);
                if (gm.Success) goalShort = $"g{gm.Groups[1].Value}";
            }

            foreach (var kw in existing.Keywords.Where(k => k.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) || k.StartsWith("gate:", StringComparison.OrdinalIgnoreCase)))
            {
                string gateType = kw.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)
                    ? kw["tag:".Length..]
                    : kw["gate:".Length..];

                // Find matching activity in workflow definition
                var activity = workflow.SeedActivities
                    .Concat(workflow.PostPlanActivities)
                    .FirstOrDefault(a => string.Equals(a.Tag, gateType, StringComparison.OrdinalIgnoreCase));

                // Unknown gate type → pass through (WF-09 backward compat)
                if (activity?.Validation is null)
                    continue;

                var validation = activity.Validation;
                string target = SubstituteGoalShort(validation.Target, goalShort);
                string errorMessage = SubstituteGoalShort(validation.ErrorTemplate, goalShort);
                string? instructionMessage = validation.InstructionTemplate is not null
                    ? SubstituteGoalShort(validation.InstructionTemplate, goalShort)
                    : null;
                string? validationError = null;

                try
                {
                    switch (validation.CheckType)
                    {
                        case "memory-exists":
                            try { await ReadMemoryAsync(store, target, cancellationToken); }
                            catch (FileNotFoundException) { validationError = errorMessage; }
                            break;

                        case "index-prefix":
                        {
                            int colonIdx = target.IndexOf(':');
                            string topic = colonIdx >= 0 ? target[..colonIdx] : target;
                            string prefix = colonIdx >= 0 ? target[(colonIdx + 1)..] : "";
                            var (indexScope, _) = store.ParseQualifiedName($"{topic}:placeholder");
                            var entries = store.LoadIndex(indexScope);
                            bool found = entries.Any(e =>
                                string.IsNullOrEmpty(prefix) ||
                                e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                            if (!found)
                                validationError = errorMessage;
                            break;
                        }

                        case "index-no-gate":
                        {
                            int ngColonIdx = target.IndexOf(':');
                            string ngTopic = ngColonIdx >= 0 ? target[..ngColonIdx] : target;
                            var (ngScope, _) = store.ParseQualifiedName($"{ngTopic}:placeholder");
                            var ngEntries = store.LoadIndex(ngScope);
                            bool hasExecutionTasks = ngEntries.Any(e =>
                                (string.IsNullOrEmpty(goalShort) || HasKeyword(e, $"goal:{activeGoal}")) &&
                                e.Keywords is not null && !e.Keywords.Any(k => k.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) || k.StartsWith("gate:", StringComparison.OrdinalIgnoreCase)));
                            if (!hasExecutionTasks)
                                validationError = errorMessage;
                            break;
                        }

                        case "filesystem-glob":
                        {
                            string fsStoreDir = store.GetStoreDirForScope("local");
                            string fsScriniaDir = Path.GetDirectoryName(fsStoreDir) ?? fsStoreDir;
                            string workspaceRoot = Path.GetDirectoryName(fsScriniaDir) ?? fsScriniaDir;
                            // target is e.g. "docs/reports/*.md" — split into directory and file pattern
                            string targetNormalized = target.Replace('/', Path.DirectorySeparatorChar);
                            string globDir = Path.Combine(workspaceRoot, Path.GetDirectoryName(targetNormalized) ?? "");
                            string globPattern = Path.GetFileName(targetNormalized);
                            if (!Directory.Exists(globDir) || !Directory.EnumerateFiles(globDir, globPattern).Any())
                                validationError = errorMessage;
                            break;
                        }
                    }
                }
                catch { /* best-effort validation — swallow exceptions like the original */ }

                if (validationError is not null)
                {
                    var gateResponse = ResponseBuilder.Error($"gate '{gateType}' validation failed — {validationError}");
                    if (instructionMessage is not null)
                        gateResponse = gateResponse.WithInstruction(instructionMessage);
                    return gateResponse.ToYaml();
                }

                // RequiredOutputs validation (in addition to gate validation)
                if (activity?.RequiredOutputs is { Length: > 0 } requiredOutputs)
                {
                    foreach (var req in requiredOutputs)
                    {
                        string reqTarget = SubstituteGoalShort(req.Target, goalShort);
                        string? reqError = null;

                        try
                        {
                            switch (req.CheckType?.ToLowerInvariant())
                            {
                                case "memory-exists":
                                    try { await ReadMemoryAsync(store, reqTarget, cancellationToken); }
                                    catch (FileNotFoundException) { reqError = SubstituteGoalShort(req.ErrorTemplate, goalShort); }
                                    break;

                                case "index-prefix":
                                {
                                    int roColonIdx = reqTarget.IndexOf(':');
                                    string roTopic = roColonIdx >= 0 ? reqTarget[..roColonIdx] : reqTarget;
                                    string roPrefix = roColonIdx >= 0 ? reqTarget[(roColonIdx + 1)..] : "";
                                    var (roScope, _) = store.ParseQualifiedName($"{roTopic}:placeholder");
                                    var roEntries = store.LoadIndex(roScope);
                                    bool roFound = roEntries.Any(e =>
                                        string.IsNullOrEmpty(roPrefix) ||
                                        e.Name.StartsWith(roPrefix, StringComparison.OrdinalIgnoreCase));
                                    if (!roFound)
                                        reqError = SubstituteGoalShort(req.ErrorTemplate, goalShort);
                                    break;
                                }

                                case "index-no-gate":
                                {
                                    int roNgColonIdx = reqTarget.IndexOf(':');
                                    string roNgTopic = roNgColonIdx >= 0 ? reqTarget[..roNgColonIdx] : reqTarget;
                                    var (roNgScope, _) = store.ParseQualifiedName($"{roNgTopic}:placeholder");
                                    var roNgEntries = store.LoadIndex(roNgScope);
                                    bool roHasExecTasks = roNgEntries.Any(e =>
                                        (string.IsNullOrEmpty(goalShort) || HasKeyword(e, $"goal:{activeGoal}")) &&
                                        e.Keywords is not null && !e.Keywords.Any(k => k.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) || k.StartsWith("gate:", StringComparison.OrdinalIgnoreCase)));
                                    if (!roHasExecTasks)
                                        reqError = SubstituteGoalShort(req.ErrorTemplate, goalShort);
                                    break;
                                }

                                case "filesystem-glob":
                                {
                                    string roFsStoreDir = store.GetStoreDirForScope("local");
                                    string roFsScriniaDir = Path.GetDirectoryName(roFsStoreDir) ?? roFsStoreDir;
                                    string roWorkspaceRoot = Path.GetDirectoryName(roFsScriniaDir) ?? roFsScriniaDir;
                                    string roTargetNormalized = reqTarget.Replace('/', Path.DirectorySeparatorChar);
                                    string roGlobDir = Path.Combine(roWorkspaceRoot, Path.GetDirectoryName(roTargetNormalized) ?? "");
                                    string roGlobPattern = Path.GetFileName(roTargetNormalized);
                                    if (!Directory.Exists(roGlobDir) || !Directory.EnumerateFiles(roGlobDir, roGlobPattern).Any())
                                        reqError = SubstituteGoalShort(req.ErrorTemplate, goalShort);
                                    break;
                                }
                            }
                        }
                        catch { /* best-effort validation — swallow exceptions */ }

                        if (reqError is not null)
                        {
                            string reqInstruction = req.InstructionTemplate is not null
                                ? SubstituteGoalShort(req.InstructionTemplate, goalShort)
                                : "Produce the required output before completing this task.";
                            return ResponseBuilder.Error($"Required output check failed: {reqError}")
                                .WithInstruction(reqInstruction)
                                .ToYaml();
                        }
                    }
                }
            }
        }

        // Read task content to surface acceptance criteria
        string acceptanceCriteria = "";
        try
        {
            string taskContent = await ReadMemoryAsync(store, taskName, cancellationToken);
            int acIdx = taskContent.IndexOf("Acceptance criteria:", StringComparison.OrdinalIgnoreCase);
            if (acIdx >= 0)
                acceptanceCriteria = taskContent[(acIdx + "Acceptance criteria:".Length)..].Trim();
        }
        catch { /* best-effort */ }

        // Replace status keyword: remove status:* and add status:complete
        var newKeywords = (existing.Keywords ?? [])
            .Where(k => !k.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
            .Append("status:complete")
            .ToArray();

        // Update entry via record with-expression — DO NOT call ArchiveVersion
        var updated = existing with
        {
            Keywords = newKeywords,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        store.Upsert(updated, scope);

        // Append to execution log: task:{phaseId}-execution-log
        string phaseId = existing.Keywords?
            .FirstOrDefault(k => k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase))
            ?["phase:".Length..] ?? "unknown";

        string logName = $"task:{phaseId}-execution-log";
        string outcomeEntry = $"[{DateTimeOffset.UtcNow:o}] COMPLETE: {taskName}\n{outcome}";

        await AppendToExecutionLogAsync(store, logName, outcomeEntry, cancellationToken);

        // Auto-compact execution log if too large (COMPACT-01)
        string compactionNotice = "";
        try
        {
            var (logScope, logSubject) = store.ParseQualifiedName(logName);
            var logEntries = store.LoadIndex(logScope);
            var logEntry = logEntries.FirstOrDefault(e => e.Name == logSubject);
            if (logEntry is not null && logEntry.ChunkCount > 50)
            {
                const int keepRecent = 20;

                string artifact = await store.ReadArtifactAsync(logSubject, logScope, cancellationToken);
                int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);

                if (chunkCount > 50)
                {
                    // Archive the original before modifying
                    store.ArchiveVersion(logSubject, logScope);

                    // Keep last 20 chunks
                    int startChunk = chunkCount - keepRecent + 1;
                    var keptChunks = new string[keepRecent];
                    for (int i = 0; i < keepRecent; i++)
                        keptChunks[i] = Nmp2ChunkedEncoder.DecodeChunk(artifact, startChunk + i);

                    string compacted = Nmp2ChunkedEncoder.EncodeChunks(keptChunks);
                    await store.WriteArtifactAsync(logSubject, logScope, compacted, cancellationToken);

                    // Update index entry
                    long newBytes = System.Text.Encoding.UTF8.GetByteCount(
                        System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(compacted)));
                    store.Upsert(logEntry with
                    {
                        ChunkCount = keepRecent,
                        OriginalBytes = newBytes,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        ChunkEntries = null
                    }, logScope);

                    // COMPACT-02: notice for response
                    compactionNotice = $"\nExecution log auto-compacted: {chunkCount} → {keepRecent} chunks.";
                }
            }
        }
        catch { /* best-effort compaction */ }

        // Update project state with computed progress
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? $"Phase {phaseId}";

        // Compute progress from task data (scoped to active goal)
        string? tcGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        string progressPct = CalculateProgress(store, tcGoalId);

        // Check if this was the last pending task in the phase (scoped to goal)
        var updatedEntries = store.LoadIndex(scope);
        var goalScopedEntries = updatedEntries
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
            .Where(e => tcGoalId is null || HasKeyword(e, $"goal:{tcGoalId}"))
            .ToList();
        bool phaseComplete = !goalScopedEntries.Any(e => HasKeyword(e, "status:pending"));

        string nextStep;
        if (phaseComplete)
            nextStep = tcGoalId is not null
                ? $"all phase {phaseId} tasks complete — the QA gate task will handle verification, run task('next', {{ path: '/goal/{tcGoalId}' }}) to continue"
                : $"all phase {phaseId} tasks complete — the QA gate task will handle verification, run task('next', {{ path: '/goal/G-X' }}) to continue";
        else
        {
            var pendingCheck = goalScopedEntries
                .Where(e => HasKeyword(e, "status:pending"))
                .ToList();
            int thisWaveCheck = ParseWave(existing);
            int sameWaveCheck = pendingCheck.Count(e => ParseWave(e) == thisWaveCheck);
            nextStep = sameWaveCheck > 0
                ? $"keep {sameWaveCheck} remaining wave {thisWaveCheck} parallel agents running"
                : tcGoalId is not null
                    ? $"run task('next', {{ path: '/goal/{tcGoalId}' }}) to get the next wave's tasks"
                    : "run task('next', { path: '/goal/G-X' }) to get the next wave's tasks";
        }

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: progressPct,
            lastAction: $"Completed {taskName}",
            blockers: "none",
            nextStep: nextStep,
            cancellationToken);

        string tcContent;
        string? tcInstruction;
        var tcInfoItems = new List<string>();

        if (phaseComplete)
        {
            tcContent = $"Task '{taskName}' marked complete. All phase {phaseId} tasks done.";
            tcInstruction = tcGoalId is not null
                ? $"verify the work (run tests, review changes, confirm behavior), then call task('next', {{ path: '/goal/{tcGoalId}' }}) — the QA gate task will handle structured verification."
                : "verify the work (run tests, review changes, confirm behavior), then call task('next', { path: '/goal/G-X' }) — the QA gate task will handle structured verification.";
        }
        else
        {
            // Check remaining pending tasks — distinguish same-wave (parallel) from next-wave
            var pendingInPhase = goalScopedEntries
                .Where(e => HasKeyword(e, "status:pending"))
                .ToList();

            // Find the current task's wave to determine if remaining tasks are in the same wave
            int thisWave = ParseWave(existing);
            int sameWaveRemaining = pendingInPhase.Count(e => ParseWave(e) == thisWave);
            int totalRemaining = pendingInPhase.Count;

            if (sameWaveRemaining > 1)
            {
                tcContent = $"Task '{taskName}' marked complete. {sameWaveRemaining} tasks remaining in wave {thisWave}.";
                tcInstruction = "keep parallel agents running — call task('complete', { path: '/task/...', outcome: '...' }) for each as they finish.";
            }
            else if (sameWaveRemaining == 1)
            {
                tcContent = $"Task '{taskName}' marked complete. 1 task remaining in wave {thisWave}.";
                tcInstruction = null;
            }
            else
            {
                tcContent = $"Task '{taskName}' marked complete. Wave {thisWave} done.";
                tcInstruction = tcGoalId is not null
                    ? $"call task('next', {{ path: '/goal/{tcGoalId}' }}) to get wave {thisWave + 1} tasks ({totalRemaining} pending)."
                    : $"call task('next', {{ path: '/goal/G-X' }}) to get wave {thisWave + 1} tasks ({totalRemaining} pending).";
            }
        }

        if (!string.IsNullOrWhiteSpace(acceptanceCriteria))
            tcContent += $"\nAcceptance criteria for this task:\n{acceptanceCriteria}";

        // COMPACT-02: add compaction notice if triggered
        if (!string.IsNullOrEmpty(compactionNotice))
            tcInfoItems.Add(compactionNotice.Trim());

        var tcResponse = ResponseBuilder.Success(tcContent)
            .WithPath($"/task/{subject}")
            .WithAction("completed");
        if (tcInstruction is not null)
            tcResponse = tcResponse.WithInstruction(tcInstruction);
        if (tcInfoItems.Count > 0)
            tcResponse = tcResponse.WithInfo([.. tcInfoItems]);

        return tcResponse.ToYaml();
    }

    /// <summary>Decompose a phase into task memories with keyword-based metadata.</summary>
    internal static async Task<string> PlanTasks(
        [Description("Two-digit phase number (e.g. '01').")] string phaseId,
        [Description(
            "Free-text task definitions. Each task section uses this format:\n" +
            "## Task {id}\n" +
            "Depends on: {comma-separated task IDs, or 'none'}\n" +
            "Action: {what to do}\n" +
            "Acceptance criteria:\n" +
            "- criterion 1\n" +
            "- criterion 2\n" +
            "Waves are computed automatically from the dependency graph — no need to specify them.")] string tasks,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(phaseId))
            return ResponseBuilder.Error("phaseId is required.").ToYaml();

        var store = CurrentStore;

        // Parse task sections from free-text input
        var parsedTasks = ParseTaskSections(tasks);
        if (parsedTasks.Count == 0)
            return ResponseBuilder.Error("No tasks found. Provide tasks using '## Task {id}' section headers.").ToYaml();

        // Auto-inject gate tasks
        var allUserTaskIds = parsedTasks.Select(t => t.Id).ToArray();

        // Resolve active goal for task scoping (moved up so we can read its workflow keyword)
        string? activeGoalId = await GetActiveGoalIdAsync(store, cancellationToken);

        // Inject gate tasks from workflow definition (override-aware)
        string goalWorkflowName = ResolveGoalWorkflowName(store, activeGoalId);
        var (workflow, workflowWarning) = await ResolveWorkflowAsync(store, goalWorkflowName, cancellationToken);
        foreach (var gate in workflow.PostPlanActivities.Where(a => !string.Equals(a.Type, "spawner", StringComparison.OrdinalIgnoreCase)))
        {
            var deps = new List<string>();
            foreach (var dep in gate.DependsOn)
            {
                if (dep == "*")
                    deps.AddRange(allUserTaskIds);  // wildcard = all user tasks
                else
                    deps.Add(dep);  // other gate IDs (e.g., "qa-gate")
            }
            parsedTasks.Add(new ParsedTask(
                Id: gate.Id,
                DependsOn: deps.ToArray(),
                Content: gate.Prompt));
        }

        // Compute waves from the dependency graph (topological ordering)
        // Tasks with no dependencies → wave 1. Tasks depending on wave N tasks → wave N+1.
        var taskById = parsedTasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var computedWaves = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Also build a lookup including phaseId-prefixed IDs (e.g. "01-01" maps to task "01")
        foreach (var t in parsedTasks)
            computedWaves[t.Id] = 0; // sentinel

        bool changed = true;
        int iterations = 0;
        while (changed && iterations < 100) // safety limit
        {
            changed = false;
            iterations++;
            foreach (var task in parsedTasks)
            {
                int maxDepWave = 0;
                foreach (string dep in task.DependsOn)
                {
                    // Dependency might be "01-1-01" (full) or "01" (short ID)
                    // Try exact match first, then suffix match
                    string depId = dep;
                    if (!computedWaves.ContainsKey(depId))
                    {
                        // Try extracting just the last segment (task ID portion)
                        string[] parts = dep.Split('-');
                        depId = parts[^1];
                    }

                    if (computedWaves.TryGetValue(depId, out int depWave))
                        maxDepWave = Math.Max(maxDepWave, depWave);
                    else
                        maxDepWave = Math.Max(maxDepWave, 1); // unknown dep → assume wave 1
                }

                int newWave = maxDepWave + 1;
                if (computedWaves[task.Id] != newWave)
                {
                    computedWaves[task.Id] = newWave;
                    changed = true;
                }
            }
        }

        int waveCount = computedWaves.Values.DefaultIfEmpty(1).Max();
        var createdNames = new List<string>();

        // Compute goal prefix once before the loop
        string goalPrefix = "";
        if (activeGoalId is not null)
        {
            var m = GoalIdPattern.Match(activeGoalId);
            if (m.Success) goalPrefix = $"g{m.Groups[1].Value}-";
        }

        foreach (var task in parsedTasks)
        {
            int wave = computedWaves[task.Id];

            // Build keywords: status:pending, wave:N, phase:XX, goal:GN, depends_on:* entries
            var keywords = new List<string>
            {
                "status:pending",
                $"wave:{wave}",
                $"phase:{phaseId}"
            };
            if (activeGoalId is not null)
                keywords.Add($"goal:{activeGoalId}");
            foreach (string dep in task.DependsOn)
            {
                // Store full task name as dependency for reliable resolution.
                // If dep is a raw ID (e.g., "01"), resolve to full name "{goalPrefix}phaseId-wave-id".
                // If dep is already a full name (e.g., "g1-01-1-01"), pass through as-is.
                if (computedWaves.TryGetValue(dep, out int depWave))
                    keywords.Add($"depends_on:{goalPrefix}{phaseId}-{depWave}-{dep}");
                else
                    keywords.Add($"depends_on:{dep}"); // already full name or external ref
            }
            if (task.Files is { Length: > 0 })
            {
                foreach (string file in task.Files)
                    keywords.Add($"files:{file.Trim()}");
            }
            // Add tag: keyword from workflow activity definition, or infer from -gate suffix
            var matchingActivity = workflow.Activities.FirstOrDefault(a => string.Equals(a.Id, task.Id, StringComparison.OrdinalIgnoreCase));
            if (matchingActivity is not null)
                keywords.Add($"tag:{matchingActivity.Tag}");
            else if (task.Id.EndsWith("-gate", StringComparison.OrdinalIgnoreCase))
                keywords.Add($"tag:{task.Id.Replace("-gate", "", StringComparison.OrdinalIgnoreCase)}");

            // Extract REQ-IDs from task content for requirement traceability
            foreach (Match reqMatch in ReqIdPattern.Matches(task.Content))
            {
                string reqKw = $"req:{reqMatch.Groups[1].Value}";
                if (!keywords.Contains(reqKw, StringComparer.OrdinalIgnoreCase))
                    keywords.Add(reqKw);
            }

            // Task naming: task:{goalPrefix}{phaseId}-{wave}-{id}
            string taskNameStr = $"task:{goalPrefix}{phaseId}-{wave}-{task.Id}";

            await WritePlanningMemoryAsync(store, taskNameStr, task.Content,
                archiveExisting: false, keywords: [.. keywords], cancellationToken);

            createdNames.Add(taskNameStr);
        }

        // File-conflict detection: flag same-wave tasks that modify the same file
        var fileConflicts = new List<string>();
        var tasksByWave = parsedTasks
            .Where(t => t.Files is { Length: > 0 })
            .GroupBy(t => computedWaves[t.Id]);

        foreach (var waveGroup in tasksByWave)
        {
            var fileToTasks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in waveGroup)
            {
                foreach (var file in task.Files!)
                {
                    var trimmed = file.Trim();
                    if (!fileToTasks.ContainsKey(trimmed))
                        fileToTasks[trimmed] = new();
                    fileToTasks[trimmed].Add(task.Id);
                }
            }
            foreach (var (file, conflictTasks) in fileToTasks.Where(kv => kv.Value.Count > 1))
            {
                fileConflicts.Add($"Wave {waveGroup.Key}: {file} modified by tasks {string.Join(", ", conflictTasks)}. Use worktree isolation or re-sequence.");
            }
        }

        // Update project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string currentPhase = ExtractStateField(stateText, "Phase:") ?? "Not started";

        await WriteStateAsync(store, projectName, projectId,
            phase: currentPhase,
            progressPct: "30",
            lastAction: $"Tasks created for phase {phaseId} ({parsedTasks.Count} tasks, {waveCount} wave(s))",
            blockers: "none",
            nextStep: activeGoalId is not null
                ? $"run task('next', {{ path: '/goal/{activeGoalId}' }}) to get first task for phase {phaseId}"
                : $"run task('next', {{ path: '/goal/G-X' }}) to get first task for phase {phaseId}",
            cancellationToken);

        // Optionally surface learn:patterns as a hint for the task planner
        string patternNote = "";
        try
        {
            string patterns = await ReadMemoryAsync(store, "learn:patterns", cancellationToken);
            string hint = patterns.Length > 300 ? patterns[..300] + "..." : patterns;
            patternNote = $"\nPatterns from prior phases: {hint}";
        }
        catch { /* no learn:patterns yet — skip silently */ }

        int firstWaveCount = computedWaves.Values.Count(w => w == 1);
        string parallelHint = firstWaveCount > 1
            ? $" First wave has {firstWaveCount} independent tasks — spawn parallel agents, one per task."
            : "";

        // Check if agent:execution-policy exists — file first, NMP/2 fallback
        bool hasPolicy = false;
        string epBaseDir = GetScriniaBaseDir(store);
        string policyPath = Path.Combine(epBaseDir, "agent", "execution-policy.md");
        hasPolicy = File.Exists(policyPath);
        if (!hasPolicy)
        {
            // Fall back to NMP/2 check (legacy)
            try
            {
                var (epScope, _) = store.ParseQualifiedName("agent:execution-policy");
                var epEntries = store.LoadIndex(epScope);
                hasPolicy = epEntries.Any(e => e.Name == "execution-policy");
            }
            catch { }
        }

        string taskList = string.Join("\n", createdNames.Select(n => $"  - {n}"));
        string responseContent =
            $"Created {parsedTasks.Count} task(s) for phase {phaseId} in {waveCount} wave(s).\n" +
            $"Tasks stored:\n{taskList}";
        if (!string.IsNullOrEmpty(patternNote))
            responseContent += patternNote;

        string instruction = activeGoalId is not null
            ? $"call task('next', {{ path: '/goal/{activeGoalId}' }}) to get the first pending tasks.{parallelHint} Spawn agents for all task execution — the primary agent orchestrates, it does not execute tasks directly."
            : $"call task('next', {{ path: '/goal/G-X' }}) to get the first pending tasks.{parallelHint} Spawn agents for all task execution — the primary agent orchestrates, it does not execute tasks directly.";

        var infoItems = new List<string>();
        if (hasPolicy)
            infoItems.Add("Agent execution policy available — show('agent:execution-policy') for spawn requirements.");

        var warningItems = new List<string>();
        if (workflowWarning is not null)
            warningItems.Add(workflowWarning);
        if (fileConflicts.Count > 0)
            warningItems.AddRange(fileConflicts);

        var ptResponse = ResponseBuilder.Success(responseContent)
            .WithFileChanges()
            .WithAction("created")
            .WithInstruction(instruction);
        if (warningItems.Count > 0)
            ptResponse = ptResponse.WithActionNeeded([.. warningItems]);
        if (infoItems.Count > 0)
            ptResponse = ptResponse.WithInfo([.. infoItems]);

        return ptResponse.ToYaml();
    }

    /// <summary>Verify a phase achieved its goal using acceptance criteria from requirements.</summary>
    internal static async Task<string> PlanVerify(
        string phaseId,
        string? evidence = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Discover REQ-IDs referenced by tasks in this phase
        string? verifyGoalId0 = await GetActiveGoalIdAsync(store, cancellationToken);
        var (taskScope0, _) = store.ParseQualifiedName("task:placeholder");
        var allEntries0 = store.LoadIndex(taskScope0);
        var phaseReqIds = allEntries0
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
            .Where(e => verifyGoalId0 is null || HasKeyword(e, $"goal:{verifyGoalId0}"))
            .SelectMany(e => e.Keywords ?? [])
            .Where(k => k.StartsWith("req:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k["req:".Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Read requirements and extract matching entries as criteria
        string requirementsText;
        try { requirementsText = await ReadMemoryAsync(store, "project:requirements", cancellationToken); }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No requirements found. Call memory('remember', { path: '/requirement/...' }) first.").ToYaml();
        }

        var criteria = ExtractRequirementCriteria(requirementsText, phaseReqIds);
        if (criteria.Count == 0)
            return ResponseBuilder.Success($"No requirements found for phase {phaseId}. Ensure tasks reference REQ-IDs in their content.").WithAction("listed").ToYaml();

        // Load task summary for context (scoped to active goal)
        string? verifyGoalId = verifyGoalId0;
        var phaseEntries = allEntries0
            .Where(e => HasKeyword(e, $"phase:{phaseId}"))
            .Where(e => verifyGoalId is null || HasKeyword(e, $"goal:{verifyGoalId}"))
            .ToList();
        int totalTasks = phaseEntries.Count;
        int completeTasks = phaseEntries.Count(e => HasKeyword(e, "status:complete"));

        // Try to surface the hypothesis from research for this phase
        string? hypothesisText = null;
        try
        {
            var (resScope, _) = store.ParseQualifiedName("research:placeholder");
            var resEntries = store.LoadIndex(resScope);
            foreach (var re in resEntries.Where(e => HasKeyword(e, $"phase:{phaseId}")))
            {
                string resContent = await ReadMemoryAsync(store, $"research:{re.Name}", cancellationToken);
                int hypIdx = resContent.IndexOf("## Hypothesis", StringComparison.OrdinalIgnoreCase);
                if (hypIdx >= 0)
                {
                    string hypSection = resContent[(hypIdx + "## Hypothesis".Length)..];
                    int nextSection = hypSection.IndexOf("\n## ", StringComparison.Ordinal);
                    hypothesisText = (nextSection >= 0 ? hypSection[..nextSection] : hypSection).Trim();
                    break;
                }
            }
        }
        catch { /* no research or no hypothesis — skip */ }

        // ── Checklist mode (no evidence provided) ──
        if (string.IsNullOrWhiteSpace(evidence))
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## Phase {phaseId} — Verification Checklist");
            sb.AppendLine();
            sb.AppendLine($"Tasks: {completeTasks}/{totalTasks} complete");
            sb.AppendLine();

            if (hypothesisText is not null)
            {
                sb.AppendLine($"**Hypothesis from research:** {hypothesisText}");
                sb.AppendLine("Does the evidence support this hypothesis, or did reality diverge?");
                sb.AppendLine();
            }

            sb.AppendLine("Verify each criterion yourself (run tests, review code, confirm behavior).");
            sb.AppendLine("The QA gate task (auto-injected) will handle structured verification for this phase.");
            sb.AppendLine();

            for (int i = 0; i < criteria.Count; i++)
                sb.AppendLine($"{i + 1}. [ ] {criteria[i]}");

            return ResponseBuilder.Success(sb.ToString().TrimEnd()).WithAction("checklist").ToYaml();
        }

        // ── Recording mode (evidence provided) ──

        // Concern gate — reject if open high/medium concerns for this phase
        try
        {
            var (concernScope, _) = store.ParseQualifiedName("concern:placeholder");
            var concernEntries = store.LoadIndex(concernScope);
            var openConcerns = concernEntries
                .Where(e => e.Keywords is not null &&
                    e.Keywords.Any(k => k.Equals($"phase:{phaseId}", StringComparison.OrdinalIgnoreCase) ||
                                        k.Equals("phase:all", StringComparison.OrdinalIgnoreCase)) &&
                    e.Keywords.Any(k => k.Equals("status:active", StringComparison.OrdinalIgnoreCase)) &&
                    e.Keywords.Any(k => k.StartsWith("severity:high", StringComparison.OrdinalIgnoreCase) ||
                                        k.StartsWith("severity:medium", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (openConcerns.Count > 0)
            {
                var names = string.Join(", ", openConcerns.Select(e => e.Name));
                return ResponseBuilder.Error($"{openConcerns.Count} open high/medium concern(s) for phase {phaseId}: {names}.")
                    .WithInstruction("Resolve them (memory('transition', { path: '/concern/...', to: 'resolved' }) with verifiedBy) before verification.")
                    .ToYaml();
            }
        }
        catch { /* best-effort — don't block if concern scope doesn't exist */ }

        var sb2 = new System.Text.StringBuilder();
        sb2.AppendLine($"## Phase Verification: {phaseId}");

        // Parse evidence lines — match PASS:/FAIL: prefixes to criteria in order
        var evidenceLines = evidence.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("PASS:", StringComparison.OrdinalIgnoreCase)
                     || l.StartsWith("FAIL:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int passCount = 0;
        var criterionResults = new List<(bool pass, string criterion, string evidenceText)>();

        for (int i = 0; i < criteria.Count; i++)
        {
            if (i < evidenceLines.Count)
            {
                string line = evidenceLines[i];
                bool pass = line.StartsWith("PASS:", StringComparison.OrdinalIgnoreCase);
                string evidenceText = line[(line.IndexOf(':') + 1)..].Trim();
                criterionResults.Add((pass, criteria[i], evidenceText));
                if (pass) passCount++;
            }
            else
            {
                criterionResults.Add((false, criteria[i], "No evidence provided"));
            }
        }

        // Overall status
        string status = passCount == criteria.Count
            ? "ALL_PASS"
            : passCount == 0
                ? "ALL_FAIL"
                : $"PARTIAL ({passCount}/{criteria.Count} passed)";

        sb2.AppendLine($"Status: {status}");
        sb2.AppendLine();

        foreach (var (pass, criterion, evidenceText) in criterionResults)
        {
            sb2.AppendLine($"{(pass ? "PASS" : "FAIL")}: {criterion}");
            sb2.AppendLine($"  Evidence: {evidenceText}");
            sb2.AppendLine();
        }

        // Append verification record to execution log for auditability
        string verifyLog = $"[{DateTimeOffset.UtcNow:o}] VERIFY phase {phaseId}: {status}\n" +
            string.Join("\n", criterionResults.Select(r => $"  {(r.pass ? "PASS" : "FAIL")}: {r.criterion} — {r.evidenceText}"));
        await AppendToExecutionLogAsync(store, $"task:{phaseId}-execution-log", verifyLog, cancellationToken);

        // Update project:state with verification results
        string stateText2;
        try { stateText2 = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText2 = ""; }

        string projectName2 = ExtractStateField(stateText2, "Project:") ?? "Unknown Project";
        string projectId2 = ExtractStateField(stateText2, "ID:") ?? DeriveProjectId(store);
        string currentPhase2 = ExtractStateField(stateText2, "Phase:") ?? $"Phase {phaseId}";
        string progressPct2 = CalculateProgress(store, verifyGoalId);

        // Build next step guidance based on verification result
        string verifyNextStep;
        if (passCount < criteria.Count)
        {
            verifyNextStep = "call task('plan') with gap closure tasks";
        }
        else
        {
            // Check for active concerns scoped to this phase
            bool hasActiveConcerns = false;
            try
            {
                var (cs, _) = store.ParseQualifiedName("concern:placeholder");
                var concernEntries = store.LoadIndex(cs);
                hasActiveConcerns = concernEntries.Any(e =>
                    HasKeyword(e, "status:active") &&
                    (HasKeyword(e, $"phase:{phaseId}") || HasKeyword(e, "phase:all")));
            }
            catch { /* concern scope not yet created — skip silently */ }

            verifyNextStep = hasActiveConcerns
                ? $"resolve addressed concerns (memory('transition', {{ path: '/concern/...', to: 'resolved' }})), then the self-reflector gate task will handle retrospective for phase {phaseId}"
                : verifyGoalId is not null
                    ? $"self-reflector gate task will handle retrospective for phase {phaseId} — proceed to task('next', {{ path: '/goal/{verifyGoalId}' }})"
                    : $"self-reflector gate task will handle retrospective for phase {phaseId} — proceed to task('next', {{ path: '/goal/G-X' }})";
        }

        await WriteStateAsync(store, projectName2, projectId2,
            phase: currentPhase2,
            progressPct: progressPct2,
            lastAction: $"QA verification for phase {phaseId}: {status}",
            blockers: passCount < criteria.Count ? $"{criteria.Count - passCount} criteria failed" : "none",
            nextStep: verifyNextStep,
            cancellationToken);

        var verifyResponse = (passCount == criteria.Count
                ? ResponseBuilder.Success(sb2.ToString().TrimEnd())
                : ResponseBuilder.Warning(sb2.ToString().TrimEnd()))
            .WithAction("verified");
        if (passCount == criteria.Count)
            verifyResponse = verifyResponse.WithInstruction(verifyNextStep);

        return verifyResponse.ToYaml();
    }

    /// <summary>Create gap closure tasks for failed verification criteria and re-open the phase.</summary>
    internal static async Task<string> PlanGaps(
        string phaseId,
        string failedCriteria,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Verify project exists
        try { await ReadMemoryAsync(store, "project:context", cancellationToken); }
        catch (FileNotFoundException)
        {
            return ResponseBuilder.Error("No project found. Run project_init first.").ToYaml();
        }

        // Parse failed criteria — split on newlines, trim, filter empty
        var criteria = failedCriteria
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length > 0)
            .ToList();

        if (criteria.Count == 0)
            return ResponseBuilder.Error("No failed criteria provided. Pass newline-separated criterion texts.").ToYaml();

        // Create a gap task for each failed criterion (scoped to active goal)
        string? gapGoalId = await GetActiveGoalIdAsync(store, cancellationToken);
        var createdNames = new List<string>();
        for (int i = 0; i < criteria.Count; i++)
        {
            string criterion = criteria[i];
            string gapTaskName = $"task:{phaseId}-gap-{(i + 1):D2}";
            var gapKeywordsList = new List<string> { "status:pending", "wave:1", $"phase:{phaseId}", "gap_closure:true" };
            if (gapGoalId is not null) gapKeywordsList.Add($"goal:{gapGoalId}");
            string[] gapKeywords = gapKeywordsList.ToArray();
            string content =
                $"Gap closure task for failed criterion:\n{criterion}\n\n" +
                $"Action: Address the failed criterion and ensure it passes on re-verification.";

            await WritePlanningMemoryAsync(store, gapTaskName, content,
                archiveExisting: false, keywords: gapKeywords, cancellationToken);

            createdNames.Add(gapTaskName);
        }

        // Re-open phase in project:state
        string stateText;
        try { stateText = await ReadMemoryAsync(store, "project:state", cancellationToken); }
        catch (FileNotFoundException) { stateText = ""; }

        string projectName = ExtractStateField(stateText, "Project:") ?? "Unknown Project";
        string projectId = ExtractStateField(stateText, "ID:") ?? DeriveProjectId(store);
        string progressPct = CalculateProgress(store, gapGoalId);

        await WriteStateAsync(store, projectName, projectId,
            phase: $"Phase {phaseId} (re-opened for gap closure)",
            progressPct: progressPct,
            lastAction: $"Gap closure: {criteria.Count} task(s) created for phase {phaseId}",
            blockers: "none",
            nextStep: gapGoalId is not null
                ? $"call task('next', {{ path: '/goal/{gapGoalId}' }}) to work on gap tasks"
                : "call task('next', { path: '/goal/G-X' }) to work on gap tasks",
            cancellationToken);

        string taskList = string.Join("\n", createdNames.Select(n => $"  - {n}"));
        return ResponseBuilder.Success($"Created {criteria.Count} gap closure task(s) for phase {phaseId}. Phase re-opened.\nGap tasks created:\n{taskList}")
            .WithAction("created")
            .WithInstruction(gapGoalId is not null
                ? $"call task('next', {{ path: '/goal/{gapGoalId}' }}) to begin."
                : "call task('next', { path: '/goal/G-X' }) to begin.")
            .ToYaml();
    }

    private sealed record ParsedTask(string Id, string[] DependsOn, string Content, string[]? Files = null);

    /// <summary>
    /// Parses free-text task input into structured task records.
    /// Each task section starts with "## Task {id}" and contains Wave, Depends on, Action, and Acceptance criteria fields.
    /// </summary>
    private static List<ParsedTask> ParseTaskSections(string tasks)
    {
        var result = new List<ParsedTask>();
        // Split by task section headers: ## Task XX or ## Task XX (anything)
        var headerMatches = TaskHeaderPattern.Matches(tasks);

        for (int i = 0; i < headerMatches.Count; i++)
        {
            Match header = headerMatches[i];
            string taskId = header.Groups[1].Value.TrimStart('0');
            if (taskId.Length == 0) taskId = "0";
            // Pad to 2 digits
            if (int.TryParse(taskId, out int taskIdNum))
                taskId = taskIdNum.ToString("D2");

            // Extract the section content between this header and the next
            int sectionStart = header.Index + header.Length;
            int sectionEnd = i + 1 < headerMatches.Count
                ? headerMatches[i + 1].Index
                : tasks.Length;
            string section = tasks[sectionStart..sectionEnd];

            // Parse Depends on
            string[] dependsOn = [];
            var depsMatch = DependsOnPattern.Match(section);
            if (depsMatch.Success)
            {
                string depsValue = depsMatch.Groups[1].Value.Trim();
                if (!string.Equals(depsValue, "none", StringComparison.OrdinalIgnoreCase))
                {
                    dependsOn = depsValue
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToArray();
                }
            }

            // Parse Files
            string[]? files = null;
            var filesMatch = FilesFieldPattern.Match(section);
            if (filesMatch.Success)
            {
                files = filesMatch.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            }

            // Build content: Action + Acceptance criteria (everything except Depends on / Files lines)
            var contentLines = section.Split('\n')
                .Where(line => !DependsOnPattern.IsMatch(line.Trim()))
                .Where(line => !FilesFieldPattern.IsMatch(line.Trim()))
                .ToList();

            // Trim leading/trailing blank lines from content
            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[0]))
                contentLines.RemoveAt(0);
            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[^1]))
                contentLines.RemoveAt(contentLines.Count - 1);

            string content = string.Join('\n', contentLines).Trim();
            if (string.IsNullOrWhiteSpace(content))
                content = "(no action specified)";

            result.Add(new ParsedTask(taskId, dependsOn, content, files));
        }

        return result;
    }

    /// <summary>
    /// Checks whether the cartographer should be run based on memory growth
    /// since the last cartography entry. Returns a warning string or empty.
    /// </summary>
    private static string CheckCartographerNeeded(IMemoryStore store)
    {
        try
        {
            var allEntries = store.ListScoped(null);
            int totalMemories = allEntries.Count;

            // Check last cartography entry
            var (cartScope2, _) = store.ParseQualifiedName("cartography:placeholder");
            var cartEntries2 = store.LoadIndex(cartScope2);
            var lastCart = cartEntries2.OrderByDescending(e => e.UpdatedAt ?? e.CreatedAt).FirstOrDefault();

            if (lastCart is not null)
            {
                var lastCartDate = lastCart.UpdatedAt ?? lastCart.CreatedAt;
                // Count memories created/modified after last cartography
                int newSince = allEntries.Count(e => (e.Entry.UpdatedAt ?? e.Entry.CreatedAt) > lastCartDate);
                if (newSince >= 10)
                    return $"\u26a0 ACTION NEEDED: {newSince} memories created/modified since last cartographer run — spawn a cartographer to index connections.\n";
            }
            else if (totalMemories >= 10)
            {
                return $"\u26a0 ACTION NEEDED: {totalMemories} memories exist with no cartographer run — spawn a cartographer to index connections.\n";
            }
        }
        catch { /* best-effort */ }

        return "";
    }

    /// <summary>
    /// Returns the number of memories created/modified since the last cartography entry.
    /// If no cartography exists, returns total memory count. Returns 0 on error.
    /// </summary>
    private static int CountMemoriesSinceLastCartography(IMemoryStore store)
    {
        try
        {
            var allEntries = store.ListScoped(null);
            int totalMemories = allEntries.Count;

            var (cartScope2, _) = store.ParseQualifiedName("cartography:placeholder");
            var cartEntries2 = store.LoadIndex(cartScope2);
            var lastCart = cartEntries2.OrderByDescending(e => e.UpdatedAt ?? e.CreatedAt).FirstOrDefault();

            if (lastCart is not null)
            {
                var lastCartDate = lastCart.UpdatedAt ?? lastCart.CreatedAt;
                return allEntries.Count(e => (e.Entry.UpdatedAt ?? e.Entry.CreatedAt) > lastCartDate);
            }
            else
            {
                return totalMemories;
            }
        }
        catch { return 0; }
    }

    /// <summary>
    /// Appends an outcome entry to the named execution log memory using AppendChunk.
    /// Creates the log if it doesn't exist.
    /// </summary>
    private static Task AppendToExecutionLogAsync(
        IMemoryStore store, string logName, string outcomeText, CancellationToken ct)
        => AppendToExecutionLogAsync(store, logName, outcomeText, keywords: null, ct);

    /// <summary>
    /// Appends an outcome entry to the named execution log memory using AppendChunk,
    /// optionally setting keywords on the index entry.
    /// Creates the log if it doesn't exist.
    /// </summary>
    private static async Task AppendToExecutionLogAsync(
        IMemoryStore store, string logName, string outcomeText, string[]? keywords, CancellationToken ct)
    {
        var (logScope, logSubject) = store.ParseQualifiedName(logName);

        // Check for existing log artifact
        string? existingArtifact = null;
        long existingBytes = 0;
        var logEntries = store.LoadIndex(logScope);
        var logEntry = logEntries.FirstOrDefault(e => e.Name == logSubject);

        if (logEntry is not null)
        {
            try
            {
                existingArtifact = await store.ReadArtifactAsync(logSubject, logScope, ct);
                existingBytes = logEntry.OriginalBytes;
            }
            catch (FileNotFoundException)
            {
                existingArtifact = null;
            }
        }

        // Build new artifact: AppendChunk if existing, Encode if fresh
        string newArtifact;
        int newByteCount = System.Text.Encoding.UTF8.GetByteCount(outcomeText);
        long totalBytes;

        if (existingArtifact is not null)
        {
            newArtifact = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, outcomeText);
            totalBytes = existingBytes + newByteCount;
        }
        else
        {
            newArtifact = Nmp2ChunkedEncoder.Encode(outcomeText);
            totalBytes = newByteCount;
        }

        await store.WriteArtifactAsync(logSubject, logScope, newArtifact, ct);

        string uri = store.ArtifactUri(logSubject, logScope);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        string desc = outcomeText[..Math.Min(200, outcomeText.Length)];
        DateTimeOffset? updatedAt = existingArtifact is not null ? DateTimeOffset.UtcNow : null;

        var newLogEntry = new ArtifactEntry(
            Name: logSubject,
            Uri: uri,
            OriginalBytes: totalBytes,
            ChunkCount: chunkCount,
            CreatedAt: logEntry?.CreatedAt ?? DateTimeOffset.UtcNow,
            Description: desc,
            Keywords: keywords,
            UpdatedAt: updatedAt);

        store.Upsert(newLogEntry, logScope);
    }

    /// <summary>
    /// Discovers distinct phase IDs from task keywords (e.g. "phase:01" → "01").
    /// Optionally scoped to a specific goal.
    /// </summary>
    private static List<string> DiscoverPhaseIds(IReadOnlyList<ArtifactEntry> taskEntries, string? goalId = null)
    {
        return taskEntries
            .Where(e => goalId is null || HasKeyword(e, $"goal:{goalId}"))
            .SelectMany(e => e.Keywords ?? [])
            .Where(k => k.StartsWith("phase:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k["phase:".Length..])
            .Where(p => p != "init") // exclude init phase
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Calculates overall progress percentage from task completion data.
    /// Each phase contributes equally. Within a phase, progress = completed / total tasks.
    /// </summary>
    /// <remarks>
    /// Called at 10+ carry-forward sites on every state write. Must stay synchronous and
    /// lightweight (keyword scan over in-memory index only — no artifact decoding, no I/O).
    /// Tightly coupled to the <c>task:</c> topic and <c>phase:XX</c> / <c>status:complete</c>
    /// / <c>goal:ID</c> keyword conventions. Not a general-purpose API.
    /// </remarks>
    internal static string CalculateProgress(IMemoryStore store, string? goalId = null)
    {
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var allTaskEntries = store.LoadIndex(taskScope);

        var phaseIds = DiscoverPhaseIds(allTaskEntries, goalId);
        if (phaseIds.Count == 0)
            return "0";

        double totalProgress = 0;
        foreach (string phId in phaseIds)
        {
            var phaseEntries = allTaskEntries
                .Where(e => HasKeyword(e, $"phase:{phId}"))
                .Where(e => goalId is null || HasKeyword(e, $"goal:{goalId}"))
                .ToList();

            if (phaseEntries.Count == 0)
                continue;

            int complete = phaseEntries.Count(e => HasKeyword(e, "status:complete"));
            totalProgress += (double)complete / phaseEntries.Count;
        }

        int pct = (int)Math.Round(totalProgress / phaseIds.Count * 100);
        return pct.ToString();
    }

    /// <summary>Extracts wave number from "wave:N" keyword; returns 0 if not found.</summary>
    private static int ParseWave(ArtifactEntry e)
    {
        string? waveKw = e.Keywords?.FirstOrDefault(k =>
            k.StartsWith("wave:", StringComparison.OrdinalIgnoreCase));
        return waveKw is not null && int.TryParse(waveKw[5..], out int w) ? w : 0;
    }

    /// <summary>Returns all subject names from "depends_on:*" keywords.</summary>
    private static IEnumerable<string> GetDependencies(ArtifactEntry e) =>
        e.Keywords?
            .Where(k => k.StartsWith("depends_on:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k["depends_on:".Length..])
        ?? Enumerable.Empty<string>();

    /// <summary>
    /// Extracts requirement entries from requirements text, filtered to the given REQ-IDs.
    /// Each matching "REQ-NNN: description" line becomes one criterion.
    /// </summary>
    private static List<string> ExtractRequirementCriteria(string requirementsText, HashSet<string> reqIds)
    {
        var criteria = new List<string>();
        foreach (string rawLine in requirementsText.Split('\n'))
        {
            string trimmed = rawLine.Trim();
            var match = ReqIdPattern.Match(trimmed);
            if (match.Success && reqIds.Contains(match.Groups[1].Value))
            {
                // Use the full REQ line as the criterion (e.g. "REQ-001: description")
                criteria.Add(trimmed);
            }
        }
        return criteria;
    }
}
