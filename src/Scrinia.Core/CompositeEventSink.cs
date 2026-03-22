namespace Scrinia.Core;

/// <summary>
/// Chains multiple <see cref="IMemoryEventSink"/> instances. Each sink runs in sequence;
/// exceptions in one sink are caught and logged, never blocking subsequent sinks.
/// </summary>
public sealed class CompositeEventSink(IMemoryEventSink[] sinks) : IMemoryEventSink
{
    public async Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
    {
        foreach (var sink in sinks)
        {
            try { await sink.OnStoredAsync(qualifiedName, content, store, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink {sink.GetType().Name} error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public async Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
    {
        foreach (var sink in sinks)
        {
            try { await sink.OnAppendedAsync(qualifiedName, content, store, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink {sink.GetType().Name} error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public async Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
    {
        foreach (var sink in sinks)
        {
            try { await sink.OnForgottenAsync(qualifiedName, wasDeleted, store, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink {sink.GetType().Name} error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
