using System.Text;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class TrackingTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedReplacementSyncFailureNeverRollsBack(bool migrate)
    {
        using var scope = new TestHelpers.StoreScope();
        var service = new LegacyMigrationService();
        Assert.True(service.Set(scope.WorkspaceDir, TrackingPolicy.Local).Succeeded);
        string journal = Path.Combine(scope.WorkspaceDir, ".scrinia", ".tracking-recovery.json");
        int publications = 0;
        bool commitPending = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (path == journal && phase == DurableWritePhase.BeforeMove)
                commitPending = ++publications == 2;
            if (commitPending && phase == DurableWritePhase.BeforeDirSync)
            {
                commitPending = false;
                return new IOException("commit sync");
            }
            return null;
        };
        LegacyMigrationResult result;
        try { result = migrate ? service.Migrate(scope.WorkspaceDir, TrackingPolicy.ProjectGit)
                               : service.Set(scope.WorkspaceDir, TrackingPolicy.ProjectGit); }
        finally { DurableFile.FailureInjector = null; }
        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Equal(LegacyMigrationState.ProjectGit, new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null).State);
        Assert.False(File.Exists(journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcknowledgedCommitCleanupFailureRemainsSuccess(bool migrate)
    {
        using var scope = new TestHelpers.StoreScope();
        var service = new LegacyMigrationService();
        Assert.True(service.Set(scope.WorkspaceDir, TrackingPolicy.Local).Succeeded);
        string journal = Path.Combine(scope.WorkspaceDir, ".scrinia", ".tracking-recovery.json");
        DurableFile.DeleteFailureInjector = (phase, path) => path == journal && phase == DurableDeletePhase.BeforeDelete
            ? new IOException("cleanup") : null;
        LegacyMigrationResult result;
        try { result = migrate ? service.Migrate(scope.WorkspaceDir, TrackingPolicy.ProjectGit)
                               : service.Set(scope.WorkspaceDir, TrackingPolicy.ProjectGit); }
        finally { DurableFile.DeleteFailureInjector = null; }
        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.NotEmpty(result.Warnings!);
        Assert.Equal("committed", TrackingRecoveryJournal.Read(journal, scope.WorkspaceDir).Decision);
        Assert.Equal(LegacyMigrationState.ProjectGit, new LegacyMigrationService().Inspect(scope.WorkspaceDir, null, null).State);
        Assert.False(File.Exists(journal));
    }

    public static IEnumerable<object[]> WriteFaults()
    {
        foreach (bool migrate in new[] { false, true })
        foreach (string target in new[] { "prepared", "committed", "config.json", ".gitignore", ".gitattributes", "merge.config" })
        foreach (string phase in Enum.GetNames<DurableWritePhase>())
            yield return [migrate, target, phase];
    }

    [Theory]
    [MemberData(nameof(WriteFaults))]
    public void EveryWriteBoundaryPreservesACompleteDecision(bool migrate, string target, string phaseName)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        var before = Snapshot(scope.WorkspaceDir);
        var phase = Enum.Parse<DurableWritePhase>(phaseName);
        string journalPath = Journal(scope.WorkspaceDir);
        string? current = null;
        int journalWrites = 0;
        bool fired = false;
        Dictionary<string, byte[]?>? atFailure = null;
        string? decisionAtFailure = null;
        DurableFile.FailureInjector = (point, path) =>
        {
            if (point == DurableWritePhase.BeforeWrite)
            {
                current = path == journalPath ? (++journalWrites == 1 ? "prepared" : "committed") : Path.GetFileName(path);
            }
            if (!fired && current == target && point == phase)
            {
                fired = true;
                atFailure = Snapshot(scope.WorkspaceDir);
                decisionAtFailure = File.Exists(journalPath)
                    ? TrackingRecoveryJournal.Read(journalPath, scope.WorkspaceDir).Decision : null;
                return new UnauthorizedAccessException("injected boundary");
            }
            return null;
        };
        LegacyMigrationResult result;
        try { result = Change(scope.WorkspaceDir, migrate); }
        finally { DurableFile.FailureInjector = null; }
        Assert.True(fired);
        Assert.NotNull(atFailure);
        if (target == "committed")
        {
            Assert.Equal(phase == DurableWritePhase.BeforeDirSync ? "committed" : "prepared", decisionAtFailure);
            if (phase == DurableWritePhase.BeforeDirSync)
            {
                Assert.True(result.Succeeded);
                Assert.True(result.Changed);
                AssertSnapshot(atFailure!, scope.WorkspaceDir);
                Assert.False(File.Exists(journalPath));
                Assert.Equal(LegacyMigrationState.ProjectGit, FreshInspect(scope.WorkspaceDir).State);
                AssertSnapshot(atFailure!, scope.WorkspaceDir);
            }
            else
            {
                Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, result.ErrorCode);
                Assert.False(result.Changed);
                AssertSnapshot(atFailure!, scope.WorkspaceDir); // no compensation after commit was attempted
                Assert.Equal("prepared", TrackingRecoveryJournal.Read(journalPath, scope.WorkspaceDir).Decision);
                Assert.Equal(LegacyMigrationState.Local, FreshInspect(scope.WorkspaceDir).State);
                AssertSnapshot(before, scope.WorkspaceDir);
                Assert.False(File.Exists(journalPath));
            }
        }
        else
        {
            Assert.Equal(ConfigurationErrorCode.ConfigWriteFailed, result.ErrorCode);
            Assert.False(result.Changed);
            AssertSnapshot(before, scope.WorkspaceDir);
            Assert.False(File.Exists(journalPath));
            Assert.Equal(LegacyMigrationState.Local, FreshInspect(scope.WorkspaceDir).State);
            AssertSnapshot(before, scope.WorkspaceDir);
        }
    }

    public static IEnumerable<object[]> DeleteFaults()
    {
        foreach (bool migrate in new[] { false, true })
        foreach (string target in new[] { ".gitattributes", "merge.config", ".tracking-recovery.json" })
        foreach (string phase in Enum.GetNames<DurableDeletePhase>())
            yield return [migrate, target, phase];
    }

    [Theory]
    [MemberData(nameof(DeleteFaults))]
    public void EveryDeleteBoundaryPreservesMembersAndEvidence(bool migrate, string target, string phaseName)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir, emptyMembers: true);
        if (migrate)
        {
            File.WriteAllText(Member(scope.WorkspaceDir, "config.json"), "{}");
            File.WriteAllText(Member(scope.WorkspaceDir, ".gitattributes"), LegacyMergeRecognition.ScriniaMetaAttributeLine + "\n");
            File.WriteAllText(Member(scope.WorkspaceDir, "merge.config"), "{\r\n  \"jaccardThreshold\": 0.7,\r\n  \"conflictDir\": \"conflict\"\r\n}\n");
        }
        var before = Snapshot(scope.WorkspaceDir);
        var phase = Enum.Parse<DurableDeletePhase>(phaseName);
        bool fired = false;
        Dictionary<string, byte[]?>? atFailure = null;
        DurableFile.DeleteFailureInjector = (point, path) =>
        {
            if (!fired && Path.GetFileName(path) == target && point == phase)
            {
                fired = true;
                atFailure = Snapshot(scope.WorkspaceDir);
                return new IOException("delete boundary");
            }
            return null;
        };
        LegacyMigrationResult result;
        try { result = Change(scope.WorkspaceDir, migrate); }
        finally { DurableFile.DeleteFailureInjector = null; }
        Assert.True(fired);
        if (target == ".tracking-recovery.json")
        {
            Assert.True(result.Succeeded);
            Assert.True(result.Changed);
            Assert.NotEmpty(result.Warnings!);
            AssertSnapshot(atFailure!, scope.WorkspaceDir);
            Assert.Equal(phase == DurableDeletePhase.BeforeDelete, File.Exists(Journal(scope.WorkspaceDir)));
            if (File.Exists(Journal(scope.WorkspaceDir)))
                Assert.Equal("committed", TrackingRecoveryJournal.Read(Journal(scope.WorkspaceDir), scope.WorkspaceDir).Decision);
            Assert.Equal(LegacyMigrationState.ProjectGit, FreshInspect(scope.WorkspaceDir).State);
            AssertSnapshot(atFailure!, scope.WorkspaceDir);
        }
        else
        {
            Assert.Equal(ConfigurationErrorCode.ConfigWriteFailed, result.ErrorCode);
            Assert.False(result.Changed);
            AssertSnapshot(before, scope.WorkspaceDir);
            Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
            Assert.Equal(migrate ? LegacyMigrationState.LegacyClean : LegacyMigrationState.Local, FreshInspect(scope.WorkspaceDir).State);
            AssertSnapshot(before, scope.WorkspaceDir);
        }
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedCompensationRetainsEvidenceAndRejectsLaterThirdState(bool migrate, bool thirdState)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        var before = Snapshot(scope.WorkspaceDir);
        DurableFile.FailureInjector = (phase, path) => Path.GetFileName(path) == ".gitignore" && phase == DurableWritePhase.BeforeWrite
            ? new IOException("persistent member failure") : null;
        LegacyMigrationResult result;
        try { result = Change(scope.WorkspaceDir, migrate); }
        finally { DurableFile.FailureInjector = null; }
        Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, result.ErrorCode);
        Assert.False(result.Changed);
        Assert.Equal("prepared", TrackingRecoveryJournal.Read(Journal(scope.WorkspaceDir), scope.WorkspaceDir).Decision);
        if (thirdState) File.WriteAllText(Member(scope.WorkspaceDir, "merge.config"), "unknown later member");
        var retained = Snapshot(scope.WorkspaceDir);
        byte[] evidence = File.ReadAllBytes(Journal(scope.WorkspaceDir));
        var inspection = FreshInspect(scope.WorkspaceDir);
        if (thirdState)
        {
            Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, inspection.ErrorCode);
            AssertSnapshot(retained, scope.WorkspaceDir);
            Assert.Equal(evidence, File.ReadAllBytes(Journal(scope.WorkspaceDir)));
        }
        else
        {
            Assert.Equal(LegacyMigrationState.Local, inspection.State);
            AssertSnapshot(before, scope.WorkspaceDir);
            Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncertainCommitRewriteFailureRetainsCommittedAfterImages(bool migrate)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        int journalWrites = 0;
        bool journalWrite = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (phase == DurableWritePhase.BeforeWrite)
            {
                journalWrite = path == Journal(scope.WorkspaceDir);
                if (journalWrite) journalWrites++;
            }
            return journalWrite && journalWrites >= 2 && phase == DurableWritePhase.BeforeDirSync ? new IOException("sync") : null;
        };
        LegacyMigrationResult result;
        try { result = Change(scope.WorkspaceDir, migrate); }
        finally { DurableFile.FailureInjector = null; }
        Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, result.ErrorCode);
        Assert.False(result.Changed);
        var after = Snapshot(scope.WorkspaceDir);
        Assert.Equal("committed", TrackingRecoveryJournal.Read(Journal(scope.WorkspaceDir), scope.WorkspaceDir).Decision);
        Assert.Equal(LegacyMigrationState.ProjectGit, FreshInspect(scope.WorkspaceDir).State);
        AssertSnapshot(after, scope.WorkspaceDir);
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("committed")]
    public void RecoveryReadFailureDoesNotMutateEarlierMembers(string decision)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        LeaveEvidence(scope.WorkspaceDir, decision);
        var snapshot = Snapshot(scope.WorkspaceDir);
        byte[] evidence = File.ReadAllBytes(Journal(scope.WorkspaceDir));
        using (var held = new FileStream(Member(scope.WorkspaceDir, "merge.config"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, FreshInspect(scope.WorkspaceDir).ErrorCode);
            foreach (var member in snapshot.Where(x => x.Key != "merge.config"))
                Assert.Equal(member.Value, File.ReadAllBytes(Member(scope.WorkspaceDir, member.Key)));
            Assert.Equal(evidence, File.ReadAllBytes(Journal(scope.WorkspaceDir)));
        }
        AssertSnapshot(snapshot, scope.WorkspaceDir);
        Assert.Equal(decision == "prepared" ? LegacyMigrationState.Local : LegacyMigrationState.ProjectGit,
            FreshInspect(scope.WorkspaceDir).State);
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedRecoveryDistinguishesEmptyAndAbsentPriorMember(bool exists)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        if (exists) File.WriteAllBytes(Member(scope.WorkspaceDir, "merge.config"), []);
        else File.Delete(Member(scope.WorkspaceDir, "merge.config"));
        var before = Snapshot(scope.WorkspaceDir);
        LeaveEvidence(scope.WorkspaceDir, "prepared");
        var journal = TrackingRecoveryJournal.Read(Journal(scope.WorkspaceDir), scope.WorkspaceDir);
        var state = journal.Members.Single(x => x.Path == "merge.config").Before;
        Assert.Equal(exists, state.Exists);
        Assert.Equal(exists, state.Bytes is not null);
        Assert.Equal(LegacyMigrationState.Local, FreshInspect(scope.WorkspaceDir).State);
        AssertSnapshot(before, scope.WorkspaceDir);
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateProcessCrashRecoversCompleteDecision(bool committed)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        var before = Snapshot(scope.WorkspaceDir);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var child = await TestProcessRunner.StartAsync(
            ["tracking", committed ? "committed-crash" : "prepared-crash", scope.WorkspaceDir], deadline.Token))
        {
            await child.ReadEventAsync("ready", deadline.Token);
            await child.SendAsync("continue", deadline.Token);
            Assert.Equal(73, await child.WaitForExitAsync(deadline.Token));
        }
        var retained = Snapshot(scope.WorkspaceDir);
        Assert.Equal(committed ? "committed" : "prepared", TrackingRecoveryJournal.Read(Journal(scope.WorkspaceDir), scope.WorkspaceDir).Decision);
        await using (var child = await TestProcessRunner.StartAsync(
            ["tracking", committed ? "recover-project-git" : "recover-local", scope.WorkspaceDir], deadline.Token))
        {
            await child.ReadEventAsync("recovered", deadline.Token);
            Assert.Equal(0, await child.WaitForExitAsync(deadline.Token));
        }
        AssertSnapshot(committed ? retained : before, scope.WorkspaceDir);
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }


    [Theory]
    [InlineData("config.json")]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData("merge.config")]
    public void ServiceRefusesUnsafeMemberBeforePolicyRead(string name)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        string target = Path.Combine(scope.WorkspaceDir, "outside");
        File.WriteAllText(target, name == "config.json" ? """{"Scrinia:Workspace:Tracking":"local"}""" : "outside");
        File.Delete(Member(scope.WorkspaceDir, name));
        File.CreateSymbolicLink(Member(scope.WorkspaceDir, name), target);
        try
        {
            Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, FreshInspect(scope.WorkspaceDir).ErrorCode);
            Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, Change(scope.WorkspaceDir, false).ErrorCode);
            Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
        }
        finally { File.Delete(Member(scope.WorkspaceDir, name)); }
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("committed")]
    public void RecoveryThirdStateOnLaterMemberPreservesEveryByte(string decision)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        LeaveEvidence(scope.WorkspaceDir, decision);
        File.WriteAllText(Member(scope.WorkspaceDir, "merge.config"), "third state");
        var snapshot = Snapshot(scope.WorkspaceDir);
        byte[] evidence = File.ReadAllBytes(Journal(scope.WorkspaceDir));
        Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, FreshInspect(scope.WorkspaceDir).ErrorCode);
        AssertSnapshot(snapshot, scope.WorkspaceDir);
        Assert.Equal(evidence, File.ReadAllBytes(Journal(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPreparedRestoresCompleteBoundedCanonicalSet(bool empty)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        var before = Snapshot(scope.WorkspaceDir);
        before["merge.config"] = empty ? [] : null;
        string evidence = "prepared\n" + string.Concat(before.Select(pair =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Member(scope.WorkspaceDir, pair.Key))) + "|" +
            (pair.Value is null ? "-" : Convert.ToBase64String(pair.Value)) + "\n"));
        File.WriteAllText(Journal(scope.WorkspaceDir), evidence);
        File.WriteAllText(Member(scope.WorkspaceDir, ".gitignore"), "partial legacy after bytes");
        Assert.Equal(LegacyMigrationState.Local, FreshInspect(scope.WorkspaceDir).State);
        AssertSnapshot(before, scope.WorkspaceDir);
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData("base64")]
    [InlineData("utf8")]
    [InlineData("oversize")]
    [InlineData("alias-path")]
    public void InvalidLegacyRecordOnLaterMemberRefusesBeforeAnyMutation(string corruption)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        var before = Snapshot(scope.WorkspaceDir);
        var lines = before.Select(pair =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Member(scope.WorkspaceDir, pair.Key))) + "|" +
            Convert.ToBase64String(pair.Value!)).ToArray();
        string target = Convert.ToBase64String(Encoding.UTF8.GetBytes(Member(scope.WorkspaceDir, "merge.config")));
        lines[^1] = corruption switch
        {
            "base64" => target + "|Zg== ",
            "utf8" => Convert.ToBase64String(new byte[] { 0xff }) + "|-",
            "oversize" => target + "|" + new string('A', (int)((TrackingRecoveryJournal.MaxStateBytes + 2) / 3 * 4 + 4)),
            _ => Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.Combine(scope.WorkspaceDir, ".scrinia", ".", "merge.config"))) + "|-",
        };
        byte[] evidence = Encoding.UTF8.GetBytes("prepared\n" + string.Join('\n', lines) + "\n");
        File.WriteAllBytes(Journal(scope.WorkspaceDir), evidence);
        Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, FreshInspect(scope.WorkspaceDir).ErrorCode);
        AssertSnapshot(before, scope.WorkspaceDir);
        Assert.Equal(evidence, File.ReadAllBytes(Journal(scope.WorkspaceDir)));
    }

    [Fact]
    public void AliasedServiceCallerAndCanonicalRecoveryShareOwnership()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        string alias = scope.WorkspaceDir + "-tracking-alias";
        Directory.CreateSymbolicLink(alias, scope.WorkspaceDir);
        try
        {
            Assert.True(Change(alias, false).Succeeded);
            Assert.Equal(LegacyMigrationState.ProjectGit, FreshInspect(scope.WorkspaceDir).State);
            Assert.Equal(LegacyMigrationState.ProjectGit, FreshInspect(alias).State);
            using var lease = new WorkspaceAdmission(alias, null).EnterRestore(TimeSpan.FromSeconds(5), default);
            TrackingTransaction.RecoverLocked(scope.WorkspaceDir, lease);
            TrackingTransaction.RecoverLocked(alias, lease);
        }
        finally { Directory.Delete(alias); }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvenRollbackOfEmptyOrMissingConfigIsWriteFailure(bool empty)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        string config = Member(scope.WorkspaceDir, "config.json");
        if (empty) File.WriteAllBytes(config, []);
        else File.Delete(config);
        var before = Snapshot(scope.WorkspaceDir);
        var after = new Dictionary<string, byte[]?>(before) { ["config.json"] = "{}"u8.ToArray() };
        bool fired = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (!fired && phase == DurableWritePhase.BeforeWrite && Path.GetFileName(path) == ".gitignore")
            { fired = true; return new UnauthorizedAccessException("member"); }
            return null;
        };
        LegacyMigrationResult result;
        try { result = CommitDirect(scope.WorkspaceDir, after); }
        finally { DurableFile.FailureInjector = null; }
        Assert.Equal(ConfigurationErrorCode.ConfigWriteFailed, result.ErrorCode);
        AssertSnapshot(before, scope.WorkspaceDir);
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RollbackCleanupFailureRetainsPreparedEvidenceForRestart(bool afterDelete)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        var before = Snapshot(scope.WorkspaceDir);
        bool fired = false;
        DurableFile.FailureInjector = (phase, path) =>
        {
            if (!fired && phase == DurableWritePhase.BeforeWrite && Path.GetFileName(path) == ".gitignore")
            { fired = true; return new UnauthorizedAccessException("member"); }
            return null;
        };
        DurableFile.DeleteFailureInjector = (phase, path) => path == Journal(scope.WorkspaceDir) &&
            phase == (afterDelete ? DurableDeletePhase.AfterDelete : DurableDeletePhase.BeforeDelete)
                ? new IOException("rollback cleanup") : null;
        LegacyMigrationResult result;
        try { result = Change(scope.WorkspaceDir, false); }
        finally { DurableFile.FailureInjector = null; DurableFile.DeleteFailureInjector = null; }
        Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, result.ErrorCode);
        AssertSnapshot(before, scope.WorkspaceDir);
        Assert.Equal("prepared", TrackingRecoveryJournal.Read(Journal(scope.WorkspaceDir), scope.WorkspaceDir).Decision);
        Assert.Equal(LegacyMigrationState.Local, FreshInspect(scope.WorkspaceDir).State);
        AssertSnapshot(before, scope.WorkspaceDir);
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData("merge.config")]
    public void DirectTransactionCanCommitPositiveAbsenceOfEachMember(string member)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        var after = Snapshot(scope.WorkspaceDir);
        after[member] = null;
        LegacyMigrationResult result = CommitDirect(scope.WorkspaceDir, after);
        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        AssertSnapshot(after, scope.WorkspaceDir);
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }

    [Fact]
    public void OversizedAfterImageRefusesBeforePublicationOrMutation()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        var before = Snapshot(scope.WorkspaceDir);
        var after = new Dictionary<string, byte[]?>(before)
            { ["merge.config"] = new byte[TrackingRecoveryJournal.MaxStateBytes + 1] };
        Assert.Throws<TrackingRecoveryRequiredException>(() => CommitDirect(scope.WorkspaceDir, after));
        AssertSnapshot(before, scope.WorkspaceDir);
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }

    private static LegacyMigrationResult CommitDirect(string root, IReadOnlyDictionary<string, byte[]?> after)
    {
        using var lease = new WorkspaceAdmission(root, null).EnterRestore(TimeSpan.FromSeconds(5), default);
        using var configLock = FileLock.AcquireExclusive(ConfigurationLock.GetPath(Member(root, "config.json")));
        return TrackingTransaction.CommitLocked(root, lease, "set", after);
    }


    [SkippableTheory]
    [InlineData("config.json")]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData("merge.config")]
    public void UnixSpecialMemberIsRefusedWithoutOpeningIt(string member)
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix socket carrier regression requires Unix.");
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        File.Delete(Member(scope.WorkspaceDir, member));
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(Member(scope.WorkspaceDir, member)));
        Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, FreshInspect(scope.WorkspaceDir).ErrorCode);
        Assert.Equal(ConfigurationErrorCode.ConfigRecoveryRequired, Change(scope.WorkspaceDir, false).ErrorCode);
        Assert.True(File.Exists(Member(scope.WorkspaceDir, member)));
        Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
    }


    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WarmCanonicalAndAliasCacheFollowSuccessfulTrackingChange(bool migrate, bool aliasCaller)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        string alias = scope.WorkspaceDir + "-cache-alias";
        Directory.CreateSymbolicLink(alias, scope.WorkspaceDir);
        try
        {
            var canonicalSnapshot = WorkspaceConfig.Load(scope.WorkspaceDir);
            var aliasSnapshot = WorkspaceConfig.Load(alias);
            Assert.Equal(TrackingPolicy.Local, canonicalSnapshot.GetTrackingPolicy());
            Assert.Equal(TrackingPolicy.Local, aliasSnapshot.GetTrackingPolicy());
            Assert.True(Change(aliasCaller ? alias : scope.WorkspaceDir, migrate).Succeeded);
            Assert.Equal(TrackingPolicy.ProjectGit, WorkspaceConfig.Load(scope.WorkspaceDir).GetTrackingPolicy());
            Assert.Equal(TrackingPolicy.ProjectGit, WorkspaceConfig.Load(alias).GetTrackingPolicy());
            Assert.Equal(TrackingPolicy.Local, canonicalSnapshot.GetTrackingPolicy());
            Assert.Equal(TrackingPolicy.Local, aliasSnapshot.GetTrackingPolicy());
        }
        finally { Directory.Delete(alias); }
    }

    [Theory]
    [InlineData("prepared", false)]
    [InlineData("prepared", true)]
    [InlineData("committed", false)]
    [InlineData("committed", true)]
    public void WarmCanonicalAndAliasCacheFollowRetainedRecovery(string decision, bool aliasCaller)
    {
        using var scope = new TestHelpers.StoreScope();
        File.WriteAllText(Member(scope.WorkspaceDir, "config.json"), """{"Scrinia:Workspace:Tracking":"local"}""");
        File.WriteAllText(Member(scope.WorkspaceDir, ".gitignore"), ManagedIgnoreBlock.Local);
        string alias = scope.WorkspaceDir + "-recovery-cache-alias";
        Directory.CreateSymbolicLink(alias, scope.WorkspaceDir);
        try
        {
            if (decision == "committed")
            {
                Assert.Equal(TrackingPolicy.Local, WorkspaceConfig.Load(scope.WorkspaceDir).GetTrackingPolicy());
                Assert.Equal(TrackingPolicy.Local, WorkspaceConfig.Load(alias).GetTrackingPolicy());
            }
            LeaveEvidence(scope.WorkspaceDir, decision);
            if (decision == "prepared")
            {
                Assert.Equal(TrackingPolicy.ProjectGit, WorkspaceConfig.Load(scope.WorkspaceDir).GetTrackingPolicy());
                Assert.Equal(TrackingPolicy.ProjectGit, WorkspaceConfig.Load(alias).GetTrackingPolicy());
            }
            var result = new LegacyMigrationService().Inspect(aliasCaller ? alias : scope.WorkspaceDir, null, null);
            Assert.Equal(ConfigurationErrorCode.None, result.ErrorCode);
            TrackingPolicy expected = decision == "prepared" ? TrackingPolicy.Local : TrackingPolicy.ProjectGit;
            Assert.Equal(expected, WorkspaceConfig.Load(scope.WorkspaceDir).GetTrackingPolicy());
            Assert.Equal(expected, WorkspaceConfig.Load(alias).GetTrackingPolicy());
            Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
        }
        finally { Directory.Delete(alias); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("malformed")]
    public void VerifiedMalformedRestorationInvalidatesWarmValidCache(string beforeConfig)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        string alias = scope.WorkspaceDir + "-malformed-cache-alias";
        Directory.CreateSymbolicLink(alias, scope.WorkspaceDir);
        try
        {
            Assert.Equal(TrackingPolicy.Local, WorkspaceConfig.Load(scope.WorkspaceDir).GetTrackingPolicy());
            Assert.Equal(TrackingPolicy.Local, WorkspaceConfig.Load(alias).GetTrackingPolicy());
            var after = Snapshot(scope.WorkspaceDir);
            var before = new Dictionary<string, byte[]?>(after) { ["config.json"] = Encoding.UTF8.GetBytes(beforeConfig) };
            TrackingState State(byte[]? bytes) => bytes is null ? new(false, 0, null, null) :
                new(true, bytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), bytes);
            var journal = new TrackingJournal(1, "malformed-restoration", "set", "prepared",
                TrackingRecoveryJournal.MemberNames.Select(name => new TrackingMember(name, State(before[name]), State(after[name]))).ToArray());
            File.WriteAllBytes(Journal(scope.WorkspaceDir), TrackingRecoveryJournal.Serialize(journal));
            var result = new LegacyMigrationService().Inspect(alias, null, null);
            Assert.Equal(ConfigurationErrorCode.WorkspaceConfigMalformed, result.ErrorCode);
            AssertSnapshot(before, scope.WorkspaceDir);
            Assert.False(File.Exists(Journal(scope.WorkspaceDir)));
            Assert.Throws<ScriniaConfigurationException>(() => WorkspaceConfig.Load(scope.WorkspaceDir));
            Assert.Throws<ScriniaConfigurationException>(() => WorkspaceConfig.Load(alias));
        }
        finally { Directory.Delete(alias); }
    }

    [Theory]
    [InlineData("save")]
    [InlineData("set")]
    [InlineData("unset")]
    [InlineData("enabled")]
    public void EveryConfigurationPublicationUsesCanonicalCacheIdentity(string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(scope.WorkspaceDir);
        WorkspaceConfig.SetValue(scope.WorkspaceDir, "test-key", "old");
        string alias = scope.WorkspaceDir + "-config-cache-alias";
        Directory.CreateSymbolicLink(alias, scope.WorkspaceDir);
        try
        {
            var snapshot = WorkspaceConfig.Load(scope.WorkspaceDir);
            Assert.Equal("old", WorkspaceConfig.Load(alias).GetValue("test-key"));
            switch (operation)
            {
                case "save": WorkspaceConfig.Save(alias, new() { [WorkspaceConfig.TrackingKey] = "local", ["test-key"] = "new" }); break;
                case "set": WorkspaceConfig.SetValue(alias, "test-key", "new"); break;
                case "unset": WorkspaceConfig.UnsetValue(alias, "test-key"); break;
                case "enabled": WorkspaceConfig.Load(alias).UpdateEnabledBases(_ => ["shared"]); break;
            }
            var canonical = WorkspaceConfig.Load(scope.WorkspaceDir);
            var aliased = WorkspaceConfig.Load(alias);
            if (operation == "enabled")
            {
                Assert.Equal("shared", Assert.Single(canonical.GetEnabledBases()));
                Assert.Equal("shared", Assert.Single(aliased.GetEnabledBases()));
            }
            else
            {
                Assert.Equal(operation == "unset" ? null : "new", canonical.GetValue("test-key"));
                Assert.Equal(operation == "unset" ? null : "new", aliased.GetValue("test-key"));
            }
            Assert.Equal("old", snapshot.GetValue("test-key"));
        }
        finally { Directory.Delete(alias); }
    }

    [SkippableFact]
    public void CanonicalCacheIdentityDoesNotCollapseCaseDistinctUnixWorkspaces()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows workspace identities ignore case.");
        using var scope = new TestHelpers.StoreScope();
        string lower = Path.Combine(scope.WorkspaceDir, "lower");
        string upper = Path.Combine(scope.WorkspaceDir, "LOWER");
        Directory.CreateDirectory(Path.Combine(lower, ".scrinia"));
        Directory.CreateDirectory(Path.Combine(upper, ".scrinia"));
        File.WriteAllText(Path.Combine(lower, ".scrinia", "config.json"), """{"test-key":"lower"}""");
        File.WriteAllText(Path.Combine(upper, ".scrinia", "config.json"), """{"test-key":"upper"}""");
        Skip.If(File.ReadAllText(Path.Combine(lower, ".scrinia", "config.json")).Contains("upper", StringComparison.Ordinal),
            "Requires a case-sensitive filesystem.");
        Assert.Equal("lower", WorkspaceConfig.Load(lower).GetValue("test-key"));
        Assert.Equal("upper", WorkspaceConfig.Load(upper).GetValue("test-key"));
    }

    private static void LeaveEvidence(string root, string decision)
    {
        int journals = 0;
        DurableFile.FailureInjector = (phase, path) => path == Journal(root) &&
            phase == DurableWritePhase.BeforeWrite && ++journals == 2 && decision == "prepared"
                ? new UnauthorizedAccessException("commit write") : null;
        DurableFile.DeleteFailureInjector = (_, path) => path == Journal(root) && decision == "committed"
            ? new IOException("cleanup") : null;
        try
        {
            var result = Change(root, false);
            Assert.Equal(decision == "committed", result.Succeeded);
        }
        finally { DurableFile.FailureInjector = null; DurableFile.DeleteFailureInjector = null; }
        Assert.True(File.Exists(Journal(root)));
    }

    private static void Seed(string root, bool emptyMembers = false)
    {
        Assert.True(new LegacyMigrationService().Set(root, TrackingPolicy.Local).Succeeded);
        if (!emptyMembers)
        {
            File.WriteAllText(Member(root, ".gitattributes"), "custom attributes\r\n");
            File.WriteAllText(Member(root, "merge.config"), "custom config\r\n");
        }
    }

    private static LegacyMigrationResult Change(string root, bool migrate) => migrate
        ? new LegacyMigrationService().Migrate(root, TrackingPolicy.ProjectGit)
        : new LegacyMigrationService().Set(root, TrackingPolicy.ProjectGit);
    private static LegacyFilesystemObservation FreshInspect(string root)
    {
        WorkspaceConfig.RefreshCache(root, []);
        return new LegacyMigrationService().Inspect(root, null, null);
    }
    private static Dictionary<string, byte[]?> Snapshot(string root) => TrackingRecoveryJournal.MemberNames.ToDictionary(
        name => name, name => File.Exists(Member(root, name)) ? File.ReadAllBytes(Member(root, name)) : null);
    private static void AssertSnapshot(Dictionary<string, byte[]?> expected, string root)
    {
        foreach ((string name, byte[]? bytes) in expected)
        {
            Assert.Equal(bytes is not null, File.Exists(Member(root, name)));
            if (bytes is not null) Assert.Equal(bytes, File.ReadAllBytes(Member(root, name)));
        }
    }
    private static string Member(string root, string name) => Path.Combine(root, ".scrinia", name);
    private static string Journal(string root) => Member(root, ".tracking-recovery.json");
}
