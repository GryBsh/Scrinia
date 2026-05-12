using System.Text.Json.Serialization;

namespace Scrinia.Commands;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliListOutput))]
[JsonSerializable(typeof(CliListSummaryOutput))]
[JsonSerializable(typeof(CliSearchOutput))]
[JsonSerializable(typeof(CliShowOutput))]
[JsonSerializable(typeof(CliStoreOutput))]
[JsonSerializable(typeof(CliForgetOutput))]
[JsonSerializable(typeof(CliExportOutput))]
[JsonSerializable(typeof(CliImportOutput))]
[JsonSerializable(typeof(CliBundleOutput))]
[JsonSerializable(typeof(CliConfigOutput))]
[JsonSerializable(typeof(CliErrorOutput))]
[JsonSerializable(typeof(MergeConfig))]
[JsonSerializable(typeof(CliMcpOutput))]
internal partial class CliJsonContext : JsonSerializerContext;

internal sealed record MergeConfig(
    double JaccardThreshold,
    string Resolver,
    string ConflictDir);

internal sealed record CliMemoryEntry(
    string Name,
    int ChunkCount,
    long OriginalBytes,
    int EstimatedTokens,
    string CreatedAt,
    string? UpdatedAt,
    string Description,
    string[]? Tags,
    string? ReviewAfter,
    string? ReviewWhen,
    bool IsStale,
    bool NeedsReview);

internal sealed record CliSearchResult(
    string Type,
    string Name,
    double Score,
    int EstimatedTokens,
    string Description,
    int? ChunkIndex,
    int? TotalChunks);

internal sealed record CliListOutput(CliMemoryEntry[] Memories, int Total, string? Rendered);
internal sealed record CliScopeEntry(string Name, int Count, long TotalBytes);
internal sealed record CliListSummaryOutput(
    int TotalMemories, long TotalBytes, int EstimatedTokens,
    int TopicCount, int EphemeralCount, int StaleCount, int ReviewCount,
    CliScopeEntry[] Scopes, string[]? TopKeywords, string? Rendered);
internal sealed record CliSearchOutput(CliSearchResult[] Results, int Total, string Query);
internal sealed record CliShowOutput(string Name, string Content, int Length);
internal sealed record CliStoreOutput(string Name, int ChunkCount, long OriginalBytes, string Message);
internal sealed record CliForgetOutput(string Name, bool Success, string Message);
internal sealed record CliExportOutput(string Path, string Message);
internal sealed record CliImportOutput(string Message);
internal sealed record CliBundleOutput(string Path, int FileCount, string Topic, long BundleBytes, string Message);
internal sealed record CliConfigOutput(Dictionary<string, string>? Settings, string? Key, string? Value);
internal sealed record CliErrorOutput(string Error);

/// <summary>
/// Shared output shape for the MCP-passthrough commands (append, compact, link,
/// restore, reconcile, guide). The raw YAML response from the MCP handler is
/// preserved verbatim under <see cref="Yaml"/> so callers can re-parse if needed.
/// <see cref="Status"/> mirrors the YAML's status field for quick branching.
/// </summary>
internal sealed record CliMcpOutput(string Action, string Status, string Yaml);
