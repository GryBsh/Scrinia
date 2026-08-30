using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
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
    public async Task ExportedIndexContainsLogicalAddressAndPortableKeyOnly()
    {
        using var source = new FileMemoryStore(Path.Combine(_root, "source"));
        await source.StoreAsync(new MemoryWrite(
            "/portable/note", "local:portable", ["body"], Attributes: [], Keywords: []));

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

    [Fact]
    public async Task QualifiedPersistentAddress_RoundTripsContentAndScopeIntoASecondWorkspace()
    {
        string sourceRoot = Path.Combine(_root, "qualified-source");
        string targetRoot = Path.Combine(_root, "qualified-target");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        using var source = new FileMemoryStore(sourceRoot);
        using var target = new FileMemoryStore(targetRoot);
        await source.StoreAsync(new MemoryWrite(
            "/portable/note", "local:portable", ["qualified body"], Attributes: [], Keywords: []));

        using var zip = OpenZip(Export(source));
        BundleFormatService.ImportTopicsFromZip(zip, target, filterTopics: null, overwrite: true);

        ArtifactEntry imported = target.LoadIndex("local:portable").Should().ContainSingle().Subject;
        imported.Address.Should().Be(MemoryAddress.Parse("/portable/note"));
        imported.ArtifactKey.Should().Be("artifacts/portable/note.nmp2");
        string artifact = await target.ReadArtifactAsync(MemoryAddress.Parse("/portable/note"));
        Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact).Chunks
            .Should().Equal("qualified body");
    }

    [Fact]
    public void LegacyPhysicalLocationMetadata_IsDroppedAcrossImportAndExport_WhileUnknownMetadataSurvives()
    {
        string targetRoot = Path.Combine(_root, "legacy-target");
        Directory.CreateDirectory(targetRoot);
        using var target = new FileMemoryStore(targetRoot);

        using (var zip = OpenZip(CreateLegacyBundleWithPhysicalMetadata()))
            BundleFormatService.ImportTopicsFromZip(zip, target, filterTopics: null, overwrite: true);

        ArtifactEntry imported = target.LoadIndex("local:portable").Should().ContainSingle().Subject;
        imported.Unknown.Should().ContainKey("futureMetadata");
        imported.Unknown!["futureMetadata"].GetString().Should().Be("retain-me");
        imported.Unknown.Keys.Should().NotContain(key =>
            key.Equals("uri", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("artifactPath", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("filePath", StringComparison.OrdinalIgnoreCase));

        using var exported = OpenZip(Export(target));
        using var reader = new StreamReader(exported.GetEntry("topics/portable/index.json")!.Open());
        JsonObject exportedEntry = JsonNode.Parse(reader.ReadToEnd())!["entries"]![0]!.AsObject();
        exportedEntry.Should().ContainKey("futureMetadata");
        exportedEntry["futureMetadata"]!.GetValue<string>().Should().Be("retain-me");
        exportedEntry.Should().NotContainKey("uri");
        exportedEntry.Should().NotContainKey("artifactPath");
        exportedEntry.Should().NotContainKey("filePath");
        exportedEntry.ToJsonString().Should().NotContain(targetRoot);
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
        string targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);
        using var target = new FileMemoryStore(targetRoot);
        byte[] bundle = CreateBundle(artifactKey);

        Action import = () =>
        {
            using var stream = new MemoryStream(bundle);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            BundleFormatService.ImportTopicsFromZip(zip, target, null, overwrite: true);
        };

        import.Should().Throw<InvalidOperationException>().WithMessage("*ArtifactKey*");
        target.LoadIndex("local:portable").Should().BeEmpty();
        Directory.EnumerateFiles(target.WorkspaceRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.Contains("portable", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
    }

    private byte[] Export(FileMemoryStore store)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            BundleFormatService.ExportTopicsToZip(zip, store, ["portable"]);
        return stream.ToArray();
    }

    private static ZipArchive OpenZip(byte[] bytes) =>
        new(new MemoryStream(bytes), ZipArchiveMode.Read);

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

    private static byte[] CreateLegacyBundleWithPhysicalMetadata()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = new JsonObject
            {
                ["entries"] = new JsonArray(new JsonObject
                {
                    ["address"] = "/portable/note",
                    ["artifactKey"] = "artifacts/portable/note.nmp2",
                    ["originalBytes"] = 4,
                    ["chunkCount"] = 1,
                    ["createdAt"] = DateTimeOffset.UnixEpoch.ToString("o"),
                    ["contentPreview"] = "note",
                    ["uri"] = "file:///source/workspace/.scrinia/memories/portable/note.nmp2",
                    ["artifactPath"] = "C:/source/workspace/.scrinia/memories/portable/note.nmp2",
                    ["filePath"] = "\\\\source\\workspace\\note.nmp2",
                    ["futureMetadata"] = "retain-me",
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
