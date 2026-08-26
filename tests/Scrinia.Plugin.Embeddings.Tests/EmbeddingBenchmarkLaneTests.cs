using FluentAssertions;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

public sealed class EmbeddingBenchmarkLaneTests
{
    [Fact]
    public void EmbeddingBenchmarkAllTests_AreMarkedAsLongRunningBenchmarks()
    {
        typeof(EmbeddingBenchmarkAllTests)
            .CustomAttributes
            .Should()
            .Contain(attribute =>
                attribute.AttributeType == typeof(TraitAttribute)
                && (string)attribute.ConstructorArguments[0].Value! == "Category"
                && (string)attribute.ConstructorArguments[1].Value! == "LongRunningBenchmark");
    }
}
