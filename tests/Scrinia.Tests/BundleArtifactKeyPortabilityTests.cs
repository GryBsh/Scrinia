using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class BundleArtifactKeyPortabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "scrinia-bundle-portability-" + Guid.NewGuid().ToString("N"));

    public BundleArtifactKeyPortabilityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ExportedIndexContainsLogicalAddressAndPortableKeyOnly()
    {
        using var source = new FileMemoryStore(Path.Combine(_root, "source"));
        var entry = new ArtifactEntry(
            MemoryAddress.Parse("/portable/note"),
            "artifacts/portable/note.nmp2", 4, 1, DateTimeOffset.UnixEpoch, "note");
        source.Upsert(entry, "local:portable");
        source.WriteArtifactAsync("note", "local:portable", "body").GetAwaiter().GetResult();

        byte[] bundle = Export(source);
        using var stream = new MemoryStream(bundle);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("topics/portable/index.json")!.Open());
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        JsonElement serialized = document.RootElement.GetProperty("entries")[0];

        serialized.GetProperty("address").GetString().Should().Be("/portable/note");
        serialized.GetProperty("artifactKey").GetString().Should().Be("artifacts/portable/note.nmp2");
        serialized.EnumerateObject().Select(property => property.Value.ToString())
            .Should().NotContain(value => value.Contains(_root, StringComparison.OrdinalIgnoreCase));
        serialized.EnumerateObject().Select(property => property.Value.ToString())
            .Should().NotContain(value => value.Contains("file://", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/rooted")]
    [InlineData("C:/workspace/note.nmp2")]
    [InlineData("\\\\server\\share\\note.nmp2")]
    [InlineData("artifacts/../note.nmp2")]
    [InlineData("file:///workspace/note.nmp2")]
    [InlineData("https://host/note.nmp2")]
    public void ImportRejectsPhysicalOrTraversalArtifactKeysBeforeWriting(string artifactKey)
    {
        using var target = new FileMemoryStore(Path.Combine(_root, "target"));
        byte[] bundle = CreateBundle(artifactKey);

        Action import = () =>
        {
            using var stream = new MemoryStream(bundle);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            BundleFormatService.ImportTopicsFromZip(zip, target, null, overwrite: true);
        };

        import.Should().Throw<InvalidOperationException>().WithMessage("*ArtifactKey*");
        target.LoadIndex("local:portable").Should().BeEmpty();
    }

    private byte[] Export(FileMemoryStore store)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            BundleFormatService.ExportTopicsToZip(zip, store, ["portable"]);
        return stream.ToArray();
    }

    private static byte[] CreateBundle(string artifactKey)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = new JsonObject
            {
                ["entries"] = new JsonArray(new JsonObject
                {
                    ["address"] = "/portable/note",
                    ["artifactKey"] = artifactKey,
                    ["originalBytes"] = 4,
                    ["chunkCount"] = 1,
                    ["createdAt"] = DateTimeOffset.UnixEpoch.ToString("o"),
                    ["contentPreview"] = "note",
                }),
            };
            WriteText(zip, "topics/portable/index.json", index.ToJsonString());
            WriteText(zip, "topics/portable/note.nmp2", "body");
            WriteText(zip, "manifest.json", "{\"version\":1,\"topics\":[\"portable\"],\"totalEntries\":1}");
        }
        return stream.ToArray();
    }

    private static void WriteText(ZipArchive zip, string name, string value)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(value);
    }
}
