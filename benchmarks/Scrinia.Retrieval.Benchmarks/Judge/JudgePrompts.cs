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

    /// <summary>
    /// Reads the grader's one-word verdict. <c>true</c> = correct, <c>false</c> = incorrect,
    /// <c>null</c> = no parseable verdict (excluded from the aggregate and drained to the
    /// failure sink, exactly like a failed call — never silently scored).
    /// </summary>
    /// <remarks>
    /// <para>This replaced a substring parser (<c>Contains("incorrect")</c> first, else
    /// <c>Contains("correct")</c>) that was wrong in three ways, all silent because the return
    /// type could not express "unreadable":</para>
    /// <list type="bullet">
    /// <item>An echoed rubric — <c>GraderUser</c> ends with "Verdict (correct/incorrect):", and a
    /// grader that restates it before answering emits BOTH tokens, so checking "incorrect" first
    /// scored an echoed <em>correct</em> verdict as incorrect. Measured over the 3,079 cached
    /// grader replies, this shape had fired twice — the only failure mode that reached production.</item>
    /// <item>A negated verdict — "this is not correct" contains "correct" and not "incorrect",
    /// so it scored CORRECT.</item>
    /// <item>Free prose containing neither token scored INCORRECT, indistinguishable from a
    /// genuine wrong answer.</item>
    /// </list>
    /// <para>Two rules fix all three. Read only what follows the LAST colon, which discards an
    /// echoed rubric and the reasoning in a chain-of-thought reply; then match the remainder
    /// EXACTLY rather than by substring, because <c>GraderSystem</c> mandates "exactly one word".
    /// Anything off-contract is a null, not a guess: the grader prompt is the contract, and a
    /// reply that breaks it is evidence about the grader, not about the answer.</para>
    /// </remarks>
    public static bool? ParseGrade(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim().ToLowerInvariant();
        int lastColon = s.LastIndexOf(':');
        if (lastColon >= 0) s = s[(lastColon + 1)..];
        s = s.Trim(' ', '\t', '\r', '\n', '.', '!', '"', '\'', '*', '`');
        return s switch { "correct" => true, "incorrect" => false, _ => null };
    }
}
