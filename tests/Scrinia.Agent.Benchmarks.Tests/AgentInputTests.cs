using System.Text.Json;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class AgentInputTests
{
    [Fact]
    public async Task Preserves_assistant_turns_and_abstention_without_labels()
    {
        var result = await new LongMemEvalReader().LoadAsync(LongMemEvalReaderTests.Fixture, ["sample_abs"], CancellationToken.None);
        var item = Assert.Single(result.Cases);
        Assert.True(item.Evaluation.Abstention);
        Assert.Equal("2024/06/15 (Sat) 09:00", item.Answer.Date);
        Assert.Equal("What is my confirmation code?", item.Answer.Question);
        Assert.Equal("REFERENCE_ONLY", item.Evaluation.ReferenceAnswer);
        Assert.Equal("I prefer the morning train.", item.Sessions[0].Turns[0].Content);
        Assert.Equal("I can check the timetable.", item.Sessions[0].Turns[1].Content);
        Assert.Equal("assistant", item.Sessions[0].Turns[1].Role);
        Assert.Equal([1, 2], item.Sessions.Select(s => s.Ordinal));
        var projected = JsonSerializer.Serialize(item.Sessions, LongMemEvalJsonContext.Default.IReadOnlyListLearningSession);
        foreach (string sentinel in new[] { "REFERENCE_ONLY", "answer_evidence", "has_answer", "sample_abs", "confirmation code", "2024/06/15" })
            Assert.DoesNotContain(sentinel, projected);
    }
}
