using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class AtomicConfigWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"scrinia_atomic_config_{Guid.NewGuid():N}");

    public AtomicConfigWriteTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void WorkspaceWriteFailurePreservesOriginalBytesAndCleansTemporarySiblings()
    {
        WorkspaceConfig.Save(_root, new Dictionary<string, string> { ["existing"] = "old" });
        string path = Path.Combine(_root, ".scrinia", "config.json");
        byte[] originalBytes = File.ReadAllBytes(path);
        WorkspaceConfig writer = WorkspaceConfig.Load(_root);
        DurableFile.FailureInjector = FailBeforeMove;

        Action write = () => writer.SetValue("new", "value");

        var failure = write.Should().Throw<ScriniaConfigurationException>().Which;
        failure.InnerException.Should().BeOfType<DurableWriteException>();
        AssertConfigUnchanged(path, originalBytes);
        AssertConfigArtifactsClean(path);
    }

    [Fact]
    public void HomeWriteFailurePreservesOriginalBytesAndCleansTemporarySiblings()
    {
        var store = new HomeConfigStore();
        var original = new HomeConfig { SchemaVersion = 1, SharedBases = new SharedBasesMap() };
        store.Save(_root, original);
        string path = Path.Combine(_root, "config.json");
        byte[] originalBytes = File.ReadAllBytes(path);
        DurableFile.FailureInjector = FailBeforeMove;

        Action write = () => store.Save(_root, new HomeConfig
        {
            SchemaVersion = 1,
            SharedBases = new SharedBasesMap
            {
                ["team"] = new SharedBaseEntry { Root = Path.Combine(_root, "team") },
            },
        });

        write.Should().Throw<ScriniaConfigurationException>();
        AssertConfigUnchanged(path, originalBytes);
        AssertConfigArtifactsClean(path);
    }

    [Fact]
    public void ConfigWriteFailureMapsToWriteFailureAndPreservesExceptionDetails()
    {
        var exception = new ScriniaConfigurationException("write failed", new IOException("injected"));

        ConfigurationResult result = ConfigResults.ConfigWriteFailed(exception);

        result.ErrorCode.Should().Be(ConfigurationErrorCode.ConfigWriteFailed);
        result.ExitClass.Should().Be(ConfigurationExitClass.WriteFailure);
        result.Exception.Should().BeSameAs(exception);
    }

    [Fact]
    public void SuccessfulConfigWritesLeaveNoTemporaryOrConfigLockArtifacts()
    {
        string workspaceConfig = Path.Combine(_root, ".scrinia", "config.json");
        WorkspaceConfig.Save(_root, new Dictionary<string, string> { ["key"] = "value" });
        AssertConfigArtifactsClean(workspaceConfig);

        string homeConfig = Path.Combine(_root, "config.json");
        new HomeConfigStore().Save(_root, new HomeConfig { SchemaVersion = 1, SharedBases = new SharedBasesMap() });
        AssertConfigArtifactsClean(homeConfig);
    }

    public void Dispose()
    {
        DurableFile.FailureInjector = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static IOException? FailBeforeMove(DurableWritePhase phase, string _) =>
        phase == DurableWritePhase.BeforeMove ? new IOException("injected before move") : null;

    private static void AssertConfigUnchanged(string path, byte[] originalBytes)
    {
        File.Exists(path).Should().BeTrue();
        File.ReadAllBytes(path).Should().Equal(originalBytes);
        TemporarySiblings(path).Should().BeEmpty();
    }

    private static void AssertConfigArtifactsClean(string path)
    {
        TemporarySiblings(path).Should().BeEmpty();
        File.Exists(path + ".lock").Should().BeFalse();
    }

    private static IEnumerable<string> TemporarySiblings(string destination)
    {
        string directory = Path.GetDirectoryName(destination)!;
        string prefix = Path.GetFileName(destination) + ".";
        return Directory.EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)
                && path.EndsWith(".tmp", StringComparison.Ordinal));
    }
}
