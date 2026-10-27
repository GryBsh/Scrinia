using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class CompactConcurrencyTests
{
    [Fact]
    public async Task ConcurrentInitialEphemeralAppends_PreserveEverySuccessfulWrite()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var address = MemoryAddress.Parse("~initial-append-race");
        const int writers = 8;
        // Give every writer meaningful derivation/encoding work after the absent-entry check.
        // Dedicated workers and a shared gate exercise creation without elapsed-time assertions.
        string payload = string.Concat(Enumerable.Repeat("retained evidence content ", 12_000));
        string[] expected = Enumerable.Range(0, writers)
            .Select(index => $"writer-{index}|{payload}").ToArray();
        MemoryWrite[] writes = expected.Select(content => MemoryWrite.Create(address, "local", [content])).ToArray();
        using var start = new Barrier(writers + 1);
        Task<AppendStoreResult>[] appends = writes.Select(write => Task.Factory.StartNew(async () =>
        {
            start.SignalAndWait();
            return await store.AppendAsync(write);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();

        start.SignalAndWait();
        AppendStoreResult[] results = await Task.WhenAll(appends);

        var (chunks, _) = Nmp2ChunkedEncoder.DecodeAllChunksVerified(store.GetEphemeral(address)!.Artifact);
        chunks.Should().BeEquivalentTo(expected, "every successful initial append must remain in the memory");
        results.Should().ContainSingle(result => result.Outcome == AppendStoreOutcome.Created,
            "only the writer that atomically publishes an absent memory creates it");
    }

    [Fact]
    public async Task Compact_RefusesAStoreWithoutAnAtomicCapability()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryStoreContext.Current = new FakeMemoryStore();

        var result = await new ScriniaMcpTools().Compact(MemoryAddress.Parse("/unsafe-store"));

        result.ErrorCode.Should().Be(ErrorCodes.NotSupported);
    }

    [Fact]
    public async Task EphemeralCompaction_AndConcurrentAppendsPreserveAllContent()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var address = MemoryAddress.Parse("~concurrent");
        await store.StoreAsync(MemoryWrite.Create(address, "local", ["first", "second"]));
        using var start = new ManualResetEventSlim();
        Task[] appends = Enumerable.Range(0, 24).Select(index => Task.Run(async () =>
        {
            start.Wait();
            await store.AppendAsync(MemoryWrite.Create(address, "local", [$"|{index}|"]));
        })).ToArray();
        Task compact = Task.Run(async () =>
        {
            start.Wait();
            for (int i = 0; i < 8; i++)
                await store.CompactAsync(address);
        });
        start.Set();
        await Task.WhenAll([.. appends, compact]);

        var (chunks, _) = Nmp2ChunkedEncoder.DecodeAllChunksVerified(store.GetEphemeral(address)!.Artifact);
        string content = string.Concat(chunks);
        content.Should().StartWith("firstsecond");
        foreach (int index in Enumerable.Range(0, 24))
            content.Should().Contain($"|{index}|");
    }

    [Fact]
    public async Task TypedEphemeralUpdate_RetriesAfterAConcurrentAppend()
    {
        using var scope = new TestHelpers.StoreScope();
        IMemoryStore store = TestHelpers.Store;
        var address = MemoryAddress.Parse("~metadata-race");
        await store.StoreAsync(MemoryWrite.Create(address, "local", ["first"]));
        bool appended = false;

        store.TryUpdateEphemeral(address, current =>
        {
            if (!appended)
            {
                appended = true;
                store.AppendAsync(MemoryWrite.Create(address, "local", ["second"]))
                    .GetAwaiter().GetResult();
            }
            return current with { ContentPreview = "curated description" };
        }).Should().BeTrue();

        var updated = store.GetEphemeral(address)!;
        var (chunks, _) = Nmp2ChunkedEncoder.DecodeAllChunksVerified(updated.Artifact);
        chunks.Should().Equal("first", "second");
        updated.ContentPreview.Should().Be("curated description");
    }

    [Theory]
    [InlineData("/compact-race")]
    [InlineData("~compact-race")]
    public async Task Compact_DoesNotOverwriteAnAppendAfterItsSnapshot(string path)
    {
        using var scope = new TestHelpers.StoreScope();
        var real = (FileMemoryStore)TestHelpers.Store;
        var address = MemoryAddress.Parse(path);
        await real.StoreAsync(MemoryWrite.Create(address, "local", ["first", "second"]));
        var interleaving = new AppendAfterReadStore(real, address);
        MemoryStoreContext.Current = interleaving;

        var result = await new ScriniaMcpTools().Compact(address);

        result.Status.Should().Be("success");
        interleaving.Appended.Should().BeTrue();
        var artifact = await real.ReadArtifactAsync(address);
        var (chunks, _) = Nmp2ChunkedEncoder.DecodeAllChunksVerified(artifact);
        string.Concat(chunks).Should().Be("firstsecondthird");
        var metadata = path.StartsWith('~')
            ? real.GetEphemeral(address)!.ToArtifactEntry()
            : real.LoadIndex().Single();
        metadata.ContentPreview.Should().Be("Latest curated description");
        metadata.AgentKeywords.Should().Contain("curated-term");
        metadata.Attributes!["owner"].Should().Contain("latest-owner");
    }

    private sealed class AppendAfterReadStore(FileMemoryStore real, MemoryAddress address)
        : FakeMemoryStore, IAtomicMemoryCompactor
    {
        public bool Appended { get; private set; }

        public async Task<CompactMemoryResult> CompactAsync(MemoryAddress name, int keepRecent = 0,
            CancellationToken cancellationToken = default)
        {
            // The new operation owns the snapshot; the append completes before it takes ownership.
            // The legacy read callbacks below still expose the lost-update window if MCP regresses.
            await AppendOnce();
            return await real.CompactAsync(name, keepRecent, cancellationToken);
        }

        private async Task AppendOnce()
        {
            if (Appended) return;
            Appended = true;
            await real.AppendAsync(MemoryWrite.Create(address, ["third"],
                new Dictionary<string, IReadOnlyList<string>> { ["owner"] = ["latest-owner"] },
                ["curated-term"], description: "Latest curated description"));
        }

        public override async Task<string> ReadArtifactAsync(MemoryAddress name, CancellationToken ct = default)
        {
            string snapshot = await real.ReadArtifactAsync(name, ct);
            await AppendOnce();
            return snapshot;
        }

        public override EphemeralEntry? GetEphemeral(MemoryAddress name)
        {
            var snapshot = real.GetEphemeral(name);
            AppendOnce().GetAwaiter().GetResult();
            return snapshot;
        }

        public override IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local") => real.LoadIndex(scope);
        public override Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default) =>
            real.StoreAsync(write, ct);
    }
}
