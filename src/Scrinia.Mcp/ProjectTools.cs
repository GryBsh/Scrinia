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

// ── Planning DTOs ────────────────────────────────────────────────────────────

/// <summary>Represents a project tracked in scrinia planning memory (project:* topic).</summary>
public sealed record ProjectRecord(
    string Id,
    string Name,
    string? Description,
    string[]? Goals,
    string[]? Constraints);

/// <summary>Represents a plan (phase) tracked in scrinia planning memory (plan:* topic).</summary>
public sealed record PlanRecord(
    string Id,
    string Phase,
    string? Goal,
    string? Status,
    string[]? TaskIds);

/// <summary>Represents a task tracked in scrinia planning memory (task:* topic).</summary>
public sealed record TaskRecord(
    string Id,
    string Phase,
    string Name,
    string? Description,
    string? Status,
    string[]? DependsOn,
    string[]? AcceptanceCriteria);

/// <summary>Represents a concern/risk tracked across project phases (concern:* topic).</summary>
public sealed record ConcernRecord(
    string Id,
    string Phase,
    string Description,
    string Severity,
    string? Status,
    string? Resolution,
    string? ResolvedAt);

/// <summary>Represents a reusable agent skill/prompt template (skill:* topic).</summary>
public sealed record SkillRecord(
    string Id,
    string Name,
    string? Description,
    string? SystemPrompt,
    string[]? Tools,
    string[]? Capabilities);

/// <summary>Represents a research investigation and its findings (research:* topic).</summary>
public sealed record ResearchRecord(
    string Id,
    string Topic,
    string? Question,
    string? Status,
    string? Findings,
    string[]? Sources);

/// <summary>Represents a project goal that can evolve over time (project:goals topic).</summary>
public sealed record GoalRecord(
    string Id,
    string Description,
    string? Status,
    string? Outcome,
    string? CompletedAt);

// ── Source-gen JSON context (trimming-safe) ──────────────────────────────────

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ProjectRecord))]
[JsonSerializable(typeof(PlanRecord))]
[JsonSerializable(typeof(TaskRecord))]
[JsonSerializable(typeof(ConcernRecord))]
[JsonSerializable(typeof(SkillRecord))]
[JsonSerializable(typeof(ResearchRecord))]
[JsonSerializable(typeof(GoalRecord))]
[JsonSerializable(typeof(ProjectRecord[]))]
[JsonSerializable(typeof(PlanRecord[]))]
[JsonSerializable(typeof(TaskRecord[]))]
[JsonSerializable(typeof(ConcernRecord[]))]
[JsonSerializable(typeof(SkillRecord[]))]
[JsonSerializable(typeof(ResearchRecord[]))]
[JsonSerializable(typeof(GoalRecord[]))]
[JsonSerializable(typeof(WorkflowDefinition))]
[JsonSerializable(typeof(WorkflowActivity))]
[JsonSerializable(typeof(WorkflowActivity[]))]
[JsonSerializable(typeof(GateValidation))]
[JsonSerializable(typeof(GateValidation[]))]
[JsonSerializable(typeof(SkillFileMeta))]
[JsonSerializable(typeof(WorkflowFileMeta))]
[JsonSerializable(typeof(AgentFileMeta))]
[JsonSerializable(typeof(Dictionary<string,string>))]
public partial class PlanningJsonContext : JsonSerializerContext;

// ── Planning MCP tool class ──────────────────────────────────────────────────

/// <summary>
/// MCP tools for project planning — stores and retrieves planning memories using
/// the plan:*, task:*, project:*, learn:*, and backlog:* topic conventions.
/// </summary>
[McpServerToolType]
public sealed partial class ScriniaProjectTools
{
    /// <summary>
    /// Copilot CLI hard-truncates MCP tool responses at 10 KB (fixed constant in Iw()).
    /// VS Code Copilot Chat truncates at ~50% of prompt token budget (dynamic).
    /// We cap at 8 KB to stay safely under the CLI limit with 2 KB headroom.
    /// </summary>
    private const int MaxResponseChars = 8 * 1024;

    // ── Compiled regex patterns (single source of truth) ─────────────────────
    private const string GoalIdCore = @"\d+(?:-[a-fA-F0-9]+)?";
    private static readonly Regex GoalIdPattern = new($@"G-({GoalIdCore})", RegexOptions.Compiled);
    private static readonly Regex BracketedGoalIdPattern = new($@"\[G-({GoalIdCore})\]", RegexOptions.Compiled);
    private static readonly Regex BracketedGoalIdFullPattern = new($@"\[(G-{GoalIdCore})\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GoalIdNumericPattern = new($@"\[G-(\d+)(?:-[a-fA-F0-9]+)?\]", RegexOptions.Compiled);
    private static readonly Regex GoalIdStructuredPattern = new($@"^\[G-{GoalIdCore}\]", RegexOptions.Compiled);
    private static readonly Regex ShortGoalIdPattern = new(@"^G-\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal static readonly Regex PhaseNumberPattern = new(@"Phase\s+0*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GoalsSectionPattern = new(@"^#{0,4}\s*Goals\s*:?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GoalsSectionAltPattern = new(@"^Goals\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OriginalGoalsPattern = new(@"^[Oo]riginal goals?\s*:\s*\d+", RegexOptions.Compiled);
    private static readonly Regex DependsOnPattern = new(@"^Depends\s+on:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex FilesFieldPattern = new(@"^Files:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex CompletedTimestampPattern = new(@"Completed:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex SectionHeadingPattern = new(@"^#{1,4}\s+\S", RegexOptions.Compiled);
    private static readonly Regex ReqIdPattern = new(@"\b([A-Z]+-\d+)\b", RegexOptions.Compiled);
    private static readonly Regex TaskHeaderPattern = new(@"^##\s+Task\s+(\w+)", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex DigitPattern = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex GoalStatusPrefixPattern = new($@"^\[G-{GoalIdCore}\]\s*\[[\w]+\]\s*", RegexOptions.Compiled);

    internal static string Truncate(string text) =>
        text.Length <= MaxResponseChars ? text : text[..MaxResponseChars] + "\n[... truncated to 8KB limit]";

    private static IMemoryStore CurrentStore =>
        MemoryStoreContext.Current ?? throw new InvalidOperationException(
            "No memory store configured. Call MemoryStoreContext.Current = ... before using planning tools.");
}
