using Scrinia.Core.Configuration;

namespace Scrinia.Core;

/// <summary>
/// Resolves filesystem locations under <c>.scrinia/</c>: hand-authored file entities (skills, agent
/// configs) to a top-level directory, and a store SCOPE to its directory under
/// <c>.scrinia/memories/</c>. Pure and side-effect-free.
/// <para>It takes strings, not <see cref="ParsedPath"/>. It used to say otherwise — a leftover from
/// when <c>ToFilesystemPath</c> and <c>IsEphemeral(ParsedPath)</c> lived here; no surviving member
/// takes one.</para>
/// </summary>
public static class PathRouter
{
    // ADJUST IF MERGER LATER — see CHANGELOG Breaking; sunset 2027-03-21 (180d from spec approval).
    internal static DateTimeOffset LocalTopicSunsetDate = new DateTimeOffset(2027, 3, 21, 0, 0, 0, TimeSpan.Zero);

    private const string MemoriesDir = "memories";

    /// <summary>
    /// Path segment name -> on-disk directory name for hand-authored file entities.
    /// <para>The singular/plural mismatch ("skill" -> "skills", "agent" -> "agent") is historical.
    /// It was previously implicit in five separate call sites; this table is the only place it
    /// exists.</para>
    /// <para>These directories sit at the top level of <c>.scrinia/</c>, deliberately NOT under
    /// <c>memories/</c>: their <c>.meta.json</c> sidecars share a filename namespace with the
    /// store's own <c>ArtifactEntry</c> sidecars, so a file entity inside a store scope directory
    /// would be destroyed by <see cref="FileMemoryStore.SaveIndex"/>'s orphan sweep.</para>
    /// </summary>
    private static readonly Dictionary<string, string> FileEntityDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skill"] = "skills",
        ["agent"] = "agent",
    };

    /// <summary>
    /// Resolves a file-entity kind to its directory. The single authority for where skills and
    /// agent configs live on disk.
    /// </summary>
    /// <exception cref="ArgumentException">The kind is not a known file entity. Callers pass a
    /// literal, never user input — an unknown kind is a programming error, matching
    /// <see cref="ToStoreDir"/>'s contract.</exception>
    public static string ToFileEntityDir(string kind, string workspaceRoot)
    {
        if (!FileEntityDirs.TryGetValue(kind, out string? dirName))
            throw new ArgumentException($"Unknown file-entity kind: {kind}", nameof(kind));
        return Path.Combine(workspaceRoot, ".scrinia", dirName);
    }

    /// <summary>
    /// Resolves a file entity's full path, enforcing both name validity and directory containment.
    /// Returns false for a name that is invalid or would escape — callers surface their own error.
    /// <para>Validation lives here so it cannot be forgotten: it was previously duplicated at two
    /// call sites and absent from the rest.</para>
    /// </summary>
    public static bool TryToFileEntityPath(string kind, string? name, string workspaceRoot, out string path)
    {
        path = "";
        if (!FileEntityName.IsValid(name))
            return false;

        string dir = ToFileEntityDir(kind, workspaceRoot);
        string candidate = Path.Combine(dir, name + ".md");
        if (!PathContainment.IsWithin(dir, candidate))
            return false;

        path = candidate;
        return true;
    }

    // DELETED (Task 13): ToFilesystemPath, ToVersionsDir and ToMetadataPath.
    //
    // ToFilesystemPath was a SECOND, independent scope→directory mapper living in this same file
    // alongside ToStoreDir, built by Path.Combine-ing RAW ParsedPath segments. It therefore
    // bypassed both sanitization and the aliasing rule, and once the one scope constructor
    // (MemoryNaming.BuildScopedTopicScope) started rejecting a trailing dot / bare "." / NUL and
    // collapsing redundant separators, it silently disagreed with ToStoreDir about where a memory
    // for the same path lives. ToVersionsDir and ToMetadataPath were derived from it and carried
    // the same divergence.
    //
    // All three had ZERO production callers — only PathRouterTests and MigrationTests — while
    // being public on Scrinia.Core, i.e. one `using` away from becoming the wrong resolver. The
    // live equivalents are FileMemoryStore.ArtifactPath / MetaPath / ArchiveVersion, which all
    // route through GetStoreDirForScope → ToStoreDir, the single authority. MigrationTests'
    // layout assertions were rewritten against those rather than deleted, so the behaviour they
    // covered is still pinned.
    //
    // ToFileEntityDir and TryToFileEntityPath are NOT dead (MemoryTools.Core/Skills/Lifecycle and
    // BundleFormatService call them) and remain above.
    //
    // ALSO DELETED (branch review): IsFileEntity(string?) and IsEphemeral(ParsedPath), which the
    // commit above missed. Both were reachable only from ToFilesystemPath, so deleting it orphaned
    // them; their only remaining references were tests written to exercise them, and both were
    // `public static` on Scrinia.Core — the same "one `using` from being wrong" hazard that deletion
    // cited. The live rules they duplicated:
    //   * the file-entity kind test is FileEntityDirs.TryGetValue, inside ToFileEntityDir, where a
    //     caller cannot ask the question and then forget to act on the answer;
    //   * the ephemeral-alias rule is MemoryNaming.CanonicalizeEphemeralAlias, applied at the
    //     two surfaces: the MCP facade (MemoryTools.cs) and the CLI commands
    //     (ScriniaCommands.Memory.cs). Nothing below those surfaces sees the alias form.

    /// <summary>
    /// Maps an internal scope string ("local", "local:findings", "local:goal/G-5") to its
    /// store directory under <c>.scrinia/memories/</c>. The single authority for
    /// scope-to-directory resolution (v3 layout — see <see cref="StoreLayoutMigration"/>).
    /// <para>The <c>local-topic:</c> prefix is a read-only recovery-compat shim for legacy journals
    /// from older store layouts; it resolves to <c>.scrinia/memories/&lt;topic&gt;</c> only before the
    /// sunset in <see cref="LocalTopicSunsetDate"/> (2027-03-21). After sunset the prefix falls
    /// through to the <c>Unknown scope</c> throw, matching the other prefixes' treatment.</para>
    /// </summary>
    public static string ToStoreDir(string scope, string workspaceRoot)
    {
        string memories = Path.Combine(workspaceRoot, ".scrinia", MemoriesDir);
        if (scope == "local")
            return memories;

        // Recovery-compat read-only shim for legacy `local-topic:` journals. Sunset 2027-03-21; after sunset legacy inputs resolve to Unknown scope (see LocalTopicSunsetDate).
        if (DateTimeOffset.UtcNow < LocalTopicSunsetDate && scope.StartsWith("local-topic:", StringComparison.Ordinal))
        {
            return Path.Combine(memories,
                scope["local-topic:".Length..].Replace('/', Path.DirectorySeparatorChar));
        }

        if (scope.StartsWith("local:", StringComparison.Ordinal))
            return Path.Combine(memories,
                scope["local:".Length..].Replace('/', Path.DirectorySeparatorChar));

        throw new ArgumentException($"Unknown scope: {scope}");
    }
}
