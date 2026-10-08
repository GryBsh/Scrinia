using System.Diagnostics;
using Scrinia.Core.Bundles;
using Scrinia.Core.Migration;

namespace Scrinia.Core;

/// <summary>Owns cross-process admission before authoritative workspace state is inspected.</summary>
internal sealed class WorkspaceAdmission
{
    private readonly string _workspaceRoot;
    private readonly string _directory;
    private readonly string _lockPath;
    private readonly string _journalPath;
    private readonly Action<WorkspaceLease, CancellationToken>? _recover;

    internal WorkspaceAdmission(string workspaceRoot, Action<WorkspaceLease, CancellationToken>? recover)
    {
        _workspaceRoot = CanonicalRoot(workspaceRoot);
        _directory = Path.Combine(_workspaceRoot, ".scrinia");
        _lockPath = Path.Combine(_directory, ".bundle-restore.lock");
        _journalPath = Path.Combine(_directory, ".bundle-restore-journal.json");
        _recover = recover;
    }

    internal WorkspaceLease EnterRead(TimeSpan timeout, CancellationToken ct) => Enter(timeout, mutation: false, exclusive: false, ct);
    internal WorkspaceLease EnterMutation(TimeSpan timeout, CancellationToken ct) => Enter(timeout, mutation: true, exclusive: false, ct);
    internal WorkspaceLease EnterRestore(TimeSpan timeout, CancellationToken ct) => Enter(timeout, mutation: true, exclusive: true, ct);

    internal WorkspaceLease EnterTracking(TimeSpan timeout, CancellationToken ct) =>
        Enter(timeout, mutation: true, exclusive: true, ct, tracking: true);

    private WorkspaceLease Enter(TimeSpan timeout, bool mutation, bool exclusive, CancellationToken ct, bool tracking = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            if (mutation)
                Directory.CreateDirectory(_directory);
            else if (!DirectoryPresent())
                return new WorkspaceLease(_workspaceRoot, null, isExclusive: false, capturedAbsentWorkspace: true);

            if (exclusive)
            {
                var lease = new WorkspaceLease(_workspaceRoot,
                    FileLock.AcquireExclusive(_lockPath, Remaining(), deadline.Token), isExclusive: true);
                try
                {
                    if (tracking)
                    {
                        lease.AssertValid(_workspaceRoot, requireExclusive: true);
                        try
                        {
                            if (JournalPresent())
                                throw new TrackingRecoveryRequiredException(
                                    "Bundle recovery evidence blocks tracking changes.", _journalPath);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            throw new TrackingRecoveryRequiredException(
                                "Bundle recovery evidence must be resolved before tracking changes.", _journalPath, exception);
                        }
                    }
                    else ThrowIfTrackingBlocked(lease);
                    return lease;
                }
                catch { lease.Dispose(); throw; }
            }

            bool recovered = false;
            while (true)
            {
                var shared = new WorkspaceLease(_workspaceRoot,
                    FileLock.AcquireShared(_lockPath, Remaining(), deadline.Token),
                    isExclusive: false, recoveryPerformed: recovered);
                try
                {
                    ThrowIfTrackingBlocked(shared);
                    if (!JournalPresent()) return shared;
                }
                catch
                {
                    shared.Dispose();
                    throw;
                }
                shared.Dispose();

                // Never upgrade while retaining shared admission. Recheck evidence after the
                // exclusive acquisition because another process may have recovered it first.
                using (var recoveryLease = new WorkspaceLease(_workspaceRoot,
                    FileLock.AcquireExclusive(_lockPath, Remaining(), deadline.Token), isExclusive: true))
                {
                    ThrowIfTrackingBlocked(recoveryLease);
                    if (JournalPresent())
                    {
                        if (_recover is null)
                            throw new BundleImportException(BundleImportFailure.RecoveryRequired,
                                new InvalidOperationException("No workspace recovery adapter is configured."), _journalPath);
                        _recover(recoveryLease, deadline.Token);
                        recovered = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FileLockTimeoutException(_lockPath, timeout);
        }

        TimeSpan Remaining()
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new FileLockTimeoutException(_lockPath, timeout);
            return remaining;
        }
    }

    private void ThrowIfTrackingBlocked(WorkspaceLease lease)
    {
        lease.AssertValid(_workspaceRoot);
        try { TrackingRecoveryJournal.ThrowIfPresent(_workspaceRoot); }
        catch (TrackingRecoveryRequiredException exception)
        {
            throw new RestoreRecoveryException("local", exception.JournalPath!,
                RestoreRecoveryFailure.ReplayIoFailed, exception.Message, exception);
        }
    }

    private bool DirectoryPresent()
    {
        try
        {
            if ((File.GetAttributes(_directory) & FileAttributes.Directory) == 0)
                throw new IOException("The workspace storage path is not a directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private bool JournalPresent()
    {
        try { _ = File.GetAttributes(_journalPath); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static string CanonicalRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.TrimEndingDirectorySeparator(PathContainment.ResolveCanonical(workspaceRoot));
    }
}
