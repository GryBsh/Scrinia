using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

public sealed class PluginAddressPropagationTests
{
    [Fact]
    public void Hook_contexts_carry_only_the_canonical_memory_address()
    {
        foreach (var contextType in new[]
        {
            typeof(BeforeStoreContext), typeof(AfterStoreContext),
            typeof(BeforeAppendContext), typeof(AfterAppendContext),
            typeof(BeforeForgetContext), typeof(AfterForgetContext),
        })
        {
            contextType.GetProperty("Address")!.PropertyType.Should().Be<MemoryAddress>();
            contextType.GetProperty("Name").Should().BeNull();
            contextType.GetProperty("QualifiedName").Should().BeNull();
        }
    }
}
