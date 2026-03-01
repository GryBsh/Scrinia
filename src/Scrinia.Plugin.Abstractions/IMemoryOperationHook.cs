namespace Scrinia.Plugin.Abstractions;

/// <summary>
/// Hook into store, append, and forget operations.
/// Register as a service via <see cref="IScriniaPlugin.ConfigureServices"/>
/// or implement on the plugin class itself.
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
