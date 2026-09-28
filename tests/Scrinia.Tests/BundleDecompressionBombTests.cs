using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;

namespace Scrinia.Tests;

/// <summary>
/// End-to-end proof that the NMP/2 <c>MaxDecodedBytes</c> decompression-bomb guard fires through the
/// real bundle-import path (<see cref="BundleFormatService.ImportTopicsFromZip"/>) rather than only
/// under a unit-test seam.
///
/// <para>The guard caps the DECOMPRESSED size in <see cref="Nmp2Strategy"/>; a 256 KiB all-<c>A</c>
/// payload compresses to a few hundred bytes, comfortably under the 96 MiB compressed-body bound at
/// <c>BundleFormatService.ReadZipArtifactBytes</c>. Lowering <see cref="Nmp2Strategy.MaxDecodedBytes"/>
/// to 8 KiB around import makes the decode fail fast with an <c>InvalidDataException</c> that import
/// tolerates — the entry is kept with <c>Keywords</c> null rather than aborting the whole import.</para>
///
/// <para>Mutates a process-wide static, so the test lives in a serial collection with
/// <c>DisableParallelization</c> to isolate the lowered cap from every other test in the suite.</para>
/// </summary>
[Collection(BundleDecompressionBombCollection.Name)]
public sealed class BundleDecompressionBombTests
{
    [Fact]
    public void ImportTopicsFromZip_ToleratesDecompressionBomb_WithoutAbortingImport()
    {
        // Arrange — a 256 KiB highly-compressible payload; the NMP/2 artifact compresses to a few
        // hundred bytes, well under the compressed-body bound.
        string payload = new('A', 256 * 1024);
        string bombArtifact = Nmp2ChunkedEncoder.Encode(payload);

        byte[] zipBytes;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                // index.json with NO keywords field — this is what routes import through the decode
                // path instead of treating the entry as never-derived.
                string indexJson = """
                    {"entries":[{"name":"bomb","originalBytes":262144,"chunkCount":1}]}
                    """;
                WriteZipText(zip, "topics/bomb/index.json", indexJson);
                WriteZipBytes(zip, "topics/bomb/bomb.nmp2", Encoding.UTF8.GetBytes(bombArtifact));

                var manifest = new BundleManifest(
                    Version: 2,
                    Exported: DateTimeOffset.UtcNow.ToString("o"),
                    Topics: ["bomb"],
                    TotalEntries: 1);
                WriteZipText(zip, "manifest.json",
                    JsonSerializer.Serialize(manifest, BundleFormatService.DefaultJsonOptions));
            }
            zipBytes = stream.ToArray();
        }

        string importRoot = Path.Combine(Path.GetTempPath(), "scrinia-bomb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(importRoot);
        var importStore = new FileMemoryStore(importRoot);

        try
        {
            int originalCap = Nmp2Strategy.MaxDecodedBytes;
            try
            {
                Nmp2Strategy.MaxDecodedBytes = 8 * 1024;

                (int topicCount, int entryCount, _, _) =
                    ImportFromBytes(zipBytes, importStore);

                topicCount.Should().Be(1, "the import must complete rather than abort on the bomb");
                entryCount.Should().Be(1);
            }
            finally
            {
                Nmp2Strategy.MaxDecodedBytes = originalCap;
            }

            // Assert — read through a COLD store so LoadIndex is a deterministic disk read rather
            // than a race against the importing store's _scopeIndexCache fast lane. The bomb was
            // gracefully tolerated: the entry exists but was never derived, so its Keywords stay
            // null exactly as the bundle carried them.
            using var cold = new FileMemoryStore(importRoot);
            string scope = MemoryNaming.BuildScopedTopicScope("bomb");
            var imported = cold.LoadIndex(scope);

            imported.Should().NotBeEmpty("the topic must be imported");
            imported.Single().Keywords.Should().BeNull(
                because: "the MaxDecodedBytes cap fired during decode and import abstained from " +
                         "enrichment rather than failing the whole bundle");
        }
        finally
        {
            importStore.Dispose();
            try { Directory.Delete(importRoot, recursive: true); } catch { }
        }
    }

    private static (int TopicCount, int EntryCount, List<string> Names, List<string> SkippedCategories)
        ImportFromBytes(byte[] zipBytes, IMemoryStore store)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        return BundleFormatService.ImportTopicsFromZip(zip, store, filterTopics: null, overwrite: true);
    }

    private static void WriteZipText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static void WriteZipBytes(ZipArchive zip, string entryName, byte[] bytes)
    {
        var entry = zip.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}

[CollectionDefinition(BundleDecompressionBombCollection.Name, DisableParallelization = true)]
public sealed class BundleDecompressionBombCollection
{
    public const string Name = "BundleDecompressionBomb";
}
