using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Centralized <see cref="IMemoryStore"/> construction so the CLI composition root
/// (<c>WorkspaceSetup</c>) stays a wiring layer rather than a store constructor.
/// The <c>IMultiMemoryParticipantAdapterFactory</c> overload matches the historical
/// constructor pairing in <see cref="FileMemoryStore"/>.
/// </summary>
internal static class StoreFactory
{
    internal static IMemoryStore Create(
        string storeRoot,
        RankerOptions rankerOptions,
        IMultiMemoryParticipantAdapterFactory? participantAdapterFactory = null)
        => participantAdapterFactory is null
            ? new FileMemoryStore(storeRoot, rankerOptions)
            : new FileMemoryStore(storeRoot, rankerOptions, null, participantAdapterFactory);
}
