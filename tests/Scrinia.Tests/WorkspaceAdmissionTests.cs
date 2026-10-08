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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryAdmissionModeRetainsUnreadableTrackingEvidence(bool directoryCarrier)
    {
        using var scope = new TestHelpers.StoreScope();
        string path = TrackingAdmissionTests.JournalPath(scope.WorkspaceDir);
        if (directoryCarrier) Directory.CreateDirectory(path);
        else File.WriteAllText(path, "complete\n");
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, (_, _) => throw new InvalidOperationException("must not recover"));
        foreach (var enter in new Func<WorkspaceLease>[]
        {
            () => admission.EnterRead(Timeout, default),
            () => admission.EnterMutation(Timeout, default),
            () => admission.EnterRestore(Timeout, default)
        })
        {
            var error = Assert.Throws<RestoreRecoveryException>(() => { using var lease = enter(); });
            Assert.IsType<Scrinia.Core.Migration.TrackingRecoveryRequiredException>(error.InnerException);
            Assert.Equal(path, error.JournalPath);
        }
        using var tracking = admission.EnterTracking(Timeout, default);
        Assert.True(tracking.IsExclusive);
        tracking.AssertValid(scope.WorkspaceDir, requireExclusive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrackingNeverConsumesUnrelatedBundleEvidence(bool trackingPresent)
    {
        using var scope = new TestHelpers.StoreScope();
        string bundle = JournalPath(scope.WorkspaceDir);
        File.WriteAllText(bundle, "bundle evidence");
        if (trackingPresent) File.WriteAllText(TrackingAdmissionTests.JournalPath(scope.WorkspaceDir), "complete\n");
        bool recovered = false;
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, (_, _) => recovered = true);
        var exception = Assert.Throws<Scrinia.Core.Migration.TrackingRecoveryRequiredException>(() =>
        {
            using var lease = admission.EnterTracking(Timeout, default);
        });
        Assert.Equal(bundle, exception.JournalPath);
        Assert.False(recovered);
        Assert.Equal("bundle evidence", File.ReadAllText(bundle));
        Assert.Equal(Scrinia.Core.Configuration.ConfigurationErrorCode.ConfigRecoveryRequired,
            new Scrinia.Core.Migration.LegacyMigrationService().Set(scope.WorkspaceDir,
                Scrinia.Core.Configuration.TrackingPolicy.Local).ErrorCode);
        if (trackingPresent)
        {
            Assert.Throws<RestoreRecoveryException>(() => { using var lease = admission.EnterRestore(Timeout, default); });
            Assert.Equal("complete\n", File.ReadAllText(TrackingAdmissionTests.JournalPath(scope.WorkspaceDir)));
        }
        Assert.Equal("bundle evidence", File.ReadAllText(bundle));
    }

    [Fact]
    public void TrackingIsRecheckedAfterBundleRecoveryReacquisition()
    {
        using var scope = new TestHelpers.StoreScope();
        string bundle = JournalPath(scope.WorkspaceDir);
        File.WriteAllText(bundle, "bundle evidence");
        var admission = new WorkspaceAdmission(scope.WorkspaceDir, (_, _) =>
        {
            File.Delete(bundle);
            File.WriteAllText(TrackingAdmissionTests.JournalPath(scope.WorkspaceDir), "complete\n");
        });
        Assert.Throws<RestoreRecoveryException>(() => { using var lease = admission.EnterRead(Timeout, default); });
        using var tracking = admission.EnterTracking(Timeout, default);
        Assert.Equal("complete\n", File.ReadAllText(TrackingAdmissionTests.JournalPath(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TrackingAndConfigWritersShareCanonicalLocksWithoutInversion(bool caseVariant, bool save)
    {
        using var scope = new TestHelpers.StoreScope();
        string alias = scope.WorkspaceDir + "-config-alias";
        Directory.CreateSymbolicLink(alias, scope.WorkspaceDir);
        string selected = caseVariant && OperatingSystem.IsWindows() ? scope.WorkspaceDir.ToUpperInvariant() : alias;
        string canonical = WorkspaceAdmission.CanonicalRoot(scope.WorkspaceDir);
        string configPath = Path.Combine(canonical, ".scrinia", "config.json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        FileLock? configLock = null;
        try
        {
            configLock = FileLock.AcquireExclusive(Scrinia.Core.Configuration.ConfigurationLock.GetPath(configPath));
            await using var holder = await TestProcessRunner.StartAsync(["admission", "hold", scope.WorkspaceDir], deadline.Token);
            await holder.ReadEventAsync("ready", deadline.Token);
            await using var tracking = await TestProcessRunner.StartAsync(["admission", "tracking", selected], deadline.Token);
            await tracking.ReadEventAsync("ready", deadline.Token);
            await tracking.SendAsync("continue", deadline.Token);
            await tracking.ReadEventAsync("waiting-workspace", deadline.Token);
            await using var writer = await TestProcessRunner.StartAsync(["admission", save ? "config-save" : "config", selected], deadline.Token);
            await writer.ReadEventAsync("ready", deadline.Token);
            await writer.SendAsync("continue", deadline.Token);
            await writer.ReadEventAsync("waiting-config", deadline.Token);
            configLock.Dispose();
            configLock = null;
            await writer.ReadEventAsync("completed", deadline.Token);
            Assert.Equal(0, await writer.WaitForExitAsync(deadline.Token));
            Assert.Contains("config-child", File.ReadAllText(configPath));

            // The tracking waiter must not own the config lock while blocked on workspace admission.
            // Hold config again and release workspace: tracking must now wait on this same lock.
            configLock = FileLock.AcquireExclusive(Scrinia.Core.Configuration.ConfigurationLock.GetPath(configPath));
            await holder.SendAsync("continue", deadline.Token);
            await holder.ReadEventAsync("completed", deadline.Token);
            Assert.Equal(0, await holder.WaitForExitAsync(deadline.Token));
            await tracking.ReadEventAsync("waiting-config", deadline.Token);
            configLock.Dispose();
            configLock = null;
            await tracking.ReadEventAsync("completed", deadline.Token);
            Assert.Equal(0, await tracking.WaitForExitAsync(deadline.Token));
            Scrinia.Core.Configuration.WorkspaceConfig.InvalidateCache(canonical);
            var config = Scrinia.Core.Configuration.WorkspaceConfig.Load(canonical);
            Assert.Equal("kept", config.GetValue("config-child"));
            Assert.Equal("local", config.GetValue(Scrinia.Core.Configuration.WorkspaceConfig.TrackingKey));
            Assert.False(File.Exists(TrackingAdmissionTests.JournalPath(canonical)));
        }
        finally
        {
            configLock?.Dispose();
            Directory.Delete(alias);
        }
    }

    [Fact]
    public void WindowsConfigurationLockIdentityIgnoresCase()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scope = new TestHelpers.StoreScope();
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", "config.json");
        Assert.Equal(Scrinia.Core.Configuration.ConfigurationLock.GetPath(path),
            Scrinia.Core.Configuration.ConfigurationLock.GetPath(path.ToUpperInvariant()));
    }

    [Fact]
    public async Task ConfigWriterRechecksEvidenceAfterWaitingForConfigurationLock()
    {
        using var scope = new TestHelpers.StoreScope();
        Scrinia.Core.Configuration.WorkspaceConfig.SetValue(scope.WorkspaceDir, "before", "kept");
        var before = TrackingAdmissionTests.Snapshot(scope.WorkspaceDir);
        using var tracking = new WorkspaceAdmission(scope.WorkspaceDir, null).EnterTracking(Timeout, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string configPath = Path.Combine(WorkspaceAdmission.CanonicalRoot(scope.WorkspaceDir), ".scrinia", "config.json");
        using (var configLock = FileLock.AcquireExclusive(Scrinia.Core.Configuration.ConfigurationLock.GetPath(configPath)))
        {
            await using var writer = await TestProcessRunner.StartAsync(["admission", "config", scope.WorkspaceDir], deadline.Token);
            await writer.ReadEventAsync("ready", deadline.Token);
            await writer.SendAsync("continue", deadline.Token);
            await writer.ReadEventAsync("waiting-config", deadline.Token);
            File.WriteAllText(TrackingAdmissionTests.JournalPath(scope.WorkspaceDir), "complete\n");
            configLock.Dispose();
            await writer.ReadEventAsync("recovery-required", deadline.Token);
            Assert.Equal(4, await writer.WaitForExitAsync(deadline.Token));
        }
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal("kept", Scrinia.Core.Configuration.WorkspaceConfig.Load(scope.WorkspaceDir).GetValue("before"));
        Assert.Equal("complete\n", File.ReadAllText(TrackingAdmissionTests.JournalPath(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedBaseRegistrationWaitsForTrackingAndRechecksEvidence(bool retained)
    {
        using var scope = new TestHelpers.StoreScope();
        Directory.CreateDirectory(Path.Combine(scope.WorkspaceDir, ".git"));
        string alias = scope.WorkspaceDir + "-add-alias";
        Directory.CreateSymbolicLink(alias, scope.WorkspaceDir);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var tracking = new WorkspaceAdmission(scope.WorkspaceDir, null).EnterTracking(Timeout, default);
            await using var child = await TestProcessRunner.StartAsync(["admission", "add", alias], deadline.Token);
            await child.ReadEventAsync("ready", deadline.Token);
            await child.SendAsync("continue", deadline.Token);
            await child.ReadEventAsync("waiting-workspace", deadline.Token);
            if (retained) File.WriteAllText(TrackingAdmissionTests.JournalPath(scope.WorkspaceDir), "complete\n");
            tracking.Dispose();
            await child.ReadEventAsync(retained ? "recovery-required" : "completed", deadline.Token);
            Assert.Equal(retained ? 4 : 0, await child.WaitForExitAsync(deadline.Token));
            string homeConfig = Path.Combine(scope.WorkspaceDir, "test-home", "config.json");
            string ignore = Path.Combine(scope.WorkspaceDir, ".scrinia", ".gitignore");
            if (retained)
            {
                Assert.False(File.Exists(homeConfig));
                Assert.False(File.Exists(ignore));
                Assert.Equal("complete\n", File.ReadAllText(TrackingAdmissionTests.JournalPath(scope.WorkspaceDir)));
            }
            else
            {
                Assert.Contains("team", File.ReadAllText(homeConfig));
                Assert.Equal(Scrinia.Core.Configuration.ManagedIgnoreBlock.ProjectGit, File.ReadAllText(ignore));
            }
            using var released = new WorkspaceAdmission(scope.WorkspaceDir, null).EnterTracking(Timeout, default);
            Assert.True(released.IsExclusive);
        }
        finally { Directory.Delete(alias); }
    }

}
