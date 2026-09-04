using System.Text.Json.Serialization;
using Scrinia.Core.Models;

namespace Scrinia.Commands;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliListOutput))]
[JsonSerializable(typeof(CliListSummaryOutput))]
[JsonSerializable(typeof(CliAttributeEntry))]
[JsonSerializable(typeof(CliSearchOutput))]
[JsonSerializable(typeof(CliShowOutput))]
[JsonSerializable(typeof(CliStoreOutput))]
[JsonSerializable(typeof(CliForgetOutput))]
[JsonSerializable(typeof(CliExportOutput))]
[JsonSerializable(typeof(CliImportOutput))]
[JsonSerializable(typeof(CliBundleOutput))]
[JsonSerializable(typeof(CliConfigOutput))]
[JsonSerializable(typeof(CliReindexOutput))]
[JsonSerializable(typeof(CliErrorOutput))]
[JsonSerializable(typeof(CliMcpOutput))]
[JsonSerializable(typeof(CliConsolidateOutput))]
[JsonSerializable(typeof(CliMigrateOutput))]
[JsonSerializable(typeof(CliStoreMigrateOutput))]
[JsonSerializable(typeof(CliHintOutput))]
[JsonSerializable(typeof(CliSharedBaseOutput))]
[JsonSerializable(typeof(CliSharedBaseListOutput))]
[JsonSerializable(typeof(CliTrackingOutput))]
[JsonSerializable(typeof(MemoryAddress))]
[JsonSerializable(typeof(MemoryAddressKind))]
[JsonSerializable(typeof(MemoryWrite))]
[JsonSerializable(typeof(MemoryError))]
[JsonSerializable(typeof(MemoryAddressError))]
[JsonSerializable(typeof(MemorySearchRequest))]
[JsonSerializable(typeof(MemoryListRequest))]
[JsonSerializable(typeof(ArtifactEntry))]
internal partial class CliJsonContext : JsonSerializerContext;

internal sealed record CliMemoryEntry(
    string Name,
    int ChunkCount,
    long OriginalBytes,
    int EstimatedTokens,
    string CreatedAt,
    string? UpdatedAt,
    string Description,
    Dictionary<string, string[]>? Attributes,
    bool IsStale,
    bool NeedsReview);

internal sealed record CliSearchResult(
    string Type,
    string Name,
    double Score,
    int EstimatedTokens,
    string Description,
    int? ChunkIndex,
    int? TotalChunks,
    Dictionary<string, string[]>? Attributes,
    string? CreatedAt,
    string? UpdatedAt,
    string[]? Anchors);

internal sealed record CliListOutput(CliMemoryEntry[] Memories, int Total, string? Rendered);
internal sealed record CliScopeEntry(string Name, int Count, long TotalBytes);
internal sealed record CliAttributeEntry(string Key, int Count);
internal sealed record CliListSummaryOutput(
    int TotalMemories, long TotalBytes, int EstimatedTokens,
    int TopicCount, int EphemeralCount, int StaleCount, int ReviewCount,
    CliScopeEntry[] Scopes, string[]? TopKeywords, CliAttributeEntry[]? TopAttributes, string? Rendered);
internal sealed record CliSearchOutput(CliSearchResult[] Results, int Total, string Query);
internal sealed record CliShowOutput(string Name, string Content, int Length);
internal sealed record CliStoreOutput(string Name, long OriginalBytes, string Message);
internal sealed record CliForgetOutput(string Name, string Message);
internal sealed record CliExportOutput(string Message);
internal sealed record CliImportOutput(string Message);
internal sealed record CliBundleOutput(string Path, int FileCount, string Topic, long BundleBytes, string Message);
internal sealed record CliConfigOutput(Dictionary<string, string>? Settings, string? Key, string? Value);
internal sealed record CliReindexOutput(int Embedded, int Total, int Skipped, int Failed, string Message);
internal sealed record CliErrorOutput(
    string Error,
    string? Category = null,
    string? Code = null,
    string? Field = null,
    long? Limit = null,
    long? Actual = null,
    int? ExitClass = null);

/// <summary>
/// Shared output shape for the MCP-passthrough commands (append, compact, link,
/// restore, guide). The raw YAML response from the MCP handler is
/// preserved verbatim under <see cref="Yaml"/> so callers can re-parse if needed.
/// <see cref="Status"/> mirrors the YAML's status field for quick branching.
/// </summary>
internal sealed record CliMcpOutput(string Action, string Status, string Yaml);

/// <summary>Output of the consolidate command. Status: "completed", "preview" (dry-run), or "skipped" (debounce).</summary>
internal sealed record CliConsolidateOutput(
    string Status,
    string? Reason,
    int TotalMemories,
    long TotalBytes,
    int StaleCount,
    int CompactionCandidates,
    string[] Compacted);

/// <summary>Output of the attributes-migration command (<c>scri migrate_attributes</c>).</summary>
internal sealed record CliMigrateOutput(int Total, int Migrated, int Skipped, bool DryRun);

/// <summary>Output of the store-layout migration command (<c>scri migrate</c>).</summary>
internal sealed record CliStoreMigrateOutput(
    bool Ran,
    int Moved,
    int Conflicts,
    string? BackupPath,
    bool BackupSkipped,
    bool DryRun);

/// <summary>Output of <c>scri hint --json</c> — the raw structured HintResult shape for
/// programmatic consumers, distinct from the hook envelope Hint.BuildHookEnvelope emits.</summary>
internal sealed record CliHintOutput(int Count, CliHintMatch[] Matches);

internal sealed record CliHintMatch(string Scope, string Name, double Score);
