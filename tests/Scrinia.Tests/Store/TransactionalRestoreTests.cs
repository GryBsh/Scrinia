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

        // Assert against DISK, not LoadIndex: LoadIndex returns the cache PublishIndexUnsafe just
        // installed, which reflects whatever list was HANDED to it — it would say the same thing
        // even if the orphan sweep itself were deleted outright. Only checking the sidecar files
        // exercises the sweep at all.
        string storeDir = Path.Combine(scope.TempDir, "notes");
        File.Exists(Path.Combine(storeDir, "victim.meta.json")).Should().BeFalse(
            because: "SaveIndex's orphan sweep is unchanged by this work — only import "
                   + "stops using it, and this must fail if the sweep is ever deleted");
        File.Exists(Path.Combine(storeDir, "keeper.meta.json")).Should().BeTrue(
            because: "the sweep must delete only what is absent from the list it is handed");
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

    [Fact]
    public async Task Import_ThatFailsPartWay_LeavesNoLiveArtifactUnderAStaleSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("victim", Scope, ["ORIGINAL BODY"]));

        string storeDir = Path.Combine(scope.TempDir, "notes");
        var before = store.LoadIndex(Scope).Single(e => e.Name == "victim");

        // Raw artifact text, captured for an exact comparison below. ReadArtifactAsync returns
        // the NMP/2 envelope (Brotli + URL-safe Base64), NOT the decoded content — so asserting
        // on a substring of the original prose would never match, before or after the fix.
        string bodyBefore = await store.ReadArtifactAsync("victim", Scope);

        // A bundle that overwrites `victim` and then fails: the second entry claims content the
        // dictionary does not carry AND a name that cannot be written, so the import throws after
        // `victim` would previously have been overwritten in place.
        var bundled = new List<ArtifactEntry>
        {
            new("victim", "file://placeholder", 13, 1, DateTimeOffset.UtcNow),
            new(new string('n', 300), "file://placeholder", 5, 1, DateTimeOffset.UtcNow),
        };
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["victim"] = "REPLACEMENT BODY",
            [new string('n', 300)] = "boom",
        };

        Action failing = () => store.ImportTopicEntries(Scope, bundled, contents, overwrite: true);
        failing.Should().Throw<Exception>(because: "the over-length component cannot be written");

        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        var after = cold.LoadIndex(Scope).SingleOrDefault(e => e.Name == "victim");

        after.Should().NotBeNull();
        string bodyAfter = await cold.ReadArtifactAsync("victim", Scope);
        bodyAfter.Should().Be(bodyBefore, because:
            "a failed import must not leave new content under a sidecar that still describes the "
          + "old content — content and metadata must agree, whatever the outcome");
        after!.OriginalBytes.Should().Be(before.OriginalBytes);

        Directory.EnumerateFiles(storeDir, "*.staging").Should().BeEmpty(
            because: "a failed import rolls back everything it staged");
    }

    [Fact]
    public async Task Import_SweepsStagingLitterLeftByAPriorCrash()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("seed", Scope, ["seeded"]));
        string storeDir = Path.Combine(scope.TempDir, "notes");

        string litter = Path.Combine(storeDir, "ghost.import-deadbeef.staging");
        File.WriteAllText(litter, "a body from an import that died before committing");

        store.ImportTopicEntries(
            Scope,
            [new("fresh", "file://placeholder", 5, 1, DateTimeOffset.UtcNow)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["fresh"] = "hello" },
            overwrite: true);

        File.Exists(litter).Should().BeFalse(because: "a later import clears staging litter");

        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        cold.LoadIndex(Scope).Select(e => e.Name).Should()
            .Contain("fresh").And.Contain("seed").And.NotContain("ghost",
                because: "litter is discarded, never imported");
    }

    [Fact]
    public void StagingFiles_AreInvisibleToScopeDiscovery()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        string ghostScopeDir = Path.Combine(scope.TempDir, "ghostscope");
        Directory.CreateDirectory(ghostScopeDir);
        File.WriteAllText(
            Path.Combine(ghostScopeDir, "x.import-deadbeef.staging"), "staged, never committed");

        store.ResolveReadScopes(null).Should().NotContain("local:ghostscope",
            because: "a directory holding only staging litter is not a scope — the suffix "
                   + "carries no .nmp2 segment precisely so DiscoverMemoriesRecursive ignores it");
    }

    [Fact]
    public async Task Import_ThatSucceeds_LeavesEveryEntryConsistentAndNoStagingLeftovers()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        string storeDir = Path.Combine(scope.TempDir, "notes");

        await store.StoreAsync(new MemoryWrite("alpha", Scope, ["alpha v1"]));
        await store.StoreAsync(new MemoryWrite("beta", Scope, ["beta v1"]));

        // Every entry must end with content and metadata describing the SAME version.
        //
        // This test runs the import to COMPLETION, so it is deliberately NOT the guard for the
        // per-entry commit ordering: a rename-all-then-publish loop reaches the identical end
        // state and passes this identically. Its earlier name claimed otherwise. The ordering
        // is pinned by Import_ThatFailsPartWayThroughTheCommitLoop_... below, which injects a
        // real fault between two entries' commits.
        //
        // OriginalBytes matches its own content's length, as a real bundle's export-time
        // metadata would — the assertion below checks that the COMMITTED sidecar still agrees
        // with what was actually staged, not a formula relating it to the pre-import body.
        var bundled = new List<ArtifactEntry>
        {
            new("alpha", "file://placeholder", 13, 1, DateTimeOffset.UtcNow),
            new("beta", "file://placeholder", 12, 1, DateTimeOffset.UtcNow),
        };
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = "alpha v2 body",
            ["beta"] = "beta v2 body",
        };

        store.ImportTopicEntries(Scope, bundled, contents, overwrite: true);

        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        foreach (string name in new[] { "alpha", "beta" })
        {
            var entry = cold.LoadIndex(Scope).Single(e => e.Name == name);
            string body = await cold.ReadArtifactAsync(name, Scope);
            entry.OriginalBytes.Should().Be(contents[name].Length, because:
                $"{name}'s sidecar must describe the body actually on disk after a commit");
            body.Should().NotBeNullOrEmpty();
        }

        Directory.EnumerateFiles(storeDir, "*.staging").Should().BeEmpty();
    }

    /// <summary>
    /// The commit loop applies each entry as <c>File.Move(staged → final)</c> immediately
    /// followed by that entry's <c>WriteSidecar</c>, per entry, before moving to the next.
    ///
    /// <para><b>Why this test has to inject a fault.</b> Any test that runs the import to
    /// completion passes identically under the correct interleave and under
    /// "rename every body, then publish every sidecar" — the end states are the same. Only a
    /// failure BETWEEN two entries' commits separates them. Under the interleave, every entry
    /// committed before the failure has a body and a sidecar that agree. Under
    /// rename-all-then-publish, the throw happens before any sidecar is written, so every
    /// already-renamed body sits under its OLD sidecar — which is the exact content/metadata
    /// mismatch this branch exists to eliminate, reintroduced one level up.</para>
    ///
    /// <para>The fault: hold <c>beta.nmp2</c> open with <see cref="FileShare.None"/> so the
    /// second entry's <c>File.Move</c> cannot land. <c>alpha</c> is committed first and must
    /// survive intact.</para>
    ///
    /// <para><b>Verified to discriminate, not merely to pass.</b> Rewriting the commit loop as
    /// "rename every body, then write every sidecar" makes this test fail with
    /// <c>alpha.OriginalBytes</c> = 8 (<c>"alpha v1"</c>) against a body already holding
    /// <c>"alpha v2 body"</c> — the predicted mismatch, observed. A test asserting an
    /// end state after a completed import cannot do this, which is why the sibling test above
    /// is not the guard its earlier name claimed.</para>
    /// </summary>
    [Fact]
    public async Task Import_ThatFailsPartWayThroughTheCommitLoop_LeavesEarlierEntriesConsistent()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        string storeDir = Path.Combine(scope.TempDir, "notes");

        await store.StoreAsync(new MemoryWrite("alpha", Scope, ["alpha v1"]));
        await store.StoreAsync(new MemoryWrite("beta", Scope, ["beta v1"]));

        // Bundle order drives commit order: alpha commits, then beta fails.
        var bundled = new List<ArtifactEntry>
        {
            new("alpha", "file://placeholder", 13, 1, DateTimeOffset.UtcNow),
            new("beta", "file://placeholder", 12, 1, DateTimeOffset.UtcNow),
        };
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = "alpha v2 body",
            ["beta"] = "beta v2 body",
        };

        Exception? thrown;
        using (new FileStream(Path.Combine(storeDir, "beta.nmp2"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            thrown = Record.Exception(
                () => store.ImportTopicEntries(Scope, bundled, contents, overwrite: true));
        }

        thrown.Should().NotBeNull("holding beta.nmp2 exclusively must make its commit fail");

        // Read from a COLD store: asserting through LoadIndex after a write that installed the
        // cache proves nothing about disk. The import threw before PublishIndexUnsafe, so there
        // is no cache to be fooled by here — but the habit is what the branch's other vacuous
        // test failed on, so it is kept deliberately.
        using var cold = new FileMemoryStore(scope.WorkspaceDir);
        var alpha = cold.LoadIndex(Scope).Single(e => e.Name == "alpha");
        string alphaBody = await cold.ReadArtifactAsync("alpha", Scope);

        alphaBody.Should().Be("alpha v2 body",
            "alpha's commit completed before beta's failed");
        alpha.OriginalBytes.Should().Be("alpha v2 body".Length,
            "alpha's sidecar must have been written in the SAME loop iteration as its body — " +
            "under rename-all-then-publish it would still describe 'alpha v1'");
    }
}
