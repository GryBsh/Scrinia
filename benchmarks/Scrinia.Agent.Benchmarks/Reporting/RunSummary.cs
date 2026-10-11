namespace Scrinia.Agent.Benchmarks;

public sealed record AccuracySummary(int Selected, int Answered, int Graded, int Correct,
    int ExecutionFailed, int GradingFailed, int Cancelled, double? Accuracy, double? EndToEndSuccess, bool Provisional);
public static class RunSummary
{
    public static AccuracySummary Compute(IReadOnlyList<string> selectedIds, IReadOnlyList<CaseOutcome> latestOutcomes)
    {
        var ids = selectedIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count != selectedIds.Count || latestOutcomes.Select(o => o.QuestionId).Distinct(StringComparer.Ordinal).Count() != latestOutcomes.Count ||
            latestOutcomes.Any(o => !ids.Contains(o.QuestionId)))
            throw new InvalidDataException("Summary identities must be unique and selected.");
        int graded = latestOutcomes.Count(o => o.State is CaseState.Correct or CaseState.Incorrect);
        int correct = latestOutcomes.Count(o => o.State == CaseState.Correct);
        return new(selectedIds.Count, latestOutcomes.Count(o => o.AnswerSha256 is not null), graded, correct,
            latestOutcomes.Count(o => o.State == CaseState.ExecutionFailed), latestOutcomes.Count(o => o.State == CaseState.GradingFailed),
            latestOutcomes.Count(o => o.State == CaseState.Cancelled), graded == 0 ? null : (double)correct / graded,
            selectedIds.Count == 0 ? null : (double)correct / selectedIds.Count, graded != selectedIds.Count);
    }
}
