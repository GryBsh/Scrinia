using FluentAssertions;
using Xunit;
using CoreJaccard = Scrinia.Core.Merge.KeywordJaccard;
using DriverJaccard = Scrinia.Merge.KeywordJaccard;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The Jaccard rule exists twice and must compute once. Both assemblies declare a type named
/// KeywordJaccard, so both are aliased here — an unqualified name does not compile.
/// </summary>
public sealed class KeywordJaccardParityTests
{
    public static TheoryData<string, string[]?, string[]?> Cases() => new()
    {
        { "both null",              null,                 null },
        { "both empty",             [],                   [] },
        { "one empty",              ["auth"],             [] },
        { "one null",               null,                 ["auth"] },
        { "identical",              ["auth", "jwt"],      ["auth", "jwt"] },
        { "disjoint",               ["auth"],             ["remote"] },
        { "partial overlap",        ["auth", "jwt"],      ["auth", "remote"] },
        { "casing differs",         ["Auth", "JWT"],      ["auth", "jwt"] },
        { "duplicates within side", ["auth", "auth"],     ["auth"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothImplementationsAgree(string caseName, string[]? a, string[]? b) =>
        DriverJaccard.Compute(a, b).Should().Be(CoreJaccard.Compute(a, b),
            because: $"[{caseName}] the AOT copy must compute exactly what the Core original does");
}
