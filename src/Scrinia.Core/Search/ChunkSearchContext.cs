using System.Text;
using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>Builds bounded, query-matching sibling context without changing memory ranking.</summary>
internal static class ChunkSearchContext
{
    private const int MaxChunks = 3;
    private const int MaxPreviewChars = 500;

    internal static string? Create(SearchResult result, IReadOnlyList<string> queryTerms)
    {
        ScopedArtifact? parent = result switch
        {
            EntryResult entry => entry.Item,
            ChunkEntryResult chunk => chunk.ParentItem,
            _ => null
        };
        if (parent?.Entry.ChunkEntries is not { Length: > 0 } chunks)
            return null;

        // Inherited parent keywords do not make every sibling relevant. Require a match
        // in the chunk itself; old indexes without TF data can still use their preview.
        var terms = queryTerms.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matching = chunks.Where(chunk => !string.IsNullOrWhiteSpace(chunk.ContentPreview)).Where(chunk =>
            (chunk.TermFrequencies?.Keys.Any(terms.Contains) ?? false)
            || TextAnalysis.Tokenize(chunk.ContentPreview ?? "").Any(terms.Contains)
            || (chunk.Keywords?.Any(keyword => TextAnalysis.Tokenize(keyword).Any(terms.Contains)) ?? false))
            .OrderByDescending(chunk => chunk.ChunkIndex).ToArray();
        ChunkEntry? winner = (result as ChunkEntryResult)?.Chunk;
        string? parentPreview = result is EntryResult || chunks.Length < parent.Entry.ChunkCount
            ? AttributeReaders.PreviewText(parent.Entry.ContentPreview) : null;
        bool includeParent = !string.IsNullOrWhiteSpace(parentPreview);
        if (matching.Length == 0 || (!includeParent && matching.Length == 1 && matching[0].ChunkIndex == winner?.ChunkIndex))
            return null;

        // Retain the ranked winner plus the last matching storage chunks. Storage order
        // is only a context-selection heuristic, never a claim about factual chronology.
        var selected = matching.Where(chunk => chunk.ChunkIndex != winner?.ChunkIndex)
            .Take(MaxChunks - (winner is null ? 0 : 1) - (includeParent ? 1 : 0)).ToList();
        if (winner is not null) selected.Add(winner);
        selected.Sort((a, b) => b.ChunkIndex.CompareTo(a.ChunkIndex));

        var text = new StringBuilder(
            "Matching chunk previews (reverse storage order, not event chronology). Compare dates and explicit corrections.\n");
        foreach (var chunk in selected)
        {
            string preview = Clip(chunk.ContentPreview ?? "");
            text.Append("[chunk ").Append(chunk.ChunkIndex).Append('/').Append(parent.Entry.ChunkCount)
                .Append("] ").Append(preview).Append('\n');
        }
        if (includeParent)
            text.Append("[memory preview] ").Append(Clip(parentPreview!, 1500)).Append('\n');
        text.Append("Partial preview. Recall this path for more context");
        if (matching.Length > selected.Count)
            text.Append(" and additional matching chunks");
        text.Append('.');
        return text.ToString();
    }

    private static string Clip(string preview, int limit = MaxPreviewChars) => preview.Length > limit
        ? preview[..limit] + " (truncated)" : preview;
}
