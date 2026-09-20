using Scrinia.Core;

namespace Scrinia.Tests;

public class AddressResolverPortTests
{
    [Fact]
    public void FileMemoryStore_Implements_IAddressResolver()
    {
        Assert.True(typeof(IAddressResolver).IsAssignableFrom(typeof(FileMemoryStore)));
    }
}
