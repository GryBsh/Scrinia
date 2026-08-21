using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// Durability of bundle restore — finding C1 of the 2026-08-21 design review.
///
/// <para>The defect: <c>ImportTopicEntries</c> snapshotted the index under a shared lock that
/// released immediately, wrote every body unlocked, then committed through <c>SaveIndex</c>,
/// whose orphan sweep deletes every sidecar absent from a list that was by then seconds old.
/// A memory written during a restore was annihilated after its write reported success.</para>
///
/// <para>The fix is not a wider lock around the sweep. Import is purely additive and never
/// needed one, so the sweep left the import path entirely.</para>
/// </summary>
public sealed class TransactionalRestoreTests
{
    private const string Scope = "local:notes";

    [Fact]
    public async Task SaveIndex_StillSweepsSidecarsAbsentFromTheListItIsHanded()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("keeper", Scope, ["kept"]));
        await store.StoreAsync(new MemoryWrite("victim", Scope, ["swept"]));

        var withoutVictim = store.LoadIndex(Scope)
            .Where(e => !e.Name.Equals("victim", StringComparison.OrdinalIgnoreCase))
            .ToList();

        store.SaveIndex(withoutVictim, Scope);

        store.LoadIndex(Scope).Select(e => e.Name).Should()
            .Contain("keeper").And.NotContain("victim",
                because: "SaveIndex's orphan sweep is unchanged by this work — only import "
                       + "stops using it");
    }

    [Fact]
    public void WithExclusiveLock_ForwardsAnExplicitTimeoutToFileLock()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;

        // Hold the scope's cross-process lock from outside the store, so the store's own
        // acquisition must wait and then give up.
        string lockPath = Path.Combine(scope.TempDir, "notes", ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        using var held = FileLock.AcquireExclusive(lockPath);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Action tooSlow = () => store.InvokeWithExclusiveLockForTest(
            Scope, () => { }, TimeSpan.FromMilliseconds(300));

        tooSlow.Should().Throw<FileLockTimeoutException>(
            because: "an explicit timeout must reach FileLock rather than being ignored");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            because: "it must have used the 300ms timeout, not FileLock's 5s default");
    }

    [Fact]
    public async Task Import_DoesNotDeleteAMemoryThatLandedAfterItsSnapshot()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("seed", Scope, ["seeded"]));
        store.LoadIndex(Scope); // warm this store's cache: it now knows only "seed"

        // A SECOND store over the same root has its own index cache, so this write is invisible
        // to `store` until it reloads — precisely the state a concurrent process leaves behind,
        // and precisely what made the first store's import snapshot stale.
        using (var otherProcess = new FileMemoryStore(scope.WorkspaceDir))
            await otherProcess.StoreAsync(new MemoryWrite("bystander", Scope, ["written during the restore"]));

        var bundled = new List<ArtifactEntry>
        {
            new("imported", "file://placeholder", 11, 1, DateTimeOffset.UtcNow),
        };
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["imported"] = "bundle body",
        };

        store.ImportTopicEntries(Scope, bundled, contents, overwrite: true);

        // Cold read: a fresh store, so nothing is answered from a cache the import updated.
        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        cold.LoadIndex(Scope).Select(e => e.Name).Should()
            .Contain("bystander", because:
                "import is additive — it must never delete a memory the bundle does not mention, "
              + "however stale its own view of the scope was")
            .And.Contain("imported")
            .And.Contain("seed");
    }

    [Fact]
    public async Task Import_ConcurrentWriteToTheSameScope_SurvivesOrFailsLoudly()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("seed", Scope, ["seeded"]));

        var bundled = new List<ArtifactEntry>();
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string blob = new string('x', 60_000);
        for (int i = 0; i < 120; i++)
        {
            string name = $"bundled-{i:D3}";
            bundled.Add(new ArtifactEntry(name, "file://placeholder", blob.Length, 1, DateTimeOffset.UtcNow));
            contents[name] = blob;
        }

        Exception? importError = null;
        var importDone = new ManualResetEventSlim(false);
        var importThread = new Thread(() =>
        {
            try { store.ImportTopicEntries(Scope, bundled, contents, overwrite: true); }
            catch (Exception ex) { importError = ex; }
            finally { importDone.Set(); }
        });
        importThread.Start();

        bool wrote;
        try
        {
            using var otherProcess = new FileMemoryStore(scope.WorkspaceDir);
            await otherProcess.StoreAsync(new MemoryWrite("urgent", Scope, ["written mid-restore"]));
            wrote = true;
        }
        catch (FileLockTimeoutException)
        {
            wrote = false; // blocked out by the import's lock — loud, and acceptable
        }

        importDone.Wait(TimeSpan.FromMinutes(2)).Should().BeTrue();
        importThread.Join();
        importError.Should().BeNull();

        if (wrote)
        {
            using var cold = new FileMemoryStore(scope.WorkspaceDir);
            cold.LoadIndex(Scope).Select(e => e.Name).Should().Contain("urgent",
                because: "a write that reported success must never be silently deleted by a "
                       + "concurrent restore — it may block, or fail loudly, but not vanish");
        }
    }
}
