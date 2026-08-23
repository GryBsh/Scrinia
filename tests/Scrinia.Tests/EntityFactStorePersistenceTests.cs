using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Scrinia.Tests;

[Collection(BackgroundWorkCollection.Name)]
public sealed class EntityFactStorePersistenceTests
{
    private readonly ITestOutputHelper _output;

    public EntityFactStorePersistenceTests(ITestOutputHelper output) => _output = output;

    private static ArtifactEntry Entry(string name, DateTimeOffset created,
        string[]? keywords = null, Dictionary<string, string[]>? attributes = null) =>
        new(Name: name, Uri: $"file://{name}", OriginalBytes: 1, ChunkCount: 1,
            CreatedAt: created, Keywords: keywords, Attributes: attributes);

    [Fact]
    public async Task GetEntityFactStore_AggregatesAcrossScopes_AndDefersSidecarUntilDrain()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["person"] = ["maria"] }), "local");
        store.Upsert(Entry("b", d, attributes: new() { ["person"] = ["maria"] }), "local:findings");

        var facts = store.GetEntityFactStore();
        facts.MentionCount(EntityRef.Of("person", "maria")).Should().Be(2); // cross-scope merge
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "facts.json")).Should().BeFalse();
        store.HasDirtyFactCache.Should().BeTrue();

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        File.Exists(Path.Combine(store.GetStoreDirForScope("local"), "facts.json")).Should().BeTrue();
    }

    [Fact]
    public void GetEntityFactStore_RebuildsWhenSidecarTokenStale()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        _ = store.GetEntityFactStore(); // queues facts.json
        store.InvalidateCaches(); // force the next read to inspect the sidecar again
        // Corrupt the sidecar with a stale token; the index no longer matches it.
        string sidecar = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        File.WriteAllText(sidecar, EntityFactSidecar.Serialize("STALE_TOKEN", []));

        var facts = store.GetEntityFactStore(); // must rebuild from entries, not trust the file
        facts.MentionCount(EntityRef.Of("topic", "auth")).Should().Be(1);
    }

    [Fact]
    public async Task DirtyFactFlush_RetainsDirtyStateWhenMutationRacesFinalPayload()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["old"] }), "local");
        _ = store.GetEntityFactStore();

        string factsPath = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        var writeReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int mutationInjected = 0;
        Action<string, byte[]>? previousObserver = FileMemoryStore.WriteObserver;
        FileMemoryStore.WriteObserver = (path, bytes) =>
        {
            previousObserver?.Invoke(path, bytes);
            if (!path.Equals(factsPath, StringComparison.OrdinalIgnoreCase)
                || Interlocked.Exchange(ref mutationInjected, 1) != 0) return;
            store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
                attributes: new() { ["topic"] = ["newest"] }), "local");
            writeReached.TrySetResult(store.HasDirtyFactCache);
        };

        try
        {
            (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await writeReached.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue(
                "the mutation itself must retain dirty state before the writer's final check");
            string finalPayload = File.ReadAllText(factsPath);
            finalPayload.Should().Contain("newest");
            var authoritative = store.ListScoped(null)
                .Where(item => item.Scope.Equals("local", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var expectedMemories = authoritative
                .Select(EntityFactDerivation.Derive)
                .Where(memory => memory is not null)
                .Select(memory => memory!)
                .ToArray();
            string expectedToken = EntityFactSidecar.ComputeToken(authoritative.Select(item =>
                ($"local|{item.Entry.Name}", item.Entry.UpdatedAt ?? item.Entry.CreatedAt,
                    IndependentEntitySignature(item.Entry))));
            EntityFactSidecar.Serialize(expectedToken, expectedMemories)
                .Should().Be(finalPayload,
                    "the drained bytes must equal an independently rebuilt serialization of the final authoritative entries");
            store.HasDirtyFactCache.Should().BeFalse();
        }
        finally
        {
            FileMemoryStore.WriteObserver = previousObserver;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(46)]
    public async Task SyntheticMutationScaling_CoalescesToOneFinalFactsWritePerDirtyScope(int mutationCount)
    {
        using var scope = new TestHelpers.StoreScope();
        using var writes = new FileWriteCounter(scope.WorkspaceDir);
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var created = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        for (int index = 0; index < mutationCount; index++)
        {
            store.Upsert(Entry($"memo-{index}", created,
                attributes: new() { ["topic"] = [$"topic-{index}"] }), "local:measure");
        }

        _ = store.GetEntityFactStore();
        writes.FactsJsonWrites.Should().BeEmpty();

        writes.Reset();
        for (int index = 0; index < mutationCount; index++)
        {
            store.Upsert(Entry($"memo-{index}", created,
                attributes: new() { ["topic"] = [$"updated-{index}"] }), "local:measure");
            _ = store.GetEntityFactStore();
        }

        writes.FactsJsonWrites.Should().BeEmpty();
        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        writes.FactsJsonWrites.Should().ContainSingle();
        writes.FactsJsonBytes.Should().Be(writes.FactsJsonWrites.Single().Bytes);
        writes.FactsJsonBytes.Should().BeGreaterThan(0);
        writes.FactsJsonWrites.Single().Path.EndsWith("facts.json", StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue();
        writes.Writes.Should().NotContain(write => write.Path.EndsWith("vectors.bin", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine(
            $"N={mutationCount}; facts.json writes after reset={writes.FactsJsonWrites.Count}; " +
            $"facts.json bytes after reset={writes.FactsJsonBytes}; " +
            $"vectors.bin bytes counted as facts=0");
    }

    [Fact]
    public async Task RepositoryCopy_N46_ReportsPathSpecificReadWriteAmplification()
    {
        const int corpusSize = 46;
        const int expectedLogicalPayloadBytes = 1302;
        string repositoryRoot = FindRepositoryRoot();
        string sourceScrinia = Path.Combine(repositoryRoot, ".scrinia");
        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"scrinia_h6_{Guid.NewGuid():N}");
        string sourceMemories = Path.Combine(sourceScrinia, "memories");
        string[] sourceArtifacts = Directory.GetFiles(sourceMemories, "*.nmp2", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "sessions" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "versions" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(corpusSize)
            .ToArray();

        sourceArtifacts.Should().HaveCount(corpusSize);
        CopyCorpusForMeasurement(sourceScrinia, temporaryRoot, sourceArtifacts);

        try
        {
            using (var store = new FileMemoryStore(temporaryRoot))
            using (var writes = new FileWriteCounter(temporaryRoot))
            {
                var created = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
                string newContent = new('x', expectedLogicalPayloadBytes);
                int actualLogicalPayloadBytes = Encoding.UTF8.GetByteCount(newContent);
                actualLogicalPayloadBytes.Should().Be(expectedLogicalPayloadBytes);

                const long historicalBaselineWriteBytes = 86790;

                var newEntry = await store.StoreAsync(new MemoryWrite(
                    "repository-copy-measurement",
                    "local:measurement",
                    [newContent],
                    Attributes: new() { ["topic"] = ["h6-measurement"] }));
                newEntry.OriginalBytes.Should().Be(expectedLogicalPayloadBytes,
                    "the measurement must account for the real logical payload written to the store");
                _ = store.GetEntityFactStore();

                writes.FactsJsonWrites.Should().BeEmpty("the cache write is deferred until lifecycle drain");
                writes.FactsJsonReadBytes.Should().Be(0,
                    "the copied corpus has no reusable facts sidecars, so no facts bytes may be read");

                (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

                var expectedByPath = store.ListScoped(null)
                    .GroupBy(item => item.Scope, StringComparer.Ordinal)
                    .ToDictionary(
                        group => Path.GetFullPath(Path.Combine(store.GetStoreDirForScope(group.Key), "facts.json")),
                        group => SerializeAuthoritativeFacts(group.Key, group.ToArray()),
                        StringComparer.OrdinalIgnoreCase);
                var expectedBytesByPath = expectedByPath.ToDictionary(
                    pair => pair.Key,
                    pair => (long)Encoding.UTF8.GetByteCount(pair.Value),
                    StringComparer.OrdinalIgnoreCase);

                expectedByPath.Should().NotBeEmpty();
                foreach (var (path, expected) in expectedByPath)
                {
                    File.ReadAllText(path).Should().Be(expected,
                        "each final sidecar must equal an independently rebuilt payload");
                    Encoding.UTF8.GetByteCount(expected).Should().Be((int)expectedBytesByPath[path]);
                }

                writes.FactsJsonWrites.Select(write => Path.GetFullPath(write.Path))
                    .Should().BeEquivalentTo(expectedByPath.Keys,
                        options => options.WithoutStrictOrdering());
                writes.FactsJsonWrites.GroupBy(write => Path.GetFullPath(write.Path), StringComparer.OrdinalIgnoreCase)
                    .Should().OnlyContain(group => group.Count() == 1,
                        "each dirty scope gets exactly one final facts write");
                foreach (var write in writes.FactsJsonWrites)
                    write.Bytes.Should().Be(expectedBytesByPath[Path.GetFullPath(write.Path)],
                        "the measured write must equal the independently computed UTF-8 payload length");

                writes.FactsJsonReadBytes.Should().Be(0);
                store.HasDirtyFactCache.Should().BeFalse();

                double currentWriteAmplification = (double)writes.FactsJsonBytes / actualLogicalPayloadBytes;
                double historicalBaselineAmplification = (double)historicalBaselineWriteBytes / actualLogicalPayloadBytes;
                _output.WriteLine(
                    $"N={corpusSize}; logical payload={actualLogicalPayloadBytes} B; " +
                    $"facts reads={writes.FactsJsonReadBytes} B; facts writes={writes.FactsJsonBytes} B; " +
                    $"paths={expectedByPath.Count}; writes={writes.FactsJsonWrites.Count}; " +
                    $"baseline recorded={historicalBaselineWriteBytes} B; " +
                    $"amplification baseline={historicalBaselineAmplification:F2}x; " +
                    $"amplification current={currentWriteAmplification:F2}x; " +
                    $"vectors.bin counted as facts=0");
            }
        }
        finally
        {
            DeleteTemporaryCorpusOrFail(temporaryRoot);
        }
    }

    [Fact]
    public async Task EphemeralOnlyInvalidation_DoesNotCreateDirtySidecarWork()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;

        store.RememberEphemeral("scratch", new EphemeralEntry(
            "scratch", "temporary", 0, 1, DateTimeOffset.UtcNow));
        _ = store.GetEntityFactStore();

        store.HasDirtyFactCache.Should().BeFalse();
        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public async Task RebuiltFacts_ClearDirtyStateOnlyAfterDrainFlushesSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("a", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["auth"] }), "local");

        _ = store.GetEntityFactStore();

        store.HasDirtyFactCache.Should().BeTrue();
        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        store.HasDirtyFactCache.Should().BeFalse();
    }

    [Fact]
    public void Mutation_InvalidatesCachedMergedView()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");

        store.Upsert(Entry("a", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        store.GetEntityFactStore().MentionCount(EntityRef.Of("topic", "auth")).Should().Be(1);

        store.Upsert(Entry("b", d, attributes: new() { ["topic"] = ["auth"] }), "local");
        store.GetEntityFactStore().MentionCount(EntityRef.Of("topic", "auth")).Should().Be(2); // fresh
    }

    [Fact]
    public async Task DirtyFactFlush_DrainsQueuedWritesForEveryScope()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.UtcNow;
        store.Upsert(Entry("a", d, attributes: new() { ["topic"] = ["a"] }), "local:a");
        store.Upsert(Entry("b", d, attributes: new() { ["topic"] = ["b"] }), "local:b");

        string factsA = Path.Combine(store.GetStoreDirForScope("local:a"), "facts.json");
        string factsB = Path.Combine(store.GetStoreDirForScope("local:b"), "facts.json");
        Directory.CreateDirectory(factsA);
        Directory.CreateDirectory(factsB);

        _ = store.GetEntityFactStore();
        Directory.Delete(factsA);
        Directory.Delete(factsB);

        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        File.Exists(factsA).Should().BeTrue();
        File.Exists(factsB).Should().BeTrue();
    }

    [Fact]
    public async Task DirtyFactFlush_PersistsNewestGenerationForAQueuedPath()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        string factsPath = Path.Combine(store.GetStoreDirForScope("local:a"), "facts.json");
        Directory.CreateDirectory(factsPath);

        store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["old"] }), "local:a");
        _ = store.GetEntityFactStore();

        store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["newest"] }), "local:a");
        _ = store.GetEntityFactStore();

        Directory.Delete(factsPath);
        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        SidecarContains(factsPath, "newest").Should().BeTrue();
    }

    [Fact]
    public async Task DirtyFactFlush_RestartsQueuedWriteAfterDrainCancellation()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["restart"] }), "local");

        string factsPath = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        Directory.CreateDirectory(factsPath);
        _ = store.GetEntityFactStore();

        (await BackgroundWork.DrainAsync(TimeSpan.FromMilliseconds(250))).Should().BeFalse();
        store.HasDirtyFactCache.Should().BeTrue(
            because: "a canceled drain must leave the unavailable sidecar queued and dirty");

        Directory.Delete(factsPath);
        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        File.Exists(factsPath).Should().BeTrue(
            because: "cancelling a retrying facts flush must requeue it for a later fresh-token drain");
        store.HasDirtyFactCache.Should().BeFalse();
    }

    [Fact]
    public async Task DirtyFactFlush_DoesNotWritePayloadFromAnOlderGeneration()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["before"] }), "local");
        _ = store.GetEntityFactStore();

        string factsPath = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        using var writes = new FileWriteCounter(scope.WorkspaceDir);
        var beforeWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string, byte[]>? previousObserver = FileMemoryStore.WriteObserver;
        FileMemoryStore.WriteObserver = (path, bytes) =>
        {
            previousObserver?.Invoke(path, bytes);
            if (path.Equals(factsPath, StringComparison.OrdinalIgnoreCase))
                beforeWrite.TrySetResult(true);
        };

        try
        {
            store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
                attributes: new() { ["topic"] = ["after"] }), "local");
            _ = store.GetEntityFactStore();

            (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await beforeWrite.Task.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();

            string finalPayload = File.ReadAllText(factsPath);
            finalPayload.Should().Contain("after");
            writes.FactsJsonWrites.Should().ContainSingle();
        }
        finally
        {
            FileMemoryStore.WriteObserver = previousObserver;
        }
    }

    [Fact]
    public async Task DirtyFactFlush_RetryAfterCancellationResetsInFlightState()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(Entry("memo", DateTimeOffset.UtcNow,
            attributes: new() { ["topic"] = ["retry"] }), "local");

        string factsPath = Path.Combine(store.GetStoreDirForScope("local"), "facts.json");
        Directory.CreateDirectory(factsPath);
        _ = store.GetEntityFactStore();

        (await BackgroundWork.DrainAsync(TimeSpan.FromMilliseconds(100))).Should().BeFalse();
        store.HasDirtyFactCache.Should().BeTrue();

        Directory.Delete(factsPath);
        (await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        File.Exists(factsPath).Should().BeTrue();
        store.HasDirtyFactCache.Should().BeFalse();
    }

    private static async Task EventuallyAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    private static bool SidecarContains(string path, string text)
    {
        try { return File.Exists(path) && File.ReadAllText(path).Contains(text, StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    private static string IndependentEntitySignature(ArtifactEntry entry)
    {
        string attrs = entry.Attributes is null
            ? string.Empty
            : string.Join(";", entry.Attributes
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key.ToLowerInvariant()}=[{string.Join(",", pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}]"));
        string keywords = entry.Keywords is null
            ? string.Empty
            : string.Join(",", entry.Keywords.OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase));
        return $"{attrs}|kw=[{keywords}]";
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".scrinia", "memories")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository .scrinia corpus.");
    }

    private static void CopyCorpusForMeasurement(string sourceScrinia, string temporaryRoot, string[] selectedArtifacts)
    {
        string destinationScrinia = Path.Combine(temporaryRoot, ".scrinia");
        Directory.CreateDirectory(destinationScrinia);
        foreach (string sourceFile in Directory.GetFiles(sourceScrinia, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceScrinia, sourceFile);
            if (relative.StartsWith("memories" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            string destination = Path.Combine(destinationScrinia, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination, overwrite: true);
        }

        foreach (string sourceArtifact in selectedArtifacts)
        {
            string relative = Path.GetRelativePath(sourceScrinia, sourceArtifact);
            string destinationArtifact = Path.Combine(destinationScrinia, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationArtifact)!);
            File.Copy(sourceArtifact, destinationArtifact, overwrite: true);

            string sourceMeta = Path.ChangeExtension(sourceArtifact, ".meta.json");
            if (!File.Exists(sourceMeta))
                throw new FileNotFoundException("A selected corpus artifact has no metadata sidecar.", sourceMeta);
            File.Copy(sourceMeta, Path.ChangeExtension(destinationArtifact, ".meta.json"), overwrite: true);
        }
    }

    private static void DeleteTemporaryCorpusOrFail(string temporaryRoot)
    {
        Exception? lastFailure = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            if (!Directory.Exists(temporaryRoot)) return;
            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        throw new IOException(
            $"Could not clean up temporary H6 corpus '{temporaryRoot}' after five attempts.",
            lastFailure);
    }

    private static string SerializeAuthoritativeFacts(string scope, ScopedArtifact[] entries)
    {
        var memories = entries.Select(EntityFactDerivation.Derive)
            .Where(memory => memory is not null)
            .Select(memory => memory!)
            .ToArray();
        string token = EntityFactSidecar.ComputeToken(entries.Select(item =>
            ($"{scope}|{item.Entry.Name}", item.Entry.UpdatedAt ?? item.Entry.CreatedAt,
                IndependentEntitySignature(item.Entry))));
        return EntityFactSidecar.Serialize(token, memories);
    }

    /// <summary>
    /// Regression for a lock-ordering deadlock: GetEntityFactStore used to hold _factStoreLock
    /// across ListScoped/LoadIndex (which take the per-scope index lock) — order
    /// _factStoreLock -&gt; index-lock. Upsert/Remove take the index write-lock first, then
    /// _factStoreLock -&gt; the opposite order. A query racing a mutation on the same scope could
    /// deadlock. Time-bounded (Stopwatch controls loop duration only, not the assertion) so a
    /// regression fails fast instead of hanging the suite.
    /// </summary>
    [Fact]
    public void ConcurrentQueryAndMutation_DoesNotDeadlock()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var d = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        store.Upsert(Entry("seed", d, attributes: new() { ["topic"] = ["auth"] }), "local");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var runFor = TimeSpan.FromSeconds(1);

        var tasks = new List<Task>();
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (stopwatch.Elapsed < runFor)
                    store.GetEntityFactStore();
            }));
        }
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(() =>
            {
                while (stopwatch.Elapsed < runFor)
                {
                    store.Upsert(Entry($"mut{idx}", d, attributes: new() { ["topic"] = ["auth"] }), "local");
                    store.Remove($"mut{idx}", "local");
                }
            }));
        }

        bool completed = Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(15));
        completed.Should().BeTrue("a concurrent GetEntityFactStore + Upsert/Remove race on the same scope must not deadlock");
    }
}
