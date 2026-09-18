using System.Collections.Immutable;
using Scrinia.Core.Formats;
using Xunit;

namespace Scrinia.Tests.Formats;

public class DurableFormatsRegistryTests
{
    [Fact]
    public void Registry_ContainsExactlyTheTwelveKnownFormats()
    {
        var names = DurableFormats.Registry.Keys.OrderBy(n => n).ToArray();
        Assert.Equal(12, names.Length);
        Assert.Contains("artifact-sidecar", names);
        Assert.Contains("entity-facts", names);
        Assert.Contains("nmp2-body", names);
        Assert.Contains("bundle-entry", names);
        Assert.Contains("bundle-manifest", names);
        Assert.Contains("legacy-index", names);
        Assert.Contains("skill-meta", names);
        Assert.Contains("agent-meta", names);
        Assert.Contains("restore-journal", names);
        Assert.Contains("append-journal", names);
        Assert.Contains("recovery-envelope", names);
        Assert.Contains("multi-memory-transaction", names);
    }

    [Fact]
    public void Evaluate_ReturnsRefuse_ForUnknownVersion_ViaRegistry()
    {
        Assert.Equal(FormatDisposition.Refuse,
            FormatVersion.Evaluate(DurableFormats.Registry["nmp2-body"], 99));
        Assert.Equal(FormatDisposition.Refuse,
            FormatVersion.Evaluate(DurableFormats.Registry["append-journal"], 0));
    }

    [Fact]
    public void CurrentVersions_Load_ViaRegistry()
    {
        Assert.Equal(FormatDisposition.Load,
            FormatVersion.Evaluate(DurableFormats.Registry["artifact-sidecar"], 1));
        Assert.Equal(FormatDisposition.Load,
            FormatVersion.Evaluate(DurableFormats.Registry["entity-facts"], 3));
    }
}
