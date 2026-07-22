using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Commands.Hooks;
using Scrinia.Mcp;
using Spectre.Console;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    /// <summary>v1→v2 directory-layout migration (legacy). Migrates .scrinia/ data from v1 (topic:name) to v2 (path) structure. One-shot — hidden from top-level help.</summary>
    /// <param name="workspace">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="dryRun">Print what would be copied without actually doing it.</param>
    /// <param name="backup">Create a timestamped backup of .scrinia/ before migrating.</param>
    /// <param name="cleanup">Remove v1 originals after verifying migration.</param>
    [Command("migrate_v1")]
    [Hidden]
    public Task<int> MigrateV1(
        string? workspace = null,
        bool dryRun = false,
        bool backup = true,
        bool cleanup = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspace);
        string root = ScriniaArtifactStore.WorkspaceRootPath;
        string scriniaDir = Path.Combine(root, ".scrinia");

        if (!Directory.Exists(scriniaDir))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No .scrinia/ directory found.");
            return Task.FromResult(1);
        }

        // ── Cleanup mode: remove v1 originals ───────────────────────────
        if (cleanup)
        {
            return RunCleanup(scriniaDir, dryRun);
        }

        // ── Backup ──────────────────────────────────────────────────────
        if (backup && !dryRun)
            BackupScriniaDir(root);

        // ── Gather migration plan ───────────────────────────────────────
        var plan = BuildMigrationPlan(scriniaDir);

        if (plan.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to migrate — no v1 files found.[/]");
            return Task.FromResult(0);
        }

        // ── Execute or report ───────────────────────────────────────────
        int migrated = 0;
        int skipped = 0;
        int errors = 0;

        if (dryRun)
            AnsiConsole.MarkupLine($"[bold][DRY RUN][/] Would migrate {plan.Count} files:");

        foreach (var (source, target) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relSource = Path.GetRelativePath(scriniaDir, source).Replace('\\', '/');
            string relTarget = Path.GetRelativePath(scriniaDir, target).Replace('\\', '/');

            if (File.Exists(target))
            {
                if (!dryRun)
                    skipped++;
                else
                    AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(relSource)} → {Markup.Escape(relTarget)} (skip, exists)[/]");
                continue;
            }

            if (dryRun)
            {
                AnsiConsole.MarkupLine($"  {Markup.Escape(relSource)} → {Markup.Escape(relTarget)}");
                continue;
            }

            try
            {
                string? targetDir = Path.GetDirectoryName(target);
                if (targetDir is not null)
                    Directory.CreateDirectory(targetDir);
                File.Copy(source, target);
                migrated++;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error copying {Markup.Escape(relSource)}: {Markup.Escape(ex.Message)}[/]");
                errors++;
            }
        }

        if (!dryRun)
        {
            AnsiConsole.MarkupLine($"[green]Migrated {migrated} files to v2 path structure.[/]");
            if (skipped > 0)
                AnsiConsole.MarkupLine($"[dim]Skipped {skipped} files (already exist at target).[/]");
            if (errors > 0)
                AnsiConsole.MarkupLine($"[red]{errors} errors during migration.[/]");
            AnsiConsole.MarkupLine("[dim]Original files preserved in topics/ for fallback.[/]");
            AnsiConsole.MarkupLine("[dim]Run 'scri migrate_v1 --cleanup' to remove originals after verifying.[/]");
        }

        return Task.FromResult(errors > 0 ? 1 : 0);
    }

    private static List<(string Source, string Target)> BuildMigrationPlan(string scriniaDir)
    {
        var plan = new List<(string Source, string Target)>();
        string memoriesDir = Path.Combine(scriniaDir, "memories");

        // 1. Scan .scrinia/topics/ for .nmp2 and .meta.json files
        string topicsDir = Path.Combine(scriniaDir, "topics");
        if (Directory.Exists(topicsDir))
        {
            foreach (string file in Directory.EnumerateFiles(topicsDir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".nmp2", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip version files — they live in topic/versions/ subdirs
                string relPath = Path.GetRelativePath(topicsDir, file).Replace('\\', '/');
                if (relPath.Contains("/versions/", StringComparison.OrdinalIgnoreCase))
                    continue;

                string target = MapTopicFileToV2(relPath, memoriesDir);
                plan.Add((file, target));
            }
        }

        // 2. Scan .scrinia/agent/ for markdown files → memories/agent/
        string agentDir = Path.Combine(scriniaDir, "agent");
        if (Directory.Exists(agentDir))
        {
            foreach (string file in Directory.EnumerateFiles(agentDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(agentDir, file).Replace('\\', '/');
                string target = Path.Combine(memoriesDir, "agent", relPath.Replace('/', Path.DirectorySeparatorChar));
                plan.Add((file, target));
            }
        }

        // 3. Scan .scrinia/skills/ → memories/skill/
        string skillsDir = Path.Combine(scriniaDir, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (string file in Directory.EnumerateFiles(skillsDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(skillsDir, file).Replace('\\', '/');
                string target = Path.Combine(memoriesDir, "skill", relPath.Replace('/', Path.DirectorySeparatorChar));
                plan.Add((file, target));
            }
        }

        // 4. Scan .scrinia/workflows/ → memories/workflow/
        string workflowsDir = Path.Combine(scriniaDir, "workflows");
        if (Directory.Exists(workflowsDir))
        {
            foreach (string file in Directory.EnumerateFiles(workflowsDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(workflowsDir, file).Replace('\\', '/');
                string target = Path.Combine(memoriesDir, "workflow", relPath.Replace('/', Path.DirectorySeparatorChar));
                plan.Add((file, target));
            }
        }

        return plan;
    }

    /// <summary>
    /// Maps a relative path under topics/ to its v2 target under memories/.
    /// Handles the three v1 layouts:
    ///   entity/{topic}/{file} → {topic}/{file}     (strip entity/ prefix)
    ///   memory/{topic}/{file} → {topic}/{file}     (strip memory/ prefix)
    ///   agent/{file}          → agent/{file}        (keep as-is)
    ///   {topic}/{file}        → {topic}/{file}      (flat topic, keep as-is)
    /// </summary>
    private static string MapTopicFileToV2(string relPath, string memoriesDir)
    {
        // relPath uses forward slashes, e.g. "entity/goal/G-5.nmp2" or "arch/overview.nmp2"
        string[] parts = relPath.Split('/');

        string mappedRelPath;
        if (parts.Length >= 3 &&
            parts[0].Equals("entity", StringComparison.OrdinalIgnoreCase))
        {
            // entity/goal/G-5.nmp2 → goal/G-5.nmp2
            mappedRelPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts[1..]);
        }
        else if (parts.Length >= 3 &&
                 parts[0].Equals("memory", StringComparison.OrdinalIgnoreCase))
        {
            // memory/api/auth-flow.nmp2 → api/auth-flow.nmp2
            mappedRelPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts[1..]);
        }
        else
        {
            // agent/profile.nmp2 → agent/profile.nmp2
            // arch/overview.nmp2 → arch/overview.nmp2
            mappedRelPath = relPath.Replace('/', Path.DirectorySeparatorChar);
        }

        return Path.Combine(memoriesDir, mappedRelPath);
    }

    private static Task<int> RunCleanup(string scriniaDir, bool dryRun)
    {
        string topicsDir = Path.Combine(scriniaDir, "topics");
        string memoriesDir = Path.Combine(scriniaDir, "memories");

        if (!Directory.Exists(memoriesDir))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No memories/ directory found. Run 'scri migrate_v1' first.");
            return Task.FromResult(1);
        }

        if (!Directory.Exists(topicsDir))
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to clean up — topics/ directory does not exist.[/]");
            return Task.FromResult(0);
        }

        // Only remove files from topics/ that have a corresponding file in memories/
        var plan = BuildMigrationPlan(scriniaDir);
        int removed = 0;
        int kept = 0;

        foreach (var (source, target) in plan)
        {
            if (!File.Exists(target))
            {
                kept++;
                continue;
            }

            string relSource = Path.GetRelativePath(scriniaDir, source).Replace('\\', '/');

            if (dryRun)
            {
                AnsiConsole.MarkupLine($"  [red]Would remove[/] {Markup.Escape(relSource)}");
            }
            else
            {
                File.Delete(source);
                removed++;
            }
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine($"[bold][DRY RUN][/] Would remove {plan.Count - kept} v1 files.");
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]Removed {removed} v1 files.[/]");
            if (kept > 0)
                AnsiConsole.MarkupLine($"[dim]Kept {kept} files (no v2 counterpart found).[/]");

            // Clean up empty directories in topics/
            CleanEmptyDirectories(topicsDir);
        }

        return Task.FromResult(0);
    }

    /// <summary>
    /// Copies .scrinia/ to a timestamped sibling backup directory before a migration writes to
    /// it. Shared by <see cref="MigrateV1"/> (layout migration) and <see cref="Migrate"/>
    /// (attributes migration) — no-op if .scrinia/ doesn't exist.
    /// </summary>
    private static void BackupScriniaDir(string root)
    {
        string scriniaDir = Path.Combine(root, ".scrinia");
        if (!Directory.Exists(scriniaDir))
            return;

        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string backupDir = Path.Combine(root, $".scrinia-backup-{timestamp}");
        AnsiConsole.MarkupLine($"[dim]Backing up .scrinia/ to {Markup.Escape(Path.GetFileName(backupDir))}...[/]");
        CopyDirectoryRecursive(scriniaDir, backupDir);
        AnsiConsole.MarkupLine("[green]Backup complete.[/]");
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
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
            CopyDirectoryRecursive(dir, destDir);
        }
    }

    private static void CleanEmptyDirectories(string dir)
    {
        foreach (string subDir in Directory.GetDirectories(dir))
        {
            CleanEmptyDirectories(subDir);
        }

        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            try { Directory.Delete(dir); } catch { /* ignore */ }
        }
    }

    /// <summary>Run deterministic consolidation passes against the local store. Mechanical only — no LLM calls.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="auto">When set, skip if .scrinia/.last-consolidation indicates a recent run (debounce). Intended for hook-driven invocation.</param>
    /// <param name="dryRun">Report what would change without modifying anything.</param>
    /// <param name="debounceMinutes">Minimum minutes between auto runs. Prevents wasted work when invoked repeatedly.</param>
    /// <param name="sessionAgeDays">Compact multi-chunk session entries older than N days (preserves content, drops chunk granularity).</param>
    /// <param name="json">Output as JSON instead of a styled table.</param>
    public async Task<int> Consolidate(
        string? workspaceRoot = null,
        bool auto = false,
        bool dryRun = false,
        int debounceMinutes = 30,
        int sessionAgeDays = 7,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        string scriniaDir = Path.Combine(ScriniaArtifactStore.WorkspaceRootPath, ".scrinia");
        string debounceFile = Path.Combine(scriniaDir, ".last-consolidation");

        // Hook-friendly debounce: hooks may fire on every event, but consolidation only earns
        // its keep if there's been new activity. The debounce file lets us cheaply no-op when
        // the last run is recent. Corrupt or missing file → proceed (fail-open).
        if (auto && File.Exists(debounceFile))
        {
            try
            {
                string lastRunStr = File.ReadAllText(debounceFile).Trim();
                if (DateTimeOffset.TryParse(lastRunStr, out var lastRun))
                {
                    double minutesSince = (DateTimeOffset.UtcNow - lastRun).TotalMinutes;
                    if (minutesSince < debounceMinutes)
                    {
                        string reason = $"Last run {minutesSince:F1}m ago (debounce {debounceMinutes}m)";
                        if (json)
                            WriteJson(
                                new CliConsolidateOutput("skipped", reason, 0, 0, 0, 0, []),
                                CliJsonContext.Default.CliConsolidateOutput);
                        else
                            AnsiConsole.MarkupLine($"[dim]consolidate: skipped — {reason}[/]");
                        return 0;
                    }
                }
            }
            catch { /* corrupt debounce file → proceed */ }
        }

        var allEntries = ScriniaArtifactStore.ListScoped(null);
        long totalBytes = allEntries.Sum(e => e.Entry.OriginalBytes);
        var now = DateTimeOffset.UtcNow;
        int staleCount = allEntries.Count(e =>
            AttributeReaders.GetReviewStatus(e.Entry.Attributes, now).IsStale);

        // Compact old multi-chunk session entries. Sessions are append-only logs that accumulate
        // chunks across a day's work; once a session is past its useful window, chunk-level
        // search granularity stops earning its sidecar overhead. Single-chunk artifacts still
        // search fine via entry-level keywords and TF.
        var ageThreshold = DateTimeOffset.UtcNow.AddDays(-sessionAgeDays);
        var compactionCandidates = allEntries
            .Where(e => IsSessionScope(e.Scope))
            .Where(e => e.Entry.ChunkCount > 1)
            .Where(e => (e.Entry.UpdatedAt ?? e.Entry.CreatedAt) <= ageThreshold)
            .ToList();

        var compacted = new List<string>();
        if (!dryRun)
        {
            // Wire the embedding pipeline before compacting so the merged single-chunk entry
            // can be re-embedded through the same event-sink path a normal store/append uses —
            // otherwise vectors stay keyed to the pre-compaction chunk layout (stale/orphaned).
            IMemoryStore? store = null;
            if (compactionCandidates.Count > 0)
            {
                await WorkspaceSetup.LoadPluginsAsync(cancellationToken);
                store = MemoryStoreContext.Current;
            }

            foreach (var item in compactionCandidates)
            {
                try
                {
                    await CompactEntryToSingleChunk(item, store, cancellationToken);
                    compacted.Add(ScriniaArtifactStore.FormatQualifiedName(item.Scope, item.Entry.Name));
                }
                catch (Exception ex)
                {
                    if (!json)
                        AnsiConsole.MarkupLine($"[red]compact failed for {Markup.Escape(item.Entry.Name)}: {Markup.Escape(ex.Message)}[/]");
                }
            }

            Directory.CreateDirectory(scriniaDir);
            File.WriteAllText(debounceFile, DateTimeOffset.UtcNow.ToString("o"));
        }

        if (json)
        {
            WriteJson(
                new CliConsolidateOutput(
                    Status: dryRun ? "preview" : "completed",
                    Reason: null,
                    TotalMemories: allEntries.Count,
                    TotalBytes: totalBytes,
                    StaleCount: staleCount,
                    CompactionCandidates: compactionCandidates.Count,
                    Compacted: compacted.ToArray()),
                CliJsonContext.Default.CliConsolidateOutput);
            return 0;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[bold]consolidate{(dryRun ? " (dry-run)" : "")}[/]");
        sb.AppendLine($"[blue]{allEntries.Count} memories[/] — {ScriniaMcpTools.FormatBytes(totalBytes)}");
        if (staleCount > 0)
            sb.AppendLine($"[yellow]{staleCount} entries past reviewAfter[/] — review with [italic]scrinia list[/]");
        if (compactionCandidates.Count > 0)
        {
            string verb = dryRun ? "would compact" : "compacted";
            sb.AppendLine($"[green]{verb} {compactionCandidates.Count} multi-chunk session{(compactionCandidates.Count == 1 ? "" : "s")} older than {sessionAgeDays}d[/]");
            foreach (var name in (dryRun ? compactionCandidates.Select(c => ScriniaArtifactStore.FormatQualifiedName(c.Scope, c.Entry.Name)) : compacted).Take(10))
                sb.AppendLine($"  [dim]•[/] {Markup.Escape(name)}");
            int total = dryRun ? compactionCandidates.Count : compacted.Count;
            if (total > 10)
                sb.AppendLine($"  [dim]… and {total - 10} more[/]");
        }
        else
        {
            sb.AppendLine("[dim]Nothing to consolidate.[/]");
        }
        AnsiConsole.Write(new Markup(sb.ToString()));
        return 0;
    }

    /// <summary>True when the scope is the session-log scope under any namespacing variant.</summary>
    private static bool IsSessionScope(string scope)
    {
        if (!scope.StartsWith("local:", StringComparison.Ordinal)) return false;
        string topicPart = scope["local:".Length..];
        return topicPart.Equals("sessions", StringComparison.OrdinalIgnoreCase)
            || topicPart.Equals("memory/sessions", StringComparison.OrdinalIgnoreCase)
            || topicPart.EndsWith("/sessions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decodes a multi-chunk artifact and re-encodes it as a single chunk. Archives the original
    /// so the prior chunked form remains recoverable. Updates the sidecar to reflect the new
    /// chunk count and clears per-chunk metadata (which is no longer accurate after merge).
    /// </summary>
    /// <param name="item">The multi-chunk entry to compact.</param>
    /// <param name="store">Active memory store (wired by the caller via <c>WorkspaceSetup.LoadPluginsAsync</c>),
    /// used to re-embed the merged entry. Null skips re-embedding (e.g. workspace not configured).</param>
    /// <param name="ct">Cancellation token for the re-embed call.</param>
    /// <remarks>Internal (rather than private) so tests can exercise the re-embed wiring directly
    /// without going through <c>WorkspaceSetup.LoadPluginsAsync</c>'s real provider discovery,
    /// which is environment-dependent (model files may or may not be present on disk).</remarks>
    internal static async Task CompactEntryToSingleChunk(ScopedArtifact item, IMemoryStore? store, CancellationToken ct)
    {
        string path = ScriniaArtifactStore.FindArtifactPath(item.Entry.Name, item.Scope);
        if (!File.Exists(path)) return;

        string artifact = File.ReadAllText(path);
        if (Nmp2ChunkedEncoder.GetChunkCount(artifact) <= 1) return;

        ScriniaArtifactStore.ArchiveVersion(item.Entry.Name, item.Scope);

        byte[] allBytes = Nmp2Strategy.Instance.Decode(artifact);
        string fullText = System.Text.Encoding.UTF8.GetString(allBytes);
        string compacted = Nmp2ChunkedEncoder.Encode(fullText);
        File.WriteAllText(path, compacted);

        var updated = item.Entry with
        {
            ChunkCount = 1,
            OriginalBytes = allBytes.LongLength,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChunkEntries = null,
        };
        ScriniaArtifactStore.Upsert(updated, item.Scope);

        // Merging chunks collapses the chunk layout (e.g. indices 0-7 -> just 0), so any
        // vectors keyed to the old per-chunk indices are now orphaned/stale. Re-embed through
        // the same event-sink path a normal store/append uses (CoreEmbeddingEventHandler.
        // EmbedAndIndexAsync does an atomic Remove-then-Upsert per entry) so stale higher-index
        // vectors are cleared and the merged single chunk gets a fresh vector. If no sink is
        // wired (embeddings unavailable in this process), there's nothing to clear or rebuild —
        // a subsequent `scri reindex` will repopulate.
        var sink = MemoryEventSinkContext.Current;
        if (sink is not null && store is not null)
        {
            try
            {
                // FormatRoundTripQualifiedName (not FormatQualifiedName) — the sink re-parses
                // this string to decide which scope to re-embed under, and FormatQualifiedName's
                // display form strips the "memory/"/"entity/" namespace prefix, which would
                // reparse to the WRONG scope for any v1-namespaced topic.
                string qualifiedName = store.FormatRoundTripQualifiedName(item.Scope, item.Entry.Name);
                await sink.OnStoredAsync(qualifiedName, [fullText], store, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[scrinia:warn] Re-embed after compaction failed for '{item.Entry.Name}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Migrate stored memories to the attributes model (forward-fill: tags, codeRefs,
    /// review, and anchors become attributes/keywords). Additive and idempotent — safe to re-run.</summary>
    /// <param name="workspaceRoot">Workspace root. Defaults to the nearest ancestor with a .scrinia/ dir.</param>
    /// <param name="dryRun">Report what would change without writing.</param>
    /// <param name="skipBackup">Skip the timestamped .scrinia/ backup (backup runs by default).</param>
    /// <param name="json">Emit a machine-readable summary.</param>
    public Task<int> Migrate(
        string? workspaceRoot = null, bool dryRun = false, bool skipBackup = false, bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        if (MemoryStoreContext.Current is not FileMemoryStore store)
            throw new InvalidOperationException("Migrate requires a file-backed workspace store.");

        if (!skipBackup && !dryRun)
            BackupScriniaDir(ScriniaArtifactStore.WorkspaceRootPath);

        int lastDone = -1;
        var summary = AttributeMigration.MigrateStore(store, dryRun, (done, total) =>
        {
            if (done == lastDone) return;
            lastDone = done;
            Console.Error.Write($"\r[scrinia] migrating {done}/{total}…");
            if (done == total) Console.Error.WriteLine();
        });

        if (json)
        {
            WriteJson(
                new CliMigrateOutput(summary.Total, summary.Migrated, summary.Skipped, dryRun),
                CliJsonContext.Default.CliMigrateOutput);
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[bold]migrate{(dryRun ? " (dry-run)" : "")}[/] — {summary.Migrated} of {summary.Total} memories forward-filled ({summary.Skipped} already current).");
        }

        return Task.FromResult(0);
    }
}
