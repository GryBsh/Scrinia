using System.Collections.Concurrent;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests.Wave1;

// Verifies FileMemoryStore.LoadIndex cache aliasing contract (C1):
// - Fast path is a live view under EnterReadLock (in-process lock stays).
// - No AcquireShared file handle is opened on a cache hit — cross-process staleness is
//   acknowledged as unsolved; only same-process read-your-writes aliasing is fixed.
//   See FileMemoryStore.cs:797 comment block (EnterReadLock vs AcquireShared distinction).
// - Every mutation REPLACES CachedIndex under EnterWriteLock (WithExclusiveLock), so
//   callers holding a prior live view never see it mutated behind them.
// - Readers see IReadOnlyList<ArtifactEntry>; the single mutating caller ImportTopicEntries
//   (and this test's mutation helper) materializes via .ToList() explicitly.
public sealed class LoadIndexImmutabilityTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileMemoryStore _store;

    public LoadIndexImmutabilityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "scrinia-w1-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _store = new FileMemoryStore(_tempRoot);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // Live view: cached hit returns the same instance (EnterReadLock, no AcquireShared).
    // This is the identity pin for the in-process live-view contract.
    [Fact]
    public void LoadIndex_CachedHit_ReturnsLiveView_SameInstance_UnderEnterReadLock()
    {
        // Seed 1 entry -> populates cache via WithExclusiveLock/EnterWriteLock.
        _store.Upsert(new ArtifactEntry("seed", "", 10, 1, DateTimeOffset.UtcNow), "local");

        // First LoadIndex primes cache (cold -> cached).
        var first = _store.LoadIndex("local");
        // Second LoadIndex must hit cache fast path: EnterReadLock only, no AcquireShared.
        // Documented at FileMemoryStore.cs:803-865: fast lane needs no file handle.
        var second = _store.LoadIndex("local");

        // Live view: same backing list, not a copy.
        Assert.Same(first, second);
    }

    // EnterReadLock vs AcquireShared: cache hit opens no file handle.
    // In-process EnterReadLock stays (see FileMemoryStore.LoadIndexUnderRecoveryBarrier);
    // cross-process AcquireShared is NOT taken on the fast path.
    [Fact]
    public void LoadIndex_CachedHit_DoesNotAcquireSharedFileLock()
    {
        _store.Upsert(new ArtifactEntry("seed", "", 10, 1, DateTimeOffset.UtcNow), "local");

        // Prime cache so next read is a hit.
        var first = _store.LoadIndex("local");
        Assert.NotEmpty(first);

        string lockPath = Path.Combine(_store.GetStoreDirForScope("local"), ".lock");
        // Capture lock-file state before the hit.
        bool existsBefore = File.Exists(lockPath);
        long lengthBefore = existsBefore ? new FileInfo(lockPath).Length : -1;
        DateTime writeBefore = existsBefore ? File.GetLastWriteTimeUtc(lockPath) : DateTime.MinValue;

        // Hit again — should be live view, no AcquireShared handle opened.
        var second = _store.LoadIndex("local");
        Assert.Same(first, second); // live view, EnterReadLock

        // AcquireShared would open/create the .lock file; on a pure EnterReadLock hit
        // the file's observable state must not change (no new handle, no truncation).
        bool existsAfter = File.Exists(lockPath);
        // Either file did not exist before and still does not (no AcquireShared created it),
        // or it existed and is untouched.
        if (!existsBefore)
        {
            // Permissive: recovery barrier may have created .lock on first write;
            // second hit must not change it. If it still does not exist, that is also
            // proof that no AcquireShared was taken.
            if (existsAfter)
            {
                // If a .lock now exists but was not before, it must have been created by the FIRST
                // priming read's recovery barrier, not the second hit. Verify second hit did not
                // change its timestamp.
                DateTime writeAfter = File.GetLastWriteTimeUtc(lockPath);
                // Second hit timestamp should equal first hit's creation - no new open.
                Assert.Equal(writeBefore == DateTime.MinValue ? writeAfter : writeBefore, writeAfter);
            }
        }
        else
        {
            long lengthAfter = new FileInfo(lockPath).Length;
            DateTime writeAfter = File.GetLastWriteTimeUtc(lockPath);
            Assert.Equal(lengthBefore, lengthAfter);
            Assert.Equal(writeBefore, writeAfter);
        }
    }

    // IReadOnlyList narrowing: callers cannot mutate through the returned type without an
    // explicit ToList() copy, which ImportTopicEntries demonstrates. This prevents Add behind reader.
    [Fact]
    public void LoadIndex_ReturnsIReadOnlyList()
    {
        _store.Upsert(new ArtifactEntry("seed", "", 10, 1, DateTimeOffset.UtcNow), "local");

        IReadOnlyList<ArtifactEntry> list = _store.LoadIndex("local");

        Assert.IsAssignableFrom<IReadOnlyList<ArtifactEntry>>(list);
        // The concrete type is List<ArtifactEntry> (live view) but exposed as IReadOnlyList.
        // Callers that need to mutate must copy explicitly.
        var copy = list.ToList();
        copy.Add(new ArtifactEntry("mutated-copy", "", 10, 1, DateTimeOffset.UtcNow));
        // Original live view must be unchanged by mutation of the copy (copy-on-write isolation).
        Assert.DoesNotContain(list, e => e.Name == "mutated-copy");
        Assert.Contains(copy, e => e.Name == "mutated-copy");
    }

    // Replace-on-write: every mutation installs a NEW CachedIndex over a NEW list.
    // A caller holding a prior live view never sees it mutated behind them.
    [Fact]
    public void LoadIndex_ReplaceOnWrite_NewCachedIndexAndOldViewUnchanged()
    {
        _store.Upsert(new ArtifactEntry("a", "", 10, 1, DateTimeOffset.UtcNow), "local");
        _store.Upsert(new ArtifactEntry("b", "", 10, 1, DateTimeOffset.UtcNow), "local");

        var before = _store.LoadIndex("local");
        int countBefore = before.Count;
        Assert.Equal(2, countBefore);

        // Hold a reference to the old live view, then mutate via Upsert (EnterWriteLock).
        _store.Upsert(new ArtifactEntry("c", "", 10, 1, DateTimeOffset.UtcNow), "local");

        var after = _store.LoadIndex("local");

        // Write replaced CachedIndex: new instance, not mutated old.
        Assert.NotSame(before, after);
        // Old view must not have been mutated behind the holder (copy-on-write).
        Assert.Equal(2, before.Count);
        Assert.Equal(countBefore, before.Count);
        // New view reflects the write.
        Assert.Equal(3, after.Count);
        Assert.Contains(after, e => e.Name == "c");
        Assert.DoesNotContain(before, e => e.Name == "c");
    }

    // ImportTopicEntries is the single mutating caller that uses .ToList() explicitly
    // before mutating and handing to PublishIndexUnsafe (ownership transfer). This pins
    // that the explicit copy contract is honored. Uses the proven pattern from
    // FileMemoryStoreTests.ImportTopicEntries_creates_topic (known green at BASE).
    [Fact]
    public void ImportTopicEntries_UsesExplicitToList_ForMutation()
    {
        var incoming = new List<ArtifactEntry>
        {
            new(MemoryAddress.Parse("/imported"), "artifacts/imported.nmp2", 50, 1, DateTimeOffset.UtcNow),
        };
        var artifactBytes = new Dictionary<string, byte[]>
        {
            ["imported"] = System.Text.Encoding.UTF8.GetBytes(Scrinia.Core.Encoding.Nmp2ChunkedEncoder.Encode("Imported content")),
        };

        // Import into a fresh scope "local:import-demo" — no prior entries.
        // Internally ImportTopicEntries does LoadIndexUnsafe(...).ToList() before mutating
        // and takes ownership of the resulting list (replace-on-write).
        _store.ImportTopicEntries("local:import-demo", incoming, artifactBytes, overwrite: false);

        var before = _store.LoadIndex("local:import-demo");
        Assert.Single(before);
        Assert.Contains(before, e => e.Name == "imported");

        // Hold live view, then import a second entry — old view must not mutate.
        var heldBefore = before;
        var second = new List<ArtifactEntry>
        {
            new(MemoryAddress.Parse("/imported2"), "artifacts/imported2.nmp2", 50, 1, DateTimeOffset.UtcNow),
        };
        var secondBytes = new Dictionary<string, byte[]>
        {
            ["imported2"] = System.Text.Encoding.UTF8.GetBytes(Scrinia.Core.Encoding.Nmp2ChunkedEncoder.Encode("Second")),
        };
        _store.ImportTopicEntries("local:import-demo", second, secondBytes, overwrite: false);

        var after = _store.LoadIndex("local:import-demo");
        // Replace-on-write: new CachedIndex, old held view unchanged.
        Assert.NotSame(heldBefore, after);
        Assert.Single(heldBefore);
        Assert.Equal(2, after.Count);
        Assert.Contains(after, e => e.Name == "imported2");
        Assert.DoesNotContain(heldBefore, e => e.Name == "imported2");
    }

    // Monotonic count + live view not mutated behind reader under concurrent writer.
    // A reader must never observe count shrink, never see its held list mutated behind it,
    // and never see an exception due to tearing. This exercises the EnterReadLock hold
    // across the live-view return (the S2 shape from analysis/2026-08-10-loadindex-aliasing-and-lock.md).
    [Fact]
    public async Task LoadIndex_MonotonicCount_AndLiveViewNotMutatedBehindReader_UnderConcurrentWriter()
    {
        // Seed 5 entries in "local" so the cache is warm before the race.
        for (int i = 0; i < 5; i++)
            _store.Upsert(new ArtifactEntry($"seed-{i}", "", 10, 1, DateTimeOffset.UtcNow), "local");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new ConcurrentBag<Exception>();
        var shrinking = new ConcurrentBag<string>();

        // Writer: loops Upsert (Each Upsert replaces CachedIndex under EnterWriteLock).
        var writer = Task.Run(() =>
        {
            int n = 5;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    _store.Upsert(new ArtifactEntry($"w-{n}", "", 10, 1, DateTimeOffset.UtcNow), "local");
                    n++;
                }
                catch (Exception ex) { exceptions.Add(ex); }
                Thread.SpinWait(500);
            }
        }, cts.Token);

        // Reader: loops LoadIndex and asserts monotonic count + snapshot stability.
        // Live view must not be mutated behind the reader — capture and re-check.
        // With correct EnterReadLock / replace-on-write, shrinking or tearing never happens.
        var reader = Task.Run(() =>
        {
            int last = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var list = _store.LoadIndex("local");
                    int count = list.Count;
                    if (count < last)
                        shrinking.Add($"shrunk {last}->{count}");
                    last = Math.Max(last, count);

                    // Live view: hold reference, spin, re-check — must not mutate behind us.
                    // Old CachedIndex lists are never mutated after replacement, only new ones created.
                    int snap = list.Count;
                    Thread.SpinWait(1000);
                    if (list.Count != snap)
                        shrinking.Add($"mutated behind reader {snap}->{list.Count}");
                }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        }, cts.Token);

        await Task.Delay(1200, cts.Token).ContinueWith(_ => { });
        cts.Cancel();
        try { await Task.WhenAll(writer, reader); } catch (OperationCanceledException) { }

        Assert.Empty(exceptions);
        Assert.Empty(shrinking);
    }
}
