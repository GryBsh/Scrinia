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
    public async Task BatchAndSingleValueWritersRetainAllKeys()
    {
        WorkspaceConfig.Save(_root, new Dictionary<string, string> { ["existing"] = "preserve" });
        WorkspaceConfig singleWriter = WorkspaceConfig.Load(_root);
        using var ready = new Barrier(2);
        Task batchWrite = Task.Run(() =>
        {
            ready.SignalAndWait();
            WorkspaceConfig.SetValues(_root, new Dictionary<string, string>
            {
                ["plugins:embeddings"] = "scri-plugin-embeddings",
                ["plugins:embeddings:version"] = "0.6.0",
            });
        });
        Task singleWrite = Task.Run(() =>
        {
            ready.SignalAndWait();
            singleWriter.SetValue("unrelated", "fresh");
        });

        await Task.WhenAll(batchWrite, singleWrite);

        var values = WorkspaceConfig.LoadValuesUncached(_root);
        values.Should().Contain(new Dictionary<string, string>
        {
            ["existing"] = "preserve", ["unrelated"] = "fresh",
            ["plugins:embeddings"] = "scri-plugin-embeddings", ["plugins:embeddings:version"] = "0.6.0",
        });
    }

    [Fact]
    public void FailedAtomicBatchLeavesBothKeysFileAndCacheUnchanged()
    {
        WorkspaceConfig.Save(_root, new Dictionary<string, string>
        {
            ["plugins:embeddings"] = "previous", ["plugins:embeddings:version"] = "0.5.0",
        });
        byte[] before = File.ReadAllBytes(Path.Combine(_root, ".scrinia", "config.json"));
        DurableFile.FailureInjector = (phase, _) => phase == DurableWritePhase.BeforeMove
            ? new IOException("injected before batch move") : null;

        Action mutate = () => WorkspaceConfig.SetValues(_root, new Dictionary<string, string>
        {
            ["plugins:embeddings"] = "scri-plugin-embeddings", ["plugins:embeddings:version"] = "0.6.0",
        });

        mutate.Should().Throw<ScriniaConfigurationException>();
        File.ReadAllBytes(Path.Combine(_root, ".scrinia", "config.json")).Should().Equal(before);
        WorkspaceConfig.Load(_root).GetValue("plugins:embeddings").Should().Be("previous");
        WorkspaceConfig.Load(_root).GetValue("plugins:embeddings:version").Should().Be("0.5.0");
    }

    [Theory]
    [InlineData("", "value")]
    [InlineData("key", null)]
    public void InvalidBatchEntryDoesNotCreateConfiguration(string key, string? value)
    {
        Action mutate = () => WorkspaceConfig.SetValues(_root, new Dictionary<string, string>
        {
            ["valid-first"] = "must not be written", [key] = value!,
        });
        mutate.Should().Throw<ArgumentException>();
        Directory.Exists(Path.Combine(_root, ".scrinia")).Should().BeFalse();
    }

    [Fact]
    public void InvalidBatchArgumentsDoNotCreateConfiguration()
    {
        Action missingRoot = () => WorkspaceConfig.SetValues("", new Dictionary<string, string>());
        Action missingValues = () => WorkspaceConfig.SetValues(_root, null!);
        missingRoot.Should().Throw<ArgumentException>();
        missingValues.Should().Throw<ArgumentNullException>();
        Directory.Exists(Path.Combine(_root, ".scrinia")).Should().BeFalse();
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

        mutate.Should().Throw<ScriniaConfigurationException>()
            .Which.InnerException.Should().BeOfType<DurableWriteException>();
        WorkspaceConfig.Load(_root).GetValue("existing").Should().Be("old");
        WorkspaceConfig.Load(_root).GetValue("new").Should().BeNull();
        File.ReadAllText(Path.Combine(_root, ".scrinia", "config.json"))
            .Should().Contain("\"existing\": \"old\"");
    }
}
