namespace Scrinia.Plugin.Abstractions;

/// <summary>
/// Hook into store, append, and forget operations. Implement it on the plugin class itself.
///
/// <para>The canonical path for reacting to these operations is <c>IMemoryEventSink</c>; this hook
/// interface is invoked only through a <c>sink as IMemoryOperationHook</c> cast on the append path
/// (<c>MemoryTools.Core</c>) as a backward-compatibility bridge for plugins that implement the hook
/// instead of the event sink. The REST path that previously wrapped these routes was removed; the
/// rule that separated the two still applies: a component registered as both a hook and a sink fires
/// exactly once.</para>
/// </summary>
public interface IMemoryOperationHook
{
    /// <summary>Execution order — lower values run first. Default 0.</summary>
    int Order => 0;

    Task OnBeforeStoreAsync(BeforeStoreContext context, CancellationToken ct = default) => Task.CompletedTask;
    Task OnAfterStoreAsync(AfterStoreContext context, CancellationToken ct = default) => Task.CompletedTask;

    Task OnBeforeAppendAsync(BeforeAppendContext context, CancellationToken ct = default) => Task.CompletedTask;
    Task OnAfterAppendAsync(AfterAppendContext context, CancellationToken ct = default) => Task.CompletedTask;

    Task OnBeforeForgetAsync(BeforeForgetContext context, CancellationToken ct = default) => Task.CompletedTask;
    Task OnAfterForgetAsync(AfterForgetContext context, CancellationToken ct = default) => Task.CompletedTask;
}
