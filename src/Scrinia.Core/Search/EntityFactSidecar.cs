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
    /// <summary>
    /// Bumped whenever the DERIVATION RULE changes, not only when the file's shape does — that is
    /// the whole reason this constant exists, and the only thing standing between a rule change and
    /// a cache that predates it.
    ///
    /// <para><b>2 → 3:</b> state-marker attributes (<see cref="KnownAttributes.StateMarkerKeys"/>)
    /// stopped being entities. The validity <see cref="ComputeToken">token</see> is content-only —
    /// id, effective date and entity signature — so it says nothing about which RULES produced the
    /// facts. A sidecar written by the previous build therefore validated against a scope whose
    /// entries had not changed, and handed <c>orphan:true</c> straight back as a graph node: the hub
    /// reinstated on the unfiltered <c>SearchPipeline</c> path, persisting until something in that
    /// scope mutated. Verified by loading a pre-fix sidecar — <c>entityToDocs.Keys</c> came back
    /// carrying <c>orphan:true</c>.</para>
    ///
    /// <para>The general lesson, worth more than this instance: a cache that predates a rule change
    /// is a path around the new rule. <see cref="TryLoad"/> is a second route into
    /// <see cref="EntityFactStore"/> that bypasses <c>EntityGraph.ParseEntityAttributes</c>
    /// entirely, so a filter added there governs derivation and nothing else. Any future change to
    /// what <c>ParseEntityAttributes</c>/<c>ParseEntityKeywords</c> yield must bump this too.</para>
    /// </summary>
    public const int Version = 3;

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
