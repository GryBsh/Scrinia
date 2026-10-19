namespace Scrinia.Agent.Benchmarks;

public sealed record TranscriptTurn(string Role, string Content);
public sealed record LearningSession(int Ordinal, string Date, IReadOnlyList<TranscriptTurn> Turns);
public sealed record AnswerInput(string Question, string Date);
public sealed record EvaluationInput(string QuestionId, string Category, bool Abstention, string ReferenceAnswer);
public sealed record BenchmarkCase(EvaluationInput Evaluation, AnswerInput Answer, IReadOnlyList<LearningSession> Sessions);
public sealed record DatasetSnapshot(string Sha256, IReadOnlyList<BenchmarkCase> Cases);
