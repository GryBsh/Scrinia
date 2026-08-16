using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// The regression tests the suite lacked. Every pre-existing ephemeral test writes at "/temp/x"
/// and reads back at "~x", which is why H2 survived 2,015 tests: no test used the SAME spelling
/// twice. These do.
/// </summary>
public class EphemeralAliasRoundTripTests
{
    [Fact]
    public async Task Recall_FindsAMemoryRememberedAtTheSameTempPath()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["the secret note"]);

        string recalled = await tools.MemoryRecall("/temp/probe");

        Assert.Contains("the secret note", recalled, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT_FOUND", recalled, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forget_DeletesAMemoryRememberedAtTheSameTempPath()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["gone soon"]);

        string forgotten = await tools.MemoryForget("/temp/probe");

        Assert.DoesNotContain("NOT_FOUND", forgotten, StringComparison.Ordinal);
        Assert.Contains("NOT_FOUND", await tools.MemoryRecall("/temp/probe"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_ExtendsAMemoryRememberedAtTheSameTempPath()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["first"]);

        string appended = await tools.MemoryAppend("/temp/probe", "second");

        Assert.DoesNotContain("NOT_FOUND", appended, StringComparison.Ordinal);
        string recalled = await tools.MemoryRecall("/temp/probe");
        Assert.Contains("first", recalled, StringComparison.Ordinal);
        Assert.Contains("second", recalled, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compact_MergesAMemoryRememberedAtTheSameTempPath()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["one", "two"]);

        string compacted = await tools.MemoryCompact("/temp/probe");

        Assert.DoesNotContain("NOT_FOUND", compacted, StringComparison.Ordinal);
    }

    /// <summary>link carries TWO addresses; both ends must canonicalize.</summary>
    [Fact]
    public async Task Link_ResolvesBothEndsAtTheSameTempPaths()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/alpha", ["a"]);
        await tools.MemoryRemember("/temp/beta", ["b"]);

        string linked = await tools.MemoryLink("/temp/alpha", "/temp/beta");

        Assert.DoesNotContain("NOT_FOUND", linked, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing was written", linked, StringComparison.Ordinal);
    }

    /// <summary>D-3: the response teaches the caller the real address.</summary>
    [Fact]
    public async Task Remember_EchoesTheCanonicalFormNotTheCallerSpelling()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();

        string response = await tools.MemoryRemember("/temp/probe", ["x"]);

        Assert.Contains("~probe", response, StringComparison.Ordinal);
        Assert.DoesNotContain("/temp/probe", response, StringComparison.Ordinal);
    }

    /// <summary>Both spellings address one memory — not two.</summary>
    [Fact]
    public async Task TheTwoSpellingsAddressTheSameMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/temp/probe", ["written via the alias"]);

        Assert.Contains("written via the alias", await tools.MemoryRecall("~probe"), StringComparison.Ordinal);
    }

    /// <summary>A near-miss must stay persistent, or the prefix check is too greedy.</summary>
    [Fact]
    public async Task ANearMissPathIsStillPersistent()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();

        string response = await tools.MemoryRemember("/tempest/probe", ["x"]);

        Assert.DoesNotContain("~", response, StringComparison.Ordinal);
    }
}
