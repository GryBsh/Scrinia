using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Encoding;
using Scrinia.Core.Bundles;
using Scrinia.Core.Formats;
using Scrinia.Core.Migration;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;

namespace Scrinia.Core;

internal enum ScopeAdmissionPurpose
{
    Mutation,
    VerifiedReadPreparation,
    RecoveryCleanup
}

internal enum RestoreJournalTransition
{
    BeforeRecoveryBarrierRead,
    BeforePreparedWrite,
    AfterPreparedWrite,
    BeforeReplayBody,
    BeforeReplaySidecar,
    AfterReplayDestinations,
    BeforeCompletedWrite,
    AfterCompletedWrite,
    BeforeCleanupDelete
}

/// <summary>
/// Instance-based <see cref="IMemoryStore"/> backed by the filesystem.
/// Each instance is scoped to a workspace root directory.
///
/// Naming convention (v3 — see <see cref="PathRouter.ToStoreDir"/>):
///   "subject"              → local scope:   {workspace}/.scrinia/memories/subject.nmp2
///   "/topic/subject"       → local topic:   {workspace}/.scrinia/memories/topic/subject.nmp2
///   "~subject"             → ephemeral:     in-memory only (dies with instance)
///
/// The ephemeral alias (an incoming path starting with the reserved /temp/ prefix) is resolved
/// to this ~subject form at the surfaces — MemoryTools.cs and ScriniaCommands.Memory.cs — before
/// the store is ever reached; this store only ever sees the ~subject spelling.
/// </summary>
public sealed partial class FileMemoryStore : IMemoryStore, IDisposable, IAddressResolver
{
    private static readonly AsyncLocal<Func<AppendFaultPoint, string, string, Exception?>?> AppendFaultInjectorContext = new();
    private static readonly AsyncLocal<Func<WholeMemoryFaultPoint, string, Exception?>?> WholeMemoryFaultInjectorContext = new();

    internal static Func<AppendFaultPoint, string, string, Exception?>? AppendFaultInjector
    {
        get => AppendFaultInjectorContext.Value;
        set => AppendFaultInjectorContext.Value = value;
    }

    internal static Func<WholeMemoryFaultPoint, string, Exception?>? WholeMemoryFaultInjector
    {
        get => WholeMemoryFaultInjectorContext.Value;
        set => WholeMemoryFaultInjectorContext.Value = value;
    }

    internal static IReadOnlyList<AppendFaultPoint> PreCommitFaultPoints { get; } =
    [
        AppendFaultPoint.RawArtifactRead,
        AppendFaultPoint.RawSidecarRead,
        AppendFaultPoint.JournalWrite,
        AppendFaultPoint.JournalFlush,
        AppendFaultPoint.JournalParse,
        AppendFaultPoint.JournalDelete,
        AppendFaultPoint.ArchiveArtifactWrite,
        AppendFaultPoint.ArchiveArtifactFlush,
        AppendFaultPoint.ArchiveSidecarWrite,
        AppendFaultPoint.ArchiveSidecarFlush,
        AppendFaultPoint.LiveArtifactWrite,
        AppendFaultPoint.LiveArtifactFlush,
        AppendFaultPoint.LiveSidecarWrite,
        AppendFaultPoint.LiveSidecarFlush,
        AppendFaultPoint.VerificationRead,
        AppendFaultPoint.RollbackWrite,
        AppendFaultPoint.RollbackDelete
    ];

    internal void FailOnceAt(AppendFaultPoint point) =>
        B0FaultSeam.FailOnceAt(B0FaultSeam.MapAppendFaultPoint(point), point.ToString());

    internal void FailOnceAt(WholeMemoryFaultPoint point) =>
        B0FaultSeam.FailOnceAt(B0FaultSeam.MapWholeMemoryFaultPoint(point), point.ToString());

    internal void FailPersistentlyAt(AppendFaultPoint point) =>
        B0FaultSeam.FailPersistentlyAt(B0FaultSeam.MapAppendFaultPoint(point), point.ToString());

    internal void ClearFaults()
    {
        B0FaultSeam.ClearFaults();
        AppendFaultInjector = null;
        WholeMemoryFaultInjector = null;
    }

    internal static Exception? InjectAppendFault(AppendFaultPoint point, string operationId, string path) =>
        B0FaultSeam.ThrowIfInjectedOrNull(B0FaultSeam.MapAppendFaultPoint(point), point.ToString())
        ?? AppendFaultInjector?.Invoke(point, operationId, path);

    internal static void InjectWholeFault(WholeMemoryFaultPoint point, string path)
    {
        Exception? failure = B0FaultSeam.ThrowIfInjectedOrNull(
            B0FaultSeam.MapWholeMemoryFaultPoint(point), point.ToString());
        if (failure is not null)
            throw failure;
        failure = WholeMemoryFaultInjector?.Invoke(point, path);
        if (failure is not null)
            throw failure;
    }

    internal string WholeMemoryJournalPath(string scope) =>
        AppendJournalCodec.GetJournalPath(Path.Combine(GetStoreDirUnderLease(scope), "versions"));

    /// <summary>Test-only crash seam around journal durable transitions.</summary>
    internal static AsyncLocal<Func<RestoreJournalTransition, string, Exception?>?> RestoreJournalFailureInjector { get; } = new();
    internal Action? MaintenancePageServiceCreationBarrierForTest
    {
        get => _maintenancePageServiceCache.MaintenancePageServiceCreationBarrierForTest;
        set => _maintenancePageServiceCache.MaintenancePageServiceCreationBarrierForTest = value;
    }
    /// <summary>Test-only seam receiving the exact bytes successfully written to a derived file.</summary>
    private static readonly object WriteObserverGate = new();
    private static Action<string, byte[]>? WriteObserverSlot;
    private static Action<string, long>? ReadObserverSlot;
    internal static Action<string, byte[]>? WriteObserver
    {
        get { lock (WriteObserverGate) return WriteObserverSlot; }
        set { lock (WriteObserverGate) WriteObserverSlot = value; }
    }

    internal static Action<string, long>? ReadObserver
    {
        get { lock (WriteObserverGate) return ReadObserverSlot; }
        set { lock (WriteObserverGate) ReadObserverSlot = value; }
    }

    private const int MaxEphemeralEntries = 1000;
    private readonly string _workspaceRoot;
    private readonly WorkspaceAdmission _workspaceAdmission;
    private readonly WholeMemoryTransactionCoordinator _wholeMemoryTransactions;
    private readonly FileEntityTransactionCoordinator _fileEntityTransactions;
    private readonly MultiMemoryTransactionCoordinator _multiMemoryTransactions;
    private readonly IMultiMemoryParticipantAdapterFactory? _participantAdapterFactory;
    private readonly RecoveryDiscoveryCoordinator _recoveryDiscovery = new();
    private readonly MaintenanceRunRegistry _maintenanceRuns = new();
    private readonly MaintenancePageServiceCache _maintenancePageServiceCache;
    private readonly MemoryValidationOptions _writeValidationOptions;
    private readonly ConcurrentDictionary<string, EphemeralEntry> _ephemeralStore = new(StringComparer.OrdinalIgnoreCase);
    private readonly ScopeLockManager _scopeLockManager;
    internal Action<string, string>? IndexProjectionObserverForTest { get; set; }
    private readonly ScopeIndexCache _scopeIndexCache;
    private readonly ConcurrentDictionary<string, string> _scopeSnapshotFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly TopicDiscoveryService _topicDiscoveryService;
    private readonly ArchiveService _archiveService;
    private readonly AppendJournalWriter _appendJournalWriter;
    private readonly AppendRecoveryEnforcer _recoveryEnforcer;
    private readonly AppendArchiveReplayer _archiveReplayer;
    private readonly AppendMemberVerifier _memberVerifier;
    private readonly AppendRollbackExecutor _rollbackExecutor;
    private readonly AppendRecoveryConductor _recoveryConductor;

    internal byte[]? LastCapturedOldArtifactBytesForTest { get; private set; }
    internal byte[]? LastCapturedOldSidecarBytesForTest { get; private set; }
    internal AppendTransaction? LastPreparedAppendTransactionForTest { get; private set; }

    // Gate for the one-time v3 layout migration check — see MemoriesRoot below.
    private volatile bool _layoutChecked;
    private readonly object _layoutGate = new();

    private readonly EntityFactMaterializer _factMaterializer;

    /// <summary>Whether the materialized facts view still has a sidecar flush pending or failed.</summary>
    internal bool HasDirtyFactCache
    {
        get => _factMaterializer.IsDirty;
    }

    internal int CommittedScopeInvalidationCount { get; private set; }
    internal int RollbackScopeInvalidationCount { get; private set; }

    /// <summary>
    /// LRU cache for decoded artifact text, bounded by total byte size.
    /// Thread-safe via internal locking.
    /// </summary>
    private sealed class ArtifactLruCache
    {
        private readonly int _maxBytes;
        private int _currentBytes;
        private readonly Dictionary<string, LinkedListNode<(string Key, string Value)>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<(string Key, string Value)> _order = new();
        private readonly object _lock = new();

        public ArtifactLruCache(int maxBytes) => _maxBytes = maxBytes;

        public bool TryGet(string key, out string value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = "";
            return false;
        }

        public void Set(string key, string value)
        {
            int size = value.Length * 2; // approximate byte size (UTF-16)
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _currentBytes -= existing.Value.Value.Length * 2;
                    _order.Remove(existing);
                    _map.Remove(key);
                }

                while (_currentBytes + size > _maxBytes && _order.Count > 0)
                {
                    var last = _order.Last!;
                    _currentBytes -= last.Value.Value.Length * 2;
                    _map.Remove(last.Value.Key);
                    _order.RemoveLast();
                }

                var node = _order.AddFirst((key, value));
                _map[key] = node;
                _currentBytes += size;
            }
        }

        public void Invalidate(string keyPrefix)
        {
            lock (_lock)
            {
                var toRemove = _map.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var key in toRemove)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _currentBytes -= node.Value.Value.Length * 2;
                        _order.Remove(node);
                        _map.Remove(key);
                    }
                }
            }
        }
    }

    // 50 MB artifact LRU cache
    private readonly ArtifactLruCache _artifactCache = new(50 * 1024 * 1024);

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(IndexFile))]
    [JsonSerializable(typeof(ArtifactEntry))]
    [JsonSerializable(typeof(MemoryAddress))]
    [JsonSerializable(typeof(MemoryAddressKind))]
    [JsonSerializable(typeof(MemoryWrite))]
    [JsonSerializable(typeof(MemoryError))]
    [JsonSerializable(typeof(MemoryAddressError))]
    [JsonSerializable(typeof(MemorySearchRequest))]
    [JsonSerializable(typeof(MemoryListRequest))]
    [JsonSerializable(typeof(LegacyArtifactEntry))]
    private partial class FileStoreJsonContext : JsonSerializerContext;

    /// <summary>
    /// Tunable ranker weights — read by <see cref="SearchService"/>
    /// when constructing the underlying <see cref="WeightedAttributeScorer"/>. Set at
    /// construction; <c>WorkspaceSetup.Configure</c> passes the workspace's
    /// <c>Scrinia:Search:*</c> config-derived values.
    /// </summary>
    public RankerOptions RankerOptions { get; init; } = RankerOptions.Default;

    public FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions = null,
        MemoryValidationOptions? writeValidationOptions = null)
        : this(workspaceRoot, rankerOptions, writeValidationOptions, null)
    {
    }

    internal FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions,
        MemoryValidationOptions? writeValidationOptions,
        IMultiMemoryParticipantAdapterFactory? participantAdapterFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _workspaceAdmission = new WorkspaceAdmission(_workspaceRoot,
            (lease, ct) =>
            {
                try { BundleRestoreCoordinator.RecoverUnderLease(lease, BundleReadLimits.Default, ct); }
                finally { InvalidateCaches(); }
            });
        _scopeLockManager = new ScopeLockManager();
        _scopeIndexCache = new ScopeIndexCache();
        _maintenancePageServiceCache = new MaintenancePageServiceCache(_recoveryDiscovery, _maintenanceRuns);
        _topicDiscoveryService = new TopicDiscoveryService(() => MemoriesRoot);
        _wholeMemoryTransactions = new WholeMemoryTransactionCoordinator(this);
        _fileEntityTransactions = new FileEntityTransactionCoordinator(_workspaceRoot);
        _participantAdapterFactory = participantAdapterFactory;
        _multiMemoryTransactions = participantAdapterFactory is null
            ? new MultiMemoryTransactionCoordinator(this, ResolveMultiMemoryParticipantAsync)
            : new MultiMemoryTransactionCoordinator(this, ResolveMultiMemoryParticipantAsync,
                participantAdapterFactory);
         RankerOptions = rankerOptions ?? RankerOptions.Default;
        _archiveService = new ArchiveService(
            (name, scope) => ArtifactPath(name, scope),
            scope => GetStoreDirUnderLease(scope),
            name => SanitizeName(name),
            (name, scope) => MetaPath(name, scope),
            InjectAppendFault);
        _appendJournalWriter = new AppendJournalWriter(
            scope => GetStoreDirUnderLease(scope),
            InjectAppendFault);
        _recoveryEnforcer = new AppendRecoveryEnforcer(_appendJournalWriter);
        _archiveReplayer = new AppendArchiveReplayer(
            InjectAppendFault, _appendJournalWriter);
        _memberVerifier = new AppendMemberVerifier(
            InjectAppendFault);
        _rollbackExecutor = new AppendRollbackExecutor(
            InjectAppendFault);
        _recoveryConductor = new AppendRecoveryConductor(
            InjectAppendFault, _archiveReplayer, _appendJournalWriter, _recoveryEnforcer, _archiveService, _rollbackExecutor);
        _writeValidationOptions = writeValidationOptions ?? new MemoryValidationOptions();
         _factMaterializer = new EntityFactMaterializer(
            items => items.Select(EntityFactDerivation.Derive).Where(memory => memory is not null).Select(memory => memory!).ToArray(),
            ResolveFactSidecarPath,
            TryWriteAllText,
             BackgroundWork.Register);

    }

    internal static void EnsureGitIgnore(string workspaceRoot)
    {
        string scriniaDir = Path.Combine(workspaceRoot, ".scrinia");
        Directory.CreateDirectory(scriniaDir);

        string gitIgnorePath = Path.Combine(scriniaDir, ".gitignore");
        if (File.Exists(gitIgnorePath)) return;

        try
        {
            WriteDefaultGitIgnore(gitIgnorePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Benign first-write race. EnsureMaterialized calls this from OUTSIDE the scope's
            // file lock and write lock, so two concurrent writers to a fresh workspace both pass
            // the File.Exists check above and both File.Move onto the same destination. On
            // Windows the loser gets ERROR_ACCESS_DENIED (UnauthorizedAccessException), which
            // FileRetry classifies as permanent and will not retry — the same collision the
            // sidecar write-back documents in LoadIndexUnsafe. The content is a fixed constant,
            // so whichever move landed left the file correct. Rethrow only if it really is absent.
            // See FileLock.Acquire IsTransientIoException — only ERROR_SHARING_VIOLATION /
            // ERROR_LOCK_VIOLATION retry; UnauthorizedAccessException (ERROR_ACCESS_DENIED)
            // from File.Move collision is benign success, not a retry.
            if (!File.Exists(gitIgnorePath)) throw;
        }
    }

    private static void WriteDefaultGitIgnore(string gitIgnorePath) =>
        DurableWriter.WriteDefaultGitIgnore(gitIgnorePath);

    // ── Naming ───────────────────────────────────────────────────────────────

    internal (string Scope, string Subject) ParseQualifiedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        // v2 path syntax: starts with "/"
        if (name.StartsWith('/'))
        {
            var parsed = PathParser.Parse(name, new HashSet<string>());

            var segments = parsed.Segments;
            if (segments.Count == 0)
                throw new ArgumentException("Empty path", nameof(name));

            if (segments.Count == 1)
                return ("local", SanitizeName(segments[0].Value));

            // Build scope from all but last segment, through the ONE scope constructor
            // (MemoryNaming.BuildScopedTopicScope), which sanitizes and validates each segment.
            // This is a WRITE path, so a topic that cannot be constructed throws rather than
            // resolving to something else: before Task 13 the scope was built from RAW segments
            // while only the leaf was sanitized, so "local:topic.", "local:topic " and
            // "local:topic" were three distinct scope strings — three CachedIndex keys and three
            // ReaderWriterLockSlim instances — over ONE directory.
            string scope = MemoryNaming.BuildScopedTopicScope(
                segments.Take(segments.Count - 1).Select(s => s.Value));
            string subject = SanitizeName(segments[^1].Value);
            return (scope, subject);
        }

        // A bare name is a local memory; the removed "topic:subject" form is rejected.
        MemoryNaming.RejectLegacyColonSyntax(name);
        return ("local", SanitizeName(name.Trim()));
    }

    public string FormatQualifiedName(string scope, string subject) =>
        MemoryNaming.FormatQualifiedName(scope, subject);

    internal bool IsEphemeral(string name) =>
        MemoryNaming.IsEphemeralName(name);

    public (string Scope, string Subject) ResolveAddress(MemoryAddress address) =>
        MemoryNaming.ResolveAddress(address);

    public string SanitizeName(string name) => MemoryNaming.SanitizeName(name);

    public ValueTask<ArtifactEntry?> ReadAsync(MemoryAddress address, CancellationToken ct = default)
    {
        var (scope, subject) = ResolveAddress(address);
        if (MemoryArtifactSource.IsEphemeral(scope))
        {
            var ephemeral = GetEphemeral(subject);
            return ValueTask.FromResult<ArtifactEntry?>(ephemeral is null
                ? null
                : ephemeral.ToArtifactEntry() with { Address = address });
        }

        return ValueTask.FromResult(WithRecoveryReadBarrier(scope, () =>
        {
            if (!File.Exists(ArtifactPath(subject, scope)))
                return (ArtifactEntry?)null;
            return MemoryNaming.ResolveExistingName(LoadIndexUnderRecoveryBarrier(scope), address);
        }, ct));
    }

    public async ValueTask<ArtifactEntry> WriteAsync(MemoryAddress address, MemoryWrite write,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.Address != address)
            throw new ArgumentException("The write address must match the requested address.", nameof(write));
        var (scope, subject) = ResolveAddress(address);
        return await StoreAsync(write.Readdress(address, scope, subject), ct).ConfigureAwait(false);
    }

    public ValueTask DeleteAsync(MemoryAddress address, CancellationToken ct = default)
    {
        var (scope, subject) = ResolveAddress(address);
        if (MemoryArtifactSource.IsEphemeral(scope))
            ForgetEphemeral(subject);
        else
        {
            using WorkspaceLease lease = EnterWorkspaceMutation(ct: ct);
            WithExclusiveLock(lease, scope, () =>
            {
                RemoveUnderLease(lease, subject, scope);
                return DeleteArtifactUnsafe(subject, scope);
            }, cancellationToken: ct);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Static core of <see cref="SanitizeName"/> — no instance state, so it is also
    /// usable from <see cref="WriteSidecarCore"/>/<see cref="ExpandLegacyIndexEntries"/>, which
    /// run without a <see cref="FileMemoryStore"/> instance (migration time).
    ///
    /// <para>Task 13 moved the body to <see cref="MemoryNaming.SanitizeSegment"/> so the LEAF and
    /// the SCOPE share one character rule rather than two that could drift. Note what is NOT
    /// shared: the scope path also rejects what substitution cannot repair (a trailing dot or
    /// space, a bare ".", NUL), and that rejection deliberately does not reach here. The leaf has
    /// no aliasing defect to fix — <see cref="ArtifactPath"/>/<see cref="MetaPath"/> append
    /// <c>.nmp2</c>/<c>.meta.json</c> AFTER sanitizing, so the path component never ends in the
    /// name's trailing dot or space (<c>notes.</c> → <c>notes..nmp2</c>, distinct from
    /// <c>notes.nmp2</c>; measured) — and, decisively, this runs against names READ BACK FROM
    /// DISK: <see cref="WriteSidecarCore"/>, <see cref="DeleteSidecar"/>,
    /// <see cref="SaveIndexUnsafe"/>'s orphan-sweep allow-list, the case-insensitive entry
    /// matchers, and <see cref="ExpandLegacyIndexEntries"/> at migration time. Throwing here
    /// would let one stored entry poison every read of its scope, and abort the orphan sweep
    /// part-way through a <see cref="SaveIndex"/> that deletes files.</para></summary>
    private static string SanitizeNameCore(string name) => MemoryNaming.SanitizeName(name);

    // ── Ephemeral memory ─────────────────────────────────────────────────────

    [Obsolete("Use MemoryNaming.StripEphemeralPrefix instead.")]
    public static string StripEphemeralPrefix(string name) =>
        MemoryNaming.StripEphemeralPrefix(name);

    internal void RememberEphemeral(string key, EphemeralEntry entry)
    {
        _ephemeralStore[key] = entry;

        if (_ephemeralStore.Count > MaxEphemeralEntries)
        {
            // Evict oldest by CreatedAt
            var oldest = _ephemeralStore.OrderBy(e => e.Value.CreatedAt).First();
            _ephemeralStore.TryRemove(oldest.Key, out _);
        }

        InvalidateEntityFacts();
    }

    internal bool ForgetEphemeral(string key)
    {
        if (!_ephemeralStore.TryRemove(key, out _)) return false;
        InvalidateEntityFacts();
        return true;
    }

    internal EphemeralEntry? GetEphemeral(string key) =>
        _ephemeralStore.TryGetValue(key, out var entry) ? entry : null;

    /// <inheritdoc/>
    /// <remarks>
    /// Compare-and-swap over <c>_ephemeralStore</c>, replacing the interface default's
    /// unsynchronized read-modify-write. <c>ConcurrentDictionary.TryUpdate</c> swaps only if the
    /// stored value is still the one this loop read; if a concurrent writer won, the mutation is
    /// re-applied to that newer entry instead of clobbering it.
    ///
    /// <para><b>Why record equality is a safe comparison here.</b> Not because it is an identity
    /// check — for an entry whose six collection members are all null (a plain memory with no
    /// attributes, keywords or chunk entries) <c>Equals</c> is pure scalar comparison and reference
    /// identity plays no part at all. The guarantee is stronger and simpler than that:
    /// <see cref="EphemeralEntry"/> is a SEALED record holding no state outside its members, so
    /// <c>TryUpdate</c> can only match a value other than the one this loop read if that value is
    /// MEMBER-WISE EQUAL to it — and a member-wise equal entry is one with nothing to lose, so
    /// swapping over it discards nothing. Any writer that genuinely changed something produces an
    /// entry that differs in at least one member and is therefore correctly rejected. (This is the
    /// one deliberate USE of that equality; see the footgun note on <see cref="ArtifactEntry"/>,
    /// which records that nothing relied on it — still true of <c>ArtifactEntry</c>.)</para>
    ///
    /// <para>Deliberately not routed through <see cref="RememberEphemeral"/>, for two reasons. It
    /// adds-or-replaces and then evicts when over capacity, where a swap leaves the entry count
    /// unchanged so no eviction can be owed; and it would RESURRECT the memory if
    /// <see cref="ForgetEphemeral"/> removed the key between this loop's read and its write.
    /// <c>TryUpdate</c> refuses a missing key, so a forgotten memory stays forgotten and the caller
    /// gets false.</para>
    ///
    /// <para>The fact-store invalidation tail matches every other mutating write path here
    /// (<c>SaveIndex</c>, <c>Remove</c>, <c>TryUpdateEntry</c>): an ephemeral entry's attributes are
    /// part of <see cref="GetEntityFactStore"/>'s merged view, which <see cref="ListScoped(IReadOnlyList{string}?)"/>
    /// builds INCLUDING the ephemeral scope, so a swap that did not invalidate would leave an
    /// already-built fact store blind to it for the rest of the session. Taken alone — no index lock
    /// is held here — so it cannot invert the documented (index lock, then fact-store lock)
    /// order.</para>
    /// </remarks>
    public bool TryUpdateEphemeral(string key, Func<EphemeralEntry, EphemeralEntry?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        while (true)
        {
            if (!_ephemeralStore.TryGetValue(key, out var current)) return false;

            var updated = mutate(current);
            if (updated is null) return false;

            if (_ephemeralStore.TryUpdate(key, updated, current))
            {
                InvalidateEntityFacts();
                return true;
            }
        }
    }

    public List<ScopedArtifact> ListEphemeral()
    {
        var result = new List<ScopedArtifact>();
        foreach (var kvp in _ephemeralStore)
        {
            // EphemeralEntry.ToArtifactEntry, not a copy of it written out here. This projection
            // and PersistEphemeral's are two views of ONE memory, and a field present in one and
            // null in the other means the answer depends on which read you came through — the
            // reason Keywords and AgentKeywords both had to be added to it after the fact.
            result.Add(new ScopedArtifact("ephemeral", ProjectEphemeralEntry(kvp.Value)));
        }
        return result;
    }

    // ── Paths ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The root every persistent path resolves under. Runs the one-time v3 layout migration
    /// on first use, which is why every path computation must flow through here — a method
    /// that builds a path another way would read a store that has not been migrated yet.
    ///
    /// <para>Double-checked locking, not a bare <c>Interlocked.Exchange</c> flag: a flag alone
    /// only stops a SECOND migration attempt — it does not stop a second (or 64th) concurrent
    /// caller from reading <c>memories/</c> WHILE the winning thread's migration is still moving
    /// files. <see cref="_layoutGate"/> makes every concurrent first-touch caller block until the
    /// one migration attempt actually finishes, so nobody ever observes a half-migrated tree.</para>
    /// </summary>
    private string MemoriesRoot => Path.Combine(_workspaceRoot, ".scrinia", "memories");

    private void EnsureLayoutUnderLease(WorkspaceLease lease)
    {
        lease.AssertValid(_workspaceRoot);
        if (_layoutChecked) return;
        lock (_layoutGate)
        {
            if (_layoutChecked) return;
            if (StoreLayoutMigration.NeedsMigration(_workspaceRoot))
            {
                StoreLayoutMigration.MigrateIfNeeded(_workspaceRoot);
                InvalidateCaches();
            }
            _layoutChecked = true;
        }
    }

    public string GetStoreDirForScope(string scope) =>
        WithWorkspaceSnapshot(lease => GetStoreDirUnderLease(scope));

    private string GetStoreDirUnderLease(string scope) => PathRouter.ToStoreDir(scope, _workspaceRoot);

    public string WorkspaceRoot => _workspaceRoot;

    internal string ArtifactPath(string name, string scope = "local") =>
        MapArtifactKey(ArtifactIdentityMapper.BuildArtifactKey(SanitizeName(name), scope));

    internal string MetaPath(string name, string scope = "local") =>
        Path.Combine(GetStoreDirUnderLease(scope), SanitizeName(name) + ".meta.json");

    public string FindArtifactPath(string subject, string normalizedScope) =>
        WithWorkspaceSnapshot(lease => ArtifactPath(subject, normalizedScope));

    // ── Scope helpers ────────────────────────────────────────────────────────

    internal IReadOnlyList<string> NormalizeScopeFilters(string? token) =>
        token?.Trim().StartsWith('/') == true
            ? WithWorkspaceSnapshot(lease => lease.CapturedAbsentWorkspace ? [] : _topicDiscoveryService.NormalizeScopeFilters(token))
            : _topicDiscoveryService.NormalizeScopeFilters(token);

    public IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null) =>
        IsEphemeralOnly(scopes) ? [] :
        WithWorkspaceSnapshot(lease => lease.CapturedAbsentWorkspace ? [] : _topicDiscoveryService.ResolveReadScopes(scopes));

    // ── CRUD ─────────────────────────────────────────────────────────────────

    private string GetLockPath(string scope) =>
        Path.Combine(GetStoreDirUnderLease(scope), ".lock");

    internal async Task<T> WithExclusiveScopeAdmissionAsync<T>(string scope, TimeSpan timeout,
        Func<Task<T>> body, CancellationToken cancellationToken, bool recoverCoordinator = true,
        ScopeAdmissionPurpose purpose = ScopeAdmissionPurpose.Mutation)
    {
        using WorkspaceLease lease = EnterWorkspaceMutation(timeout, cancellationToken);
        return await WithExclusiveScopeAdmissionAsync(lease, scope, timeout, body, cancellationToken, recoverCoordinator, purpose).ConfigureAwait(false);
    }

    internal async Task<T> WithExclusiveScopeAdmissionAsync<T>(WorkspaceLease lease, string scope, TimeSpan timeout,
        Func<Task<T>> body, CancellationToken cancellationToken, bool recoverCoordinator = true,
        ScopeAdmissionPurpose purpose = ScopeAdmissionPurpose.Mutation)
    {
        lease.AssertValid(_workspaceRoot);
        ArgumentNullException.ThrowIfNull(body);
        AdmissionGate admissionGate = _scopeLockManager.AcquireAdmissionGate(scope);
        bool admitted = false;
        Stopwatch admissionTimer = Stopwatch.StartNew();
        try
        {
            bool acquired = await admissionGate.Semaphore.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!acquired)
                throw new FileLockTimeoutException(GetLockPath(scope), timeout);
            admitted = true;
            TimeSpan remaining = timeout - admissionTimer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new FileLockTimeoutException(GetLockPath(scope), timeout);
            EnsureMaterialized(scope);
            using FileLock fileLock = FileLock.AcquireExclusive(GetLockPath(scope), remaining, cancellationToken);
            bool retained = RecoverScopeUnsafe(lease, scope, GetStoreDirUnderLease(scope), recoverCoordinator, cancellationToken);
            if (purpose == ScopeAdmissionPurpose.Mutation)
                RefuseMutationWithRetainedEvidence(scope, GetStoreDirUnderLease(scope), retained, cancellationToken);
            return await body().ConfigureAwait(false);
        }
        finally
        {
            if (admitted)
                admissionGate.Semaphore.Release();
            _scopeLockManager.ReleaseAdmissionGate(scope, admissionGate);
        }
    }

    internal RecoveryDiscoveryResult DiscoverRecoveryForScope(string scope, string storeDirectory,
        CancellationToken cancellationToken = default) =>
        _recoveryDiscovery.Discover(scope, storeDirectory, cancellationToken: cancellationToken);

    internal MaintenanceDiscoveryPage DiscoverRecoveryPage(string scope, string storeDirectory,
        MaintenanceWorkspaceKey workspace, CancellationToken cancellationToken = default) =>
        _maintenancePageServiceCache.GetMaintenancePageService(scope, storeDirectory).ReadFirstPage(workspace, scope, cancellationToken);

    internal MaintenanceDiscoveryPage ContinueRecoveryPage(MaintenanceCursor cursor,
        string scope, string storeDirectory, CancellationToken cancellationToken = default) =>
        _maintenancePageServiceCache.GetMaintenancePageService(scope, storeDirectory).ReadNextPage(cursor, cancellationToken);

    internal MaintenanceLifecyclePage DiscoverMaintenancePage(string scope, string storeDirectory,
        MaintenanceWorkspaceKey workspace, CancellationToken cancellationToken = default) =>
        new MaintenanceDiscoveryLifecycle(_maintenancePageServiceCache.GetMaintenancePageService(scope, storeDirectory), _maintenanceRuns)
            .Start(workspace, scope, cancellationToken);

    internal MaintenanceLifecyclePage ContinueMaintenancePage(string scope, string storeDirectory,
        string continuationToken, CancellationToken cancellationToken = default) =>
        new MaintenanceDiscoveryLifecycle(_maintenancePageServiceCache.GetMaintenancePageService(scope, storeDirectory), _maintenanceRuns)
            .Continue(continuationToken, cancellationToken);

    private static string SafeRecoveryRelativePath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return MaintenanceContractValidation.RequireRelativePath(relative, nameof(path));
    }

    // B5 Task 6 — single lock-scoped maintenance admission seam. The only
    // production path that deletes maintenance candidates. Acquires the
    // existing scope boundary once (which runs the recovery barrier), plans
    // bounded work through MaintenancePlanner, revalidates each eligible
    // candidate under the lock immediately before deletion, deletes only
    // verified terminal recovery evidence with bounded retry, and invalidates
    // derived state only after a successful archive deletion.
    internal async Task<MaintenanceScopeExecution> ExecuteMaintenanceScopeAsync(
        string scope,
        Configuration.MaintenancePolicy policy,
        int remainingJournalBudget,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        MaintenanceContractValidation.RequireSafeText(scope, nameof(scope));
        ArgumentNullException.ThrowIfNull(policy);
        if (remainingJournalBudget is < 1 or > MaintenanceLimits.JournalBudgetMaximum)
            throw new ArgumentException("Remaining journal budget must be within 1..50.", nameof(remainingJournalBudget));
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= deadlineUtc)
            throw new TimeoutException("The maintenance deadline has expired.");

        TimeSpan remaining = deadlineUtc - DateTimeOffset.UtcNow;
        TimeSpan lockTimeout = remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
        if (lockTimeout <= TimeSpan.Zero)
            throw new TimeoutException("The maintenance deadline has expired.");

        try
        {
            using WorkspaceLease lease = EnterWorkspaceMutation(lockTimeout, cancellationToken);
            return await WithExclusiveScopeAdmissionAsync(
                lease, scope, lockTimeout,
                () => Task.FromResult(ExecuteMaintenanceScopeUnderLock(
                    lease, scope, remainingJournalBudget, deadlineUtc, cancellationToken)),
                cancellationToken, purpose: ScopeAdmissionPurpose.RecoveryCleanup).ConfigureAwait(false);
        }
        catch (AppendStoreException)
        {
            return RecoveryRetainedExecution(scope);
        }
        catch (RestoreRecoveryException)
        {
            return RecoveryRetainedExecution(scope);
        }
    }

    private MaintenanceScopeExecution ExecuteMaintenanceScopeUnderLock(
        WorkspaceLease lease,
        string scope,
        int remainingJournalBudget,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        lease.AssertValid(_workspaceRoot);
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= deadlineUtc)
            throw new TimeoutException("The maintenance deadline has expired.");

        string storeDir = GetStoreDirUnderLease(scope);
        MaintenanceCandidate[] discovered = _recoveryDiscovery
            .EnumerateMaintenanceCandidates(scope, storeDir, cancellationToken)
            .OrderBy(static candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ToArray();

        MaintenancePlannerCandidate[] plannerCandidates = new MaintenancePlannerCandidate[discovered.Length];
        var byKey = new Dictionary<MaintenanceCandidateKey, MaintenanceCandidate>(discovered.Length);
        for (int index = 0; index < discovered.Length; index++)
        {
            MaintenanceCandidate candidate = discovered[index];
            MaintenancePlannerCandidate plannerCandidate = ToPlannerCandidate(scope, candidate, (ulong)index);
            plannerCandidates[index] = plannerCandidate;
            byKey[plannerCandidate.CandidateKey] = candidate;
        }

        MaintenancePlannerPage page = MaintenancePlanner.Plan(
            plannerCandidates, remainingJournalBudget, deadlineUtc, cancellationToken: cancellationToken);

        var warnings = new List<string>();
        int deletedRecoveryEvidence = 0;
        int retained = 0;
        foreach (MaintenancePlannerProjection projection in page.Projections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadlineUtc)
                throw new TimeoutException("The maintenance deadline has expired.");
            if (!projection.Eligible)
            {
                retained++;
                continue;
            }
            if (!byKey.TryGetValue(projection.CandidateKey, out MaintenanceCandidate? candidate))
            {
                retained++;
                continue;
            }
            if (!IsStillDeletable(storeDir, candidate.RelativePath))
            {
                retained++;
                warnings.Add($"Scope '{scope}' retained a candidate: revalidation failed.");
                continue;
            }
            if (!TryDeleteBounded(Path.GetFullPath(Path.Combine(storeDir, candidate.RelativePath)), cancellationToken))
            {
                retained++;
                warnings.Add($"Scope '{scope}' retained a candidate: deletion failed.");
            }
            else
            {
                deletedRecoveryEvidence++;
            }
        }

        var scopeResult = new MaintenanceScopeResult(
            scope, page.Scanned, 0, deletedRecoveryEvidence, retained,
            false, false, null,
            Array.Empty<MaintenanceCandidateResult>(),
            warnings);
        return new MaintenanceScopeExecution(scopeResult, page.JournalInspected);
    }

    private static MaintenanceScopeExecution RecoveryRetainedExecution(string scope) =>
        new(new MaintenanceScopeResult(
            scope, 0, 0, 0, 1, false, false, null,
            Array.Empty<MaintenanceCandidateResult>(),
            new[] { $"Scope '{scope}' retained recovery evidence." }),
            0, true);

    private static MaintenancePlannerCandidate ToPlannerCandidate(
        string scope, MaintenanceCandidate candidate, ulong entryOrdinal)
    {
        byte[] subjectBytes = System.Text.Encoding.UTF8.GetBytes(scope + "\0" + candidate.Subject);
        byte[] identityBytes = System.Text.Encoding.UTF8.GetBytes(
            candidate.Kind + "\0" + scope + "\0" + candidate.Subject + "\0" + candidate.RelativePath + "\0" + candidate.OperationId);
        byte[] keyBytes = System.Security.Cryptography.SHA256.HashData(identityBytes)[..16];
        bool verified = candidate.VerificationOutcome == RecoveryVerificationOutcome.Verified;
        bool terminal = candidate.LifecycleState is RecoveryLifecycleState.Committed
            or RecoveryLifecycleState.CleanupPending or RecoveryLifecycleState.RolledBack;
        bool eligible = candidate.Protection is null && verified && terminal;
        return new MaintenancePlannerCandidate(
            0,
            candidate.Kind,
            new MaintenanceCandidateKey(new Guid(keyBytes)),
            new MaintenanceHash256(System.Security.Cryptography.SHA256.HashData(subjectBytes)),
            entryOrdinal,
            true,
            candidate.Protection is not null,
            candidate.Protection,
            eligible,
            eligible,
            true,
            false,
            false);
    }

    private static bool IsStillDeletable(string storeDir, string relativePath)
    {
        string root;
        string fullPath;
        try
        {
            root = Path.GetFullPath(storeDir);
            fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        if (string.Equals(root, fullPath, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            return false;
        if (!PathContainment.IsWithin(root, fullPath))
            return false;
        try
        {
            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        return File.Exists(fullPath);
    }

    private static bool TryDeleteBounded(string fullPath, CancellationToken cancellationToken) =>
        DurableWriter.TryDeleteBounded(fullPath, cancellationToken);

    private Task<IMultiMemoryParticipantAdapter> ResolveMultiMemoryParticipantAsync(
        string scope, MultiMemoryParticipantManifest manifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string storeDirectory = GetStoreDirUnderLease(scope);
        RecoveryDiscoveryResult discovery = _recoveryDiscovery.Discover(scope, storeDirectory,
            includeCoordinator: false, cancellationToken: cancellationToken);
        if (discovery.BlockingEvidence is not null)
            throw new MultiMemoryParticipantMismatchException();
        MultiMemoryTransactionRecord record = MultiMemoryTransactionRecordCodec.Read(storeDirectory, scope, cancellationToken);
        foreach (MultiMemoryParticipantManifest persistedManifest in record.Participants)
            _ = ResolveCandidate(discovery, persistedManifest);

        RecoveryCandidate? candidate = ResolveCandidate(discovery, manifest);
        if (candidate is null)
        {
            MultiMemoryParticipantCompletion? proof = record.CompletedParticipants?.FirstOrDefault(completion =>
                completion.Subject == manifest.Subject && completion.OperationId == manifest.OperationId &&
                completion.Kind == manifest.Kind);
            if (proof is null)
                throw new MultiMemoryParticipantMismatchException();
            return Task.FromResult<IMultiMemoryParticipantAdapter>(
                new CompletedProofParticipantAdapter(scope, manifest, proof));
        }
        return Task.FromResult<IMultiMemoryParticipantAdapter>(
            new FileMemoryParticipantAdapter(this, manifest, candidate));
    }

    private bool CandidateMatchesSubject(RecoveryCandidate candidate, string subject)
    {
        if (candidate.Operation == RecoveryOperation.Append && candidate.Envelope.Address is not null)
        {
            (string scope, string canonicalSubject) = ResolveAddress(MemoryAddress.Parse(candidate.Envelope.Address));
            return scope == candidate.Scope && canonicalSubject == subject;
        }
        return candidate.CanonicalSubject.EndsWith('/' + subject, StringComparison.Ordinal);
    }

    private RecoveryCandidate? ResolveCandidate(RecoveryDiscoveryResult discovery,
        MultiMemoryParticipantManifest manifest)
    {
        RecoveryCandidate[] matches = discovery.Candidates
            .Where(candidate => candidate.Operation is RecoveryOperation.Append or RecoveryOperation.Restore)
            .Where(candidate => CandidateMatchesSubject(candidate, manifest.Subject))
            .ToArray();
        if (matches.Any(candidate => candidate.OperationId != manifest.OperationId ||
            !CandidateKindMatches(candidate, manifest.Kind)))
            throw new MultiMemoryParticipantMismatchException();
        if (matches.Length > 1)
            throw new MultiMemoryParticipantMismatchException();
        return matches.SingleOrDefault();
    }

    private static bool CandidateKindMatches(RecoveryCandidate candidate, string kind) =>
        kind.Equals("memory", StringComparison.Ordinal) ||
        kind.Equals(candidate.Operation.ToString(), StringComparison.OrdinalIgnoreCase);

    internal Task<MultiMemoryAggregateResult> ExecuteMultiMemoryAsync(
        MultiMemoryTransactionRequest request,
        Func<MultiMemoryParticipantRequest, CancellationToken, Task<IMultiMemoryParticipantAdapter>> admitParticipant,
        CancellationToken cancellationToken = default) =>
        _multiMemoryTransactions.ExecuteAsync(request, (participant, token) =>
            participant.ParticipantIdentity is not null && _participantAdapterFactory is not null
                ? _participantAdapterFactory.CreateAsync(participant, token)
                : admitParticipant(participant, token), cancellationToken);

    /// <summary>
    /// Creates the workspace store and the scope's directory. Called only from write paths, so
    /// a workspace that is never written to leaves no directory behind.
    /// </summary>
    private void EnsureMaterialized(string scope)
    {
        EnsureGitIgnore(_workspaceRoot);
        Directory.CreateDirectory(GetStoreDirUnderLease(scope));
    }

    /// <summary>
    /// Runs <paramref name="body"/> under the scope's cross-process file lock and in-process
    /// write lock, materialising the store first. Every write goes through here so that
    /// "writes materialize, reads do not" holds in one place.
    /// </summary>
    private T WithExclusiveLock<T>(string scope, Func<T> body, TimeSpan? timeout = null,
        Action<bool>? afterRecovery = null, ScopeAdmissionPurpose purpose = ScopeAdmissionPurpose.Mutation)
    {
        using WorkspaceLease lease = EnterWorkspaceMutation(timeout: timeout);
        return WithExclusiveLock(lease, scope, body, timeout, afterRecovery, purpose);
    }

    private T WithExclusiveLock<T>(WorkspaceLease lease, string scope, Func<T> body, TimeSpan? timeout = null,
        Action<bool>? afterRecovery = null, ScopeAdmissionPurpose purpose = ScopeAdmissionPurpose.Mutation,
        CancellationToken cancellationToken = default)
    {
        lease.AssertValid(_workspaceRoot);
        EnsureMaterialized(scope);
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope), timeout ?? TimeSpan.FromSeconds(5), cancellationToken);
        var lk = _scopeLockManager.GetIndexLock(scope);
        lk.EnterWriteLock();
        try
        {
            bool completedJournalRetained = RecoverScopeUnsafe(lease, scope, GetStoreDirUnderLease(scope), cancellationToken: cancellationToken);
            afterRecovery?.Invoke(completedJournalRetained);
            if (purpose == ScopeAdmissionPurpose.Mutation)
                RefuseMutationWithRetainedEvidence(scope, GetStoreDirUnderLease(scope), completedJournalRetained, cancellationToken);
            return body();
        }
        finally
        {
            lk.ExitWriteLock();
        }
    }

    private void WithExclusiveLock(string scope, Action body, TimeSpan? timeout = null,
        Action<bool>? afterRecovery = null, ScopeAdmissionPurpose purpose = ScopeAdmissionPurpose.Mutation) =>
        WithExclusiveLock(scope, () => { body(); return true; }, timeout, afterRecovery, purpose);

    /// <summary>
    /// Holds the scope's shared lock across a persistent read. If a journal is present, releases
    /// the shared lock before escalating, recovers under the exclusive lock, then retries the
    /// read. Keeping the check and read in one lock lifetime prevents a reader from observing a
    /// prepared journal after its pre-check but before its body or sidecar access.
    /// </summary>
    private enum ReadPreparationReason
    {
        ScopeRecovery,
        IndexProjection,
        LayoutOnly
    }

    private sealed class ReadPreparationRequired(string scope,
        ReadPreparationReason reason = ReadPreparationReason.ScopeRecovery) : Exception
    {
        internal string Scope { get; } = scope;
        internal ReadPreparationReason Reason { get; } = reason;
    }

    internal T WithWorkspaceSnapshot<T>(Func<WorkspaceLease, T> read, CancellationToken ct = default)
    {
        var prepared = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            string? prepareScope = null;
            ReadPreparationReason preparationReason = ReadPreparationReason.ScopeRecovery;
            using (WorkspaceLease lease = _workspaceAdmission.EnterRead(ImportLockTimeout, ct))
            {
                if (lease.RecoveryPerformed) InvalidateCaches();
                foreach (var proof in prepared) lease.SetVerifiedScopeSnapshot(proof.Key, proof.Value);
                if (lease.CapturedAbsentWorkspace) return read(lease);
                if (!_layoutChecked && StoreLayoutMigration.NeedsMigration(_workspaceRoot))
                {
                    prepareScope = "local";
                    preparationReason = ReadPreparationReason.LayoutOnly;
                }
                else
                {
                    _layoutChecked = true;
                    try { return read(lease); }
                    catch (ReadPreparationRequired preparation)
                    {
                        prepareScope = preparation.Scope;
                        preparationReason = preparation.Reason;
                    }
                }
            }
            using WorkspaceLease mutation = EnterWorkspaceMutation(ct: ct);
            // EnterWorkspaceMutation has completed layout migration. Re-enter shared discovery;
            // layout-only work neither owns scope recovery nor establishes a verified snapshot.
            if (preparationReason == ReadPreparationReason.LayoutOnly) continue;
            if (preparationReason == ReadPreparationReason.IndexProjection)
            {
                PrepareIndexProjection(mutation, prepareScope!, ct);
                continue;
            }
            bool terminalRetained = false;
            string fingerprint = WithExclusiveLock(mutation, prepareScope!,
                () => terminalRetained ? CaptureScopeSnapshotFingerprint(prepareScope!) : string.Empty,
                afterRecovery: retained => terminalRetained = retained,
                purpose: ScopeAdmissionPurpose.VerifiedReadPreparation, cancellationToken: ct);
            if (terminalRetained) prepared[prepareScope!] = fingerprint;
        }
    }


    private static readonly TimeSpan IndexProjectionAdmissionTimeout = TimeSpan.FromSeconds(5);

    private TimeSpan RemainingProjectionBudget(Stopwatch timer, string scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan remaining = IndexProjectionAdmissionTimeout - timer.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new FileLockTimeoutException(GetLockPath(scope), IndexProjectionAdmissionTimeout);
        return remaining;
    }

    private void PrepareIndexProjection(WorkspaceLease lease, string scope, CancellationToken cancellationToken)
    {
        lease.AssertValid(_workspaceRoot);
        Stopwatch timer = Stopwatch.StartNew();
        IndexProjectionObserverForTest?.Invoke(scope, "requested");
        AdmissionGate gate = _scopeLockManager.AcquireAdmissionGate(scope);
        bool acquired = false;
        try
        {
            IndexProjectionObserverForTest?.Invoke(scope, "gate-wait");
            acquired = gate.Semaphore.Wait(RemainingProjectionBudget(timer, scope, cancellationToken), cancellationToken);
            if (!acquired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new FileLockTimeoutException(GetLockPath(scope), IndexProjectionAdmissionTimeout);
            }
            IndexProjectionObserverForTest?.Invoke(scope, "gate-acquired");
            PrepareIndexProjectionUnderAdmission(lease, scope, timer, cancellationToken);
        }
        finally
        {
            if (acquired) gate.Semaphore.Release();
            _scopeLockManager.ReleaseAdmissionGate(scope, gate);
        }
    }

    private async Task PrepareIndexProjectionAsync(WorkspaceLease lease, string scope, CancellationToken cancellationToken)
    {
        lease.AssertValid(_workspaceRoot);
        Stopwatch timer = Stopwatch.StartNew();
        IndexProjectionObserverForTest?.Invoke(scope, "requested");
        AdmissionGate gate = _scopeLockManager.AcquireAdmissionGate(scope);
        bool acquired = false;
        try
        {
            IndexProjectionObserverForTest?.Invoke(scope, "gate-wait");
            acquired = await gate.Semaphore.WaitAsync(RemainingProjectionBudget(timer, scope, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (!acquired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new FileLockTimeoutException(GetLockPath(scope), IndexProjectionAdmissionTimeout);
            }
            IndexProjectionObserverForTest?.Invoke(scope, "gate-acquired");
            PrepareIndexProjectionUnderAdmission(lease, scope, timer, cancellationToken);
        }
        finally
        {
            if (acquired) gate.Semaphore.Release();
            _scopeLockManager.ReleaseAdmissionGate(scope, gate);
        }
    }

    private void PrepareIndexProjectionUnderAdmission(WorkspaceLease lease, string scope, Stopwatch timer,
        CancellationToken cancellationToken)
    {
        try
        {
            // Gate ownership coordinates this instance only. The shared file barrier and fresh
            // fingerprint decide whether the old request still requires a durable projection.
            WithRecoveryReadBarrier(lease, scope, () => LoadIndexUnderRecoveryBarrier(scope),
                RemainingProjectionBudget(timer, scope, cancellationToken), cancellationToken);
            IndexProjectionObserverForTest?.Invoke(scope, "recheck-current");
            return;
        }
        catch (ReadPreparationRequired preparation) when (
            string.Equals(preparation.Scope, scope, StringComparison.OrdinalIgnoreCase))
        {
            // Shared file/index ownership has unwound before exclusive admission below.
        }

        IndexProjectionObserverForTest?.Invoke(scope, "before-exclusive");
        bool terminalRetained = false;
        WithExclusiveLock(lease, scope, () =>
        {
            IndexProjectionObserverForTest?.Invoke(scope, "exclusive-start");
            RefuseMutationWithRetainedEvidence(scope, GetStoreDirUnderLease(scope), terminalRetained, cancellationToken);
            // A peer may publish in the shared-to-exclusive gap. Never use the preflight cache
            // as authority for this projection under its newly acquired exclusive handle.
            RefreshScopeSnapshot(scope);
            return LoadIndexUnsafe(scope);
        }, RemainingProjectionBudget(timer, scope, cancellationToken),
            afterRecovery: retained => terminalRetained = retained,
            purpose: ScopeAdmissionPurpose.VerifiedReadPreparation, cancellationToken: cancellationToken);
        // No snapshot permission escapes this helper. The caller retries ordinary fresh admission.
    }

    private T WithRecoveryReadBarrier<T>(string scope, Func<T> read, CancellationToken ct = default) =>
        WithWorkspaceSnapshot(lease => lease.CapturedAbsentWorkspace ? default! : WithRecoveryReadBarrier(lease, scope, read, cancellationToken: ct), ct);

    private T WithRecoveryReadBarrier<T>(WorkspaceLease lease, string scope, Func<T> read,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        lease.AssertValid(_workspaceRoot);
        string storeDir = GetStoreDirUnderLease(scope);
        if (!Directory.Exists(storeDir)) return read();
        cancellationToken.ThrowIfCancellationRequested();
        using var scopeRead = timeout is null && !cancellationToken.CanBeCanceled
            ? FileLock.AcquireShared(GetLockPath(scope))
            : FileLock.AcquireShared(GetLockPath(scope), timeout ?? TimeSpan.FromSeconds(5), cancellationToken);
        if (lease.MatchesVerifiedScopeSnapshot(scope, () => CaptureScopeSnapshotFingerprint(scope)))
            return read();
        RecoveryDiscoveryResult discovery = _recoveryDiscovery.Discover(scope, storeDir, cancellationToken: cancellationToken);
        if (discovery.BlockingEvidence is not null)
            throw CreateDiscoveryRecoveryException(discovery.BlockingEvidence);
        if (discovery.Candidates.Count > 0 && discovery.Candidates[0].Operation == RecoveryOperation.Append &&
            IsAppendReadBlocked(discovery.Candidates[0]))
            throw CreateDiscoveryRecoveryException(new RecoveryBlock(scope,
                discovery.Candidates[0].EvidencePath, RecoveryOperation.Append,
                discovery.Candidates[0].OperationId, RecoveryBlockReason.Inconsistent,
                "Append recovery evidence must be resolved before reading the affected scope."));
        if (discovery.CoordinatorRecord is not null ||
            discovery.Candidates.Any(c => c.Operation != RecoveryOperation.Append))
            throw new ReadPreparationRequired(scope);
        ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.BeforeRecoveryBarrierRead, GetRestoreJournalPath(storeDir));
        return read();
    }

    private async Task<T> WithRecoveryReadBarrierAsync<T>(string scope, Func<Task<T>> read, CancellationToken ct = default)
    {
        var prepared = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            string? prepareScope = null;
            ReadPreparationReason preparationReason = ReadPreparationReason.ScopeRecovery;
            using (WorkspaceLease lease = _workspaceAdmission.EnterRead(ImportLockTimeout, ct))
            {
                if (lease.RecoveryPerformed) InvalidateCaches();
                foreach (var proof in prepared) lease.SetVerifiedScopeSnapshot(proof.Key, proof.Value);
                if (lease.CapturedAbsentWorkspace) throw new FileNotFoundException("The workspace contains no persistent memories.");
                if (!_layoutChecked && StoreLayoutMigration.NeedsMigration(_workspaceRoot))
                {
                    prepareScope = scope;
                    preparationReason = ReadPreparationReason.LayoutOnly;
                }
                else
                {
                    _layoutChecked = true;
                    try
                    {
                        // Acquire the scope read handle for the entire asynchronous byte read.
                        string directory = GetStoreDirUnderLease(scope);
                        if (!Directory.Exists(directory)) return await read().ConfigureAwait(false);
                        using var scopeRead = FileLock.AcquireShared(GetLockPath(scope), ImportLockTimeout, ct);
                        if (lease.MatchesVerifiedScopeSnapshot(scope, () => CaptureScopeSnapshotFingerprint(scope)))
                            return await read().ConfigureAwait(false);
                        RecoveryDiscoveryResult discovery = _recoveryDiscovery.Discover(scope, directory, cancellationToken: ct);
                        if (discovery.BlockingEvidence is not null)
                            throw CreateDiscoveryRecoveryException(discovery.BlockingEvidence);
                        if (discovery.CoordinatorRecord is not null || discovery.Candidates.Any(c => c.Operation != RecoveryOperation.Append))
                            throw new ReadPreparationRequired(scope);
                        if (discovery.Candidates.FirstOrDefault(IsAppendReadBlocked) is { } blocked)
                            throw CreateDiscoveryRecoveryException(new RecoveryBlock(scope, blocked.EvidencePath,
                                RecoveryOperation.Append, blocked.OperationId, RecoveryBlockReason.Inconsistent,
                                "Append recovery evidence must be resolved before reading the affected scope."));
                        return await read().ConfigureAwait(false);
                    }
                    catch (ReadPreparationRequired preparation)
                    {
                        prepareScope = preparation.Scope;
                        preparationReason = preparation.Reason;
                    }
                }
            }
            using WorkspaceLease mutation = EnterWorkspaceMutation(ct: ct);
            // EnterWorkspaceMutation has completed layout migration. Re-enter shared discovery;
            // layout-only work neither owns scope recovery nor establishes a verified snapshot.
            if (preparationReason == ReadPreparationReason.LayoutOnly) continue;
            if (preparationReason == ReadPreparationReason.IndexProjection)
            {
                await PrepareIndexProjectionAsync(mutation, prepareScope!, ct).ConfigureAwait(false);
                continue;
            }
            bool terminalRetained = false;
            string fingerprint = WithExclusiveLock(mutation, prepareScope!,
                () => terminalRetained ? CaptureScopeSnapshotFingerprint(prepareScope!) : string.Empty,
                afterRecovery: retained => terminalRetained = retained,
                purpose: ScopeAdmissionPurpose.VerifiedReadPreparation, cancellationToken: ct);
            if (terminalRetained) prepared[prepareScope!] = fingerprint;
        }
    }

    private string CaptureScopeSnapshotFingerprint(string scope)
    {
        string directory = GetStoreDirUnderLease(scope);
        if (!Directory.Exists(directory)) return "absent";
        // Per-call proof: enumerate authoritative files and recovery evidence, excluding locks
        // and derived caches. Names and content hashes prove the terminal snapshot remains unchanged.
        return string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".nmp2", StringComparison.Ordinal) ||
                path.EndsWith(".meta.json", StringComparison.Ordinal) ||
                Path.GetFileName(path).Contains("journal", StringComparison.Ordinal) ||
                path.EndsWith(".recovery", StringComparison.Ordinal) ||
                Path.GetFileName(path).Contains("multi-memory", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                return path + "|" + Convert.ToHexStringLower(SHA256.HashData(stream));
            }));
    }

    private ListingService CreateListingUnderLease(WorkspaceLease lease)
    {
        lease.AssertValid(_workspaceRoot);
        return new ListingService(
            scope => lease.CapturedAbsentWorkspace ? [] :
                WithRecoveryReadBarrier(lease, scope, () => LoadIndexUnderRecoveryBarrier(scope)),
            GetStoreDirUnderLease, SanitizeName, ListEphemeral, TopicDiscoveryService.IncludesEphemeral,
            scopes => lease.CapturedAbsentWorkspace ? [] : _topicDiscoveryService.ResolveReadScopes(scopes),
            (scopes, excludes) => lease.CapturedAbsentWorkspace ? [] : _topicDiscoveryService.ResolveReadScopes(scopes, excludes));
    }

    internal (IReadOnlyList<ArtifactEntry> Entries, List<(string Name, string Content)> Artifacts)
        CaptureBundleTopicUnderLease(WorkspaceLease lease, string scope)
    {
        lease.AssertValid(_workspaceRoot);
        if (lease.CapturedAbsentWorkspace) return ([], []);
        return WithRecoveryReadBarrier(lease, scope, () =>
        {
            var entries = LoadIndexUnderRecoveryBarrier(scope).ToArray();
            var artifacts = CreateListingUnderLease(lease).ListTopicArtifacts(scope)
                .Select(item => (item.Name, File.ReadAllText(item.FilePath))).ToList();
            return ((IReadOnlyList<ArtifactEntry>)entries, artifacts);
        });
    }

    private static bool IsAppendReadBlocked(RecoveryCandidate candidate) =>
        candidate.Envelope.State is not (RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending);

    /// <summary>
    /// Test-only seam for <see cref="WithExclusiveLock(string, Action, TimeSpan?)"/>, which is
    /// private because every production write reaches it through a named store operation. Exists
    /// so the timeout-forwarding contract can be pinned without widening the real API.
    /// </summary>
    internal void InvokeWithExclusiveLockForTest(string scope, Action body, TimeSpan? timeout = null)
        => WithExclusiveLock(scope, body, timeout);

    public IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local")
    {
        return WithWorkspaceSnapshot(lease => lease.CapturedAbsentWorkspace ? [] : WithRecoveryReadBarrier(lease, scope, () => LoadIndexUnderRecoveryBarrier(scope)));
    }

    private void RefreshScopeSnapshot(string scope)
    {
        try
        {
            string directory = GetStoreDirUnderLease(scope);
            string fingerprint = Directory.Exists(directory)
                ? string.Join("\n", Directory.EnumerateFiles(directory, "*.meta.json")
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .Select(path =>
                    {
                        using var stream = File.OpenRead(path);
                        return Path.GetFileName(path) + "|" + Convert.ToHexStringLower(SHA256.HashData(stream));
                    }))
                : "absent";
            if (!_scopeSnapshotFingerprints.TryGetValue(scope, out string? previous) || previous != fingerprint)
            {
                _scopeIndexCache.Remove(scope);
                _scopeSnapshotFingerprints[scope] = fingerprint;
                _factMaterializer.Invalidate();
            }
        }
        catch
        {
            _scopeIndexCache.Remove(scope);
            _scopeSnapshotFingerprints.TryRemove(scope, out _);
            _factMaterializer.Invalidate();
            throw;
        }
    }

    private List<ArtifactEntry> LoadIndexUnderRecoveryBarrier(string scope)
    {
        RefreshScopeSnapshot(scope);
        var lk = _scopeLockManager.GetIndexLock(scope);

        // The outer read barrier owns the scope file lock. The sidecar fingerprint above
        // refreshes other-process changes; this lock protects the in-process cache projection.
        lk.EnterReadLock();
        try
        {
            // Live view, not a copy. Safe because no reader mutates the returned list — there is
            // no derived structure on CachedIndex left to desync — and every cache write REPLACES
            // the CachedIndex rather than mutating this instance.
            if (_scopeIndexCache.TryGet(scope, out var cached))
                return cached.Entries;
        }
        finally
        {
            lk.ExitReadLock();
        }

        lk.EnterReadLock();
        try
        {
            // LoadIndexUnsafe re-checks the cache as its first statement, which covers the
            // window between the two acquisitions above — another thread may have populated it
            // while this one was waiting on the file lock.
            return LoadIndexUnsafe(scope, persistMigration: false);
        }
        finally
        {
            lk.ExitReadLock();
        }
    }

    private List<ArtifactEntry> LoadIndexUnderExclusiveAppendLock(string scope)
    {
        RefreshScopeSnapshot(scope);
        // The exclusive FILE lock (held by the caller) prevents cross-process writes, but not a
        // same-process writer that replaces _scopeIndexCache under WithExclusiveLock's EnterWriteLock.
        // Take the per-scope read lock so this cache check is sequenced against that writer — matching
        // LoadIndexUnderRecoveryBarrier. LoadIndexUnsafe below re-checks the cache and already runs
        // inside the caller's exclusive file lock, so it needs no additional read lock here.
        var lk = _scopeLockManager.GetIndexLock(scope);
        lk.EnterReadLock();
        try
        {
            if (_scopeIndexCache.TryGet(scope, out var cached))
                return cached.Entries;
        }
        finally
        {
            lk.ExitReadLock();
        }

        return LoadIndexUnsafe(scope);
    }

    private List<ArtifactEntry> LoadIndexUnsafe(string scope, bool persistMigration = true)
    {
        // Live view — see the comment on the fast-path check in LoadIndex.
        if (_scopeIndexCache.TryGet(scope, out var cached))
            return cached.Entries;

        string storeDir = GetStoreDirUnderLease(scope);
        // RAW, not the normalizing seam: AttributeMigration.MigratesTo below needs to see an
        // entry that still carries LegacyFields (if any) so its own NormalizeAttributesKey()
        // call can detect a change and trigger the durable sidecar rewrite. Feeding it an
        // already-normalized entry makes that diff a no-op — the in-memory result is still
        // correct (MigratesTo's forward-fill is a superset of NormalizeAttributesKey, so
        // `entries[i] = upgraded` below normalizes the cached slot either way), but the sidecar
        // on disk would never get rewritten from "fields" to "attributes".
        var entries = LoadEntriesFromSidecarsRaw(storeDir);

        // Sidecars are durable records, but their logical address and artifact key are derived
        // from the scope directory they were read from. Reconstruct that pair at the boundary so
        // a recovered journal (or an older sidecar carrying a bare address) cannot create a second
        // logical entry for the same leaf or leave reads and fact materialization on different keys.
        for (int i = 0; i < entries.Count; i++)
            entries[i] = ArtifactIdentityMapper.CanonicalizeEntryForScope(entries[i], scope);

        // Read-time forward-fill: upgrade legacy carriers (tags/codeRefs/review/anchors/
        // recognized-key keywords) into the attributes model (Fields/Keywords) so every
        // reader sees Fields without a prior `scri migrate`. Primary-scope sidecars whose
        // shape actually changes are written back once here — guarded by MigratesTo, so a
        // later cold load of an already-upgraded sidecar performs no write — mirroring the
        // index.json→sidecar upgrade above. This durably promotes the on-disk data into
        // Fields before a later plan removes the legacy members, since STJ silently drops
        // unmapped members on serialize and would otherwise lose it. Purely additive:
        // legacy fields are never cleared, so scoring (Math.Max over legacy + attribute
        // reads) is unchanged.
        //
        // A shared read requests preparation before any durable rewrite. Projection admission
        // coalesces this instance's requests, rechecks under shared file ownership, and performs
        // required write-back under exclusive file/index ownership. Persistence remains best-effort:
        // an I/O failure leaves the in-memory upgrade intact and may be retried on a later cold load.
        for (int i = 0; i < entries.Count; i++)
        {
            if (AttributeMigration.MigratesTo(entries[i], out var upgraded))
            {
                entries[i] = upgraded;

                // The in-memory upgrade above stands; only the PERSIST is skipped. A sidecar
                // written by a newer build must not be rewritten by this one, and the check
                // has to happen HERE rather than as a refusal thrown from WriteSidecar: this
                // loop's catch is filtered to IOException and UnauthorizedAccessException, so
                // a ScopeReadOnlyException would propagate out of a plain read and break
                // search() for the whole scope. Attempting a write already known to be refused
                // and then widening the catch to swallow it is the worse repair.
                //
                // Consequence, stated because it is the next question a reader has: a
                // legacy-shaped sidecar from a newer build is therefore NEVER migrated on disk
                // by this build, and re-pays AttributeMigration.Forward on every cold load. That
                // is intended -- the durable rewrite is left to the newer build that owns the
                // file, which has already migrated its own shape anyway.
                if (ArtifactSidecarPolicy.IsReadOnly(upgraded)) continue;

                if (!persistMigration) throw new ReadPreparationRequired(scope, ReadPreparationReason.IndexProjection);
                try { WriteSidecar(upgraded, storeDir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort; see above */ }
            }
        }

        if (entries.Count > 0)
            _scopeIndexCache.Replace(scope, entries);

        return entries;
    }

    /// <summary>
    /// The load seam for a WHOLE SCOPE, and one of the two members that own the legacy
    /// "fields" → Attributes normalization rule (<see cref="ArtifactEntry.NormalizeAttributesKey"/>)
    /// per the invariant at <c>ArtifactEntry.cs:67</c> — an entry never escapes here with data
    /// still sitting in <c>LegacyFields</c>. <see cref="LoadSidecar"/> is the other, for a single
    /// entry. Callers must NOT re-apply the rule; that used to be pasted around every call site
    /// (Upsert, Remove, TryUpdateEntry's two lookups) and one missed copy would have silently
    /// hidden an entry's facets from search/filter/display for the process lifetime. See
    /// Scrinia.Tests.Store.NormalizationSeamTests for the regression guard.
    ///
    /// <para>Two callers deliberately bypass normalization and read
    /// <see cref="LoadEntriesFromSidecarsRaw"/> directly — <see cref="LoadIndexUnsafe"/> and
    /// <see cref="EnumerateRawForMigration"/> — because
    /// <see cref="Migration.AttributeMigration.MigratesTo"/> has to still see
    /// <see cref="ArtifactEntry.LegacyFields"/> populated to detect a sidecar that needs
    /// rewriting. Each says so at its own call site. Nothing else reads a sidecar without
    /// normalizing it.</para>
    /// </summary>
    private List<ArtifactEntry> LoadEntriesFromSidecars(string storeDir)
    {
        var entries = LoadEntriesFromSidecarsRaw(storeDir);
        for (int i = 0; i < entries.Count; i++)
            entries[i] = entries[i].NormalizeAttributesKey();
        return entries;
    }

    /// <summary>
    /// The load seam for ONE sidecar, already located by <see cref="ResolveSidecarPath"/>.
    /// Normalizes exactly as <see cref="LoadEntriesFromSidecars"/> does; returns null for a
    /// missing, unreadable or corrupt file, matching that method's skip-corrupt behaviour.
    /// </summary>
    private static ArtifactEntry? LoadSidecar(string metaPath) =>
        ReadSidecarRaw(metaPath)?.NormalizeAttributesKey();

    /// <summary>
    /// Locates the sidecar for <paramref name="sanitizedName"/> under <paramref name="storeDir"/>,
    /// or returns null when the scope holds none. The exact-name probe is one stat and answers the
    /// overwhelmingly common case; only a miss pays a directory enumeration.
    ///
    /// <para>RULING (case-insensitive name resolution, Task 2): the fallback scan matches
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> deliberately. <see cref="SanitizeName"/>
    /// does not fold case, so on a case-sensitive filesystem an exact-name probe alone answers
    /// "no such memory" for a sidecar differing only in casing — <c>forget("mixedcase")</c>
    /// reporting not-found for a memory stored as "MixedCase", while every name match around it
    /// (LoadIndex, TryUpdateEntry, the write paths) is case-insensitive. NTFS folds case itself by
    /// default, so on an ordinary Windows volume the probe always succeeds and the fallback is
    /// never entered; <c>CaseSensitiveFileStoreTests</c> uses a directory carrying NTFS's
    /// per-directory case-sensitivity flag to exercise it, and SKIPS (never silently passes) on a
    /// volume that cannot provide the flag.</para>
    ///
    /// <para>Tie-break, and it matters because <see cref="Remove"/> deletes what this returns: a
    /// case-sensitive filesystem holding pre-branch data can have BOTH "Note.meta.json" and
    /// "note.meta.json", and the fallback is only reached when the caller asked for neither
    /// exactly. Directory enumeration order is not guaranteed — notably on ext4 — so candidates
    /// are sorted ordinally and the first is taken, making the choice deterministic rather than
    /// arbitrary. Ordinal sort puts uppercase first, so "Note.meta.json" wins. That is a coin toss
    /// decided consistently, not a claim about which one is meant; the alternative considered was
    /// refusing on ambiguity, rejected because it would make <c>forget()</c> fail with no name the
    /// caller could give it instead.</para>
    /// </summary>
    private static string? ResolveSidecarPath(string storeDir, string sanitizedName)
    {
        string metaPath = Path.Combine(storeDir, sanitizedName + ".meta.json");
        if (File.Exists(metaPath)) return metaPath;
        if (!Directory.Exists(storeDir)) return null;

        string wanted = Path.GetFileName(metaPath);
        return Directory.EnumerateFiles(storeDir, "*.meta.json")
            .Where(p => Path.GetFileName(p).Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The actual disk read behind <see cref="LoadEntriesFromSidecars"/>, deliberately NOT
    /// normalized. Kept separate for <see cref="EnumerateRawForMigration"/>, whose documented
    /// contract is to hand <see cref="Migration.AttributeMigration.MigratesTo"/> genuinely raw
    /// entries: <c>MigratesTo</c> calls <c>NormalizeAttributesKey()</c> itself as part of
    /// detecting whether an entry still carries the legacy "fields" key, and needs
    /// <see cref="ArtifactEntry.LegacyFields"/> to still be populated to do that. If this fed
    /// pre-normalized entries instead, every legacy-"fields" sidecar would look already-migrated
    /// and <c>scri migrate</c> would silently stop rewriting it on disk.
    /// </summary>
    private List<ArtifactEntry> LoadEntriesFromSidecarsRaw(string storeDir)
    {
        if (!Directory.Exists(storeDir)) return [];

        var entries = new List<ArtifactEntry>();
        foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json"))
        {
            var entry = ReadSidecarRaw(metaFile);
            if (entry is not null)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// One sidecar off disk, deserialized and NOT normalized. The single read rule shared by the
    /// whole-scope load (<see cref="LoadEntriesFromSidecarsRaw"/>) and the single-entry one
    /// (<see cref="LoadSidecar"/>), so the two cannot drift on retry or corruption handling.
    /// Returns null for a missing, unreadable or corrupt file.
    /// </summary>
    private static ArtifactEntry? ReadSidecarRaw(string metaFile)
    {
        try
        {
            // Retry the READ (only) on a transient lock: read-time forward-fill writes
            // sidecars under LoadIndex's shared lock, so a concurrent reader can hit the
            // File.Move replace window. Without the retry, that transient sharing violation
            // would fall into the blanket "skip corrupt" catch below and silently drop the
            // entry from this read. FileRetry retries transient IOExceptions and rethrows a
            // permanently-stuck file (which then degrades to skip, as before). Deserialize
            // stays OUTSIDE the retry so genuinely-corrupt JSON is still skipped, not retried.
            string json = FileRetry.Run(() => File.ReadAllText(metaFile));
            try
            {
                return JsonSerializer.Deserialize(json, FileStoreJsonContext.Default.ArtifactEntry);
            }
            catch (Exception) when (json.Contains("\"name\"", StringComparison.OrdinalIgnoreCase) &&
                                   json.Contains("\"uri\"", StringComparison.OrdinalIgnoreCase))
            {
                var legacy = JsonSerializer.Deserialize(json, FileStoreJsonContext.Default.LegacyArtifactEntry)
                    ?? throw new JsonException("Legacy sidecar is empty.");
                if (string.IsNullOrWhiteSpace(legacy.Name))
                    throw new JsonException("Legacy sidecar is missing its name.");

                return new ArtifactEntry(legacy.Name, legacy.Uri ?? "", legacy.OriginalBytes,
                    legacy.ChunkCount, legacy.CreatedAt, legacy.ContentPreview, legacy.Keywords,
                    legacy.TermFrequencies, legacy.UpdatedAt, legacy.ChunkEntries, legacy.FileHashes,
                    legacy.Attributes, legacy.AgentKeywords, legacy.SchemaVersion)
                {
                    LegacyFields = legacy.LegacyFields,
                    Unknown = legacy.Unknown
                };
            }
        }
        catch { /* skip corrupt sidecar */ return null; }
    }

    /// <summary>
    /// Reads a legacy v1 <c>index.json</c>'s entries raw (no forward-fill, no write-back).
    /// Internal (not private): the read path's own fallback use of this was removed when the
    /// index.json → sidecar upgrade moved into <see cref="StoreLayoutMigration"/> (which calls
    /// this directly — see <c>ExpandLegacyIndexEntries</c>'s call site), but
    /// <see cref="EnumerateRawForMigration"/> still uses it as a belt-and-braces fallback for
    /// <see cref="Migration.AttributeMigration.MigrateStore"/>. In practice that fallback is
    /// unreachable once the workspace has gone through migration once, because every
    /// <see cref="GetStoreDirForScope"/> call gates on <see cref="MemoriesRoot"/> first — but it
    /// is still real, compiled, exercised code, not dead code eligible for deletion.
    /// </summary>
    internal static List<ArtifactEntry> LoadEntriesFromLegacyIndex(string storeDir)
    {
        string indexPath = Path.Combine(storeDir, "index.json");
        if (!File.Exists(indexPath)) return [];

        try
        {
            string json = File.ReadAllText(indexPath);
            try
            {
                var idx = JsonSerializer.Deserialize(json, FileStoreJsonContext.Default.IndexFile);
                return idx?.Entries ?? [];
            }
            catch (Exception)
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("entries", out var entries)) return [];
                return entries.EnumerateArray()
                    .Select(entry => JsonSerializer.Deserialize(entry.GetRawText(),
                        FileStoreJsonContext.Default.LegacyArtifactEntry))
                    .Where(entry => entry?.Name is not null)
                    .Select(entry => new ArtifactEntry(entry!.Name!, entry.Uri ?? "", entry.OriginalBytes,
                        entry.ChunkCount, entry.CreatedAt, entry.ContentPreview, entry.Keywords,
                        entry.TermFrequencies, entry.UpdatedAt, entry.ChunkEntries, entry.FileHashes,
                        entry.Attributes, entry.AgentKeywords, entry.SchemaVersion)
                    {
                        LegacyFields = entry.LegacyFields,
                        Unknown = entry.Unknown
                    })
                    .ToList();
            }
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Yields every persistent entry across all scopes as RAW deserialized sidecars — WITHOUT the
    /// read-time forward-fill write-back that <see cref="LoadIndex"/>/<see cref="ListScoped(IReadOnlyList{string}?)"/>
    /// perform as a side effect of loading. Each item carries the entry, its scope, and the primary
    /// store directory the sidecar lives in. Read-only; used by
    /// <see cref="Migration.AttributeMigration.MigrateStore"/> to measure and persist accurately.
    /// </summary>
    internal IEnumerable<(ArtifactEntry Entry, string Scope, string StoreDir)> EnumerateRawForMigration() =>
        WithWorkspaceSnapshot(lease =>
    {
        // Guard against yielding the same on-disk sidecar twice — a defensive keying, kept even
        // though every scope now maps to exactly one v3 directory (no more shadowed legacy-flat-dir
        // target shared by two scopes to double-count).
        var yielded = new HashSet<(string Dir, string Name)>();
        var result = new List<(ArtifactEntry Entry, string Scope, string StoreDir)>();
        foreach (string scope in lease.CapturedAbsentWorkspace ? [] : _topicDiscoveryService.ResolveReadScopes(["all"]))
        {
            var (primaryDir, primary) = WithRecoveryReadBarrier(lease, scope, () =>
            {
                string storeDir = GetStoreDirUnderLease(scope);
                var entries = LoadEntriesFromSidecarsRaw(storeDir);
                if (entries.Count == 0)
                    entries = LoadEntriesFromLegacyIndex(storeDir); // raw index.json fallback
                return (storeDir, entries);
            });
            foreach (var entry in primary)
            {
                var canonical = ArtifactIdentityMapper.CanonicalizeEntryForScope(entry, scope);
                if (yielded.Add((primaryDir, canonical.Name.ToLowerInvariant())))
                    result.Add((canonical, scope, primaryDir));
            }
        }
        return result;
    });

    /// <summary>
    /// Writes an already-upgraded entry's sidecar to a SPECIFIC directory under <paramref name="scope"/>'s
    /// exclusive lock, then invalidates that scope's cached index. Unlike <see cref="Upsert"/> (which always
    /// targets <see cref="GetStoreDirForScope"/>), the explicit <paramref name="storeDir"/> lets a shadowed
    /// legacy-flat-dir entry be rewritten in place (its flat dir) rather than promoted into the namespaced
    /// scope dir. Used only by the migrate path.
    /// </summary>
    internal void PersistMigratedEntry(ArtifactEntry entry, string storeDir, string scope)
    {
        WithExclusiveLock(scope, () =>
        {
            Directory.CreateDirectory(storeDir);
            WriteSidecar(entry, storeDir);
            _scopeIndexCache.Remove(scope); // next read rebuilds from the now-upgraded sidecars
            if (scope.StartsWith("local:", StringComparison.Ordinal))
                _topicDiscoveryService.InvalidateCachedTopics();
        });
    }

    private void WriteSidecars(List<ArtifactEntry> entries, string storeDir)
    {
        // Refuse the WHOLE list before writing any of it. Checking per iteration would refuse
        // mid-loop and leave a partial write -- the exact failure class the transactional-restore
        // work closed. This is O(n) over a list already in hand and reads nothing from disk.
        if (entries.Find(ArtifactSidecarPolicy.IsReadOnly) is { } blocking)
            throw new ScopeReadOnlyException(
                storeDir, blocking.Name, blocking.SchemaVersion ?? 1,
                DurableFormats.ArtifactSidecar.Current);

        foreach (var entry in entries)
            WriteSidecar(entry, storeDir);
    }

    /// <summary>
    /// Test-only seams for <see cref="WriteSidecar"/> and <see cref="WriteSidecars"/>, both
    /// private because every production write reaches them through a named store operation.
    /// Exist so the version-refusal gates can be pinned directly, matching
    /// <see cref="InvokeWithExclusiveLockForTest"/>'s convention.
    /// </summary>
    /// <summary>
    /// Test seam for <see cref="BuildDerivedEntry"/>. Inheritance of a version ABOVE
    /// <c>Current</c> cannot be observed through <see cref="StoreAsync"/> any more: the write gate
    /// refuses such a memory before the rebuild runs, so a test going through the public write path
    /// pins the REFUSAL and would keep passing if the inheritance were deleted outright.
    /// </summary>
    internal ArtifactEntry InvokeBuildDerivedEntryForTest(
        string subject, string scope, DerivedMetadata derived, ArtifactEntry? prior)
        => BuildDerivedEntry(subject, scope, 0, 1, DateTimeOffset.UtcNow, null, derived, prior);

    internal void InvokeWriteSidecarForTest(ArtifactEntry entry, string storeDir)
        => WriteSidecar(entry, storeDir);

    internal void InvokeWriteSidecarsForTest(List<ArtifactEntry> entries, string storeDir)
        => WriteSidecars(entries, storeDir);

    private void WriteSidecar(ArtifactEntry entry, string storeDir, string? operationId = null) =>
        WriteSidecarCore(entry, storeDir, operationId);

    /// <summary>
    /// Static core of sidecar writing: normalize, sort for stable diffs, serialize via
    /// <see cref="FileStoreJsonContext"/>, atomic temp+move. No instance state, so it is also
    /// used directly by <see cref="ExpandLegacyIndexEntries"/>, which runs from
    /// <see cref="StoreLayoutMigration"/> before any <see cref="FileMemoryStore"/> instance
    /// exists — that call site depends on this producing byte-identical sidecars to a normal
    /// store write.
    /// </summary>
    private static void WriteSidecarCore(ArtifactEntry entry, string storeDir, string? operationId = null) =>
        DurableWriter.WriteSidecar(entry, storeDir, operationId, SanitizeNameCore, SerializeSidecar);

    private static string SerializeSidecar(ArtifactEntry entry)
    {
        entry = entry.NormalizeAttributesKey();
        var sorted = entry with
        {
            Keywords = entry.Keywords?.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
            TermFrequencies = entry.TermFrequencies?.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            FileHashes = entry.FileHashes?.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            ChunkEntries = entry.ChunkEntries?.Select(ce => ce with
            {
                Keywords = ce.Keywords?.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
                TermFrequencies = ce.TermFrequencies?.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            }).ToArray(),
            Unknown = entry.Unknown?.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        return JsonSerializer.Serialize(sorted, FileStoreJsonContext.Default.ArtifactEntry);
    }

    /// <summary>
    /// Expands a legacy v1 <c>index.json</c>'s entries into individual <c>.meta.json</c> sidecars
    /// written to <paramref name="targetDir"/>, reusing <see cref="WriteSidecarCore"/> so the
    /// output is byte-identical to what this store writes normally. Called by
    /// <see cref="StoreLayoutMigration"/> after it has moved every real content file into place,
    /// so this is what makes an <c>index.json</c>-only legacy directory's entries visible again
    /// after the merge — see the migration's class doc comment.
    ///
    /// <para>Skips an entry whose sidecar already exists at the target: a real
    /// <c>.meta.json</c> merged in from a sibling legacy directory (e.g. the flat and
    /// <c>memory/</c>-namespaced forms of the same topic) describes the same artifact more
    /// currently than a legacy index.json snapshot ever can, so it wins rather than being
    /// clobbered.</para>
    /// </summary>
    internal static void ExpandLegacyIndexEntries(List<ArtifactEntry> entries, string targetDir)
    {
        if (entries.Count == 0) return;

        Directory.CreateDirectory(targetDir);
        foreach (var entry in entries)
        {
            string metaPath = Path.Combine(targetDir, SanitizeNameCore(entry.Name) + ".meta.json");
            if (File.Exists(metaPath)) continue;
            WriteSidecarCore(entry, targetDir);
        }
    }

    /// <summary>Internal (not private): also used by <see cref="StoreLayoutMigration"/> to update
    /// the WORKSPACE-level <c>.gitignore</c> (a different file from the <c>.scrinia/.gitignore</c>
    /// this class's own <see cref="EnsureGitIgnore"/> writes), so that write goes through the same
    /// atomic-temp-then-move convention as every other artifact write in this codebase rather than
    /// a bare <c>File.WriteAllText</c>.</summary>
    internal static void AtomicWriteAllText(string path, string content) =>
        DurableFile.AtomicWriteAllText(path, content);

    private static Task AtomicWriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DurableFile.AtomicWriteAllText(path, content);
        return Task.CompletedTask;
    }

    private void DeleteSidecar(string name, string storeDir)
    {
        string metaPath = Path.Combine(storeDir, SanitizeName(name) + ".meta.json");
        if (File.Exists(metaPath))
            File.Delete(metaPath);
    }

    /// <summary>
    /// Persists <paramref name="entries"/> as the full index for <paramref name="scope"/> and takes
    /// ownership of the list instance: it becomes the scope's in-memory cache directly, not a copy.
    /// The caller must not retain a reference to <paramref name="entries"/> or mutate it after this
    /// call — doing so corrupts the cache and, since readers now get that same list by reference
    /// from <see cref="LoadIndex"/>, can surface as an <see cref="InvalidOperationException"/> inside
    /// an unrelated concurrent enumerator. Pass a fresh or already-copied list (see
    /// <see cref="ImportTopicEntries"/> for the pattern).
    /// <para>Ownership includes normalizing: the elements are replaced in place with their
    /// <see cref="ArtifactEntry.NormalizeAttributesKey"/> form before anything reads them.</para>
    /// </summary>
    /// <summary>
    /// Drops every cached view of the store, so the next read comes from disk.
    /// </summary>
    /// <remarks>
    /// For callers that repair store files OUT OF BAND — <c>reconcile</c> writes merged bytes
    /// straight to an absolute path rather than going through <see cref="Upsert"/>, because a
    /// git-conflict repair is not a well-formed store write and forcing it through one would
    /// change the merge semantics the driver and the auto-resolver share.
    /// <para>Blanket rather than per-scope, deliberately: the caller holds a filesystem path,
    /// not a scope, and a conflict repair is a rare heavyweight operation where re-reading
    /// everything is proportionate. The narrow per-scope invalidation stays where the hot path
    /// is, inside <see cref="Upsert"/>.</para>
    /// <para>Why this is needed at all: <c>_indexCache</c> is not keyed by mtime — only
    /// <c>_artifactCache</c> is — so a long-lived process (an MCP <c>scri serve</c> session)
    /// went on serving the pre-repair sidecar for its whole lifetime, having just reported the
    /// repair as successful.</para>
    /// </remarks>
    public void InvalidateCaches()
    {
        _scopeIndexCache.Clear();
        _topicDiscoveryService.InvalidateCachedTopics();
        _artifactCache.Invalidate(string.Empty);
        InvalidateEntityFacts();
    }

    /// <summary>
    /// Invalidates the in-process view for a scope after an out-of-band rollback restored its
    /// files. The next read must rebuild from the restored bytes rather than serving the entry
    /// that the failed publication installed before verification failed.
    /// </summary>
    internal void InvalidateScopeCacheAfterRollback(string scope)
    {
        _scopeIndexCache.Remove(scope);
        _artifactCache.Invalidate($"{scope}|");
        if (scope.StartsWith("local:", StringComparison.Ordinal))
            _topicDiscoveryService.InvalidateCachedTopics();
        InvalidateEntityFacts();
        RollbackScopeInvalidationCount++;
    }

    /// <summary>
    /// Drops every derived view that can expose a participant before the coordinator has finished
    /// publishing and verifying the aggregate decision. The caller holds the scope write lock, so
    /// a same-instance reader cannot reacquire the read lock between these replacements.
    /// </summary>
    internal void InvalidateCommittedScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        _scopeIndexCache.Remove(scope);
        _artifactCache.Invalidate($"{scope}|");
        if (scope.StartsWith("local:", StringComparison.Ordinal))
            _topicDiscoveryService.InvalidateCachedTopics();
        InvalidateEntityFacts();
        CommittedScopeInvalidationCount++;
    }

    public void SaveIndex(List<ArtifactEntry> entries, string scope = "local")
    {
        WithExclusiveLock(scope, () => SaveIndexUnsafe(entries, scope));
    }

    private void SaveIndexUnsafe(List<ArtifactEntry> entries, string scope)
    {
        string storeDir = GetStoreDirUnderLease(scope);
        Directory.CreateDirectory(storeDir);

        // SaveIndex accepts entries assembled by callers, including records whose logical address
        // still contains the topic prefix. Canonicalize the physical leaf before both the orphan
        // allow-list and publication; otherwise the sweep can delete the real sidecar while the
        // subsequent write recreates a nested topic/subject path.
        for (int i = 0; i < entries.Count; i++)
            entries[i] = ArtifactIdentityMapper.CanonicalizeEntryForScope(entries[i], scope);

        // Delete orphaned sidecars: any *.meta.json whose entry is no longer present.
        //
        // Correctness of this sweep depends on something the lock cannot supply: the entry list
        // must have been READ under the same exclusive lock that commits it. Holding the write
        // lock here says nothing about how old `entries` is. ImportTopicEntries used to hand over
        // a list read before any lock was taken, and this loop deleted whatever had landed since
        // (finding C1) — which is why import no longer calls this method at all.
        // Refuse BEFORE the sweep. PublishIndexUnsafe below routes through WriteSidecars, whose
        // whole-list gate would throw only after this loop had already deleted files -- deletions
        // committed, writes refused, at a public API. Refusing first makes SaveIndex all-or-nothing
        // for this hazard, the same ordering rule the import path follows.
        if (entries.Find(ArtifactSidecarPolicy.IsReadOnly) is { } blockingSave)
            throw new ScopeReadOnlyException(
                scope, blockingSave.Name, blockingSave.SchemaVersion ?? 1,
                DurableFormats.ArtifactSidecar.Current);

        DurableWriter.SweepOrphanSidecars(storeDir, entries.Select(e => SanitizeName(e.Name)));

        PublishIndexUnsafe(entries, scope);
    }

    /// <summary>
    /// Commits <paramref name="entries"/> as the scope's index WITHOUT deleting anything: writes
    /// each sidecar, then refreshes the caches that derive from the index.
    ///
    /// <para>Split out of <see cref="SaveIndexUnsafe"/> so an ADDITIVE writer can commit without
    /// inheriting the orphan sweep. Taking ownership of the list instance is unchanged — it
    /// becomes the cache, so the caller must not retain a reference or mutate it afterwards.</para>
    ///
    /// <para>Caller must already hold the scope's exclusive lock.</para>
    /// </summary>
    private void PublishIndexUnsafe(List<ArtifactEntry> entries, string scope)
    {
        // Normalize BEFORE anything else reads this list, because this list becomes the cache.
        // WriteSidecarCore normalizes a LOCAL copy on its way to disk, so disk is always
        // "attributes"-keyed; without this, the cache below would keep whatever the caller handed
        // over — the same cache-diverges-from-disk defect the load seam and Upsert's cache writes
        // exist to prevent. In place rather than into a copy: this method takes ownership per its
        // summary, and the list is not yet reachable through _indexCache.
        for (int i = 0; i < entries.Count; i++)
            entries[i] = entries[i].NormalizeAttributesKey();

        string storeDir = GetStoreDirUnderLease(scope);
        Directory.CreateDirectory(storeDir);

        WriteSidecars(entries, storeDir);

        _scopeIndexCache.Replace(scope, entries);

        if (scope.StartsWith("local:", StringComparison.Ordinal))
            _topicDiscoveryService.InvalidateCachedTopics();

        InvalidateEntityFacts();
    }

    public void Upsert(ArtifactEntry entry, string scope = "local")
    {
        using WorkspaceLease lease = EnterWorkspaceMutation();
        WithExclusiveLock(lease, scope, () => { UpsertUnderLease(lease, entry, scope, null); return true; });
    }

    private void UpsertUnderLease(WorkspaceLease lease, ArtifactEntry entry, string scope, string? operationId)
    {
        lease.AssertValid(_workspaceRoot);
            entry = ArtifactIdentityMapper.CanonicalizeEntryForScope(entry, scope);
            string storeDir = GetStoreDirUnderLease(scope);
            Directory.CreateDirectory(storeDir);

            // A scope counts as "new" for topic-discovery purposes when it currently holds no
            // sidecars — covers both a directory that did not exist yet AND one left behind,
            // empty, by a prior Remove that emptied it (Remove deletes only the .meta.json
            // files, never the directory). Checked AFTER Directory.CreateDirectory just above
            // (idempotent — adds no files) but BEFORE WriteSidecar below, whose job is exactly
            // to add the first file.
            //
            // Deliberately NOT `!Directory.Exists(storeDir)` computed before CreateDirectory:
            // WithExclusiveLock already ran EnsureMaterialized, which unconditionally creates
            // storeDir before this body executes at all, so by the time this body runs the
            // directory always exists regardless of whether THIS write is the scope's first —
            // that check would never fire.
            bool scopeWasEmpty = !Directory.EnumerateFiles(storeDir, "*.meta.json").Any();

            // Refuse a single entry this build must not rewrite. No index read: `entry` is the
            // parameter, and BuildDerivedEntry (Task 3b) is what puts the inherited version on it
            // for a re-derived write, so the check needs nothing beyond what the caller already
            // handed in. This is the backstop for every route into a sidecar write OTHER than
            // StoreAsync's own earlier, user-facing check (see StoreAsync) and WriteSidecars'
            // whole-list pre-check (see WriteSidecars).
            if (ArtifactSidecarPolicy.IsReadOnly(entry))
                throw new ScopeReadOnlyException(
                    scope, entry.Name, entry.SchemaVersion ?? 1, DurableFormats.ArtifactSidecar.Current);

            // Write the sidecar for this single entry
            WriteSidecar(entry, storeDir, operationId);

            // Copy-on-write cache update. Never re-read the scope's sidecars here: that made a
            // single write cost a full directory enumeration plus one read and JSON parse per
            // entry, so writing N memories cost O(N^2) reads (finding H5).
            //
            // MUST install a NEW CachedIndex over a NEW list. LoadIndex hands Entries out BY
            // REFERENCE, and that is safe only because every cache write replaces the whole
            // object rather than mutating it — see
            // docs/superpowers/analysis/2026-08-10-loadindex-aliasing-and-lock.md.
            //
            // When the scope is not cached, leave it absent rather than installing a cache
            // holding only this entry; the next read cold-loads the whole scope.
            //
            // ORDERING (decision, see task-2-report.md): replace-in-slot for an existing name,
            // append for a new one — option (a), not a sort-both-paths scheme. The cold path
            // (LoadEntriesFromSidecarsRaw) never sorts; it yields raw Directory.EnumerateFiles
            // order, which .NET does not document as stable or alphabetical. No caller of
            // LoadIndex/ListScoped/GatherTopicInfos/ListTopicArtifacts sorts or otherwise
            // depends on a particular order for correctness — they consume whatever order
            // arrives, which was already filesystem-arbitrary before this change. Appending a
            // new entry (or replacing one in place) therefore cannot violate an ordering
            // contract that never existed.
            if (_scopeIndexCache.TryGet(scope, out var cachedIndex))
            {
                // Cache the NORMALIZED entry, not the caller's raw parameter. WriteSidecarCore
                // calls entry.NormalizeAttributesKey() on a LOCAL copy before serializing, so disk
                // always gets the "attributes"-keyed form — but that normalization never escaped
                // back to this method. Caching `entry` unchanged would let a LegacyFields-bearing
                // entry (still carrying data under the old "fields" key, Attributes == null) sit
                // in the cache while disk holds the normalized form: exactly the divergence the
                // load seam (LoadEntriesFromSidecars) exists to prevent, reintroduced on the write
                // side. Idempotent no-op for an already-normalized or freshly-constructed entry.
                var cacheEntry = entry.NormalizeAttributesKey();
                string target = SanitizeName(cacheEntry.Name);
                var next = new List<ArtifactEntry>(cachedIndex.Entries.Count + 1);
                bool replaced = false;
                foreach (var existing in cachedIndex.Entries)
                {
                    if (!replaced
                        && MemoryNaming.NameEquals(existing.Name, target))
                    {
                        next.Add(cacheEntry);
                        replaced = true;
                        continue;
                    }
                    next.Add(existing);
                }
                if (!replaced) next.Add(cacheEntry);
                _scopeIndexCache.Replace(scope, next);
            }
            else
            {
                _scopeIndexCache.Remove(scope);
            }

            // Only a scope transitioning from empty (no sidecars) to non-empty changes the
            // topic list; writing into an already-populated topic does not. Blanket
            // invalidation here forced DiscoverTopics to re-enumerate directories on every
            // write (finding H5).
            if (scopeWasEmpty && scope.StartsWith("local:", StringComparison.Ordinal))
                _topicDiscoveryService.InvalidateCachedTopics();

            InvalidateEntityFacts();
    }

    internal bool Remove(string name, string scope = "local")
    {
        using WorkspaceLease lease = EnterWorkspaceMutation();
        return WithExclusiveLock(lease, scope, () => RemoveUnderLease(lease, name, scope));
    }

    private bool RemoveUnderLease(WorkspaceLease lease, string name, string scope)
    {
        lease.AssertValid(_workspaceRoot);
            string storeDir = GetStoreDirUnderLease(scope);

            // Exact-name probe with a case-insensitive fallback scan — see ResolveSidecarPath for
            // the ruling on why the fallback exists and how its tie-break is decided. Pinned by
            // CaseSensitiveFileStoreTests.Remove_DeletesCaseMismatchedName.
            string? metaPath = ResolveSidecarPath(storeDir, SanitizeName(name));
            if (metaPath is null)
                return false;

            // Delete the sidecar file
            DurableWriter.DeleteSidecar(metaPath);

            // Copy-on-write cache update — see the matching comment in Upsert for why this must
            // install a NEW CachedIndex and must not re-read the scope's sidecars.
            //
            // Match against the sidecar that was actually deleted, not necessarily the caller's
            // raw `name`: the case-mismatch fallback can resolve `name` to a differently-cased
            // sidecar on a case-sensitive filesystem (see ResolveSidecarPath). metaPath's file
            // name IS the sanitized stem WriteSidecar used to write it, so compare against that
            // directly instead of re-deriving it from the caller's argument.
            //
            // FIRST MATCH ONLY, the same `!removed &&` guard Upsert's loop carries, and here it is
            // load-bearing rather than defensive: exactly ONE file was deleted, so exactly one
            // entry may leave the cache. A case-sensitive filesystem holding pre-branch data can
            // carry both "Note.meta.json" and "note.meta.json" — the state the tie-break in
            // ResolveSidecarPath exists for — and dropping every case-insensitive match would
            // evict BOTH twins while deleting one file, leaving the survivor's sidecar on disk but
            // invisible to LoadIndex/search/list for the rest of the process's life (nothing
            // re-reads the scope on a write any more). Which of two twins the loop drops is not
            // pinned down: the cache can be left showing the survivor under the deleted twin's
            // casing until the next cold load. That is cosmetic — the entry count is right and the
            // surviving sidecar stays reachable — where losing the survivor entirely is not.
            if (_scopeIndexCache.TryGet(scope, out var cachedIndex))
            {
                string target = Path.GetFileName(metaPath)[..^".meta.json".Length];
                var next = new List<ArtifactEntry>(cachedIndex.Entries.Count);
                bool dropped = false;
                foreach (var existing in cachedIndex.Entries)
                {
                    if (!dropped
                        && MemoryNaming.NameEquals(existing.Name, target))
                    {
                        dropped = true;
                        continue;
                    }
                    next.Add(existing);
                }
                _scopeIndexCache.Replace(scope, next);
            }
            else
            {
                _scopeIndexCache.Remove(scope);
            }

            // Invalidate topic discovery cache only when removing this entry emptied the scope.
            // There is no TTL to fall back on — _cachedTopics is invalidated event-driven and
            // nothing else clears it — so without invalidating here, DiscoverTopics would keep
            // reporting a topic with no remaining entries for the rest of the process's life.
            // This is the mirror image of Upsert's scope-lifecycle check (first sidecar gained
            // there, last sidecar lost here); SaveIndex clears unconditionally instead, because it
            // rewrites a scope's whole sidecar set and can empty it outright. Narrowed to fire
            // only WHEN the scope is now empty: a removal that leaves other entries behind cannot
            // change the topic list. Probe the filesystem rather than the cache — the cache may be
            // absent, and DiscoverTopics answers from the filesystem, so the filesystem is the
            // authority for this question.
            if (scope.StartsWith("local:", StringComparison.Ordinal)
                && !Directory.EnumerateFiles(storeDir, "*.meta.json").Any())
                _topicDiscoveryService.InvalidateCachedTopics();

            InvalidateEntityFacts();

            return true;
    }

    /// <summary>
    /// Atomically load-mutate-persist the entry for <paramref name="subject"/> in <paramref
    /// name="scope"/> under a single acquisition of the per-scope write lock. Returns false when
    /// the entry is missing or <paramref name="mutate"/> returns null (no write). This is the seam
    /// the async typing sink uses to write attribute promotions back — it fires NO event sinks.
    /// Resolves identity from the sidecars themselves (case-insensitively, like every other name
    /// match in this type) and reuses <see cref="Upsert"/>'s persist/invalidate tail.
    ///
    /// <para><b>The entry handed to <paramref name="mutate"/> comes off disk, inside the lock.</b>
    /// That is what makes "load-mutate-persist" true across processes rather than only within one:
    /// the lock <see cref="WithExclusiveLock"/> takes is a real cross-process file lock, and this
    /// method writes back a COMPLETE entry derived from what it read, so a lookup served from
    /// <c>_indexCache</c> — which nothing invalidates across processes — would silently roll back
    /// another writer's edits to every field the mutation does not itself touch.</para>
    /// </summary>
    internal bool TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        return WithExclusiveLock(scope, () =>
        {
            string storeDir = GetStoreDirUnderLease(scope);

            // DISK-AUTHORITATIVE, and it has to be. This does not merely READ the entry: `mutate`
            // and MemoryDerivation.ForMetadataUpdate below build the full entry that gets written
            // back FROM it, so every field the mutation leaves alone (ArtifactKey, OriginalBytes,
            // ChunkEntries, ContentPreview, TermFrequencies, ...) is copied from whatever this
            // lookup returned. Serving it from `_indexCache` would therefore not just answer with
            // stale data, it would PERSIST stale data over a concurrent writer's — and staleness
            // here is not hypothetical: nothing invalidates _indexCache across processes (see
            // LoadIndex's fast lane), and this repository's own `.scrinia/memories/**/*.meta.json`
            // are git-tracked, so a `git pull` under a live `scri serve` is enough to make every
            // warm entry in that scope stale. The cross-process lock this body runs under exists
            // precisely to sequence this read against another process's write; reading in-memory
            // state inside it throws away the only thing the lock is for.
            //
            // ONE sidecar read, not the whole scope. ResolveSidecarPath answers the identity
            // question with a single stat on the common path (only a genuine miss pays a directory
            // enumeration), and it carries the same case-insensitive fallback Remove uses, so
            // "stored as MixedCase, looked up as mixedcase" resolves here as it does there —
            // pinned by CaseSensitiveFileStoreTests.TryUpdateEntry_ResolvesCaseMismatchedSubject,
            // which runs against a directory carrying NTFS's per-directory case-sensitivity flag
            // and SKIPS (never silently passes) where the flag cannot be obtained. This is O(1) in
            // scope size, which is the property finding H5 is about; the warm-cache read that
            // stood here briefly bought a list scan over a file read and paid for it with the
            // guarantee above.
            //
            // "Entry genuinely missing -> false, and mutate never runs" is unchanged: a scope
            // directory that does not exist, or holds no matching sidecar, yields null here.
            // Pinned by TryUpdateEntryTests.MissingEntry_ReturnsFalse.
            //
            // LoadSidecar is a load seam and normalizes legacy "fields" itself, so the mutator
            // always sees Attributes populated — this path (unlike LoadIndexUnsafe) has no
            // AttributeMigration.MigratesTo forward-fill of its own.
            string? metaPath = ResolveSidecarPath(storeDir, SanitizeName(subject));
            var current = metaPath is null ? null : LoadSidecar(metaPath);
            if (current is not null)
                current = ArtifactIdentityMapper.CanonicalizeEntryForScope(current, scope);
            if (current is null) return false;

            var updated = mutate(current);
            if (updated is null) return false;

            // The store owns derivation, including on a metadata-only write — see
            // IMemoryStore.TryUpdateEntry.
            updated = MemoryDerivation.ForMetadataUpdate(current, updated);

            // SKIP, do not throw. This is a DERIVED, best-effort write — the callers are detached
            // background handlers (MaintenanceEventSink, EntityTypingEventHandler) where a thrown
            // ScopeReadOnlyException has no handler and becomes an unobserved fault. Its contract
            // is already "may not have updated" (see the missing-entry case just above); a newer
            // sidecar is one more reason it did not. Rule: derived, best-effort writes SKIP;
            // user-initiated writes (StoreAsync, AppendAsync, Upsert) REFUSE.
            if (ArtifactSidecarPolicy.IsReadOnly(updated)) return false;

            WriteSidecar(updated, storeDir);

            // Copy-on-write cache update — see the matching comment in Upsert for why this must
            // install a NEW CachedIndex and must not re-read the scope's sidecars.
            //
            // Cache the NORMALIZED entry, exactly as Upsert's cache write does, and for the same
            // reason: disk always gets the "attributes"-keyed form (WriteSidecarCore normalizes a
            // local copy before serializing) and the cache must not diverge from it. `current` is
            // normalized — LoadSidecar is a load seam — so on today's paths this only has to
            // survive `mutate` handing back an entry it constructed with LegacyFields set. That is
            // reachable through the public delegate, and normalizing costs nothing when there is
            // nothing to normalize. Pinned by IndexCacheWritePathTests.
            var cacheEntry = updated.NormalizeAttributesKey();
            if (_scopeIndexCache.TryGet(scope, out var cachedIndex))
            {
                string target = SanitizeName(cacheEntry.Name);
                var next = new List<ArtifactEntry>(cachedIndex.Entries.Count + 1);
                bool replaced = false;
                foreach (var existing in cachedIndex.Entries)
                {
                    if (!replaced
                        && MemoryNaming.NameEquals(existing.Name, target))
                    {
                        next.Add(cacheEntry);
                        replaced = true;
                        continue;
                    }
                    next.Add(existing);
                }
                if (!replaced) next.Add(cacheEntry);
                _scopeIndexCache.Replace(scope, next);
            }
            else
            {
                _scopeIndexCache.Remove(scope);
            }

            // No _cachedTopics invalidation, and that is the same scope-lifecycle rule Upsert and
            // Remove follow rather than an omission. DiscoverTopics lists a directory as a topic
            // exactly when it holds at least one .nmp2 or .meta.json; a successful TryUpdateEntry
            // requires an existing sidecar, so the scope was non-empty before this write, and this
            // write only ever adds or replaces a sidecar — even a name-changing `mutate` leaves the
            // original in place and writes a second one. The topic list therefore cannot change.
            // Clearing it unconditionally cost the next ResolveReadScopes/search a full re-walk of
            // the memories/ tree — repeatedly, since MaintenanceEventSink calls this once per
            // memory the stored content references plus once or twice for the orphan marker, on
            // every store event. H5's own shape, on the branch that removes H5.

            InvalidateEntityFacts();

            return true;
        });
    }

    // ── The write surface: raw input in, derived entry out ───────────────────

    private sealed record PrecomputedStore(
        DerivedMetadata Derived, string Artifact, int ChunkCount, long OriginalBytes);

    private PrecomputedStore PrecomputeForStore(MemoryWrite write, bool isEphemeral)
    {
        // The one call. `write.Keywords` is the caller's RAW list by MemoryWrite's contract (D-01);
        // nothing this method computes is ever fed back in as input.
        var derived = MemoryDerivation.ForStore(
            write.RawContent, write.Description, write.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), write.Keywords.ToArray(),
            isEphemeral: isEphemeral,
            fileHasher: HashWorkspaceFile);

        string artifact = write.RawContent.Length == 1
            ? Nmp2ChunkedEncoder.Encode(write.RawContent[0])
            : Nmp2ChunkedEncoder.EncodeChunks(write.RawContent);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(string.Concat(write.RawContent));

        return new PrecomputedStore(derived, artifact, chunkCount, originalBytes);
    }

    private async Task<ArtifactEntry> PersistEphemeralAsync(MemoryWrite write, CancellationToken ct)
    {
        string ephemeralKey = MemoryNaming.StripEphemeralPrefix(write.Name);
        EphemeralEntry? existingEph = GetEphemeral(ephemeralKey);
        PrecomputedStore pre = PrecomputeForStore(write, isEphemeral: true);
        return PersistEphemeral(write.Name, pre.Artifact, pre.OriginalBytes, pre.ChunkCount, pre.Derived,
            createdAt: existingEph?.CreatedAt ?? DateTimeOffset.UtcNow,
            updatedAt: existingEph is not null ? DateTimeOffset.UtcNow : null);
    }

    /// <inheritdoc/>
    public async Task<ArtifactEntry> StoreAsync(MemoryWrite write, CancellationToken ct = default)
        => await StorePersistentOrEphemeralAsync(write, ct);

    internal async Task<(ArtifactEntry Entry, WholeMemoryResult? Transaction)> StoreWithTransactionResultAsync(
        MemoryWrite write, CancellationToken ct = default)
    {
        WholeMemoryResult? transaction = null;
        ArtifactEntry entry = await StorePersistentOrEphemeralAsync(write, ct,
            result => transaction = result).ConfigureAwait(false);
        return (entry, transaction);
    }

    private async Task<ArtifactEntry> StorePersistentOrEphemeralAsync(
        MemoryWrite write, CancellationToken ct, Action<WholeMemoryResult>? transactionResult = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        write.ValidateOrThrow(_writeValidationOptions);   // plain validation — the appendOperationId
                                                        // annotation catch (was :2378–2382) is dead
                                                        // and removed.
        if (write.Address.Kind == MemoryAddressKind.Ephemeral)
            return await PersistEphemeralAsync(write, ct);
        return await StorePersistentAsync(write, ct, transactionResult);
    }

    private async Task<ArtifactEntry> CreateForAppendAsync(
        WorkspaceLease lease, MemoryWrite write, string appendOperationId, CancellationToken ct)
    {
        lease.AssertValid(_workspaceRoot);
        PrecomputedStore pre = PrecomputeForStore(write, isEphemeral: false);
        var derived = pre.Derived;
        var artifact = pre.Artifact;
        var chunkCount = pre.ChunkCount;
        var originalBytes = pre.OriginalBytes;

        // The typed address is the canonical identity. `MemoryWrite.Scope` is retained as the
        // resolved pair for legacy callers, but a public write can still carry the default
        // "local" scope alongside a qualified address. Deriving both values here prevents the
        // body/sidecar from landing in the root scope while ReadArtifactAsync correctly resolves
        // the address to its topic scope.
        var (scope, subject) = ResolveAddress(write.Address);

        // Case-insensitive, and the resolved entry's own casing travels forward. SanitizeName does
        // not fold case, so an ordinal match here reports "new memory" for a re-store under a
        // different casing — which skips the archive below and resets CreatedAt while the write
        // still lands on the same file on a case-insensitive filesystem. Matching the name is only
        // half of it: everything below turns `subject` back into a path (ArchiveVersion,
        // WriteArtifactAsync, the sidecar written by Upsert, and the entry's own Name), so leaving
        // the caller's casing here would leave the fix depending on the filesystem to fold case on
        // its behalf — right on NTFS, and a twin alongside the original anywhere case-sensitive.
        // Twelve sites of this exact shape were fixed across two earlier branches; this is not one.
        var existingEntry = LoadIndexUnderExclusiveAppendLock(scope)
            .FirstOrDefault(
                e => MemoryNaming.NameEquals(e.Name, subject));
        if (existingEntry is not null)
            subject = existingEntry.Name;

        // A sidecar without its artifact is an orphan, not an overwrite target. Preserve the
        // sidecar as a raw before-image while giving this create the same StoreAsync semantics as
        // any other missing target.
        if (appendOperationId is not null && !File.Exists(ArtifactPath(subject, scope)))
            existingEntry = null;

        DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;
        DateTimeOffset? updatedAt = existingEntry is not null ? DateTimeOffset.UtcNow : null;

        // The user-facing gate, and the earliest one: refuse BEFORE any side effect. Without this,
        // a write destined to be refused at Upsert has already deposited a versions/ archive entry
        // (below) and a new artifact body for a memory it never changed, relying on the rollback
        // path to restore the body -- a rollback that decodes and re-encodes rather than restoring
        // bytes, so byte-identity would become a property of the encoder rather than of the
        // refusal. Same principle as WriteSidecars' whole-list pre-check -- refuse before the
        // first side effect -- applied at the outermost caller. Upsert's own check stays: it is
        // the backstop for every other route into a sidecar write.
        if (existingEntry is not null && ArtifactSidecarPolicy.IsReadOnly(existingEntry))
            throw new ScopeReadOnlyException(
                scope, existingEntry.Name, existingEntry.SchemaVersion ?? 1,
                DurableFormats.ArtifactSidecar.Current);

        // Captured BEFORE the overwrite so a failed Upsert can put the memory's previous content
        // back. Leaving the new bytes in place instead is not a benign no-op: the artifact would
        // hold the new content while the sidecar still described the old, and that divergence
        // propagates — a later AppendAsync reads the stale entry's ChunkEntries but numbers the
        // appended one from the real chunk count, yielding a non-contiguous [1, 4]. Usually free:
        // ReadArtifactAsync is served by _artifactCache, and on a re-store the artifact is
        // typically already resident.
        string? previousArtifact = null;
        if (existingEntry is not null)
        {
            try { previousArtifact = await File.ReadAllTextAsync(ArtifactPath(subject, scope), ct); }
            catch (FileNotFoundException) { /* index knows the entry but the file is gone */ }
            ArchiveVersionUnderLease(lease, subject, scope);
        }

        var entry = BuildDerivedEntry(subject, scope, originalBytes, chunkCount, createdAt, updatedAt, derived,
            prior: existingEntry);

        AppendTransaction? createTransaction = null;
        bool appendCommitted = false;
        if (appendOperationId is not null && existingEntry is null)
        {
            // Creation is normally the same write surface as StoreAsync, but once its append
            // operation has a durable identity both live members need a frozen before-image. A
            // sidecar can exist without an artifact after an interrupted older write; capturing it
            // here means recovery preserves that unrelated-to-this-operation member instead of
            // treating it as newly created. The shared raw capture seam also makes this boundary
            // deterministic under the append fault injector.
            RawMemberSnapshot oldArtifact = await CaptureRawMemberAsync(
                ArtifactPath(subject, scope), AppendFaultPoint.RawArtifactRead, appendOperationId, ct);
            RawMemberSnapshot oldSidecar = await CaptureRawMemberAsync(
                MetaPath(subject, scope), AppendFaultPoint.RawSidecarRead, appendOperationId, ct);
            LastCapturedOldArtifactBytesForTest = oldArtifact.Bytes;
            LastCapturedOldSidecarBytesForTest = oldSidecar.Bytes;
            byte[] newArtifactBytes = System.Text.Encoding.UTF8.GetBytes(artifact);
            byte[] newSidecarBytes = System.Text.Encoding.UTF8.GetBytes(SerializeSidecar(entry));
            createTransaction = new AppendTransaction(
                appendOperationId, entry.Address, scope, true, oldArtifact, oldSidecar,
                RawMemberSnapshot.Capture(oldArtifact.Path, true, newArtifactBytes),
                RawMemberSnapshot.Capture(oldSidecar.Path, true, newSidecarBytes),
                new ArchivePlan(false, null, null, ArchiveMemberStatus.NotRequired,
                    ArchiveMemberStatus.NotRequired, []), DateTimeOffset.UtcNow);
            LastPreparedAppendTransactionForTest = createTransaction;
            _appendJournalWriter.PersistAppendJournal(createTransaction, AppendJournalState.Prepared);
        }

        try
        {
            if (createTransaction is not null)
                _appendJournalWriter.PersistAppendJournal(createTransaction, AppendJournalState.NewPairInProgress);
            await WriteArtifactUnderLeaseAsync(lease, subject, scope, artifact, appendOperationId, ct);
            UpsertUnderLease(lease, entry, scope, appendOperationId);

            if (createTransaction is not null)
            {
                await _memberVerifier.VerifyAsync(createTransaction.NewArtifact, appendOperationId!, ct);
                await _memberVerifier.VerifyAsync(createTransaction.NewSidecar, appendOperationId!, ct);
                string committedHash = Convert.ToHexStringLower(SHA256.HashData(
                    createTransaction.NewArtifact.Bytes.Concat(createTransaction.NewSidecar.Bytes).ToArray()));
                _appendJournalWriter.PersistAppendJournal(createTransaction, AppendJournalState.Committed, committedHash);
                appendCommitted = true;
                _appendJournalWriter.DeleteAppendJournal(createTransaction);
            }
        }
        catch (Exception exception) when (appendCommitted)
        {
            throw new AppendPostCommitCleanupException(appendOperationId!, entry, exception);
        }
        catch
        {
            // A brand-new memory leaves an orphaned artifact, so removing it is the revert. An
            // existing one is RESTORED instead: Upsert does real file I/O and can fail transiently,
            // and deleting would make that permanent — nothing reads versions/ back, so the user
            // would have no recovery path for a memory they only asked to overwrite. Same rule as
            // AppendAsync below. The destructive form of this block — an unconditional delete —
            // is what the server's own store path did; it was deleted, not ported.
            // CancellationToken.None: a cancelled store must still be rolled back.
            if (createTransaction is not null)
            {
                if (!_rollbackExecutor.TryRestoreAppendTransaction(createTransaction))
                {
                    try
                    {
                        _appendJournalWriter.PersistAppendJournal(createTransaction, AppendJournalState.RecoveryRequired,
                            detail: "Create rollback could not prove both original live members were restored.");
                    }
                    catch
                    {
                        // Retain the existing journal when recovery-state persistence itself fails.
                    }
                    throw AppendRecoveryBuilder.RecoveryRequired(
                        AppendJournalCodec.GetJournalPath(Path.Combine(GetStoreDirUnderLease(scope), "versions")),
                        "Create rollback could not prove both missing live members were restored.",
                        operationId: appendOperationId!);
                }
                try
                {
                    _appendJournalWriter.PersistAppendJournal(createTransaction, AppendJournalState.RolledBack);
                    _appendJournalWriter.DeleteAppendJournal(createTransaction);
                }
                catch (Exception recoveryException)
                {
                    throw AppendRecoveryBuilder.RecoveryRequired(
                        AppendJournalCodec.GetJournalPath(Path.Combine(GetStoreDirUnderLease(scope), "versions")),
                        "Create rollback completed but the journal could not be removed.",
                        recoveryException, appendOperationId!);
                }
            }
            else
            {
                try
                {
                    if (previousArtifact is not null)
                        await WriteArtifactUnderLeaseAsync(lease, subject, scope, previousArtifact, null, CancellationToken.None);
                    else
                        DeleteArtifactUnsafe(subject, scope);
                }
                catch { /* best-effort rollback */ }
            }
            throw;
        }

        return entry;
    }

    private async Task<ArtifactEntry> StorePersistentAsync(
        MemoryWrite write, CancellationToken cancellationToken,
        Action<WholeMemoryResult>? transactionResult = null)
    {
        using WorkspaceLease lease = EnterWorkspaceMutation(ct: cancellationToken);
        PrecomputedStore pre = PrecomputeForStore(write, isEphemeral: false);
        var (scope, subject) = ResolveAddress(write.Address);
        EnsureMaterialized(scope);
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
        string storeDirectory = GetStoreDirUnderLease(scope);
        bool retained = RecoverScopeUnsafe(lease, scope, storeDirectory, cancellationToken: cancellationToken);
        RefuseMutationWithRetainedEvidence(scope, storeDirectory, retained, cancellationToken);
        await _recoveryConductor.RecoverAppendUnsafeAsync(scope, storeDirectory, "store-recovery", cancellationToken).ConfigureAwait(false);

        ArtifactEntry? existingEntry = LoadIndexUnderExclusiveAppendLock(scope)
            .FirstOrDefault(entry => MemoryNaming.NameEquals(entry.Name, subject));
        if (existingEntry is not null)
            subject = existingEntry.Name;

        if (existingEntry is not null && ArtifactSidecarPolicy.IsReadOnly(existingEntry))
            throw new ScopeReadOnlyException(scope, existingEntry.Name, existingEntry.SchemaVersion ?? 1,
                DurableFormats.ArtifactSidecar.Current);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset createdAt = existingEntry?.CreatedAt ?? startedAt;
        DateTimeOffset? updatedAt = existingEntry is null ? null : startedAt;
        ArtifactEntry entry = BuildDerivedEntry(subject, scope, pre.OriginalBytes, pre.ChunkCount,
            createdAt, updatedAt, pre.Derived, existingEntry);

        RawMemberSnapshot oldArtifact = await CaptureWholeMemberAsync(
            ArtifactPath(subject, scope), WholeMemoryFaultPoint.RawArtifactRead, cancellationToken)
            .ConfigureAwait(false);
        RawMemberSnapshot oldSidecar = await CaptureWholeMemberAsync(
            MetaPath(subject, scope), WholeMemoryFaultPoint.RawSidecarRead, cancellationToken)
            .ConfigureAwait(false);
        byte[] newArtifactBytes = System.Text.Encoding.UTF8.GetBytes(pre.Artifact);
        byte[] newSidecarBytes = System.Text.Encoding.UTF8.GetBytes(SerializeSidecar(entry));
        ArchivePlan archive = BuildArchivePlan(subject, scope, existingEntry is not null,
            oldSidecar.Existed, startedAt);
        string[] wholePruneCandidates = archive.Required
            ? BuildWholePruneCandidates(subject, scope)
            : [];
        var transaction = new WholeMemoryTransaction(
            Guid.NewGuid().ToString("N"), scope, write.Address.ToString(), false,
            oldArtifact, oldSidecar,
            RawMemberSnapshot.Capture(oldArtifact.Path, true, newArtifactBytes),
            RawMemberSnapshot.Capture(oldSidecar.Path, true, newSidecarBytes),
            new ArchivePairPlan(archive.Required, archive.ArtifactArchivePath, archive.SidecarArchivePath,
                wholePruneCandidates), startedAt);

#pragma warning disable CA1859 // Keep the production transaction boundary interface-typed.
        IWholeMemoryTransactionCoordinator coordinator = _wholeMemoryTransactions;
#pragma warning restore CA1859
        WholeMemoryResult result = await coordinator.ExecuteAsync(transaction, async ct =>
        {
            if (archive.Required)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(archive.ArtifactArchivePath!)!);
                InjectWholeFault(WholeMemoryFaultPoint.ArchiveArtifactWrite, archive.ArtifactArchivePath!);
                DurableFile.AtomicWriteAllBytes(archive.ArtifactArchivePath!, oldArtifact.Bytes);
                if (oldSidecar.Existed && archive.SidecarArchivePath is not null)
                {
                    InjectWholeFault(WholeMemoryFaultPoint.ArchiveSidecarWrite, archive.SidecarArchivePath);
                    DurableFile.AtomicWriteAllBytes(archive.SidecarArchivePath, oldSidecar.Bytes);
                }
            }

            ct.ThrowIfCancellationRequested();
            InjectWholeFault(WholeMemoryFaultPoint.LiveArtifactWrite, transaction.NewArtifact.Path);
            await WriteArtifactUnderLeaseAsync(lease, subject, scope, pre.Artifact, transaction.OperationId, ct)
                .ConfigureAwait(false);
            InjectWholeFault(WholeMemoryFaultPoint.LiveSidecarWrite, transaction.NewSidecar.Path);
            UpsertUnderLease(lease, entry, scope, transaction.OperationId);
            InjectWholeFault(WholeMemoryFaultPoint.VerificationRead, transaction.NewArtifact.Path);
            // Publication verification is compensation-safe: once the live pair is visible,
            // cancellation must not turn a committed write into a rollback.
            VerifyWholeMember(transaction.NewArtifact, CancellationToken.None);
            VerifyWholeMember(transaction.NewSidecar, CancellationToken.None);
        }, cancellationToken).ConfigureAwait(false);
        transactionResult?.Invoke(result);

        return entry;
    }

    /// <summary>
    /// Executes a file-entity publication through the shared coordinator seam. Artifact writes use
    /// <see cref="IWholeMemoryTransactionCoordinator"/> above; file entities are kept as a separate
    /// pair boundary for the skill/agent publishers that own their sidecar policy.
    /// </summary>
    internal Task<FileEntityMutationResult> ExecuteFileEntityTransactionAsync(
        FileEntityMutation mutation, CancellationToken cancellationToken = default) =>
        ExecuteFileEntityTransactionThroughBoundary(mutation, cancellationToken);

    private async Task<FileEntityMutationResult> ExecuteFileEntityTransactionThroughBoundary(
        FileEntityMutation mutation, CancellationToken cancellationToken)
    {
#pragma warning disable CA1859 // Keep the production transaction boundary interface-typed.
        IFileEntityTransactionCoordinator coordinator = _fileEntityTransactions;
#pragma warning restore CA1859
        return await coordinator.ExecuteAsync(mutation, cancellationToken).ConfigureAwait(false);
    }

    internal string[] PruneWholeArchives(IReadOnlyList<string> candidates, string operationId)
    {
        var failures = new List<string>();
        Exception? injected = B0FaultSeam.ThrowIfInjectedOrNull(
                B0FaultSeam.MapWholeMemoryFaultPoint(WholeMemoryFaultPoint.ArchiveCleanup),
                WholeMemoryFaultPoint.ArchiveCleanup.ToString())
            ?? WholeMemoryFaultInjector?.Invoke(WholeMemoryFaultPoint.ArchiveCleanup, operationId);
        if (injected is not null)
            return [$"archive cleanup: {injected.Message}"];

        foreach (string artifactPath in candidates)
        {
            try
            {
                File.Delete(artifactPath);
                if (artifactPath.EndsWith(".nmp2", StringComparison.OrdinalIgnoreCase))
                    File.Delete(Path.ChangeExtension(artifactPath, null) + ".meta.json");
            }
            catch (Exception exception)
            {
                failures.Add($"{artifactPath}: {exception.Message}");
            }
        }

        return failures.ToArray();
    }

    private string[] BuildWholePruneCandidates(string subject, string scope)
    {
        string versionsDirectory = Path.Combine(GetStoreDirUnderLease(scope), "versions");
        if (!Directory.Exists(versionsDirectory))
            return [];

        string safe = SanitizeName(subject);
        return Directory.GetFiles(versionsDirectory, $"{safe}_*.nmp2")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Skip(9)
            .SelectMany(path => new[] { path, Path.ChangeExtension(path, null) + ".meta.json" })
            .ToArray();
    }

    private async Task<RawMemberSnapshot> CaptureWholeMemberAsync(
        string path, WholeMemoryFaultPoint faultPoint, CancellationToken cancellationToken)
    {
        InjectWholeFault(faultPoint, path);
        RecoveryVerificationResult verification = await RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("workspace", _workspaceRoot), RecoveryEvidenceKind.AppendJournal,
            path, default, MaintenanceLimits.Hard, cancellationToken).ConfigureAwait(false);
        if (verification.Outcome == RecoveryVerificationOutcome.ProtectedMissing)
            return RawMemberSnapshot.Capture(path, false, []);
        if (verification.Outcome != RecoveryVerificationOutcome.Verified || verification.SourceContent is null)
            throw new IOException($"Whole-memory member could not be captured safely: {path}");
        using (verification.SourceContent)
            return RawMemberSnapshot.Capture(path, true, verification.SourceContent.Bytes.ToArray());
    }

    private void VerifyWholeMember(RawMemberSnapshot expected, CancellationToken cancellationToken)
    {
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("workspace", _workspaceRoot), RecoveryEvidenceKind.AppendJournal,
            expected.Path, new RecoveryExpectedHash(expected.Sha256), MaintenanceLimits.Hard,
            cancellationToken).AsTask().GetAwaiter().GetResult();
        if (verification.Outcome != RecoveryVerificationOutcome.Verified)
            throw new IOException($"Whole-memory member verification failed: {expected.Path}");
        verification.SourceContent?.Dispose();
    }

    /// <inheritdoc/>
    public async Task<AppendStoreResult> AppendAsync(MemoryWrite write, CancellationToken ct = default)
    {
        string operationId = Guid.NewGuid().ToString("N");
        try
        {
            ArgumentNullException.ThrowIfNull(write);
            write.ValidateOrThrow(_writeValidationOptions, isAppend: true);
        }
        catch (InvalidMemoryWriteException exception)
        {
            exception.AppendOperationId = operationId;
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new AppendOperationCanceledException(operationId, exception);
        }
        catch (Exception exception)
        {
            throw new AppendStoreException(AppendStoreErrorCode.Internal, operationId,
                "Append input could not be admitted.", exception);
        }
        try
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;

            bool ephemeral = write.Address.Kind == MemoryAddressKind.Ephemeral;
            if (ephemeral)
            {
                if (GetEphemeral(MemoryNaming.StripEphemeralPrefix(write.Name)) is null)
                {
                    ArtifactEntry created = await PersistEphemeralAsync(write, ct);
                    return new AppendStoreResult(AppendStoreOutcome.Created, created, operationId);
                }

                return await AppendAsyncCore(null, write, operationId, startedAt, ct);
            }

            using WorkspaceLease lease = EnterWorkspaceMutation(ct: ct);
            var resolved = ResolveAddress(write.Address);
            string scope = resolved.Scope;
            EnsureMaterialized(scope);
            using FileLock fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
            string subject = resolved.Subject;
            // Append admission shares the persistent scope with the restore journal.  Recover
            // that journal before deciding whether this is a create or an append; otherwise a
            // missing live artifact can enter CreateForAppendAsync and bypass the recovery barrier.
            string storeDirectory = GetStoreDirUnderLease(scope);
            bool retained = RecoverScopeUnsafe(lease, scope, storeDirectory, cancellationToken: ct);
            RefuseMutationWithRetainedEvidence(scope, storeDirectory, retained, ct);
            await _recoveryConductor.RecoverAppendUnsafeAsync(scope, storeDirectory, operationId, ct);
            ArtifactEntry? canonicalEntry = LoadIndexUnderExclusiveAppendLock(scope).FirstOrDefault(
                entry => MemoryNaming.NameEquals(entry.Name, subject));
            if (canonicalEntry is not null)
                subject = canonicalEntry.Name;

            if (!File.Exists(ArtifactPath(subject, scope)))
            {
                try
                {
                    ArtifactEntry created = await CreateForAppendAsync(lease, write, operationId, ct);
                    return new AppendStoreResult(AppendStoreOutcome.Created, created, operationId);
                }
                catch (AppendPostCommitCleanupException exception)
                {
                    return new AppendStoreResult(
                        AppendStoreOutcome.SuccessWithWarning,
                        exception.Entry,
                        exception.OperationId,
                        new AppendStoreWarning(
                            AppendWarningCode.PostCommitCleanupFailed,
                            "Append committed, but journal cleanup failed; retained journal evidence."));
                }
            }
            return await AppendAsyncCore(lease, write, operationId, startedAt, ct);
        }
        catch (DurableWriteException exception)
        {
            throw new DurableWriteException(exception.Operation, exception.Destination,
                exception.InnerException ?? exception, operationId);
        }
        catch (ScopeReadOnlyException exception)
        {
            throw new ScopeReadOnlyException(exception.Scope, exception.MemoryName,
                exception.DeclaredVersion, exception.SupportedVersion, operationId);
        }
        catch (OperationCanceledException exception)
        {
            if (exception is AppendOperationCanceledException)
                throw;
            throw new AppendOperationCanceledException(operationId, exception);
        }
        catch (AppendStoreException)
        {
            throw;
        }
        catch (RestoreRecoveryException)
        {
            throw;
        }
        catch (BundleImportException) { throw; }
        catch (Exception exception)
        {
            throw new AppendStoreException(
                AppendStoreErrorCode.Internal,
                operationId,
                "Append failed after establishing its operation boundary.",
                exception);
        }
    }

    private async Task<AppendStoreResult> AppendAsyncCore(
        WorkspaceLease? lease, MemoryWrite write, string operationId, DateTimeOffset startedAt, CancellationToken ct)
    {

        bool ephemeral = write.Address.Kind == MemoryAddressKind.Ephemeral;
        var (scope, subject) = ResolveAddress(write.Address);
        if (!ephemeral)
        {
            ArgumentNullException.ThrowIfNull(lease);
            lease.AssertValid(_workspaceRoot);
        }
        if (!ephemeral)
            await _recoveryConductor.RecoverAppendUnsafeAsync(scope, GetStoreDirUnderLease(scope), operationId, ct);
        ArtifactEntry? existingEntry = null;
        EphemeralEntry? existingEph = null;
        RawMemberSnapshot? oldArtifact = null;
        RawMemberSnapshot? oldSidecar = null;
        string? existingArtifact = null;

        if (ephemeral)
        {
            existingEph = GetEphemeral(MemoryNaming.StripEphemeralPrefix(write.Name));
            existingArtifact = existingEph?.Artifact;
        }
        else
        {
            existingEntry = LoadIndexUnderExclusiveAppendLock(scope).FirstOrDefault(
                e => MemoryNaming.NameEquals(e.Name, subject));
            if (existingEntry is not null)
                subject = existingEntry.Name;

            oldArtifact = await CaptureRawMemberAsync(
                ArtifactPath(subject, scope), AppendFaultPoint.RawArtifactRead, operationId, ct);
            oldSidecar = await CaptureRawMemberAsync(
                MetaPath(subject, scope), AppendFaultPoint.RawSidecarRead, operationId, ct);
            LastCapturedOldArtifactBytesForTest = oldArtifact.Bytes;
            LastCapturedOldSidecarBytesForTest = oldSidecar.Bytes;
            existingArtifact = oldArtifact.Existed
                ? System.Text.Encoding.UTF8.GetString(oldArtifact.Bytes)
                : null;
        }

        if (existingArtifact is null)
            throw new AppendStoreException(AppendStoreErrorCode.Internal, operationId,
                "Append admission found a missing live artifact after the transaction lock was acquired.");

        string appended = write.RawContent[0];
        string newArtifact = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, appended);

        // The pipeline derives over the memory's FULL post-append content with the appended chunk
        // last (D-22 reads only that last element for attributes and the new chunk entry).
        int priorChunks = Nmp2ChunkedEncoder.GetChunkCount(existingArtifact);
        string[] fullContent = new string[priorChunks + 1];
        if (priorChunks > 0)
        {
            if (Nmp2Strategy.IsMultiChunk(existingArtifact))
            {
                int count = Nmp2Strategy.ParseChunkCount(existingArtifact);
                var sections = Nmp2Strategy.ExtractRawChunkSections(existingArtifact, count);
                for (int i = 0; i < sections.Count && i < priorChunks; i++)
                {
                    byte[] bytes = Nmp2Strategy.DecodeChunkSectionFrom(sections[i], Nmp2Strategy.MaxDecodedBytes);
                    fullContent[i] = System.Text.Encoding.UTF8.GetString(bytes);
                }
            }
            else
            {
                // Single-chunk artifact: no ##CHUNK: marker exists, so keep the original decode path.
                fullContent[0] = Nmp2ChunkedEncoder.DecodeChunk(existingArtifact, 1);
            }
        }
        fullContent[priorChunks] = appended;

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(string.Concat(fullContent));

        // Everything the memory already carries that an append must not destroy. A server-side REST
        // append used to lose the curated keywords (D-12) and the drift baseline (D-21) outright.
        // EphemeralEntry now carries FileHashes too, so the fallback below matches every other field.
        var existing = new ExistingMetadata(
            Attributes: existingEntry?.Attributes ?? existingEph?.Attributes,
            Keywords: existingEntry?.Keywords ?? existingEph?.Keywords,
            ContentPreview: existingEntry?.ContentPreview ?? existingEph?.ContentPreview,
            ChunkEntries: existingEntry?.ChunkEntries ?? existingEph?.ChunkEntries,
            FileHashes: existingEntry?.FileHashes ?? existingEph?.FileHashes,
            // Null here is UNKNOWN, and ForAppend reads it that way: a memory last written before
            // the field existed keeps the old blanket carry-forward until this append rewrites it.
            AgentKeywords: existingEntry?.AgentKeywords ?? existingEph?.AgentKeywords);

        var derived = MemoryDerivation.ForAppend(
            fullContent, write.Description, write.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), write.Keywords.ToArray(), existing,
            fileHasher: HashWorkspaceFile);

        if (ephemeral)
        {
            var appendedEphemeral = PersistEphemeral(write.Name, newArtifact, originalBytes, chunkCount, derived,
                createdAt: existingEph!.CreatedAt, updatedAt: DateTimeOffset.UtcNow);
            return new AppendStoreResult(AppendStoreOutcome.Appended, appendedEphemeral, operationId);
        }

        // Same user-facing gate as StoreAsync's, and for the same reason: refuse before any side
        // effect rather than depend on the rollback path below. `existingEntry` can genuinely be
        // null here (the orphan case B7 documents -- an artifact with no sidecar), and an orphan
        // has no version to be newer, so the check is simply skipped for it.
        if (existingEntry is not null && ArtifactSidecarPolicy.IsReadOnly(existingEntry))
            throw new ScopeReadOnlyException(
                scope, existingEntry.Name, existingEntry.SchemaVersion ?? 1,
                DurableFormats.ArtifactSidecar.Current);

        // The bytes to revert to, already in hand from the read above: appending to an existing
        // memory must restore its content on failure, not delete it. Nothing reads versions/ back,
        // so a delete here would leave the user no way to recover.
        var entry = BuildDerivedEntry(subject, scope, originalBytes, chunkCount,
            createdAt: existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            derived,
            prior: existingEntry);

        byte[] newArtifactBytes = System.Text.Encoding.UTF8.GetBytes(newArtifact);
        byte[] newSidecarBytes = System.Text.Encoding.UTF8.GetBytes(SerializeSidecar(entry));
        var transaction = new AppendTransaction(
            operationId,
            entry.Address,
            scope,
            IsCreate: existingEntry is null,
            oldArtifact ?? RawMemberSnapshot.Capture(ArtifactPath(subject, scope), false, []),
            oldSidecar ?? RawMemberSnapshot.Capture(MetaPath(subject, scope), false, []),
            RawMemberSnapshot.Capture(ArtifactPath(subject, scope), true, newArtifactBytes),
            RawMemberSnapshot.Capture(MetaPath(subject, scope), true, newSidecarBytes),
            BuildArchivePlan(subject, scope, existingEntry is not null, oldSidecar?.Existed == true, startedAt),
            startedAt);
        LastPreparedAppendTransactionForTest = transaction;

        _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.Prepared);
        _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.ArchiveInProgress);

        // Archive publication is part of the frozen plan boundary. If it fails, no live member has
        // been touched and there is nothing to roll back.
        try
        {
            transaction = await PublishFrozenArchiveAsync(transaction, ct);
            LastPreparedAppendTransactionForTest = transaction;
            _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.Archived);
        }
        catch (Exception exception)
        {
            if (ct.IsCancellationRequested)
            {
                // Compensation is the documented cancellation allowlist: recovery reads must
                // finish even though the originating operation token is already cancelled.
                string journalPath = AppendJournalCodec.GetJournalPath(
                    Path.Combine(GetStoreDirUnderLease(scope), "versions"));
                AppendTransaction? rollbackTransaction = TryReadAppendTransactionForRollback(
                    journalPath, scope);
                bool archivesReconciled = rollbackTransaction is not null &&
                    _rollbackExecutor.TryRemoveCreatedArchiveMembers(rollbackTransaction, CancellationToken.None);
                bool liveMembersRestored = rollbackTransaction is not null &&
                    _rollbackExecutor.TryRestoreAppendTransaction(rollbackTransaction);
                if (!archivesReconciled || !liveMembersRestored)
                {
                    string detail = !archivesReconciled
                        ? "Cancellation rollback could not prove operation-owned archive members were removed."
                        : "Cancellation rollback could not prove both live members were restored.";
                    try
                    {
                        _appendJournalWriter.PersistAppendJournal(rollbackTransaction ?? transaction,
                            AppendJournalState.RecoveryRequired, detail: detail);
                    }
                    catch
                    {
                        // Keep the existing journal when recovery-state persistence fails.
                    }
                    throw AppendRecoveryBuilder.RecoveryRequired(
                        journalPath,
                        detail,
                        exception, operationId);
                }

                try
                {
                    _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.RolledBack,
                        detail: "Append cancellation was rolled back byte-for-byte.");
                    _appendJournalWriter.DeleteAppendJournal(transaction);
                }
                catch (Exception recoveryException)
                {
                    throw AppendRecoveryBuilder.RecoveryRequired(
                        AppendJournalCodec.GetJournalPath(Path.Combine(GetStoreDirUnderLease(scope), "versions")),
                        "Cancellation rollback completed but the journal could not be removed.",
                        recoveryException, operationId);
                }

                throw new AppendOperationCanceledException(operationId, exception);
            }

            try
            {
                _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.RecoveryRequired,
                    detail: "Archive publication did not complete; retained evidence requires recovery.");
            }
            catch (Exception journalFailure)
            {
                throw AppendRecoveryBuilder.RecoveryRequired(
                    AppendJournalCodec.GetJournalPath(Path.Combine(GetStoreDirUnderLease(scope), "versions")),
                    "Archive publication failed and its recovery state could not be persisted.",
                    new AggregateException(exception, journalFailure), operationId);
            }
            throw AppendRecoveryBuilder.RecoveryRequired(
                AppendJournalCodec.GetJournalPath(Path.Combine(GetStoreDirUnderLease(scope), "versions")),
                "Archive publication did not complete safely.", exception, operationId);
        }

        try
        {
            await PublishLiveAppendAsync(lease ?? throw new InvalidOperationException("Missing workspace lease."), transaction, entry, newArtifact, ct);
        }
        catch (Exception exception)
        {
            // CancellationToken.None: a cancelled append must still be rolled back.
            if (!_rollbackExecutor.TryRestoreAppendTransaction(transaction))
            {
                try
                {
                    _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.RecoveryRequired,
                        detail: "Pre-commit rollback could not prove both live members were restored.");
                }
                catch
                {
                    // Preserve the original recovery boundary if journal persistence itself fails.
                }
                throw AppendRecoveryBuilder.RecoveryRequired(
                    AppendJournalCodec.GetJournalPath(Path.Combine(GetStoreDirUnderLease(scope), "versions")),
                    "Pre-commit rollback could not prove both live members were restored.",
                    exception, operationId);
            }

                try
                {
                    _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.RolledBack,
                        detail: "Pre-commit append failure was rolled back byte-for-byte.");
                    _appendJournalWriter.DeleteAppendJournal(transaction);
                }
                catch (Exception recoveryException)
                {
                    throw AppendRecoveryBuilder.RecoveryRequired(
                    AppendJournalCodec.GetJournalPath(Path.Combine(GetStoreDirUnderLease(scope), "versions")),
                    "Rollback completed but the journal could not be removed.", recoveryException, operationId);
            }

            if (ct.IsCancellationRequested)
            {
                throw new AppendOperationCanceledException(operationId, exception);
            }

            if (exception is OperationCanceledException ||
                exception.InnerException is OperationCanceledException)
            {
                throw new AppendStoreException(
                    AppendStoreErrorCode.Internal,
                    operationId,
                    "Live append publication failed with a non-caller cancellation.",
                    exception);
            }

            throw;
        }

        ArchiveCleanupResult cleanup = _archiveService.PruneCommittedArchives(transaction, ct);
        bool pruningFailed = cleanup.Failed;
        bool journalCleanupFailed = false;
        if (!pruningFailed)
        {
            try
            {
                _appendJournalWriter.DeleteAppendJournal(transaction);
            }
            catch (Exception exception)
            {
                cleanup = cleanup with { Failed = true, Failures = cleanup.Failures.Append(
                    $"journal cleanup: {exception.Message}").ToArray() };
                journalCleanupFailed = true;
            }
        }

        if (pruningFailed || journalCleanupFailed)
        {
            string journalPath = AppendJournalCodec.GetJournalPath(
                Path.Combine(GetStoreDirUnderLease(scope), "versions"));
            string committedHash = Convert.ToHexStringLower(SHA256.HashData(
                transaction.NewArtifact.Bytes.Concat(transaction.NewSidecar.Bytes).ToArray()));
            try
            {
                _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.CleanupPending,
                    committedHash, cleanup.Detail);
            }
            catch (Exception exception)
            {
                return new AppendStoreResult(
                    AppendStoreOutcome.RecoveryRequired,
                    entry,
                    operationId,
                    Recovery: new AppendRecoveryInfo(
                        journalPath,
                        AppendJournalState.Committed.ToString(),
                        "retain the committed append and resolve cleanup evidence",
                        $"Post-commit cleanup evidence could not be updated: {exception.Message}"));
            }
        }

        return pruningFailed || journalCleanupFailed
            ? new AppendStoreResult(
                AppendStoreOutcome.SuccessWithWarning,
                entry,
                operationId,
                new AppendStoreWarning(
                    AppendWarningCode.PostCommitCleanupFailed,
                     cleanup.Detail))
            : new AppendStoreResult(AppendStoreOutcome.Appended, entry, operationId);
    }

    private async Task<RawMemberSnapshot> CaptureRawMemberAsync(
        string path, AppendFaultPoint faultPoint, string operationId, CancellationToken ct)
    {
        Exception? injected = InjectAppendFault(faultPoint, operationId, path);
        if (injected is not null)
            throw injected;

        RecoveryVerificationResult verification = await RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding("workspace", _workspaceRoot), RecoveryEvidenceKind.AppendJournal,
            path, default, MaintenanceLimits.Hard, ct).ConfigureAwait(false);
        if (verification.Outcome == RecoveryVerificationOutcome.ProtectedMissing)
            return RawMemberSnapshot.Capture(path, false, []);
        if (verification.Outcome != RecoveryVerificationOutcome.Verified || verification.SourceContent is null)
            throw new IOException($"Append member could not be captured safely: {path}");
        using (verification.SourceContent)
            return RawMemberSnapshot.Capture(path, true, verification.SourceContent.Bytes.ToArray());
    }

    internal ArchivePlan BuildArchivePlanForTest(string subject, string scope, bool required, DateTimeOffset startedAt) =>
        BuildArchivePlan(subject, scope, required, sidecarRequired: true, startedAt);

    internal string[] PruneCommittedArchivesForTest(AppendTransaction transaction) =>
        _archiveService.PruneCommittedArchives(transaction, CancellationToken.None).Failures;

    private ArchivePlan BuildArchivePlan(
        string subject, string scope, bool required, bool sidecarRequired, DateTimeOffset startedAt)
    {
        if (!required)
            return new ArchivePlan(false, null, null, ArchiveMemberStatus.NotRequired,
                ArchiveMemberStatus.NotRequired, []);

        string versionsDir = Path.Combine(GetStoreDirUnderLease(scope), "versions");
        string safe = SanitizeName(subject);
        string baseName = $"{safe}_{startedAt:yyyyMMdd-HHmmss-fff}";
        string artifactPath = Path.Combine(versionsDir, baseName + ".nmp2");
        for (int suffix = 2; File.Exists(artifactPath); suffix++)
            artifactPath = Path.Combine(versionsDir, $"{baseName}-{suffix}.nmp2");

        string sidecarPath = Path.ChangeExtension(artifactPath, null) + ".meta.json";
        var archiveBodies = Directory.Exists(versionsDir)
            ? Directory.GetFiles(versionsDir, $"{safe}_*.nmp2").Append(artifactPath)
            : [artifactPath];
        var candidates = archiveBodies
            .Where(path => !string.Equals(path, artifactPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Skip(10)
            .SelectMany(path => new[] { path, Path.ChangeExtension(path, null) + ".meta.json" })
            .ToArray();

        return new ArchivePlan(true, artifactPath, sidecarPath, ArchiveMemberStatus.Planned,
            sidecarRequired ? ArchiveMemberStatus.Planned : ArchiveMemberStatus.NotRequired, candidates);
    }

    private async Task<AppendTransaction> PublishFrozenArchiveAsync(AppendTransaction transaction, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArchivePlan plan = transaction.Archive;
        if (!plan.Required)
            return transaction;

        string versionsDirectory = Path.GetDirectoryName(plan.ArtifactArchivePath!)!;
        Directory.CreateDirectory(versionsDirectory);
        ArchiveMemberStatus artifactStatus = _archiveReplayer.WriteArchiveMember("artifact", transaction.OldArtifact, plan.ArtifactArchivePath!, transaction.OperationId,
            AppendFaultPoint.ArchiveArtifactWrite, AppendFaultPoint.ArchiveArtifactFlush, ct);
            transaction = transaction with { Archive = plan.WithStatuses(artifactStatus, plan.SidecarStatus) };
        _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.ArchiveInProgress);

        ArchiveMemberStatus sidecarStatus = ArchiveMemberStatus.NotRequired;
        if (transaction.OldSidecar.Existed && plan.SidecarArchivePath is not null)
            sidecarStatus = _archiveReplayer.WriteArchiveMember("sidecar", transaction.OldSidecar, plan.SidecarArchivePath, transaction.OperationId,
                AppendFaultPoint.ArchiveSidecarWrite, AppendFaultPoint.ArchiveSidecarFlush, ct);

        transaction = transaction with { Archive = plan.WithStatuses(artifactStatus, sidecarStatus) };
        _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.ArchiveInProgress);
        return transaction;
    }


    private async Task PublishLiveAppendAsync(
        WorkspaceLease lease,         AppendTransaction transaction, ArtifactEntry entry, string artifact, CancellationToken ct)
    {
        await WriteArtifactUnderLeaseAsync(lease, entry.Name, transaction.Scope, artifact, transaction.OperationId, ct);
        _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.NewPairInProgress);
        UpsertUnderLease(lease, entry, transaction.Scope, transaction.OperationId);

        await _memberVerifier.VerifyAsync(transaction.NewArtifact, transaction.OperationId, ct);
        await _memberVerifier.VerifyAsync(transaction.NewSidecar, transaction.OperationId, ct);
        string committedHash = Convert.ToHexStringLower(SHA256.HashData(
            transaction.NewArtifact.Bytes.Concat(transaction.NewSidecar.Bytes).ToArray()));
        _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.Committed, committedHash);
    }

    internal async Task RecoverAppendAsync(string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        using WorkspaceLease lease = EnterWorkspaceMutation(ct: cancellationToken);
        EnsureMaterialized(scope);
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope), TimeSpan.FromSeconds(5), cancellationToken);
        await _recoveryConductor.RecoverAppendUnsafeAsync(scope, GetStoreDirUnderLease(scope), "recovery", cancellationToken);
    }

    internal Task<bool> RecoverAppendParticipantForCoordinatorAsync(string scope, CancellationToken cancellationToken) =>
        _recoveryConductor.RecoverAppendUnsafeAsync(scope, GetStoreDirUnderLease(scope), "coordinator-recovery", cancellationToken);

    internal void WriteAppendRecoveryRequiredJournalForTest(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        AppendTransaction transaction = LastPreparedAppendTransactionForTest
            ?? throw new InvalidOperationException("An append transaction must be prepared before the recovery seam is used.");
        transaction = transaction with { OperationId = operationId };
        LastPreparedAppendTransactionForTest = transaction;
        _appendJournalWriter.PersistAppendJournal(transaction, AppendJournalState.RecoveryRequired,
            detail: "Deterministic recovery evidence created for testing.");
    }

    private static AppendTransaction? TryReadAppendTransactionForRollback(string journalPath, string scope) =>
        AppendRollbackJournalReader.TryReadAppendTransactionForRollback(journalPath, scope);



    /// <summary>Assembles the index entry from the derived half plus the store's own bookkeeping.
    /// <see cref="AttributeMigration.Forward"/> is the same safety net both pre-collapse write paths
    /// ran: idempotent here (the pipeline already folds file hashes and routes <c>key:value</c>
    /// keywords), and load-bearing for a pre-attributes entry appended to for the first time.
    ///
    /// <para><b>This is the sole assembler, and it builds a BRAND-NEW entry every call</b> — no
    /// ordinary write is a round-trip. <paramref name="prior"/> is therefore the only thing
    /// carrying <see cref="ArtifactEntry.SchemaVersion"/> and <see cref="ArtifactEntry.Unknown"/>
    /// across a rewrite; without it, the first <c>remember</c> on an existing memory deletes both.
    /// Required, not optional — an optional parameter would let a future write path omit it and
    /// quietly resume deleting unknown fields.</para></summary>
    private ArtifactEntry BuildDerivedEntry(
        string subject, string scope, long originalBytes, int chunkCount,
        DateTimeOffset createdAt, DateTimeOffset? updatedAt, DerivedMetadata derived,
        ArtifactEntry? prior) =>
        AttributeMigration.Forward(new ArtifactEntry(
            Address: MemoryAddress.Parse(scope == "local"
                ? $"/{subject}"
                : FormatQualifiedName(scope, subject)),
            ArtifactKey: ArtifactIdentityMapper.BuildArtifactKey(subject, scope),
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            ContentPreview: derived.ContentPreview,
            Keywords: derived.Keywords.Length > 0 ? derived.Keywords : null,
            TermFrequencies: derived.TermFrequencies.Count > 0 ? derived.TermFrequencies : null,
            UpdatedAt: updatedAt,
            ChunkEntries: derived.ChunkEntries,
            FileHashes: derived.FileHashes,
            Attributes: derived.Attributes,
            AgentKeywords: derived.AgentKeywords,
            // Carried forward, not re-derived: this build has no member to re-derive the version
            // from, and re-deriving it would silently downgrade a v2 sidecar to v1 on the first
            // rewrite — the exact bug this task exists to close.
            SchemaVersion: prior?.SchemaVersion)
        {
            // Keep the physical leaf separate from the qualified logical address. Without this
            // projection, the Name getter derives "topic/subject" from Address and sidecar writes
            // append that path beneath an already topic-scoped folder.
            Name = subject,
            // COPIED, never aliased.
            //
            // An earlier version of this comment justified the copy by claiming WriteSidecarCore's
            // ordinal sort would mutate the cache in place. THAT IS FALSE and is corrected here
            // rather than deleted, because the false version is the more dangerous artefact: a
            // reader who checks it finds it wrong and removes the copy as cargo cult. The sort
            // does `entry with { Unknown = ...ToDictionary() }`, allocating a new dictionary on a
            // new record; it cannot mutate anything in place.
            //
            // The real reason: Unknown is `{ get; set; }` (System.Text.Json refuses an init-only
            // extension-data property on a record with a parameterized constructor), and Upsert
            // installs the rebuilt entry into _indexCache while LoadIndex hands cached entries out
            // BY REFERENCE. An alias would therefore put one mutable Dictionary in two places, one
            // of which is shared cache state, and the setter makes writing to it legal. No caller
            // does that today. The copy is what keeps it from becoming a bug when one does.
            Unknown = prior?.Unknown is { } u ? new Dictionary<string, JsonElement>(u) : null
        });

    /// <summary>Persists an ephemeral write and projects it back as an <see cref="ArtifactEntry"/>,
    /// the way <see cref="ListEphemeral"/> does — through the same
    /// <see cref="EphemeralEntry.ToArtifactEntry"/>, applied to the record just stored.
    /// <para>The stored entry and the returned one are two views of ONE memory, and a field present
    /// in one and null in the other means the answer depends on which read you came through. This
    /// used to build both objects side by side and keep them in step by inspection —
    /// <see cref="EphemeralEntry.FileHashes"/>, the derived drift baseline, was the last field to
    /// have to be added to each. Projecting the stored record makes them agree by
    /// construction.</para></summary>
    private ArtifactEntry PersistEphemeral(
        string name, string artifact, long originalBytes, int chunkCount,
        DerivedMetadata derived, DateTimeOffset createdAt, DateTimeOffset? updatedAt)
    {
        string key = MemoryNaming.StripEphemeralPrefix(name);

        var stored = new EphemeralEntry(
            Name: key,
            Artifact: artifact,
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            ContentPreview: derived.ContentPreview,
            Keywords: derived.Keywords.Length > 0 ? derived.Keywords : null,
            TermFrequencies: derived.TermFrequencies.Count > 0 ? derived.TermFrequencies : null,
            UpdatedAt: updatedAt,
            ChunkEntries: derived.ChunkEntries,
            FileHashes: derived.FileHashes,
            Attributes: derived.Attributes,
            AgentKeywords: derived.AgentKeywords);

        RememberEphemeral(key, stored);
        return ProjectEphemeralEntry(stored);
    }

    private static ArtifactEntry ProjectEphemeralEntry(EphemeralEntry entry)
    {
        string address = $"~{entry.Name}";
        return entry.ToArtifactEntry() with
        {
            Address = MemoryAddress.Parse(address),
            ArtifactKey = ArtifactIdentityMapper.BuildArtifactKey(entry.Name, "ephemeral")
        };
    }

    /// <summary>
    /// The pipeline's one contact with the filesystem (D-04): resolves a <c>file</c> attribute value
    /// against this store's workspace root and hashes it, or returns null when it cannot — outside
    /// the workspace, missing, or unreadable.
    /// <para>Supplying it here makes the baseline unconditional: derivation runs inside the
    /// workspace-backed store that owns the path.</para>
    /// </summary>
    private string? HashWorkspaceFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;
        try
        {
            string full = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath.Trim()));
            if (!PathContainment.IsWithin(_workspaceRoot, full) || !File.Exists(full))
                return null;
            return Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full)));
        }
        catch
        {
            // An unhashable reference is not a failed write — the memory stores without a baseline.
            return null;
        }
    }

    // ── Artifact file I/O ────────────────────────────────────────────────────

    internal async Task WriteArtifactAsync(string subject, string scope, string artifactText,
        CancellationToken ct = default)
    {
        using WorkspaceLease lease = EnterWorkspaceMutation(ct: ct);
        await WithExclusiveScopeAdmissionAsync(lease, scope, ImportLockTimeout, async () =>
        {
            await WriteArtifactUnderLeaseAsync(lease, subject, scope, artifactText, null, ct);
            return true;
        }, ct).ConfigureAwait(false);
    }

    private Task WriteArtifactUnderLeaseAsync(WorkspaceLease lease, string subject, string scope, string artifactText,
        string? operationId, CancellationToken ct)
    {
        lease.AssertValid(_workspaceRoot);
        return DurableWriter.WriteArtifact(subject, scope, artifactText, operationId, ArtifactPath,
            key => _artifactCache.Invalidate(key), (_, _, action) => { lease.AssertValid(_workspaceRoot); action(); }, ct);
    }

    private async Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default)
    {
        return await WithRecoveryReadBarrierAsync(scope, async () =>
        {
            string path = ArtifactPath(subject, scope);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Artifact not found: {subject} in scope {scope}", path);

            // Check LRU cache (keyed by scope|subject|lastWriteTicks for staleness safety)
            long ticks = new FileInfo(path).LastWriteTimeUtc.Ticks;
            string cacheKey = $"{scope}|{subject}|{ticks}";
            if (_artifactCache.TryGet(cacheKey, out string cached))
                return cached;

            string text = await File.ReadAllTextAsync(path, ct);
            _artifactCache.Set(cacheKey, text);
            return text;
        }, ct);
    }

    public bool DeleteArtifact(string subject, string scope)
    {
        return WithExclusiveLock(scope, () => DeleteArtifactUnsafe(subject, scope));
    }

    private bool DeleteArtifactUnsafe(string subject, string scope) =>
        DurableWriter.DeleteArtifact(subject, scope, ArtifactPath, key => _artifactCache.Invalidate(key));

    private string MapArtifactKey(string artifactKey)
    {
        ArtifactIdentityMapper.ValidateArtifactKey(artifactKey);

        const string prefix = "artifacts/";
        string relativePath = artifactKey[prefix.Length..];
        string candidate = Path.GetFullPath(Path.Combine(MemoriesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathContainment.IsWithin(MemoriesRoot, candidate))
            throw new ArgumentException("ArtifactKey resolves outside the store root.", nameof(artifactKey));
        return candidate;
    }

    // ── Typed artifact reads ────────────────────────────────────────────────

    public async Task<string> ReadArtifactAsync(MemoryAddress address, CancellationToken ct = default)
    {
        if (!address.IsValid)
            throw new ArgumentException("The memory address must be valid.", nameof(address));

        if (address.Kind == MemoryAddressKind.Ephemeral)
        {
            var entry = GetEphemeral(address.Value);
            if (entry is null)
                throw new FileNotFoundException($"Ephemeral memory '~{address.Value}' not found.");
            return entry.Artifact;
        }

        var (scope, subject) = ResolveAddress(address);
        return await WithRecoveryReadBarrierAsync(scope, async () =>
        {
            string path = ArtifactPath(subject, scope);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Memory '{address}' not found.", path);
            return await File.ReadAllTextAsync(path, ct);
        }, ct);
    }

    // ── Listing & Search ─────────────────────────────────────────────────────

    private static bool IsEphemeralOnly(IReadOnlyList<string>? scopes) =>
        scopes is { Count: > 0 } && scopes.All(MemoryArtifactSource.IsEphemeral);

    private ListingService CreateEphemeralListing() => new(
        _ => [], GetStoreDirUnderLease, SanitizeName, ListEphemeral,
        TopicDiscoveryService.IncludesEphemeral, _ => [], (_, _) => []);

    public List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null) => ListScoped(scopes, null);

    public bool AnyScoped(IReadOnlyList<string>? scopes, Func<ScopedArtifact, bool> predicate) =>
        IsEphemeralOnly(scopes) ? CreateEphemeralListing().AnyScoped(scopes, predicate) :
        WithWorkspaceSnapshot(lease => CreateListingUnderLease(lease).AnyScoped(scopes, predicate));

    // ── Entity Facts ─────────────────────────────────────────────────────────

    private void InvalidateEntityFacts()
    {
        bool hadDirtyWork = _factMaterializer.IsDirty;
        _factMaterializer.Invalidate();
        if (hadDirtyWork)
            BackgroundWork.Fire(_ =>
            {
                GetEntityFactStore();
                return Task.CompletedTask;
            }, "entity fact rebuild");
    }

    /// <summary>
    /// The merged cross-scope <see cref="EntityFactStore"/>. Built lazily and cached; invalidated on
    /// any Upsert/Remove. Each scope's facts persist in a <c>facts.json</c> sidecar, reused when its
    /// validity token matches the current index and rebuilt otherwise. Invalidated or missing sidecars
    /// are coalesced and flushed by registered background maintenance work rather than synchronously
    /// on the read path; a later <see cref="BackgroundWork.DrainAsync"/> observes the deferred writes.
    /// </summary>
    public EntityFactStore GetEntityFactStore()
        => WithWorkspaceSnapshot(lease =>
    {
        var items = new List<ScopedArtifact>();
        if (TopicDiscoveryService.IncludesEphemeral(null))
            items.AddRange(ListEphemeral());

        var persistedFacts = new Dictionary<string, PersistedFactResult>(StringComparer.Ordinal);
        foreach (string scope in lease.CapturedAbsentWorkspace ? [] : _topicDiscoveryService.ResolveReadScopes(null))
        {
            WithRecoveryReadBarrier(lease, scope, () =>
            {
                var scopedItems = LoadIndexUnderRecoveryBarrier(scope)
                    .Select(entry => new ScopedArtifact(scope, entry))
                    .ToArray();
                items.AddRange(scopedItems);

                string? sidecarPath = ResolveFactSidecarPath(scope);
                if (sidecarPath is null) return true;

                string token = EntityFactMaterializer.ComputeToken(scopedItems);
                if (File.Exists(sidecarPath))
                {
                    var loaded = EntityFactSidecar.TryLoad(SafeReadAllText(sidecarPath));
                    if (loaded is { } valid && valid.Token == token)
                        persistedFacts[scope] = new PersistedFactResult(valid.Token, valid.Memories);
                }
                return true;
            });
        }
        return _factMaterializer.Materialize(items, persistedFacts);
    });

    /// <summary>
    /// Deterministic, order-independent summary of everything <see cref="EntityFactDerivation.Derive"/>
    /// reads from an entry — <c>Attributes</c> and <c>Keywords</c> (the same union
    /// <c>EntityGraph.ParseEntityKeywords ∪ ParseEntityAttributes</c> consumes) — for use as the
    /// content component of a <see cref="EntityFactSidecar.ComputeToken"/> entry. Attribute keys and,
    /// within each key, its values are sorted so two attribute dictionaries that differ only in
    /// insertion/enumeration order produce the same signature. Keywords are sorted the same way.
    /// Over-invalidating on a non-entity field (e.g. capitalization noise elsewhere) is acceptable —
    /// it just costs a safe rebuild; under-invalidating (missing a real entity change) is the bug
    /// this signature exists to close.
    /// </summary>
    private static string SafeReadAllText(string path)
    {
        try
        {
            string content = File.ReadAllText(path);
            ReadObserver?.Invoke(path, System.Text.Encoding.UTF8.GetByteCount(content));
            return content;
        }
        catch { return string.Empty; }
    }

    private static (bool Written, byte[] Bytes) TryWriteAllText(string path, string content) =>
        DurableWriter.TryWriteAllText(path, content, WriteObserver);

    private string? ResolveFactSidecarPath(string scope)
    {
        try { return Path.Combine(GetStoreDirUnderLease(scope), "facts.json"); }
        catch (ArgumentException) { return null; }
    }

    // ── Filtering overloads (excludeTopics) ──────────────────────────────────

    public List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics) =>
        IsEphemeralOnly(scopes) ? CreateEphemeralListing().ListScoped(scopes, excludeTopics) :
        WithWorkspaceSnapshot(lease => CreateListingUnderLease(lease).ListScoped(scopes, excludeTopics));

    public IReadOnlyList<ScopedArtifact> SelectLexicalCandidates(
        IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics, string query, int limit) =>
        SearchCandidateOptions.RankLexically(ListScoped(scopes, excludeTopics), query).Take(limit).ToArray();

    /// <summary>Returns the query-eligible lexical top-K snapshot under workspace read admission.</summary>
    public IReadOnlyList<ScopedArtifact> SelectLexicalCandidates(SearchQuery query, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return IsEphemeralOnly(query.Scopes)
            ? CreateEphemeralListing().SelectLexicalCandidates(query, limit, FormatQualifiedName)
            : WithWorkspaceSnapshot(lease => CreateListingUnderLease(lease)
                .SelectLexicalCandidates(query, limit, FormatQualifiedName));
    }

    /// <summary>
    /// Search orchestration is owned by <see cref="SearchService"/>; this is a one-line forwarder.
    /// Filters candidates and topics BEFORE the scorer's top-K cut, so a selective filter fills to
    /// <c>Limit</c> instead of returning the post-limit remnant. Every filter dimension is a
    /// property of the parent candidate (path/attributes) or the topic (scope label); chunk results
    /// inherit their parent's, so filtering parents covers them.
    /// </summary>
    public IReadOnlyList<SearchResult> SearchAll(SearchQuery query)
    {
        var candidates = IsEphemeralOnly(query.Scopes)
            ? query.Candidates is { } ephemeralCandidates
                ? CreateEphemeralListing().FilterToQueryScopes(ephemeralCandidates, query.Scopes, query.ExcludeTopics)
                : CreateEphemeralListing().ListScoped(query.Scopes, query.ExcludeTopics)
            : WithWorkspaceSnapshot(lease =>
        {
            var listing = CreateListingUnderLease(lease);
            return query.Candidates is { } supplied
                ? listing.FilterToQueryScopes(supplied, query.Scopes, query.ExcludeTopics)
                : listing.ListScoped(query.Scopes, query.ExcludeTopics);
        });
        return SearchService.ScoreSnapshot(query, candidates, FormatQualifiedName, RankerOptions);
    }

    /// <summary>
    /// Efficient override of the default interface method: resolves scopes then excludes
    /// the specified topic scopes.
    /// </summary>
    public IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes, IReadOnlyList<string>? excludeTopics) =>
        IsEphemeralOnly(scopes) ? [] :
        WithWorkspaceSnapshot(lease => lease.CapturedAbsentWorkspace ? [] : _topicDiscoveryService.ResolveReadScopes(scopes, excludeTopics));

    // ── Archive ──────────────────────────────────────────────────────────────

    internal void ArchiveVersion(string subject, string scope = "local")
    {
        WithExclusiveLock(scope, () => _archiveService.ArchiveVersionUnsafe(subject, scope));
    }

    private void ArchiveVersionUnderLease(WorkspaceLease lease, string subject, string scope)
    {
        lease.AssertValid(_workspaceRoot);
        _archiveService.ArchiveVersionUnsafe(subject, scope);
    }

    // ── Topic discovery ──────────────────────────────────────────────────────

    public string[] DiscoverTopics() =>
        WithWorkspaceSnapshot(lease => lease.CapturedAbsentWorkspace ? [] : _topicDiscoveryService.DiscoverTopics());

    public List<TopicInfo> GatherTopicInfos(IReadOnlyList<string>? scopes = null) =>
        IsEphemeralOnly(scopes) ? CreateEphemeralListing().GatherTopicInfos(scopes) :
        WithWorkspaceSnapshot(lease => CreateListingUnderLease(lease).GatherTopicInfos(scopes));

    // ── Export/Import helpers ─────────────────────────────────────────────────

    public Task<BundleRestoreResult> RestoreBundleAsync(BundleRestorePlan plan, CancellationToken ct = default) =>
        new BundleRestoreCoordinator(this, _workspaceAdmission, BundleReadLimits.Default).ExecuteAsync(plan, ct);

    internal static string SerializeBundleSidecar(ArtifactEntry entry) => SerializeSidecar(entry.NormalizeAttributesKey());

    internal void RecoverBundleScopeUnderLease(WorkspaceLease lease, string scope, CancellationToken ct)
    {
        lease.AssertValid(_workspaceRoot, requireExclusive: true);
        string directory = PathRouter.ToStoreDir(scope, _workspaceRoot);
        if (!Directory.Exists(directory)) return;
        using var scopeLock = FileLock.AcquireExclusive(Path.Combine(directory, ".lock"), ImportLockTimeout, ct);
        if (RecoverScopeUnsafe(lease, scope, directory, cancellationToken: ct))
        {
            string coordinator = MultiMemoryTransactionRecordCodec.GetPath(directory);
            string evidence = File.Exists(coordinator) ? coordinator : GetRestoreJournalPath(directory);
            throw new RestoreRecoveryException(scope, evidence, RestoreRecoveryFailure.ReplayIoFailed,
                "Terminal scope recovery evidence must be removed before bundle publication.");
        }
        _recoveryConductor.RecoverAppendUnsafeAsync(scope, directory, "bundle-recovery", ct).GetAwaiter().GetResult();
    }

    internal void RecoverBundleEntityUnderLease(WorkspaceLease lease, string kind, string name, string directory, CancellationToken ct)
    {
        lease.AssertValid(_workspaceRoot, requireExclusive: true);
        if (!Directory.Exists(directory)) return;
        using var entityLock = FileLock.AcquireExclusive(Path.Combine(directory, ".lock"), ImportLockTimeout, ct);
        var result = _fileEntityTransactions.RecoverPendingEvidence(directory, kind, name, ct);
        if (result is not null)
            throw new BundleImportException(BundleImportFailure.RecoveryRequired,
                new IOException("File entity has unresolved recovery evidence."));
    }

    internal WorkspaceLease EnterWorkspaceMutation(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        WorkspaceLease lease = _workspaceAdmission.EnterMutation(timeout ?? ImportLockTimeout, ct);
        try
        {
            if (lease.RecoveryPerformed) InvalidateCaches();
            EnsureLayoutUnderLease(lease);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope) =>
        WithWorkspaceSnapshot(lease => CreateListingUnderLease(lease).ListTopicArtifacts(topicScope));

    /// <summary>
    /// Cross-process lock budget for a restore. A restore holds its scope's lock for the whole
    /// import, which is unbounded in the bundle's size, so the 5s default that suits a single
    /// write would fail a large one against itself.
    ///
    /// <para>Fixed rather than scaled to entry count: this is a ceiling that exists so a
    /// legitimate long restore does not fail spuriously, not a correctness parameter. A formula
    /// would be one more thing to get wrong. The accepted cost is that a genuine deadlock on an
    /// import surfaces in five minutes rather than five seconds.</para>
    /// </summary>
    private static readonly TimeSpan ImportLockTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Suffix for a bundle body staged but not yet committed.
    ///
    /// <para>Deliberately carries NO <c>.nmp2</c> segment: <c>DiscoverMemoriesRecursive</c> decides
    /// whether a directory is a v2 scope with <c>EnumerateFiles(dir, "*.nmp2")</c>, and a staging
    /// file that registered as an artifact would make a half-restored scope discoverable as a
    /// topic. Keeping <c>.nmp2</c> out of the name removes the question rather than answering
    /// it.</para>
    /// </summary>
    private const string ImportStagingSuffix = ".staging";
    private const string RestoreJournalFileName = ".restore-journal.json";

    /// <summary>
    /// The <see cref="IMemoryStore"/> contract stays <c>void</c>: the interface is public and must
    /// not surface the internal <see cref="ImportMemoryResult"/> at its surface. This explicit
    /// implementation preserves that void contract (and its lock-timeout translation) for interface
    /// callers; the result-returning entry point is the internal method below, which the bundle write
    /// pass and the import-result tests drive directly.
    /// </summary>
    void IMemoryStore.ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, byte[]> artifactBytes, bool overwrite)
    {
        _ = ImportTopicEntries(topicScope, entries, artifactBytes, overwrite);
    }

    internal ImportMemoryResult ImportTopicEntries(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, byte[]> artifactBytes, bool overwrite)
    {
        try
        {
            return ImportTopicEntriesCore(topicScope, entries, artifactBytes, overwrite);
        }
        catch (FileLockTimeoutException ex)
        {
            // WithExclusiveLock's own exception names only the lock file path
            // (".../{scope-dir}/.lock") — useful for a human staring at the filesystem, useless for
            // a caller trying to report which operation failed. Name both here instead.
            throw new TimeoutException(
                $"Import into topic scope '{topicScope}' timed out waiting for its exclusive lock.",
                ex);
        }
    }

    private ImportMemoryResult ImportTopicEntriesCore(string topicScope, List<ArtifactEntry> entries,
        Dictionary<string, byte[]> artifactBytes, bool overwrite)
    {
        // Import bypasses MemoryWrite, so reject every unsafe durable leaf before materializing
        // the scope lock or creating a staging artifact. Once a restore begins staging, failure
        // must not leave residue for a later recovery sweep to interpret.
        foreach (var entry in entries)
            MemoryWrite.ValidatePersistentSubject(entry.Name);

        // Defaulted so the lock-free path (no candidates) reports Created with an empty operation id;
        // the exclusive-lock action always assigns a real result before it returns or throws.
        ImportMemoryResult result = new ImportMemoryResult(
            WholeMemoryOutcome.Created, null, string.Empty, "No candidates to import.", null);

        WithExclusiveLock(topicScope, () =>
        {
            string storeDir = GetStoreDirUnderLease(topicScope);
            Directory.CreateDirectory(storeDir);
            SweepStaleStagingUnsafe(storeDir);

            // Drop the cached index so the snapshot below is read from disk. LoadIndexUnsafe is
            // cache-first, and PublishIndexUnsafe writes a sidecar for EVERY entry it is handed —
            // so a warm entry that another process has since rewritten would be reverted to the
            // cached version over the newer body. Holding the exclusive lock is not enough: the
            // lock says nothing about how old the CACHE is. One cold read per bulk import.
            _scopeIndexCache.Remove(topicScope);

            // Read under the lock that will commit it. The snapshot and the commit used to sit on
            // opposite sides of an unlocked write loop, which is what let a concurrent write land
            // in between and then be swept as an orphan (finding C1).
            //
            // ToList() because this method mutates the list below and hands it to
            // PublishIndexUnsafe, which takes ownership of the instance it is given (it becomes
            // the cache). LoadIndexUnsafe returns the cache's own backing list, so mutating it
            // here would corrupt every other reader's live view.
            var existingEntries = LoadIndexUnsafe(topicScope).ToList();

            // The scope itself may already contain a forward-versioned sidecar. Refuse before
            // building the restore plan; waiting until after the incoming candidates are merged
            // makes this guard depend on the candidate set and can allow a scope-only newer entry
            // to be overwritten without the promised read-only protection.
            if (existingEntries.Find(ArtifactSidecarPolicy.IsReadOnly) is { } existingBlocking)
                throw new ScopeReadOnlyException(topicScope, existingBlocking.Name,
                    existingBlocking.SchemaVersion ?? 1, DurableFormats.ArtifactSidecar.Current);

            var candidates = new List<ArtifactEntry>();
            foreach (ArtifactEntry entry in entries)
            {
                bool exists = existingEntries.Any(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                if (exists && !overwrite || !artifactBytes.ContainsKey(entry.Name))
                    continue;

                    // ArtifactKey is recomputed deliberately: it identifies THIS store's layout, so the exporting
                    // machine's value is wrong here. CreatedAt is NOT — a restore reinstates the
                    // memory as it was, and overwriting it reset every memory's creation date on
                    // every restore (design review finding M4). A bundle carrying no "createdAt" key
                    // deserializes to default(DateTimeOffset) — 0001-01-01T00:00:00+00:00 — and that
                    // specific value is the only one rewritten to "now" here. Any OTHER unparseable
                    // createdAt is refused earlier, in ImportTopicsFromZip's deserialize.
                string canonicalAddress = topicScope == "local"
                    ? $"/{entry.Name}"
                    : FormatQualifiedName(topicScope, entry.Name);
                candidates.Add(entry with
                {
                    Address = MemoryAddress.Parse(canonicalAddress),
                    ArtifactKey = ArtifactIdentityMapper.BuildArtifactKey(entry.Name, topicScope),
                    Name = SanitizeName(entry.Name),
                    CreatedAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt,
                });
            }

            List<RestoreJournalPayload> payloads = BuildRestorePayloadsUnsafe(topicScope, candidates, artifactBytes, overwrite);
            if (payloads.Count == 0)
            {
                result = new ImportMemoryResult(WholeMemoryOutcome.Created, null, string.Empty,
                    "No candidates to import.", null);
                return;
            }

            // Pre-import existence is judged against this snapshot, taken BEFORE the mutation loop
            // merges the incoming candidates in — comparing against the mutated list would report
            // every import as a replace rather than a create.
            HashSet<string> preImportNames = new(
                existingEntries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

            foreach (RestoreJournalPayload payload in payloads)
            {
                ArtifactEntry updatedEntry = payload.Entry;
                int idx = existingEntries.FindIndex(e => e.Name.Equals(updatedEntry.Name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    existingEntries[idx] = updatedEntry;
                else
                    existingEntries.Add(updatedEntry);
            }

            var blocking = existingEntries.FirstOrDefault(ArtifactSidecarPolicy.IsReadOnly);
            if (blocking is not null)
                throw new ScopeReadOnlyException(topicScope, blocking.Name, blocking.SchemaVersion ?? 1,
                    DurableFormats.ArtifactSidecar.Current);

            RestoreJournal journal = RestoreJournalCodec.CreatePrepared(topicScope, payloads);
            byte[] journalBytes = RecoveryEnvelopeCodec.Serialize(new RestoreRecoveryAdapter().ToEnvelope(journal));
            ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.BeforePreparedWrite,
                GetRestoreJournalPath(storeDir));
            DurableFile.AtomicWriteAllBytes(GetRestoreJournalPath(storeDir), journalBytes);
            ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.AfterPreparedWrite,
                GetRestoreJournalPath(storeDir));
            var restoreAdapter = new RestoreRecoveryAdapter();
            ApplyPreparedJournalUnsafe(journal, storeDir,
                new RestoreEnvelopeAdapterResult(restoreAdapter, restoreAdapter.ToEnvelope(journal)));
            InvalidateRecoveredScopeUnsafe(topicScope);
            bool cleanupDeferred = !TryDeleteCompletedJournalUnsafe(GetRestoreJournalPath(storeDir));
            bool anyCreated = candidates.Any(c => !preImportNames.Contains(c.Name));
            ArtifactEntry? importedEntry = candidates.Count > 0 ? candidates[0] : null;
            WholeMemoryOutcome outcome = anyCreated ? WholeMemoryOutcome.Created : WholeMemoryOutcome.Replaced;
            string? warning = cleanupDeferred
                ? "Import committed; completed-journal cleanup deferred."
                : null;
            RecoveryInfo? recovery = cleanupDeferred
                ? new RecoveryInfo(GetRestoreJournalPath(storeDir), "cleanupPending",
                    "resolve retained completed journal", "completed-journal cleanup deferred")
                : null;
            if (cleanupDeferred)
                outcome = WholeMemoryOutcome.SuccessWithWarning;
            result = new ImportMemoryResult(
                outcome,
                importedEntry,
                journal.TransactionId,
                warning,
                recovery);
        }, ImportLockTimeout);

        return result;
    }

    /// <summary>
    /// Recovers a scope while its existing exclusive file and write locks are held.
    ///
    /// <para>State | Destination action | Verification | Next journal action</para>
    /// <para>absent | none | none | continue</para>
    /// <para>prepared | replay every body then sidecar | hash every destination | atomically write completed, invalidate, delete</para>
    /// <para>completed | none | hash every destination | invalidate, delete</para>
    /// <para>invalid/newer | none | none | retain and throw recovery-blocked</para>
    /// </summary>
    private bool RecoverScopeUnsafe(WorkspaceLease lease, string scope, string storeDir, bool recoverCoordinator = true,
        CancellationToken cancellationToken = default)
    {
        lease.AssertValid(_workspaceRoot);
        cancellationToken.ThrowIfCancellationRequested();
        RecoveryDiscoveryResult discovery = _recoveryDiscovery.Discover(scope, storeDir,
            cancellationToken: cancellationToken);
        if (discovery.BlockingEvidence is not null)
            throw CreateDiscoveryRecoveryException(discovery.BlockingEvidence);

        if (recoverCoordinator && discovery.CoordinatorRecord is not null)
        {
            MultiMemoryAggregateResult coordinatorRecovery = _multiMemoryTransactions
                .RecoverUnderLockAsync(lease, scope, cancellationToken).GetAwaiter().GetResult();
            if (coordinatorRecovery.Outcome == MultiMemoryOutcome.RecoveryRequired)
                throw CreateCoordinatorRecoveryException(scope, storeDir, coordinatorRecovery);
            if (coordinatorRecovery.Outcome == MultiMemoryOutcome.CommittedCleanupPending)
                return true;
        }

        RecoveryOutcome admission;
        try
        {
            admission = _recoveryDiscovery.AdmitNext(scope, storeDir, discovery, cancellationToken);
        }
        catch (RecoveryEnvelopeException exception) when (discovery.Candidates.Count > 0 &&
            discovery.Candidates[0].Operation == RecoveryOperation.Restore)
        {
            throw CreateRestoreRecoveryException(scope, discovery.Candidates[0].EvidencePath, exception);
        }
        if (admission.IsBlocked && admission.Block is not null)
            throw CreateDiscoveryRecoveryException(admission.Block);
        if (discovery.Candidates.Count > 0 && discovery.Candidates[0].Operation == RecoveryOperation.Append)
            return false;
        if (discovery.Candidates.Count > 0 &&
            discovery.Candidates[0].Envelope.State is RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending)
            return admission.CleanupPending;
        if (discovery.Candidates.Count > 0 && discovery.Candidates[0].Operation != RecoveryOperation.Restore)
            throw CreateDiscoveryRecoveryException(new RecoveryBlock(scope, admission.EvidencePath,
                discovery.Candidates[0].Operation, admission.OperationId, RecoveryBlockReason.Inconsistent,
                "Recovery evidence is validated but its operation-specific replay boundary is not active."));

        string journalPath = GetRestoreJournalPath(storeDir);
        if (!File.Exists(journalPath))
            return false;

        RestoreJournal journal;
        RestoreEnvelopeAdapterResult parsed;
        try
        {
            var adapter = new RestoreRecoveryAdapter();
            RecoveryVerifiedEnvelope verifiedEnvelope = ReadJournalBytesUnsafe(journalPath, scope, cancellationToken);
            RecoveryEnvelopeDocument envelope = ((IRecoveryAdapter)adapter).ConsumeParsed(in verifiedEnvelope);
            journal = adapter.FromEnvelope(envelope, journalPath);
            parsed = new RestoreEnvelopeAdapterResult(adapter, envelope);
        }
        catch (RestoreRecoveryException)
        {
            throw;
        }
        catch (BundleImportException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ReplayIoFailed(scope, journalPath, exception);
        }

        try
        {
            if (journal.State == RestoreJournalState.Prepared)
                ApplyPreparedJournalUnsafe(journal, storeDir, parsed, cancellationToken);
            else
                VerifyJournalDestinationsUnsafe(journal, storeDir, cancellationToken);
        }
        catch (RestoreRecoveryException)
        {
            throw;
        }
        catch (BundleImportException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ReplayIoFailed(scope, journalPath, exception);
        }

        InvalidateRecoveredScopeUnsafe(scope);
        return !TryDeleteCompletedJournalUnsafe(journalPath);
    }

    private void ApplyPreparedJournalUnsafe(RestoreJournal journal, string storeDir,
        RestoreEnvelopeAdapterResult parsed, CancellationToken cancellationToken = default)
    {
        string journalPath = GetRestoreJournalPath(storeDir);
        var payloads = journal.Entries.Select(entry => (Entry: entry,
            Bytes: RestoreJournalCodec.DecodeAndVerifyPayloads(entry, journal.Scope, journalPath))).ToList();

        foreach ((RestoreJournalEntry entry, (byte[] body, byte[] sidecar) bytes) in payloads)
        {
            string bodyPath = Path.Combine(storeDir, entry.BodyFile);
            ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.BeforeReplayBody, bodyPath);
            DurableFile.AtomicWriteAllBytes(bodyPath, bytes.body);

            string sidecarPath = Path.Combine(storeDir, entry.SidecarFile);
            ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.BeforeReplaySidecar, sidecarPath);
            DurableFile.AtomicWriteAllBytes(sidecarPath, bytes.sidecar);
        }

        ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.AfterReplayDestinations, journalPath);
        VerifyJournalDestinationsUnsafe(journal, storeDir, cancellationToken);
        ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.BeforeCompletedWrite, journalPath);
        RestoreJournal completed = RestoreJournalCodec.WithCompleted(journal);
        byte[] completedBytes = parsed.Envelope.IsLegacy
            ? RestoreJournalCodec.Serialize(completed)
            : RecoveryEnvelopeCodec.Serialize(parsed.Adapter.ToEnvelope(completed));
        DurableFile.AtomicWriteAllBytes(journalPath, completedBytes);
        ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.AfterCompletedWrite, journalPath);
    }

    private static RecoveryVerifiedEnvelope ReadJournalBytesUnsafe(string journalPath, string scope,
        CancellationToken cancellationToken)
    {
        string storeDirectory = Path.GetDirectoryName(journalPath)!;
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding(scope, storeDirectory), RecoveryEvidenceKind.RestoreJournal,
            journalPath, default, MaintenanceLimits.Hard, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        if (verification.Outcome != RecoveryVerificationOutcome.Verified || verification.SourceContent is null)
            throw new RestoreRecoveryException(scope, journalPath,
                RestoreRecoveryFailure.MalformedOrIntegrityFailed,
                "Restore journal could not be verified.");
        using (verification.SourceContent)
        {
            RecoveryVerificationContent content = verification.SourceContent;
            RecoveryVerifiedFileMetadata metadata = content.Metadata;
            RecoveryScopeBinding scopeBinding = new(scope, storeDirectory);
            return RecoveryEnvelopeCodec.ParseVerifiedEnvelope(in content, in scopeBinding,
                journalPath, storeDirectory, RecoveryEvidenceKind.RestoreJournal, in metadata);
        }
    }

    /// <summary>Materializes a restore journal while enforcing its byte cap as each stream read
    /// arrives, rather than trusting a stale length snapshot before reading.</summary>
    internal static byte[] ReadBoundedJournalStream(Stream stream, long maximumBytes, string scope, string journalPath)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var materialized = new MemoryStream();
        byte[] buffer = new byte[81_920];

        while (true)
        {
            int read = stream.Read(buffer);
            if (read == 0)
                return materialized.ToArray();

            if (read > maximumBytes - materialized.Length)
                throw new RestoreRecoveryException(scope, journalPath,
                    RestoreRecoveryFailure.MalformedOrIntegrityFailed,
                    "Restore journal exceeds the maximum document size.");

            materialized.Write(buffer, 0, read);
        }
    }

    private static void VerifyJournalDestinationsUnsafe(RestoreJournal journal, string storeDir,
        CancellationToken cancellationToken)
    {
        string journalPath = GetRestoreJournalPath(storeDir);
        foreach (RestoreJournalEntry entry in journal.Entries)
        {
            VerifyJournalDestination(Path.Combine(storeDir, entry.BodyFile), entry.BodySha256, journal, journalPath, storeDir, cancellationToken);
            VerifyJournalDestination(Path.Combine(storeDir, entry.SidecarFile), entry.SidecarSha256, journal, journalPath, storeDir, cancellationToken);
        }
    }

    private static void VerifyJournalDestination(string path, string expectedHash, RestoreJournal journal,
        string journalPath, string storeDir, CancellationToken cancellationToken)
    {
        RecoveryVerificationResult verification = RecoveryEvidenceVerifier.VerifySingleAsync(
            new RecoveryScopeBinding(journal.Scope, storeDir), RecoveryEvidenceKind.RestoreJournal,
            path, new RecoveryExpectedHash(expectedHash), MaintenanceLimits.Hard, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        verification.SourceContent?.Dispose();
        if (verification.Outcome == RecoveryVerificationOutcome.Verified)
            return;

        throw new RestoreRecoveryException(journal.Scope, journalPath, RestoreRecoveryFailure.MalformedOrIntegrityFailed,
            "Restore journal destination failed integrity verification.");
    }

    private void InvalidateRecoveredScopeUnsafe(string scope)
    {
        _scopeIndexCache.Remove(scope);
        _artifactCache.Invalidate($"{scope}|");
        if (scope.StartsWith("local:", StringComparison.Ordinal))
            _topicDiscoveryService.InvalidateCachedTopics();
        InvalidateEntityFacts();
    }

    private static bool TryDeleteCompletedJournalUnsafe(string journalPath)
    {
        try
        {
            ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.BeforeCleanupDelete, journalPath);
            File.Delete(journalPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Completion was already verified and durably recorded. Keep that evidence; a later
            // access re-verifies the destinations and retries this cleanup.
            return false;
        }
    }

    internal static void ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition transition, string destination)
    {
        Exception? failure = RestoreJournalFailureInjector.Value?.Invoke(transition, destination);
        if (failure is not null)
            throw failure;
    }

    private static RestoreRecoveryException ReplayIoFailed(string scope, string journalPath, Exception exception) =>
        new(scope, journalPath, RestoreRecoveryFailure.ReplayIoFailed,
            $"Restore replay failed for topic scope '{scope}'.", exception);

    private void RefuseMutationWithRetainedEvidence(string scope, string directory, bool retained,
        CancellationToken cancellationToken = default)
    {
        if (!retained) return;
        RecoveryDiscoveryResult discovery = _recoveryDiscovery.Discover(scope, directory,
            cancellationToken: cancellationToken);
        if (discovery.BlockingEvidence is not null)
            throw CreateDiscoveryRecoveryException(discovery.BlockingEvidence);
        RecoveryCandidate? candidate = discovery.Candidates.Count == 0 ? null : discovery.Candidates[0];
        string evidencePath = discovery.CoordinatorRecord is not null
            ? MultiMemoryTransactionRecordCodec.GetPath(directory)
            : candidate?.EvidencePath ?? GetRestoreJournalPath(directory);
        string operationId = discovery.CoordinatorRecord?.TransactionId
            ?? candidate?.OperationId ?? "retained-terminal";
        throw CreateDiscoveryRecoveryException(new RecoveryBlock(scope, evidencePath,
            discovery.CoordinatorRecord is null ? candidate?.Operation : null, operationId,
            RecoveryBlockReason.Inconsistent,
            "Terminal recovery evidence must be retired before a new scope mutation."));
    }

    private static Exception CreateDiscoveryRecoveryException(RecoveryBlock block)
    {
        if (block.Operation == RecoveryOperation.Append)
            return new AppendStoreException(
                AppendStoreErrorCode.RecoveryRequired,
                block.OperationId,
                block.Detail,
                block);

        return new RestoreRecoveryException(block.Scope, block.EvidencePath,
            block.Reason == RecoveryBlockReason.UnsupportedVersion
                ? RestoreRecoveryFailure.UnsupportedVersion
                : block.Reason == RecoveryBlockReason.ReplayIoFailed
                    ? RestoreRecoveryFailure.ReplayIoFailed
                    : RestoreRecoveryFailure.MalformedOrIntegrityFailed,
            block.Detail, unsupportedVersion: block.UnsupportedVersion, discoveryBlock: block);
    }

    private static RestoreRecoveryException CreateCoordinatorRecoveryException(
        string scope, string storeDir, MultiMemoryAggregateResult result)
    {
        string evidencePath = MultiMemoryTransactionRecordCodec.GetPath(storeDir);
        RecoveryBlockReason reason = result.Failure switch
        {
            MultiMemoryFailure.UnsupportedVersion => RecoveryBlockReason.UnsupportedVersion,
            MultiMemoryFailure.Malformed => RecoveryBlockReason.Malformed,
            MultiMemoryFailure.Unsafe => RecoveryBlockReason.Unsafe,
            MultiMemoryFailure.Inconsistent => RecoveryBlockReason.Inconsistent,
            MultiMemoryFailure.ReplayIoFailed => RecoveryBlockReason.ReplayIoFailed,
            _ => RecoveryBlockReason.Ambiguous
        };
        var block = new RecoveryBlock(scope, evidencePath, null, result.TransactionId, reason,
            result.Warning ?? "Multi-memory coordinator recovery is required.");
        return new RestoreRecoveryException(scope, evidencePath,
            reason == RecoveryBlockReason.UnsupportedVersion
                ? RestoreRecoveryFailure.UnsupportedVersion
                : RestoreRecoveryFailure.MalformedOrIntegrityFailed,
            block.Detail, discoveryBlock: block);
    }

    private static RestoreRecoveryException CreateRestoreRecoveryException(
        string scope, string journalPath, RecoveryEnvelopeException exception) =>
        new(scope, journalPath, exception.Failure switch
        {
            RecoveryEnvelopeFailure.UnsupportedVersion => RestoreRecoveryFailure.UnsupportedVersion,
            _ => RestoreRecoveryFailure.MalformedOrIntegrityFailed
        }, exception.Message, exception,
            unsupportedVersion: exception.UnsupportedVersion);

    private List<RestoreJournalPayload> BuildRestorePayloadsUnsafe(string topicScope, List<ArtifactEntry> incoming,
        Dictionary<string, byte[]> bodies, bool overwrite)
    {
        if (incoming.Count > RestoreJournalCodec.MaxJournalEntryCount)
            throw new InvalidOperationException("Imported topic exceeds the restore journal entry limit.");

        var payloads = new List<RestoreJournalPayload>(incoming.Count);
        long aggregatePayloadBytes = 0;
        foreach (ArtifactEntry entry in incoming)
        {
            if (!bodies.TryGetValue(entry.Name, out byte[]? bodyBytes))
                continue;
            if (bodyBytes.LongLength > RestoreJournalCodec.MaxRawNmp2BodyBytes)
                throw new InvalidOperationException($"Imported artifact '{entry.Name}' exceeds the maximum raw NMP/2 body size.");

            aggregatePayloadBytes = RestoreJournalCodec.AddDecodedPayloadLength(
                aggregatePayloadBytes, bodyBytes.LongLength, topicScope, "<new restore journal>");

            ArtifactEntry normalizedEntry = entry.NormalizeAttributesKey();
            byte[] sidecarBytes = JsonSerializer.SerializeToUtf8Bytes(normalizedEntry,
                FileStoreJsonContext.Default.ArtifactEntry);
            if (sidecarBytes.LongLength > RestoreJournalCodec.MaxSidecarBytes)
                throw new InvalidOperationException($"Imported artifact '{entry.Name}' sidecar exceeds the maximum size.");

            aggregatePayloadBytes = RestoreJournalCodec.AddDecodedPayloadLength(
                aggregatePayloadBytes, sidecarBytes.LongLength, topicScope, "<new restore journal>");

            payloads.Add(new RestoreJournalPayload(normalizedEntry, bodyBytes, sidecarBytes));
        }

        return payloads;
    }

    private static string GetRestoreJournalPath(string storeDir) => Path.Combine(storeDir, RestoreJournalFileName);

    /// <summary>Best-effort removal of a staging file. A leftover is inert litter, not
    /// corruption — it is invisible to every enumerator in the store — so a failure to delete
    /// must not mask the exception that caused the rollback.</summary>
    private static void TryDeleteStagingFile(string path) => DurableWriter.TryDeleteStagingFile(path);

    /// <summary>
    /// Removes staging files left by an import that died before committing or rolling back.
    ///
    /// <para>Unconditional rather than age-based: the caller holds the scope's exclusive lock, so
    /// no other import can be staging into this directory, and every staging file present is
    /// therefore abandoned by definition.</para>
    ///
    /// <para>Caller must already hold the scope's exclusive lock.</para>
    /// </summary>
    private void SweepStaleStagingUnsafe(string storeDir)
    {
        if (!Directory.Exists(storeDir)) return;
        foreach (string stale in Directory.EnumerateFiles(storeDir, "*" + ImportStagingSuffix).ToList())
            TryDeleteStagingFile(stale);
    }

    // ── Display helpers ──────────────────────────────────────────────────────

    [Obsolete("Use MemoryNaming.FormatScopeLabel instead.")]
    public static string FormatScopeLabel(string scope) =>
        MemoryNaming.FormatScopeLabel(scope);

    public void Dispose()
    {
        MaintenanceDiscoveryPageService[] maintenancePages;
        maintenancePages = _maintenancePageServiceCache.TakeDisposalSnapshot();

        _scopeLockManager.Dispose();

        Exception? firstFailure = null;
        try { _factMaterializer.Dispose(); }
        catch (Exception exception) { firstFailure = exception; }

        foreach (MaintenanceDiscoveryPageService service in maintenancePages)
        {
            try { service.Dispose(); }
            catch (Exception exception) { firstFailure ??= exception; }
        }

        try { _maintenanceRuns.Dispose(); }
        catch (Exception exception) { firstFailure ??= exception; }

        // Admission gates are managed-only and may still be released by an async operation that
        // was already admitted when disposal began. Letting those gates become unreachable avoids
        // turning a valid in-flight completion into ObjectDisposedException.
        if (firstFailure is not null)
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }

    public bool Remove(MemoryAddress address)
    {
        var (scope, subject) = ResolveAddress(address);
        return MemoryArtifactSource.IsEphemeral(scope)
            ? ForgetEphemeral(address)
            : Remove(subject, scope);
    }

    public bool TryUpdateEntry(MemoryAddress address, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        var (scope, subject) = ResolveAddress(address);
        return TryUpdateEntry(subject, scope, mutate);
    }

    public void RememberEphemeral(MemoryAddress address, EphemeralEntry entry)
    {
        EnsureEphemeralAddress(address);
        RememberEphemeral(address.Value, entry);
    }

    public bool ForgetEphemeral(MemoryAddress address)
    {
        EnsureEphemeralAddress(address);
        return ForgetEphemeral(address.Value);
    }

    public EphemeralEntry? GetEphemeral(MemoryAddress address)
    {
        EnsureEphemeralAddress(address);
        return GetEphemeral(address.Value);
    }

    public Task WriteArtifactAsync(MemoryAddress address, string artifactText,
        CancellationToken ct = default)
    {
        var (scope, subject) = ResolveAddress(address);
        return WriteArtifactAsync(subject, scope, artifactText, ct);
    }

    public void ArchiveVersion(MemoryAddress address)
    {
        var (scope, subject) = ResolveAddress(address);
        ArchiveVersion(subject, scope);
    }

    private static void EnsureEphemeralAddress(MemoryAddress address)
    {
        if (address.Kind != MemoryAddressKind.Ephemeral)
            throw new ArgumentException("The address must be ephemeral.", nameof(address));
    }
}
