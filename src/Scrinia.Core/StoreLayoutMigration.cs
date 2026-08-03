namespace Scrinia.Core;

/// <summary>
/// Migrates a workspace store from any older on-disk layout to v3, where every memory
/// lives under <c>.scrinia/memories/</c>.
///
/// <para>One implementation, two triggers: <see cref="MigrateIfNeeded"/> runs automatically
/// on first store access, <see cref="Migrate"/> backs the `scri migrate` command. Both funnel
/// into <see cref="RunMigration"/> so there is exactly one code path — modelled on
/// <c>EmbeddingReindexer</c>, which pairs an automatic (<c>ReindexIfStaleAsync</c>) and a
/// manual (<c>ForceReindexAsync</c>) entry point the same way.</para>
///
/// <para>This is not a fallback. It moves rather than copies, so exactly one copy of each
/// memory exists at all times, and it terminates permanently once the legacy roots are gone
/// (<see cref="PruneEmptyLegacyRoots"/> deletes them once empty, so <see cref="NeedsMigration"/>
/// keeps returning <see langword="false"/>). Nothing reads through it.</para>
///
/// <para>Ported from <c>ScriniaCommands.BuildMigrationPlan</c>/<c>MapTopicFileToV2</c>
/// (the v1→v2 <c>migrate_v1</c> command), which already targeted <c>.scrinia/memories/</c>
/// and already stripped the <c>entity/</c>/<c>memory/</c> namespace prefixes. This version
/// adds the <c>store/</c> legacy root, stops skipping <c>versions/</c> archives, moves instead
/// of copies, and detects/quarantines collisions instead of silently skipping the loser.</para>
///
/// <para>The sweep is two-tier: known-transient artifacts (a scope's <c>.lock</c> marker,
/// any <c>*.tmp</c> partial write) are deleted rather than relocated — they carry no committed
/// data and moving them just relocates the cruft into <c>memories/</c> where nothing would ever
/// clean it up. Everything else is moved. See <see cref="IsTransientArtifact"/>.</para>
/// </summary>
public static class StoreLayoutMigration
{
    private static readonly string[] LegacyRoots = ["store", "topics", "agent", "skills"];

    public sealed record Result(int Moved, int Conflicts, bool Ran, string? BackupPath);

    /// <summary>True when any legacy root still holds files. Three-stat fast path.</summary>
    public static bool NeedsMigration(string workspaceRoot)
    {
        string scriniaDir = Path.Combine(workspaceRoot, ".scrinia");
        foreach (string root in LegacyRoots)
        {
            string dir = Path.Combine(scriniaDir, root);
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
                return true;
        }
        return false;
    }

    /// <summary>Automatic trigger. No-op when nothing needs moving. Backup always on.</summary>
    public static Result MigrateIfNeeded(string workspaceRoot) =>
        RunMigration(workspaceRoot, dryRun: false, backup: true);

    /// <summary>Manual trigger, backing `scri migrate`.</summary>
    public static Result Migrate(string workspaceRoot, bool dryRun, bool backup) =>
        RunMigration(workspaceRoot, dryRun, backup);

    /// <summary>
    /// The single implementation both <see cref="MigrateIfNeeded"/> and <see cref="Migrate"/>
    /// funnel through — there is no second code path that can drift between the automatic and
    /// manual triggers.
    /// </summary>
    private static Result RunMigration(string workspaceRoot, bool dryRun, bool backup)
    {
        if (!NeedsMigration(workspaceRoot))
            return new Result(0, 0, Ran: false, BackupPath: null);

        string scriniaDir = Path.Combine(workspaceRoot, ".scrinia");
        var (plan, transientFiles) = BuildPlan(scriniaDir);
        if (plan.Count == 0 && transientFiles.Count == 0)
            return new Result(0, 0, Ran: false, BackupPath: null);

        if (dryRun)
            return new Result(plan.Count, CountCollisions(plan), Ran: false, BackupPath: null);

        // The lock file must live OUTSIDE .scrinia/, not inside it: the backup below does a
        // recursive copy of scriniaDir, and FileLock.AcquireExclusive opens with FileShare.None,
        // so a lock file placed under scriniaDir would make File.Copy throw IOException on its
        // own lock the moment a backup ran (self-deadlock-on-read, not a race with another
        // process). Sibling to .scrinia/ avoids the conflict entirely.
        string lockPath = Path.Combine(workspaceRoot, ".scrinia-migration.lock");
        var fileLock = FileLock.AcquireExclusive(lockPath);
        try
        {
            string? backupPath = null;
            if (backup)
            {
                backupPath = Path.Combine(workspaceRoot,
                    $".scrinia-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
                CopyDirectory(scriniaDir, backupPath);
            }

            // Known-transient artifacts are deleted, never relocated: a `.lock` is recreated
            // lazily on the next write, and a `.tmp` is by construction an interrupted
            // AtomicWriteAllText that never completed its rename — there is no committed data to
            // preserve. The delete is best-effort: a `.lock` may be held open (FileShare.None) by
            // a concurrent FileMemoryStore operation, in which case we leave it behind rather
            // than abort the whole pass — the next migration attempt retries it, and
            // NeedsMigration only keeps reporting true while a file actually remains.
            foreach (string transient in transientFiles)
            {
                try { File.Delete(transient); } catch { /* leave it; next run retries */ }
            }

            string conflictsDir = Path.Combine(workspaceRoot,
                $".scrinia-conflicts-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");

            int moved = 0, conflicts = 0;
            foreach (var (source, target, _) in plan)
            {
                if (!File.Exists(source)) continue;      // resumption: already moved
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    // Winner precedence (see BuildPlan's ordering): non-namespaced first, then
                    // entity/ before memory/, then source path — so which entry wins is
                    // repeatable given identical input, not an artifact of enumeration order.
                    string quarantine = Path.Combine(conflictsDir, Path.GetRelativePath(scriniaDir, source));
                    Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
                    File.Move(source, quarantine, overwrite: true);
                    conflicts++;
                    continue;
                }

                File.Move(source, target);
                moved++;
            }

            MoveRenamedVectorDirs(scriniaDir);
            PruneEmptyLegacyRoots(scriniaDir);
            return new Result(moved, conflicts, Ran: true, backupPath);
        }
        finally
        {
            fileLock.Dispose();
            // Unlike the timestamped .scrinia-backup-*/.scrinia-conflicts-* directories, a bare
            // zero-byte lock file at the workspace root doesn't explain itself — clean it up.
            // Best-effort: if something else has it open right after we release, leave it; it's
            // just an empty marker and will be recreated (and deleted again) by the next run.
            try { File.Delete(lockPath); } catch { /* benign */ }
        }
    }

    /// <summary>
    /// Scans every legacy root and splits what it finds into two lists: the move plan (returned
    /// first) and known-transient artifacts to delete outright (returned second — see
    /// <see cref="IsTransientArtifact"/>). Move-plan entries carry a namespace rank used to order
    /// (and thereby deterministically resolve collisions in) the plan: 0 for non-namespaced
    /// entries (flat topics, <c>store/</c>, <c>agent/</c>, <c>skills/</c>), 1 for <c>entity/</c>,
    /// 2 for <c>memory/</c>. Lower ranks are ordered first, so when two sources map to the same
    /// target, the higher-ranked (more-namespaced) loser sees the target already occupied and is
    /// quarantined instead of winning by whatever order <c>Directory.EnumerateFiles</c> happens
    /// to produce (unspecified by .NET). A final tiebreak on the full source path makes the
    /// ordering — and therefore which entry wins — fully deterministic for identical input, even
    /// within the same rank (e.g. two different <c>memory/</c> topics can never collide with each
    /// other, but this keeps the sort well-defined regardless).
    /// </summary>
    private static (List<(string Source, string Target, int NamespaceRank)> Plan, List<string> TransientFiles)
        BuildPlan(string scriniaDir)
    {
        var plan = new List<(string Source, string Target, int NamespaceRank)>();
        var transientFiles = new List<string>();
        string memoriesDir = Path.Combine(scriniaDir, "memories");

        // 1. .scrinia/store/* -> memories/<file> (flat root store, never namespaced).
        string storeDir = Path.Combine(scriniaDir, "store");
        if (Directory.Exists(storeDir))
        {
            foreach (string file in Directory.EnumerateFiles(storeDir, "*", SearchOption.AllDirectories))
            {
                if (IsTransientArtifact(file)) { transientFiles.Add(file); continue; }
                string relPath = Path.GetRelativePath(storeDir, file).Replace('\\', '/');
                string target = Path.Combine(memoriesDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                plan.Add((file, target, NamespaceRank: 0));
            }
        }

        // 2. .scrinia/topics/* -> memories/* via MapTopicFileToV2 (strips entity/ and memory/
        //    namespace prefixes). Versions archives are included — they move with their artifact.
        string topicsDir = Path.Combine(scriniaDir, "topics");
        if (Directory.Exists(topicsDir))
        {
            foreach (string file in Directory.EnumerateFiles(topicsDir, "*", SearchOption.AllDirectories))
            {
                if (IsTransientArtifact(file)) { transientFiles.Add(file); continue; }
                string relPath = Path.GetRelativePath(topicsDir, file).Replace('\\', '/');
                string target = MapTopicFileToV2(relPath, memoriesDir);
                int rank = relPath.StartsWith("entity/", StringComparison.OrdinalIgnoreCase) ? 1
                    : relPath.StartsWith("memory/", StringComparison.OrdinalIgnoreCase) ? 2
                    : 0;
                plan.Add((file, target, rank));
            }
        }

        // 3. .scrinia/agent/* -> memories/agent/*
        string agentDir = Path.Combine(scriniaDir, "agent");
        if (Directory.Exists(agentDir))
        {
            foreach (string file in Directory.EnumerateFiles(agentDir, "*", SearchOption.AllDirectories))
            {
                if (IsTransientArtifact(file)) { transientFiles.Add(file); continue; }
                string relPath = Path.GetRelativePath(agentDir, file).Replace('\\', '/');
                string target = Path.Combine(memoriesDir, "agent", relPath.Replace('/', Path.DirectorySeparatorChar));
                plan.Add((file, target, NamespaceRank: 0));
            }
        }

        // 4. .scrinia/skills/* -> memories/skill/*
        string skillsDir = Path.Combine(scriniaDir, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (string file in Directory.EnumerateFiles(skillsDir, "*", SearchOption.AllDirectories))
            {
                if (IsTransientArtifact(file)) { transientFiles.Add(file); continue; }
                string relPath = Path.GetRelativePath(skillsDir, file).Replace('\\', '/');
                string target = Path.Combine(memoriesDir, "skill", relPath.Replace('/', Path.DirectorySeparatorChar));
                plan.Add((file, target, NamespaceRank: 0));
            }
        }

        // Deterministic ordering: rank ascending (non-namespaced, then entity/, then memory/),
        // full source path as the final tiebreak. Not just a stable sort over discovery order —
        // Directory.EnumerateFiles order is unspecified by .NET, so without this tiebreak two
        // namespaced entries colliding on the same target (e.g. entity/skill/x and memory/skill/x
        // after skill's historical reclassification, see MemoryNaming.EntityTopics) would pick an
        // arbitrary, unrepeatable winner.
        var ordered = plan
            .OrderBy(p => p.NamespaceRank)
            .ThenBy(p => p.Source, StringComparer.Ordinal)
            .ToList();
        return (ordered, transientFiles);
    }

    /// <summary>
    /// True for artifacts that carry no committed data and must be deleted rather than moved:
    /// a scope's <c>.lock</c> marker (<see cref="FileLock"/> recreates it lazily on the next
    /// write) and any <c>*.tmp</c> file (an interrupted <c>AtomicWriteAllText</c>/
    /// <c>AtomicFileCopy</c> that never completed its rename into place). Relocating either into
    /// <c>memories/</c> would just move the cruft somewhere nothing cleans it up.
    /// </summary>
    private static bool IsTransientArtifact(string filePath)
    {
        string name = Path.GetFileName(filePath);
        return name.Equals(".lock", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a relative path under topics/ to its v3 target under memories/.
    /// Handles the v1/v2 layouts:
    ///   entity/{topic}/{file} → {topic}/{file}     (strip entity/ prefix)
    ///   memory/{topic}/{file} → {topic}/{file}     (strip memory/ prefix)
    ///   agent/{file}          → agent/{file}        (keep as-is)
    ///   {topic}/{file}        → {topic}/{file}      (flat topic, keep as-is)
    /// Ported unchanged from <c>ScriniaCommands.MapTopicFileToV2</c>.
    /// </summary>
    private static string MapTopicFileToV2(string relPath, string memoriesDir)
    {
        string[] parts = relPath.Split('/');

        string mappedRelPath;
        if (parts.Length >= 3 &&
            parts[0].Equals("entity", StringComparison.OrdinalIgnoreCase))
        {
            mappedRelPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts[1..]);
        }
        else if (parts.Length >= 3 &&
                 parts[0].Equals("memory", StringComparison.OrdinalIgnoreCase))
        {
            mappedRelPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts[1..]);
        }
        else
        {
            mappedRelPath = relPath.Replace('/', Path.DirectorySeparatorChar);
        }

        return Path.Combine(memoriesDir, mappedRelPath);
    }

    /// <summary>Counts how many plan entries would lose a collision (i.e. share a target with
    /// an entry earlier in the plan). Used to report conflicts on the dry-run path without
    /// actually moving anything.</summary>
    private static int CountCollisions(List<(string Source, string Target, int NamespaceRank)> plan)
    {
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int collisions = 0;
        foreach (var (_, target, _) in plan)
        {
            if (!seenTargets.Add(target))
                collisions++;
        }
        return collisions;
    }

    /// <summary>Recursive file copy, used for the pre-migration backup.</summary>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            string destDir = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    /// <summary>
    /// Renames the two vector-store scope directories whose on-disk path changes under v3, per
    /// <c>VectorStore.ScopeToRelativeDir</c>: <c>embeddings/local/entity/skill</c> →
    /// <c>embeddings/local/skill</c>, and each <c>embeddings/local/memory/&lt;topic&gt;</c> →
    /// <c>embeddings/local/&lt;topic&gt;</c>. These are the only two scope families whose
    /// directory string changes — everything else is already flat. Best-effort: on failure
    /// (e.g. target already exists) the source directory is left in place and the pass
    /// continues; a subsequent <c>scri reindex</c> rebuilds any vectors this leaves stale.
    ///
    /// <para>Correct as written for the post-flattening world: wiring this engine (Task 7) is
    /// planned to happen only after <c>MemoryNaming.BuildScopedTopicScope</c> stops emitting
    /// <c>"local:entity/skill"</c> (Task 8's flattening moved earlier in the plan) — otherwise
    /// this rename would orphan vectors under a path nothing looks up anymore. That ordering is a
    /// plan-level concern, not an engine one; this method needs no change for it.</para>
    /// </summary>
    private static void MoveRenamedVectorDirs(string scriniaDir)
    {
        string embeddingsLocal = Path.Combine(scriniaDir, "embeddings", "local");
        if (!Directory.Exists(embeddingsLocal))
            return;

        string entitySkillDir = Path.Combine(embeddingsLocal, "entity", "skill");
        if (Directory.Exists(entitySkillDir))
            TryMoveVectorDir(entitySkillDir, Path.Combine(embeddingsLocal, "skill"));

        string memoryDir = Path.Combine(embeddingsLocal, "memory");
        if (Directory.Exists(memoryDir))
        {
            foreach (string topicDir in Directory.GetDirectories(memoryDir))
            {
                string topic = Path.GetFileName(topicDir);
                TryMoveVectorDir(topicDir, Path.Combine(embeddingsLocal, topic));
            }
        }
    }

    private static void TryMoveVectorDir(string source, string target)
    {
        try
        {
            if (Directory.Exists(target))
                return; // Leave the old directory in place; scri reindex will reconcile.

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            // Leave the directory and continue — scri reindex rebuilds.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave the directory and continue — scri reindex rebuilds.
        }
    }

    /// <summary>Deletes each legacy root that is now empty (recursively pruning empty
    /// subdirectories first), so <see cref="NeedsMigration"/> returns false permanently once
    /// every file has moved. This is what makes the migration an engine rather than another
    /// permanent fallback.</summary>
    private static void PruneEmptyLegacyRoots(string scriniaDir)
    {
        foreach (string root in LegacyRoots)
        {
            string dir = Path.Combine(scriniaDir, root);
            if (Directory.Exists(dir))
                PruneEmptyDirectoriesRecursive(dir);
        }
    }

    private static void PruneEmptyDirectoriesRecursive(string dir)
    {
        foreach (string sub in Directory.GetDirectories(dir))
            PruneEmptyDirectoriesRecursive(sub);

        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            try { Directory.Delete(dir); } catch { /* benign race, e.g. concurrent access */ }
        }
    }
}
