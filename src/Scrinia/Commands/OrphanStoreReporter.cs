using Scrinia.Core;

namespace Scrinia.Commands;

/// <summary>
/// Reports workspace stores that root resolution passed over. Extracted from the former
/// <c>WorkspaceSetup</c> composition root so that type owns only workspace identity.
/// </summary>
internal static class OrphanStoreReporter
{
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
    internal static void WarnOnOrphanStores(string workspaceRoot)
    {
        try
        {
            string root = workspaceRoot;
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
}
