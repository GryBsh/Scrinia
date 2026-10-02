using System.IO.Compression;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class BundleInterfaceRestoreTests
{
    [Fact]
    public void ImportRestoresThroughIMemoryStoreAndReportsCommittedCounts()
    {
        var store = new RecordingBundleStore();
        byte[] bundle = BundleTestSupport.CreateBundle();

        var result = BundleTestSupport.Import(bundle, store);

        result.TopicCount.Should().Be(1);
        result.EntryCount.Should().Be(1);
        store.ReceivedPlan.Should().NotBeNull();
        store.ReceivedPlan!.Topics.Should().ContainSingle();
        store.ReceivedPlan.Topics[0].Entries.Should().ContainSingle();
        store.ReceivedPlan.FileEntities.Should().HaveCount(1);
    }

    [Fact]
    public void FilteredImportDoesNotRestoreUnselectedFileEntities()
    {
        var store = new RecordingBundleStore();
        byte[] bundle = BundleTestSupport.CreateBundle();

        using var stream = new MemoryStream(bundle);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        BundleFormatService.ImportTopicsFromZip(zip, store, ["not-selected"], overwrite: true);

        store.ReceivedPlan.Should().NotBeNull();
        store.ReceivedPlan!.Topics.Should().BeEmpty();
        store.ReceivedPlan.FileEntities.Should().BeEmpty();
    }

    private sealed class RecordingBundleStore : FakeMemoryStore
    {
        public BundleRestorePlan? ReceivedPlan { get; private set; }
        public override string WorkspaceRoot => Path.GetTempPath();

        public override Task<BundleRestoreResult> RestoreBundleAsync(BundleRestorePlan plan,
            CancellationToken ct = default)
        {
            ReceivedPlan = plan;
            return Task.FromResult(new BundleRestoreResult(
                plan.Topics.Count, plan.Topics.Sum(topic => topic.Entries.Count),
                plan.FileEntities.Count, BundleRestoreOutcome.Committed));
        }
    }
}
