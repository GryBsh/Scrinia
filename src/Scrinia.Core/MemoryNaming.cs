namespace Scrinia.Core;

/// <summary>
/// Pure static naming utilities for memory scopes and ephemeral prefixes.
/// Extracted from <see cref="FileMemoryStore"/> so they can be used without
/// depending on a concrete store implementation.
/// </summary>
public static class MemoryNaming
{
    /// <summary>
    /// Throws if <paramref name="name"/> uses the removed v1 "topic:subject" colon
    /// syntax. Memory names are paths now ("/topic/subject"); rejecting a bare colon
    /// (rather than letting <c>SanitizeName</c> silently fold it into a mangled local
    /// name) forces callers to migrate and surfaces the exact path to use.
    /// </summary>
    public static void RejectLegacyColonSyntax(string name)
    {
        int colon = name.IndexOf(':');
        if (colon < 0) return;

        string topic = name[..colon].Trim();
        string subject = name[(colon + 1)..].Trim();
        string suggestion = topic.Length > 0 && subject.Length > 0
            ? $"/{topic}/{subject}"
            : "/topic/subject";
        throw new ArgumentException(
            $"Memory names use path syntax now — '{name}' should be '{suggestion}'. " +
            "The 'topic:subject' form was removed.",
            nameof(name));
    }

    /// <summary>
    /// Strips the leading '~' from an ephemeral name.
    /// Returns the input unchanged if it does not start with '~'.
    /// </summary>
    public static string StripEphemeralPrefix(string name) =>
        name.Length > 0 && name[0] == '~' ? name[1..] : name;

    // ── Scope construction ───────────────────────────────────────────────────
    //
    // THE ONE WAY a "local:{topic}" scope string is constructed from caller input. Task 13
    // consolidated four independent builders here (ParseQualifiedName, NormalizeScopeFilters,
    // BundleFormatService and IMemoryStore.BuildExcludedScopeSet), because the aliasing rule
    // below only holds if it is intrinsic to construction rather than layered over each site.
    //
    // The one deliberate NON-participant is scope RECOVERY — reading back a scope that already
    // exists, in FileMemoryStore.DiscoverMemoriesRecursive (from a directory name the filesystem
    // itself produced) and ParseScopeLabel (from a label a remote store reports). Those must
    // report what they find, not re-validate it, for the same reason SanitizeSegment must never
    // throw on a name loaded from a sidecar.

    /// <summary>Characters invalid on Windows that Linux allows — always stripped for
    /// portability.</summary>
    private static readonly HashSet<char> s_portableInvalid =
        [.. Path.GetInvalidFileNameChars(), ':', '*', '?', '<', '>', '|', '"'];

    /// <summary>
    /// Replaces every character that cannot appear in a path component, for ONE component.
    /// Shared verbatim by the scope path (via <see cref="TryBuildScopedTopicScope"/>) and the
    /// leaf/subject path (via <c>FileMemoryStore.SanitizeName</c>) so a topic and a subject are
    /// never sanitized by two subtly different rules.
    /// </summary>
    /// <returns><see langword="false"/> when nothing usable survives.</returns>
    public static bool TrySanitizeSegment(string name, out string safe)
    {
        safe = "";

        // Strip path traversal and directory separators first.
        string s = name.Replace("..", "").Replace('/', '_').Replace('\\', '_');
        s = string.Concat(s.Select(c => s_portableInvalid.Contains(c) ? '_' : c));

        // Final safety: extract only the filename component (blocks any residual path).
        s = Path.GetFileName(s);

        if (string.IsNullOrWhiteSpace(s)) return false;
        safe = s;
        return true;
    }

    /// <summary>Throwing form of <see cref="TrySanitizeSegment"/>.</summary>
    public static string SanitizeSegment(string name) =>
        TrySanitizeSegment(name, out string safe)
            ? safe
            : throw new ArgumentException($"Name '{name}' is invalid after sanitization.", nameof(name));

    /// <summary>
    /// Builds the full internal scope string used by the storage layer, sanitizing and validating
    /// every segment. Every topic — including the former "entity" (skill) and "agent" namespaces —
    /// is flat under the v3 layout: "local:{topic}". ("local:agent" happens to coincide with the
    /// old agent-namespace scope, since "agent" has no child topic to flatten away.)
    ///
    /// <para>Flattened as part of the v3 cutover (Task 7): once <see cref="PathRouter.ToStoreDir"/>
    /// routes every scope to <c>.scrinia/memories/</c>, a lingering "entity/skill" segment would
    /// resolve to <c>memories/entity/skill/</c> — a location <see cref="StoreLayoutMigration"/>
    /// never writes to (it moves entity/skill/ content to <c>memories/skill/</c>). Keeping this
    /// namespaced would silently orphan every skill memory (and, via
    /// <c>StoreLayoutMigration.MoveRenamedVectorDirs</c>, its vectors too).</para>
    /// </summary>
    /// <exception cref="ArgumentException">The topic cannot be made into a non-aliasing scope.
    /// This is the form WRITE callers want: writing to an aliased scope is a data-loss path
    /// (see <see cref="TryBuildScopedTopicScope"/>), so the failure must propagate.</exception>
    public static string BuildScopedTopicScope(string topic) =>
        TryBuild(topic, out string scope, out string? error)
            ? scope
            : throw new ArgumentException(error, nameof(topic));

    /// <summary>
    /// Non-throwing form, for READ callers (scope filters, exclude lists). A scope that cannot be
    /// constructed cannot name a stored memory either — <c>ArtifactEntry</c> carries no scope
    /// field and <c>DiscoverTopics</c> re-derives every scope from the on-disk directory name, so
    /// an aliased string never survives a round trip. Treating a failure as "matches nothing" is
    /// therefore the correct answer for a read, not leniency; it is the same rule, differing only
    /// in what the caller does with the failure.
    /// </summary>
    public static bool TryBuildScopedTopicScope(string? topic, out string scope) =>
        TryBuild(topic, out scope, out _);

    /// <summary>
    /// The rule itself. A scope's topic part becomes a chain of DIRECTORY components
    /// (<see cref="PathRouter.ToStoreDir"/>), so any two topics naming one directory chain must
    /// not be two scope strings — they would be two <c>CachedIndex</c> keys and two write locks
    /// over one directory, and a write through either leaves the other's cache stale.
    /// <c>SaveIndex</c>'s orphan sweep then deletes every sidecar absent from that stale list.
    /// </summary>
    private static bool TryBuild(string? topic, out string scope, out string? error)
    {
        scope = "";
        error = null;

        if (string.IsNullOrWhiteSpace(topic))
        {
            // "local:" alone resolves to the memories root — i.e. it aliases the bare "local"
            // scope, which is exactly the collision this method exists to prevent.
            error = "A scope topic must not be empty.";
            return false;
        }

        // RemoveEmptyEntries collapses "a//b", "/a" and "a/" — each of which would otherwise
        // produce a distinct scope string for the same directory chain.
        string[] parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = $"Scope topic '{topic}' contains no usable segments.";
            return false;
        }

        var safe = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!TrySanitizeSegment(parts[i], out string s))
            {
                error = $"Scope segment '{parts[i]}' is invalid after sanitization.";
                return false;
            }

            // The filesystem trims trailing dots and spaces from a path component, so "topic."
            // and "topic " both open "topic". Substitution cannot repair that, and rewriting to
            // the trimmed form would file the caller's memory under a scope they did not name.
            // This also covers a bare "." segment, which resolves to the directory itself and so
            // aliases the PARENT scope.
            if (s[^1] is '.' or ' ')
            {
                error = $"Scope segment '{parts[i]}' must not end with a '.' or a space — the "
                      + "filesystem trims those, so it would name the same directory as the "
                      + "trimmed form.";
                return false;
            }

            // MEASURED, and deliberately narrow: on Windows a directory named CON/PRN/AUX/
            // COM1-9/LPT1-9 is created and written successfully, so refusing those would reject
            // input that demonstrably works. NUL is the only one that genuinely cannot back a
            // directory (Directory.CreateDirectory throws DirectoryNotFoundException).
            if (s.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Scope segment '{parts[i]}' is a reserved device name that cannot back "
                      + "a directory.";
                return false;
            }

            safe[i] = s;
        }

        scope = $"local:{string.Join('/', safe)}";
        return true;
    }

    /// <summary>
    /// Returns a human-friendly display label for an internal scope string.
    /// "local" → "local", "local:api" → "api", "local:agent" → "agent",
    /// "ephemeral" → "ephemeral". v3: every topic part is already flat, so there is no
    /// namespace prefix left to strip (see Task 8, which removed the v1/v2 fallback this
    /// used to interoperate with).
    /// </summary>
    public static string FormatScopeLabel(string scope)
    {
        if (scope == "local") return "local";
        if (scope == "ephemeral") return "ephemeral";
        if (scope.StartsWith("local:", StringComparison.Ordinal))
            return scope["local:".Length..];
        return scope;
    }

    /// <summary>Inverse of <see cref="FormatScopeLabel"/>: RECOVERS a scope from its display
    /// label. Needed by remote clients, which receive topic results labelled rather than scoped.
    ///
    /// <para><b>Deliberately not routed through <see cref="BuildScopedTopicScope"/></b> (Task 13).
    /// This recovers a scope the remote store has already reported as existing, rather than
    /// constructing one from caller input, so it must round-trip whatever that store says —
    /// exactly as <c>FileMemoryStore.DiscoverMemoriesRecursive</c> reports the directory names it
    /// finds on disk. Validating here would hide a remote memory rather than prevent a local
    /// write, and no write is reachable from this value.</para>
    /// Round-trips "local", "ephemeral", and "local:{topic}" where topic is neither "local" nor
    /// "ephemeral". <see cref="FormatScopeLabel"/> is not injective — a memory scoped
    /// "local:local" or "local:ephemeral" (e.g. a topic literally named "local" or "ephemeral")
    /// formats to the same label as the bare "local"/"ephemeral" scope, so labels in that
    /// collision zone do not round-trip back to their original scope.
    /// </summary>
    public static string ParseScopeLabel(string label) => label switch
    {
        "local" => "local",
        "ephemeral" => "ephemeral",
        _ => $"local:{label}",
    };
}
