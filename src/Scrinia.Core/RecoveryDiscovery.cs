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

    internal RecoveryDiscoveryResult Discover(string scope, string storeDirectory)
    {
        try
        {
            return DiscoverCore(scope, storeDirectory);
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

    private RecoveryDiscoveryResult DiscoverCore(string scope, string storeDirectory)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Recovery scope is required.", nameof(scope));
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("Recovery store directory is required.", nameof(storeDirectory));

        string root = PathContainment.ResolveCanonical(storeDirectory);
        RejectReparsePoint(root);
        var candidates = new List<RecoveryCandidate>();

        RecoveryDiscoveryResult? coordinator = InspectCoordinator(root, scope);
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
        if (!File.Exists(path))
            return null;

        try
        {
            RejectReparsePoint(path);
            byte[] bytes = ReadBounded(path);
            MultiMemoryTransactionRecord record = MultiMemoryTransactionRecordCodec.ParseAndValidate(
                bytes, scope, path, root);
            return new RecoveryDiscoveryResult([], null, record);
        }
        catch (MultiMemoryTransactionRecordException exception)
        {
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
            return new RecoveryDiscoveryResult([], new RecoveryBlock(scope, path, null, "unknown",
                exception is ArgumentException ? RecoveryBlockReason.Unsafe : RecoveryBlockReason.ReplayIoFailed,
                "Coordinator record could not safely be inspected."));
        }
    }

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
        const long maximumBytes = 128L * 1024 * 1024;
        if (stream.Length > maximumBytes)
            throw new IOException("Recovery evidence exceeds the maximum document size.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes, 81_920);
        if (bytes.Length > maximumBytes)
            throw new IOException("Recovery evidence exceeds the maximum document size.");
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
