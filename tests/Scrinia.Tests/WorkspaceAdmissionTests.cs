using System.Diagnostics;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class WorkspaceAdmissionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    [Fact]
    public void RestoreBlocksFirstMutation()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, recover: null);
        using var held = admission.EnterRestore(Timeout, default);
        Action acquire = () =>
        {
            using var writer = admission.EnterMutation(TimeSpan.FromMilliseconds(100), default);
        };
        acquire.Should().Throw<FileLockTimeoutException>();
    }

    [Fact]
    public void SharedMutationsBlockRestore()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, recover: null);
        using var first = admission.EnterMutation(Timeout, default);
        using var second = admission.EnterMutation(Timeout, default);
        first.IsExclusive.Should().BeFalse();
        second.IsExclusive.Should().BeFalse();
        Action restore = () =>
        {
            using var held = admission.EnterRestore(TimeSpan.FromMilliseconds(100), default);
        };
        restore.Should().Throw<FileLockTimeoutException>();
    }

    [Fact]
    public void AbsentReadCapturesEmpty()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, recover: null);
        using var reader = admission.EnterRead(Timeout, default);
        reader.CapturedAbsentWorkspace.Should().BeTrue();
        reader.IsExclusive.Should().BeFalse();
        reader.WorkspaceRoot.Should().Be(Path.GetFullPath(scope.WorkspaceDir));
        Directory.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia")).Should().BeFalse();
        using var writer = admission.EnterMutation(Timeout, default);
        reader.CapturedAbsentWorkspace.Should().BeTrue("an absent read remains an empty snapshot after first write");
        writer.CapturedAbsentWorkspace.Should().BeFalse();
    }

    [Fact]
    public void ExistingReadBlocksRestore()
    {
        using var scope = new TestHelpers.StoreScope();
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, recover: null);
        using var reader = admission.EnterRead(Timeout, default);
        reader.CapturedAbsentWorkspace.Should().BeFalse();
        Action restore = () =>
        {
            using var held = admission.EnterRestore(TimeSpan.FromMilliseconds(100), default);
        };
        restore.Should().Throw<FileLockTimeoutException>();
    }

    [Fact]
    public void CancelledSharedWaitReleasesOwnership()
    {
        using var scope = new TestHelpers.StoreScope();
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, recover: null);
        using (var held = admission.EnterRestore(Timeout, default))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            Action acquire = () =>
            {
                using var writer = admission.EnterMutation(Timeout, cancellation.Token);
            };
            acquire.Should().Throw<OperationCanceledException>();
        }
        using var released = admission.EnterRestore(Timeout, default);
    }

    [Fact]
    public void DisposedOrForeignLeaseIsRejected()
    {
        using var scope = new TestHelpers.StoreScope();
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, recover: null);
        var lease = admission.EnterMutation(Timeout, default);
        Action valid = () => lease.AssertValid(Path.Combine(scope.WorkspaceDir, "."));
        valid.Should().NotThrow();
        Action foreign = () => lease.AssertValid(Path.Combine(scope.WorkspaceDir, "other"));
        foreign.Should().Throw<InvalidOperationException>();
        Action insufficient = () => lease.AssertValid(scope.WorkspaceDir, requireExclusive: true);
        insufficient.Should().Throw<InvalidOperationException>();
        lease.Dispose();
        lease.Dispose();
        Action disposed = () => lease.AssertValid(scope.WorkspaceDir);
        disposed.Should().Throw<ObjectDisposedException>();
        using var released = admission.EnterRestore(Timeout, default);
    }

    [Fact]
    public void FileAtWorkspaceDirectoryDoesNotCaptureAbsence()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);
        Directory.CreateDirectory(scope.WorkspaceDir);
        File.WriteAllText(Path.Combine(scope.WorkspaceDir, ".scrinia"), "blocked");
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, recover: null);
        Action read = () => { using var held = admission.EnterRead(Timeout, default); };
        read.Should().Throw<IOException>();
    }

    [Fact]
    public void JournalRecoveryRunsExclusivelyAndRetriesSharedAdmission()
    {
        using var scope = new TestHelpers.StoreScope();
        string journal = JournalPath(scope.WorkspaceDir);
        File.WriteAllText(journal, "evidence");
        WorkspaceLease? recoveryLease = null;
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, (lease, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            lease.AssertValid(scope.WorkspaceDir, requireExclusive: true);
            recoveryLease = lease;
            File.Delete(journal);
        });
        using var admitted = admission.EnterMutation(Timeout, default);
        admitted.RecoveryPerformed.Should().BeTrue();
        admitted.IsExclusive.Should().BeFalse();
        recoveryLease.Should().NotBeNull();
        Action useRecoveryLease = () => recoveryLease!.AssertValid(scope.WorkspaceDir);
        useRecoveryLease.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void JournalWithoutRecoveryAdapterBlocksAdmission()
    {
        using var scope = new TestHelpers.StoreScope();
        string journal = JournalPath(scope.WorkspaceDir);
        File.WriteAllText(journal, "evidence");
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, recover: null);
        Action acquire = () => { using var held = admission.EnterRead(Timeout, default); };
        var error = acquire.Should().Throw<BundleImportException>().Which;
        error.Failure.Should().Be(BundleImportFailure.RecoveryRequired);
        error.JournalPath.Should().Be(journal);
        File.ReadAllText(journal).Should().Be("evidence");
        using var released = admission.EnterRestore(Timeout, default);
    }

    [Fact]
    public void RecoveryFailureReleasesExclusiveOwnership()
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(JournalPath(scope.WorkspaceDir), "evidence");
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, (_, _) => throw new InvalidDataException("bad journal"));
        Action acquire = () => { using var held = admission.EnterMutation(Timeout, default); };
        acquire.Should().Throw<InvalidDataException>().WithMessage("bad journal");
        using var released = admission.EnterRestore(Timeout, default);
    }

    [Fact]
    public void RecoveryAndRetriesShareOneDeadline()
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(JournalPath(scope.WorkspaceDir), "evidence");
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, (_, ct) =>
        {
            ct.WaitHandle.WaitOne();
            ct.ThrowIfCancellationRequested();
        });
        var elapsed = Stopwatch.StartNew();
        Action acquire = () => { using var held = admission.EnterMutation(TimeSpan.FromMilliseconds(100), default); };
        acquire.Should().Throw<FileLockTimeoutException>();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        using var released = admission.EnterRestore(Timeout, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkspaceAliasUsesCanonicalLeaseIdentity(bool absent)
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: !absent);
        Directory.CreateDirectory(scope.WorkspaceDir);
        string alias = scope.WorkspaceDir + "-alias";
        Directory.CreateSymbolicLink(alias, scope.WorkspaceDir);
        try
        {
            using var lease = new WorkspaceAdmission(alias, null).EnterRead(Timeout, default);
            lease.AssertValid(scope.WorkspaceDir);
            lease.AssertValid(alias);
            lease.CapturedAbsentWorkspace.Should().Be(absent);
            Action foreign = () => lease.AssertValid(scope.WorkspaceDir + "-other");
            foreign.Should().Throw<InvalidOperationException>();
            if (absent)
            {
                Directory.CreateDirectory(Path.Combine(scope.WorkspaceDir, ".scrinia"));
                lease.CapturedAbsentWorkspace.Should().BeTrue();
            }
        }
        finally { Directory.Delete(alias); }
    }

    private static string JournalPath(string root) => Path.Combine(root, ".scrinia", ".bundle-restore-journal.json");

    [Fact]
    public async Task SeparateProcessRestoreExcludesMutation()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var child = await TestProcessRunner.StartAsync(
            ["admission", "hold", scope.WorkspaceDir], deadline.Token);
        await child.ReadEventAsync("ready", deadline.Token);
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, recover: null);
        Action acquire = () =>
        {
            using var writer = admission.EnterMutation(TimeSpan.FromMilliseconds(100), deadline.Token);
        };
        acquire.Should().Throw<FileLockTimeoutException>();
        await child.SendAsync("continue", deadline.Token);
        await child.ReadEventAsync("completed", deadline.Token);
        (await child.WaitForExitAsync(deadline.Token)).Should().Be(0);
        using var released = admission.EnterMutation(Timeout, default);
    }

    [Fact]
    public async Task RejectedControlIncludesStderrAndReleasesOwnership()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var child = await TestProcessRunner.StartAsync(
            ["admission", "hold", scope.WorkspaceDir], deadline.Token);
        await child.ReadEventAsync("ready", deadline.Token);
        await child.SendAsync("invalid", deadline.Token);
        Func<Task> read = () => child.ReadEventAsync("completed", deadline.Token);
        await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*Expected control line 'continue'*invalid*");
        (await child.WaitForExitAsync(deadline.Token)).Should().Be(1);
        using var released = new WorkspaceAdmission(scope.WorkspaceDir, null).EnterRestore(Timeout, default);
    }

    [Fact]
    public async Task UnexpectedEventKillsAndReapsChild()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var child = await TestProcessRunner.StartAsync(
            ["admission", "hold", scope.WorkspaceDir], deadline.Token);
        Func<Task> read = () => child.ReadEventAsync("completed", deadline.Token);
        await read.Should().ThrowAsync<InvalidDataException>().WithMessage("*completed*ready*");
        (await child.WaitForExitAsync(deadline.Token)).Should().NotBe(0);
        using var released = new WorkspaceAdmission(scope.WorkspaceDir, null).EnterRestore(Timeout, default);
    }

    [Fact]
    public async Task CancelledProcessReadKillsAndReapsChild()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var child = await TestProcessRunner.StartAsync(
            ["admission", "hold", scope.WorkspaceDir], deadline.Token);
        await child.ReadEventAsync("ready", deadline.Token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Func<Task> read = () => child.ReadEventAsync("completed", cancellation.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
        (await child.WaitForExitAsync(deadline.Token)).Should().NotBe(0);
        using var released = new WorkspaceAdmission(scope.WorkspaceDir, null).EnterRestore(Timeout, default);
    }

    [Fact]
    public async Task DisposedProcessReleasesRestoreOwnership()
    {
        using var scope = new TestHelpers.StoreScope(createStoreDir: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var child = await TestProcessRunner.StartAsync(["admission", "hold", scope.WorkspaceDir], deadline.Token);
        try { await child.ReadEventAsync("ready", deadline.Token); }
        finally { await child.DisposeAsync(); }
        await child.DisposeAsync();
        using var released = new WorkspaceAdmission(scope.WorkspaceDir, null).EnterRestore(Timeout, default);
    }
}
