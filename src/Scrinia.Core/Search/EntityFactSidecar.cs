using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Core.Search;

/// <summary>
/// JSON persistence for a scope's <see cref="MemoryEntities"/> set — a rebuildable cache of derived
/// data, guarded by a validity <see cref="ComputeToken">token</see>. The store is NEVER a competing
/// source of truth: a missing / wrong-version / token-mismatch sidecar is rebuilt from the entries.
/// </summary>
public static class EntityFactSidecar
{
    public const int Version = 1;

    /// <summary>Order-independent, content-sensitive token over a scope's (id, effective-date) pairs.
    /// Effective date = <c>UpdatedAt ?? CreatedAt</c>. Sorting makes it independent of entry order.</summary>
    public static string ComputeToken(IEnumerable<(string Id, DateTimeOffset EffectiveDate)> entries)
    {
        var lines = entries
            .Select(e => $"{e.Id} {e.EffectiveDate.UtcDateTime:O}")
            .OrderBy(s => s, StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexStringLower(hash);
    }

    public static string Serialize(string token, IReadOnlyList<MemoryEntities> memories)
    {
        var dto = new Dto(
            Version, token,
            memories.Select(m => new MemDto(
                m.MemoryId, m.Date,
                m.Entities.Select(e => new RefDto(e.Key, e.Value)).ToArray())).ToArray());
        return JsonSerializer.Serialize(dto, SidecarJsonContext.Default.Dto);
    }

    public static (string Token, IReadOnlyList<MemoryEntities> Memories)? TryLoad(string json)
    {
        Dto? dto;
        try { dto = JsonSerializer.Deserialize(json, SidecarJsonContext.Default.Dto); }
        catch (JsonException) { return null; }
        if (dto is null || dto.Version != Version) return null;

        var mems = dto.Memories
            .Select(m => new MemoryEntities(
                m.MemoryId, m.Date,
                m.Entities.Select(r => new EntityRef(r.Key, r.Value)).ToArray()))
            .ToArray();
        return (dto.Token, mems);
    }

    internal sealed record Dto(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("memories")] MemDto[] Memories);

    internal sealed record MemDto(
        [property: JsonPropertyName("id")] string MemoryId,
        [property: JsonPropertyName("date")] DateTimeOffset Date,
        [property: JsonPropertyName("entities")] RefDto[] Entities);

    internal sealed record RefDto(
        [property: JsonPropertyName("k")] string Key,
        [property: JsonPropertyName("v")] string Value);
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EntityFactSidecar.Dto))]
internal partial class SidecarJsonContext : JsonSerializerContext;
