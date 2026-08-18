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

    /// <summary>
    /// True for an ephemeral name in its canonical form (<c>~subject</c>).
    /// <para>THE static authority for the predicate. <see cref="FileMemoryStore.IsEphemeral"/>
    /// delegates here, the same way <see cref="StripEphemeralPrefix"/> already owns the strip.
    /// Null-safe, because the body it replaced was.</para>
    /// </summary>
    public static bool IsEphemeralName(string name) =>
        !string.IsNullOrEmpty(name) && name[0] == '~';

    /// <summary>
    /// True for the ephemeral SURFACE alias — a path under the reserved temp prefix.
    /// <para>The alias is a surface concern, canonicalized at the MCP facade and at the CLI
    /// commands, and it never reaches the store or the wire. Note this is false for
    /// <c>~x</c>: an alias is not yet an ephemeral name.</para>
    /// </summary>
    public static bool IsEphemeralAlias(string name) =>
        name is not null && name.StartsWith("/temp/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// THE ONE PLACE the ephemeral alias becomes <c>~subject</c>; returns anything else unchanged.
    ///
    /// <para>This rule used to be two verbatim string literals in <c>MemoryTools.Core.cs</c>
    /// (Remember and Append) and existed nowhere else, so <c>recall</c>, <c>forget</c>,
    /// <c>compact</c> and <c>link</c> answered NOT_FOUND for a path <c>remember</c> had just
    /// accepted — including the literal example in <c>forget</c>'s own tool description. It
    /// lives here because the aliasing rule only holds if it is intrinsic rather than layered
    /// over each site, which is the reasoning that already put scope construction in this file.</para>
    ///
    /// <para>Case folding applies to the PREFIX only; the subject keeps its casing, matching
    /// the literals this replaces.</para>
    /// </summary>
    public static string CanonicalizeEphemeralAlias(string name) =>
        IsEphemeralAlias(name) ? "~" + name["/temp/".Length..] : name;

    /// <summary>
    /// Splits a legacy comma-separated filter string (e.g. a <c>scopes</c> or
    /// <c>excludeTopics</c> argument) into an array, trimming each entry and dropping empty ones.
    /// Returns null for null or whitespace-only input — the same "no filter" meaning null has
    /// always carried on these parameters (Task 9, D-4: an empty result behaves as null too, so
    /// callers should not special-case it). Returns a concrete <c>string[]?</c> rather than
    /// <c>IReadOnlyList&lt;string&gt;?</c> so it can feed either shape directly: the Core
    /// contract's list-typed members, and the MCP facade/CLI-internal handlers this task also
    /// types as <c>string[]?</c> (D-1).
    ///
    /// <para>This is the one place that turns a comma string into a list; it does not construct
    /// or validate scope names itself — each element still goes through the store's own
    /// <c>NormalizeScopeFilters</c> or <see cref="TryBuildScopedTopicScope"/> downstream. It
    /// exists for two kinds of caller, both permanent: the CLI, which keeps comma-separated
    /// arguments on purpose (Task 9, D-3 — command-line arguments are strings, and forcing
    /// repeated flags would be a worse CLI for no gain — a scope name containing a comma is
    /// therefore unaddressable from the CLI, and that is accepted), and the REST
    /// <c>/memories</c> listing endpoint's <c>scopes</c> query parameter, which this task does
    /// not convert to repeated params (only <c>/search</c> is — Task 9, D-2).</para>
    /// </summary>
    public static string[]? SplitCommaSeparated(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// THE ONE RULE for a <c>scopes</c>/<c>excludeTopics</c> filter list, regardless of which
    /// surface it arrived through: drops every null and blank entry, trims what survives, and
    /// returns null when nothing does (so "no filter" is always exactly one shape — null — never
    /// null, <c>[]</c> and <c>[""]</c> as three cases a caller has to check separately).
    ///
    /// <para>Task 9 fix round 1 (F-1/F-2/F-3). Three symptoms shared one root cause: ASP.NET Core
    /// binds an empty <c>?scopes=</c> query value to <c>string[] { "" }</c>, not an empty array
    /// (F-1); JSON allows a <c>null</c> array element regardless of a non-nullable element
    /// annotation, and that null used to reach a bare <c>.Trim()</c> a few frames downstream and
    /// throw (F-2); and blank entries used to be dropped by the old comma-string split but not by
    /// the array path, so <c>["", "api"]</c> silently added the local scope instead of resolving
    /// like <c>["api"]</c> (F-3). Rather than guard each of those three call sites separately —
    /// which would be the branch's own defect in miniature, rules hand-placed at N call sites — this
    /// is the one place the rule lives; every construction and entry path below routes through
    /// it.</para>
    /// </summary>
    public static IReadOnlyList<string>? NormalizeFilterList(IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
            return null;

        List<string>? result = null;
        foreach (string value in values)
        {
            // `value` is annotated non-null by IReadOnlyList<string>'s element type, but neither
            // JSON deserialization nor ASP.NET Core's array model binding actually enforces that at
            // runtime — a `null` element reaches here as a real null despite the annotation, which
            // is exactly F-2. IsNullOrWhiteSpace is null-safe, so this both drops nulls and blanks
            // in the same check.
            if (string.IsNullOrWhiteSpace(value))
                continue;
            (result ??= new List<string>(values.Count)).Add(value.Trim());
        }
        return result;
    }

    // ── Scope construction ───────────────────────────────────────────────────
    //
    // THE ONE WAY a "local:{topic}" scope string is constructed from caller input. Task 13
    // consolidated four independent builders here (ParseQualifiedName, NormalizeScopeFilters,
    // BundleFormatService and IMemoryStore.BuildExcludedScopeSet), because the aliasing rule
    // below only holds if it is intrinsic to construction rather than layered over each site.
    //
    // The one deliberate NON-participant is scope RECOVERY — reading back a scope that already
    // exists, in FileMemoryStore.DiscoverMemoriesRecursive (from a directory name the filesystem
    // itself produced) and ParseScopeLabel (from a label a remote store reports).
    //
    // Why that is safe is NOT "no write is reachable from them" — writes very much are: both feed
    // DiscoverTopics -> ResolveReadScopes, and MemoryTools' forget-by-URI loop calls DeleteArtifact
    // and Remove once per resolved scope. The actual reason is stronger: a RECOVERED scope is
    // canonical BY CONSTRUCTION. It is the name the filesystem or the remote store actually holds,
    // so a write through it targets the very directory it was read from and cannot mint a second
    // key for one directory — which is the only thing the rule prevents.
    //
    // Validating there would be actively harmful, and not merely by hiding a memory: because the
    // forget-by-URI loop silently skips any scope the filter drops, a memory in a scope the
    // validator rejected would become UNDELETABLE. Do not "tighten" these two sites.

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
    /// Overload for callers that ALREADY hold separate segments (e.g. a parsed path), so they do
    /// not have to join on '/' just for this to split again. That round trip is the same
    /// separator-versus-character ambiguity that made the bundle key and the scope disagree: '/'
    /// is a SEPARATOR to the string overload and an invalid CHARACTER (replaced with '_') inside
    /// one segment. Taking the segments directly removes the question.
    /// </summary>
    public static string BuildScopedTopicScope(IEnumerable<string> segments) =>
        TryBuild(segments, out string scope, out string? error)
            ? scope
            : throw new ArgumentException(error, nameof(segments));

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
        return TryBuild(topic.Split('/', StringSplitOptions.RemoveEmptyEntries), out scope, out error);
    }

    /// <summary>The rule, over already-separated segments. See the string overload.</summary>
    private static bool TryBuild(IEnumerable<string> segments, out string scope, out string? error)
    {
        scope = "";
        error = null;

        var parts = segments.ToArray();
        if (parts.Length == 0)
        {
            error = "A scope topic must not be empty.";
            return false;
        }

        var safe = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            // NOTE: TrySanitizeSegment STRIPS "..", it does not reject it — so a segment "a..b"
            // becomes "ab". That is a rewrite rather than a refusal, and it means a filter naming
            // an already-stored "local:a..b" would resolve to "local:ab" and match nothing. It is
            // unreachable from ParseQualifiedName (PathParser rejects any ".." outright before
            // this runs) and no write path can create such a scope, so nothing is stranded today
            // — but it is the one input class the rule rewrites instead of rejecting.
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
    /// This RECOVERS a scope the remote store already reports as existing, rather than
    /// constructing one from caller input, so it must round-trip whatever that store says —
    /// exactly as <c>FileMemoryStore.DiscoverMemoriesRecursive</c> reports the directory names it
    /// finds on disk.
    ///
    /// <para>Note this value DOES reach writes — <c>HttpMemoryStore</c>'s list path feeds
    /// <c>DiscoverTopics</c>, and <c>MemoryTools</c>' forget-by-URI loop calls <c>Remove</c> once
    /// per resolved scope. That is safe because a recovered scope is canonical by construction:
    /// it names the directory it was read from, so it cannot create a second key for one
    /// directory. Validating here would instead make a memory in a rejected scope UNDELETABLE,
    /// since that loop silently skips any scope the filter drops.</para></para>
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
