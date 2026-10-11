using System.Diagnostics;
using System.Globalization;

namespace Scrinia.Agent.Benchmarks.Tests;

// Readable native .NET fixture: no generated shell scripts or encoded commands.
internal static class ProcessFixture
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 3 || args[0] != "--mcp-fixture" || args[1] is not ("hang" or "invalid")) return 2;
        var marker = Path.GetFullPath(args[2]);
        var directory = Path.GetDirectoryName(marker)!;
        // TEMP is deliberately replaced in the child environment. Validate the owned fixture
        // directory by its fixed parent and random identifier, not the child's TEMP value.
        if (Path.GetFileName(Path.GetDirectoryName(directory)) != "scrinia-agent-tests" ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || !Directory.Exists(directory) || File.Exists(marker)) return 2;
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(marker, self.Id.ToString(CultureInfo.InvariantCulture));
        if (args[1] == "invalid") { Console.WriteLine("invalid MCP handshake"); return 1; }
        await Task.Delay(TimeSpan.FromSeconds(60));
        return 0;
    }
}
