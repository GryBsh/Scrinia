namespace Scrinia.Core;

/// <summary>
/// The one place Core's internal event handlers widen the narrowed sink contract back to the full
/// store. <see cref="IMemoryEventSink"/> declares <see cref="IPluginStore"/> because that is what a
/// plugin-shaped sink needs; <c>EntityTypingEventHandler</c> and <c>MaintenanceEventSink</c> are not
/// plugin-shaped — they are Core's own maintenance code and need five and six members respectively.
///
/// <para>Two handlers × three sink methods is six cast sites, so the cast and its message live here
/// rather than being written out six times. It throws rather than returning null or skipping,
/// because a handler that silently does not run is invisible data loss;
/// <see cref="CompositeEventSink"/> isolates per-sink exceptions, so the blast radius is one logged
/// sink failure.</para>
/// </summary>
internal static class CoreSinkStore
{
    public static IMemoryStore Require(IPluginStore store, string handler) =>
        store as IMemoryStore
        ?? throw new InvalidOperationException(
            $"{handler} is a Core-internal event handler and requires the full {nameof(IMemoryStore)}, " +
            $"not the narrowed {nameof(IPluginStore)} the sink contract declares. It was handed a " +
            $"{store.GetType().Name}.");
}
