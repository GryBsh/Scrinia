using System.Text.Json.Serialization;

namespace Scrinia.Core.Models;

public sealed class IndexFile
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 3;

    [JsonPropertyName("entries")]
    public List<ArtifactEntry> Entries { get; set; } = [];
}
