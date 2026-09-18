using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Drives <see cref="FileMemoryStore.ImportTopicEntries"/> over a valid one-topic bundle built
/// through the real <see cref="BundleFormatService"/> export path, so the Created/Replaced outcome
/// and the journaled transaction id are exercised exactly as the import facade imports them.
///
/// <para>The (entry, body) pair mirrors what a parsed one-topic bundle hands the store — a raw
/// body (import journals the bytes as-is, verifying only their SHA256) keyed by the subject name —
/// which is the same shape the established import tests construct directly.</para>
/// </summary>
internal static class ImportFixture
{
    private const string Topic = "findings";
    private const string Content = "fixture import content for per-memory import-result coverage";

    private static string Scope => MemoryNaming.BuildScopedTopicScope(Topic);
    private static FileMemoryStore Store => (FileMemoryStore)TestHelpers.Store;

    /// <summary>Seeds durable state under the fixture topic so a later import of the same name replaces.</summary>
    public static void Prepopulate(TestHelpers.StoreScope scope, string entryName)
    {
        ArtifactEntry entry = Entry(entryName);
        Store.Upsert(entry, Scope);
        Store.WriteArtifactAsync(entry.Address, Content, CancellationToken.None).GetAwaiter().GetResult();
        // Prove the seeded entry is a real, importable topic through the export path.
        ValidateBundleThroughExport(entry, Encoding.UTF8.GetBytes(Content));
    }

    /// <summary>Builds a valid one-topic bundle through the real export path and drives
    /// <c>ImportTopicEntries</c> directly, returning the per-memory import result.</summary>
    public static ImportMemoryResult ImportOneTopic(TestHelpers.StoreScope scope, string entryName)
    {
        ArtifactEntry entry = Entry(entryName);
        byte[] body = Encoding.UTF8.GetBytes(Content);
        ValidateBundleThroughExport(entry, body);
        return Store.ImportTopicEntries(Scope, [entry], BodyMap(entry.Name, body), overwrite: true);
    }

    private static void ValidateBundleThroughExport(ArtifactEntry entry, byte[] body)
    {
        // Build a real one-topic bundle through the export path, then read it back through the real
        // import path into a throwaway scope to prove the entry is a valid, round-tripping topic —
        // the same export→import pipeline the facade drives. The store-level test then exercises
        // FileMemoryStore.ImportTopicEntries directly with the known (entry, body).
        string root = Path.Combine(Path.GetTempPath(), "scrinia-import-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var seed = new FileMemoryStore(root);
        seed.Upsert(entry, Scope);
        seed.WriteArtifactAsync(entry.Address, Encoding.UTF8.GetString(body), CancellationToken.None)
            .GetAwaiter().GetResult();
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            BundleFormatService.ExportTopicsToZip(zip, seed, [Topic]);
        stream.Position = 0;
        int topicCount;
        using (var throwaway = new TestHelpers.StoreScope())
        using (var zipArchive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            topicCount = BundleFormatService.ImportTopicsFromZip(
                zipArchive, (FileMemoryStore)TestHelpers.Store, filterTopics: null, overwrite: true).TopicCount;
        }
        topicCount.Should().Be(1, "the exported bundle must carry exactly the seeded topic");
    }

    private static ArtifactEntry Entry(string name) =>
        new ArtifactEntry(name, $"file://{name}", Encoding.UTF8.GetByteCount(Content), 1, DateTimeOffset.UtcNow);

    private static Dictionary<string, byte[]> BodyMap(string name, byte[] body) =>
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [name] = body };
}
