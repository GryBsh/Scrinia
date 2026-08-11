using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Automatic memory maintenance: creates bidirectional ref links (via <c>Attributes["ref"]</c>)
/// when content references other memories, and flags orphan memories with no inbound
/// references (dual-reading legacy <c>ref:</c> keywords alongside the ref attribute).
/// </summary>
public sealed class MaintenanceEventSink : IMemoryEventSink
{
    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
    {
        try
        {
            string joined = string.Join("\n", content);
            await AutoLinkAndDetectOrphans(qualifiedName, joined, store);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scrinia:warn] MaintenanceEventSink error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
    {
        try
        {
            await AutoLinkAndDetectOrphans(qualifiedName, content, store);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scrinia:warn] MaintenanceEventSink error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
        => Task.CompletedTask; // no-op for now

    private static Task AutoLinkAndDetectOrphans(string qualifiedName, string content, IMemoryStore store)
    {
        // 1. Extract memory references from content
        var memoryRefs = ReferenceExtractor.ExtractMemoryRefs(content);

        // 2. For each referenced memory that exists, add the source to Attributes["ref"] on the target
        foreach (var refName in memoryRefs)
        {
            // Skip self-references
            if (refName.Equals(qualifiedName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var (targetScope, targetSubject) = store.ParseQualifiedName(refName);

                // TryUpdateEntry, not LoadIndex → mutate → Upsert: remotely Upsert is a no-op, so
                // that shape discarded the link silently; TryUpdateEntry fails closed there and runs
                // under the per-scope write lock locally. Doing the "already linked?" check inside
                // the callback also keeps this to ONE index read — hoisting it back out would cost a
                // second network round trip per referenced memory on a remote store.
                store.TryUpdateEntry(targetSubject, targetScope, targetEntry =>
                {
                    // Write the ref attribute (unified attributes) — no more ref: keyword emission.
                    string[] existingRefs = targetEntry.Attributes is not null
                        && targetEntry.Attributes.TryGetValue("ref", out var r) ? r : [];
                    if (existingRefs.Contains(qualifiedName, StringComparer.OrdinalIgnoreCase)
                        || targetEntry.Keywords?.Contains($"ref:{qualifiedName}", StringComparer.OrdinalIgnoreCase) == true)
                        return null; // already linked (either shape) — no write

                    var updatedAttributes = KnownAttributes.Merge(
                        targetEntry.Attributes, new() { ["ref"] = [qualifiedName] });
                    return targetEntry with { Attributes = updatedAttributes };
                });
            }
            catch
            {
                // target scope may not exist — silently skip
            }
        }

        // 3. Orphan detection — check if this entry has inbound refs
        try
        {
            var (sourceScope, sourceSubject) = store.ParseQualifiedName(qualifiedName);

            string sourceQualified = store.FormatQualifiedName(sourceScope, sourceSubject);
            string inboundRefKey = $"ref:{sourceQualified}";

            // Computed before the update rather than after a separate LoadIndex: neither this nor
            // the qualified name needs the source entry itself, and TryUpdateEntry below reads the
            // entry once, atomically, instead of the old racy load → mutate → Upsert.
            var allEntries = store.ListScoped(null);
            bool hasInboundRefs = allEntries.Any(sa =>
                (sa.Entry.Keywords?.Any(k => k.Equals(inboundRefKey, StringComparison.OrdinalIgnoreCase)) == true)
                || (sa.Entry.Attributes is not null
                    && sa.Entry.Attributes.TryGetValue("ref", out var refs)
                    && refs.Any(v => v.Equals(sourceQualified, StringComparison.OrdinalIgnoreCase))));

            store.TryUpdateEntry(sourceSubject, sourceScope, sourceEntry =>
            {
                var currentKw = sourceEntry.Keywords?.ToList() ?? [];
                bool isOrphan = currentKw.Any(k => k.Equals("orphan", StringComparison.OrdinalIgnoreCase));

                if (!hasInboundRefs && !isOrphan)
                {
                    currentKw.Add("orphan");
                    return sourceEntry with { Keywords = currentKw.ToArray() };
                }
                if (hasInboundRefs && isOrphan)
                {
                    currentKw.RemoveAll(k => k.Equals("orphan", StringComparison.OrdinalIgnoreCase));
                    return sourceEntry with { Keywords = currentKw.ToArray() };
                }
                return null; // nothing to change
            });
        }
        catch
        {
            // orphan detection is best-effort — silently skip
        }

        return Task.CompletedTask;
    }
}
