using System.Globalization;
using System.Text;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

/// <summary>Renders a <see cref="FactsResult"/> into the standard McpResponse YAML (action: facts).
/// Data results are `success`; disambiguation / not-found are `warning` with actionable guidance.</summary>
public static class FactsResponseFormatter
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static string Date(DateTimeOffset d) => d.ToString("yyyy-MM-dd", Ci);
    private static string Key(EntityRef e) => $"{e.Key}:{e.Value}";

    public static string ToYaml(FactsResult result) => result switch
    {
        EntitiesListResult r => Success(Entities(r)),
        ProfileResult r => Success(Profile(r)),
        IntersectionResult r => Success(Intersection(r)),
        DisambiguationResult r => Warning(Disambiguation(r)),
        NotFoundResult r => Warning($"not found: no entity named \"{r.Arg}\""),
        _ => Success("no result"),
    };

    private static string Success(string content) =>
        ResponseBuilder.Success(content).WithAction("facts").ToYaml();
    private static string Warning(string content) =>
        ResponseBuilder.Warning(content).WithAction("facts").ToYaml();

    private static string Entities(EntitiesListResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Ci, $"entities: showing {r.Items.Count} of {r.Total}\n");
        foreach (var i in r.Items)
            sb.Append(Ci, $"  {Key(i.Entity)} ({i.Count})\n");
        return sb.ToString().TrimEnd();
    }

    private static string Profile(ProfileResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Ci, $"profile: {Key(r.Entity)}\n");
        sb.Append(Ci, $"  mentions: {r.MentionCount}\n");
        if (r.First is { } f && r.Last is { } l)
            sb.Append(Ci, $"  span: {Date(f)} → {Date(l)}\n");
        if (r.Neighbors.Count > 0)
            sb.Append(Ci, $"  co-occurring: {string.Join(", ", r.Neighbors.Select(n => $"{Key(n.Entity)} ({n.Count})"))}\n");
        sb.Append(Ci, $"  timeline ({r.Timeline.Count}{(r.TimelineTruncated ? "+" : "")}):\n");
        foreach (var t in r.Timeline)
            sb.Append(Ci, $"    {Date(t.Date)}  {t.MemoryId}\n");
        return sb.ToString().TrimEnd();
    }

    private static string Intersection(IntersectionResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Ci, $"intersect: {string.Join(" AND ", r.Members.Select(Key))}\n");
        sb.Append(Ci, $"  memories: {r.Count}\n");
        foreach (var m in r.Memories)
            sb.Append(Ci, $"    {Date(m.Date)}  {m.MemoryId}\n");
        return sb.ToString().TrimEnd();
    }

    private static string Disambiguation(DisambiguationResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Ci, $"ambiguous: \"{r.Arg}\" matches {r.Candidates.Count} entities — specify key:value\n");
        foreach (var c in r.Candidates)
            sb.Append(Ci, $"  {Key(c.Entity)} ({c.Count})\n");
        return sb.ToString().TrimEnd();
    }
}
