using System.Diagnostics;
using System.Security.Cryptography;

namespace Scrinia.Core;

/// <summary>Discovers recovery evidence from one store scope without widening the recovery boundary.</summary>
internal sealed class RecoveryDiscoveryCoordinator
{
    private const string RestoreFileName = ".restore-journal.json";
    private const string CoordinatorFileName = MultiMemoryTransactionRecordCodec.FileName;
    private const int EntityEvidenceScanLimit = MaintenanceLimits.JournalBudgetMaximum;
    private static readonly string[] FileEntityDirectoryNames = ["agent", "skills"];
    private readonly Dictionary<RecoveryOperation, IRecoveryAdapter> _adapters;
    private readonly Func<string, IEnumerable<string>> _directoryEnumerator;
    private readonly Func<string, IEnumerable<string>> _evidenceEnumerator;

    internal RecoveryDiscoveryCoordinator()
        : this(null, null, new RestoreRecoveryAdapter(), new AppendRecoveryAdapter(), new FileEntityRecoveryAdapter())
    {
    }

    internal RecoveryDiscoveryCoordinator(
        Func<string, IEnumerable<string>>? directoryEnumerator = null,
        Func<string, IEnumerable<string>>? evidenceEnumerator = null,
        params IRecoveryAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = (adapters.Length == 0
                ? [new RestoreRecoveryAdapter(), new AppendRecoveryAdapter(), new FileEntityRecoveryAdapter()]
                : adapters)
            .ToDictionary(adapter => adapter.Operation);
        _directoryEnumerator = directoryEnumerator ?? (root => Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly));
        _evidenceEnumerator = evidenceEnumerator ?? (directory => Directory.EnumerateFiles(directory, ".file-entity-*.recovery", SearchOption.TopDirectoryOnly));
    }

    internal RecoveryDiscoveryResult Discover(string scope, string storeDirectory,
        bool includeCoordinator = true, CancellationToken cancellationToken = default)
    {
        try
        {
            return DiscoverCore(scope, storeDirectory, includeCoordinator, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            string evidencePath = string.IsNullOrWhiteSpace(storeDirectory) ? "unknown" : storeDirectory;
            string blockedScope = string.IsNullOrWhiteSpace(scope) ? "unknown" : scope;
            var block = new RecoveryBlock(blockedScope, evidencePath, null, "unknown",
                exception is ArgumentException or NotSupportedException or RecoveryUnsafePathException
                    ? RecoveryBlockReason.Unsafe
                    : RecoveryBlockReason.ReplayIoFailed,
                "Recovery evidence discovery could not safely inspect the selected scope.");
            return new RecoveryDiscoveryResult([], block);
        }
    }

    internal IEnumerable<MaintenanceCandidate> EnumerateMaintenanceCandidates(
        string scope, string storeDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Recovery scope is required.", nameof(scope));
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("Recovery store directory is required.", nameof(storeDirectory));

        string root = PathContainment.ResolveCanonical(storeDirectory);
        RejectReparsePoint(root);
        int evidenceScanned = 0;

        foreach (MaintenanceCandidate candidate in EnumerateFixedMaintenanceCandidate(
            RecoveryOperation.Restore, Path.Combine(root, RestoreFileName), root, scope, cancellationToken))
            yield return candidate;
        if (File.Exists(Path.Combine(root, RestoreFileName)))
            yield break;

        foreach (MaintenanceCandidate candidate in EnumerateFixedMaintenanceCandidate(
            RecoveryOperation.Append, Path.Combine(root, "versions", AppendJournal.FileName), root, scope, cancellationToken))
            yield return candidate;
        if (File.Exists(Path.Combine(root, "versions", AppendJournal.FileName)))
            yield break;

        foreach (string entityDirectory in EnumerateMaintenanceEntityDirectories(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string evidencePath in OrderMaintenanceEvidencePaths(
                _evidenceEnumerator(entityDirectory).Take(EntityEvidenceScanLimit + 1)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++evidenceScanned > EntityEvidenceScanLimit)
                {
                    yield return CreateBlockedMaintenanceCandidate(scope, entityDirectory,
                        RecoveryOperation.FileEntity, "unknown", RecoveryBlockReason.ReplayIoFailed);
                    yield break;
                }

                foreach (MaintenanceCandidate candidate in InspectMaintenanceCandidate(
                    RecoveryOperation.FileEntity, evidencePath, entityDirectory, scope, cancellationToken))
                    yield return candidate;
            }
        }
    }

    internal static IReadOnlyList<string> OrderMaintenanceEvidencePaths(IEnumerable<string> paths) =>
        paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private IEnumerable<MaintenanceCandidate> EnumerateFixedMaintenanceCandidate(
        RecoveryOperation operation, string evidencePath, string evidenceDirectory, string scope,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(evidencePath))
            yield break;

        foreach (MaintenanceCandidate candidate in InspectMaintenanceCandidate(
            operation, evidencePath, evidenceDirectory, scope, cancellationToken))
            yield return candidate;
    }

    private IEnumerable<MaintenanceCandidate> InspectMaintenanceCandidate(
        RecoveryOperation operation, string evidencePath, string evidenceDirectory, string scope,
        CancellationToken cancellationToken)
    {
        var candidates = new List<RecoveryCandidate>(1);
        RecoveryBlock? block = InspectCandidate(operation, evidencePath, evidenceDirectory, scope,
            candidates, cancellationToken);
        if (block is not null)
        {
            yield return CreateBlockedMaintenanceCandidate(scope, block.EvidencePath,
                operation, block.OperationId, block.Reason);
            yield break;
        }

        foreach (RecoveryCandidate candidate in candidates)
            yield return new MaintenanceCandidate(scope, candidate.CanonicalSubject,
                MaintenanceCandidateKind.CompletedRecoveryEvidence,
                Path.GetRelativePath(evidenceDirectory, candidate.EvidencePath).Replace('\\', '/'), null,
                candidate.Envelope.UpdatedAt, candidate.OperationId, null,
                Path.GetRelativePath(evidenceDirectory, candidate.EvidencePath).Replace('\\', '/'))
            {
                VerificationOutcome = candidate.Verification?.Outcome ?? RecoveryVerificationOutcome.ProtectedIoFailure,
                LifecycleState = candidate.Envelope.State
            };
    }

    private static MaintenanceCandidate CreateBlockedMaintenanceCandidate(
        string scope, string evidencePath, RecoveryOperation operation, string operationId,
        RecoveryBlockReason reason)
    {
        string relative = Path.GetFileName(evidencePath).Length == 0 ? "blocked" : Path.GetFileName(evidencePath);
        return new MaintenanceCandidate(scope, evidencePath,
            MaintenanceCandidateKind.CompletedRecoveryEvidence, relative, null, null, operationId,
            reason == RecoveryBlockReason.UnsupportedVersion ? MaintenanceProtectionReason.UnsupportedVersion
                : MaintenanceProtectionReason.UnresolvedRecovery, relative);
    }

    private RecoveryDiscoveryResult DiscoverCore(string scope, string storeDirectory,
        bool includeCoordinator, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Recovery scope is required.", nameof(scope));
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("Recovery store directory is required.", nameof(storeDirectory));

        string root = PathContainment.ResolveCanonical(storeDirectory);
        RejectReparsePoint(root);
        var candidates = new List<RecoveryCandidate>();

        RecoveryDiscoveryResult? coordinator = includeCoordinator ? InspectCoordinator(root, scope, cancellationToken) : null;
        if (coordinator is not null)
            return coordinator;

        RecoveryBlock? blocking = InspectFixedCandidate(RecoveryOperation.Restore,
            Path.Combine(root, RestoreFileName), root, scope, candidates, cancellationToken);
        if (blocking is not null)
            return new RecoveryDiscoveryResult(candidates, blocking);

        blocking = InspectFixedCandidate(RecoveryOperation.Append,
            Path.Combine(root, "versions", AppendJournal.FileName), root, scope, candidates, cancellationToken);
        if (blocking is not null)
            return new RecoveryDiscoveryResult(candidates, blocking);

        int evidenceScanned = 0;
        foreach (string entityDirectory in EnumerateEntityDirectories(root, cancellationToken))
        {
            int remainingEvidence = EntityEvidenceScanLimit - evidenceScanned;
            foreach (string evidencePath in OrderMaintenanceEvidencePaths(
                _evidenceEnumerator(entityDirectory).Take(remainingEvidence + 1)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                evidenceScanned++;
                if (evidenceScanned > EntityEvidenceScanLimit)
                    return new RecoveryDiscoveryResult(candidates, new RecoveryBlock(scope, entityDirectory,
                        RecoveryOperation.FileEntity, "unknown", RecoveryBlockReason.ReplayIoFailed,
                        "Recovery evidence scan exceeded its bounded admission limit."));

                blocking = InspectCandidate(RecoveryOperation.FileEntity, evidencePath, entityDirectory, scope, candidates, cancellationToken);
                if (blocking is not null)
                    return new RecoveryDiscoveryResult(candidates, blocking);
            }
        }

        var ordered = candidates
            .OrderBy(candidate => Precedence(candidate.Operation))
            .ThenBy(candidate => candidate.CanonicalSubject, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.OperationId, StringComparer.Ordinal)
            .ToArray();

        RecoveryBlock? duplicate = ordered
            .GroupBy(candidate => (candidate.Operation, candidate.CanonicalSubject))
            .Where(group => group.Count() > 1)
            .Select(group => new RecoveryBlock(scope, group.First().EvidencePath, group.Key.Operation,
                group.First().OperationId, RecoveryBlockReason.Ambiguous,
                "Multiple recovery candidates claim the same operation subject."))
            .FirstOrDefault();

        return new RecoveryDiscoveryResult(ordered, duplicate);
    }

    private static RecoveryDiscoveryResult? InspectCoordinator(string root, string scope,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(root, CoordinatorFileName);
        try
        {
            RejectReparsePoint(path);
            MultiMemoryTransactionRecord record = MultiMemoryTransactionRecordCodec.Read(root, scope, cancellationToken);
            return new RecoveryDiscoveryResult([], null, record);
        }
        catch (MultiMemoryTransactionRecordException exception)
        {
            if (IsMissingEvidence(exception))
                return null;

            RecoveryBlockReason reason = exception.Failure switch
            {
                MultiMemoryFailure.UnsupportedVersion => RecoveryBlockReason.UnsupportedVersion,
                MultiMemoryFailure.Unsafe => RecoveryBlockReason.Unsafe,
                MultiMemoryFailure.Inconsistent => RecoveryBlockReason.Inconsistent,
                MultiMemoryFailure.ReplayIoFailed => RecoveryBlockReason.ReplayIoFailed,
                _ => RecoveryBlockReason.Malformed
            };
            return new RecoveryDiscoveryResult([], new RecoveryBlock(scope, path, null, "unknown", reason,
                exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (exception is FileNotFoundException or DirectoryNotFoundException)
                return null;

            return new RecoveryDiscoveryResult([], new RecoveryBlock(scope, path, null, "unknown",
                exception is ArgumentException ? RecoveryBlockReason.Unsafe : RecoveryBlockReason.ReplayIoFailed,
                "Coordinator record could not safely be inspected."));
        }
    }

    private static bool IsMissingEvidence(MultiMemoryTransactionRecordException exception) =>
        exception.InnerException is FileNotFoundException or DirectoryNotFoundException;

    internal RecoveryOutcome AdmitNext(string scope, string storeDirectory,
        RecoveryDiscoveryResult discovery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("Recovery store directory is required.", nameof(storeDirectory));
        if (discovery.BlockingEvidence is not null)
        {
            RecoveryBlock block = discovery.BlockingEvidence;
            return new RecoveryOutcome(true, false, false, block.Scope, block.EvidencePath,
                block.OperationId, block, null);
        }

        RecoveryCandidate? candidate = discovery.Candidates.Count == 0 ? null : discovery.Candidates[0];
        if (candidate is null)
            return new RecoveryOutcome(false, false, false, scope, storeDirectory, "none", null, null);

        IRecoveryAdapter adapter = _adapters[candidate.Operation];
        if (candidate.Envelope.State is RecoveryLifecycleState.Committed or RecoveryLifecycleState.CleanupPending)
            return adapter.RecoverOrCleanup(candidate.Envelope, candidate.EvidencePath, cancellationToken);

        return adapter.Admit(candidate.Envelope, candidate.EvidencePath);
    }

    private RecoveryBlock? InspectFixedCandidate(RecoveryOperation operation, string evidencePath,
        string evidenceDirectory, string scope, List<RecoveryCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(evidencePath))
            return null;
        RejectReparsePoint(evidencePath);
        return InspectCandidate(operation, evidencePath, evidenceDirectory, scope, candidates, cancellationToken);
    }

    private RecoveryBlock? InspectCandidate(RecoveryOperation operation, string evidencePath,
        string evidenceDirectory, string scope, List<RecoveryCandidate> candidates,
        CancellationToken cancellationToken)
    {
        IRecoveryAdapter adapter = _adapters[operation];
        RecoveryVerifiedEnvelope? verified = null;
        RecoveryVerificationAccounting? verifiedAccounting = null;
        try
        {
            RejectReparsePoint(evidencePath);
            RecoveryVerifiedEvidence evidence = ReadVerified(operation, evidencePath, evidenceDirectory, scope, cancellationToken);
            verified = evidence.Envelope;
            verifiedAccounting = evidence.Accounting;
            RecoveryEnvelopeDocument envelope = adapter.ConsumeParsed(in verified);
            string subject = SubjectOf(envelope);
            envelope = adapter.ValidateForMutation(envelope, scope, evidencePath, subject, evidenceDirectory);
            candidates.Add(new RecoveryCandidate(operation, evidencePath, scope, subject,
                envelope.OperationId, envelope, envelope.IsLegacy)
            { Verification = verifiedAccounting });
            return null;
        }
        catch (RecoveryEnvelopeException exception)
        {
            return new RecoveryBlock(scope, evidencePath, operation,
                exception.OperationId ?? verified?.Document.OperationId ?? TryFileEntityOperationId(evidencePath),
                Classify(exception.Failure),
                exception.Message, exception.UnsupportedVersion);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new RecoveryBlock(scope, evidencePath, operation, verified?.Document.OperationId ?? TryFileEntityOperationId(evidencePath),
                exception is ArgumentException or RecoveryUnsafePathException
                    ? RecoveryBlockReason.Unsafe : RecoveryBlockReason.ReplayIoFailed, exception.Message);
        }
    }

    private static string SubjectOf(RecoveryEnvelopeDocument envelope) => envelope.TypedBody switch
    {
        AppendRecoveryBody append when !string.IsNullOrWhiteSpace(envelope.Address) => envelope.Address!,
        RestoreRecoveryBody restore => "/" + restore.Entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).First().Name,
        FileEntityRecoveryBody fileEntity => $"{fileEntity.EntityKind}/{fileEntity.EntityName}",
        _ => throw new InvalidDataException("Recovery evidence has no canonical subject.")
    };

    internal static IReadOnlyList<string> OrderMaintenanceDirectories(IEnumerable<string> paths)
    {
        string[] bounded = paths.Take(MaintenanceLimits.UnsafeScanLimit + 1)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (bounded.Length > MaintenanceLimits.UnsafeScanLimit)
            throw new IOException("Recovery directory enumeration exceeded its bounded admission limit.");
        return bounded;
    }

    private IEnumerable<string> EnumerateEntityDirectories(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            yield break;
        foreach (string directory in OrderMaintenanceDirectories(_directoryEnumerator(root)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileEntityDirectoryNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase) ||
                !Directory.Exists(directory))
                continue;
            RejectReparsePoint(directory);
            yield return directory;
        }
    }

    private static IEnumerable<string> EnumerateMaintenanceEntityDirectories(
        string root, CancellationToken cancellationToken)
    {
        foreach (string directoryName in FileEntityDirectoryNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.Combine(root, directoryName);
            if (!Directory.Exists(directory))
                continue;
            RejectReparsePoint(directory);
            yield return directory;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new RecoveryUnsafePathException($"Recovery evidence path uses a reparse point: {path}");
    }

    private readonly record struct RecoveryVerifiedEvidence(
        RecoveryVerifiedEnvelope Envelope, RecoveryVerificationAccounting Accounting);

    private static RecoveryVerifiedEvidence ReadVerified(RecoveryOperation operation, string path, string scopeDirectory, string scope,
        CancellationToken cancellationToken)
    {
        string relativeName = Path.GetRelativePath(scopeDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
        var verifier = new RecoveryEvidenceVerifier(new RecoveryScopeBinding(scope, scopeDirectory));
        var request = new RecoveryVerificationRequest(
            new RecoveryScopeBinding(scope, scopeDirectory), operation switch
            {
                RecoveryOperation.Append => RecoveryEvidenceKind.AppendJournal,
                RecoveryOperation.Restore => RecoveryEvidenceKind.RestoreJournal,
                RecoveryOperation.FileEntity => RecoveryEvidenceKind.FileEntityJournal,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            },
            relativeName, null, default, null, MaintenanceLimits.Hard);
        RecoveryVerificationResult result = verifier.VerifyAsync(request, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Outcome != RecoveryVerificationOutcome.Verified || result.SourceContent is null)
            throw new IOException($"Recovery evidence verification failed: {result.Outcome}.");
        using (result.SourceContent)
        {
            RecoveryVerificationContent content = result.SourceContent;
            RecoveryVerifiedFileMetadata metadata = content.Metadata;
            RecoveryScopeBinding scopeBinding = new(scope, scopeDirectory);
            RecoveryVerifiedEnvelope envelope = RecoveryEnvelopeCodec.ParseVerifiedEnvelope(in content, in scopeBinding,
                path, scopeDirectory, operation switch
                {
                    RecoveryOperation.Append => RecoveryEvidenceKind.AppendJournal,
                    RecoveryOperation.Restore => RecoveryEvidenceKind.RestoreJournal,
                    RecoveryOperation.FileEntity => RecoveryEvidenceKind.FileEntityJournal,
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                }, in metadata);
            return new RecoveryVerifiedEvidence(envelope, result.Accounting());
        }
    }

    private static int Precedence(RecoveryOperation operation) => operation switch
    {
        RecoveryOperation.Restore => 0,
        RecoveryOperation.Append => 1,
        RecoveryOperation.FileEntity => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static RecoveryBlockReason Classify(RecoveryEnvelopeFailure failure) => failure switch
    {
        RecoveryEnvelopeFailure.UnsupportedVersion => RecoveryBlockReason.UnsupportedVersion,
        RecoveryEnvelopeFailure.WrongScope => RecoveryBlockReason.WrongScope,
        RecoveryEnvelopeFailure.Unsafe => RecoveryBlockReason.Unsafe,
        RecoveryEnvelopeFailure.Inconsistent => RecoveryBlockReason.Inconsistent,
        _ => RecoveryBlockReason.Malformed
    };

    private static string TryFileEntityOperationId(string evidencePath)
    {
        string name = Path.GetFileName(evidencePath);
        const string prefix = ".file-entity-";
        const string suffix = ".recovery";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal) &&
            name.Length > prefix.Length + suffix.Length
            ? name[prefix.Length..^suffix.Length]
            : "unknown";
    }
}

internal sealed class B2RecoveryMaintenanceAdapter : IB2RecoveryMaintenanceAdapter
{
    private readonly string? _evidenceRoot;

    internal B2RecoveryMaintenanceAdapter(string? evidenceRoot = null) =>
        _evidenceRoot = evidenceRoot is null ? null : PathContainment.ResolveCanonical(evidenceRoot);

    public IReadOnlyList<B2RecoveryMaintenanceProjection> Project(RecoveryDiscoveryResult discovery, string expectedScope)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        RequireScope(expectedScope);
        var views = discovery.Candidates.Select(candidate =>
        {
            if (!string.Equals(candidate.Scope, expectedScope, StringComparison.Ordinal))
                return Blocked(new RecoveryBlock(expectedScope, candidate.EvidencePath, candidate.Operation,
                    candidate.OperationId, RecoveryBlockReason.WrongScope, "Recovery evidence scope does not match the selected scope."), expectedScope);
            if (!Path.IsPathRooted(candidate.EvidencePath))
                throw new ArgumentException("Recovery evidence path must be absolute at the adapter boundary.", nameof(discovery));
            if (candidate.Envelope.TypedBody is null)
                throw new ArgumentException("Recovery candidate has no typed body.", nameof(discovery));

            RecoveryVerificationAccounting verification = candidate.Verification ?? default;
            bool terminal = candidate.Envelope.State is RecoveryLifecycleState.Committed or RecoveryLifecycleState.RolledBack;
            bool verified = verification.Outcome == RecoveryVerificationOutcome.Verified;
            string evidenceRoot = _evidenceRoot ?? EvidenceRoot(candidate);
            bool pathSafe = IsSafeEvidencePath(candidate.EvidencePath, evidenceRoot);
            return new B2RecoveryMaintenanceProjection(candidate.Operation, candidate.Scope,
                pathSafe ? SafeRelativePath(candidate.EvidencePath, evidenceRoot) : "recovery/unsafe",
                candidate.CanonicalSubject, candidate.OperationId, candidate.Envelope.State,
                candidate.Envelope.CreatedAt, candidate.Envelope.UpdatedAt,
                terminal && verified,
                false, false, false, pathSafe)
            {
                VerificationOutcome = verification.Outcome,
                VerificationBytesRead = verification.BytesRead,
                VerificationOpenOperations = verification.OpenOperations,
                VerificationStatOperations = verification.StatOperations,
                VerificationReadOperations = verification.ReadOperations,
                VerificationFinalZeroByteRead = verification.FinalZeroByteRead,
                VerificationDeadlineObserved = verification.DeadlineObserved,
                VerificationCancellationObserved = verification.CancellationObserved
            };
        }).ToArray();

        if (discovery.BlockingEvidence is not null)
            views = views.Append(Blocked(discovery.BlockingEvidence, expectedScope)).ToArray();
        return views.OrderBy(view => view.EvidencePath, StringComparer.Ordinal).ToArray();
    }

    public RecoveryCleanupDecision EvaluateCleanup(B2RecoveryMaintenanceProjection view, IReadOnlySet<string> referencedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(referencedRelativePaths);
        string path = view.EvidencePath;
        if (view.Protection is not null) return Protected(path, view.Protection.Value, null);
        if (!view.IsPathSafe) return Protected(path, view.Protection ?? MaintenanceProtectionReason.Unsafe, "Recovery evidence path is unsafe.");
        if (view.IsQuarantined || view.IsAmbiguous) return Protected(path, view.IsQuarantined ? MaintenanceProtectionReason.UnresolvedRecovery : MaintenanceProtectionReason.Ambiguous, null);
        if (referencedRelativePaths.Contains(path)) return Protected(path, MaintenanceProtectionReason.Referenced, null);
        if (!view.IsTerminalAndVerified) return Protected(path, MaintenanceProtectionReason.UnresolvedRecovery, null);
        return new RecoveryCleanupDecision(true, false, null, path, null);
    }

    private B2RecoveryMaintenanceProjection Blocked(RecoveryBlock block, string scope)
    {
        if (!string.Equals(block.Scope, scope, StringComparison.Ordinal))
            throw new ArgumentException("Recovery block scope does not match the requested scope.", nameof(scope));
        RecoveryOperation operation = block.Operation ?? RecoveryOperation.Restore;
        string root = _evidenceRoot ?? Path.GetDirectoryName(block.EvidencePath) ?? string.Empty;
        bool pathSafe = IsSafeEvidencePath(block.EvidencePath, root);
        return new B2RecoveryMaintenanceProjection(operation, scope, pathSafe ? SafeRelativePath(block.EvidencePath, root) : "recovery/unsafe", "unknown", block.OperationId,
            RecoveryLifecycleState.RecoveryRequired, DateTimeOffset.MinValue, DateTimeOffset.MinValue,
            false, false, false, block.Reason == RecoveryBlockReason.Ambiguous, pathSafe)
        {
            Protection = MapProtection(block.Reason)
        };
    }

    private static MaintenanceProtectionReason MapProtection(RecoveryBlockReason reason) => reason switch
    {
        RecoveryBlockReason.Malformed => MaintenanceProtectionReason.Malformed,
        RecoveryBlockReason.UnsupportedVersion => MaintenanceProtectionReason.UnsupportedVersion,
        RecoveryBlockReason.Ambiguous => MaintenanceProtectionReason.Ambiguous,
        RecoveryBlockReason.Inconsistent => MaintenanceProtectionReason.Unverifiable,
        RecoveryBlockReason.WrongScope => MaintenanceProtectionReason.Unsafe,
        RecoveryBlockReason.Unsafe => MaintenanceProtectionReason.Unsafe,
        RecoveryBlockReason.ReplayIoFailed => MaintenanceProtectionReason.Inaccessible,
        _ => MaintenanceProtectionReason.UnresolvedRecovery
    };

    internal static string SafeRelativePath(string evidencePath)
    {
        if (!Path.IsPathRooted(evidencePath))
            return MaintenanceContractValidation.RequireRelativePath(evidencePath, nameof(evidencePath));
        string file = Path.GetFileName(evidencePath);
        string? parent = Path.GetFileName(Path.GetDirectoryName(evidencePath));
        return parent is "versions" or "skills" or "agents" ? $"{parent}/{file}" : file;
    }

    internal static string SafeRelativePath(string evidencePath, string root)
    {
        if (!IsSafeEvidencePath(evidencePath, root)) return "recovery/unsafe";
        string relative = Path.GetRelativePath(root, evidencePath).Replace('\\', '/');
        return MaintenanceContractValidation.RequireRelativePath(relative, nameof(evidencePath));
    }

    internal static bool IsSafeEvidencePath(string path, string root) => Path.IsPathRooted(path) && Path.IsPathRooted(root) &&
        PathContainment.IsWithin(root, path) && !HasReparsePoint(path, root);

    private static bool HasReparsePoint(string path, string root)
    {
        string current = Path.GetFullPath(root);
        string relative = Path.GetRelativePath(current, path);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static string EvidenceRoot(RecoveryCandidate candidate) => candidate.Operation switch
    {
        RecoveryOperation.Append => Path.GetDirectoryName(Path.GetDirectoryName(candidate.EvidencePath)!)!,
        _ => candidate.Operation == RecoveryOperation.FileEntity
            ? Path.GetDirectoryName(Path.GetDirectoryName(candidate.EvidencePath)!)!
            : Path.GetDirectoryName(candidate.EvidencePath)!,
    };

    private static RecoveryCleanupDecision Protected(string path, MaintenanceProtectionReason reason, string? warning) =>
        new(false, true, reason, path, warning);

    private static void RequireScope(string scope) =>
        MaintenanceContractValidation.RequireSafeText(scope, nameof(scope));
}

internal sealed class B3CoordinatorMaintenanceAdapter : IB3CoordinatorMaintenanceAdapter
{
    internal static B3CoordinatorMaintenanceProjection Blocked(RecoveryBlock block, string expectedScope) =>
        new(expectedScope, block.OperationId, MultiMemoryTransactionState.RecoveryRequired, DateTimeOffset.MinValue,
            DateTimeOffset.MinValue, [], [], [], [], false, false)
        { Protection = block.Reason switch
            {
                RecoveryBlockReason.Malformed => MaintenanceProtectionReason.Malformed,
                RecoveryBlockReason.UnsupportedVersion => MaintenanceProtectionReason.UnsupportedVersion,
                RecoveryBlockReason.Ambiguous => MaintenanceProtectionReason.Ambiguous,
                RecoveryBlockReason.Inconsistent => MaintenanceProtectionReason.Unverifiable,
                RecoveryBlockReason.WrongScope => MaintenanceProtectionReason.Unsafe,
        RecoveryBlockReason.Unsafe => MaintenanceProtectionReason.Unsafe,
                _ => MaintenanceProtectionReason.Inaccessible
            } };

    public B3CoordinatorMaintenanceProjection Project(MultiMemoryTransactionRecord record, string expectedScope)
    {
        ArgumentNullException.ThrowIfNull(record);
        MaintenanceContractValidation.RequireSafeText(expectedScope, nameof(expectedScope));
        if (!string.Equals(record.Scope, expectedScope, StringComparison.Ordinal))
            throw new ArgumentException("Coordinator record scope does not match the requested scope.", nameof(record));

        var participants = (record.TransactionalParticipants ?? []).Select(new B4ParticipantMaintenanceAdapter().Project).ToArray();
        var rejected = (record.RejectedTransactionalParticipants ?? []).Select(new B4ParticipantMaintenanceAdapter().ProjectRejected).ToArray();
        bool proofsComplete = record.CompletedParticipants is { Count: > 0 } completions &&
            record.Participants.All(participant => completions.Any(completion => completion.Subject == participant.Subject && completion.OperationId == participant.OperationId && completion.Kind == participant.Kind));
        bool terminal = record.State == MultiMemoryTransactionState.Committed && proofsComplete &&
            participants.All(participant => participant.IsTerminalAndVerified) && rejected.Length == 0;
        return new B3CoordinatorMaintenanceProjection(record.Scope, record.TransactionId, record.State,
            record.CreatedAt, record.UpdatedAt, record.Participants.ToArray(),
            (record.CompletedParticipants ?? []).ToArray(), participants, rejected, terminal, true);
    }

    public RecoveryCleanupDecision EvaluateCleanup(B3CoordinatorMaintenanceProjection projection, IReadOnlySet<string> referencedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(referencedRelativePaths);
        const string path = MultiMemoryTransactionRecordCodec.FileName;
        if (!projection.IsPathSafe) return new(false, true, projection.Protection ?? MaintenanceProtectionReason.Unsafe, path, null);
        if (referencedRelativePaths.Contains(path)) return new(false, true, MaintenanceProtectionReason.Referenced, path, null);
        if (!projection.IsTerminalAndVerified) return new(false, true, MaintenanceProtectionReason.UnresolvedRecovery, path, null);
        return new(true, false, null, path, null);
    }
}

internal sealed class B4ParticipantMaintenanceAdapter : IB4ParticipantMaintenanceAdapter
{
    public B4ParticipantMaintenanceProjection Project(B4ParticipantRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool hasEvidence = !string.IsNullOrWhiteSpace(state.EvidenceReference);
        bool safe = hasEvidence && IsSafe(state.EvidenceReference) && IsSafe(state.EvidenceKind) && IsSafe(state.EvidenceVersion);
        bool terminal = state.LifecycleState is TransactionalParticipantLifecycleState.Committed or TransactionalParticipantLifecycleState.RolledBack;
        terminal &= state.QuarantineState == TransactionalQuarantineState.None && !state.Ambiguous && !state.StateMayHaveChanged && safe;
        MaintenanceProtectionReason? protection = !hasEvidence
            ? MaintenanceProtectionReason.MissingExpectedMember
            : !safe ? MaintenanceProtectionReason.Unsafe : null;
        return new B4ParticipantMaintenanceProjection(state.TransactionId, state.OperationId, state.Scope, state.Address,
            state.ParticipantIdentity, state.OrderingKey, state.Kind, state.LifecycleState, state.QuarantineState,
            state.EvidenceReference, state.EvidenceKind, state.EvidenceVersion, state.StateMayHaveChanged,
            state.Ambiguous, terminal, safe) { Protection = protection };
    }

    public B4RejectedParticipantMaintenanceProjection ProjectRejected(B4ParticipantRequestMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new B4RejectedParticipantMaintenanceProjection(metadata.TransactionId, metadata.OperationId, metadata.Scope,
            metadata.Address, metadata.ParticipantIdentity, metadata.OrderingKey, metadata.Kind, metadata.RejectionCode,
            IsSafe(metadata.Address) && IsSafe(metadata.ParticipantIdentity) && IsSafe(metadata.OrderingKey));
    }

    public B4QuarantineMaintenanceProjection ProjectQuarantine(B4ParticipantRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new B4QuarantineMaintenanceProjection(state.TransactionId, state.OperationId, state.Scope, state.Address,
            state.QuarantineState, state.QuarantineReason, IsSafe(state.QuarantineReason));
    }

    public RecoveryCleanupDecision EvaluateCleanup(B4ParticipantMaintenanceProjection projection, IReadOnlySet<string> referencedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(referencedRelativePaths);
        if (projection.Protection is not null)
            return new(false, true, projection.Protection.Value, string.Empty, null);
        if (projection.EvidenceReference is null || !B4Validation.SafeReference(projection.EvidenceReference))
            return new(false, true, MaintenanceProtectionReason.MissingExpectedMember, string.Empty, null);
        string path = projection.EvidenceReference;
        if (!projection.IsPathSafe) return new(false, true, MaintenanceProtectionReason.Unsafe, string.Empty, null);
        if (projection.QuarantineState == TransactionalQuarantineState.Quarantined) return new(false, true, MaintenanceProtectionReason.UnresolvedRecovery, path, null);
        if (projection.Ambiguous || projection.StateMayHaveChanged) return new(false, true, MaintenanceProtectionReason.Ambiguous, path, null);
        if (!projection.IsTerminalAndVerified) return new(false, true, MaintenanceProtectionReason.UnresolvedRecovery, path, null);
        if (referencedRelativePaths.Contains(path)) return new(false, true, MaintenanceProtectionReason.Referenced, path, null);
        return new(true, false, null, path, null);
    }

    private static bool IsSafe(string? value) => value is null || B4Validation.SafeReference(value);
}
