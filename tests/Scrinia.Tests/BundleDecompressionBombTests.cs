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
/// the bundle archive reader. Lowering <see cref="Nmp2Strategy.MaxDecodedBytes"/>
/// to 8 KiB around import makes the decode fail fast and rejects the complete restore plan before
/// any mutation.</para>
///
/// <para>Mutates a process-wide static, so the test lives in a serial collection with
/// <c>DisableParallelization</c> to isolate the lowered cap from every other test in the suite.</para>
/// </summary>
[Collection(BundleDecompressionBombCollection.Name)]
public sealed class BundleDecompressionBombTests
{
    [Fact]
    public void ImportTopicsFromZip_RejectsDecompressionBombBeforeMutation()
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

                Action import = () => ImportFromBytes(zipBytes, importStore);
                import.Should().Throw<BundleValidationException>().WithMessage("*invalid artifact body*");
            }
            finally
            {
                Nmp2Strategy.MaxDecodedBytes = originalCap;
            }

        }
        finally
        {
            importStore.Dispose();
            try { Directory.Delete(importRoot, recursive: true); } catch { }
        }
    }

    private static (int TopicCount, int EntryCount, List<string> Names)
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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection definition; the Collection suffix identifies the shared fixture contract.")]
public sealed class BundleDecompressionBombCollection
{
    public const string Name = "BundleDecompressionBomb";
}
