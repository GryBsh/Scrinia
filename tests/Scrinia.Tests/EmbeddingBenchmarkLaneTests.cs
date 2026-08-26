using FluentAssertions;
using Scrinia.Tests.Benchmarks;
using Xunit;

namespace Scrinia.Tests;

public sealed class EmbeddingBenchmarkLaneTests
{
    [Fact]
    public void CoreBenchmarkClasses_AreMarkedAsLongRunningBenchmarks()
    {
        Type[] benchmarkClasses =
        [
            typeof(ColdStartBenchmarkTests),
            typeof(ContextEfficiencyBenchmarkTests),
            typeof(EmbeddingBenchmarkTests),
            typeof(IsolationBenchmarkTests),
            typeof(NeedleInHaystackBenchmarkTests),
            typeof(ScalingBenchmarkTests),
            typeof(SearchPrecisionBenchmarkTests),
            typeof(UpdateCorrectnessBenchmarkTests),
            typeof(Nmp2BenchmarkTests),
        ];

        benchmarkClasses.Should().OnlyContain(benchmarkClass => IsLongRunningBenchmark(benchmarkClass));
    }

    private static bool IsLongRunningBenchmark(Type benchmarkClass) =>
        benchmarkClass.CustomAttributes.Any(attribute =>
            attribute.AttributeType == typeof(TraitAttribute)
            && (string)attribute.ConstructorArguments[0].Value! == "Category"
            && (string)attribute.ConstructorArguments[1].Value! == "LongRunningBenchmark");
}
