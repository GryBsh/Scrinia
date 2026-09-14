using System.Reflection;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Plugin.Embeddings.Tests;

public sealed class EmbeddingsPluginObserverBoundaryTests
{
    [Fact]
    public void VulkanChildProcessHostExposesObserverAndSearchCapabilitiesOnly()
    {
        Type hostType = Assembly.Load("scri")
            .GetType("Scrinia.Services.McpPluginHost")
            ?? throw new InvalidOperationException("MCP plugin host type is missing.");

        hostType.GetInterfaces().Should().Contain(typeof(IMemoryEventSink));
        hostType.GetInterfaces().Should().Contain(typeof(ISearchScoreContributor));
        hostType.GetInterfaces().Should().NotContain(typeof(ITransactionalMemoryParticipant));
    }
}
