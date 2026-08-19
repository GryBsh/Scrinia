using System.Text.Json.Nodes;
using Scrinia.Core.Search;

namespace Scrinia.Core.Bundles;

/// <summary>
/// Migrates a bundle index's pre-Merge-3 JSON keys to the modern <see cref="Models.ArtifactEntry"/>
/// shape, so <see cref="BundleFormatService.ImportTopicsFromZip"/> can deserialize through
/// <see cref="BundleJsonContext"/> instead of reading members by hand.
///
/// <para><b>Why this is a separate pass rather than holder properties on the entry.</b> The
/// obvious alternative — <c>[JsonPropertyName]</c> holder properties on <c>ArtifactEntry</c>,
/// following the <c>LegacyFields</c> precedent — was rejected because <c>ArtifactEntry</c> also
/// serializes into every scope's <c>index.json</c>, and these two keys are bundle-only. That
/// shape would push bundle-format history into a type the store shares.</para>
///
/// <para>This class knows about exactly TWO keys and nothing else about an entry. Adding a member
/// to <c>ArtifactEntry</c> requires no change here — that is the whole point. When pre-Merge-3
/// bundles stop mattering, delete this file and its single call site.</para>
///
/// <para>Every migration is conditional on the modern key being ABSENT. A bundle carrying both
/// keeps the modern value and ignores the legacy one; the two are never merged.</para>
/// </summary>
internal static class BundleLegacyKeys
{
    /// <summary>
    /// Rewrites legacy keys on every entry of a bundle topic index, in place. No-op for an index
    /// with no <c>entries</c> array, and for entries that already carry only modern keys.
    /// </summary>
    public static void NormalizeIndex(JsonObject index)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (index["entries"] is not JsonArray entries)
            return;

        foreach (JsonNode? entryNode in entries)
        {
            if (entryNode is JsonObject entry)
                NormalizeEntry(entry);
        }
    }

    private static void NormalizeEntry(JsonObject entry)
    {
        // "description" was the display carrier before "contentPreview" existed. DeepClone is
        // required: a JsonNode has one parent, so the value cannot be re-attached in place.
        if (entry["contentPreview"] is null && entry["description"] is JsonNode description)
            entry["contentPreview"] = description.DeepClone();
        entry.Remove("description");

        if (entry["tags"] is JsonArray tags)
        {
            // Matches the hand-parse this replaces: blank entries were dropped, and an empty or
            // all-blank array folded to nothing rather than to an empty attribute. Non-string
            // elements are skipped rather than throwing, which the old JsonElement.GetString()
            // path did not manage.
            var values = new JsonArray();
            foreach (JsonNode? tag in tags)
            {
                if (tag is JsonValue value
                    && value.TryGetValue(out string? text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    // The (JsonNode?) cast is load-bearing. Without it this binds to the generic
                    // JsonArray.Add<T>, which carries [RequiresUnreferencedCode] — and Scrinia.Core
                    // is IsTrimmable with TreatWarningsAsErrors, so it fails the build as IL2026.
                    values.Add((JsonNode?)JsonValue.Create(text));
                }
            }

            if (values.Count > 0)
            {
                if (entry["attributes"] is not JsonObject attributes)
                {
                    attributes = [];
                    entry["attributes"] = attributes;
                }

                attributes[KnownAttributes.TagKey] ??= values;
            }

            entry.Remove("tags");
        }
    }
}
