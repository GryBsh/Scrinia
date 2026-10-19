using System.Text.Json;
using Microsoft.Extensions.AI;
using Scrinia.Agent.Benchmarks;
using Scrinia.LongMemEvalV2.Worker.Evidence;
using Scrinia.LongMemEvalV2.Worker.Protocol;
namespace Scrinia.LongMemEvalV2.Worker.Learning;

public sealed class SourceSeeder
{
    public static string ContentFor(SourceCatalog catalog, SourceFragment fragment) =>
        JsonSerializer.Serialize(catalog.Materialize(fragment),WorkerJsonContext.Default.SourceMemory);

    public Task SeedAsync(SourceCatalog catalog, ScriniaMcpSession session, CancellationToken ct) =>
        SeedAsync(catalog,session.Tools.Single(t=>t.Name=="remember"),ct);
    public async Task SeedAsync(SourceCatalog catalog, AIFunction remember, CancellationToken ct)
    {
        // The complete original trajectory remains available even for fields the upstream simplifier omits.
        await WriteAsync(remember, "/source/" + catalog.Trajectory.SourceHash + "/trajectory",
            catalog.Trajectory.OriginalJson, "Original trajectory source metadata, dates and ordered state references.", ct);
        foreach (var fragment in catalog.Fragments)
        {
            ct.ThrowIfCancellationRequested();
            var state = catalog.Trajectory.States[fragment.StateIndex];
            await WriteAsync(remember, SourceCatalog.SourcePath(catalog.Trajectory.SourceHash,fragment),
                ContentFor(catalog,fragment),
                $"Original trajectory state {state.StateIndex}, step {state.Step}, fragment {fragment.FragmentIndex}; {state.Url}",ct);
        }
    }
    private static bool IsError(object? value) => value switch {
        ModelContextProtocol.Protocol.CallToolResult result => result.IsError == true,
        TextContent text => text.Text.TrimStart().StartsWith("status: error",StringComparison.Ordinal),
        IEnumerable<AIContent> contents => contents.Any(IsError),
        JsonElement element when element.ValueKind == JsonValueKind.Object =>
            (element.TryGetProperty("isError",out var error) && error.ValueKind==JsonValueKind.True)
            || (element.TryGetProperty("status",out var status) && status.GetString()=="error")
            || (element.TryGetProperty("content",out var content) && IsError(content))
            || (element.TryGetProperty("text",out var text) && text.ValueKind==JsonValueKind.String
                && text.GetString()!.TrimStart().StartsWith("status: error",StringComparison.Ordinal)),
        JsonElement element when element.ValueKind == JsonValueKind.Array => element.EnumerateArray().Any(e=>IsError(e)),
        _ => false
    };
    private static async Task WriteAsync(AIFunction remember,string path,string content,string description,CancellationToken ct)
    {
        // A transport failure can follow a committed write. Never retry it.
        object? response;
        try {
            response = await remember.InvokeAsync(new AIFunctionArguments {
                ["path"]=path, ["content"]=StorageChunks.Split(content), ["description"]=description
            },ct);
        } catch (Exception error) when (error is not OperationCanceledException) {
            throw new BenchmarkFailureException(BenchmarkFailureCode.TOOL_COMPLETION_UNCERTAIN,
                "Source seeding completion is uncertain.",error);
        }
        if (IsError(response))
            throw new InvalidDataException("SOURCE_SEED_FAILED");
    }
}
