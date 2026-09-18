using System.Text.Json;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class B0ResultFacadeMappingTests
{
    [Fact]
    public void B0Result_RoundTripsThroughScriniaMcpJsonContext()
    {
        B0Result result = new(
            B0Outcome.Appended, null, null, "op-1", "op-1", null, "warn");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            result, ScriniaMcpJsonContext.Default.B0Result);
        B0Result decoded = JsonSerializer.Deserialize(json, ScriniaMcpJsonContext.Default.B0Result)!;

        Assert.Equal(B0Outcome.Appended, decoded.Outcome);
        Assert.Equal("op-1", decoded.CorrelationId);
        Assert.Equal("warn", decoded.Warning);
    }
}
