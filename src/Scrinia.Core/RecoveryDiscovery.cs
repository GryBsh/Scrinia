using System.Text.Json;

namespace Scrinia.Core;

/// <summary>Discovers recovery evidence from one store scope without widening the recovery boundary.</summary>
internal sealed class RecoveryDiscoveryCoordinator
{
    private const string RestoreFileName = ".restore-journal.json";
    private const string CoordinatorFileName = MultiMemoryTransactionRecordCodec.FileName;
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

    internal RecoveryDiscoveryResult Discover(string scope, string storeDirectory, bool includeCoordinator = true)
    {
        try
        {
            return DiscoverCore(scope, storeDirectory, includeCoordinator);
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

    private RecoveryDiscoveryResult DiscoverCore(string scope, string storeDirectory, bool includeCoordinator)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Recovery scope is required.", nameof(scope));
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("Recovery store directory is required.", nameof(storeDirectory));

        string root = PathContainment.ResolveCanonical(storeDirectory);
        RejectReparsePoint(root);
        var candidates = new List<RecoveryCandidate>();

        RecoveryDiscoveryResult? coordinator = includeCoordinator ? InspectCoordinator(root, scope) : null;
        if (coordinator is not null)
            return coordinator;

        RecoveryBlock? blocking = InspectFixedCandidate(RecoveryOperation.Restore,
            Path.Combine(root, RestoreFileName), root, scope, candidates);
        if (blocking is not null)
            return new RecoveryDiscoveryResult(candidates, blocking);

        blocking = InspectFixedCandidate(RecoveryOperation.Append,
            Path.Combine(root, "versions", AppendJournal.FileName), root, scope, candidates);
        if (blocking is not null)
            return new RecoveryDiscoveryResult(candidates, blocking);

        foreach (string entityDirectory in EnumerateEntityDirectories(root))
        {
            foreach (string evidencePath in _evidenceEnumerator(entityDirectory)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                blocking = InspectCandidate(RecoveryOperation.FileEntity, evidencePath, entityDirectory, scope, candidates);
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

    private static RecoveryDiscoveryResult? InspectCoordinator(string root, string scope)
    {
        string path = Path.Combine(root, CoordinatorFileName);
        try
        {
            RejectReparsePoint(path);
            MultiMemoryTransactionRecord record = MultiMemoryTransactionRecordCodec.Read(root, scope);
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

    internal RecoveryMaintenanceDiscoveryPage DiscoverForMaintenance(string scope, string storeDirectory,
        MaintenanceDiscoveryCursor cursor, int candidateBudget, TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (candidateBudget is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(candidateBudget));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero, nameof(deadline));
        ArgumentNullException.ThrowIfNull(cursor);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        RecoveryDiscoveryResult recovery = Discover(scope, storeDirectory, includeCoordinator: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow - started >= deadline)
            return new RecoveryMaintenanceDiscoveryPage([], null, cursor, true);

        IReadOnlyList<B2RecoveryMaintenanceProjection> projected = new B2RecoveryMaintenanceAdapter().Project(recovery, scope);
        var selected = projected.Where(view => cursor.AfterRelativePath is null ||
                string.CompareOrdinal(B2RecoveryMaintenanceAdapter.SafeRelativePath(view.EvidencePath), cursor.AfterRelativePath) > 0)
            .Take(candidateBudget).ToArray();
        string? last = selected.Length == 0 ? cursor.AfterRelativePath : B2RecoveryMaintenanceAdapter.SafeRelativePath(selected[^1].EvidencePath);
        bool more = projected.Count(view => cursor.AfterRelativePath is null ||
            string.CompareOrdinal(B2RecoveryMaintenanceAdapter.SafeRelativePath(view.EvidencePath), cursor.AfterRelativePath) > 0) > selected.Length;

        B3CoordinatorMaintenanceProjection? coordinator = null;
        RecoveryDiscoveryResult coordinatorDiscovery = Discover(scope, storeDirectory, includeCoordinator: true);
        if (coordinatorDiscovery.CoordinatorRecord is not null)
            coordinator = new B3CoordinatorMaintenanceAdapter().Project(coordinatorDiscovery.CoordinatorRecord, scope);
        return new RecoveryMaintenanceDiscoveryPage(selected, coordinator,
            more ? new MaintenanceDiscoveryCursor(last) : null, more);
    }

    private RecoveryBlock? InspectFixedCandidate(RecoveryOperation operation, string evidencePath,
        string evidenceDirectory, string scope, List<RecoveryCandidate> candidates)
    {
        if (!File.Exists(evidencePath))
            return null;
        RejectReparsePoint(evidencePath);
        return InspectCandidate(operation, evidencePath, evidenceDirectory, scope, candidates);
    }

    private RecoveryBlock? InspectCandidate(RecoveryOperation operation, string evidencePath,
        string evidenceDirectory, string scope, List<RecoveryCandidate> candidates)
    {
        IRecoveryAdapter adapter = _adapters[operation];
        byte[]? bytes = null;
        try
        {
            RejectReparsePoint(evidencePath);
            bytes = ReadBounded(evidencePath);
            RecoveryEnvelopeDocument envelope = adapter.ParseAndNormalize(bytes, scope, evidencePath, evidenceDirectory);
            string subject = SubjectOf(envelope);
            envelope = adapter.ValidateForMutation(envelope, scope, evidencePath, subject, evidenceDirectory);
            candidates.Add(new RecoveryCandidate(operation, evidencePath, scope, subject,
                envelope.OperationId, envelope, envelope.IsLegacy));
            return null;
        }
        catch (RecoveryEnvelopeException exception)
        {
            return new RecoveryBlock(scope, evidencePath, operation, TryOperationId(operation, evidencePath, bytes),
                Classify(exception.Failure),
                exception.Message, exception.UnsupportedVersion);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new RecoveryBlock(scope, evidencePath, operation, TryOperationId(operation, evidencePath, bytes),
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

    private IEnumerable<string> EnumerateEntityDirectories(string root)
    {
        if (!Directory.Exists(root))
            yield break;
        foreach (string directory in _directoryEnumerator(root)
                     .Where(path => FileEntityDirectoryNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                     .Where(Directory.Exists)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            RejectReparsePoint(directory);
            yield return directory;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new RecoveryUnsafePathException($"Recovery evidence path uses a reparse point: {path}");
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        const int maximumBytes = 128 * 1024 * 1024;
        if (stream.Length > maximumBytes)
            throw new IOException("Recovery evidence exceeds the maximum document size.");
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[81_920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            bytes.Write(buffer, 0, read);
            if (bytes.Length > maximumBytes)
                throw new IOException("Recovery evidence exceeds the maximum document size.");
        }
        return bytes.ToArray();
    }

    private static int Precedence(RecoveryOperation operation) => operation switch
    {
        RecoveryOperation.Restore => 0,
        RecoveryOperation.Append => 1,
        RecoveryOperation.FileEntity => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static string TryOperationId(RecoveryOperation operation, string evidencePath, byte[]? bytes) =>
        operation switch
        {
            RecoveryOperation.Append or RecoveryOperation.Restore => TryJsonOperationId(bytes),
            RecoveryOperation.FileEntity => TryFileEntityOperationId(evidencePath),
            _ => "unknown"
        };

    private static RecoveryBlockReason Classify(RecoveryEnvelopeFailure failure) => failure switch
    {
        RecoveryEnvelopeFailure.UnsupportedVersion => RecoveryBlockReason.UnsupportedVersion,
        RecoveryEnvelopeFailure.WrongScope => RecoveryBlockReason.WrongScope,
        RecoveryEnvelopeFailure.Unsafe => RecoveryBlockReason.Unsafe,
        RecoveryEnvelopeFailure.Inconsistent => RecoveryBlockReason.Inconsistent,
        _ => RecoveryBlockReason.Malformed
    };

    private static string TryJsonOperationId(byte[]? bytes)
    {
        if (bytes is null) return "unknown";
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            foreach (string name in new[] { "operationId", "transactionId" })
                if (document.RootElement.TryGetProperty(name, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString()!;
        }
        catch (JsonException) { }
        return "unknown";
    }

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
    public IReadOnlyList<B2RecoveryMaintenanceProjection> Project(RecoveryDiscoveryResult discovery, string expectedScope)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        RequireScope(expectedScope);
        var views = discovery.Candidates.Select(candidate =>
        {
            if (!string.Equals(candidate.Scope, expectedScope, StringComparison.Ordinal))
                throw new ArgumentException("Recovery candidate scope does not match the requested scope.", nameof(discovery));
            if (!Path.IsPathRooted(candidate.EvidencePath))
                throw new ArgumentException("Recovery evidence path must be absolute at the adapter boundary.", nameof(discovery));
            if (candidate.Envelope.TypedBody is null)
                throw new ArgumentException("Recovery candidate has no typed body.", nameof(discovery));

            bool terminal = candidate.Envelope.State is RecoveryLifecycleState.Committed or RecoveryLifecycleState.RolledBack;
            bool pathSafe = IsSafeEvidencePath(candidate.EvidencePath);
            return new B2RecoveryMaintenanceProjection(candidate.Operation, candidate.Scope,
                pathSafe ? SafeRelativePath(candidate.EvidencePath) : "recovery/unsafe",
                candidate.CanonicalSubject, candidate.OperationId, candidate.Envelope.State,
                candidate.Envelope.CreatedAt, candidate.Envelope.UpdatedAt, terminal && IsVerified(candidate),
                false, false, false, pathSafe);
        }).ToArray();

        if (discovery.BlockingEvidence is not null)
            views = views.Append(Blocked(discovery.BlockingEvidence, expectedScope)).ToArray();
        return views.OrderBy(view => SafeRelativePath(view.EvidencePath), StringComparer.Ordinal).ToArray();
    }

    public RecoveryCleanupDecision EvaluateCleanup(B2RecoveryMaintenanceProjection view, IReadOnlySet<string> referencedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(referencedRelativePaths);
        string path = view.EvidencePath;
        if (!view.IsPathSafe) return Protected(path, MaintenanceProtectionReason.Unsafe, "Recovery evidence path is unsafe.");
        if (view.IsQuarantined || view.IsAmbiguous) return Protected(path, view.IsQuarantined ? MaintenanceProtectionReason.UnresolvedRecovery : MaintenanceProtectionReason.Ambiguous, null);
        if (referencedRelativePaths.Contains(path)) return Protected(path, MaintenanceProtectionReason.Referenced, null);
        if (!view.IsTerminalAndVerified) return Protected(path, MaintenanceProtectionReason.UnresolvedRecovery, null);
        return new RecoveryCleanupDecision(true, false, null, path, null);
    }

    private static B2RecoveryMaintenanceProjection Blocked(RecoveryBlock block, string scope)
    {
        if (!string.Equals(block.Scope, scope, StringComparison.Ordinal))
            throw new ArgumentException("Recovery block scope does not match the requested scope.", nameof(scope));
        RecoveryOperation operation = block.Operation ?? RecoveryOperation.Restore;
        bool pathSafe = IsSafeEvidencePath(block.EvidencePath);
        return new B2RecoveryMaintenanceProjection(operation, scope, pathSafe ? SafeRelativePath(block.EvidencePath) : "recovery/unsafe", "unknown", block.OperationId,
            RecoveryLifecycleState.RecoveryRequired, DateTimeOffset.MinValue, DateTimeOffset.MinValue,
            false, false, false, block.Reason == RecoveryBlockReason.Ambiguous, pathSafe);
    }

    private static bool IsVerified(RecoveryCandidate candidate) => candidate.Envelope.TypedBody switch
    {
        AppendRecoveryBody append => append.NewArtifact is not null && append.NewSidecar is not null,
        RestoreRecoveryBody restore => restore.Entries.Count > 0 && restore.Entries.All(entry => !string.IsNullOrWhiteSpace(entry.BodyFile) && !string.IsNullOrWhiteSpace(entry.SidecarFile)),
        FileEntityRecoveryBody entity => entity.CommittedBody is not null || entity.CommittedMeta is not null || candidate.Envelope.State == RecoveryLifecycleState.RolledBack,
        _ => false
    };

    internal static string SafeRelativePath(string evidencePath)
    {
        if (!Path.IsPathRooted(evidencePath))
            return MaintenanceContractValidation.RequireRelativePath(evidencePath, nameof(evidencePath));
        if (!IsSafeEvidencePath(evidencePath)) return "recovery/unsafe";
        string file = Path.GetFileName(evidencePath);
        string? parent = Path.GetFileName(Path.GetDirectoryName(evidencePath));
        return parent is "versions" or "skills" or "agents" ? $"{parent}/{file}" : file;
    }

    internal static bool IsSafeEvidencePath(string path) => Path.IsPathRooted(path) &&
        !path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.None)
            .Any(segment => segment is "." or "..");

    private static RecoveryCleanupDecision Protected(string path, MaintenanceProtectionReason reason, string? warning) =>
        new(false, true, reason, path, warning);

    private static void RequireScope(string scope) =>
        MaintenanceContractValidation.RequireSafeText(scope, nameof(scope));
}

internal sealed class B3CoordinatorMaintenanceAdapter : IB3CoordinatorMaintenanceAdapter
{
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
        if (!projection.IsPathSafe) return new(false, true, MaintenanceProtectionReason.Unsafe, path, null);
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
        bool safe = IsSafe(state.EvidenceReference) && IsSafe(state.EvidenceKind) && IsSafe(state.EvidenceVersion);
        bool terminal = state.LifecycleState is TransactionalParticipantLifecycleState.Committed or TransactionalParticipantLifecycleState.RolledBack;
        terminal &= state.QuarantineState == TransactionalQuarantineState.None && !state.Ambiguous && !state.StateMayHaveChanged && safe;
        return new B4ParticipantMaintenanceProjection(state.TransactionId, state.OperationId, state.Scope, state.Address,
            state.ParticipantIdentity, state.OrderingKey, state.Kind, state.LifecycleState, state.QuarantineState,
            state.EvidenceReference, state.EvidenceKind, state.EvidenceVersion, state.StateMayHaveChanged,
            state.Ambiguous, terminal, safe);
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
        const string path = "transactional-participant";
        if (!projection.IsPathSafe) return new(false, true, MaintenanceProtectionReason.Unsafe, path, null);
        if (projection.QuarantineState == TransactionalQuarantineState.Quarantined) return new(false, true, MaintenanceProtectionReason.UnresolvedRecovery, path, null);
        if (projection.Ambiguous || projection.StateMayHaveChanged) return new(false, true, MaintenanceProtectionReason.Ambiguous, path, null);
        if (!projection.IsTerminalAndVerified) return new(false, true, MaintenanceProtectionReason.UnresolvedRecovery, path, null);
        if (referencedRelativePaths.Contains(path)) return new(false, true, MaintenanceProtectionReason.Referenced, path, null);
        return new(true, false, null, path, null);
    }

    private static bool IsSafe(string? value) => value is null || B4Validation.SafeReference(value);
}
