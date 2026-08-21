namespace Scrinia.Plugin.Abstractions;

/// <summary>
/// Hook into store, append, and forget operations. Implement it on the plugin class itself.
///
/// <para><b>Nothing invokes this today.</b> Its caller was <c>PluginPipeline</c>, which wrapped
/// the deleted REST API's store, append and forget routes; the MCP path has always used
/// <c>IMemoryEventSink</c> instead. The contract is kept because it is published, and because the
/// rule that separated the two is still the rule a second caller would have to honour: a component
/// registered as both a hook and a sink fires exactly once.</para>
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
