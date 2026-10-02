using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Commands;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public sealed class CliMcpMigrationParityTests
{
    public static IEnumerable<object[]> StrictFailures() => StrictSafetyErrorMappingTests.StrictFailures();

    [Theory]
    [MemberData(nameof(StrictFailures))]
    public void Cli_and_mcp_envelopes_preserve_the_same_machine_code(Exception failure, string expectedCode)
    {
        McpResponse mcp = ResponseBuilder.Error(failure);
        string yaml = mcp.ToYaml();

        Assert.Equal(expectedCode, mcp.ErrorCode);
        Assert.Contains($"errorCode: {expectedCode}", yaml, StringComparison.Ordinal);

        string cliJson = System.Text.Json.JsonSerializer.Serialize(
            new CliErrorOutput(mcp.Error ?? "unknown", Code: mcp.ErrorCode),
            CliJsonContext.Default.CliErrorOutput);
        using var document = System.Text.Json.JsonDocument.Parse(cliJson);
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
    }
}
