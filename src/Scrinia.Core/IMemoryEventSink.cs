using Scrinia.Core.Models;
using System.Diagnostics.CodeAnalysis;

namespace Scrinia.Core;

/// <summary>
/// Plugins implement this to react to store/append/forget events on the MCP code path.
/// The REST path uses <c>IMemoryOperationHook</c> via <c>PluginPipeline</c> instead.
/// Both paths should delegate to the same plugin logic internally to avoid duplication.
/// </summary>
public interface IMemoryEventSink
{
    Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct) =>
        MemoryEventSinkCompatibilityBridge.Invoke(this, nameof(OnStoredAsync), address, content, store, ct);
    Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
        MemoryEventSinkCompatibilityBridge.Invoke(this, nameof(OnAppendedAsync), address, content, store, ct);
    Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
        MemoryEventSinkCompatibilityBridge.Invoke(this, nameof(OnForgottenAsync), address, wasDeleted, store, ct);
}

internal static class MemoryEventSinkCompatibilityBridge
{
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Legacy compatibility bridge is only used for pre-typed in-process test/plugin implementations.")]
    internal static Task Invoke(IMemoryEventSink sink, string methodName, MemoryAddress address,
        object payload, IPluginStore store, CancellationToken ct)
    {
        var payloadType = payload is bool ? typeof(bool) : payload.GetType();
        var method = sink.GetType().GetMethod(methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
            [typeof(string), payloadType, typeof(IPluginStore), typeof(CancellationToken)]);
        return method?.Invoke(sink, [address.ToString(), payload, store, ct]) as Task ?? Task.CompletedTask;
    }
}

internal static class MemoryEventSinkCompatibilityExtensions
{
    internal static Task OnStoredAsync(this IMemoryEventSink sink, string rawAddress, string[] content,
        IPluginStore store, CancellationToken ct) =>
        sink.OnStoredAsync(MemoryAddress.Parse(rawAddress), content, store, ct);

    internal static Task OnAppendedAsync(this IMemoryEventSink sink, string rawAddress, string content,
        IPluginStore store, CancellationToken ct) =>
        sink.OnAppendedAsync(MemoryAddress.Parse(rawAddress), content, store, ct);

    internal static Task OnForgottenAsync(this IMemoryEventSink sink, string rawAddress, bool wasDeleted,
        IPluginStore store, CancellationToken ct) =>
        sink.OnForgottenAsync(MemoryAddress.Parse(rawAddress), wasDeleted, store, ct);
}

/// <summary>
/// AsyncLocal context for making <see cref="IMemoryEventSink"/> available
/// to MCP tools without passing it through every method.
/// <para>
/// <see cref="Current"/> is an AsyncLocal, so a host that serves concurrent callers sets it per
/// call rather than once per process.
/// In the CLI, AsyncLocal doesn't propagate through the generic host to MCP tool handlers,
/// so <see cref="Default"/> provides a process-wide fallback.
/// </para>
/// </summary>
public static class MemoryEventSinkContext
{
    private static readonly AsyncLocal<IMemoryEventSink?> _current = new();
    private static IMemoryEventSink? _default;

    /// <summary>Gets/sets the event sink for the current async context, falling back to <see cref="Default"/>.</summary>
    public static IMemoryEventSink? Current { get => _current.Value ?? _default; set => _current.Value = value; }

    /// <summary>Process-wide default used when no AsyncLocal value is set (CLI single-session mode).</summary>
    public static IMemoryEventSink? Default { get => _default; set => _default = value; }

    /// <summary>Installs the process-wide default sink as a composite over <paramref name="sinks"/>.
    /// The one place <see cref="Default"/> is assigned from — the call sites in WorkspaceSetup each
    /// built their own CompositeEventSink, so a change to how the default is composed had that many
    /// places to miss. There are eight today (<c>WorkspaceSetup.cs</c> :240 :400 :417 :426 :446
    /// :463 :472 :528), all inside <c>LoadPluginsAsync</c> and its helpers; this comment said "six"
    /// for two of them longer than it was true, so prefer the count that is checkable over one
    /// written into prose.
    /// The guards below exist because centralising those call sites turned that many independent
    /// chances to notice a bad argument into one trap for the next caller.</summary>
    public static void InstallDefault(params IMemoryEventSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        if (sinks.Length == 0)
            throw new ArgumentException(
                "At least one sink is required — an empty composite silently discards every memory event.",
                nameof(sinks));
        if (Array.IndexOf(sinks, null) >= 0)
            throw new ArgumentException(
                "Sinks must not contain nulls — CompositeEventSink's per-sink catch dereferences the "
                + "sink to name it, so a null escapes the catch and aborts every later sink.",
                nameof(sinks));

        var uniqueSinks = new List<IMemoryEventSink>(sinks.Length);
        var seen = new HashSet<IMemoryEventSink>(ReferenceEqualityComparer.Instance);
        foreach (var sink in sinks)
            if (seen.Add(sink))
                uniqueSinks.Add(sink);

        Default = new CompositeEventSink([.. uniqueSinks]);
    }
}
