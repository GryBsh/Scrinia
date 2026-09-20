using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Search;
using Scrinia.Services;

namespace Scrinia.Commands;

internal static class WorkspaceSetup
{
    private static readonly AsyncLocal<string?> _rootOverride = new();
    private static string? _root;

    /// <summary>
    /// The resolved workspace root for this process, cached by <see cref="Configure"/>.
    /// Respects test overrides set via <see cref="OverrideRoot"/> (checked first, via
    /// <see cref="AsyncLocal{T}"/> so parallel test classes don't clobber each other),
    /// falling back to the process-wide value <see cref="Configure"/> cached.
    /// Replaces the former static workspace-root accessor.
    /// </summary>
    internal static string Root =>
        _rootOverride.Value ?? _root ?? throw new InvalidOperationException(
            "WorkspaceSetup.Configure must be called before reading Root.");

    /// <summary>Sets an async-local override for <see cref="Root"/>, scoped to the current
    /// async context. For tests that need root isolation under parallel execution; not
    /// wired into any production path.</summary>
    internal static void OverrideRoot(string? path) => _rootOverride.Value = path;

    /// <summary>
    /// The configured store for this process. Throws if <see cref="Configure"/> has not run.
    /// Resolves via <see cref="MemoryStoreContext.Current"/> ?? <see cref="MemoryStoreContext.Default"/>
    /// so the generic-host thread boundary does not lose the store.
    /// </summary>
    internal static IMemoryStore Store =>
        MemoryStoreContext.Current ?? MemoryStoreContext.Default ?? throw new InvalidOperationException(
            "WorkspaceSetup.Configure must be called before reading Store.");

    private static MaintenanceScheduler? _maintenanceScheduler;

    /// <summary>
    /// The active bounded background maintenance adapter, if one was started
    /// for this process. Owned by <see cref="StartMaintenanceScheduler"/> and
    /// <see cref="StopMaintenanceSchedulerAsync"/>; the single global
    /// background-work drain stays in <c>Program</c>.
    /// </summary>
    internal static MaintenanceScheduler? ActiveMaintenanceScheduler => _maintenanceScheduler;

    /// <summary>
    /// Starts the B5 bounded background adapter: one single-flight
    /// <see cref="MaintenanceScheduler"/> over the configured file store that
    /// fires at most one Core maintenance invocation via
    /// <c>BackgroundWork.Fire</c>. Returns <c>null</c> when no file-backed
    /// store is configured. A background-disabled or invalid policy schedules
    /// zero work but still returns the scheduler so shutdown stays ordered.
    /// </summary>
    internal static MaintenanceScheduler? StartMaintenanceScheduler()
    {
        if (MemoryStoreContext.Current is not FileMemoryStore fileStore &&
            MemoryStoreContext.Default is not FileMemoryStore)
            return null;
        FileMemoryStore store = MemoryStoreContext.Current as FileMemoryStore
            ?? (FileMemoryStore)MemoryStoreContext.Default!;
        var scheduler = new MaintenanceScheduler(
            store, Core.Configuration.WorkspaceConfig.Load(Root));
        _maintenanceScheduler = scheduler;
        scheduler.TrySchedule();
        return scheduler;
    }

    /// <summary>
    /// Stops the active background adapter inner-first (close admission,
    /// cancel and await its active invocation), then disposes it. Never
    /// drains unrelated global work — the outer global drain stays in
    /// <c>Program</c>.
    /// </summary>
    internal static async Task StopMaintenanceSchedulerAsync()
    {
        MaintenanceScheduler? scheduler = _maintenanceScheduler;
        _maintenanceScheduler = null;
        if (scheduler is null)
            return;
        await scheduler.StopAsync().ConfigureAwait(false);
        scheduler.Dispose();
    }

    internal static void Configure(string? workspaceRoot)
        => Configure(workspaceRoot, null);

    internal static void Configure(string? workspaceRoot, IMultiMemoryParticipantAdapterFactory? participantAdapterFactory)
    {
        string root = WorkspaceRoot.Resolve(workspaceRoot, Directory.GetCurrentDirectory());
        _root = root;

        // Build ranker weights from config so a workspace can tune α_recency without
        // recompiling. Defaults (RankerOptions.Default) match the historical composition —
        // see RankerOptions docstring for the formula and calibration notes.
        var rankerOptions = RankerOptions.FromConfig(GetConfigValue);
        var store = StoreFactory.Create(root, rankerOptions, participantAdapterFactory);
        MemoryStoreContext.Current = store;
        MemoryStoreContext.Default = store;
    }

    /// <summary>
    /// Builds the message shown when a store exists somewhere resolution deliberately did not
    /// look. Separated from the write so the wording is testable without capturing the console.
    /// </summary>
    internal static string FormatOrphanWarning(string orphan, string root, IReadOnlyList<string> topics)
    {
        string orphanParent = Path.GetDirectoryName(orphan) ?? orphan;
        if (topics.Count > 0)
        {
            string topicList = string.Join(",", topics);
            return $"""
                scrinia: ignoring store at {orphan}
                         using {root}

                to migrate its memories:
                  scri bundle export {topicList} --workspace-root "{orphanParent}"
                  scri bundle import <file> --workspace-root "{root}"
                """;
        }

        // No topic list could be produced — either the store is empty, or it contains a
        // layout `scri bundle export` cannot target with a topic list (e.g. the v2
        // .scrinia/memories/ hierarchy). Guessing a list here would either be wrong or
        // silently incomplete, so point at `scri list` instead of printing one.
        return $"""
            scrinia: ignoring store at {orphan}
                     using {root}

            to migrate its memories, first see what's there:
              scri list --workspace-root "{orphanParent}"
            then:
              scri bundle export <topics> --workspace-root "{orphanParent}"
              scri bundle import <file> --workspace-root "{root}"
            """;
    }

    /// <summary>
    /// Reports stores that root resolution passed over. Called only from commands that already
    /// do real work — never from the per-prompt hint path.
    /// </summary>
    internal static void WarnOnOrphanStores()
    {
        try
        {
            string root = Root;
            string cwd = Directory.GetCurrentDirectory();
            foreach (string orphan in WorkspaceRoot.FindOrphanStores(root, cwd))
                Console.Error.WriteLine(FormatOrphanWarning(orphan, root, ListTopics(orphan)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Reporting an ignored store is advisory; a filesystem it cannot read (or a path
            // FindOrphanStores rejects via Path.GetFullPath/ArgumentException.ThrowIfNullOrWhiteSpace)
            // must not fail the caller's write.
        }
    }

    /// <summary>
    /// Namespace directory names used by the OLD (pre-v3) <c>.scrinia/topics/</c> layout that
    /// <see cref="ListTopics"/> below still has to interpret. Intentionally NOT
    /// <c>MemoryNaming</c> — that type dropped the "reserved namespace directory" concept
    /// entirely in Task 8, since <see cref="Scrinia.Core.FileMemoryStore"/> itself no longer
    /// reads through it. This scan targets an arbitrary ORPHANED/ignored store elsewhere on
    /// disk that this workstream's automatic migration has never touched (it may still be
    /// sitting in the pre-v3 layout), so the classification still needs to exist somewhere.
    /// </summary>
    private static readonly HashSet<string> s_legacyNamespaceDirs =
        new(StringComparer.OrdinalIgnoreCase) { "entity", "memory", "agent" };

    /// <summary>
    /// Topic names inside an orphaned store, for the migration command — the bare names
    /// <c>scri bundle export</c> actually accepts, not raw directory names. Descends into the
    /// <c>entity/</c> and <c>memory/</c> namespace directories for their real child topics
    /// instead of reporting the namespace directory itself as a topic (it isn't one).
    /// </summary>
    internal static IReadOnlyList<string> ListTopics(string storeDir)
    {
        try
        {
            // The v2 hierarchical layout (.scrinia/memories/<segment>/...) has no flat
            // topic-name equivalent that `scri bundle export --topics` can target. Reporting
            // only the v1 topics here would look like a complete migration command while
            // silently leaving v2 memories behind, so when v2 content exists, report no
            // topics at all — the caller falls back to `scri list` instead of a confidently
            // incomplete command.
            string memoriesDir = Path.Combine(storeDir, "memories");
            if (Directory.Exists(memoriesDir) && Directory.EnumerateFileSystemEntries(memoriesDir).Any())
                return [];

            string topicsDir = Path.Combine(storeDir, "topics");
            if (!Directory.Exists(topicsDir)) return [];

            var topics = new List<string>();
            var namespacedChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string nsDir in Directory.EnumerateDirectories(topicsDir))
            {
                string nsName = Path.GetFileName(nsDir) ?? "";
                if (!s_legacyNamespaceDirs.Contains(nsName)) continue;

                if (nsName.Equals("agent", StringComparison.OrdinalIgnoreCase))
                {
                    // agent is a single scope with no child topic directories.
                    topics.Add("agent");
                    continue;
                }

                foreach (string childDir in Directory.EnumerateDirectories(nsDir))
                {
                    string childName = Path.GetFileName(childDir) ?? "";
                    topics.Add(childName);
                    namespacedChildren.Add(childName);
                }
            }

            foreach (string dir in Directory.EnumerateDirectories(topicsDir))
            {
                string name = Path.GetFileName(dir) ?? "";
                if (s_legacyNamespaceDirs.Contains(name)) continue;
                if (namespacedChildren.Contains(name)) continue; // shadowed by a namespaced entry
                topics.Add(name);
            }

            return [.. topics.Distinct(StringComparer.OrdinalIgnoreCase).Order()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Resolves a plugin executable name, falling back to <paramref name="defaultName"/>.
    ///
    /// <para>The resolution order — both environment forms, then the workspace config file —
    /// lives in <see cref="GetConfigValue"/> and is not repeated here. This method used to carry
    /// its own copy, and when the double-underscore lookup was repaired the fix had to be applied
    /// to both; a rule with two homes is the shape that let the broken form ship documented in the
    /// first place.</para>
    /// </summary>
    internal static string GetPluginName(string key, string defaultName) =>
        GetConfigValue(key) ?? defaultName;

    /// <summary>
    /// Reads config from environment variables, then falls back to the workspace config file.
    /// A colon-separated key (e.g. "Scrinia:Embeddings:Provider") is accepted in two environment
    /// forms: single-underscore (SCRINIA_EMBEDDINGS_PROVIDER), which is the historically documented
    /// one and keeps priority, and the .NET-standard double-underscore form
    /// (SCRINIA__EMBEDDINGS__PROVIDER), which is what a .NET configuration host would read
    /// natively and is accepted here so the two spellings never disagree.
    /// </summary>
    internal static string? GetConfigValue(string key) => GetConfigValue(key, null);

    internal static string? GetConfigValue(string key, string? defaultValue)
    {
        if (key == "Scrinia:Shared:Enabled")
            return WorkspaceConfig.GetValue(Root, key) ?? defaultValue;

        // 1. Environment variable, single-underscore form (highest priority)
        string envKey = key.Replace(':', '_').Replace("__", "_").ToUpperInvariant();
        string? value = Environment.GetEnvironmentVariable(envKey);
        if (value is not null) return value;

        // 2. Environment variable, .NET-standard double-underscore form. This branch used to
        //    compute a string byte-identical to the one above for every key in the repository, so
        //    the documented form never resolved.
        envKey = key.Replace(":", "__").ToUpperInvariant();
        value = Environment.GetEnvironmentVariable(envKey);
        if (value is not null) return value;

        // 3. Config file (workspace-scoped)
        return WorkspaceConfig.GetValue(Root, key) ?? defaultValue;
    }
}
