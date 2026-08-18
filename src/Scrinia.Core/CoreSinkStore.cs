namespace Scrinia.Core;

/// <summary>
/// The one place Core's internal event handlers widen the narrowed sink contract back to the full
/// store. <see cref="IMemoryEventSink"/> declares <see cref="IPluginStore"/> because that is what a
/// plugin-shaped sink needs; <c>EntityTypingEventHandler</c> and <c>MaintenanceEventSink</c> are not
/// plugin-shaped — they are Core's own maintenance code and need five and six members respectively.
///
/// <para>There are four cast sites, not six: <c>EntityTypingEventHandler.OnStoredAsync</c>,
/// <c>EntityTypingEventHandler.OnAppendedAsync</c>, <c>MaintenanceEventSink.OnStoredAsync</c> and
/// <c>MaintenanceEventSink.OnAppendedAsync</c>. Both handlers' <c>OnForgottenAsync</c> is
/// <c>=> Task.CompletedTask;</c> — a no-op that never touches the store — so widening there would be
/// a dead cast that throws for a store the method does not use. Four is still enough that the cast
/// and its message live here rather than being written out four times. It throws rather than
/// returning null or skipping, because a handler that silently does not run is invisible data loss.
/// Containment differs by caller: <see cref="CompositeEventSink"/> isolates the throw for
/// <c>EntityTypingEventHandler</c>, whose exception propagates out of the handler; for
/// <c>MaintenanceEventSink</c>, which catches it in its own <c>try</c>/<c>catch</c> and logs it,
/// <c>CompositeEventSink</c> never sees the exception at all. Either way the blast radius is one
/// logged sink failure, not a failed write.</para>
/// </summary>
internal static class CoreSinkStore
{
    public static IMemoryStore Require(IPluginStore store, string handler)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store as IMemoryStore
            ?? throw new InvalidOperationException(
                $"{handler} is a Core-internal event handler and requires the full {nameof(IMemoryStore)}, " +
                $"not the narrowed {nameof(IPluginStore)} the sink contract declares. It was handed a " +
                $"{store.GetType().Name}.");
    }
}
