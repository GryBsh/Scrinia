using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// Best-effort <see cref="IMemoryEventSink"/> that upgrades untyped <c>mention:</c> prose spans to
/// typed entity keys using <see cref="EntityTypeClassifier"/> (Piece 2, Stage 2). Runs out-of-band
/// after a write; guarded by <see cref="IEmbeddingProvider.IsAvailable"/>; writes back only via
/// <see cref="IMemoryStore.TryUpdateEntry"/> (fires no sinks). Never blocks or fails the write it
/// runs after — dispatch is detached and guarded, so nothing here can turn into a failed store or
/// append. On the embedder being absent or unsure, spans simply stay <c>mention:</c>. The one path
/// that DOES throw is <see cref="CoreSinkStore.Require"/>, when this handler is run outside Core
/// against a store that is not a full <see cref="IMemoryStore"/>; that throw is a handler failure
/// isolated by the sink dispatcher, not a write failure.
/// </summary>
// CA1711: the "EventHandler" suffix is deliberate here (it implements IMemoryEventSink, not a
// delegate) — it names the class's role in the sink pipeline, matching CoreEmbeddingEventHandler.
#pragma warning disable CA1711
public sealed class EntityTypingEventHandler(IEmbeddingProvider provider) : IMemoryEventSink
#pragma warning restore CA1711
{
    private readonly EntityTypeClassifier _classifier = new(provider);

    public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
    {
        var full = CoreSinkStore.Require(store, nameof(EntityTypingEventHandler));
        return PromoteAsync(address, full, ct);
    }

    public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
    {
        var full = CoreSinkStore.Require(store, nameof(EntityTypingEventHandler));
        return PromoteAsync(address, full, ct);
    }

    public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
        => Task.CompletedTask;

    private async Task PromoteAsync(MemoryAddress address, IMemoryStore store, CancellationToken ct)
    {
        if (!provider.IsAvailable) return;

        var (scope, subject) = store.ParseQualifiedName(address.ToString());

        // Case-insensitive to match IMemoryStore's default TryUpdateEntry: SanitizeName does not
        // fold case, so an ordinal match here gated a case-mismatched name out of promotion
        // entirely, above and independent of the store's own lookup. Every store's own lookup is
        // case-insensitive too, on every filesystem — but this one runs first, so it has to be.
        var entry = store.LoadIndex(scope)
            .FirstOrDefault(e => store.SanitizeName(e.Name)
                .Equals(store.SanitizeName(subject), StringComparison.OrdinalIgnoreCase));
        if (entry?.Attributes is null) return;
        if (!entry.Attributes.TryGetValue(KnownAttributes.MentionKey, out var mentions) || mentions.Length == 0)
            return;

        // Existing typed vocabulary across all scopes, from the cached cross-scope fact store
        // (cache-on-first-read, invalidated on mutation) rather than a bespoke ListScoped(null)
        // rescan on every write event.
        var facts = store.GetEntityFactStore();
        var existingTyped = new List<(string Value, string Type)>();
        foreach (var key in KnownAttributes.EntityKeys)
            foreach (var e in facts.Entities(key))
                existingTyped.Add((e.Value, key));

        // Classify (async, no lock held).
        var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in mentions)
        {
            string? type = await _classifier.TypeAsync(value, existingTyped, ct);
            if (type is not null) moves[value] = type;
        }
        if (moves.Count == 0) return;

        // Apply under the store lock; move only values STILL under mention: on the current entry.
        // Pass entry.Name (the resolved entry's own casing), not the event's subject. This is the
        // convention across every name-resolving write path in the codebase: once a lookup has
        // resolved an entry case-insensitively, the entry's OWN casing is what travels onward,
        // because the write paths rebuild filesystem paths out of that name (sidecars, artifacts,
        // version archives) and a name is only as good as the file it names. Handing the event's
        // casing back instead would resolve one entry and write against another's path.
        store.TryUpdateEntry(entry.Name, scope, current =>
        {
            if (current.Attributes is null ||
                !current.Attributes.TryGetValue(KnownAttributes.MentionKey, out var curMentions))
                return null;

            var attrs = new Dictionary<string, string[]>(current.Attributes, StringComparer.OrdinalIgnoreCase);
            var remaining = new List<string>();
            bool moved = false;

            foreach (var m in curMentions)
            {
                if (moves.TryGetValue(m, out var type))
                {
                    var existing = attrs.TryGetValue(type, out var tv) ? tv.ToList() : [];
                    if (!existing.Contains(m, StringComparer.OrdinalIgnoreCase)) existing.Add(m);
                    attrs[type] = [.. existing];
                    moved = true;
                }
                else
                {
                    remaining.Add(m);
                }
            }

            if (!moved) return null;

            if (remaining.Count == 0) attrs.Remove(KnownAttributes.MentionKey);
            else attrs[KnownAttributes.MentionKey] = [.. remaining];

            return current with { Attributes = attrs };
        });
    }
}
