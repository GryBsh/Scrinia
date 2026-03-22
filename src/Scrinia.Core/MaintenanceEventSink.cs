using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Automatic memory maintenance: creates bidirectional ref: links when content
/// references other memories, and flags orphan memories with no inbound references.
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

        // 2. For each referenced memory that exists, add ref:{source} keyword on the target
        foreach (var refName in memoryRefs)
        {
            // Skip self-references
            if (refName.Equals(qualifiedName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var (targetScope, targetSubject) = store.ParseQualifiedName(refName);
                var targetEntries = store.LoadIndex(targetScope);
                var targetEntry = targetEntries.FirstOrDefault(e =>
                    e.Name.Equals(targetSubject, StringComparison.OrdinalIgnoreCase));

                if (targetEntry is null) continue; // referenced memory doesn't exist

                string refKeyword = $"ref:{qualifiedName}";
                if (targetEntry.Keywords?.Contains(refKeyword, StringComparer.OrdinalIgnoreCase) == true)
                    continue; // already linked

                // Add ref keyword to target
                var existingKw = targetEntry.Keywords ?? [];
                var updatedKw = existingKw.Append(refKeyword).ToArray();
                var updatedEntry = targetEntry with { Keywords = updatedKw };
                store.Upsert(updatedEntry, targetScope);
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
            var sourceEntries = store.LoadIndex(sourceScope);
            var sourceEntry = sourceEntries.FirstOrDefault(e =>
                e.Name.Equals(sourceSubject, StringComparison.OrdinalIgnoreCase));

            if (sourceEntry is null) return Task.CompletedTask;

            string sourceQualified = store.FormatQualifiedName(sourceScope, sourceSubject);
            string inboundRefKey = $"ref:{sourceQualified}";

            var allEntries = store.ListScoped(null);
            bool hasInboundRefs = allEntries.Any(sa =>
                sa.Entry.Keywords?.Any(k => k.Equals(inboundRefKey, StringComparison.OrdinalIgnoreCase)) == true);

            var currentKw = sourceEntry.Keywords?.ToList() ?? [];
            bool isOrphan = currentKw.Any(k => k.Equals("orphan", StringComparison.OrdinalIgnoreCase));

            if (!hasInboundRefs && !isOrphan)
            {
                currentKw.Add("orphan");
                var updated = sourceEntry with { Keywords = currentKw.ToArray() };
                store.Upsert(updated, sourceScope);
            }
            else if (hasInboundRefs && isOrphan)
            {
                currentKw.RemoveAll(k => k.Equals("orphan", StringComparison.OrdinalIgnoreCase));
                var updated = sourceEntry with { Keywords = currentKw.ToArray() };
                store.Upsert(updated, sourceScope);
            }
        }
        catch
        {
            // orphan detection is best-effort — silently skip
        }

        return Task.CompletedTask;
    }
}
