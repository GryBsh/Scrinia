using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// The store surface an <see cref="IMemoryEventSink"/> is handed. Four members, because four is
/// what the sink path uses: <c>ParseQualifiedName</c> to key the write, and the ephemeral-aware
/// read trio that <see cref="Embeddings.MemoryArtifactSource.ReadForAppendAsync"/> needs.
///
/// <para>Sinks used to receive the whole <see cref="IMemoryStore"/> — 30 instance members — which
/// said nothing about what a sink may rely on. Narrowing states the contract. It does not enforce
/// it: a sink can still cast back, and Core's own two handlers do exactly that (see
/// <see cref="CoreSinkStore"/>). What changes is that widening is now a visible cast rather than
/// an invisible use of an already-available member.</para>
///
/// <para><see cref="IMemoryStore"/> derives from this, so every concrete store satisfies both and
/// no adapter is needed at the call sites that hand a store to a sink.</para>
/// </summary>
public interface IPluginStore
{
    MemoryAddress ParseAddress(string rawAddress) => MemoryAddress.Parse(rawAddress);

    /// <summary>Reads an artifact identified by a parsed logical address.</summary>
    Task<string> ReadArtifactAsync(MemoryAddress address, CancellationToken ct = default) =>
        throw new NotSupportedException("Typed artifact reads must be implemented by the store.");
}

/// <summary>Temporary source compatibility helpers for the physical store migration.</summary>
internal static class PluginStoreCompatibilityExtensions
{
    internal static (string Scope, string Subject) ParseQualifiedName(this IPluginStore store, string raw) =>
        (store.IsEphemeral(raw) ? "ephemeral" : "local", raw);

    internal static bool IsEphemeral(this IPluginStore store, string raw) =>
        MemoryAddress.Parse(raw).Kind == MemoryAddressKind.Ephemeral;

    internal static Task<string> ResolveArtifactAsync(this IPluginStore store, string raw,
        CancellationToken ct = default) =>
        store.ReadArtifactAsync(MemoryAddress.Parse(raw), ct);

    internal static Task<string> ReadArtifactAsync(this IPluginStore store, string subject, string scope,
        CancellationToken ct = default) =>
        store.ReadArtifactAsync(MemoryAddress.Parse(subject.StartsWith('/') ? subject : $"/{subject}"), ct);
}
