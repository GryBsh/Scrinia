using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>
/// Narrow port for the single address-resolution back-reference used by the
/// transactional coordinator to resolve a recovery candidate's subject without
/// depending on the concrete <see cref="FileMemoryStore"/> type.
/// </summary>
internal interface IAddressResolver
{
    (string Scope, string Subject) ResolveAddress(MemoryAddress address);
}
