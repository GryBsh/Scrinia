using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class DurableWritePhaseMappingTests
{
    [Theory]
    [MemberData(nameof(AllPhasePairs))]
    public void MapDurableWritePhase_RoundTripsEveryPhase(
        object phase, object expected) =>
        B0FaultSeam.MapDurableWritePhase((DurableWritePhase)phase)
            .Should().Be((B0FaultSeam.FaultPhase)expected);

    public static IEnumerable<object[]> AllPhasePairs() =>
    [
        new object[] { DurableWritePhase.BeforeWrite, B0FaultSeam.FaultPhase.BeforeWrite },
        new object[] { DurableWritePhase.BeforeFlush, B0FaultSeam.FaultPhase.BeforeFlush },
        new object[] { DurableWritePhase.BeforeMove, B0FaultSeam.FaultPhase.BeforeMove },
        new object[] { DurableWritePhase.BeforeDirSync, B0FaultSeam.FaultPhase.BeforeDirSync },
    ];
}
