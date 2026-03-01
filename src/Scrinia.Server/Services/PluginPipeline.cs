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
                QualifiedName = result.QualifiedName,
                ChunkCount = result.ChunkCount,
                OriginalBytes = result.OriginalBytes,
                Content = req.Content,
            };

            foreach (var hook in _hooks)
                await hook.OnAfterStoreAsync(afterCtx, ct);
        }

        return result;
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
                Name = result.Name,
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

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        IMemoryStore store, string query, string? scopes, int limit, CancellationToken ct = default)
    {
        var contributor = SearchContributorContext.Current;

        IReadOnlyDictionary<string, double>? supplemental = null;
        if (contributor is not null)
        {
            var candidates = store.ListScoped(scopes);
            supplemental = await contributor.ComputeScoresAsync(query, candidates, store, ct);
        }

        return supplemental is { Count: > 0 }
            ? store.SearchAll(query, scopes, limit, supplemental)
            : store.SearchAll(query, scopes, limit);
    }
}
