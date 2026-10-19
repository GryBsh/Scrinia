using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Security.Cryptography;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public static class ToolCatalog
{
    public static string ComputeHash(IReadOnlyList<McpClientTool> tools) => ComputeProtocolHash(tools.Select(t => t.ProtocolTool).ToArray());
    internal static string ComputeProtocolHash(IReadOnlyList<Tool> tools)
    {
        if (tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new InvalidDataException("MCP tool names must be unique.");
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartArray();
            foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                WriteCanonical(writer, JsonSerializer.SerializeToElement(tool, RuntimeJsonContext.Default.Tool));
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    internal static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
