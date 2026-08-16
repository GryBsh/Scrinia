using Scrinia.Core;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Server.Services;

/// <summary>
/// Carries the registered <see cref="IMemoryEventSink"/> set onto the REST path.
///
/// <para>The server has two write doors — REST via <see cref="PluginPipeline"/>, and the hosted
/// MCP endpoint via <c>MemoryEventSinkContext</c> — and they used different mechanisms, so a REST
/// write got embeddings but no ref-linking or orphan marking while the identical write through
/// MCP, in the same process against the same store, got both. The split itself is deliberate and
/// documented (docs/architecture/overview.md): it is what stops a component that implements both
/// interfaces firing twice. Only the membership was wrong.</para>
///
/// <para><b>The exclusion rule is the whole point.</b> A sink that is ALSO registered as an
/// <see cref="IMemoryOperationHook"/> — <c>BuiltInEmbeddingsService</c> is — already runs on this
/// path in its own right, so firing it from here would embed every REST write twice. Filtering
/// here keeps the documented once-per-operation guarantee while making the sink set identical
/// across both doors, and it puts that rule in ONE place instead of requiring every future sink
/// to remember which interfaces to implement.</para>
///
/// <para>After-phases only. <see cref="IMemoryEventSink"/> is an after-the-fact notification with
/// no veto; implementing <c>OnBefore*</c> here would hand sinks a cancel power the contract does
/// not give them.</para>
///
/// <para><b>Dispatch is detached.</b> The hosted MCP endpoint fires sinks fire-and-forget via
/// <see cref="BackgroundWork"/> (see <c>MemoryTools.FireEventSinkAsync</c>) so embedding latency
/// (100–400 ms for remote providers) and <c>MaintenanceEventSink</c>'s full-corpus scan never
/// block the write response. REST used to await the sinks inline instead — a REST write blocked
/// on the same work the MCP door had already deliberately detached, for no reason other than that
/// nobody had asked the question. This adapter now routes through <see cref="BackgroundWork"/> the
/// same way, for the same reason, so both doors give the same latency guarantee alongside the same
/// membership: the on-disk artifact is already durable by the time this runs, so a missed or
/// delayed sink only means the entry won't show in semantic search / get ref-linked until the
/// background work catches up.</para>
/// </summary>
public sealed class EventSinkOperationHook : IMemoryOperationHook
{
    /// <summary>Wraps the filtered sink set so a throwing sink cannot fail an already-succeeded
    /// write on REST. <see cref="CompositeEventSink"/> catches and logs per-sink, exactly what the
    /// hosted MCP endpoint already gets via <c>MemoryEventSinkContext</c> — one place defines the
    /// exception semantics and both doors inherit it, rather than this adapter looping the sinks
    /// itself and needing its own try/catch that could drift from the MCP door's.</summary>
    private readonly CompositeEventSink _sinks;

    public EventSinkOperationHook(IEnumerable<IMemoryEventSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = new CompositeEventSink([.. sinks.Where(s => s is not IMemoryOperationHook)]);
    }

    /// <summary>Runs after the hooks that do the primary work.</summary>
    public int Order => 100;

    public Task OnAfterStoreAsync(AfterStoreContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        // CancellationToken.None: the sink runs detached on a background Task — propagating the
        // per-request ct would abort it the moment the REST response returns.
        BackgroundWork.Fire(
            _ => _sinks.OnStoredAsync(context.QualifiedName, context.Content, context.Store, CancellationToken.None),
            "event sink");
        return Task.CompletedTask;
    }

    public Task OnAfterAppendAsync(AfterAppendContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        BackgroundWork.Fire(
            _ => _sinks.OnAppendedAsync(context.Name, context.Content, context.Store, CancellationToken.None),
            "event sink");
        return Task.CompletedTask;
    }

    public Task OnAfterForgetAsync(AfterForgetContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        BackgroundWork.Fire(
            _ => _sinks.OnForgottenAsync(context.Name, context.WasDeleted, context.Store, CancellationToken.None),
            "event sink");
        return Task.CompletedTask;
    }
}
