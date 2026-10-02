using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;

namespace Scrinia.Tests;

public sealed class BundleStrictValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scrinia-bundle-strict-" + Guid.NewGuid().ToString("N"));

    public BundleStrictValidationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("topic-artifact")]
    [InlineData("file-body")]
    [InlineData("file-sidecar")]
    public void EveryDeclaredMemberMustHaveItsArchiveMember(string missing)
    {
        using var store = new FileMemoryStore(_root);
        SeedSentinel(store);
        byte[] bundle = BundleTestSupport.CreateBundle(missingMember: missing);

        Action import = () => BundleTestSupport.Import(bundle, store);

        import.Should().Throw<InvalidOperationException>().WithMessage("*missing*");
        store.LoadIndex(MemoryNaming.BuildScopedTopicScope("sentinel")).Should().ContainSingle();
        store.LoadIndex(MemoryNaming.BuildScopedTopicScope("incoming")).Should().BeEmpty();
        File.ReadAllText(BundleTestSupport.SentinelPath(_root)).Should().Be("sentinel");
    }

    [Fact]
    public void DuplicateDeclaredTopicEntriesRefuseBeforeMutation()
    {
        using var store = new FileMemoryStore(_root);
        SeedSentinel(store);
        byte[] bundle = BundleTestSupport.CreateBundle(duplicateTopicEntry: true);

        Action import = () => BundleTestSupport.Import(bundle, store);

        import.Should().Throw<BundleValidationException>().WithMessage("*duplicate entry*");
        store.LoadIndex(MemoryNaming.BuildScopedTopicScope("incoming")).Should().BeEmpty();
        File.ReadAllText(BundleTestSupport.SentinelPath(_root)).Should().Be("sentinel");
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("nested/name.md")]
    [InlineData("/absolute.md")]
    public void UnsafeDeclaredFileNamesRefuseBeforeMutation(string name)
    {
        using var store = new FileMemoryStore(_root);
        SeedSentinel(store);
        byte[] bundle = BundleTestSupport.CreateBundle(unsafeFileName: name);

        Action import = () => BundleTestSupport.Import(bundle, store);

        import.Should().Throw<InvalidOperationException>().WithMessage("*unsafe*");
        store.LoadIndex(MemoryNaming.BuildScopedTopicScope("incoming")).Should().BeEmpty();
        File.ReadAllText(BundleTestSupport.SentinelPath(_root)).Should().Be("sentinel");
    }

    [Fact]
    public void MalformedMetadataRefusesBeforeAnyTopicOrFileMutation()
    {
        using var store = new FileMemoryStore(_root);
        SeedSentinel(store);
        byte[] bundle = BundleTestSupport.CreateBundle(malformedMetadata: true);

        Action import = () => BundleTestSupport.Import(bundle, store);

        import.Should().Throw<InvalidOperationException>().WithMessage("*malformed metadata*");
        store.LoadIndex(MemoryNaming.BuildScopedTopicScope("incoming")).Should().BeEmpty();
        File.ReadAllText(BundleTestSupport.SentinelPath(_root)).Should().Be("sentinel");
    }

    [Fact]
    public void NonStringFileEntityNameIsReportedAsInvalidBundle()
    {
        using var store = new FileMemoryStore(_root);
        byte[] bundle = BundleTestSupport.CreateBundle(nonStringFileEntity: true);

        Action import = () => BundleTestSupport.Import(bundle, store);

        import.Should().Throw<BundleValidationException>().WithMessage("INVALID_BUNDLE:*");
    }

    private void SeedSentinel(FileMemoryStore store)
    {
        string scope = MemoryNaming.BuildScopedTopicScope("sentinel");
        store.Upsert(new("sentinel", "", 8, 1, DateTimeOffset.UtcNow), scope);
        store.WriteArtifactAsync("sentinel", scope, Nmp2ChunkedEncoder.Encode("sentinel")).GetAwaiter().GetResult();
        Directory.CreateDirectory(Path.GetDirectoryName(BundleTestSupport.SentinelPath(_root))!);
        File.WriteAllText(BundleTestSupport.SentinelPath(_root), "sentinel");
    }
}

internal static class BundleTestSupport
{
    public static string SentinelPath(string root) => Path.Combine(root, ".scrinia", "skills", "sentinel.md");

    public static (int TopicCount, int EntryCount, List<string> Names) Import(
        byte[] bytes, IMemoryStore store)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return BundleFormatService.ImportTopicsFromZip(zip, store, null, overwrite: true);
    }

    public static byte[] CreateBundle(
        string? missingMember = null,
        bool duplicateTopicEntry = false,
        string? unsafeFileName = null,
        bool malformedMetadata = false,
        bool nonStringFileEntity = false)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            string artifact = Nmp2ChunkedEncoder.Encode("incoming");
            string entries = duplicateTopicEntry
                ? "{\"entries\":[{\"name\":\"note\",\"originalBytes\":8,\"chunkCount\":1},{\"name\":\"note\",\"originalBytes\":8,\"chunkCount\":1}]}"
                : "{\"entries\":[{\"name\":\"note\",\"originalBytes\":8,\"chunkCount\":1}]}";
            Write(zip, "topics/incoming/index.json", entries);
            if (missingMember != "topic-artifact")
                Write(zip, "topics/incoming/note.nmp2", artifact);

            string fileName = unsafeFileName ?? "planner.md";
            bool includeFile = unsafeFileName is null || unsafeFileName == "planner.md";
            if (includeFile && missingMember != "file-body")
                Write(zip, "files/skills/planner.md", "# planner");
            if (includeFile && missingMember != "file-sidecar")
                Write(zip, "files/skills/planner.meta.json", malformedMetadata ? "{not-json" : "{\"v\":1}");
            if (unsafeFileName is not null)
                Write(zip, $"files/skills/{unsafeFileName}", "# unsafe");

            var fileEntities = unsafeFileName is null
                ? new Dictionary<string, object[]> { ["skills"] = nonStringFileEntity ? [42] : ["planner.md", "planner.meta.json"] }
                : new Dictionary<string, object[]> { ["skills"] = [unsafeFileName] };
            Write(zip, "manifest.json", JsonSerializer.Serialize(new
            {
                version = 2,
                topics = new[] { "incoming" },
                totalEntries = duplicateTopicEntry ? 2 : 1,
                fileEntities,
            }));
        }
        return stream.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
