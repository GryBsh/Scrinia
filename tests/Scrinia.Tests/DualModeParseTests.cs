using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Integration tests for path-based name parsing (the legacy "topic:subject"
/// colon form is now rejected), FormatQualifiedName roundtrips, and legacy
/// on-disk fallback resolution.
/// </summary>
public sealed class DualModeParseTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public DualModeParseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia-dual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Dual-mode parsing tests ─────────────────────────────────────────────

    [Fact]
    public void ParseQualifiedName_ColonName_IsRejected()
    {
        // The v1 "topic:subject" colon syntax was removed — names are paths now.
        // Rejecting (rather than silently sanitizing the colon away) surfaces the
        // path to use.
        var act = () => _store.ParseQualifiedName("api:auth-flow");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*/api/auth-flow*");
    }

    [Fact]
    public void ParseQualifiedName_V2Path_ReturnsCorrectScope()
    {
        // v2 syntax: "/api/auth-flow" -> scope with "api", subject "auth-flow"
        var (scope, subject) = _store.ParseQualifiedName("/api/auth-flow");

        scope.Should().Be("local:api");
        subject.Should().Be("auth-flow");
    }

    [Fact]
    public void ParseQualifiedName_V2DeepPath_ReturnsHierarchicalScope()
    {
        // v2 deep path: "/goal/G-5/research/frontend"
        // PathParser sees "goal" as entity type -> EntityPair("goal","G-5"), then "research","frontend" as tags
        // All segments build scope: "local:goal/G-5/research", subject "frontend"
        var (scope, subject) = _store.ParseQualifiedName("/goal/G-5/research/frontend");

        scope.Should().Be("local:goal/G-5/research");
        subject.Should().Be("frontend");
    }

    [Fact]
    public void ParseQualifiedName_V2SingleSegment_ReturnsLocal()
    {
        // v2 single segment: "/frontend" -> ("local", "frontend")
        var (scope, subject) = _store.ParseQualifiedName("/frontend");

        scope.Should().Be("local");
        subject.Should().Be("frontend");
    }

    [Fact]
    public async Task LegacyFlatTopicSeededOnDisk_RecallViaV2Path_AutoMigrates()
    {
        // Formerly "StoreViaLegacyScope_RecallViaV2Deep_Works": wrote via the store's own
        // WriteArtifactAsync using a legacy scope, relying on the v1/v2 coexistence fallback to
        // find it again via a mismatched deep v2 path. Under v3 routing (this task),
        // WriteArtifactAsync can no longer produce a topics/ layout at all — PathRouter.ToStoreDir
        // routes every write straight to memories/ — so this now seeds the legacy file directly on
        // disk, the way real pre-cutover data sits. FileMemoryStore.MemoriesRoot auto-migrates it
        // to memories/research/ on the very first store touch, which is what makes it readable —
        // not the old fallback, which only ever finds content still sitting under topics/ and can
        // never fire once the gate has already relocated everything on this store's first access.
        string legacyDir = Path.Combine(_root, ".scrinia", "topics", "research");
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllTextAsync(Path.Combine(legacyDir, "frontend.nmp2"), "v1 stored content");

        var (scope, subject) = _store.ParseQualifiedName("/research/frontend");
        string content = await _store.ReadArtifactAsync(subject, scope);

        content.Should().Be("v1 stored content");
        File.Exists(Path.Combine(_root, ".scrinia", "memories", "research", "frontend.nmp2"))
            .Should().BeTrue("auto-migration, not the legacy fallback, is what made this readable");
    }

    [Fact]
    public async Task StoreViaV2_RoundTrips()
    {
        // Store using v2 path syntax and read it straight back.
        var (scopeV2, subjectV2) = _store.ParseQualifiedName("/api/auth-flow");
        await _store.WriteArtifactAsync(subjectV2, scopeV2, "v2 stored content");

        string v2Path = _store.ArtifactPath(subjectV2, scopeV2);
        File.Exists(v2Path).Should().BeTrue("v2 write should create the file");

        string v2Content = await _store.ReadArtifactAsync(subjectV2, scopeV2);
        v2Content.Should().Be("v2 stored content");
    }

    // ── Legacy fallback tests ───────────────────────────────────────────────

    [Fact]
    public async Task ReadV2Path_FallsBackToV1Legacy()
    {
        // Create file directly in v1 flat location: .scrinia/topics/api/auth-flow.nmp2
        string legacyDir = Path.Combine(_root, ".scrinia", "topics", "api");
        Directory.CreateDirectory(legacyDir);
        string legacyFile = Path.Combine(legacyDir, "auth-flow.nmp2");
        await File.WriteAllTextAsync(legacyFile, "legacy v1 content");

        // Read via v2 path — should fall back to the v1 location
        var (scope, subject) = _store.ParseQualifiedName("/api/auth-flow");
        string content = await _store.ReadArtifactAsync(subject, scope);

        content.Should().Be("legacy v1 content");
    }

    [Fact]
    public async Task NamespacedLegacySkillSeededOnDisk_RecallViaFlatV2Path_AutoMigrates()
    {
        // skill is the only remaining entity topic; place legacy data under
        // .scrinia/topics/entity/skill/. Previously (pre-v3) this was found via the deep-path
        // leaf-topic-guessing legacy fallback (ResolveV2LegacyDir), read through a mismatched
        // deep path ending in "skill". Under v3, MemoriesRoot's auto-migration flattens
        // entity/skill/ -> memories/skill/ (matching MemoryNaming.BuildScopedTopicScope("skill"),
        // itself flattened by this task) on the very first store touch, before any read runs —
        // so the content is found via the PRIMARY path for the flat "skill" scope, not a deep
        // path whose leaf happens to be "skill" (nothing routes to a nested skill/ subdirectory
        // under v3, so that deep path would no longer resolve here at all).
        string nsDir = Path.Combine(_root, ".scrinia", "topics", "entity", "skill");
        Directory.CreateDirectory(nsDir);
        string nsFile = Path.Combine(nsDir, "frontend.nmp2");
        await File.WriteAllTextAsync(nsFile, "namespaced legacy content");

        var (scope, subject) = _store.ParseQualifiedName("/skill/frontend");
        string content = await _store.ReadArtifactAsync(subject, scope);

        content.Should().Be("namespaced legacy content");
        File.Exists(Path.Combine(_root, ".scrinia", "memories", "skill", "frontend.nmp2"))
            .Should().BeTrue("auto-migration flattens entity/skill/ into memories/skill/");
    }

    [Fact]
    public async Task ReadV2DeepPath_PrefersPrimaryOverLegacy()
    {
        // Use a deep v2 path so the scope is a genuine v2 path scope
        var (scope, subject) = _store.ParseQualifiedName("/goal/G-5/research/frontend");

        // Write to v2 primary location (.scrinia/memories/goal/G-5/research/)
        await _store.WriteArtifactAsync(subject, scope, "v2 primary content");

        // Also create a legacy file in the fallback location
        // ResolveV2LegacyDir uses leaf "research" -> .scrinia/topics/research/
        string legacyDir = Path.Combine(_root, ".scrinia", "topics", "research");
        Directory.CreateDirectory(legacyDir);
        string legacyFile = Path.Combine(legacyDir, "frontend.nmp2");
        await File.WriteAllTextAsync(legacyFile, "v1 legacy content");

        // Reading should return v2 primary content, not legacy
        string content = await _store.ReadArtifactAsync(subject, scope);
        content.Should().Be("v2 primary content");
    }

    [Fact]
    public void IndexLoad_MergesV1EntriesIntoV2Scope()
    {
        // Create a v1 topic directory with sidecar metadata
        string legacyDir = Path.Combine(_root, ".scrinia", "topics", "api");
        Directory.CreateDirectory(legacyDir);

        // Write a sidecar .meta.json and an artifact .nmp2 in the v1 location
        var entry = new ArtifactEntry(
            "auth-flow", "file://auth-flow", 100, 1,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(legacyDir, "auth-flow.nmp2"),
            "legacy artifact content");
        File.WriteAllText(
            Path.Combine(legacyDir, "auth-flow.meta.json"),
            System.Text.Json.JsonSerializer.Serialize(entry));

        // Use the legacy namespaced scope to load index — the legacy dir should be discovered
        string scopeV1 = MemoryNaming.BuildScopedTopicScope("api");
        var entries = _store.LoadIndex(scopeV1);

        entries.Should().Contain(e => e.Name == "auth-flow",
            because: "v1 sidecar entries in .scrinia/topics/api/ should be visible");
    }

    // ── FormatQualifiedName roundtrip tests ─────────────────────────────────

    [Fact]
    public void FormatQualifiedName_V2Scope_ReturnsPathSyntax()
    {
        // v2 scope from parsing "/goal/G-5/research/frontend"
        var (scope, subject) = _store.ParseQualifiedName("/goal/G-5/research/frontend");

        // FormatQualifiedName should reconstruct the v2 path syntax
        string formatted = _store.FormatQualifiedName(scope, subject);

        formatted.Should().Be("/goal/G-5/research/frontend");
    }

    [Fact]
    public void FormatQualifiedName_LegacyNamespacedScope_ReturnsPathSyntax()
    {
        // A legacy namespaced topic scope (local:memory/api) still formats
        // as a path for display.
        string scope = MemoryNaming.BuildScopedTopicScope("api");

        string formatted = _store.FormatQualifiedName(scope, "auth-flow");

        formatted.Should().Be("/api/auth-flow");
    }

    // ── Additional coverage ─────────────────────────────────────────────────

    [Fact]
    public void ParseQualifiedName_V2ThreeSegment_ReturnsCorrectScopeAndSubject()
    {
        // "/agent/profile" -> 2 segments, scope "local:agent", subject "profile"
        var (scope, subject) = _store.ParseQualifiedName("/agent/profile");

        scope.Should().Be("local:agent");
        subject.Should().Be("profile");
    }

    [Fact]
    public void FormatQualifiedName_LocalScope_ReturnsSubjectOnly()
    {
        // Local scope (no topic prefix) returns just the subject
        string formatted = _store.FormatQualifiedName("local", "my-notes");

        formatted.Should().Be("my-notes");
    }

    [Fact]
    public void ParseAndFormat_V2DeepPath_Roundtrips()
    {
        // Full roundtrip: parse v2 deep path -> format -> should get same path back
        // Only deep (3+ segment) paths produce v2 path scopes that roundtrip as paths.
        // 2-segment paths like "/api/auth-flow" produce a scope indistinguishable from
        // v1, so FormatQualifiedName returns v1 syntax "api:auth-flow" for those.
        string original = "/goal/G-5/research/frontend";
        var (scope, subject) = _store.ParseQualifiedName(original);
        string formatted = _store.FormatQualifiedName(scope, subject);

        formatted.Should().Be(original);
    }

    [Fact]
    public void ParseAndFormat_V2TwoSegmentPath_FormatsAsV1()
    {
        // 2-segment v2 paths produce a flat scope (no slash in topic part),
        // so FormatQualifiedName uses v1 topic:subject syntax.
        // This is intentional: 2-segment v2 paths are equivalent to v1 topics.
        var (scope, subject) = _store.ParseQualifiedName("/api/auth-flow");
        string formatted = _store.FormatQualifiedName(scope, subject);

        formatted.Should().Be("/api/auth-flow");
    }

    [Fact]
    public void ParseAndFormat_TwoSegmentPath_Roundtrips()
    {
        // A two-segment path roundtrips through parse -> format unchanged.
        string original = "/patterns/retry";
        var (scope, subject) = _store.ParseQualifiedName(original);
        string formatted = _store.FormatQualifiedName(scope, subject);

        formatted.Should().Be("/patterns/retry");
    }

    [Fact]
    public void ParseQualifiedName_V2EntityPath_ScopeIncludesEntitySegments()
    {
        // "/concern/SEC-054" -> entity pair concern/SEC-054
        // scope = "local:concern", subject = "SEC-054"
        var (scope, subject) = _store.ParseQualifiedName("/concern/SEC-054");

        scope.Should().Be("local:concern");
        subject.Should().Be("SEC-054");
    }

    [Fact]
    public async Task FindArtifactPath_V2WithLegacyFallback_FindsLegacyFile()
    {
        // Directly test FindArtifactPath with a v2 scope that has a legacy file
        string legacyDir = Path.Combine(_root, ".scrinia", "topics", "patterns");
        Directory.CreateDirectory(legacyDir);
        string legacyFile = Path.Combine(legacyDir, "retry.nmp2");
        await File.WriteAllTextAsync(legacyFile, "retry pattern content");

        // v2 scope for "/patterns/retry"
        var (scope, subject) = _store.ParseQualifiedName("/patterns/retry");

        // FindArtifactPath should fall back to legacy
        string found = _store.FindArtifactPath(subject, scope);
        File.Exists(found).Should().BeTrue("FindArtifactPath should resolve to the legacy file");

        string content = await File.ReadAllTextAsync(found);
        content.Should().Be("retry pattern content");
    }

    // ── Scope-segment sanitization (Task 13) ─────────────────────────────────
    //
    // ParseQualifiedName used to sanitize ONLY the leaf segment while building the scope from
    // RAW segments, so "local:topic.", "local:topic " and "local:topic" were three distinct
    // scope strings — three CachedIndex keys and three ReaderWriterLockSlim instances — over
    // ONE directory. The characterization test at the bottom of this section demonstrates that
    // store-layer hazard directly; the tests above it pin the parser contract that keeps
    // ParseQualifiedName from ever minting the second and third strings.

    private static ArtifactEntry MakeEntry(string name) =>
        new(name, $"file:///{name}.nmp2", 16, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void ParseQualifiedName_ScopeSegment_TrailingDot_IsRejected()
    {
        // Windows trims trailing dots from a path COMPONENT, so "topic." opens the directory
        // "topic". Sanitization cannot repair that, and rewriting it to "topic" would silently
        // file the caller's memory under a scope they did not name.
        var act = () => _store.ParseQualifiedName("/topic./note");

        act.Should().Throw<ArgumentException>(
            because: "'topic.' and 'topic' are one directory, so returning them as two scope "
                   + "strings gives that directory two cache keys and two locks")
           .WithMessage("*must not end with*",
            because: "asserting only the exception type would pass for any other refusal");
    }

    [Fact]
    public void ParseQualifiedName_ScopeSegment_TrailingSpace_IsRejected()
    {
        // Same mechanism as the trailing dot: Windows trims trailing spaces too.
        var act = () => _store.ParseQualifiedName("/topic /note");

        act.Should().Throw<ArgumentException>(
            because: "'topic ' and 'topic' resolve to one directory")
           .WithMessage("*must not end with*");
    }

    [Fact]
    public void ParseQualifiedName_ScopeSegment_BareDot_IsRejected()
    {
        // "." resolves to the directory itself, aliasing the PARENT scope: "/./note" would
        // land in memories/ alongside every plain "local" memory.
        var act = () => _store.ParseQualifiedName("/./note");

        act.Should().Throw<ArgumentException>(
            because: "a '.' segment resolves to the parent directory, aliasing the parent scope")
           .WithMessage("*must not end with*");
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void ParseQualifiedName_ScopeSegment_WorkingDeviceName_IsAccepted(string name)
    {
        // MEASURED, and the measurement beat the assumption: on Windows a directory named
        // CON/PRN/AUX/COM1-9/LPT1-9 is created and written successfully. None of them aliases
        // another scope. Refusing input that demonstrably works would be worse than the aliasing
        // this task exists to fix, so only NUL — the one that genuinely cannot back a directory —
        // is rejected. (Task 3's endpoint comment recorded the same measurement independently.)
        var (scope, subject) = _store.ParseQualifiedName($"/{name}/note");

        scope.Should().Be($"local:{name}");
        subject.Should().Be("note");

        // Not just parseable — actually usable end to end. Asserted on the SIDECAR rather than
        // through LoadIndex, which would be served from _indexCache and so would prove only that
        // the cache round-trips, not that the filesystem accepted the directory.
        _store.Upsert(MakeEntry("note"), scope);
        File.Exists(Path.Combine(_store.GetStoreDirForScope(scope), "note.meta.json"))
            .Should().BeTrue(
                because: $"a '{name}' scope must be a real, writable directory on this platform");
    }

    [Fact]
    public void ParseQualifiedName_ScopeSegment_NulDeviceName_IsRejected()
    {
        // NUL is the one device name that genuinely cannot back a directory: measured,
        // Directory.CreateDirectory throws DirectoryNotFoundException. Before this fix that
        // surfaced from deep inside a write as an IOException; now it is refused up front.
        var act = () => _store.ParseQualifiedName("/nul/note");

        act.Should().Throw<ArgumentException>(
            because: "NUL cannot back a directory at all, so the write would fail mid-flight")
           .WithMessage("*reserved device name*");
    }

    [Fact]
    public void ParseQualifiedName_ScopeSegment_FilesystemInvalidChars_AreSanitizedLikeTheLeaf()
    {
        // '*' is not a path separator and carries no traversal meaning, so it is REPAIRABLE —
        // and the leaf has always repaired it. The scope now agrees with the leaf.
        var (scope, subject) = _store.ParseQualifiedName("/a*b/c?d");

        scope.Should().Be("local:a_b",
            because: "a scope segment must be sanitized exactly as the leaf is");
        subject.Should().Be("c_d");
    }

    [Fact]
    public void ParseQualifiedName_ScopeSegment_DeepPath_SanitizesEverySegment()
    {
        // Every non-leaf segment is sanitized, not just the first or the last.
        var (scope, subject) = _store.ParseQualifiedName("/a|b/c<d/leaf");

        scope.Should().Be("local:a_b/c_d");
        subject.Should().Be("leaf");
    }

    [Fact]
    public void ParseQualifiedName_ScopeSegment_InteriorSpaceAndDot_AreKept()
    {
        // Guard against over-rejection: spaces and dots are legal INSIDE a component. Only a
        // TRAILING one aliases. This mirrors SanitizeName_SpaceAndDot_Unchanged for the leaf.
        var (scope, subject) = _store.ParseQualifiedName("/my notes.v2/entry");

        scope.Should().Be("local:my notes.v2",
            because: "only trailing dots/spaces alias; interior ones are ordinary characters");
        subject.Should().Be("entry");
    }

    [Fact]
    public void ParseQualifiedName_ScopeSegment_LeadingSpace_IsKept()
    {
        // Measured: Windows preserves a LEADING space in a directory name (" lead" and "lead"
        // are two real, distinct directories), so it does not alias and must not be refused.
        var (scope, _) = _store.ParseQualifiedName("/ lead/entry");

        scope.Should().Be("local: lead");
    }

    // ── The ONE scope constructor (Task 13 consolidation) ────────────────────
    //
    // ParseQualifiedName, NormalizeScopeFilters, BundleFormatService and
    // IMemoryStore.BuildExcludedScopeSet all build "local:{topic}" through
    // MemoryNaming.BuildScopedTopicScope now. These pin the rule at the constructor and at the
    // two callers whose behaviour on a failure differs (write throws, read matches nothing).

    [Theory]
    [InlineData("a//b", "local:a/b")]   // collapsed empty segment
    [InlineData("/a/b", "local:a/b")]   // leading slash
    [InlineData("a/b/", "local:a/b")]   // trailing slash
    public void BuildScopedTopicScope_RedundantSeparators_CollapseToOneScope(string topic, string expected)
    {
        // Each of these named the same directory chain as "a/b" while being a different scope
        // STRING — the same aliasing as a trailing dot, one level up.
        MemoryNaming.BuildScopedTopicScope(topic).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void BuildScopedTopicScope_EmptyTopic_IsRejected(string topic)
    {
        // "local:" alone resolves to the memories ROOT, i.e. it aliases the bare "local" scope.
        var act = () => MemoryNaming.BuildScopedTopicScope(topic);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildScopedTopicScope_AppliesTheSameRuleAsParseQualifiedName()
    {
        // The point of the consolidation: one rule, not four copies that can drift.
        MemoryNaming.BuildScopedTopicScope("a*b").Should().Be("local:a_b");
        new Action(() => MemoryNaming.BuildScopedTopicScope("topic.")).Should().Throw<ArgumentException>();
        new Action(() => MemoryNaming.BuildScopedTopicScope("nul")).Should().Throw<ArgumentException>();
        MemoryNaming.BuildScopedTopicScope("CON").Should().Be("local:CON");

        // …and ParseQualifiedName agrees, because it calls it.
        _store.ParseQualifiedName("/a*b/leaf").Scope.Should().Be("local:a_b");
    }

    [Fact]
    public void BuildScopedTopicScope_SegmentOverload_TreatsSlashAsACharacterNotASeparator()
    {
        // The two overloads deliberately differ, and the difference is the whole point of having
        // the segment one: to a caller holding already-split segments, '/' inside ONE of them is
        // an invalid character (sanitized to '_'), not a separator. Conflating the two is what
        // made the bundle zip key and the scope disagree.
        MemoryNaming.BuildScopedTopicScope("a/b").Should().Be("local:a/b");
        MemoryNaming.BuildScopedTopicScope(["a/b"]).Should().Be("local:a_b");
        MemoryNaming.BuildScopedTopicScope(["a", "b"]).Should().Be("local:a/b");
    }

    [Fact]
    public void TrySanitizeSegment_StripsDotDot_RatherThanRejectingIt()
    {
        // The one input class the rule REWRITES instead of refusing. Unreachable from
        // ParseQualifiedName (PathParser rejects ".." outright before this runs) and no write path
        // can create such a scope, so nothing is stranded — but it is worth pinning that this is
        // a rewrite, because every other unrepairable form is a refusal.
        MemoryNaming.TrySanitizeSegment("a..b", out string safe).Should().BeTrue();
        safe.Should().Be("ab");
        MemoryNaming.BuildScopedTopicScope("a..b").Should().Be("local:ab");
    }

    [Fact]
    public void ResolveReadScopes_ReservedDeviceFilter_MatchesNothing()
    {
        // The device-name rule reaches the read path through the same constructor. NUL is
        // refused, so the filter matches nothing; the names that demonstrably work are not.
        _store.ResolveReadScopes("nul").Should().BeEmpty();
        _store.ResolveReadScopes("CON").Should().BeEquivalentTo(["local:CON"]);
    }

    [Fact]
    public void ResolveReadScopes_UnconstructableFilter_MatchesNothingInsteadOfThrowing()
    {
        // READ callers take the non-throwing form. An aliased scope can never name a stored
        // memory (ArtifactEntry has no scope field; DiscoverTopics re-derives scopes from on-disk
        // directory names), so "matches nothing" is the correct answer, not leniency.
        _store.ResolveReadScopes("topic.").Should().BeEmpty();
        _store.ResolveReadScopes("local:topic.").Should().BeEmpty();
    }

    [Fact]
    public void ResolveReadScopes_AliasingFilters_AreClosedAtTheFilterSurfaceToo()
    {
        // The empty-topic and redundant-separator aliases are pinned at the constructor above,
        // but NormalizeScopeFilters is the one surface they were actually reachable from.
        // "local:" resolves to the memories ROOT, i.e. it aliased the bare "local" scope.
        _store.ResolveReadScopes("local:").Should().BeEmpty();

        // "a//b" named the same directory chain as "a/b" under a different scope string.
        _store.ResolveReadScopes("local:a//b").Should().BeEquivalentTo(["local:a/b"]);
    }

    [Fact]
    public void BuildExcludedScopeSet_SanitizesTheTopic_SoItCanActuallyMatchAStoredScope()
    {
        // I-1: this consolidation had a real behaviour gain and no test. excludeTopics "a*b" used
        // to build "local:a*b" — a scope string no write can produce, so the exclusion silently
        // matched nothing. It now builds the scope a memory stored at "/a*b/x" really has.
        var (storedScope, _) = _store.ParseQualifiedName("/a*b/x");
        storedScope.Should().Be("local:a_b");

        IMemoryStore.ShouldExcludeScope(storedScope, "a*b").Should().BeTrue(
            because: "the exclusion must resolve to the same scope the write path produced");

        // The '/'-containing arm used to bypass the constructor entirely via string interpolation.
        var (deepScope, _) = _store.ParseQualifiedName("/goal/G-5/x");
        IMemoryStore.ShouldExcludeScope(deepScope, "goal/G-5").Should().BeTrue();

        // A topic that cannot be constructed excludes nothing rather than throwing.
        IMemoryStore.ShouldExcludeScope(storedScope, "topic.").Should().BeFalse();
    }

    [Fact]
    public void ResolveReadScopes_LocalPrefixedFilter_IsNormalizedRatherThanPassedThroughRaw()
    {
        // This branch used to `return [s]` verbatim. That meant "local:a*b" became a scope string
        // no write could ever produce, and "LOCAL:api" reached ToStoreDir — whose prefix test is
        // Ordinal — as an unknown scope.
        _store.ResolveReadScopes("local:a*b").Should().BeEquivalentTo(["local:a_b"]);
        _store.ResolveReadScopes("LOCAL:api").Should().BeEquivalentTo(["local:api"]);
    }

    /// <summary>
    /// The store-layer hazard the parser fix exists to prevent, demonstrated directly against
    /// <see cref="FileMemoryStore"/>'s scope API rather than through
    /// <c>ParseQualifiedName</c> — which is exactly why it keeps working after the fix, when
    /// the parser will no longer hand anyone the second string.
    ///
    /// <para>Two scope strings differing only by a trailing dot are ONE directory on disk, but
    /// two independent <c>CachedIndex</c> keys. Writing through the second leaves the first
    /// holding a stale index that is missing the entry — and because
    /// <c>SaveIndexUnsafe</c> deletes every <c>*.meta.json</c> not present in the entry list it
    /// is handed, a later save driven by that stale list would delete the other scope's
    /// sidecar. That is the data-loss path behind the aliasing.</para>
    /// </summary>
    [Fact]
    public void TwoScopeStrings_DifferingOnlyByTrailingDot_ShareOneDirectoryAndDesyncTheirCaches()
    {
        const string cleanScope = "local:aliasdemo";
        const string aliasScope = "local:aliasdemo.";

        // One directory, two names for it.
        string cleanDir = _store.GetStoreDirForScope(cleanScope);
        string aliasDir = _store.GetStoreDirForScope(aliasScope);
        aliasDir.Should().NotBe(cleanDir,
            because: "the two scope strings produce two different directory PATH strings");

        _store.Upsert(MakeEntry("alpha"), cleanScope);
        _store.Upsert(MakeEntry("beta"), aliasScope);

        // Both sidecars landed in the SAME real directory — the paths are aliases.
        Directory.GetFiles(cleanDir, "*.meta.json").Select(Path.GetFileName)
            .Should().BeEquivalentTo(["alpha.meta.json", "beta.meta.json"],
                because: "'aliasdemo.' and 'aliasdemo' are one directory on this filesystem");

        // But the caches disagree: the clean scope's CachedIndex was never invalidated by the
        // write that went through the alias, so it still reports only its own entry.
        _store.LoadIndex(cleanScope).Select(e => e.Name)
            .Should().BeEquivalentTo(["alpha"],
                because: "writing through the aliased scope string did not invalidate this "
                       + "scope's cache — the stale list is what a later SaveIndex would use "
                       + "as its orphan-sweep allow-list, deleting 'beta' from disk");
        _store.LoadIndex(aliasScope).Select(e => e.Name)
            .Should().BeEquivalentTo(["alpha", "beta"]);
    }
}
