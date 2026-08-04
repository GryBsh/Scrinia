using Scrinia.Core.Resilience;

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
///
/// <para><b>Neither <c>agent</c> nor <c>skills</c> is swept.</b> Both top-level directories hold
/// live, hand-authored Markdown resolved by <see cref="PathRouter.ToFileEntityDir"/>. An earlier
/// revision swept <c>agent</c> into <c>memories/agent/</c>; that put <c>AgentFileMeta</c> sidecars
/// in a directory <see cref="FileMemoryStore"/> enumerates, where
/// <see cref="FileMemoryStore.SaveIndex"/>'s orphan sweep deleted them. Keeping file entities out
/// of every scope directory makes that impossible by construction.</para>
///
/// <para>Nothing under <c>topics/agent/</c> or <c>topics/entity/skill/</c> is affected by any of
/// this — those are FileMemoryStore's own NMP2 layout, nested under the (already-swept)
/// <c>topics/</c> root, and migrate exactly as before.</para>
///
/// <para><b>Final-review fix: <c>index.json</c>-only directories are expanded, not moved.</b> A
/// legacy directory whose entries still live in a v1 <c>index.json</c> (rather than per-entry
/// <c>.meta.json</c> sidecars) used to have that file relocated as an ordinary file. Merged
/// against a sidecar-based sibling directory targeting the same <c>memories/&lt;topic&gt;/</c>
/// (the common case — <c>entity/</c>/<c>memory/</c> and the <c>index.json</c> format are both the
/// oldest on-disk shapes), <see cref="FileMemoryStore.LoadIndexUnsafe"/>'s old "consult
/// <c>index.json</c> only when the sidecar scan returns zero entries" fallback meant the
/// non-empty sidecar scan always won and the <c>index.json</c> side's entries — describing real
/// files that moved to the right place — became permanently invisible. <see cref="BuildPlan"/>
/// now recognises <c>index.json</c> (<see cref="IsLegacyIndexFile"/>) and routes it to
/// <see cref="IndexFileEntry"/> instead of a normal <see cref="PlanEntry"/>; <see cref="RunMigration"/>
/// expands its entries into real sidecars via <see cref="FileMemoryStore.ExpandLegacyIndexEntries"/>
/// (after the main move loop, so a real sidecar from the sibling directory already occupies its
/// spot and wins) and then deletes the now-redundant <c>index.json</c>. This is what
/// <c>LoadEntriesFromLegacyIndex</c>'s read-time upgrade used to do lazily on the read path — moved
/// into the migration per the design's deletion list, since the read path no longer has a
/// fallback to fall into.</para>
/// </summary>
public static class StoreLayoutMigration
{
    private static readonly string[] LegacyRoots = ["store", "topics"];

    /// <param name="Moved">Files actually relocated into <c>memories/</c> (a dry run reports
    /// <c>plan.Count - Conflicts</c> — collision losers are quarantined, not moved).</param>
    /// <param name="Conflicts">Plan entries that lost a target collision — either to another plan
    /// entry or to a file already sitting at the target (the resumption case).</param>
    /// <param name="Ran">Whether a migration pass actually executed (false for a no-op or a dry
    /// run).</param>
    /// <param name="BackupPath">Where the pre-migration backup was written, or null if none was
    /// requested — or if one was requested but skipped; see <see cref="BackupSkipped"/>.</param>
    /// <param name="BackupSkipped">True when <c>backup: true</c> was requested but no backup was
    /// written because a <c>.scrinia-backup-*/</c> directory already existed at the workspace
    /// root — see the comment at the backup block in <see cref="RunMigration"/>. Distinct from
    /// simply not requesting one (<c>BackupPath is null &amp;&amp; !BackupSkipped</c>), so a caller
    /// can tell "no backup was asked for" apart from "one was asked for and silently withheld".</param>
    public sealed record Result(int Moved, int Conflicts, bool Ran, string? BackupPath, bool BackupSkipped = false);

    /// <summary>
    /// One planned move: an absolute <paramref name="Source"/> path to an absolute
    /// <paramref name="Target"/> path, tagged with the namespace rank used to order (and thereby
    /// deterministically resolve collisions in) the plan — see <see cref="OrderPlan"/>.
    /// Internal (not private) so <see cref="OrderPlan"/> is directly unit-testable: ordering
    /// depends on <c>Directory.EnumerateFiles</c>' enumeration order for its input, which .NET
    /// does not specify and a filesystem-driven test cannot reliably disagree with, so the
    /// ordering logic needs to be exercised directly with deliberately adversarial input.
    /// </summary>
    /// <param name="Source">Absolute path of the file as it currently sits in the legacy layout.</param>
    /// <param name="Target">Absolute path of where it will land under <c>memories/</c>.</param>
    /// <param name="NamespaceRank">0 for non-namespaced (flat topics, <c>store/</c>,
    /// <c>topics/agent/</c>), 1 for <c>entity/</c>, 2 for <c>memory/</c>.</param>
    internal readonly record struct PlanEntry(string Source, string Target, int NamespaceRank);

    /// <summary>
    /// One legacy <c>index.json</c> found during the scan, paired with the directory its
    /// sibling files land in under <c>memories/</c>. Never moved as a file — see
    /// <see cref="IsLegacyIndexFile"/> and the class doc comment's CRITICAL fix note. Processed
    /// after the main move loop so <see cref="TargetDir"/> already reflects every sidecar that
    /// won its collision, and expansion (<see cref="FileMemoryStore.ExpandLegacyIndexEntries"/>)
    /// can correctly skip an entry a real sidecar already describes.
    /// </summary>
    internal readonly record struct IndexFileEntry(string IndexPath, string TargetDir);

    /// <summary>
    /// Marker delimiting the block <see cref="EnsureWorkspaceGitIgnoreForMigrationArtifacts"/>
    /// appends, so a re-run can detect it's already there without re-parsing or duplicating rules.
    /// </summary>
    private const string WorkspaceGitIgnoreMarker = "# --- scrinia: migration artifacts (auto-added) ---";

    /// <summary>
    /// Idempotently appends ignore rules for this engine's own workspace-root artifacts
    /// (<c>.scrinia-backup-*/</c>, <c>.scrinia-conflicts-*/</c>, <c>.scrinia-migration.lock</c>) to
    /// the WORKSPACE <c>.gitignore</c> — a different file from <c>.scrinia/.gitignore</c>
    /// (<see cref="FileMemoryStore.EnsureGitIgnore"/>), which lives INSIDE <c>.scrinia/</c> and
    /// therefore cannot ignore its own siblings at the workspace root.
    ///
    /// <para>Additive only: reads any existing workspace <c>.gitignore</c> and appends the marked
    /// block after it, never truncating or reordering existing content. Skips entirely if
    /// <see cref="WorkspaceGitIgnoreMarker"/> is already present, so repeated migration attempts
    /// (the stuck-legacy-lock retry case elsewhere in this file) don't grow the file. Deliberately
    /// does NOT move <c>.scrinia-migration.lock</c> itself into <c>.scrinia/</c> to be covered by
    /// the existing <c>**/*.lock</c> rule there — its sibling placement is required to avoid the
    /// backup self-deadlock described at the lock-acquisition comment in <see cref="RunMigration"/>.
    /// </para>
    ///
    /// <para>Best-effort: a failure to read or write the workspace <c>.gitignore</c> (permissions,
    /// a concurrent editor holding it open) must not abort the migration itself — an un-ignored
    /// artifact is a git-hygiene issue, not a data-loss one.</para>
    /// </summary>
    private static void EnsureWorkspaceGitIgnoreForMigrationArtifacts(string workspaceRoot)
    {
        try
        {
            string gitIgnorePath = Path.Combine(workspaceRoot, ".gitignore");
            string existing = File.Exists(gitIgnorePath) ? File.ReadAllText(gitIgnorePath) : "";
            if (existing.Contains(WorkspaceGitIgnoreMarker, StringComparison.Ordinal))
                return;

            string block =
                WorkspaceGitIgnoreMarker + "\n" +
                ".scrinia-backup-*/\n" +
                ".scrinia-conflicts-*/\n" +
                ".scrinia-migration.lock\n" +
                "# --- end scrinia ---\n";

            string updated = existing.Length == 0 ? block
                : existing.EndsWith('\n') ? existing + "\n" + block
                : existing + "\n\n" + block;

            FileMemoryStore.AtomicWriteAllText(gitIgnorePath, updated);
        }
        catch { /* best-effort; must not abort the migration itself */ }
    }

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

    /// <summary>
    /// Automatic trigger. No-op when nothing needs moving. Requests a backup, but
    /// <see cref="RunMigration"/> skips writing one when a prior attempt already left one
    /// behind — see the comment at its backup block for why.
    /// </summary>
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
        var (plan, transientFiles, indexFiles) = BuildPlan(scriniaDir);
        if (plan.Count == 0 && transientFiles.Count == 0 && indexFiles.Count == 0)
            return new Result(0, 0, Ran: false, BackupPath: null);

        if (dryRun)
        {
            // Moved excludes collision losers (quarantined, not moved) — CountCollisions checks
            // both in-plan duplicates AND a target that already exists on disk (the resumption
            // case), matching exactly what the real move loop below checks via File.Exists.
            int dryRunCollisions = CountCollisions(plan);
            return new Result(plan.Count - dryRunCollisions, dryRunCollisions, Ran: false, BackupPath: null);
        }

        // Every artifact this engine creates below (.scrinia-migration.lock, .scrinia-backup-*/,
        // .scrinia-conflicts-*/) lands at the WORKSPACE ROOT, sibling to .scrinia/ — see the lock
        // path comment just below for why. This project deliberately commits .scrinia/, and an
        // automatic, unprompted migration can fire on a plain read (any first GetStoreDirForScope
        // call), so a `git add -A` moments later could otherwise commit a full duplicate backup
        // copy of the user's store before anyone thought to update .gitignore. Ensuring the
        // ignore rule exists BEFORE creating any of these artifacts closes that window; see
        // EnsureWorkspaceGitIgnoreForMigrationArtifacts's own doc comment for why this is a
        // separate file from FileMemoryStore.EnsureGitIgnore (.scrinia/.gitignore).
        EnsureWorkspaceGitIgnoreForMigrationArtifacts(workspaceRoot);

        // The lock file must live OUTSIDE .scrinia/, not inside it: the backup below does a
        // recursive copy of scriniaDir, and FileLock.AcquireExclusive opens with FileShare.None,
        // so a lock file placed under scriniaDir would make File.Copy throw IOException on its
        // own lock the moment a backup ran (self-deadlock-on-read, not a race with another
        // process). Sibling to .scrinia/ avoids the conflict entirely — deliberately NOT moved by
        // this fix; see the comment there for the deadlock this placement avoids.
        string lockPath = Path.Combine(workspaceRoot, ".scrinia-migration.lock");
        var fileLock = FileLock.AcquireExclusive(lockPath);
        try
        {
            string? backupPath = null;
            bool backupSkipped = false;
            if (backup)
            {
                // Bounded: skip when a prior backup FROM THIS ENGINE already exists — not any
                // ".scrinia-backup-*" directory at the workspace root. The broader glob used to
                // also match ".scrinia-backup-attrs-{ts}" (scri migrate_attributes's own
                // BackupScriniaDir — renamed below specifically to stop colliding here) and any
                // directory a user copied back while following the documented "re-run after
                // restoring a backup" workflow (docs/cli-reference.md). Either one would silently
                // suppress the pre-migration backup THIS run is about to need, and the CLI only
                // prints a backup line when BackupPath is non-null — so the user would see
                // "Migrated N files" with no hint that nothing was actually backed up this time.
                // IsOwnBackupDirName's exact-shape check keeps the original bound intact (a
                // legacy root that never fully empties, e.g. a stuck scope .lock, still must not
                // grow .scrinia-backup-{ts}/ without bound across repeated automatic-trigger
                // invocations) while no longer matching another tool's differently-named backup.
                // A directory that happens to be an exact-shape ".scrinia-backup-{ts}" left behind
                // by a manual restore is not distinguishable from this engine's own — that residual
                // gap is exactly why Result.BackupSkipped exists: whenever the guard does skip,
                // the caller (and therefore the user) is told, rather than finding out never.
                bool priorBackupExists = Directory.EnumerateDirectories(workspaceRoot, ".scrinia-backup-*")
                    .Any(dir => IsOwnBackupDirName(Path.GetFileName(dir)));
                if (!priorBackupExists)
                {
                    backupPath = Path.Combine(workspaceRoot,
                        $".scrinia-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
                    CopyDirectory(scriniaDir, backupPath);
                }
                else
                {
                    backupSkipped = true;
                }
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
                    MoveWithRetry(source, quarantine, overwrite: true);
                    conflicts++;
                    continue;
                }

                MoveWithRetry(source, target, overwrite: false);
                moved++;
            }

            // Legacy index.json → sidecar expansion runs AFTER the main move loop above, so by
            // the time an index.json's entries are considered, every real .meta.json sidecar that
            // won its collision already sits in its target directory — ExpandLegacyIndexEntries
            // skips an entry a real sidecar already describes, so a merged sibling directory's
            // sidecar always wins over stale index.json-derived data for the same name. See the
            // class doc comment's CRITICAL fix note.
            foreach (var (indexPath, targetDir) in indexFiles)
            {
                if (!File.Exists(indexPath)) continue; // resumption: already expanded and deleted
                var entries = FileMemoryStore.LoadEntriesFromLegacyIndex(Path.GetDirectoryName(indexPath)!);
                FileMemoryStore.ExpandLegacyIndexEntries(entries, targetDir);
                try { File.Delete(indexPath); } catch { /* leave it; next run retries */ }
            }

            MoveRenamedVectorDirs(scriniaDir);
            PruneEmptyLegacyRoots(scriniaDir);
            return new Result(moved, conflicts, Ran: true, backupPath, backupSkipped);
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
    /// True for exactly this engine's own backup directory name shape — <c>.scrinia-backup-</c>
    /// followed by an 8-digit date and a 6-digit time (<c>RunMigration</c>'s
    /// <c>DateTimeOffset.UtcNow:yyyyMMdd-HHmmss</c> format), nothing else. Deliberately excludes
    /// <c>.scrinia-backup-attrs-{ts}</c> (<c>ScriniaCommands.BackupScriniaDir</c>, used by
    /// <c>scri migrate_attributes --backup</c>) — see the backup-guard comment in
    /// <see cref="RunMigration"/> for why the two must not collide.
    /// </summary>
    private static bool IsOwnBackupDirName(string dirName) =>
        System.Text.RegularExpressions.Regex.IsMatch(dirName, @"^\.scrinia-backup-\d{8}-\d{6}$");

    /// <summary>
    /// Scans every legacy root and splits what it finds into three lists: the move plan (ordered
    /// by <see cref="OrderPlan"/>), known-transient artifacts to delete outright (see
    /// <see cref="IsTransientArtifact"/>), and legacy <c>index.json</c> files to expand into
    /// sidecars and then delete (see <see cref="IsLegacyIndexFile"/> and
    /// <see cref="IndexFileEntry"/>). Each move-plan entry carries the
    /// <see cref="PlanEntry.NamespaceRank"/> that <see cref="OrderPlan"/> sorts on.
    /// </summary>
    private static (List<PlanEntry> Plan, List<string> TransientFiles, List<IndexFileEntry> IndexFiles) BuildPlan(string scriniaDir)
    {
        var plan = new List<PlanEntry>();
        var transientFiles = new List<string>();
        var indexFiles = new List<IndexFileEntry>();
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
                if (IsLegacyIndexFile(file)) { indexFiles.Add(new IndexFileEntry(file, Path.GetDirectoryName(target)!)); continue; }
                plan.Add(new PlanEntry(file, target, NamespaceRank: 0));
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
                if (IsLegacyIndexFile(file)) { indexFiles.Add(new IndexFileEntry(file, Path.GetDirectoryName(target)!)); continue; }
                int rank = relPath.StartsWith("entity/", StringComparison.OrdinalIgnoreCase) ? 1
                    : relPath.StartsWith("memory/", StringComparison.OrdinalIgnoreCase) ? 2
                    : 0;
                plan.Add(new PlanEntry(file, target, rank));
            }
        }

        // NOTE: no .scrinia/skills/* or .scrinia/agent/* sweep. Both hold live, hand-authored
        // Markdown that PathRouter.ToFileEntityDir resolves at the top level of .scrinia/.
        // They deliberately stay OUT of memories/: their .meta.json sidecars share a filename
        // namespace with the store's own ArtifactEntry sidecars, so a file entity inside a scope
        // directory is deleted by SaveIndex's orphan sweep. topics/entity/skill/ (FileMemoryStore's
        // own NMP2 layout) still migrates via step 2 — unrelated to the top-level directories.

        return (OrderPlan(plan), transientFiles, indexFiles);
    }

    /// <summary>
    /// True for a legacy v1 <c>index.json</c> — the pre-sidecar era's single per-directory entry
    /// list. Never moved as an ordinary file (that was the CRITICAL bug this fix round closes —
    /// see the class doc comment): it is expanded into per-entry sidecars via
    /// <see cref="FileMemoryStore.ExpandLegacyIndexEntries"/> and then deleted, so the entries it
    /// describes stay reachable after the merge instead of silently shadowed by a sidecar scan
    /// that stops looking once it finds ANY sidecar in the merged target directory.
    /// </summary>
    private static bool IsLegacyIndexFile(string filePath) =>
        Path.GetFileName(filePath).Equals("index.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deterministic ordering: <see cref="PlanEntry.NamespaceRank"/> ascending (non-namespaced,
    /// then <c>entity/</c>, then <c>memory/</c>), full source path as the final tiebreak. Not just
    /// a stable sort over discovery order — <c>Directory.EnumerateFiles</c>' enumeration order is
    /// unspecified by .NET, so without this explicit tiebreak two namespaced entries colliding on
    /// the same target (e.g. <c>entity/skill/x</c> and <c>memory/skill/x</c>, realistic because
    /// "skill" was historically classified as an entity topic) would pick an
    /// arbitrary, unrepeatable winner. Extracted as its own method (rather than inlined in
    /// <see cref="BuildPlan"/>) so it can be unit-tested directly against adversarial input that
    /// disagrees with whatever order the filesystem happens to enumerate in — see
    /// <c>StoreLayoutMigrationOrderingTests</c> in the test project.
    /// </summary>
    internal static List<PlanEntry> OrderPlan(IEnumerable<PlanEntry> entries) =>
        entries.OrderBy(e => e.NamespaceRank).ThenBy(e => e.Source, StringComparer.Ordinal).ToList();

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
    /// <summary>
    /// Predicts how many plan entries the real move loop above would quarantine, WITHOUT moving
    /// anything — used by the dry-run path. Two ways an entry collides, both mirrored here exactly
    /// as the real loop checks them: (a) <c>File.Exists(target)</c> — a file already sits at the
    /// target, the resumption case (a prior partial run already placed something there); (b) the
    /// target was already claimed by an earlier entry IN THIS PLAN. (a) is checked first and, on a
    /// hit, is deliberately NOT also added to <paramref name="plan"/>'s seen-targets set: a second
    /// plan entry aimed at the same already-occupied target hits (a) again and is independently
    /// counted, matching the real loop (which quarantines both, since the pre-existing occupant
    /// never moves either time).
    /// </summary>
    private static int CountCollisions(List<PlanEntry> plan)
    {
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int collisions = 0;
        foreach (var (_, target, _) in plan)
        {
            if (File.Exists(target) || !seenTargets.Add(target))
                collisions++;
        }
        return collisions;
    }

    /// <summary>
    /// Moves a real content file as part of the plan (either into its final <c>memories/</c>
    /// target or into a conflict-quarantine directory), retrying transient sharing violations
    /// via <see cref="FileRetry"/> — unlike the transient-artifact delete and the backup copy,
    /// this must NOT silently skip-and-continue on failure: a real memory left behind un-moved
    /// would, after Task 8 removes the legacy read-fallback, become invisible to every reader
    /// while still sitting on disk. Loud and retryable is correct here; silent and lossy is not.
    ///
    /// <para>Catches <see cref="UnauthorizedAccessException"/> alongside <see cref="IOException"/>:
    /// <c>File.Move(source, target, overwrite: true)</c> — the quarantine call site — can throw
    /// either when <paramref name="target"/> already exists and is locked, depending on exactly how
    /// the sharing violation surfaces on Windows (empirically confirmed in review). Deliberately NOT
    /// widening <see cref="FileRetry"/> itself for this: its contract treats
    /// <see cref="UnauthorizedAccessException"/> as a genuine configuration mistake to surface
    /// immediately elsewhere in the codebase, so the extra tolerance is local to this call site,
    /// not retried again here — wrapping it immediately (rather than silently skipping) is what
    /// this method exists for.</para>
    ///
    /// <para>If <see cref="FileRetry.Run(Action, Microsoft.Extensions.Logging.ILogger?)"/> exhausts
    /// its retries, or an <see cref="UnauthorizedAccessException"/> reaches here, the file is
    /// genuinely stuck (not just transiently busy), so this wraps the bare exception in one that
    /// names both paths and states the most likely cause — actionable rather than an opaque failure
    /// surfacing from <c>LoadIndex</c>/<c>Upsert</c>/etc. Resumable regardless: the caller's
    /// <c>if (!File.Exists(source)) continue</c> check means a later migration attempt picks up
    /// exactly where this one stopped.</para>
    ///
    /// <para>Internal (not private), matching this file's <see cref="OrderPlan"/> precedent: the
    /// <see cref="UnauthorizedAccessException"/> branch needs a pre-existing LOCKED target file to
    /// exercise, and the real quarantine call site's target lives under a timestamped
    /// <c>.scrinia-conflicts-*/</c> directory computed at runtime inside <see cref="RunMigration"/>
    /// — there is no way to pre-lock that exact path from outside a single end-to-end
    /// <see cref="Migrate"/> call, so the adversarial input is constructed directly instead.</para>
    /// </summary>
    internal static void MoveWithRetry(string source, string target, bool overwrite)
    {
        try
        {
            FileRetry.Run(() => File.Move(source, target, overwrite));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Store layout migration could not move '{source}' to '{target}'. A concurrent " +
                "process holding the file open — another scrinia process, an antivirus scan, or " +
                "a sync client such as Synology Drive — is the most common cause. Nothing has " +
                "been lost — this file (and any after it in this run) will be retried the next " +
                "time the store is accessed.",
                ex);
        }
    }

    /// <summary>Recursive file copy, used for the pre-migration backup. Best-effort per file:
    /// a file held open by a concurrent process (e.g. an in-use scope <c>.lock</c>, the same
    /// condition the backup-bounding above is guarding against) is skipped rather than aborting
    /// the whole backup — a live lock file carries no committed data worth preserving anyway.
    /// Catches <see cref="UnauthorizedAccessException"/> alongside <see cref="IOException"/> —
    /// on Windows a sharing violation can surface as either — matching <see cref="TryMoveVectorDir"/>'s
    /// handling of the same class of problem.</summary>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(targetDir, Path.GetFileName(file));
            try { File.Copy(file, destFile, overwrite: false); }
            catch (IOException) { /* concurrently held; skip, nothing committed to preserve */ }
            catch (UnauthorizedAccessException) { /* concurrently held; skip, nothing committed to preserve */ }
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
        {
            string destDir = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    /// <summary>
    /// Renames the two vector-store scope FAMILIES whose on-disk path changes under v3, per
    /// <c>VectorStore.ScopeToRelativeDir</c>: every <c>embeddings/local/entity/&lt;topic&gt;</c> →
    /// <c>embeddings/local/&lt;topic&gt;</c> (not just <c>skill</c> — see the IMPORTANT-2
    /// final-review fix note below), and each <c>embeddings/local/memory/&lt;topic&gt;</c> →
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
    ///
    /// <para><b>Final-review fix (IMPORTANT 2):</b> the original version special-cased
    /// <c>entity/skill</c> only, matching <see cref="MapTopicFileToV2"/>'s move for the memories
    /// themselves — but <see cref="MapTopicFileToV2"/> strips <c>entity/</c> for ANY child topic,
    /// not just <c>skill</c>. Any other historical entity topic — e.g. <c>task</c> or
    /// <c>concern</c>, both classified as entity topics in the pre-flattening v2 layout — had its
    /// memories relocated correctly by the main plan while its embeddings sat orphaned at
    /// <c>embeddings/local/entity/&lt;topic&gt;/</c>, silently, with no pointer to
    /// <c>scri reindex</c>. Now iterated the same way as the <c>memory/</c> family below, instead
    /// of a single hardcoded <c>entity/skill</c> path.</para>
    /// </summary>
    private static void MoveRenamedVectorDirs(string scriniaDir)
    {
        string embeddingsLocal = Path.Combine(scriniaDir, "embeddings", "local");
        if (!Directory.Exists(embeddingsLocal))
            return;

        // entity/<topic> -> <topic> for EVERY historical entity topic, not just "skill".
        // VectorStore.ScopeToRelativeDir derives the embedding directory purely from splitting
        // the scope string on separators, so any v2 "local:entity/<topic>" scope (skill was never
        // the only one — historical entity topics included "task" and "concern" as
        // legacy flat entity topics, and MapTopicFileToV2 strips entity/ for any child, not just
        // skill) has its vectors at embeddings/local/entity/<topic>/ before this migration and
        // needs them at embeddings/local/<topic>/ after — matching where the memories themselves
        // land. A skill-only special case here left every other entity topic's vectors orphaned,
        // silently, with no pointer to `scri reindex`.
        string entityDir = Path.Combine(embeddingsLocal, "entity");
        if (Directory.Exists(entityDir))
        {
            foreach (string topicDir in Directory.GetDirectories(entityDir))
            {
                string topic = Path.GetFileName(topicDir);
                TryMoveVectorDir(topicDir, Path.Combine(embeddingsLocal, topic));
            }
            PruneIfEmpty(entityDir);
        }

        string memoryDir = Path.Combine(embeddingsLocal, "memory");
        if (Directory.Exists(memoryDir))
        {
            foreach (string topicDir in Directory.GetDirectories(memoryDir))
            {
                string topic = Path.GetFileName(topicDir);
                TryMoveVectorDir(topicDir, Path.Combine(embeddingsLocal, topic));
            }
            PruneIfEmpty(memoryDir);
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

    /// <summary>Deletes <paramref name="dir"/> if every child moved out of it, mirroring
    /// <see cref="PruneEmptyLegacyRoots"/>'s treatment of the memory-side legacy roots — the
    /// now-empty <c>embeddings/local/entity/</c>/<c>embeddings/local/memory/</c> parents deserve
    /// the same cleanup, not an inert empty directory left behind forever. Best-effort: a
    /// directory that didn't fully empty (a move above was left in place after a failure) or a
    /// benign concurrent-access race just means this is retried on the next migration pass.</summary>
    private static void PruneIfEmpty(string dir)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { /* benign race, e.g. concurrent access; retried next pass */ }
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
