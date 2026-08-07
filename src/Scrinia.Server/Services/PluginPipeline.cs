using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Plugin.Abstractions;
using Scrinia.Server.Models;

namespace Scrinia.Server.Services;

/// <summary>
/// Wraps <see cref="MemoryOrchestrator"/> with before/after hook invocation.
/// Zero overhead when no hooks are registered.
/// </summary>
public sealed class PluginPipeline
{
    private readonly IMemoryOperationHook[] _hooks;

    public PluginPipeline(IEnumerable<IMemoryOperationHook> hooks)
    {
        _hooks = hooks.OrderBy(h => h.Order).ToArray();
    }

    public async Task<StoreResponse> StoreAsync(
        IMemoryStore store, StoreRequest req, CancellationToken ct = default)
    {
        if (_hooks.Length > 0)
        {
            var ctx = new BeforeStoreContext
            {
                Store = store,
                Name = req.Name,
                Content = req.Content,
                Description = req.Description,
                Tags = req.Tags,
                Keywords = req.Keywords,
            };

            foreach (var hook in _hooks)
            {
                await hook.OnBeforeStoreAsync(ctx, ct);
                if (ctx.Cancel)
                    throw new OperationCanceledException(ctx.CancelReason ?? "Store operation cancelled by plugin.");
            }
        }

        var result = await MemoryOrchestrator.StoreAsync(store, req, ct);

        if (_hooks.Length > 0)
        {
            var afterCtx = new AfterStoreContext
            {
                Store = store,
                Name = result.Name,
                QualifiedName = BuildRoutingQualifiedName(store, req.Name, result.QualifiedName),
                ChunkCount = result.ChunkCount,
                OriginalBytes = result.OriginalBytes,
                Content = req.Content,
            };

            foreach (var hook in _hooks)
                await hook.OnAfterStoreAsync(afterCtx, ct);
        }

        return result;
    }

    /// <summary>
    /// Hooks (e.g. the built-in/Vulkan embedding services) re-parse the after-store/append
    /// qualified name via <see cref="IMemoryStore.ParseQualifiedName"/> to decide which scope to
    /// route vectors under. Historically <see cref="IMemoryStore.FormatQualifiedName"/> stripped
    /// the v1 "memory/"/"entity/" namespace prefix for human-friendly REST responses, which made
    /// the display string lossy to re-parse — under v3 (Task 8) that stripping is gone and
    /// <c>FormatQualifiedName</c> is round-trip-safe for every scope, so this rebuild is now a
    /// no-op in practice. Kept as an explicit seam (rather than inlining
    /// <c>MemoryOrchestrator</c>'s already-built display string) so a future re-introduction of a
    /// lossy display transform can't silently regress vector routing again. Ephemeral names
    /// ("~key") already round-trip safely as-is.
    /// </summary>
    private static string BuildRoutingQualifiedName(IMemoryStore store, string requestedName, string displayQualifiedName)
    {
        if (store.IsEphemeral(requestedName))
            return displayQualifiedName;

        var (scope, subject) = store.ParseQualifiedName(requestedName);
        return store.FormatQualifiedName(scope, subject);
    }

    public async Task<AppendResponse> AppendAsync(
        IMemoryStore store, string name, string content, CancellationToken ct = default)
    {
        if (_hooks.Length > 0)
        {
            var ctx = new BeforeAppendContext
            {
                Store = store,
                Name = name,
                Content = content,
            };

            foreach (var hook in _hooks)
            {
                await hook.OnBeforeAppendAsync(ctx, ct);
                if (ctx.Cancel)
                    throw new OperationCanceledException(ctx.CancelReason ?? "Append operation cancelled by plugin.");
            }
        }

        var result = await MemoryOrchestrator.AppendAsync(store, name, content, ct);

        if (_hooks.Length > 0)
        {
            var afterCtx = new AfterAppendContext
            {
                Store = store,
                Name = BuildRoutingQualifiedName(store, name, result.Name),
                ChunkCount = result.ChunkCount,
                OriginalBytes = result.OriginalBytes,
                Content = content,
            };

            foreach (var hook in _hooks)
                await hook.OnAfterAppendAsync(afterCtx, ct);
        }

        return result;
    }

    public async Task<bool> ForgetAsync(
        IMemoryStore store, string name, CancellationToken ct = default)
    {
        if (_hooks.Length > 0)
        {
            var ctx = new BeforeForgetContext
            {
                Store = store,
                Name = name,
            };

            foreach (var hook in _hooks)
            {
                await hook.OnBeforeForgetAsync(ctx, ct);
                if (ctx.Cancel)
                    throw new OperationCanceledException(ctx.CancelReason ?? "Forget operation cancelled by plugin.");
            }
        }

        bool deleted = await MemoryOrchestrator.ForgetAsync(store, name, ct);

        if (_hooks.Length > 0)
        {
            var afterCtx = new AfterForgetContext
            {
                Store = store,
                Name = name,
                WasDeleted = deleted,
            };

            foreach (var hook in _hooks)
                await hook.OnAfterForgetAsync(afterCtx, ct);
        }

        return deleted;
    }

    // CA1068: ct is kept in its existing position (not last) to preserve the call site at
    // MemoryEndpoints.RunSearch and the signature Task 5 (HttpMemoryStore) is built against;
    // reordering would be a breaking change to an interface pinned across this branch.
#pragma warning disable CA1068
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        IMemoryStore store, string query, string? scopes, int limit, CancellationToken ct = default,
        string? excludeTopics = null,
        string? pathPrefix = null,
        IReadOnlyDictionary<string, string[]>? attributeFilter = null)
#pragma warning restore CA1068
    {
        var contributor = SearchContributorContext.Current;
        var entityContributor = EntityGraphScoreContext.Current;

        IReadOnlyDictionary<string, double>? supplemental = null;
        IReadOnlyDictionary<string, double>? entityScores = null;
        if (contributor is not null || entityContributor is not null)
        {
            // Contributors must see the same excludeTopics-filtered pool the scorer will, so an
            // excluded topic cannot influence scoring.
            var candidates = store.ListScoped(scopes, excludeTopics);
            if (contributor is not null)
                supplemental = await contributor.ComputeScoresAsync(query, candidates, store, ct);
            if (entityContributor is not null)
            {
                var factStore = string.IsNullOrWhiteSpace(scopes) && string.IsNullOrWhiteSpace(excludeTopics)
                    ? store.GetEntityFactStore()                       // cached, no rebuild
                    : EntityFactDerivation.BuildStore(candidates);     // scoped / topic-excluded
                entityScores = entityContributor.Score(factStore, query);
            }
        }

        // Every filter is pushed into SearchAll so it applies BEFORE the top-K cut. Empty
        // supplemental is normalized to null so the scorer skips the fusion source entirely.
        var supp = supplemental is { Count: > 0 } ? supplemental : null;
        return store.SearchAll(query, scopes, limit, excludeTopics, supp, entityScores,
            pathPrefix, attributeFilter);
    }
}
