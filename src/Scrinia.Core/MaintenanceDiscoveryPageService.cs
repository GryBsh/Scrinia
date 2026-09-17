using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;

namespace Scrinia.Core;

internal sealed class MaintenanceDiscoveryPageService : IDisposable
{
    private readonly Func<CancellationToken, IEnumerable<MaintenanceCandidate>> _sourceFactory;
    private readonly MaintenanceRunRegistry _registry;
    private readonly Dictionary<MaintenanceRunId, Session> _sessions = [];
    private readonly object _gate = new();
    private bool _disposed;

    internal MaintenanceDiscoveryPageService(
        Func<CancellationToken, IEnumerable<MaintenanceCandidate>> sourceFactory,
        MaintenanceRunRegistry registry)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    internal MaintenanceDiscoveryPage ReadFirstPage(
        MaintenanceWorkspaceKey workspace, string scope, CancellationToken cancellationToken)
    {
        MaintenanceContractValidation.RequireSafeText(scope, nameof(scope));
        cancellationToken.ThrowIfCancellationRequested();
        MaintenanceRunId run = new(Guid.NewGuid());
        Session? session = null;
        MaintenanceRunBinding? binding = null;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
                var scopes = new MaintenanceScopeBinding(Guid.NewGuid());
                var policy = new MaintenanceHash256(new byte[32]);
                binding = _registry.Create(workspace, run, snapshot, scopes, policy,
                    DateTimeOffset.UtcNow.AddMinutes(5));
                session = new Session(run, workspace, snapshot, scopes, policy, scope,
                    _sourceFactory(cancellationToken).GetEnumerator());
                _sessions[run] = session;
                if (!_registry.TryAcquire(run, out MaintenanceRunLease? lease) || lease is null)
                    throw new InvalidOperationException("The maintenance run was revoked during startup.");
                using (lease)
                    return ReadPageAndCleanup(session, lease.Binding, null, 0, cancellationToken);
            }
        }
        catch
        {
            if (session is not null) Terminate(session);
            else if (binding is not null) _registry.Remove(run);
            throw;
        }
    }

    internal MaintenanceDiscoveryPage ReadNextPage(MaintenanceCursor cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryAcquire(cursor.Run, out MaintenanceRunLease? lease) || lease is null)
        {
            Terminate(cursor.Run);
            return ProtectedPage("Invalid continuation.");
        }
        using (lease)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(cursor.Run, out Session? session) || !session.Matches(cursor))
                    return ProtectedPage("Invalid continuation.");
                if (cursor.PageSequence != session.NextPageSequence || cursor.AfterCandidate != session.LastReturnedKey)
                    return ProtectedPage("Invalid continuation.");
                return ReadPageAndCleanup(session, lease.Binding, cursor.AfterCandidate, cursor.PageSequence, cancellationToken);
            }
        }
    }

    private MaintenanceDiscoveryPage ReadPageAndCleanup(
        Session session, MaintenanceRunBinding binding, MaintenanceCandidateKey? after,
        uint pageSequence, CancellationToken cancellationToken)
    {
        try
        {
            MaintenanceDiscoveryPage page = ReadPage(session, binding, after, pageSequence, cancellationToken);
            if (!page.HasMore) Terminate(session);
            return page;
        }
        catch (MaintenanceOrderingException)
        {
            Terminate(session);
            return InvalidContinuation();
        }
        catch
        {
            Terminate(session);
            throw;
        }
    }

    private void Terminate(Session session)
    {
        lock (_gate)
        {
            TerminateLocked(session);
        }
    }

    private void Terminate(MaintenanceRunId run)
    {
        lock (_gate)
        {
            if (_sessions.Remove(run, out Session? session))
            {
                try { session.Dispose(); }
                finally { _registry.Remove(run); }
            }
        }
    }

    private void TerminateLocked(Session session)
    {
        if (!_sessions.Remove(session.Run))
        {
            _registry.Remove(session.Run);
            return;
        }

        try { session.Dispose(); }
        finally { _registry.Remove(session.Run); }
    }

    private static MaintenanceDiscoveryPage ReadPage(
        Session session, MaintenanceRunBinding binding, MaintenanceCandidateKey? after,
        uint pageSequence, CancellationToken cancellationToken)
    {
        var projections = ImmutableArray.CreateBuilder<MaintenanceCandidateProjection>(MaintenanceLimits.ReturnedProjectionLimit);
        int scanned = 0;
        int metadataReads = 0;
        int lookahead = 0;
        MaintenanceCandidateKey? lastReturnedKey = after;
        bool hasMore = false;

        while (scanned < MaintenanceLimits.CandidatePageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaintenanceCandidate candidate;
            if (session.Pending is not null)
            {
                candidate = session.Pending;
                session.Pending = null;
            }
            else
            {
                if (!session.Enumerator.MoveNext()) break;
                candidate = session.Enumerator.Current;
            }
            scanned++;
            MaintenanceCandidateKey key = CandidateKey(candidate, session.Workspace, session.ScopeOrdinal,
                binding.SigningKey.Span);
            if (session.LastSortKey is not null && string.CompareOrdinal(CanonicalSortKey(candidate), session.LastSortKey) <= 0)
                throw new MaintenanceOrderingException();
            metadataReads++;
            if (projections.Count < MaintenanceLimits.ReturnedProjectionLimit)
            {
                projections.Add(Project(candidate, key));
                lastReturnedKey = key;
                session.LastSortKey = CanonicalSortKey(candidate);
            }
            else
            {
                lookahead = 1;
                hasMore = true;
                session.Pending = candidate;
                break;
            }
        }

        if (!hasMore && session.Pending is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasMore = session.Enumerator.MoveNext();
            if (hasMore)
            {
                session.Pending = session.Enumerator.Current;
                scanned++;
                metadataReads++;
                lookahead = 1;
            }
        }

        int projected = projections.Count + lookahead;
        MaintenanceCursor? continuation = hasMore
            ? new MaintenanceCursor(2, binding.Workspace, binding.Run, binding.Snapshot, binding.Scopes,
                session.ScopeOrdinal, MaintenanceCursorPhase.Discover, lastReturnedKey ?? new MaintenanceCandidateKey(Guid.Empty),
                pageSequence + 1, binding.PolicyHash, binding.ExpiresUnixSeconds)
            : null;
        session.LastReturnedKey = lastReturnedKey;
        session.NextPageSequence = checked(pageSequence + 1);
        return new MaintenanceDiscoveryPage(projections.ToImmutable(), continuation, scanned,
            projected, metadataReads, projections.Count, lookahead, hasMore, 0, 0,
            0, MaintenanceLimits.JournalBudgetMaximum, 0, 0, 0, 0, false, 0);
    }

    internal static MaintenanceDiscoveryPage InvalidContinuation() =>
        new([], null, 0, 0, 0, 0, 0, false, 0, 0, 0, MaintenanceLimits.JournalBudgetMaximum,
            0, 0, 0, 0, false, 0);

    private static MaintenanceDiscoveryPage ProtectedPage(string warning) => InvalidContinuation();

    private sealed class MaintenanceOrderingException : Exception;

    private static MaintenanceCandidateProjection Project(MaintenanceCandidate candidate, MaintenanceCandidateKey key) =>
        new(key, candidate.Kind, candidate.Scope, candidate.Subject, candidate.RelativePath,
            candidate.SidecarPath, candidate.Protection,
            candidate.Protection is null && (candidate.Kind == MaintenanceCandidateKind.ArchivePair ||
                candidate.VerificationOutcome == RecoveryVerificationOutcome.Verified &&
                candidate.LifecycleState is RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending or RecoveryLifecycleState.RolledBack));

    private static MaintenanceCandidateKey CandidateKey(MaintenanceCandidate candidate,
        MaintenanceWorkspaceKey workspace, ushort scopeOrdinal, ReadOnlySpan<byte> signingKey)
    {
        byte[] subject = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{candidate.Kind}\0{candidate.Scope}\0{candidate.Subject}\0{candidate.RelativePath}\0{candidate.OperationId}"));
        return MaintenanceCursorCodec.ComputeCandidateKey(workspace,
            new MaintenanceCandidateIdentity(candidate.Kind, workspace, scopeOrdinal,
                new MaintenanceHash256(subject), 0), signingKey);
    }

    private static string CanonicalSortKey(MaintenanceCandidate candidate) =>
        $"{(int)candidate.Kind:D4}\0{candidate.Scope}\0{candidate.Subject}\0{candidate.RelativePath}\0{candidate.OperationId}";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Exception? firstFailure = null;
            foreach (Session session in _sessions.Values.ToArray())
            {
                _sessions.Remove(session.Run);
                _registry.Remove(session.Run);
                try { session.Dispose(); }
                catch (Exception exception) { firstFailure ??= exception; }
            }
            _sessions.Clear();
            if (firstFailure is not null)
                ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }
    }

    private sealed class Session : IDisposable
    {
        internal Session(MaintenanceRunId run, MaintenanceWorkspaceKey workspace, MaintenanceSnapshotId snapshot,
            MaintenanceScopeBinding scopes, MaintenanceHash256 policy, string scope,
            IEnumerator<MaintenanceCandidate> enumerator)
        { Run = run; Workspace = workspace; Snapshot = snapshot; Scopes = scopes; Policy = policy; Scope = scope; Enumerator = enumerator; }
        internal MaintenanceRunId Run { get; }
        internal MaintenanceWorkspaceKey Workspace { get; }
        internal MaintenanceSnapshotId Snapshot { get; }
        internal MaintenanceScopeBinding Scopes { get; }
        internal MaintenanceHash256 Policy { get; }
        internal string Scope { get; }
        internal ushort ScopeOrdinal => 0;
        internal IEnumerator<MaintenanceCandidate> Enumerator { get; }
        internal MaintenanceCandidate? Pending { get; set; }
        internal string? LastSortKey { get; set; }
        internal MaintenanceCandidateKey? LastReturnedKey { get; set; }
        internal uint NextPageSequence { get; set; }
        internal bool Matches(MaintenanceCursor cursor) => cursor.Workspace == Workspace && cursor.Run == Run &&
            cursor.Snapshot == Snapshot && cursor.Scopes == Scopes && cursor.PolicyHash.Equals(Policy) &&
            cursor.ScopeOrdinal == ScopeOrdinal && cursor.Phase == MaintenanceCursorPhase.Discover;
        public void Dispose() => Enumerator.Dispose();
    }
}
