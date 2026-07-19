using System.Text;

namespace Scrinia.Retrieval.Benchmarks.Judge;

public static class JudgePrompts
{
    public const string NotAnswerableSentinel = "NOT ANSWERABLE FROM CONTEXT";

    public static readonly string AnswererSystem =
        "You answer a question using ONLY the numbered context passages provided. " +
        "Base your answer strictly on that context — do not use outside knowledge. " +
        "If the context does not contain enough information to answer, reply exactly: " +
        $"{NotAnswerableSentinel}. Otherwise give the shortest correct answer (a few words).";

    public static string AnswererUser(string question, IReadOnlyList<string> retrievedContents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Context passages:");
        for (int i = 0; i < retrievedContents.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ");
            sb.AppendLine(retrievedContents[i]);
        }
        sb.AppendLine();
        sb.Append("Question: ").AppendLine(question);
        sb.Append("Answer:");
        return sb.ToString();
    }

    public const string GraderSystem =
        "You grade whether a candidate answer matches the gold answer for a question. " +
        "The candidate is correct if it conveys the same essential information as the gold answer, " +
        "even if worded differently or less completely. Minor phrasing, extra detail, or partial " +
        "specificity that is still consistent with the gold answer counts as correct. " +
        "Reply with exactly one word: correct or incorrect.";

    public static string GraderUser(string question, string candidateAnswer, string goldAnswer)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").AppendLine(question);
        sb.Append("Gold answer: ").AppendLine(goldAnswer);
        sb.Append("Candidate answer: ").AppendLine(candidateAnswer);
        sb.Append("Verdict (correct/incorrect):");
        return sb.ToString();
    }

    public static bool ParseGrade(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim().ToLowerInvariant();
        // Check "incorrect" first — it contains "correct" as a substring.
        if (s.Contains("incorrect")) return false;
        return s.Contains("correct");
    }
}
