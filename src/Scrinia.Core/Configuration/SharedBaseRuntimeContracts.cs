using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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
        RefreshSnapshot();
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

    public SharedMutationPreflight RevalidateBeforeMutation(string? canonicalPath, string? canonicalDestination)
    {
        SharedBaseSnapshot snapshot = Snapshot;
        if (snapshot.Availability is not SharedBaseAvailability.Available)
            return Unsafe(snapshot, canonicalPath, canonicalDestination, "SHARED_BASE_STALE");
        if (!AreTargetsContained(canonicalPath, canonicalDestination))
            return Unsafe(snapshot, canonicalPath, canonicalDestination, "SHARED_BASE_UNSAFE_PATH");

        string currentFingerprint = Fingerprint(_identity.RegisteredRoot);
        return string.Equals(currentFingerprint, snapshot.Fingerprint, StringComparison.Ordinal)
            ? new() { Identity = _identity, Snapshot = snapshot, CanonicalPath = canonicalPath, CanonicalDestination = canonicalDestination, IsSafe = true }
            : Unsafe(snapshot, canonicalPath, canonicalDestination, "SHARED_BASE_STALE");
    }

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

    public SharedMutationCommit CommitMutation(SharedMutationPlan plan, Action durableMutation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(durableMutation);
        SharedMutationPreflight preflight = RevalidateBeforeMutation(plan.CanonicalPath, plan.CanonicalDestination);
        if (!preflight.IsSafe)
            return new() { Identity = _identity, Mutation = SharedMutationCertainty.None, Generation = Snapshot.Generation, Manifest = [] };

        try
        {
            durableMutation();
        }
        catch
        {
            Publish(Snapshot with { Availability = SharedBaseAvailability.Stale });
            return new() { Identity = _identity, Mutation = SharedMutationCertainty.Unknown, Generation = Snapshot.Generation, Manifest = plan.AuthoritativeFiles };
        }

        RefreshSnapshot();
        return new() { Identity = _identity, Mutation = SharedMutationCertainty.Applied, Generation = Snapshot.Generation, Manifest = plan.AuthoritativeFiles };
    }

    public SharedRefreshCommit Refresh()
    {
        string? previousGeneration = Snapshot.Generation;
        RefreshSnapshot();
        return new() { Identity = _identity, PreviousGeneration = previousGeneration, Snapshot = Snapshot };
    }

    public static string Fingerprint(string registeredRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(registeredRoot);
        string inventoryRoot = Path.Combine(registeredRoot, ".scrinia");
        if (!Directory.Exists(inventoryRoot))
            throw new DirectoryNotFoundException(inventoryRoot);

        var files = Directory.EnumerateFiles(inventoryRoot, "*", SearchOption.AllDirectories)
            .Where(path => !SharedClassifier.IsExcludedFromFingerprint(Path.GetRelativePath(inventoryRoot, path)))
            .Select(path => (Path: Path.GetRelativePath(inventoryRoot, path).Replace('\\', '/'), FullPath: path))
            .OrderBy(item => System.Text.Encoding.UTF8.GetBytes(item.Path), ByteArrayComparer.Instance)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach ((string relativePath, string fullPath) in files)
        {
            byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(relativePath);
            BinaryPrimitives.WriteInt64BigEndian(length, pathBytes.Length);
            hash.AppendData(length);
            hash.AppendData(pathBytes);
            byte[] content = File.ReadAllBytes(fullPath);
            BinaryPrimitives.WriteInt64BigEndian(length, content.Length);
            hash.AppendData(length);
            hash.AppendData(content);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void RefreshSnapshot()
    {
        try
        {
            string fingerprint = Fingerprint(_identity.RegisteredRoot);
            string[] files = Directory.EnumerateFiles(Path.Combine(_identity.RegisteredRoot, ".scrinia"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Path.Combine(_identity.RegisteredRoot, ".scrinia"), path).Replace('\\', '/'))
                .Where(path => !SharedClassifier.IsExcludedFromFingerprint(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Publish(new SharedBaseSnapshot { Identity = _identity, Generation = fingerprint, Fingerprint = fingerprint, Availability = SharedBaseAvailability.Available, AuthoritativeFiles = files });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Publish(new SharedBaseSnapshot { Identity = _identity, Availability = SharedBaseAvailability.Unavailable, Warnings = [exception.GetType().Name] });
        }
    }

    private bool AreTargetsContained(string? canonicalPath, string? canonicalDestination) =>
        AreTargetContained(canonicalPath) && AreTargetContained(canonicalDestination);

    private bool AreTargetContained(string? target)
    {
        if (target is null || !Path.IsPathFullyQualified(target))
            return true;
        return PathContainment.IsWithin(_identity.RegisteredRoot, target);
    }

    private SharedMutationPreflight Unsafe(SharedBaseSnapshot snapshot, string? path, string? destination, string code) =>
        new() { Identity = _identity, Snapshot = snapshot, CanonicalPath = path, CanonicalDestination = destination, IsSafe = false, ErrorCode = code };

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            int length = Math.Min(left.Length, right.Length);
            for (int index = 0; index < length; index++)
            {
                int result = left[index].CompareTo(right[index]);
                if (result != 0) return result;
            }
            return left.Length.CompareTo(right.Length);
        }
    }

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
