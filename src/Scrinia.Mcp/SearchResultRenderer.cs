using System.Text;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

/// <summary>
/// Renders ranked <see cref="SearchResult"/>s into the plain-text body that the MCP search
/// response carries inside its `content: |` block. Formatting only — no store access beyond
/// name qualification, no ranking, no filtering.
/// </summary>
internal static class SearchResultRenderer
{
    private const int MaxDescriptionChars = 1500;
    private const int MaxAnchorsPerResult = 7;

    /// <summary>
    /// Renders one block per match with the persisted entry attributes surfaced. The agent
    /// gets enough decision-making context per hit — description, tags, retrieval
    /// anchors — to either answer directly or pick which hits warrant a recall,
    /// without a second round trip per candidate.
    /// </summary>
    /// <remarks>
    /// Block layout (inside the YAML envelope's <c>content: |</c> body):
    /// <code>
    /// [1] entry  /findings/auth-refactor  score=47  ~380 tokens
    ///     tags: security, refactor
    ///     description: Multi-line description verbatim.
    ///     anchors:
    ///       - what is the auth refactor?
    ///       - how does session rotation work?
    /// </code>
    /// Description is sanity-capped at <see cref="MaxDescriptionChars"/> chars to bound
    /// response size; anchors is capped at <see cref="MaxAnchorsPerResult"/>. Tags omitted
    /// when empty; anchors section omitted when null/empty.
    /// </remarks>
    internal static string FormatRichSearchResults(
        IMemoryStore store, IReadOnlyList<SearchResult> matches, IReadOnlyList<EntityFactSummary> entityFacts)
    {
        var sb = new System.Text.StringBuilder();
        AppendEntityFactsBlock(sb, entityFacts);
        sb.Append("results: ").Append(matches.Count).Append('\n');

        for (int i = 0; i < matches.Count; i++)
        {
            sb.Append('\n');
            AppendResultBlock(sb, store, matches[i], i + 1);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Renders the compact entity-facts preamble that precedes the <c>results:</c> section
    /// when the query named a known entity. One line per summary: mention count (singular
    /// "memory" for 1), an optional first→last date span, and an optional "; with ..." clause
    /// listing the top co-occurring entities. Absent entirely when there are no summaries.
    /// </summary>
    private static void AppendEntityFactsBlock(
        System.Text.StringBuilder sb, IReadOnlyList<EntityFactSummary> facts)
    {
        if (facts.Count == 0) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append("entity_facts:\n");
        foreach (var f in facts)
        {
            sb.Append("  ").Append(f.Entity.Key).Append(':').Append(f.Entity.Value)
              .Append(" — ").Append(f.MentionCount).Append(f.MentionCount == 1 ? " memory" : " memories");
            if (f.First is { } first && f.Last is { } last)
                sb.Append(", ")
                  .Append(first.ToString("yyyy-MM-dd", ci)).Append('→').Append(last.ToString("yyyy-MM-dd", ci));
            if (f.TopCoOccurring.Count > 0)
            {
                sb.Append("; with ");
                for (int i = 0; i < f.TopCoOccurring.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var c = f.TopCoOccurring[i];
                    sb.Append(c.Entity.Key).Append(':').Append(c.Entity.Value)
                      .Append(" (").Append(c.Count).Append(')');
                }
            }
            sb.Append('\n');
        }
        sb.Append('\n'); // blank line between the block and results:
    }

    internal static void AppendResultBlock(System.Text.StringBuilder sb, IMemoryStore store, SearchResult match, int index)
    {
        switch (match)
        {
            case EntryResult er:
                {
                    string label = er.Item.Scope == "ephemeral"
                        ? $"~{er.Item.Entry.Name}"
                        : store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name);
                    int estTokens = (int)(er.Item.Entry.OriginalBytes / 4);
                    sb.Append('[').Append(index).Append("] entry  ").Append(label)
                      .Append("  score=").Append(er.Score.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append("  ~").Append(estTokens).Append(" tokens\n");
                    AppendDatesLine(sb, er.Item.Entry.CreatedAt, er.Item.Entry.UpdatedAt);
                    AppendAttributesLine(sb, er.Item.Entry.Attributes);
                    AppendDescription(sb, AttributeReaders.PreviewText(er.Item.Entry.ContentPreview));
                    AppendAnchors(sb, AttributeReaders.DisplayAnchors(er.Item.Entry.Attributes));
                    break;
                }
            case ChunkEntryResult cr:
                {
                    string parentLabel = cr.ParentItem.Scope == "ephemeral"
                        ? $"~{cr.ParentItem.Entry.Name}"
                        : store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name);
                    int estTokens = cr.TotalChunks > 0
                        ? (int)(cr.ParentItem.Entry.OriginalBytes / cr.TotalChunks / 4)
                        : 0;
                    sb.Append('[').Append(index).Append("] chunk  ").Append(parentLabel)
                      .Append(" [chunk ").Append(cr.Chunk.ChunkIndex).Append('/').Append(cr.TotalChunks).Append(']')
                      .Append("  score=").Append(cr.Score.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append("  ~").Append(estTokens).Append(" tokens\n");
                    AppendDatesLine(sb, cr.ParentItem.Entry.CreatedAt, cr.ParentItem.Entry.UpdatedAt);
                    AppendAttributesLine(sb, cr.ParentItem.Entry.Attributes);
                    AppendDescription(sb, cr.Chunk.ContentPreview
                        ?? AttributeReaders.PreviewText(cr.ParentItem.Entry.ContentPreview));
                    AppendAnchors(sb, AttributeReaders.DisplayAnchors(cr.ParentItem.Entry.Attributes));
                    break;
                }
            case TopicResult tr:
                {
                    string label = MemoryNaming.FormatScopeLabel(tr.Scope);
                    sb.Append('[').Append(index).Append("] topic  ").Append(label)
                      .Append("  score=").Append(tr.Score.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append("  (").Append(tr.EntryCount).Append(" entries)\n");
                    AppendTagsLine(sb, tr.Tags);
                    AppendDescription(sb, tr.Description);
                    break;
                }
        }
    }

    private static void AppendTagsLine(System.Text.StringBuilder sb, string[]? tags)
    {
        if (tags is null || tags.Length == 0) return;
        sb.Append("    tags: ");
        for (int i = 0; i < tags.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(tags[i]);
        }
        sb.Append('\n');
    }

    /// <summary>Render record dates as display-only pseudo-attributes from the entry's structural
    /// CreatedAt/UpdatedAt — never stored in Attributes, never agent-writable. Date-only, ISO.</summary>
    private static void AppendDatesLine(System.Text.StringBuilder sb, DateTimeOffset createdAt, DateTimeOffset? updatedAt)
    {
        sb.Append("    created: ").Append(createdAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        if (updatedAt is { } u)
            sb.Append("    updated: ").Append(u.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void AppendAttributesLine(System.Text.StringBuilder sb, Dictionary<string, string[]>? attributes)
    {
        if (attributes is null || attributes.Count == 0) return;
        sb.Append("    attributes:\n");
        foreach (var kv in attributes)
        {
            sb.Append("      ").Append(kv.Key).Append(": ");
            sb.Append(string.Join(", ", kv.Value)).Append('\n');
        }
    }

    private static void AppendDescription(System.Text.StringBuilder sb, string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return;

        string text = description;
        bool truncated = false;
        if (text.Length > MaxDescriptionChars)
        {
            text = text[..MaxDescriptionChars];
            truncated = true;
        }

        // Inline form when the description fits on one line — easier to scan. Multi-line
        // descriptions fall back to an indented block whose continuation lines stay aligned
        // under the block header so visual grouping survives the outer YAML's content: |
        // re-indentation.
        if (!text.Contains('\n') && !text.Contains('\r'))
        {
            sb.Append("    description: ").Append(text);
            if (truncated) sb.Append(" (truncated)");
            sb.Append('\n');
            return;
        }

        sb.Append("    description: |\n");
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append("      ").Append(line).Append('\n');
        }
        if (truncated) sb.Append("      (truncated)\n");
    }

    private static void AppendAnchors(System.Text.StringBuilder sb, IReadOnlyList<string> anchors)
    {
        if (anchors.Count == 0) return;
        sb.Append("    anchors:\n");
        int emit = Math.Min(anchors.Count, MaxAnchorsPerResult);
        for (int i = 0; i < emit; i++)
        {
            sb.Append("      - ").Append(anchors[i].Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
        }
        if (anchors.Count > emit)
        {
            sb.Append("      - (+").Append(anchors.Count - emit).Append(" more)\n");
        }
    }
}
