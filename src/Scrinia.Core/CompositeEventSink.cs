using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// Chains multiple <see cref="IMemoryEventSink"/> instances. Each sink runs in sequence;
/// exceptions in one sink are caught and logged, never blocking subsequent sinks.
/// </summary>
public sealed class CompositeEventSink(IMemoryEventSink[] sinks) : IMemoryEventSink
{
    public async Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
    {
        foreach (var sink in sinks)
        {
            try { await sink.OnStoredAsync(address, content, store, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink {sink.GetType().Name} error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public async Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct)
    {
        foreach (var sink in sinks)
        {
            try { await sink.OnAppendedAsync(address, content, store, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink {sink.GetType().Name} error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public async Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct)
    {
        foreach (var sink in sinks)
        {
            try { await sink.OnForgottenAsync(address, wasDeleted, store, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink {sink.GetType().Name} error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
