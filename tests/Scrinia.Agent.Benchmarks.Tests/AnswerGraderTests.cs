using System.Net;
using Microsoft.Extensions.AI;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class AnswerGraderTests
{
    [Theory]
    [InlineData("yes", true)]
    [InlineData("Yes.", true)]
    [InlineData("No.", false)]
    [InlineData("yes. no.", null)]
    [InlineData("no. explanation", null)]
    [InlineData(" NO\n", false)]
    [InlineData("yes or no", null)]
    [InlineData("not yes", null)]
    public async Task Judge_is_tool_free_and_raw_reply_precedes_verdict(string raw, bool? expected)
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(ScriptedChatClient.Text(raw));
        var grader = new AnswerGrader(_ => fake);
        var grade = await grader.GradeAsync(new("evaluation-only", "single-session-user", false, "Reference"),
            new("Question", "2024-06-15"), RunJournalTests.Answer("Candidate"), ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, CancellationToken.None);
        Assert.Equal(expected, grade.Correct);
        Assert.Equal(raw, grade.RawResponse);
        Assert.Equal(expected is null ? "JUDGE_INVALID_VERDICT" : null, grade.FailureCode);
        var request = Assert.Single(fake.Requests);
        Assert.True(request.Options!.Tools is null || request.Options.Tools.Count == 0);
        Assert.Single(request.Messages);
        Assert.DoesNotContain("evaluation-only", request.Messages[0].Text);
        Assert.DoesNotContain(".scrinia", request.Messages[0].Text);
        var kinds = events.Items.Select(e => e.Kind).ToList();
        Assert.True(kinds.IndexOf("judge-raw") < kinds.IndexOf("judge-verdict"));
        Assert.True(fake.Disposed);
        Assert.Null(grade.Usage.InputTokens);
    }

    [Fact]
    public async Task Failed_judge_returns_failure_without_modifying_the_saved_answer()
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(new HttpRequestException("failed", null, HttpStatusCode.BadRequest));
        var answer = RunJournalTests.Answer("Candidate");
        var grade = await new AnswerGrader(_ => fake).GradeAsync(new("id", "single-session-user", false, "Reference"), new("Question", "date"), answer,
            ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, CancellationToken.None);
        Assert.Null(grade.Correct);
        Assert.Equal("JUDGE_REQUEST_FAILED", grade.FailureCode);
        AtomicJsonFile.ValidateAnswerHash(answer.Text, answer.Sha256);
    }

    [Fact]
    public async Task Cancelled_judge_does_not_return_a_verdict()
    {
        await using var events = new CapturingEvents();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var fake = new ScriptedChatClient(ScriptedChatClient.Text("yes"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnswerGrader(_ => fake).GradeAsync(
            new("id", "single-session-user", false, "Reference"), new("Question", "date"), RunJournalTests.Answer(),
            ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, cancelled.Token));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Tool_request_from_judge_is_a_grading_failure_even_with_yes_text()
    {
        await using var events = new CapturingEvents();
        var fake = new ScriptedChatClient(ScriptedChatClient.Call("bad", "recall", [], "yes"));
        var grade = await new AnswerGrader(_ => fake).GradeAsync(new("id", "single-session-user", false, "Reference"),
            new("Question", "date"), RunJournalTests.Answer(), ObservedChatClientTests.Settings, ObservedChatClientTests.Limits, events, CancellationToken.None);
        Assert.Null(grade.Correct);
        Assert.Equal("JUDGE_TOOL_CALL", grade.FailureCode);
    }
}
