using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Scrinia.Core.Configuration;
using Scrinia.Core.Models;

namespace Scrinia.Mcp.SharedContracts;

internal static class SharedValidation
{
    internal static async ValueTask<CallToolResult> Call(RequestContext<CallToolRequestParams> request,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next, CancellationToken ct)
    {
        if (request.MatchedPrimitive is McpServerTool tool)
        {
            JsonElement schema = tool.ProtocolTool.InputSchema;
            JsonElement properties = schema.GetProperty("properties");
            var args = request.Params?.Arguments;
            bool invalid = args?.Any(pair => !properties.TryGetProperty(pair.Key, out _) ||
                pair.Value.ValueKind == JsonValueKind.Null && pair.Key != "chunk") == true;
            if (schema.TryGetProperty("required", out var required))
                invalid |= required.EnumerateArray().Any(item => args is null || !args.ContainsKey(item.GetString()!));
            if (invalid) return InvalidCall();
        }
        try
        {
            CallToolResult result = await next(request, ct).ConfigureAwait(false);
            if (result.Content.OfType<TextContentBlock>().Any(block => block.Text.StartsWith("status: error\n", StringComparison.Ordinal)))
                result.IsError = true;
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        { return InvalidCall(); }
    }

    private static CallToolResult InvalidCall() => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = SharedEnvelope.CreateError("INVALID_PARAMETER", "The request is invalid.").ToYaml() }]
    };

    internal static void Text(string? value, bool optional = false, int maximumBytes = 4096)
    {
        if (value is null && optional) return;
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > maximumBytes)
            Invalid();
    }

    internal static void Range(int value, int min, int max)
    {
        if (value < min || value > max) Invalid();
    }

    internal static void Members(string[]? values, int maxCount = 1000, int maximumBytes = 4096)
    {
        if (values is null) return;
        if (values.Length > maxCount || values.Distinct(StringComparer.Ordinal).Count() != values.Length) Invalid();
        foreach (string value in values) Text(value, maximumBytes: maximumBytes);
    }

    internal static void Attributes(Dictionary<string, string[]>? values)
    {
        if (values is null) return;
        if (values.Count > 1000) Invalid();
        foreach (var pair in values)
        {
            Text(pair.Key);
            if (pair.Key.Length > 128 || pair.Value is null) Invalid();
            Members(pair.Value);
        }
    }

    internal static void Content(string[]? content, Dictionary<string, string[]>? attributes, string[]? keywords, bool append)
    {
        Attributes(attributes); Members(keywords);
        if (content is null || content.Length == 0 || content.Any(value => value is null) ||
            append && string.IsNullOrWhiteSpace(content[0])) Invalid();
        _ = Write(MemoryAddress.Parse("/validation"), content!, attributes, keywords, append);
    }

    internal static MemoryWrite Write(MemoryAddress address, string[] content,
        Dictionary<string, string[]>? attributes, string[]? keywords, bool append) =>
        MemoryWrite.Create(address, content,
            attributes?.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value)
                ?? new Dictionary<string, IReadOnlyList<string>>(), keywords ?? [], isAppend: append);

    internal static void Invalid() => throw new SharedOperationException("INVALID_PARAMETER");
}
