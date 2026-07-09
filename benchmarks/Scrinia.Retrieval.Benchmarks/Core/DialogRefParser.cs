using System.Text.RegularExpressions;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Parses LoCoMo dialog-id citations (<c>D{session}:{index}</c>) out of arbitrary
/// agent-written memory content, in every form the model emits: bare/inline ids,
/// comma-separated lists (<c>D2:1, D2:3</c>), and same-session ranges
/// (<c>D9:1-D9:17</c> or <c>D9:1-17</c>). Pure and dataset-shape-only: it does NOT
/// check ids against real docs — the caller resolves them. Ids are returned
/// de-duplicated in first-seen order.
/// </summary>
public static partial class DialogRefParser
{
    // D{n}:{a}, with an optional range tail "- D{m}:{b}" or "- {b}" (spaces allowed).
    // Group 1 = n, 2 = a, 3 = m (range-end session, optional), 4 = b (range end, optional).
    [GeneratedRegex(@"(?<![A-Za-z0-9])D(\d+):(\d+)(?:\s*-\s*(?:D(\d+):)?(\d+))?")]
    private static partial Regex DialogIdPattern();

    public static IReadOnlyList<string> ExtractDialogIds(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        void Add(string id)
        {
            if (seen.Add(id)) result.Add(id);
        }

        foreach (Match m in DialogIdPattern().Matches(content))
        {
            int n = int.Parse(m.Groups[1].Value);
            int a = int.Parse(m.Groups[2].Value);

            if (m.Groups[4].Success)
            {
                int endSession = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : n;
                int b = int.Parse(m.Groups[4].Value);
                if (endSession == n && b >= a)
                {
                    for (int i = a; i <= b; i++) Add($"D{n}:{i}");
                }
                else
                {
                    // Cross-session or descending range → emit the two endpoints only.
                    Add($"D{n}:{a}");
                    Add($"D{endSession}:{b}");
                }
            }
            else
            {
                Add($"D{n}:{a}");
            }
        }

        return result;
    }
}
