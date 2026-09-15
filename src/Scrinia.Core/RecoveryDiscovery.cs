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
        string root = PathContainment.ResolveCanonical(storeDirectory);
        RejectReparsePoint(root);
        RecoveryDiscoveryResult coordinatorDiscovery = InspectCoordinator(root, scope) is { } coordinatorResult
            ? coordinatorResult
            : new RecoveryDiscoveryResult([] , null);
        if (coordinatorDiscovery.CoordinatorRecord is not null)
            return new RecoveryMaintenanceDiscoveryPage([], new B3CoordinatorMaintenanceAdapter().Project(
                coordinatorDiscovery.CoordinatorRecord, scope), null, false);
        if (coordinatorDiscovery.BlockingEvidence is not null &&
            Path.GetFileName(coordinatorDiscovery.BlockingEvidence.EvidencePath) == CoordinatorFileName)
            return new RecoveryMaintenanceDiscoveryPage([], B3CoordinatorMaintenanceAdapter.Blocked(
                coordinatorDiscovery.BlockingEvidence, scope), null, false);

        var projected = new List<B2RecoveryMaintenanceProjection>(Math.Min(candidateBudget, 64));
        string? last = cursor.AfterRelativePath;
        bool more = false;
        bool StopEnumeration() => DateTimeOffset.UtcNow - started >= deadline;
        foreach ((RecoveryOperation Operation, string Path, string Directory) candidate in EnumerateMaintenanceEvidence(
            root, StopEnumeration, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow - started >= deadline)
            {
                more = true;
                break;
            }

            string relative = B2RecoveryMaintenanceAdapter.SafeRelativePath(candidate.Path, root);
            if (cursor.AfterRelativePath is not null && string.CompareOrdinal(relative, cursor.AfterRelativePath) <= 0)
                continue;
            B2RecoveryMaintenanceProjection view = InspectMaintenanceCandidate(candidate.Operation,
                candidate.Path, candidate.Directory, scope, root);
            projected.Add(view);
            last = relative;
            if (projected.Count == candidateBudget)
            {
                more = true;
                break;
            }
        }
        return new RecoveryMaintenanceDiscoveryPage(projected, null,
            more ? new MaintenanceDiscoveryCursor(last) : null, more);
    }

    private IEnumerable<(RecoveryOperation Operation, string Path, string Directory)> EnumerateMaintenanceEvidence(
        string root, Func<bool> shouldStop, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shouldStop()) yield break;
        string restore = Path.Combine(root, RestoreFileName);
        if (File.Exists(restore)) yield return (RecoveryOperation.Restore, restore, root);
        cancellationToken.ThrowIfCancellationRequested();
        if (shouldStop()) yield break;
        string appendDirectory = Path.Combine(root, "versions");
        if (Directory.Exists(appendDirectory)) RejectReparsePoint(appendDirectory);
        string append = Path.Combine(appendDirectory, AppendJournal.FileName);
        if (File.Exists(append)) yield return (RecoveryOperation.Append, append, root);
        foreach (string directoryName in FileEntityDirectoryNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shouldStop()) yield break;
            string directory = Path.Combine(root, directoryName);
            if (!Directory.Exists(directory)) continue;
            RejectReparsePoint(directory);
            using IEnumerator<string> paths = _evidenceEnumerator(directory).GetEnumerator();
            while (!shouldStop() && paths.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return (RecoveryOperation.FileEntity, paths.Current, directory);
            }
        }
    }

    private B2RecoveryMaintenanceProjection InspectMaintenanceCandidate(RecoveryOperation operation,
        string evidencePath, string evidenceDirectory, string scope, string root)
    {
        var candidates = new List<RecoveryCandidate>(1);
        RecoveryBlock? block = InspectCandidate(operation, evidencePath, evidenceDirectory, scope, candidates);
        if (block is not null)
            return new B2RecoveryMaintenanceAdapter(root).Project(new RecoveryDiscoveryResult([], block), scope)[0];
        RecoveryCandidate candidate = candidates.Single();
        return new B2RecoveryMaintenanceAdapter(root).Project(new RecoveryDiscoveryResult([candidate], null), scope)[0]
            with { IsTerminalAndVerified = candidate.Envelope.State is RecoveryLifecycleState.Committed or RecoveryLifecycleState.RolledBack &&
                B2RecoveryMaintenanceAdapter.VerifyTerminal(candidate, root) };
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

            bool terminal = candidate.Envelope.State is RecoveryLifecycleState.Committed or RecoveryLifecycleState.RolledBack;
            string evidenceRoot = _evidenceRoot ?? EvidenceRoot(candidate);
            bool pathSafe = IsSafeEvidencePath(candidate.EvidencePath, evidenceRoot);
            return new B2RecoveryMaintenanceProjection(candidate.Operation, candidate.Scope,
                pathSafe ? SafeRelativePath(candidate.EvidencePath, evidenceRoot) : "recovery/unsafe",
                candidate.CanonicalSubject, candidate.OperationId, candidate.Envelope.State,
                candidate.Envelope.CreatedAt, candidate.Envelope.UpdatedAt, terminal && pathSafe && VerifyTerminal(candidate, evidenceRoot),
                false, false, false, pathSafe);
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

    internal static bool VerifyTerminal(RecoveryCandidate candidate, string evidenceRoot)
    {
        try
        {
            if (!IsSafeEvidencePath(candidate.EvidencePath, evidenceRoot)) return false;
            return candidate.Envelope.TypedBody switch
            {
                AppendRecoveryBody append => VerifyAppend(append, evidenceRoot, candidate.Envelope.State),
                RestoreRecoveryBody restore => VerifyRestore(restore, candidate.Envelope.Scope, candidate.EvidencePath, evidenceRoot, candidate.Envelope.State),
                FileEntityRecoveryBody entity => VerifyEntity(entity, evidenceRoot, candidate.Envelope.State),
                _ => false
            };
        }
        catch (Exception) { return false; }
    }

    private static bool VerifyAppend(AppendRecoveryBody body, string root, RecoveryLifecycleState state)
    {
        if (state == RecoveryLifecycleState.RolledBack)
            return VerifyMember(body.OldArtifact, root) && VerifyMember(body.OldSidecar, root);
        return VerifyMember(body.NewArtifact, root) && VerifyMember(body.NewSidecar, root);
    }

    private static bool VerifyRestore(RestoreRecoveryBody body, string scope, string evidencePath, string root, RecoveryLifecycleState state)
    {
        if (state != RecoveryLifecycleState.Committed) return false;
        foreach (RestoreJournalEntry entry in body.Entries)
        {
            (byte[] bodyBytes, byte[] sidecarBytes) = RestoreJournalCodec.DecodeAndVerifyPayloads(entry, scope, evidencePath);
            if (!VerifyBytes(Path.Combine(root, entry.BodyFile), bodyBytes, root) ||
                !VerifyBytes(Path.Combine(root, entry.SidecarFile), sidecarBytes, root)) return false;
        }
        return body.Entries.Count > 0;
    }

    private static bool VerifyEntity(FileEntityRecoveryBody body, string root, RecoveryLifecycleState state)
    {
        if (state == RecoveryLifecycleState.RolledBack)
            return VerifySnapshot(body.OldBody, root) && VerifySnapshot(body.OldMeta, root);
        return body.CommittedBody is not null && body.CommittedMeta is not null &&
            VerifySnapshot(body.CommittedBody, root) && VerifySnapshot(body.CommittedMeta, root);
    }

    private static bool VerifyMember(AppendJournalMember member, string root) =>
        VerifyBytes(Path.Combine(root, member.Path), member.ToSnapshot(member.Path).Bytes, root);

    private static bool VerifySnapshot(RecoveryMemberSnapshot snapshot, string root) =>
        snapshot.Existed && snapshot.Payload is not null && VerifyBytes(snapshot.Path, Convert.FromBase64String(snapshot.Payload), root);

    private static bool VerifyBytes(string path, byte[] expected, string root) =>
        PathContainment.IsWithin(root, path) && File.Exists(path) &&
        File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

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
        string path = projection.EvidenceReference is not null
            ? projection.EvidenceReference
            : $"transactional/{projection.ParticipantIdentity}";
        if (!projection.IsPathSafe) return new(false, true, MaintenanceProtectionReason.Unsafe, path, null);
        if (projection.QuarantineState == TransactionalQuarantineState.Quarantined) return new(false, true, MaintenanceProtectionReason.UnresolvedRecovery, path, null);
        if (projection.Ambiguous || projection.StateMayHaveChanged) return new(false, true, MaintenanceProtectionReason.Ambiguous, path, null);
        if (!projection.IsTerminalAndVerified) return new(false, true, MaintenanceProtectionReason.UnresolvedRecovery, path, null);
        if (referencedRelativePaths.Contains(path)) return new(false, true, MaintenanceProtectionReason.Referenced, path, null);
        return new(true, false, null, path, null);
    }

    private static bool IsSafe(string? value) => value is null || B4Validation.SafeReference(value);
}
