using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

public sealed class StoreLayoutMigrationTests : IDisposable
{
    private readonly string _root;

    public StoreLayoutMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_mig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, string content)
    {
        string full = Path.Combine(_root, ".scrinia", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private bool Exists(string relative) =>
        File.Exists(Path.Combine(_root, ".scrinia", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Migrate_MovesEveryLegacyRootIntoMemories()
    {
        Write("store/notes.nmp2", "a");
        Write("store/notes.meta.json", "{}");
        Write("topics/findings/review.nmp2", "b");
        Write("topics/memory/api/auth.nmp2", "c");
        Write("topics/entity/skill/qa.nmp2", "d");
        Write("topics/agent/profile.nmp2", "e");
        // Top-level agent/profile.md deliberately NOT seeded here: LegacyRoots no longer
        // includes "agent" (see the class doc comment on StoreLayoutMigration) because that
        // top-level directory holds a live, hand-authored Markdown file MemoryTools.Core.cs
        // reads by a hardcoded path outside this engine entirely — see
        // AgentTopLevelDir_IsNeverSwept below for that guarantee.

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Ran.Should().BeTrue();
        Exists("memories/notes.nmp2").Should().BeTrue();
        Exists("memories/notes.meta.json").Should().BeTrue();
        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("memories/api/auth.nmp2").Should().BeTrue();
        Exists("memories/skill/qa.nmp2").Should().BeTrue();
        Exists("memories/agent/profile.nmp2").Should().BeTrue();

        // Moved, not copied.
        Exists("store/notes.nmp2").Should().BeFalse();
        Exists("topics/findings/review.nmp2").Should().BeFalse();
        Directory.Exists(Path.Combine(_root, ".scrinia", "topics")).Should().BeFalse();
    }

    [Fact]
    public void AgentAndSkillsTopLevelDirs_AreNeverSwept()
    {
        // .scrinia/agent/*.md and .scrinia/skills/*.md are live, hand-authored files
        // (MemoryTools.Core.cs / MemoryTools.Skills.cs read them by a hardcoded path, entirely
        // outside FileMemoryStore/PathRouter scope resolution) — NOT part of this engine's
        // legacy layout. Migrating something else (so NeedsMigration is true and the pass
        // actually runs) must leave these two directories completely untouched.
        Write("topics/findings/review.nmp2", "b");
        Write("agent/profile.md", "agent config");
        Write("skills/qa.md", "qa skill override");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Ran.Should().BeTrue();
        Exists("agent/profile.md").Should().BeTrue("the live agent config file must not move");
        Exists("skills/qa.md").Should().BeTrue("the live skill override file must not move");
        Exists("memories/agent/profile.md").Should().BeFalse();
        Exists("memories/skill/qa.md").Should().BeFalse();
    }

    [Fact]
    public void Migrate_PreservesVersionsSubdirectories()
    {
        Write("topics/findings/versions/review_20260801-010101.nmp2", "old");
        StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);
        Exists("memories/findings/versions/review_20260801-010101.nmp2").Should().BeTrue();
    }

    [Fact]
    public void Migrate_SourceLockedDuringMove_SurfacesActionableError_ThenResumes()
    {
        // Reviewer finding (Task 7 fix round): the core move loop's File.Move had no error
        // handling at all — a real content file transiently locked by another process (Synology
        // Drive's sync daemon, an antivirus scan, a second scrinia process) would throw a bare
        // IOException straight out of RunMigration, and because _layoutChecked only flips after a
        // clean completion, EVERY subsequent public store call would hit it again until the lock
        // cleared. Proves both halves of the fix: (1) the error surfaced is actionable (names the
        // file, explains the likely cause), not an opaque IOException; (2) once the lock clears, a
        // later attempt resumes and completes — no data lost, nothing left behind.
        Write("topics/findings/review.nmp2", "b");
        string sourcePath = Path.Combine(_root, ".scrinia", "topics", "findings", "review.nmp2");

        using (new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

            var thrown = act.Should().Throw<IOException>().Which;
            thrown.Message.Should().Contain("review.nmp2",
                "the error must name the specific file that's stuck");
            thrown.Message.Should().Contain("most common cause",
                "the error must be actionable, not a bare IOException surfacing from LoadIndex/Upsert/etc");
        }

        // Lock released — a subsequent attempt must complete the migration from where it stopped.
        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Ran.Should().BeTrue();
        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("topics/findings/review.nmp2").Should().BeFalse();
    }

    [Fact]
    public void MoveWithRetry_LockedTargetOverwrite_SurfacesActionableError_ThenResumes()
    {
        // Re-review finding: File.Move(source, target, overwrite: true) — the conflict-quarantine
        // call site's shape — can throw UnauthorizedAccessException (not IOException) when TARGET
        // already exists and is locked (empirically confirmed in review). FileRetry.Run's catch
        // filter only matches IOException, so that exception type reached MoveWithRetry's own
        // (previously IOException-only) catch unhandled and propagated raw, reproducing exactly the
        // opaque-exception failure this fix round exists to close.
        //
        // Tested directly against the internal MoveWithRetry rather than end-to-end through
        // Migrate(...): the real quarantine target lives under a ".scrinia-conflicts-{timestamp}/"
        // directory computed at runtime inside RunMigration, so there is no way to pre-lock that
        // exact path from outside a single Migrate call. See the MoveWithRetry doc comment.
        string source = Path.Combine(_root, "source.nmp2");
        string target = Path.Combine(_root, "target.nmp2");
        File.WriteAllText(source, "moved content");
        File.WriteAllText(target, "pre-existing, locked content");

        using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var act = () => StoreLayoutMigration.MoveWithRetry(source, target, overwrite: true);

            var thrown = act.Should().Throw<IOException>().Which;
            thrown.Message.Should().Contain(source, "the error must name the source path");
            thrown.Message.Should().Contain(target, "the error must name the target path");
            thrown.Message.Should().Contain("most common cause",
                "the error must be actionable, not a bare IOException/UnauthorizedAccessException");
        }

        // Lock released — the same move must now resume and complete cleanly.
        StoreLayoutMigration.MoveWithRetry(source, target, overwrite: true);

        File.Exists(source).Should().BeFalse();
        File.ReadAllText(target).Should().Be("moved content");
    }

    [Fact]
    public void Migrate_Collision_NonNamespacedWins_LoserQuarantined()
    {
        Write("topics/api/auth.nmp2", "flat-wins");
        Write("topics/memory/api/auth.nmp2", "namespaced-loses");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Conflicts.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, ".scrinia", "memories", "api", "auth.nmp2"))
            .Should().Be("flat-wins");
        Directory.EnumerateDirectories(_root, ".scrinia-conflicts-*").Should().NotBeEmpty();
    }

    [Fact]
    public void Migrate_DeletesTransientLockAndTmpFiles_NotRelocated()
    {
        Write("topics/findings/review.nmp2", "b");
        Write("topics/findings/.lock", "");
        Write("topics/findings/scratch.tmp", "partial-write");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("memories/findings/.lock").Should().BeFalse();
        Exists("memories/findings/scratch.tmp").Should().BeFalse();
        File.Exists(Path.Combine(_root, ".scrinia", "memories", "findings", ".lock")).Should().BeFalse();
        File.Exists(Path.Combine(_root, ".scrinia", "memories", "findings", "scratch.tmp")).Should().BeFalse();
        result.Moved.Should().Be(1);
        Directory.Exists(Path.Combine(_root, ".scrinia", "topics")).Should().BeFalse();
    }

    [Fact]
    public void Migrate_Collision_EntityBeforeMemory_Deterministic()
    {
        Write("topics/entity/skill/qa.nmp2", "entity-wins");
        Write("topics/memory/skill/qa.nmp2", "memory-loses");

        var result = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        result.Conflicts.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, ".scrinia", "memories", "skill", "qa.nmp2"))
            .Should().Be("entity-wins");
        Directory.EnumerateDirectories(_root, ".scrinia-conflicts-*").Should().NotBeEmpty();
    }

    [Fact]
    public void Migrate_IsIdempotent_SecondRunDoesNothing()
    {
        Write("topics/findings/review.nmp2", "b");
        StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        var second = StoreLayoutMigration.Migrate(_root, dryRun: false, backup: true);

        second.Ran.Should().BeFalse();
        second.Moved.Should().Be(0);
        second.BackupPath.Should().BeNull();
        Directory.EnumerateDirectories(_root, ".scrinia-backup-*").Should().BeEmpty();
    }

    [Fact]
    public void Migrate_DryRun_MovesNothing()
    {
        Write("topics/findings/review.nmp2", "b");
        var result = StoreLayoutMigration.Migrate(_root, dryRun: true, backup: false);

        result.Moved.Should().Be(1);
        Exists("topics/findings/review.nmp2").Should().BeTrue();
        Exists("memories/findings/review.nmp2").Should().BeFalse();
    }

    [Fact]
    public void Migrate_Resumes_AfterPartialMigration()
    {
        Write("memories/findings/already.nmp2", "done");   // simulates interrupted run
        Write("topics/findings/pending.nmp2", "todo");

        StoreLayoutMigration.Migrate(_root, dryRun: false, backup: false);

        Exists("memories/findings/already.nmp2").Should().BeTrue();
        Exists("memories/findings/pending.nmp2").Should().BeTrue();
    }

    [Fact]
    public void NeedsMigration_FalseWhenOnlyMemoriesExists()
    {
        Write("memories/findings/review.nmp2", "b");
        StoreLayoutMigration.NeedsMigration(_root).Should().BeFalse();
    }

    [Fact]
    public void BothTriggers_ProduceIdenticalLayout()
    {
        Write("topics/findings/review.nmp2", "b");
        Write("store/notes.nmp2", "a");
        StoreLayoutMigration.MigrateIfNeeded(_root);

        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("memories/notes.nmp2").Should().BeTrue();
        StoreLayoutMigration.NeedsMigration(_root).Should().BeFalse();
    }

    [Fact]
    public void DiscoverTopics_AsFirstAccess_MigratesBeforeScanning_NoDuplicateScope()
    {
        // DiscoverTopics scans the legacy topics/ root itself (v1) in addition to gating through
        // MemoriesRoot (v2). If the gate ran AFTER the v1 scan, a first-ever call with real
        // legacy content would see "local:findings" from the pre-migration v1 scan AND again
        // from the post-migration v2 scan of memories/ — the same scope reported twice.
        Write("topics/findings/review.nmp2", "b");

        var store = new FileMemoryStore(_root);
        var topics = store.DiscoverTopics();

        topics.Should().ContainSingle(t => t == "local:findings");
        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("topics/findings/review.nmp2").Should().BeFalse();
    }

    [Fact]
    public void FileMemoryStore_MigratesAutomatically_OnFirstAccess()
    {
        Write("topics/findings/review.nmp2", "body");
        // NOTE: the brief's inline fixture omitted "Uri", which ArtifactEntry's real
        // constructor requires as its 2nd positional parameter (no default value) — the same
        // class of defect an earlier task hit. Included here to match the real serialized shape
        // FileStoreJsonContext produces (PropertyNamingPolicy = CamelCase, but
        // PropertyNameCaseInsensitive = true makes the PascalCase keys below deserialize fine).
        Write("topics/findings/review.meta.json",
            """{"Name":"review","Uri":"file:///review.nmp2","OriginalBytes":4,"ChunkCount":1,"CreatedAt":"2026-08-01T00:00:00+00:00","ContentPreview":"body"}""");

        var store = new FileMemoryStore(_root);
        var entries = store.LoadIndex("local:findings");

        entries.Should().ContainSingle(e => e.Name == "review");
        Exists("memories/findings/review.nmp2").Should().BeTrue();
        Exists("topics/findings/review.nmp2").Should().BeFalse();
    }

    [Fact]
    public void MigrateIfNeeded_SecondAttempt_WithStuckLegacyLock_DoesNotWriteSecondBackup()
    {
        // Reproduces the unbounded-backup bug: a held ".lock" (simulating a concurrent
        // FileMemoryStore operation) survives the transient-artifact delete, so
        // PruneEmptyLegacyRoots can't clear "topics/", and NeedsMigration keeps reporting
        // true forever. Every new process (each CLI invocation is a new process) would
        // otherwise write another full timestamped .scrinia-backup-*/ copy on every call.
        Write("topics/findings/review.nmp2", "b");
        string lockPath = Write("topics/findings/.lock", "");

        using (var heldLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var first = StoreLayoutMigration.MigrateIfNeeded(_root);
            first.Ran.Should().BeTrue();
            first.BackupPath.Should().NotBeNull();
            Directory.EnumerateDirectories(_root, ".scrinia-backup-*").Should().HaveCount(1);

            // The held lock kept the delete from succeeding, so the legacy root never empties
            // out and migration is asked to run again on next access.
            StoreLayoutMigration.NeedsMigration(_root).Should().BeTrue();

            StoreLayoutMigration.MigrateIfNeeded(_root);

            Directory.EnumerateDirectories(_root, ".scrinia-backup-*").Should().HaveCount(1,
                "a backup from a prior attempt already preserves the pre-migration state; " +
                "writing another would grow .scrinia-backup-*/ without bound");
        }
    }
}

/// <summary>
/// Unit-tests <see cref="StoreLayoutMigration.OrderPlan"/> directly against deliberately
/// adversarial (reversed) input, constructing <see cref="StoreLayoutMigration.PlanEntry"/>
/// instances by hand rather than driving the order through the filesystem.
///
/// <para>This is the regression guard the end-to-end
/// <see cref="StoreLayoutMigrationTests.Migrate_Collision_EntityBeforeMemory_Deterministic"/>
/// cannot be: on NTFS, <c>Directory.EnumerateFiles</c> happens to return <c>entity/...</c> before
/// <c>memory/...</c> alphabetically regardless of creation order, so that test would pass even
/// against the pre-fix stable sort over a single "is namespaced" boolean — the natural
/// enumeration order coincidentally agrees with the intended rank. Supplying entries directly, in
/// the wrong order, is the only way to prove the ordering logic itself enforces the rank rather
/// than merely preserving whatever order happened to be handed to it.</para>
/// </summary>
public sealed class StoreLayoutMigrationOrderingTests
{
    [Fact]
    public void OrderPlan_EntityRanksBeforeMemory_EvenWhenSuppliedInReverse()
    {
        // Deliberately supplied memory-first, entity-second — the wrong order — so only a real
        // rank comparison (not a stable sort preserving input order) can produce entity first.
        var reversed = new[]
        {
            new StoreLayoutMigration.PlanEntry(
                @"C:\ws\.scrinia\topics\memory\skill\qa.nmp2",
                @"C:\ws\.scrinia\memories\skill\qa.nmp2",
                NamespaceRank: 2),
            new StoreLayoutMigration.PlanEntry(
                @"C:\ws\.scrinia\topics\entity\skill\qa.nmp2",
                @"C:\ws\.scrinia\memories\skill\qa.nmp2",
                NamespaceRank: 1),
        };

        var ordered = StoreLayoutMigration.OrderPlan(reversed);

        ordered.Should().HaveCount(2);
        ordered[0].NamespaceRank.Should().Be(1);
        ordered[0].Source.Should().Contain("entity");
        ordered[1].NamespaceRank.Should().Be(2);
        ordered[1].Source.Should().Contain("memory");
    }

    [Fact]
    public void OrderPlan_SameRank_BreaksTiesOnOrdinalSourcePath_EvenWhenSuppliedInReverse()
    {
        // Same NamespaceRank on both entries — only the ThenBy(Source) tiebreak can produce
        // ordinal order here. Supplied 'zzz' before 'aaa', the wrong order.
        var reversed = new[]
        {
            new StoreLayoutMigration.PlanEntry(
                @"C:\ws\.scrinia\topics\zzz\file.nmp2",
                @"C:\ws\.scrinia\memories\zzz\file.nmp2",
                NamespaceRank: 0),
            new StoreLayoutMigration.PlanEntry(
                @"C:\ws\.scrinia\topics\aaa\file.nmp2",
                @"C:\ws\.scrinia\memories\aaa\file.nmp2",
                NamespaceRank: 0),
        };

        var ordered = StoreLayoutMigration.OrderPlan(reversed);

        ordered.Should().HaveCount(2);
        ordered[0].Source.Should().Be(@"C:\ws\.scrinia\topics\aaa\file.nmp2");
        ordered[1].Source.Should().Be(@"C:\ws\.scrinia\topics\zzz\file.nmp2");
    }
}
