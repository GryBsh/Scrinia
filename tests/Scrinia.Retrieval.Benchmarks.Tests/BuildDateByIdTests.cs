using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Regression coverage for <see cref="BenchmarkRunner.BuildDateById"/>: LongMemEval
/// lists the same session id twice in ~13/500 questions' haystacks, so the date-by-id
/// map must tolerate duplicate ids instead of throwing the way ToDictionary does.
/// </summary>
public class BuildDateByIdTests
{
    [Fact]
    public void Duplicate_ids_do_not_throw_and_last_wins()
    {
        var d1 = new DateTimeOffset(2023, 5, 8, 0, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2023, 6, 9, 0, 0, 0, TimeSpan.Zero);
        var docs = new List<HaystackDoc>
        {
            new("07b7a667_1", "first",  d1),
            new("other",      "x",      null),
            new("07b7a667_1", "second", d2), // duplicate id — must not throw
        };

        var map = BenchmarkRunner.BuildDateById(docs);

        map.Should().HaveCount(2);
        map["07b7a667_1"].Should().Be(d2, because: "last write wins on a duplicate id");
        map["other"].Should().BeNull();
    }

    [Fact]
    public void Ids_match_case_insensitively()
    {
        var docs = new List<HaystackDoc> { new("Session_A", "t", null) };

        var map = BenchmarkRunner.BuildDateById(docs);

        map.ContainsKey("session_a").Should().BeTrue();
    }
}
