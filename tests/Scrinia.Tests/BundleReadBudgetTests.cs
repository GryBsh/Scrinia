using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class BundleReadBudgetTests
{
    [Fact]
    public async Task OversizedManifestIsRejectedBeforeRestore()
    {
        using var scope = new TestHelpers.StoreScope();
        using var bytes = new MemoryStream();
        using (var writer = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        using (var output = new StreamWriter(writer.CreateEntry("manifest.json").Open()))
        {
            output.Write(new string(' ', 16 * 1024 * 1024));
            output.Write("""{"version":2,"topics":[],"totalEntries":0}""");
        }
        bytes.Position = 0;
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Func<Task> import = () => BundleFormatService.ImportTopicsFromZipAsync(
            zip, TestHelpers.Store, null, false);
        await import.Should().ThrowAsync<BundleValidationException>();
        File.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia",
            ".bundle-restore-journal.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData("index")]
    [InlineData("metadata")]
    public async Task PublicImportRejectsOversizedJsonBeforeMutation(string member)
    {
        using var scope = new TestHelpers.StoreScope();
        using var bytes = CreateArchive(writer =>
        {
            Write(writer, "manifest.json", member == "index"
                ? """{"version":2,"topics":["notes"],"totalEntries":0}"""
                : """{"version":2,"topics":[],"totalEntries":0,"fileEntities":{"skills":["x.md","x.meta.json"]}}""");
            Write(writer, member == "index" ? "topics/notes/index.json" : "files/skills/x.meta.json",
                new string(' ', 16 * 1024 * 1024) + (member == "index" ? "{\"entries\":[]}" : "{\"v\":1}"));
            if (member == "metadata") Write(writer, "files/skills/x.md", "body");
        });
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Func<Task> import = () => BundleFormatService.ImportTopicsFromZipAsync(zip, TestHelpers.Store, null, false);

        await import.Should().ThrowAsync<BundleValidationException>().WithMessage("INVALID_BUNDLE:*");
        AssertNoMutation(scope);
    }

    [Theory]
    [InlineData(96 * 1024 * 1024, true)]
    [InlineData(96 * 1024 * 1024 + 1, false)]
    public async Task PublicImportEnforcesFileBodyBoundary(int size, bool accepted)
    {
        var store = new PlanRecordingStore();
        using var bytes = CreateArchive(writer =>
        {
            Write(writer, "manifest.json", """{"version":2,"topics":[],"totalEntries":0,"fileEntities":{"skills":["x.md"]}}""");
            using Stream body = writer.CreateEntry("files/skills/x.md").Open();
            byte[] buffer = new byte[81920];
            for (int remaining = size; remaining > 0; remaining -= Math.Min(remaining, buffer.Length))
                body.Write(buffer, 0, Math.Min(remaining, buffer.Length));
        });
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Func<Task> import = () => BundleFormatService.ImportTopicsFromZipAsync(zip, store, null, false);
        if (accepted)
        {
            await import();
            store.Plan!.FileEntities.Single().Body.Length.Should().Be(size);
        }
        else
        {
            await import.Should().ThrowAsync<BundleValidationException>();
            store.Plan.Should().BeNull();
        }
    }

    [Fact]
    public void AggregateBudgetRejectsManySmallEntries()
    {
        using var bytes = CreateArchive(writer => { Write(writer, "a.json", "12345"); Write(writer, "b.json", "12345"); });
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        var budget = new BundleReadBudget(BundleReadLimits.Default with { JsonBytes = 8, ExpandedBytes = 9 });
        Action admit = () => budget.AdmitArchive(zip);
        admit.Should().Throw<BundleValidationException>();
    }

    [Fact]
    public void RepeatedPhysicalMemberReadsConsumeItsBytesOnce()
    {
        using var bytes = CreateArchive(writer => { Write(writer, "a.json", "12345"); Write(writer, "b.json", "67890"); });
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        var budget = new BundleReadBudget(BundleReadLimits.Default with { ExpandedBytes = 10 });
        budget.AdmitArchive(zip);
        byte[] first = budget.Read(zip.GetEntry("a.json")!, 5);
        budget.Read(zip.GetEntry("a.json")!, 5).Should().BeSameAs(first);
        budget.Read(zip.GetEntry("b.json")!, 5).Should().Equal("67890"u8.ToArray());
    }

    [Theory]
    [InlineData("a.json")]
    [InlineData("A.JSON")]
    public void DuplicateOrCaseCollidingPhysicalNamesRefuse(string duplicate)
    {
        using var bytes = CreateArchive(writer => { Write(writer, "a.json", "{}"); Write(writer, duplicate, "{}"); });
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Action admit = () => new BundleReadBudget(BundleReadLimits.Default).AdmitArchive(zip);
        admit.Should().Throw<BundleValidationException>().WithMessage("*duplicate*");
    }

    [Fact]
    public async Task UnusedEntriesCountTowardArchiveEntryLimit()
    {
        using var scope = new TestHelpers.StoreScope();
        using var bytes = CreateArchive(writer => { Write(writer, "manifest.json", "{}"); Write(writer, "unused", ""); });
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Func<Task> import = () => BundleFormatService.ImportTopicsFromZipAsync(zip, TestHelpers.Store, null, false,
            BundleReadLimits.Default with { ZipEntries = 1 }, default);
        await import.Should().ThrowAsync<BundleValidationException>().WithMessage("*too many members*");
        AssertNoMutation(scope);
    }

    [Fact]
    public async Task OversizedMemberInUnselectedTopicRefusesBeforeMutation()
    {
        using var scope = new TestHelpers.StoreScope();
        using var bytes = CreateArchive(writer =>
        {
            Write(writer, "manifest.json", """{"version":2,"topics":["notes"],"totalEntries":0}""");
            Write(writer, "topics/notes/index.json", new string(' ', 200) + "{\"entries\":[]}");
        });
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Func<Task> import = () => BundleFormatService.ImportTopicsFromZipAsync(zip, TestHelpers.Store, ["unselected"], false,
            BundleReadLimits.Default with { JsonBytes = 100 }, default);
        await import.Should().ThrowAsync<BundleValidationException>().WithMessage("INVALID_BUNDLE:*");
        AssertNoMutation(scope);
    }

    [Fact]
    public void ShorterThanDeclaredMemberRefusesBeforeReturningBytes()
    {
        using var bytes = CreateArchive(writer => Write(writer, "a.json", "{}"));
        byte[] corrupt = bytes.ToArray();
        int central = FindCentralHeader(corrupt);
        BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(central + 24), 3);
        using var damaged = new MemoryStream(corrupt);
        using var zip = new ZipArchive(damaged, ZipArchiveMode.Read);
        var budget = new BundleReadBudget(BundleReadLimits.Default);
        budget.AdmitArchive(zip);
        Action read = () => budget.Read(zip.Entries[0], 10);
        read.Should().Throw<BundleValidationException>().WithMessage("*length does not match*");
    }

    [Fact]
    public void DeclaredExpandedLengthArithmeticOverflowRefuses()
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            foreach (byte name in new byte[] { (byte)'a', (byte)'b' })
            {
                writer.Write(0x02014b50u); writer.Write((ushort)45); writer.Write((ushort)45);
                writer.Write(new byte[12]); writer.Write(0u); writer.Write(uint.MaxValue);
                writer.Write((ushort)1); writer.Write((ushort)12); writer.Write(new byte[14]);
                writer.Write(name); writer.Write((ushort)1); writer.Write((ushort)8); writer.Write(long.MaxValue);
            }
            int centralLength = (int)bytes.Length;
            writer.Write(0x06054b50u); writer.Write(0u); writer.Write((ushort)2); writer.Write((ushort)2);
            writer.Write(centralLength); writer.Write(0u); writer.Write((ushort)0);
        }
        bytes.Position = 0;
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        var budget = new BundleReadBudget(BundleReadLimits.Default with { BodyBytes = long.MaxValue, ExpandedBytes = long.MaxValue });
        Action admit = () => budget.AdmitArchive(zip);
        admit.Should().Throw<BundleValidationException>().WithMessage("*overflowed*");
    }

    [Fact]
    public async Task LogicalEntityLimitIncludesFileEntitiesBeforeRestore()
    {
        using var scope = new TestHelpers.StoreScope();
        using var bytes = new MemoryStream(BundleTestSupport.CreateBundle());
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Func<Task> import = () => BundleFormatService.ImportTopicsFromZipAsync(zip, TestHelpers.Store, null, false,
            BundleReadLimits.Default with { LogicalEntities = 1 }, default);
        await import.Should().ThrowAsync<BundleValidationException>().WithMessage("*too many logical entities*");
        AssertNoMutation(scope);
    }

    [Fact]
    public void PathImportRejectsCompressedFileSizeBeforeZipParsingAndReleasesFile()
    {
        using var scope = new TestHelpers.StoreScope();
        string path = Path.Combine(scope.WorkspaceDir, "large.zip");
        File.WriteAllBytes(path, new byte[11]);
        Action open = () => ScriniaMcpTools.OpenBundleForImport(path,
            BundleReadLimits.Default with { CompressedFileBytes = 10 });
        open.Should().Throw<BundleValidationException>().WithMessage("*compressed file size*");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static MemoryStream CreateArchive(Action<ZipArchive> write)
    {
        var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) write(zip);
        bytes.Position = 0;
        return bytes;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using Stream output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        output.Write(Encoding.UTF8.GetBytes(content));
    }

    private static int FindCentralHeader(byte[] bytes)
    {
        for (int index = 0; index < bytes.Length - 4; index++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index)) == 0x02014b50) return index;
        throw new InvalidOperationException("Test archive has no central header.");
    }

    private static void AssertNoMutation(TestHelpers.StoreScope scope)
    {
        File.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json")).Should().BeFalse();
        Directory.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia", "skills")).Should().BeFalse();
        TestHelpers.Store.LoadIndex(MemoryNaming.BuildScopedTopicScope("incoming")).Should().BeEmpty();
    }

    private sealed class PlanRecordingStore : FakeMemoryStore
    {
        internal BundleRestorePlan? Plan { get; private set; }
        public override Task<BundleRestoreResult> RestoreBundleAsync(BundleRestorePlan plan, CancellationToken ct = default)
        {
            Plan = plan;
            return Task.FromResult(new BundleRestoreResult(0, 0, plan.FileEntities.Count, BundleRestoreOutcome.Committed));
        }
    }
}
