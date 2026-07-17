using System.Text.Json;
using Xunit;

namespace Scrinia.Merge.Tests;

public class MetaJsonMerger3WayTests
{
    private static string WriteMeta(string dir, string file, object meta)
    {
        string path = Path.Combine(dir, file);
        File.WriteAllText(path, JsonSerializer.Serialize(meta));
        return path;
    }

    [Fact]
    public void TheirsOnlyDescriptionChange_IsPreserved_NotDropped()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        var baseMeta = new { Name = "n", Uri = (string?)null, OriginalBytes = 10, ChunkCount = 1,
            CreatedAt = "2026-01-01T00:00:00Z", Description = "old", Keywords = new[] { "a", "b" }, UpdatedAt = "2026-01-01T00:00:00Z" };
        string ancestor = WriteMeta(dir, "base.json", baseMeta);
        string ours = WriteMeta(dir, "ours.json", baseMeta); // ours == ancestor (unchanged)
        string theirs = WriteMeta(dir, "theirs.json", baseMeta with { });
        // theirs changes only Description
        File.WriteAllText(theirs, JsonSerializer.Serialize(new { baseMeta.Name, baseMeta.Uri, baseMeta.OriginalBytes,
            baseMeta.ChunkCount, baseMeta.CreatedAt, Description = "new-from-theirs", baseMeta.Keywords, UpdatedAt = "2026-02-01T00:00:00Z" }));

        var result = MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig());

        Assert.Equal(MetaJsonMerger.MergeResult.Resolved, result);
        var merged = JsonSerializer.Deserialize<MetaEntry>(File.ReadAllText(ours))!;
        Assert.Equal("new-from-theirs", merged.Description); // BUG: currently keeps "old"
    }

    [Fact]
    public void BothSidesChangeDescriptionDifferently_IsConflict()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        object mk(string desc, string upd) => new { Name = "n", Uri = (string?)null, OriginalBytes = 10, ChunkCount = 1,
            CreatedAt = "2026-01-01T00:00:00Z", Description = desc, Keywords = new[] { "a", "b" }, UpdatedAt = upd };
        string ancestor = WriteMeta(dir, "base.json", mk("old", "2026-01-01T00:00:00Z"));
        string ours = WriteMeta(dir, "ours.json", mk("ours-desc", "2026-02-01T00:00:00Z"));
        string theirs = WriteMeta(dir, "theirs.json", mk("theirs-desc", "2026-03-01T00:00:00Z"));

        var result = MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig());

        Assert.Equal(MetaJsonMerger.MergeResult.Conflict, result);
    }

    [Fact]
    public void NullOursDeserialization_ReturnsConflict_NoThrow()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string ancestor = WriteMeta(dir, "base.json", new { Name = "n", Uri = (string?)null, OriginalBytes = 1, ChunkCount = 1,
            CreatedAt = (string?)null, Description = (string?)null, Keywords = new[] { "a" }, UpdatedAt = (string?)null });
        string ours = Path.Combine(dir, "ours.json"); File.WriteAllText(ours, "null");
        string theirs = WriteMeta(dir, "theirs.json", new { Name = "n", Uri = (string?)null, OriginalBytes = 1, ChunkCount = 1,
            CreatedAt = (string?)null, Description = "x", Keywords = new[] { "a" }, UpdatedAt = (string?)null });

        var ex = Record.Exception(() => MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig()));

        Assert.Null(ex); // BUG: currently NREs on `ours!`
    }
}
