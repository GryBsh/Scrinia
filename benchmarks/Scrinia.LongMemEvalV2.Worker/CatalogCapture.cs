using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using Scrinia.Agent.Benchmarks;
using Scrinia.LongMemEvalV2.Worker.Protocol;

namespace Scrinia.LongMemEvalV2.Worker;

public sealed record CatalogCaptureRequest
{
    public required string Root { get; init; }
    public required string Executable { get; init; }
    public required string[] PrefixArguments { get; init; }
}

/// <summary>Captures the runtime's canonical MCP descriptors and guide without creating a model client.</summary>
public static class CatalogCapture
{
    public static async Task RunAsync(CatalogCaptureRequest request, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(request.Root) || !Path.IsPathFullyQualified(request.Executable)
            || request.PrefixArguments is null || request.PrefixArguments.Any(a => a is null)
            || Directory.Exists(request.Root) || File.Exists(request.Root))
            throw new InvalidDataException("INVALID_CAPTURE");
        PathSafety.RequireNoLinks(request.Root);
        PathSafety.RequireNoLinks(request.Executable);
        Directory.CreateDirectory(request.Root);
        var workspace = Path.Combine(request.Root, "workspace");
        var home = Path.Combine(request.Root, "home");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(home);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var settings = new ScriniaSettings(Path.GetDirectoryName(request.Executable)!, request.Executable,
            request.PrefixArguments, [], null);
        var paths = new AttemptPaths(request.Root, workspace, home, Path.Combine(request.Root, "unused.jsonl"));
        await using var events = new CaptureEvents(Path.Combine(request.Root, "tools.json"));
        await using var session = await ScriniaMcpSession.OpenAsync(settings, paths, events, deadline.Token);
        var guide = await session.Tools.Single(t => t.Name == "guide")
            .InvokeAsync(new AIFunctionArguments(), deadline.Token);
        var text = guide switch
        {
            string value => value,
            TextContent value => value.Text,
            IEnumerable<AIContent> values => string.Join("\n", values.OfType<TextContent>().Select(c => c.Text)),
            CallToolResult value when value.IsError != true => string.Join("\n", value.Content.OfType<TextContentBlock>().Select(c => c.Text)),
            _ => throw new InvalidDataException("GUIDE_CAPTURE_FAILED")
        };
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("GUIDE_CAPTURE_FAILED");
        await File.WriteAllTextAsync(Path.Combine(request.Root, "guide.txt"), text, deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(request.Root, "catalog.sha256"), session.CatalogHash + "\n", deadline.Token);
    }

    private sealed class CaptureEvents(string output) : IRunEvents
    {
        public async ValueTask WriteAsync(string phase, string kind, JsonElement data, CancellationToken ct)
        {
            if (phase == "mcp" && kind == "catalog")
            {
                if (File.Exists(output)) throw new InvalidDataException("DUPLICATE_CAPTURE");
                // Preserve the exact source-generated runtime event bytes, including null optional fields.
                await File.WriteAllTextAsync(output, data.GetProperty("tools").GetRawText(), ct);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
