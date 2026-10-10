using System.Security.Cryptography;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

public sealed class EmbeddingsBundleInstallerTests
{
    private static string Rid => OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-x64";
    private static string Destination(EmbeddingsBundleTestFixture f) => Path.Combine(f.Home, "plugins", "scri-plugin-embeddings", "0.6.0", Rid);
    private static EmbeddingsInstallResult Install(EmbeddingsBundleTestFixture f, CancellationToken ct = default) =>
        EmbeddingsBundleInstaller.Install(f.Root, f.Home, "0.6.0", Rid, ct);

    [Fact]
    public void RepeatInstallPreservesMutableStateSourceAndOtherVersions()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        var before = Snapshot(f.Root);
        string other = Path.Combine(f.Home, "plugins", "scri-plugin-embeddings", "0.5.0", "keep");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "old");
        var first = Install(f);
        Assert.Equal(EmbeddingsInstallStatus.Success, first.Status);
        Assert.False(first.AlreadyInstalled);
        Assert.Equal(Destination(f), first.Destination);
        string state = Path.Combine(first.Destination!, "state", "nested");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "user-state"), "keep");
        var second = Install(f);
        Assert.Equal(EmbeddingsInstallStatus.Success, second.Status);
        Assert.True(second.AlreadyInstalled);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(state, "user-state")));
        Assert.Equal("old", File.ReadAllText(other));
        Assert.Equal(before, Snapshot(f.Root));
        AssertPayload(f);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Destination(f))!, ".*.staging-*"));
    }

    [Theory]
    [InlineData("different")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("directory")]
    public void ExistingRuntimeConflictIsNeverOverwritten(string change)
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        Assert.Equal(EmbeddingsInstallStatus.Success, Install(f).Status);
        string member = Path.Combine(Destination(f), f.Manifest.Files[0].Name);
        if (change == "different") File.WriteAllText(member, "different");
        if (change == "missing") File.Delete(member);
        if (change == "extra") File.WriteAllText(Path.Combine(Destination(f), "unknown"), "keep");
        if (change == "directory") Directory.CreateDirectory(Path.Combine(Destination(f), "unknown"));
        var before = Snapshot(f.Home);
        Assert.Equal(EmbeddingsInstallStatus.ExistingContentConflict, Install(f).Status);
        Assert.Equal(before, Snapshot(f.Home));
    }

    [Fact]
    public void InvalidSourceDoesNotCreateHome()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        File.WriteAllText(Path.Combine(f.PayloadDirectory, f.Manifest.Files[0].Name), "bad");
        Assert.Equal(EmbeddingsInstallStatus.InvalidBundle, Install(f).Status);
        Assert.False(Directory.Exists(f.Home));
    }

    [Theory]
    [InlineData((int)EmbeddingsInstallPhase.BeforeCopy)]
    [InlineData((int)EmbeddingsInstallPhase.AfterCopy)]
    [InlineData((int)EmbeddingsInstallPhase.BeforeRename)]
    public void PrepublicationFailureCleansOnlyOwnStaging(int phase)
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        string old = Path.Combine(Path.GetDirectoryName(Destination(f))!, "." + Rid + ".staging-old");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "unknown"), "keep");
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (p, _) => (int)p == phase ? new IOException("injected") : null;
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, Install(f).Status);
        }
        finally { EmbeddingsBundleInstaller.FailureInjector = null; }
        Assert.False(Directory.Exists(Destination(f)));
        Assert.Equal(new[] { old }, Directory.GetDirectories(Path.GetDirectoryName(Destination(f))!, ".*.staging-*"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(old, "unknown")));
    }

    [Fact]
    public void SourceReplacementBetweenValidationAndCopyIsRejected()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        bool changed = false;
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, path) =>
            {
                if (phase == EmbeddingsInstallPhase.BeforeCopy && !changed)
                {
                    changed = true;
                    File.Delete(path);
                    File.WriteAllText(path, "substituted");
                }
                return null;
            };
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, Install(f).Status);
        }
        finally { EmbeddingsBundleInstaller.FailureInjector = null; }
        Assert.True(changed);
        Assert.False(Directory.Exists(Destination(f)));
    }

    [Fact]
    public void StagedHashMismatchIsRejectedBeforePublication()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, path) =>
            {
                if (phase == EmbeddingsInstallPhase.AfterCopy) File.WriteAllText(path, "corruption");
                return null;
            };
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, Install(f).Status);
        }
        finally { EmbeddingsBundleInstaller.FailureInjector = null; }
        Assert.False(Directory.Exists(Destination(f)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforePublicationDoesNotLeavePayload(bool duringCopy)
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var ct = new CancellationTokenSource();
        if (!duringCopy) ct.Cancel();
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, _) =>
            {
                if (phase == EmbeddingsInstallPhase.BeforeCopy) ct.Cancel();
                return null;
            };
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, Install(f, ct.Token).Status);
        }
        finally { EmbeddingsBundleInstaller.FailureInjector = null; }
        Assert.False(Directory.Exists(Destination(f)));
    }

    [Fact]
    public void UncertainRenameRetainsCompletePayloadAndRetryConverges()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, _) => phase == EmbeddingsInstallPhase.AfterRename ? new IOException("uncertain rename") : null;
            var failed = Install(f);
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, failed.Status);
            Assert.Contains("retained", failed.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Destination(f), failed.Destination);
            AssertPayload(f);
        }
        finally { EmbeddingsBundleInstaller.FailureInjector = null; }
        Assert.True(Install(f).AlreadyInstalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostRenameSyncRetriesOrRetainsEvidence(bool persistent)
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        int attempts = 0;
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, path) =>
                phase == EmbeddingsInstallPhase.BeforeDirectorySync && path == Destination(f) && (++attempts == 1 || persistent)
                    ? new IOException("directory sync") : null;
            var result = Install(f);
            Assert.Equal(persistent ? EmbeddingsInstallStatus.InstallationFailed : EmbeddingsInstallStatus.Success, result.Status);
            Assert.True(attempts >= 2);
            AssertPayload(f);
        }
        finally { EmbeddingsBundleInstaller.FailureInjector = null; }
        Assert.True(Install(f).AlreadyInstalled);
    }

    [Fact]
    public void CancellationAfterRenameNeverDeletesFinalFiles()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var ct = new CancellationTokenSource();
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, _) =>
            {
                if (phase == EmbeddingsInstallPhase.AfterRename) ct.Cancel();
                return null;
            };
            Assert.Equal(EmbeddingsInstallStatus.Success, Install(f, ct.Token).Status);
            AssertPayload(f);
        }
        finally { EmbeddingsBundleInstaller.FailureInjector = null; }
    }

    [Fact]
    public void CleanupRefusesStagingWithUnknownContents()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        string? retained = null;
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, path) =>
            {
                if (phase != EmbeddingsInstallPhase.BeforeRename) return null;
                retained = path;
                File.WriteAllText(Path.Combine(path, "unknown"), "keep");
                return new IOException("failed with foreign data");
            };
            var result = Install(f);
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, result.Status);
            Assert.Contains("retained", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { EmbeddingsBundleInstaller.FailureInjector = null; }
        Assert.Equal("keep", File.ReadAllText(Path.Combine(retained!, "unknown")));
        Assert.False(Directory.Exists(Destination(f)));
    }

    [SkippableFact]
    public void UnixModeIsPreservedAndModeOnlyConflictIsRejected()
    {
        Skip.If(OperatingSystem.IsWindows(), "Native Unix mode evidence only.");
        if (OperatingSystem.IsWindows()) return;
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        string source = Path.Combine(f.PayloadDirectory, f.Manifest.Files[0].Name);
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(source, mode);
        Assert.Equal(EmbeddingsInstallStatus.Success, Install(f).Status);
        string installed = Path.Combine(Destination(f), f.Manifest.Files[0].Name);
        Assert.Equal(mode, File.GetUnixFileMode(installed));
        File.SetUnixFileMode(installed, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(EmbeddingsInstallStatus.ExistingContentConflict, Install(f).Status);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(installed));
    }

    [Fact]
    public async Task TwoProcessesSerializeAndConvergeOnOneCompletePayload()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var first = await TestProcessRunner.StartAsync(
            ["admission", "install-hold", f.Root, f.Home, "0.6.0", Rid], deadline.Token);
        await first.ReadEventAsync("ready", deadline.Token);
        await first.SendAsync("continue", deadline.Token);
        await first.ReadEventAsync("copying", deadline.Token);
        await using var second = await TestProcessRunner.StartAsync(
            ["admission", "install", f.Root, f.Home, "0.6.0", Rid], deadline.Token);
        await second.ReadEventAsync("ready", deadline.Token);
        await second.SendAsync("continue", deadline.Token);
        await second.ReadEventAsync("waiting-install", deadline.Token);
        Assert.False(Directory.Exists(Destination(f)));
        await first.SendAsync("continue", deadline.Token);
        await first.ReadEventAsync("installed", deadline.Token);
        await second.ReadEventAsync("identical", deadline.Token);
        Assert.Equal(0, await first.WaitForExitAsync(deadline.Token));
        Assert.Equal(0, await second.WaitForExitAsync(deadline.Token));
        AssertPayload(f);
        Assert.Equal(new[] { Destination(f) }, Directory.GetDirectories(Path.GetDirectoryName(Destination(f))!));
    }

    [Fact]
    public void RetrySynchronizesParentsEvenWhenPreviousCreationWasNotDurable()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        string plugins = Path.Combine(f.Home, "plugins");
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, path) =>
                phase == EmbeddingsInstallPhase.BeforeDirectorySync && path == plugins ? new IOException("parent not durable") : null;
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, Install(f).Status);
            Assert.True(Directory.Exists(plugins));
            // A retry must not silently trust this existing but unsynchronized parent.
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, Install(f).Status);
            Assert.False(Directory.Exists(Destination(f)));
        }
        finally { EmbeddingsBundleInstaller.FailureInjector = null; }
        Assert.Equal(EmbeddingsInstallStatus.Success, Install(f).Status);
    }

    [SkippableTheory]
    [InlineData("runtime")]
    [InlineData("state")]
    [InlineData("destination")]
    public void ExistingLinksAreRejectedAndTargetsStayUntouched(string place)
    {
        Skip.If(OperatingSystem.IsWindows(), "Native Unix link evidence only.");
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        Assert.Equal(EmbeddingsInstallStatus.Success, Install(f).Status);
        string target = Path.Combine(f.Workspace, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep"), "untouched");
        if (place == "runtime")
        {
            string runtime = Path.Combine(Destination(f), f.Manifest.Files[0].Name);
            File.Delete(runtime);
            File.CreateSymbolicLink(runtime, Path.Combine(target, "keep"));
        }
        else if (place == "state") Directory.CreateSymbolicLink(Path.Combine(Destination(f), "state"), target);
        else
        {
            // Move only the exact fixture payload to another fixture-owned location.
            Directory.Move(Destination(f), Path.Combine(f.Workspace, "original"));
            Directory.CreateSymbolicLink(Destination(f), target);
        }
        Assert.Equal(EmbeddingsInstallStatus.ExistingContentConflict, Install(f).Status);
        Assert.Equal(new[] { Path.Combine(target, "keep") }, Directory.GetFileSystemEntries(target));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(target, "keep")));
    }

    [Fact]
    public void InstallationCannotMutateTheBundleBySelectingHomeInsideSource()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        var before = Snapshot(f.Root);
        var result = EmbeddingsBundleInstaller.Install(f.Root, Path.Combine(f.Root, "home"), "0.6.0", Rid);
        Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, result.Status);
        Assert.Equal(before, Snapshot(f.Root));
    }

    [Fact]
    public void InstallationCannotUseBundleRootAsVersionParent()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        string source = Path.GetDirectoryName(Destination(f))!;
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.Move(f.Root, source);
        var before = Snapshot(source);
        var result = EmbeddingsBundleInstaller.Install(source, f.Home, "0.6.0", Rid);
        Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, result.Status);
        Assert.Equal(before, Snapshot(source));
        Assert.False(File.Exists(Path.Combine(f.Home, "plugins", ".scri-plugin-embeddings.install.lock")));
    }

    [Fact]
    public void SourceElsewhereUnderHomeIsAllowedWhenMutationPathsAreDisjoint()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        Directory.CreateDirectory(f.Home);
        string source = Path.Combine(f.Home, "downloads");
        Directory.Move(f.Root, source);
        var before = Snapshot(source);
        var result = EmbeddingsBundleInstaller.Install(source, f.Home, "0.6.0", Rid);
        Assert.Equal(EmbeddingsInstallStatus.Success, result.Status);
        Assert.Equal(before, Snapshot(source));
        AssertPayload(f);
    }

    [SkippableFact]
    public void SuppliedHomeAndSourceAliasesResolveToTheirCanonicalRoots()
    {
        Skip.If(OperatingSystem.IsWindows(), "Native Unix alias evidence only.");
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        Directory.CreateDirectory(f.Home);
        string sourceAlias = Path.Combine(f.Workspace, "source");
        string homeAlias = Path.Combine(f.Workspace, "home");
        Directory.CreateSymbolicLink(sourceAlias, f.Root);
        Directory.CreateSymbolicLink(homeAlias, f.Home);
        var result = EmbeddingsBundleInstaller.Install(sourceAlias, homeAlias, "0.6.0", Rid);
        Assert.Equal(EmbeddingsInstallStatus.Success, result.Status);
        Assert.Equal(Destination(f), result.Destination);
        AssertPayload(f);
    }

    [SkippableFact]
    public void ReplacedHomeAncestorCannotRedirectCopiesOutsideCanonicalHome()
    {
        Skip.If(OperatingSystem.IsWindows(), "Native Unix alias replacement evidence only.");
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        string parent = Path.Combine(f.Workspace, "parent");
        string moved = parent + "-moved";
        string outside = Path.Combine(f.Workspace, "outside");
        string home = Path.Combine(parent, "home");
        bool replaced = false;
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, _) =>
            {
                if (phase != EmbeddingsInstallPhase.BeforeCopy || replaced) return null;
                replaced = true;
                foreach (string directory in Directory.GetDirectories(parent, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(outside, Path.GetRelativePath(parent, directory)));
                Directory.Move(parent, moved);
                Directory.CreateSymbolicLink(parent, outside);
                return null;
            };
            var result = EmbeddingsBundleInstaller.Install(f.Root, home, "0.6.0", Rid);
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, result.Status);
            Assert.Empty(Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
        }
        finally
        {
            EmbeddingsBundleInstaller.FailureInjector = null;
            if (replaced)
            {
                Directory.Delete(parent);
                Directory.Move(moved, parent);
            }
        }
    }

    [SkippableFact]
    public void SourceRootReboundDuringAdmissionCannotMutateReplacementBundle()
    {
        Skip.If(OperatingSystem.IsWindows(), "Native Unix source alias replacement evidence only.");
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var replacement = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        string target = Path.GetDirectoryName(Destination(f))!;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(replacement.Root, target);
        string original = Path.Combine(f.Workspace, "original");
        var before = Snapshot(target);
        bool replaced = false;
        try
        {
            EmbeddingsBundleInstaller.FailureInjector = (phase, path) =>
            {
                if (phase == EmbeddingsInstallPhase.BeforeDirectorySync && path == f.Home && !replaced)
                {
                    Directory.Move(f.Root, original);
                    Directory.CreateSymbolicLink(f.Root, target);
                    replaced = true;
                }
                return null;
            };
            Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, Install(f).Status);
            Assert.True(replaced);
            Assert.Equal(before, Snapshot(target));
        }
        finally
        {
            EmbeddingsBundleInstaller.FailureInjector = null;
            if (replaced) { Directory.Delete(f.Root); Directory.Move(original, f.Root); }
        }
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, false)]
    [InlineData(2, false, true)]
    [InlineData(2, true, false)]
    [InlineData(2, true, true)]
    public void CancellationDuringValidationStopsReadsAndReleasesLock(int passToCancel, bool manifest, bool atEof)
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var ct = new CancellationTokenSource();
        int validationPass = 0;
        int readsAfterCancellation = 0;
        string cancelPath = manifest ? f.ManifestPath : Path.Combine(f.PayloadDirectory, f.Manifest.Files[0].Name);
        FileStream Open(string path)
        {
            if (path == f.ManifestPath) validationPass++;
            int openedDuringPass = validationPass;
            return new ObservedReadStream(path, count =>
            {
                if (ct.IsCancellationRequested) readsAfterCancellation++;
                if (path == cancelPath && openedDuringPass == passToCancel && (atEof ? count == 0 : count > 0)) ct.Cancel();
            });
        }
        var result = EmbeddingsBundleInstaller.Install(f.Root, f.Home, "0.6.0", Rid, Open, ct.Token);
        Assert.True(ct.IsCancellationRequested);
        Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, result.Status);
        Assert.Equal(0, readsAfterCancellation);
        Assert.False(Directory.Exists(Destination(f)));
        if (passToCancel == 1) Assert.False(Directory.Exists(f.Home));
        else
        {
            using var released = FileLock.AcquireExclusive(Path.Combine(f.Home, "plugins", ".scri-plugin-embeddings.install.lock"), TimeSpan.FromSeconds(1));
            Assert.NotNull(released);
        }
    }

    [Fact]
    public void CancellationWhileVerifyingIdenticalDestinationIsNotAContentConflict()
    {
        using var f = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        Assert.Equal(EmbeddingsInstallStatus.Success, Install(f).Status);
        var before = Snapshot(Destination(f));
        using var ct = new CancellationTokenSource();
        string cancelPath = Path.Combine(Destination(f), f.Manifest.Files[0].Name);
        FileStream Open(string path) => new ObservedReadStream(path, _ => { if (path == cancelPath) ct.Cancel(); });
        var result = EmbeddingsBundleInstaller.Install(f.Root, f.Home, "0.6.0", Rid, Open, ct.Token);
        Assert.True(ct.IsCancellationRequested);
        Assert.Equal(EmbeddingsInstallStatus.InstallationFailed, result.Status);
        Assert.Equal(before, Snapshot(Destination(f)));
        using var released = FileLock.AcquireExclusive(Path.Combine(f.Home, "plugins", ".scri-plugin-embeddings.install.lock"), TimeSpan.FromSeconds(1));
        Assert.NotNull(released);
    }

    private sealed class ObservedReadStream(string path, Action<int> afterRead)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            afterRead(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            afterRead(read);
            return read;
        }
    }

    private static void AssertPayload(EmbeddingsBundleTestFixture f)
    {
        foreach (var file in f.Manifest.Files)
            Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Destination(f), file.Name)))));
    }

    private static string[] Snapshot(string root) => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .Order(StringComparer.Ordinal).ToArray();
}
