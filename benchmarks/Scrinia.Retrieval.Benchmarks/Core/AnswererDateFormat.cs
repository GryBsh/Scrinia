using System.Globalization;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Single source of truth for how a memory's created date is surfaced to the judge answerer,
/// mirroring the production search block's `created:` pseudo-attribute. Display/answerer only —
/// never fed to retrieval scoring. Shared by the id-mapped path (BenchmarkRunner), the agentic
/// path (AgenticScriniaRetriever), and the date-presence diagnostic so all produce identical framing.
/// </summary>
public static class AnswererDateFormat
{
    public const string Prefix = "created: ";

    public static string Apply(string text, DateTimeOffset? date) =>
        date is { } d ? $"{Prefix}{d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n{text}" : text;

    public static bool Present(string content) =>
        content.StartsWith(Prefix, StringComparison.Ordinal) || content.Contains("\n" + Prefix, StringComparison.Ordinal);
}
