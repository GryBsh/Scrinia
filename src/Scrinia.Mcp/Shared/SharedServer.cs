using Scrinia.Core.Configuration;
using Scrinia.Core.Models;
using Scrinia.Core;
using System.Globalization;
using System.Text;
using Scrinia.Mcp;

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

    private SharedBaseSummary CreateSummary(string baseName, SharedBaseEntry? entry)
    {
        if (entry is null)
            return new SharedBaseSummary(baseName, null, SharedAvailability.Unavailable, null);

        return new SharedBaseSummary(baseName, entry.Root, SharedAvailability.Unavailable, null);
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
public sealed record SharedEnvelope
{
    public string Status { get; init; } = "success";
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
        output.AppendLine($"status: {envelope.Status}");
        if (envelope.Base is not null) output.AppendLine($"base: {Escape(envelope.Base)}");
        if (envelope.Result is not null) WriteObject(output, "result", envelope.Result, 0);
        if (envelope.Results is not null) WriteObjects(output, "results", envelope.Results);
        if (envelope.Warnings.Length == 0) output.AppendLine("warnings: []");
        else { output.AppendLine("warnings:"); foreach (var warning in envelope.Warnings) WriteObject(output, "-", warning, 1); }
        if (envelope.Error is not null) output.AppendLine($"error: {Escape(envelope.Error)}");
        if (envelope.ErrorCode is not null) output.AppendLine($"errorCode: {envelope.ErrorCode}");
        output.AppendLine(envelope.Instruction is null ? "instruction: null" : $"instruction: {Escape(envelope.Instruction)}");
        output.AppendLine("followUp: []");
        output.AppendLine($"mutation: {envelope.Mutation.ToString().ToLowerInvariant()}");
        if (envelope.Truncated is not null) output.AppendLine($"truncated: {envelope.Truncated.Value.ToString().ToLowerInvariant()}");
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
    }

    private static void WriteObjects(StringBuilder output, string name, IReadOnlyList<object> values)
    {
        output.AppendLine(name + ":");
        foreach (object value in values) WriteObject(output, "-", value, 1);
    }

    private static void WriteObject(StringBuilder output, string name, object value, int indent)
    {
        string prefix = new(' ', indent * 2);
        if (name == "-") output.Append(prefix + "- "); else output.Append(prefix + name + ":\n" + prefix + "  ");
        switch (value)
        {
            case SharedWarning warning:
                output.AppendLine($"base: {Escape(warning.Base)}\n{prefix}  code: {warning.Code}\n{prefix}  diagnostic: {Escape(warning.Diagnostic)}"); break;
            case SharedBaseSummary summary:
                output.AppendLine($"base: {Escape(summary.Base)}\n{prefix}  availability: {summary.Availability.ToString().ToLowerInvariant()}\n{prefix}  generation: {Nullable(summary.Generation)}"); break;
            case RecallResult recall:
                output.AppendLine($"base: {recall.Base}\n{prefix}  path: {recall.Path}\n{prefix}  chunks:");
                foreach (string chunk in recall.Chunks) output.AppendLine($"{prefix}    - {Escape(chunk)}");
                output.AppendLine($"{prefix}  attributes: {{}}\n{prefix}  keywords:");
                foreach (string keyword in recall.Keywords) output.AppendLine($"{prefix}    - {Escape(keyword)}");
                output.AppendLine($"{prefix}  created: {Nullable(recall.Created)}\n{prefix}  updated: {Nullable(recall.Updated)}"); break;
            case MutationResult mutation:
                output.AppendLine($"base: {mutation.Base}");
                if (mutation.Path is not null) output.AppendLine($"{prefix}  path: {mutation.Path}");
                if (mutation.Destination is not null) output.AppendLine($"{prefix}  destination: {mutation.Destination}");
                output.AppendLine($"{prefix}  mutation: {mutation.Mutation.ToString().ToLowerInvariant()}\n{prefix}  generation: {mutation.Generation}");
                if (mutation.Manifest.Length == 0) output.AppendLine($"{prefix}  manifest: []");
                else
                {
                    output.AppendLine($"{prefix}  manifest:");
                    foreach (string path in mutation.Manifest) output.AppendLine($"{prefix}    - {Escape(path)}");
                }
                break;
            case RefreshResult refresh:
                output.AppendLine($"base: {refresh.Base}\n{prefix}  previousGeneration: {Nullable(refresh.PreviousGeneration)}\n{prefix}  newGeneration: {refresh.NewGeneration}\n{prefix}  availability: available\n{prefix}  warnings: []"); break;
            case ListItem item:
                output.AppendLine($"base: {item.Base}\n{prefix}  path: {item.Path}\n{prefix}  kind: persistent\n{prefix}  created: {Nullable(item.Created)}\n{prefix}  updated: {Nullable(item.Updated)}\n{prefix}  attributes: {{}}\n{prefix}  keywords: []\n{prefix}  drift: {item.Drift?.ToString().ToLowerInvariant() ?? "null"}");
                if (item.Files is not null)
                    foreach (FileDriftRef file in item.Files)
                        output.AppendLine($"{prefix}  files:\n{prefix}    - file: {file.File}\n{prefix}      expectedSha256: {Nullable(file.ExpectedSha256)}\n{prefix}      actualSha256: {Nullable(file.ActualSha256)}");
                break;
            default: output.AppendLine(Escape(value.ToString() ?? string.Empty)); break;
        }
    }

    private static string Nullable(string? value) => value is null ? "null" : Escape(value);
    private static string Escape(string value) => value.Contains(": ", StringComparison.Ordinal) || value.Contains('#') || value.Contains('"') ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"" : value;
}
