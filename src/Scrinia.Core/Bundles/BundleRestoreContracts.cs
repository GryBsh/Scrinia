using Scrinia.Core.Models;

namespace Scrinia.Core.Bundles;

public sealed record BundleTopicRestore(
    string Scope,
    string Topic,
    IReadOnlyList<ArtifactEntry> Entries,
    IReadOnlyDictionary<string, byte[]> ArtifactBytes);

public sealed record BundleFileEntityRestore(string Kind, string Name, byte[] Body, byte[]? Sidecar);

public sealed record BundleRestorePlan(
    IReadOnlyList<BundleTopicRestore> Topics,
    IReadOnlyList<BundleFileEntityRestore> FileEntities,
    bool Overwrite);

public enum BundleRestoreOutcome
{
    Committed
}

public enum BundleImportFailure
{
    InvalidBundle,
    RolledBack,
    RecoveryRequired
}

public sealed class BundleValidationException : InvalidOperationException
{
    public BundleValidationException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class BundleImportException : InvalidOperationException
{
    public BundleImportException(BundleImportFailure failure, Exception innerException, string? journalPath = null)
        : base(CreateMessage(failure, journalPath), innerException)
    {
        Failure = failure;
        JournalPath = journalPath;
    }

    public BundleImportFailure Failure { get; }
    public string? JournalPath { get; }

    private static string CreateMessage(BundleImportFailure failure, string? journalPath) =>
        failure switch
        {
            BundleImportFailure.RolledBack => "Bundle import failed and was rolled back.",
            BundleImportFailure.RecoveryRequired =>
                $"Bundle import requires recovery before another import can run{(journalPath is null ? "." : $": {journalPath}")}",
            _ => "Bundle import failed."
        };
}

public sealed record BundleRestoreResult(
    int TopicCount,
    int EntryCount,
    int FileEntityCount,
    BundleRestoreOutcome Outcome);
