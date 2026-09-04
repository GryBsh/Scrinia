using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class WorkspaceConfigMutationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"scrinia_mutation_{Guid.NewGuid():N}");

    public WorkspaceConfigMutationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        DurableFile.FailureInjector = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task PreloadedConcurrentWritersRetainBothUnrelatedKeys()
    {
        WorkspaceConfig.Save(_root, new Dictionary<string, string>());
        WorkspaceConfig first = WorkspaceConfig.Load(_root);
        WorkspaceConfig second = WorkspaceConfig.Load(_root);
        using var ready = new Barrier(2);

        Task firstWrite = Task.Run(() =>
        {
            ready.SignalAndWait();
            first.SetValue("first", "one");
        });
        Task secondWrite = Task.Run(() =>
        {
            ready.SignalAndWait();
            second.SetValue("second", "two");
        });

        await Task.WhenAll(firstWrite, secondWrite);

        WorkspaceConfig.Load(_root).GetValue("first").Should().Be("one");
        WorkspaceConfig.Load(_root).GetValue("second").Should().Be("two");
    }

    [Fact]
    public void FailedAtomicMutationLeavesFileAndCacheUnchanged()
    {
        WorkspaceConfig.Save(_root, new Dictionary<string, string> { ["existing"] = "old" });
        WorkspaceConfig writer = WorkspaceConfig.Load(_root);
        DurableFile.FailureInjector = (phase, _) => phase == DurableWritePhase.BeforeMove
            ? new IOException("injected before move")
            : null;

        Action mutate = () => writer.SetValue("new", "value");

        mutate.Should().Throw<DurableWriteException>();
        WorkspaceConfig.Load(_root).GetValue("existing").Should().Be("old");
        WorkspaceConfig.Load(_root).GetValue("new").Should().BeNull();
        File.ReadAllText(Path.Combine(_root, ".scrinia", "config.json"))
            .Should().Contain("\"existing\": \"old\"");
    }
}
