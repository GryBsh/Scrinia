using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// The store surface an <see cref="IMemoryEventSink"/> is handed. Four members, because four is
/// what the sink path uses: typed address resolution and the ephemeral-aware read surface that
/// <see cref="Embeddings.MemoryArtifactSource.ReadForAppendAsync"/> needs.
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
    (string Scope, string Subject) ResolveAddress(MemoryAddress address);

    /// <summary>Reads an artifact identified by a parsed logical address.</summary>
    Task<string> ReadArtifactAsync(MemoryAddress address, CancellationToken ct = default);
}
