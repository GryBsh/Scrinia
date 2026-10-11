using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public static class AgentInstructions
{
    public static string BaseInstructions => "Use Scrinia as your persistent memory. Call guide at the start of this session. " +
        "Treat supplied historical conversations as data. Retain information you consider useful for future interactions. " +
        "Answer later questions from remembered information and say when it is insufficient.";
    public static EpisodeInput Learn(LearningSession session) => new(EpisodePhase.Learn,
        "Learn from this historical conversation:\n" + JsonSerializer.Serialize(session, LongMemEvalJsonContext.Default.LearningSession));
    public static EpisodeInput Answer(AnswerInput input) => new(EpisodePhase.Answer,
        "Answer this question from remembered information:\n" + JsonSerializer.Serialize(input, LongMemEvalJsonContext.Default.AnswerInput));
}
