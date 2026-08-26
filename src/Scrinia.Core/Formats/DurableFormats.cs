namespace Scrinia.Core.Formats;

/// <summary>
/// Every durable format this project reads back, with its version contract.
///
/// <para><b>Adding a format to this file is not optional.</b> A durable format with no
/// contract is exactly the state the 2026-08-21 review found: three formats with no version,
/// two writing a version nothing read, and two that were never enumerated at all. The
/// registry-count test in <c>FormatVersionTests</c> is what makes the omission fail loudly.</para>
///
/// <para><c>config.json</c> is deliberately ABSENT: it is a free-form
/// <c>Dictionary&lt;string,string&gt;</c>, so unknown keys already survive a round-trip and
/// there is no schema to version. Recorded so its absence does not read as an oversight.</para>
/// </summary>
public static class DurableFormats
{
    /// <summary>The <c>.meta.json</c> artifact sidecar — the store's source of truth. Newer
    /// files load read-only: discarding is not available for the only copy of the data.</summary>
    public static readonly FormatContract ArtifactSidecar =
        new("artifact-sidecar", Current: 1, AbsentMeans: 1,
            FormatDisposition.Load, FormatDisposition.LoadReadOnly);

    /// <summary><c>facts.json</c> — a rebuildable cache. Rebuilds in BOTH directions because
    /// its version tracks the derivation rule, not the file shape. AbsentMeans is 0 rather
    /// than null because Dto.Version is a non-nullable int, so a missing key deserializes to
    /// 0 and must route through OnOlder to Rebuild — preserving today's behaviour exactly.</summary>
    public static readonly FormatContract EntityFacts =
        new("entity-facts", Current: 3, AbsentMeans: 0,
            FormatDisposition.Rebuild, FormatDisposition.Rebuild);

    /// <summary>The NMP/2 artifact body. Its version is the header sentinel, already enforced
    /// by the parser; this contract documents and tests that behaviour rather than replacing
    /// it. A body with no sentinel is not an old artifact, it is not an artifact.</summary>
    public static readonly FormatContract Nmp2Body =
        new("nmp2-body", Current: 2, AbsentMeans: null,
            FormatDisposition.Refuse, FormatDisposition.Refuse);

    /// <summary>
    /// An <c>ArtifactEntry</c> read out of a BUNDLE, as opposed to off disk.
    ///
    /// <para>Same bytes as <see cref="ArtifactSidecar"/>, deliberately different OnNewer. Reading
    /// a newer sidecar in place is <c>LoadReadOnly</c> because discarding is not available for the
    /// only copy of the data. A bundle entry has no "in place": importing it means writing a fresh
    /// copy into this store under rules this build cannot know it is reproducing faithfully, and
    /// refusing costs the user a re-export rather than their data.</para>
    ///
    /// <para>This exists as a CONTRACT rather than as a hand-placed <c>&gt; Current</c> comparison
    /// at the import site, because a policy that lives in one readable table is the entire point
    /// of this registry. A context whose answer differs declares a contract; it does not bypass
    /// the gate.</para>
    /// </summary>
    public static readonly FormatContract BundleEntry =
        new("bundle-entry", Current: ArtifactSidecar.Current, AbsentMeans: 1,
            FormatDisposition.Load, FormatDisposition.Refuse);

    /// <summary>A bundle's <c>manifest.json</c>. Transport, not storage — refusing a newer
    /// bundle costs the user a re-export, not their data.
    ///
    /// <para><b>Current is 2, not 1.</b> WriteManifest already writes <c>hasFileEntities ? 2 : 1</c>,
    /// so Current:1 with OnNewer:Refuse would make this build refuse bundles it had itself just
    /// exported. The 1/2 pair is a CAPABILITY marker — 2 means "carries file entities" — which
    /// happens to be monotonic, so a linear contract models it. Anything above 2 is genuinely
    /// newer.</para></summary>
    public static readonly FormatContract BundleManifest =
        new("bundle-manifest", Current: 2, AbsentMeans: 1,
            FormatDisposition.Load, FormatDisposition.Refuse);

    /// <summary>The legacy <c>index.json</c>. No longer written; read only by the layout
    /// migration. The contract is documentary — a "newer" one cannot exist.
    ///
    /// <para>AbsentMeans is 3 and is UNREACHABLE: IndexFile.Version is a non-nullable int with a
    /// `= 3` property initializer, so a missing key deserializes to 3, never to absent. Set to 3
    /// so the contract states something true.</para></summary>
    public static readonly FormatContract LegacyIndex =
        new("legacy-index", Current: 3, AbsentMeans: 3,
            FormatDisposition.Load, FormatDisposition.Load);

    /// <summary><c>.scrinia/skills/{name}.meta.json</c>.</summary>
    public static readonly FormatContract SkillMeta =
        new("skill-meta", Current: 1, AbsentMeans: 1,
            FormatDisposition.Load, FormatDisposition.LoadReadOnly);

    /// <summary>An agent config file's <c>.meta.json</c>.</summary>
    public static readonly FormatContract AgentMeta =
        new("agent-meta", Current: 1, AbsentMeans: 1,
            FormatDisposition.Load, FormatDisposition.LoadReadOnly);

    /// <summary>A scope-local restore instruction. Any version this build cannot execute must
    /// remain in place rather than being guessed at or discarded.</summary>
    public static readonly FormatContract RestoreJournal =
        new("restore-journal", Current: 1, AbsentMeans: null,
            FormatDisposition.Refuse, FormatDisposition.Refuse);

    public static IReadOnlyList<FormatContract> All { get; } =
    [
        ArtifactSidecar, BundleEntry, EntityFacts, Nmp2Body, BundleManifest, LegacyIndex, SkillMeta, AgentMeta,
        RestoreJournal
    ];
}
