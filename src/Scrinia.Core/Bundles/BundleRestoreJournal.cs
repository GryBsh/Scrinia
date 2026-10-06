using System.Text.Json.Serialization;

namespace Scrinia.Core.Bundles;

internal sealed record BundleJournalMember(
    string RelativePath, string Kind, bool BeforeExists, byte[]? BeforeBytes,
    string? BeforeSha256, bool AfterExists, string? AfterSha256);

internal sealed record BundleRestoreJournal(
    string Format, int Version, string OperationId, string WorkspaceBinding,
    string State, BundleJournalMember[] Members, string ManifestSha256);

// Metadata mode preserves null byte[] payloads distinctly from empty Base64 strings.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BundleRestoreJournal))]
[JsonSerializable(typeof(BundleJournalMember[]))]
internal partial class BundleRecoveryJsonContext : JsonSerializerContext;
