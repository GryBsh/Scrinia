using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests;

public class SharedEnvelopePredicateTests
{
    [Fact]
    public void Partial_Envelope_IsWarning_And_NotError()
    {
        var envelope = SharedEnvelope.Partial(Array.Empty<object>(), Array.Empty<SharedWarning>());
        Assert.True(envelope.IsWarning);
        Assert.False(envelope.IsError);
    }

    [Fact]
    public void Error_Envelope_IsError_And_NotWarning()
    {
        var envelope = SharedEnvelope.CreateError("err", "code");
        Assert.True(envelope.IsError);
        Assert.False(envelope.IsWarning);
    }
}
