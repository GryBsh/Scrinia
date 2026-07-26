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
    public const int Version = 2;

    /// <summary>Order-independent, content-sensitive token over a scope's (id, effective-date,
    /// content-signature) triples. Effective date = <c>UpdatedAt ?? CreatedAt</c>; the content
    /// signature is a deterministic summary of the entity-bearing fields (Attributes/Keywords) —
    /// see <c>FileMemoryStore.EntitySignature</c>. Including it means a mutation that changes an
    /// entry's entity data invalidates the token even when its timestamp is untouched (e.g. a
    /// store-level attribute rewrite that must not re-date the entry). Sorting makes the whole
    /// token independent of entry order.</summary>
    public static string ComputeToken(IEnumerable<(string Id, DateTimeOffset EffectiveDate, string ContentSignature)> entries)
    {
        var lines = entries
            .Select(e => $"{e.Id} {e.EffectiveDate.UtcDateTime:O} {e.ContentSignature}")
            .OrderBy(s => s, StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexStringLower(hash);
    }

    public static string Serialize(string token, IReadOnlyList<MemoryEntities> memories)
    {
        var dto = new Dto(
            Version, token,
            memories.Select(m => new MemDto(
                m.Scope, m.Subject, m.Date,
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
                m.Scope, m.Subject, m.Date,
                m.Entities.Select(r => new EntityRef(r.Key, r.Value)).ToArray()))
            .ToArray();
        return (dto.Token, mems);
    }

    internal sealed record Dto(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("memories")] MemDto[] Memories);

    internal sealed record MemDto(
        [property: JsonPropertyName("s")] string Scope,
        [property: JsonPropertyName("j")] string Subject,
        [property: JsonPropertyName("date")] DateTimeOffset Date,
        [property: JsonPropertyName("entities")] RefDto[] Entities);

    internal sealed record RefDto(
        [property: JsonPropertyName("k")] string Key,
        [property: JsonPropertyName("v")] string Value);
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EntityFactSidecar.Dto))]
internal partial class SidecarJsonContext : JsonSerializerContext;
