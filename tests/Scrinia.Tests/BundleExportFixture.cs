using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Shared setup for the bundle-export discipline tests. Seeds valid store topics and exposes the
/// malformed-topic shape (a trailing-dot segment) that <see cref="MemoryNaming.BuildScopedTopicScope"/>
/// refuses during the export BUILD phase.
/// </summary>
internal static class BundleExportFixture
{
    /// <summary>Writes one valid topic (an index entry plus its NMP/2 body) so an export has content.</summary>
    public static void SeedValidTopic(IMemoryStore store, string topic, string subject, string content)
    {
        string scope = MemoryNaming.BuildScopedTopicScope(topic);
        var entry = new ArtifactEntry(
            Name: subject,
            Uri: "",
            OriginalBytes: content.Length,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            ContentPreview: content);
        store.Upsert(entry, scope);
        store.WriteArtifactAsync(subject, scope, Nmp2ChunkedEncoder.Encode(content))
            .GetAwaiter().GetResult();
    }
}
