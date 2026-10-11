using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Scrinia.Agent.Benchmarks.Tests;

// Readable native .NET fixture: no generated shell scripts or encoded commands.
internal static class ProcessFixture
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--unix-path-proof"]) return UnixPathProof.Run();
        if (args.Length < 3 || args[0] != "--mcp-fixture" || args[1] is not ("hang" or "invalid" or "exit-after-guide")) return 2;
        var marker = Path.GetFullPath(args[2]);
        var directory = Path.GetDirectoryName(marker)!;
        // TEMP is deliberately replaced in the child environment. Validate the owned fixture
        // directory by its fixed parent and random identifier, not the child's TEMP value.
        if (Path.GetFileName(Path.GetDirectoryName(directory)) != "scrinia-agent-tests" ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || !Directory.Exists(directory) || File.Exists(marker)) return 2;
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(marker, self.Id.ToString(CultureInfo.InvariantCulture));
        if (args[1] == "invalid") { Console.WriteLine("invalid MCP handshake"); return 1; }
        if (args[1] == "exit-after-guide")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await Console.In.ReadLineAsync(timeout.Token) is { } line)
            {
                var request = JsonNode.Parse(line)!;
                if (request["id"] is null) continue;
                string? method = request["method"]?.GetValue<string>();
                if (method is not ("initialize" or "tools/list" or "tools/call" or "ping"))
                {
                    await Console.Out.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } }.ToJsonString());
                    await Console.Out.FlushAsync();
                    continue;
                }
                JsonNode result = method switch
                {
                    "initialize" => new JsonObject { ["protocolVersion"] = request["params"]!["protocolVersion"]!.DeepClone(),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = "scri", ["version"] = "fixture" } },
                    "tools/list" => JsonNode.Parse("""{"tools":[{"name":"guide","inputSchema":{"type":"object","properties":{}}}]}""")!,
                    "tools/call" => JsonNode.Parse("""{"content":[{"type":"text","text":"fixture guide"}]}""")!,
                    _ => new JsonObject()
                };
                await Console.Out.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
                await Console.Out.FlushAsync();
                if (method == "tools/call")
                {
                    while (!File.Exists(marker + ".exit")) await Task.Delay(10, timeout.Token);
                    return 0;
                }
            }
            return 0;
        }
        await Task.Delay(TimeSpan.FromSeconds(60));
        return 0;
    }
}
