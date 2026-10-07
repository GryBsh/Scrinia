namespace Scrinia.Core;

/// <summary>Explicit ownership of a workspace snapshot or its live cross-process lock.</summary>
internal sealed class WorkspaceLease : IDisposable
{
    private FileLock? _held;
    private int _disposed;
    private readonly Dictionary<string, string> _verifiedScopeSnapshots = new(StringComparer.Ordinal);

    internal WorkspaceLease(string workspaceRoot, FileLock? held, bool isExclusive,
        bool capturedAbsentWorkspace = false, bool recoveryPerformed = false)
    {
        WorkspaceRoot = workspaceRoot;
        _held = held;
        IsExclusive = isExclusive;
        CapturedAbsentWorkspace = capturedAbsentWorkspace;
        RecoveryPerformed = recoveryPerformed;
    }

    internal string WorkspaceRoot { get; }
    internal bool IsExclusive { get; }

    /// <summary>
    /// When true, persistent reads must return the captured empty snapshot instead of inspecting
    /// live paths, even if another operation subsequently creates the workspace directory.
    /// </summary>
    internal bool CapturedAbsentWorkspace { get; }
    internal bool RecoveryPerformed { get; }

    internal void SetVerifiedScopeSnapshot(string scope, string fingerprint) => _verifiedScopeSnapshots[scope] = fingerprint;

    internal bool MatchesVerifiedScopeSnapshot(string scope, Func<string> fingerprint) =>
        _verifiedScopeSnapshots.TryGetValue(scope, out string? verified) && verified == fingerprint();

    internal void AssertValid(string workspaceRoot, bool requireExclusive = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!string.Equals(WorkspaceRoot, WorkspaceAdmission.CanonicalRoot(workspaceRoot),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The workspace lease belongs to another workspace.");
        if (requireExclusive && !IsExclusive)
            throw new InvalidOperationException("An exclusive workspace lease is required.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Interlocked.Exchange(ref _held, null)?.Dispose();
    }
}
