using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;

namespace Scrinia.Agent.Benchmarks;

/// <summary>Conservative recovery signal for reasoning-only responses that exhausted output.</summary>
internal sealed record ReasoningLoop(int ReasoningCharacters, int RepeatedCharacters, int MaximumRepetitions)
{
    internal double RepeatedFraction => (double)RepeatedCharacters / ReasoningCharacters;

    internal static ReasoningLoop? Detect(ChatResponse response)
    {
        if (response.FinishReason != ChatFinishReason.Length ||
            response.Messages.Any(m => m.Role != ChatRole.Assistant))
            return null;

        var contents = response.Messages.SelectMany(m => m.Contents).ToArray();
        // A partial answer, function call, or opaque content is not safe to treat as this failure.
        if (contents.Any(c => c is not TextReasoningContent &&
            (c is not TextContent text || !string.IsNullOrWhiteSpace(text.Text))))
            return null;

        string reasoning = string.Join("\n\n", contents.OfType<TextReasoningContent>().Select(c => c.Text));
        if (reasoning.Length < 4096) return null;
        var paragraphs = Regex.Split(reasoning, @"\r?\n\s*\r?\n", RegexOptions.NonBacktracking)
            .Select(p => Regex.Replace(p, @"\s+", " ", RegexOptions.NonBacktracking).Trim())
            .Where(p => p.Length > 0).ToArray();
        int total = paragraphs.Sum(p => p.Length);
        if (total < 4096) return null;
        var repeated = paragraphs.Where(p => p.Length >= 160)
            .GroupBy(p => p, StringComparer.Ordinal).Select(g => (Length: g.Key.Length, Count: g.Count()))
            .Where(g => g.Count >= 6).ToArray();
        // Count only copies after the first occurrence, not all text in repeated paragraphs.
        int duplicateCharacters = repeated.Sum(g => g.Length * (g.Count - 1));
        if (duplicateCharacters < total * .60) return null;
        return new(total, duplicateCharacters, repeated.Max(g => g.Count));
    }
}
