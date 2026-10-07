using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Commands;

namespace Scrinia.Tests;

public sealed class ConfigCommandDurabilityTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_config_command_{Guid.NewGuid():N}");

    public ConfigCommandDurabilityTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public async Task ConfigWriteFailureEmitsStructuredCodeAndExitClassFour()
    {
        DurableFile.FailureInjector = FailBeforeMove;
        using var output = new StringWriter();
        TextWriter originalOutput = Console.Out;
        Console.SetOut(output);

        try
        {
            int exit = await new ScriniaCommands().Config(
                key: "Scrinia:Workspace:Tracking",
                value: "local",
                workspaceRoot: _workspace,
                json: true);

            exit.Should().Be(4);
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            JsonElement root = document.RootElement;
            root.GetProperty("code").GetString().Should().Be("CONFIG_WRITE_FAILED");
            root.GetProperty("exitClass").GetInt32().Should().Be(4);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    public void Dispose()
    {
        DurableFile.FailureInjector = null;
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private static IOException? FailBeforeMove(DurableWritePhase phase, string _) =>
        phase == DurableWritePhase.BeforeMove ? new IOException("injected before move") : null;
}
