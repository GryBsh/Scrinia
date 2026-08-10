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
    /// <summary>
    /// Migrates a workspace store to the v3 layout, where every memory lives under
    /// .scrinia/memories/. Runs automatically on first store access; this command exists
    /// to preview the plan, migrate deliberately, or re-run after restoring a backup.
    /// </summary>
    /// <param name="workspace">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="dryRun">Print how many files would move without moving any.</param>
    /// <param name="backup">Copy .scrinia/ to .scrinia-backup-{timestamp}/ before migrating.</param>
    public Task<int> Migrate(
        string? workspace = null,
        bool dryRun = false,
        bool backup = true,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspace);
        string root = WorkspaceSetup.Root;

        if (!Directory.Exists(Path.Combine(root, ".scrinia")))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No .scrinia/ directory found.");
            return Task.FromResult(1);
        }

        var result = StoreLayoutMigration.Migrate(root, dryRun, backup);

        if (!result.Ran && result.Moved == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to migrate — the store is already v3.[/]");
            return Task.FromResult(0);
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine($"[bold][[DRY RUN]][/] Would move {result.Moved} file(s).");
            if (result.Conflicts > 0)
                AnsiConsole.MarkupLine($"[yellow]{result.Conflicts} collision(s) would be quarantined.[/]");
            return Task.FromResult(0);
        }

        AnsiConsole.MarkupLine($"[green]Migrated {result.Moved} file(s) to the v3 layout.[/]");
        if (result.Conflicts > 0)
            AnsiConsole.MarkupLine(
                $"[yellow]{result.Conflicts} collision(s) quarantined — non-namespaced entries won.[/]");
        if (result.BackupPath is not null)
            AnsiConsole.MarkupLine($"[dim]Backup: {Markup.Escape(result.BackupPath)}[/]");
        else if (result.BackupSkipped)
            AnsiConsole.MarkupLine(
                "[yellow]No backup was written — a prior .scrinia-backup-*/ already exists at the workspace root.[/]");
        return Task.FromResult(0);
    }

    /// <summary>
    /// Copies .scrinia/ to a timestamped sibling backup directory before a migration writes to
    /// it. Used by <see cref="MigrateAttributes"/> — no-op if .scrinia/ doesn't exist.
    ///
    /// <para>Named <c>.scrinia-backup-attrs-{ts}</c> — deliberately distinct from
    /// <see cref="StoreLayoutMigration"/>'s own <c>.scrinia-backup-{ts}</c> (no <c>attrs-</c>
    /// segment). The two used to share the same name shape, so <c>StoreLayoutMigration</c>'s
    /// "skip the backup if one already exists" guard would treat a backup this method wrote as
    /// its own and silently skip writing a real one on the next store access — see
    /// <c>StoreLayoutMigration.IsOwnBackupDirName</c>.</para>
    /// </summary>
    private static void BackupScriniaDir(string root)
    {
        string scriniaDir = Path.Combine(root, ".scrinia");
        if (!Directory.Exists(scriniaDir))
            return;

        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string backupDir = Path.Combine(root, $".scrinia-backup-attrs-{timestamp}");
        AnsiConsole.MarkupLine($"[dim]Backing up .scrinia/ to {Markup.Escape(Path.GetFileName(backupDir))}...[/]");
        CopyDirectoryRecursive(scriniaDir, backupDir);
        AnsiConsole.MarkupLine("[green]Backup complete.[/]");
    }

    /// <summary>Recursive file copy for <see cref="BackupScriniaDir"/>. Best-effort per file,
    /// matching <c>StoreLayoutMigration.CopyDirectory</c>'s handling of the same class of
    /// problem: a file held open by a concurrent process (an in-use scope <c>.lock</c>, an
    /// antivirus scan, a sync client such as Synology Drive) is skipped rather than aborting the
    /// whole backup — a live lock file carries no committed data worth preserving anyway, and
    /// <c>File.Copy</c>'s bare (unguarded) call used to let that abort
    /// <c>scri migrate_attributes --backup</c> entirely on a single transiently-locked file.
    /// Catches <see cref="UnauthorizedAccessException"/> alongside <see cref="IOException"/> — on
    /// Windows a sharing violation can surface as either.</summary>
    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
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
            CopyDirectoryRecursive(dir, destDir);
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

        string scriniaDir = Path.Combine(WorkspaceSetup.Root, ".scrinia");
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

        var allEntries = WorkspaceSetup.Store.ListScoped(null);
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
            ILocalMemoryStore store = WorkspaceSetup.LocalStore;
            if (compactionCandidates.Count > 0)
                await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

            foreach (var item in compactionCandidates)
            {
                try
                {
                    await CompactEntryToSingleChunk(item, store, cancellationToken);
                    compacted.Add(WorkspaceSetup.Store.FormatQualifiedName(item.Scope, item.Entry.Name));
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
            foreach (var name in (dryRun ? compactionCandidates.Select(c => WorkspaceSetup.Store.FormatQualifiedName(c.Scope, c.Entry.Name)) : compacted).Take(10))
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
    /// <param name="store">Active memory store, used both to read/write the artifact through its
    /// per-scope write lock and to re-embed the merged entry.</param>
    /// <param name="ct">Cancellation token for the re-embed call.</param>
    /// <remarks>Internal (rather than private) so tests can exercise the re-embed wiring directly
    /// without going through <c>WorkspaceSetup.LoadPluginsAsync</c>'s real provider discovery,
    /// which is environment-dependent (model files may or may not be present on disk).</remarks>
    internal static async Task CompactEntryToSingleChunk(ScopedArtifact item, ILocalMemoryStore store, CancellationToken ct)
    {
        string path = store.FindArtifactPath(item.Entry.Name, item.Scope);
        if (!File.Exists(path)) return;

        string artifact = await store.ReadArtifactAsync(item.Entry.Name, item.Scope, ct);
        if (Nmp2ChunkedEncoder.GetChunkCount(artifact) <= 1) return;

        store.ArchiveVersion(item.Entry.Name, item.Scope);

        byte[] allBytes = Nmp2Strategy.Instance.Decode(artifact);
        string fullText = System.Text.Encoding.UTF8.GetString(allBytes);
        string compacted = Nmp2ChunkedEncoder.Encode(fullText);
        await store.WriteArtifactAsync(item.Entry.Name, item.Scope, compacted, ct: ct);

        var updated = item.Entry with
        {
            ChunkCount = 1,
            OriginalBytes = allBytes.LongLength,
            UpdatedAt = DateTimeOffset.UtcNow,
            ChunkEntries = null,
        };
        store.Upsert(updated, item.Scope);

        // Merging chunks collapses the chunk layout (e.g. indices 0-7 -> just 0), so any
        // vectors keyed to the old per-chunk indices are now orphaned/stale. Re-embed through
        // the same event-sink path a normal store/append uses (CoreEmbeddingEventHandler.
        // EmbedAndIndexAsync does an atomic Remove-then-Upsert per entry) so stale higher-index
        // vectors are cleared and the merged single chunk gets a fresh vector. If no sink is
        // wired (embeddings unavailable in this process), there's nothing to clear or rebuild —
        // a subsequent `scri reindex` will repopulate.
        var sink = MemoryEventSinkContext.Current;
        if (sink is not null)
        {
            try
            {
                string qualifiedName = store.FormatQualifiedName(item.Scope, item.Entry.Name);
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
    /// review, and anchors become attributes/keywords). Additive and idempotent — safe to re-run.
    /// Named <c>migrate_attributes</c> on the CLI — unrelated to <see cref="Migrate"/>, which
    /// migrates the on-disk store layout and owns the plain <c>migrate</c> command name.</summary>
    /// <param name="workspaceRoot">Workspace root. Defaults to the nearest ancestor with a .scrinia/ dir.</param>
    /// <param name="dryRun">Report what would change without writing.</param>
    /// <param name="skipBackup">Skip the timestamped .scrinia/ backup (backup runs by default).</param>
    /// <param name="json">Emit a machine-readable summary.</param>
    [Command("migrate_attributes")]
    public Task<int> MigrateAttributes(
        string? workspaceRoot = null, bool dryRun = false, bool skipBackup = false, bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        if (MemoryStoreContext.Current is not FileMemoryStore store)
            throw new InvalidOperationException("MigrateAttributes requires a file-backed workspace store.");

        if (!skipBackup && !dryRun)
            BackupScriniaDir(WorkspaceSetup.Root);

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
                $"[bold]migrate_attributes{(dryRun ? " (dry-run)" : "")}[/] — {summary.Migrated} of {summary.Total} memories forward-filled ({summary.Skipped} already current).");
        }

        return Task.FromResult(0);
    }
}
