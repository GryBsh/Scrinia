namespace Scrinia.Core.Bundles;

internal sealed record BundleReadLimits
{
    internal long CompressedFileBytes { get; init; } = 256L * 1024 * 1024;
    internal int ZipEntries { get; init; } = 4096;
    internal int LogicalEntities { get; init; } = 1000;
    internal long JsonBytes { get; init; } = 16L * 1024 * 1024;
    internal long BodyBytes { get; init; } = 96L * 1024 * 1024;
    internal long ExpandedBytes { get; init; } = 128L * 1024 * 1024;
    internal long PriorBytes { get; init; } = 128L * 1024 * 1024;
    internal int JournalMembers { get; init; } = 2000;
    internal static BundleReadLimits Default { get; } = new();
}
