using Scrinia.Core.Configuration;
using Scrinia.Core.Models;
using Scrinia.Core;
using System.Globalization;
using System.Text;

namespace Scrinia.Mcp.SharedContracts;

public sealed record SharedBaseSelection(string Base, SharedBaseEntry? Entry);

public sealed class SharedAddressException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

/// <summary>Resolves the enabled shared namespace before any base-scoped work begins.</summary>
public sealed class SharedServer
{
    private readonly IWorkspaceConfig _workspaceConfig;
    private readonly ISharedBaseRegistry _registry;
    private readonly ISharedBaseRuntimeFactory _runtimeFactory;

    public SharedServer(IWorkspaceConfig workspaceConfig, ISharedBaseRegistry registry, ISharedBaseRuntimeFactory runtimeFactory)
    {
        _workspaceConfig = workspaceConfig ?? throw new ArgumentNullException(nameof(workspaceConfig));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
    }

    public SharedBaseSelection? SelectBase(string? baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            return null;
        if (!SharedBaseRegistry.IsCanonicalName(baseName))
            throw new SharedAddressException("INVALID_PARAMETER", "The shared base name is not canonical.");
        if (!_workspaceConfig.GetEnabledBases().Contains(baseName, StringComparer.Ordinal))
            return null;

        SharedBaseEntry? entry = _registry.Lookup(baseName);
        return entry is null ? null : new SharedBaseSelection(baseName, entry);
    }

    public IReadOnlyList<SharedBaseSelection> SelectEnabledBases()
    {
        var selected = new List<SharedBaseSelection>();
        foreach (string baseName in _workspaceConfig.GetEnabledBases())
        {
            SharedBaseEntry? entry = _registry.Lookup(baseName);
            selected.Add(new SharedBaseSelection(baseName, entry));
        }
        return selected;
    }

    public IReadOnlyList<SharedBaseSummary> GetEnabledBaseSummaries() =>
        SelectEnabledBases()
            .Select(selection => CreateSummary(selection.Base, selection.Entry))
            .ToArray();

    public SharedBaseRuntime OpenSelectedBase(string baseName)
    {
        SharedBaseSelection selection = SelectBase(baseName)
            ?? throw new SharedAddressException("UNKNOWN_SHARED_BASE", "The selected shared base is unavailable.");
        if (selection.Entry is null)
            throw new SharedAddressException("UNKNOWN_SHARED_BASE", "The selected shared base is unavailable.");
        return _runtimeFactory.Open(selection.Entry);
    }

    public static MemoryAddress ParsePersistentAddress(string rawAddress)
    {
        MemoryAddress address;
        try
        {
            address = MemoryAddress.Parse(rawAddress);
        }
        catch (MemoryAddressException exception)
        {
            throw new SharedAddressException("MALFORMED_ADDRESS", exception.Message);
        }

        if (address.Kind == MemoryAddressKind.Ephemeral)
            throw new SharedAddressException("SHARED_EPHEMERAL_NOT_SUPPORTED", "Shared bases accept persistent addresses only.");

        return address;
    }

    public ParsedPath ParsePersistentPath(string rawPath)
    {
        MemoryAddress address = ParsePersistentAddress(rawPath);
        return PathParser.Parse(address.Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the operation-based Git staging guidance for a successful shared mutation (§7.2): exactly
    /// one add command carrying every manifest path as an individually shell-quoted argument, followed
    /// by the fixed commit and push. Returns <c>null</c> for an empty manifest so a no-op emits no
    /// staging command. Scrinia never claims the guidance ran or that the base is tracked.
    /// </summary>
    public static string? GuidanceForMutation(string registeredRoot, IReadOnlyList<string> manifest) =>
        SharedGitGuidance.Render(registeredRoot, manifest);

    private SharedBaseSummary CreateSummary(string baseName, SharedBaseEntry? entry)
    {
        if (entry is null)
            return new SharedBaseSummary(baseName, null, SharedBaseAvailability.Unavailable, null);

        return new SharedBaseSummary(baseName, entry.Root, SharedBaseAvailability.Unavailable, null);
    }
}

public enum SharedValidationStage
{
    Parameter, BaseSyntax, EnabledMembership, Registry, GitRoot, LogicalPath,
    TrackingBlock, Availability, Freshness, Operation, Storage
}

public static class SharedValidationStageOrder
{
    public static IReadOnlyList<SharedValidationStage> All { get; } = Enum.GetValues<SharedValidationStage>();
}

public enum MutationCertainty { Applied, None, Unknown }

/// <summary>Transport-neutral shared envelope with the contract's conditional fields.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SharedEnvelope
{
    public string Status { get; init; } = "success";
    public bool IsError => Status is "error";
    public bool IsWarning => Status is "partial";
    public string? Base { get; init; }
    public object? Result { get; init; }
    public IReadOnlyList<object>? Results { get; init; }
    public SharedWarning[] Warnings { get; init; } = [];
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public string? Instruction { get; init; }
    public string[] FollowUp { get; init; } = [];
    public MutationCertainty Mutation { get; init; } = MutationCertainty.None;
    public bool? Truncated { get; init; }
    public SharedPage? Page { get; init; }

    public static SharedEnvelope Success(string? baseName, object result) => new() { Base = baseName, Result = result };
    public static SharedEnvelope Partial(IReadOnlyList<object> results, IReadOnlyList<SharedWarning> warnings) =>
        new() { Status = "partial", Results = results, Warnings = warnings.ToArray() };
    public static SharedEnvelope CreateMutation(string baseName, MutationResult result, string? instruction = null) =>
        new() { Base = baseName, Result = result, Instruction = instruction, Mutation = result.Mutation == MutationKind.Applied ? MutationCertainty.Applied : MutationCertainty.None };
    public static SharedEnvelope CreateError(string code, string error, IReadOnlyList<SharedWarning>? warnings = null, string? baseName = null,
        string? instruction = null, MutationCertainty mutation = MutationCertainty.None) => new()
        { Status = "error", Base = baseName, ErrorCode = code, Error = error, Warnings = warnings?.ToArray() ?? [], Instruction = instruction, Mutation = mutation };
    public static SharedEnvelope ForValidationFailure(SharedValidationStage stage, string submittedBase) =>
        stage == SharedValidationStage.EnabledMembership
            ? CreateError("SHARED_BASE_NOT_ENABLED", "The selected base is not enabled for this workspace.", instruction: "Enable the base in this workspace before selecting it.")
            : CreateError("INVALID_PARAMETER", "The request is invalid.");
    public static SharedEnvelope ForValidatedBase(string baseName, string code, string error, MutationCertainty mutation) =>
        CreateError(code, error, baseName: baseName, instruction: mutation == MutationCertainty.Unknown ? "Refresh this base before another mutation." : null, mutation: mutation);

    public string ToYaml() => SharedEnvelopeYaml.Serialize(this);
}

public sealed record SharedPage(int Offset, int Limit, int Returned, int Total, bool HasMore);

internal static class SharedEnvelopeYaml
{
    public static string Serialize(SharedEnvelope envelope)
    {
        var output = new StringBuilder();
        WriteLine(output, 0, "status", Scalar(envelope.Status));
        if (envelope.Base is not null) WriteLine(output, 0, "base", Scalar(envelope.Base));
        if (envelope.Result is not null) WritePayload(output, "result", envelope.Result, 0);
        if (envelope.Results is not null)
        {
            WriteLine(output, 0, "results", null);
            foreach (object result in envelope.Results) WritePayload(output, "-", result, 1);
        }
        if (envelope.Warnings.Length == 0) output.AppendLine("warnings: []");
        else { output.AppendLine("warnings:"); foreach (var warning in envelope.Warnings) WritePayload(output, "-", warning, 1); }
        if (envelope.Error is not null) WriteLine(output, 0, "error", Scalar(envelope.Error));
        if (envelope.ErrorCode is not null) WriteLine(output, 0, "errorCode", Scalar(envelope.ErrorCode));
        WriteLine(output, 0, "instruction", envelope.Instruction is null ? "null" : Scalar(envelope.Instruction));
        if (envelope.FollowUp.Length == 0) output.AppendLine("followUp: []");
        else { output.AppendLine("followUp:"); foreach (string path in envelope.FollowUp) output.AppendLine("  - " + Scalar(path)); }
        WriteLine(output, 0, "mutation", Scalar(envelope.Mutation.ToString().ToLowerInvariant()));
        if (envelope.Page is not null)
        {
            output.AppendLine("page:");
            WriteLine(output, 1, "offset", envelope.Page.Offset.ToString(CultureInfo.InvariantCulture));
            WriteLine(output, 1, "limit", envelope.Page.Limit.ToString(CultureInfo.InvariantCulture));
            WriteLine(output, 1, "returned", envelope.Page.Returned.ToString(CultureInfo.InvariantCulture));
            WriteLine(output, 1, "total", envelope.Page.Total.ToString(CultureInfo.InvariantCulture));
            WriteLine(output, 1, "hasMore", envelope.Page.HasMore ? "true" : "false");
        }
        if (envelope.Truncated is not null) WriteLine(output, 0, "truncated", envelope.Truncated.Value ? "true" : "false");
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
    }

    private static void WritePayload(StringBuilder output, string name, object value, int indent)
    {
        WriteLine(output, indent, name, null);
        int childIndent = indent + 1;
        switch (value)
        {
            case SharedWarning warning:
                WriteLine(output, childIndent, "base", Scalar(warning.Base));
                WriteLine(output, childIndent, "code", Scalar(warning.Code));
                WriteLine(output, childIndent, "diagnostic", Scalar(warning.Diagnostic));
                break;
            case SharedBaseSummary summary:
                WriteLine(output, childIndent, "base", Scalar(summary.Base));
                if (summary.Root is not null) WriteLine(output, childIndent, "root", Scalar(summary.Root));
                WriteLine(output, childIndent, "availability", Scalar(summary.Availability.ToString().ToLowerInvariant()));
                WriteLine(output, childIndent, "generation", Nullable(summary.Generation));
                break;
            case SearchHit hit:
                WriteLine(output, childIndent, "base", Scalar(hit.Base)); WriteLine(output, childIndent, "path", Scalar(hit.Path));
                WriteLine(output, childIndent, "kind", Scalar(hit.Kind.ToString().ToLowerInvariant())); WriteLine(output, childIndent, "chunk", hit.Chunk?.ToString(CultureInfo.InvariantCulture) ?? "null");
                WriteLine(output, childIndent, "score", hit.Score.ToString("R", CultureInfo.InvariantCulture)); WriteLine(output, childIndent, "preview", Scalar(hit.Preview));
                WriteDictionary(output, childIndent, "attributes", hit.Attributes); break;
            case ListItem item:
                WriteLine(output, childIndent, "base", Scalar(item.Base)); WriteLine(output, childIndent, "path", Scalar(item.Path)); WriteLine(output, childIndent, "kind", "persistent");
                WriteLine(output, childIndent, "created", Nullable(item.Created)); WriteLine(output, childIndent, "updated", Nullable(item.Updated)); WriteLine(output, childIndent, "description", Nullable(item.Description));
                WriteDictionary(output, childIndent, "attributes", item.Attributes); WriteStrings(output, childIndent, "keywords", item.Keywords);
                if (item.Drift is not null) WriteLine(output, childIndent, "drift", Scalar(item.Drift.Value.ToString().ToLowerInvariant()));
                if (item.Files is not null)
                {
                    if (item.Files.Length == 0) output.AppendLine(Indent(childIndent) + "files: []");
                    else { output.AppendLine(Indent(childIndent) + "files:"); foreach (FileDriftRef file in item.Files) WritePayload(output, "-", file, childIndent + 1); }
                }
                break;
            case FileDriftRef file:
                WriteLine(output, childIndent, "file", Scalar(file.File)); WriteLine(output, childIndent, "expectedSha256", Nullable(file.ExpectedSha256)); WriteLine(output, childIndent, "actualSha256", Nullable(file.ActualSha256)); break;
            case RecallResult recall:
                WriteLine(output, childIndent, "base", Scalar(recall.Base)); WriteLine(output, childIndent, "path", Scalar(recall.Path)); WriteStrings(output, childIndent, "chunks", recall.Chunks);
                WriteDictionary(output, childIndent, "attributes", recall.Attributes); WriteStrings(output, childIndent, "keywords", recall.Keywords);
                WriteLine(output, childIndent, "created", Nullable(recall.Created)); WriteLine(output, childIndent, "updated", Nullable(recall.Updated)); break;
            case EntityCount count:
                WriteLine(output, childIndent, "entity", Scalar(count.Entity)); WriteLine(output, childIndent, "count", count.Count.ToString(CultureInfo.InvariantCulture)); break;
            case FactsEntitiesResult entities:
                WriteFactsHeader(output, childIndent, entities.Base, entities.Op); WritePayloads(output, childIndent, "rows", entities.Rows); WriteLine(output, childIndent, "truncated", entities.Truncated ? "true" : "false"); break;
            case FactsProfileResult profile:
                WriteFactsHeader(output, childIndent, profile.Base, profile.Op); WriteLine(output, childIndent, "entity", Scalar(profile.Entity)); WriteLine(output, childIndent, "count", profile.Count.ToString(CultureInfo.InvariantCulture));
                WriteLine(output, childIndent, "firstSeen", Nullable(profile.FirstSeen)); WriteLine(output, childIndent, "lastSeen", Nullable(profile.LastSeen)); WritePayloads(output, childIndent, "coOccurrences", profile.CoOccurrences); WriteLine(output, childIndent, "coOccurrencesTruncated", profile.CoOccurrencesTruncated ? "true" : "false"); WriteStrings(output, childIndent, "timeline", profile.Timeline); WriteLine(output, childIndent, "timelineTruncated", profile.TimelineTruncated ? "true" : "false"); break;
            case FactsIntersectResult intersect:
                WriteFactsHeader(output, childIndent, intersect.Base, intersect.Op); WriteStrings(output, childIndent, "paths", intersect.Paths); WriteLine(output, childIndent, "truncated", intersect.Truncated ? "true" : "false"); break;
            case MutationResult mutation:
                WriteLine(output, childIndent, "base", Scalar(mutation.Base)); if (mutation.Path is not null) WriteLine(output, childIndent, "path", Scalar(mutation.Path)); if (mutation.Destination is not null) WriteLine(output, childIndent, "destination", Scalar(mutation.Destination));
                WriteLine(output, childIndent, "mutation", Scalar(mutation.Mutation.ToString().ToLowerInvariant())); WriteLine(output, childIndent, "generation", Scalar(mutation.Generation)); WriteStrings(output, childIndent, "manifest", mutation.Manifest); break;
            case RefreshResult refresh:
                WriteLine(output, childIndent, "base", Scalar(refresh.Base)); WriteLine(output, childIndent, "previousGeneration", Nullable(refresh.PreviousGeneration)); WriteLine(output, childIndent, "newGeneration", Scalar(refresh.NewGeneration)); WriteLine(output, childIndent, "availability", "available"); WritePayloads(output, childIndent, "warnings", refresh.Warnings); break;
            default: throw new InvalidOperationException($"Unsupported shared payload DTO: {value.GetType().Name}.");
        }
    }

    private static void WritePayloads(StringBuilder output, int indent, string name, IEnumerable<object> values)
    {
        object[] items = values.ToArray();
        if (items.Length == 0) { output.AppendLine(Indent(indent) + name + ": []"); return; }
        output.AppendLine(Indent(indent) + name + ":"); foreach (object value in items) WritePayload(output, "-", value, indent + 1);
    }

    private static void WriteDictionary(StringBuilder output, int indent, string name, IReadOnlyDictionary<string, string[]> values)
    { if (values.Count == 0) { output.AppendLine(Indent(indent) + name + ": {}"); return; } output.AppendLine(Indent(indent) + name + ":"); foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { output.AppendLine(Indent(indent + 1) + Scalar(pair.Key) + ":"); foreach (string value in pair.Value) output.AppendLine(Indent(indent + 2) + "- " + Scalar(value)); } }
    private static void WriteStrings(StringBuilder output, int indent, string name, IEnumerable<string> values)
    { string[] items = values.ToArray(); if (items.Length == 0) { output.AppendLine(Indent(indent) + name + ": []"); return; } output.AppendLine(Indent(indent) + name + ":"); foreach (string value in items) output.AppendLine(Indent(indent + 1) + "- " + Scalar(value)); }
    private static void WriteFactsHeader(StringBuilder output, int indent, string baseName, FactsOperation operation) { WriteLine(output, indent, "base", Scalar(baseName)); WriteLine(output, indent, "op", Scalar(operation.ToString().ToLowerInvariant())); }
    private static void WriteLine(StringBuilder output, int indent, string name, string? value) => output.AppendLine(Indent(indent) + (name == "-" ? "-" : name + ":" + (value is null ? "" : " " + value)));
    private static string Indent(int level) => new(' ', level * 2);
    private static string Nullable(string? value) => value is null ? "null" : Scalar(value);
    private static string Scalar(string value)
    {
        if (IsSafePlainScalar(value)) return value;
        StringBuilder escaped = new(value.Length + 2);
        escaped.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\': escaped.Append("\\\\"); break;
                case '"': escaped.Append("\\\""); break;
                case '\r': escaped.Append("\\r"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\t': escaped.Append("\\t"); break;
                default:
                    if (char.IsControl(character)) escaped.Append($"\\u{(int)character:X4}");
                    else escaped.Append(character);
                    break;
            }
        }
        return escaped.Append('"').ToString();
    }

    private static bool IsSafePlainScalar(string value)
    {
        if (value.Length == 0 || value.Trim() != value || IsYamlImplicit(value)) return false;
        if (!char.IsLetter(value[0]) && value[0] != '/') return false;
        return value.All(character => char.IsLetterOrDigit(character) || character is ' ' or '.' or '/' or '_' or '-');
    }

    private static bool IsYamlImplicit(string value) => value.Equals("null", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
