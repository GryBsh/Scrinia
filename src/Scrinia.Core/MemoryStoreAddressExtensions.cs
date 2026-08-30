using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Internal migration helpers for code that still needs physical scope inspection.</summary>
internal static class MemoryStoreAddressExtensions
{
    internal static (string Scope, string Subject) ParseQualifiedName(this IMemoryStore store, string name)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.ResolveAddress(MemoryAddress.Parse(name));
    }
}
