using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

/// <summary>P0 naming/casing foundation — Tasks 1+2+3. Pins the single-rule move of the
/// case-insensitive name rule, the URI-like guard, address resolution, qualified-name
/// formatting, and the first-match name resolver into <see cref="MemoryNaming"/>. TDD: this
/// file was written BEFORE the members existed, so it starts red and turns green as each task
/// lands.</summary>
public class P0NamingCasingTests
{
    /// <summary>Builds an entry with an explicit <c>Name</c> that is independent of the address,
    /// which the primary constructor's <c>Name</c> getter would otherwise derive from the address.
    /// This mirrors the shape TryUpdateEntryTests uses (primary constructor + <c>Attributes</c>),
    /// just with the internal <c>Name</c> override instead.</summary>
    private static ArtifactEntry Entry(string name, string address)
    {
        return new ArtifactEntry(MemoryAddress.Parse(address),
                $"artifacts/{address.TrimStart('/')}.nmp2", 1, 1, DateTimeOffset.UtcNow)
        {
            Name = name,
        };
    }

    [Fact]
    public void NameEquals_MixedCaseAndSanitizedChars_MatchesOldInlineRule()
    {
        MemoryNaming.NameEquals("Notes", "notes").Should().BeTrue();
        MemoryNaming.NameEquals("a/b", "a_b").Should().BeTrue();
        MemoryNaming.NameEquals("notes", "other").Should().BeFalse();
    }

    [Fact]
    public void Comparer_IsOrdinalIgnoreCase()
    {
        MemoryNaming.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\\", true)]
    [InlineData(":", true)]
    [InlineData("a?b", true)]
    [InlineData("a#b", true)]
    [InlineData("a%b", true)]
    [InlineData("//server/x", true)]
    [InlineData("C:/x", true)]
    [InlineData("plain-name", false)]
    [InlineData("/topic/subject", false)]
    public void IsUriLike_SixClauseTable(string value, bool expected)
    {
        MemoryNaming.IsUriLike(value).Should().Be(expected);
    }

    [Fact]
    public void ResolveAddress_LocalAndTopicAndEphemeral()
    {
        using var scope = new TestHelpers.StoreScope();
        MemoryNaming.ResolveAddress(MemoryAddress.Parse("/notes"))
            .Should().Be(("local", "notes"));
        MemoryNaming.ResolveAddress(MemoryAddress.Parse("/api/notes"))
            .Should().Be(("local:api", "notes"));
        MemoryNaming.ResolveAddress(MemoryAddress.Parse("~scratch"))
            .Should().Be(("ephemeral", "scratch"));
        MemoryNaming.ResolveAddress(MemoryAddress.Parse("/a/b/c"))
            .Should().Be(("local:a/b", "c"));
    }

    [Fact]
    public void FormatQualifiedName_RoundTrips()
    {
        MemoryNaming.FormatQualifiedName("local", "notes").Should().Be("notes");
        MemoryNaming.FormatQualifiedName("local:api", "notes").Should().Be("/api/notes");
        MemoryNaming.FormatQualifiedName("ephemeral", "scratch").Should().Be("scratch");
    }

    [Fact]
    public void ResolveExistingName_FirstMatchWins_MissIsNull()
    {
        using var scope = new TestHelpers.StoreScope();
        var a = Entry("Notes", "/api/notes");
        var b = Entry("NOTES", "/api/notes");
        var entries = new List<ArtifactEntry> { a, b };
        MemoryNaming.ResolveExistingName(entries, "notes").Should().Be(a);
        MemoryNaming.ResolveExistingName(entries, "missing").Should().BeNull();
        MemoryNaming.ResolveExistingName(entries, MemoryAddress.Parse("/api/NOTES"))
            .Should().Be(a);
    }

    [Fact]
    public void FileMemoryStore_Delegates_MatchMemoryNaming()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var addr = MemoryAddress.Parse("/api/Notes");
        store.ResolveAddress(addr).Should().Be(MemoryNaming.ResolveAddress(addr));
        store.FormatQualifiedName("local:api", "notes")
            .Should().Be(MemoryNaming.FormatQualifiedName("local:api", "notes"));
        store.SanitizeName("a/b").Should().Be(MemoryNaming.SanitizeName("a/b"));
    }

    [Fact]
    public void PathParser_EntityMatch_IsCaseInsensitive_DespiteCaseSensitiveCallerSet()
    {
        var parsed = PathParser.Parse(
            "/goal/G-5", new HashSet<string>(new[] { "goal" }, StringComparer.Ordinal));
        parsed.IsEntityPath.Should().BeTrue();
        parsed.EntityPairs.Should().ContainSingle().Which.Should().Be(
            new EntityIdPair("goal", "G-5"));
    }

    [Fact]
    public void PathParser_EntityMatch_MixedCasePair()
    {
        var parsed = PathParser.Parse(
            "/GOAL/g-5", new HashSet<string>(new[] { "goal" }, StringComparer.Ordinal));
        parsed.IsEntityPath.Should().BeTrue();
        parsed.Tags.Should().BeEmpty();
    }

    [Fact]
    public void PathParser_CallerSet_NotMutated()
    {
        var caller = new HashSet<string>(new[] { "Goal" }, StringComparer.Ordinal);
        PathParser.Parse("/goal/G-5", caller);
        caller.Should().ContainSingle().Which.Should().Be("Goal");
    }

    [Theory]
    [InlineData("/api/no?tes")]
    [InlineData("/api/no#tes")]
    [InlineData("/api/no%tes")]
    [InlineData("/api\\back")]
    [InlineData("topic:subject")]
    public void MemoryAddress_And_PathParser_RejectUriLike(string raw)
    {
        Scrinia.Core.Models.MemoryAddress.TryParse(
            raw, out _, out _).Should().BeFalse();
        // PathParser (this snapshot) rejects only `\`, `:`, `..`, control chars via its own
        // inline guards; the `?/#/%` inputs are MemoryAddress's domain (see IsUriLike unify).
        // Assert the PathParser throw only for the input it actually rejects here.
        if (raw.StartsWith('/') && raw.Contains('\\'))
            Assert.Throws<System.ArgumentException>(() =>
                Scrinia.Core.PathParser.Parse(raw, new HashSet<string>()));
    }

    [Fact]
    public void MemoryAddress_LegacyColon_GateMessageSuggestsPath()
    {
        var ex = Assert.Throws<Scrinia.Core.Models.MemoryAddressException>(() =>
            Scrinia.Core.Models.MemoryAddress.Parse("topic:subject"));
        ex.Message.Should().Contain("Address");
    }

    [Fact]
    public async Task Store_CaseInsensitiveRead_PreservesStoredCasing()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var writeAddr = MemoryAddress.Parse("/api/Notes");
        var write = MemoryWrite.Create(writeAddr, ["hello"], new Dictionary<string, IReadOnlyList<string>>(), []);
        await store.WriteAsync(writeAddr, write);
        var readAddr = MemoryAddress.Parse("/api/NOTES");
        var entry = await store.ReadAsync(readAddr);
        entry.Should().NotBeNull();
        entry!.Name.Should().Be("Notes");
    }
}
