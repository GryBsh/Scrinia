namespace Scrinia.Core;

/// <summary>Discovers recovery evidence from one store scope without widening the recovery boundary.</summary>
internal sealed class RecoveryDiscoveryCoordinator
{
    private const string RestoreFileName = ".restore-journal.json";
    private readonly Dictionary<RecoveryOperation, IRecoveryAdapter> _adapters;

    internal RecoveryDiscoveryCoordinator()
        : this(new RestoreRecoveryAdapter(), new AppendRecoveryAdapter(), new FileEntityRecoveryAdapter())
    {
    }

    internal RecoveryDiscoveryCoordinator(params IRecoveryAdapter[] adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToDictionary(adapter => adapter.Operation);
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
            foreach (string evidencePath in Directory.EnumerateFiles(entityDirectory, ".file-entity-*.recovery", SearchOption.TopDirectoryOnly)
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
        try
        {
            byte[] bytes = ReadBounded(evidencePath);
            RecoveryEnvelopeDocument envelope = adapter.ParseAndNormalize(bytes, scope, evidencePath, evidenceDirectory);
            string subject = SubjectOf(envelope);
            envelope = adapter.ValidateForMutation(envelope, scope, evidencePath, subject, evidenceDirectory);
            candidates.Add(new RecoveryCandidate(operation, evidencePath, scope, subject,
                envelope.OperationId, envelope, envelope.IsLegacy));
            return null;
        }
        catch (RecoveryEnvelopeException exception)
        {
            return new RecoveryBlock(scope, evidencePath, operation, TryOperationId(evidencePath),
                exception.Failure == RecoveryEnvelopeFailure.UnsupportedVersion
                    ? RecoveryBlockReason.UnsupportedVersion
                    : exception.Failure == RecoveryEnvelopeFailure.WrongScope
                        ? RecoveryBlockReason.Unsafe : RecoveryBlockReason.Malformed,
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new RecoveryBlock(scope, evidencePath, operation, TryOperationId(evidencePath),
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

    private static IEnumerable<string> EnumerateEntityDirectories(string root)
    {
        if (!Directory.Exists(root))
            yield break;
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
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

    private static string TryOperationId(string evidencePath) =>
        Path.GetFileName(evidencePath) switch
        {
            AppendJournal.FileName or RestoreFileName => "unknown",
            var name when name.StartsWith(".file-entity-", StringComparison.Ordinal) =>
                name[".file-entity-".Length..^".recovery".Length],
            _ => "unknown"
        };
}
