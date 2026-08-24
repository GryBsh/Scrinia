using FluentAssertions;
using Scrinia.Tests.Benchmarks;
using Xunit;

namespace Scrinia.Tests;

public sealed class EmbeddingBenchmarkLaneTests
{
    [Fact]
    public void EmbeddingBenchmarkTests_AreMarkedAsLongRunningBenchmarks()
    {
        typeof(EmbeddingBenchmarkTests)
            .CustomAttributes
            .Should()
            .Contain(attribute =>
                attribute.AttributeType == typeof(TraitAttribute)
                && (string)attribute.ConstructorArguments[0].Value! == "Category"
                && (string)attribute.ConstructorArguments[1].Value! == "LongRunningBenchmark");
    }
}
