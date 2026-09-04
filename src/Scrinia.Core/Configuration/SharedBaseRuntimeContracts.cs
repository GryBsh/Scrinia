using System.Collections.Concurrent;
using Scrinia.Core;

namespace Scrinia.Core.Configuration;

public enum SharedBaseAvailability
{
    Available,
    Unavailable,
    Stale,
    Conflicted,
}

public enum SharedMutationCertainty
{
    Applied,
    None,
    Unknown,
}

public sealed record SharedBaseIdentity
{
    public required string BaseName { get; init; }
    public required string RegisteredRoot { get; init; }

    public static SharedBaseIdentity From(SharedBaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!SharedBaseRegistry.IsCanonicalName(entry.Name))
            throw new ArgumentException("The shared base entry must have a canonical name.", nameof(entry));
        if (!Path.IsPathFullyQualified(entry.Root) || !Directory.Exists(entry.Root))
            throw new ArgumentException("The shared base root must be an existing absolute directory.", nameof(entry));

        string canonicalRoot = PathContainment.ResolveCanonical(entry.Root);
        if (string.Equals(Path.GetFileName(canonicalRoot), ".scrinia", StringComparison.OrdinalIgnoreCase) ||
            !PathContainment.IsWithin(canonicalRoot, Path.Combine(canonicalRoot, ".scrinia")))
            throw new ArgumentException("The shared base root is unsafe.", nameof(entry));

        return new SharedBaseIdentity
        {
            BaseName = entry.Name,
            RegisteredRoot = canonicalRoot,
        };
    }
}

public sealed record SharedBaseSnapshot
{
    public required SharedBaseIdentity Identity { get; init; }
    public string? Generation { get; init; }
    public string? Fingerprint { get; init; }
    public SharedBaseAvailability Availability { get; init; } = SharedBaseAvailability.Unavailable;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> AuthoritativeFiles { get; init; } = [];
}

public interface ISharedBaseRuntimeFactory
{
    SharedBaseRuntime Open(SharedBaseEntry entry);
}

public interface ISharedBaseRuntime
{
    string BaseName { get; }
    string RegisteredRoot { get; }
    SharedBaseSnapshot Snapshot { get; }
    SharedBaseOperationLease AcquireReadLease();
    SharedBaseMutationLease AcquireMutationLease();
    SharedBaseRefreshLease AcquireRefreshLease();
    SharedMutationPreflight RevalidateBeforeMutation(string? canonicalPath, string? canonicalDestination);
    SharedMutationCommit CommitMutation(SharedMutationPlan plan);
    SharedRefreshCommit Refresh();
}

public sealed class SharedBaseRuntimeFactory : ISharedBaseRuntimeFactory
{
    private readonly ConcurrentDictionary<SharedBaseIdentity, SharedBaseRuntime> _runtimes = new(SharedBaseIdentityComparer.Instance);

    public SharedBaseRuntime Open(SharedBaseEntry entry)
    {
        SharedBaseIdentity identity = SharedBaseIdentity.From(entry);
        return _runtimes.GetOrAdd(identity, static key => new SharedBaseRuntime(key));
    }
}

internal sealed class SharedBaseIdentityComparer : IEqualityComparer<SharedBaseIdentity>
{
    public static SharedBaseIdentityComparer Instance { get; } = new();

    public bool Equals(SharedBaseIdentity? left, SharedBaseIdentity? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null &&
        string.Equals(left.BaseName, right.BaseName, StringComparison.Ordinal) &&
        string.Equals(left.RegisteredRoot, right.RegisteredRoot, PathIdentityComparison);

    public int GetHashCode(SharedBaseIdentity identity) =>
        HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(identity.BaseName),
            PathComparer.GetHashCode(identity.RegisteredRoot));

    private static StringComparison PathIdentityComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public sealed class SharedBaseRuntime : ISharedBaseRuntime, IDisposable
{
    private readonly ReaderWriterLockSlim _generationGate = new(LockRecursionPolicy.NoRecursion);
    private readonly SharedBaseIdentity _identity;
    private SharedBaseSnapshot _snapshot;

    internal SharedBaseRuntime(SharedBaseIdentity identity)
    {
        _identity = identity;
        _snapshot = new SharedBaseSnapshot { Identity = identity };
    }

    public string BaseName => _identity.BaseName;
    public string RegisteredRoot => _identity.RegisteredRoot;
    public SharedBaseSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public SharedBaseOperationLease AcquireReadLease()
    {
        _generationGate.EnterReadLock();
        return new SharedBaseOperationLease(() => _generationGate.ExitReadLock());
    }

    public SharedBaseMutationLease AcquireMutationLease()
    {
        _generationGate.EnterWriteLock();
        return new SharedBaseMutationLease(() => _generationGate.ExitWriteLock());
    }

    public SharedBaseRefreshLease AcquireRefreshLease()
    {
        _generationGate.EnterWriteLock();
        return new SharedBaseRefreshLease(() => _generationGate.ExitWriteLock());
    }

    public SharedMutationPreflight RevalidateBeforeMutation(string? canonicalPath, string? canonicalDestination) =>
        new() { Identity = _identity, Snapshot = Snapshot, CanonicalPath = canonicalPath, CanonicalDestination = canonicalDestination, IsSafe = true };

    public SharedMutationCommit CommitMutation(SharedMutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new SharedMutationCommit
        {
            Identity = _identity,
            Mutation = SharedMutationCertainty.None,
            Generation = Snapshot.Generation,
            Manifest = [],
        };
    }

    public SharedRefreshCommit Refresh() =>
        new() { Identity = _identity, PreviousGeneration = Snapshot.Generation, Snapshot = Snapshot };

    internal void Publish(SharedBaseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Identity != _identity)
            throw new ArgumentException("The snapshot belongs to a different shared base.", nameof(snapshot));
        Volatile.Write(ref _snapshot, snapshot);
    }

    public void Dispose() => _generationGate.Dispose();
}

public abstract class SharedBaseLease : IDisposable
{
    private Action? _release;

    protected SharedBaseLease(Action release) => _release = release ?? throw new ArgumentNullException(nameof(release));

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        GC.SuppressFinalize(this);
    }
}

public sealed class SharedBaseOperationLease : SharedBaseLease
{
    internal SharedBaseOperationLease(Action release) : base(release) { }
}

public sealed class SharedBaseMutationLease : SharedBaseLease
{
    internal SharedBaseMutationLease(Action release) : base(release) { }
}

public sealed class SharedBaseRefreshLease : SharedBaseLease
{
    internal SharedBaseRefreshLease(Action release) : base(release) { }
}

public sealed record SharedMutationPreflight
{
    public required SharedBaseIdentity Identity { get; init; }
    public required SharedBaseSnapshot Snapshot { get; init; }
    public string? CanonicalPath { get; init; }
    public string? CanonicalDestination { get; init; }
    public bool IsSafe { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record SharedMutationPlan
{
    public required SharedBaseIdentity Identity { get; init; }
    public string? CanonicalPath { get; init; }
    public string? CanonicalDestination { get; init; }
    public IReadOnlyList<string> AuthoritativeFiles { get; init; } = [];
}

public sealed record SharedMutationCommit
{
    public required SharedBaseIdentity Identity { get; init; }
    public SharedMutationCertainty Mutation { get; init; }
    public string? Generation { get; init; }
    public IReadOnlyList<string> Manifest { get; init; } = [];
}

public sealed record SharedRefreshCommit
{
    public required SharedBaseIdentity Identity { get; init; }
    public string? PreviousGeneration { get; init; }
    public required SharedBaseSnapshot Snapshot { get; init; }
}
