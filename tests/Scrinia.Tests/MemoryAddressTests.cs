using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class MemoryAddressTests
{
    [Theory]
    [InlineData("/notes/design", "/notes/design", MemoryAddressKind.Persistent)]
    [InlineData("/temp/foo", "foo", MemoryAddressKind.Ephemeral)]
    [InlineData("~foo", "foo", MemoryAddressKind.Ephemeral)]
    public void Parse_ReturnsCanonicalAddress(string raw, string value, MemoryAddressKind kind)
    {
        var address = MemoryAddress.Parse(raw);

        address.Value.Should().Be(value);
        address.Kind.Should().Be(kind);
    }

    [Fact]
    public void EphemeralAliases_AreEqualAndRenderCanonically()
    {
        var path = MemoryAddress.Parse("/temp/foo");
        var name = MemoryAddress.Parse("~foo");

        path.Should().Be(name);
        path.GetHashCode().Should().Be(name.GetHashCode());
        path.ToString().Should().Be("~foo");
        name.ToString().Should().Be("~foo");
    }

    [Fact]
    public void PersistentAddress_RendersAsLogicalPath()
    {
        var address = MemoryAddress.Parse(" /notes/design ");

        address.ToString().Should().Be("/notes/design");
        JsonSerializer.Serialize(address).Should().Be("\"/notes/design\"");
    }

    [Fact]
    public void EphemeralAddress_SerializesAsCanonicalName()
    {
        var address = MemoryAddress.Parse("/temp/foo");

        JsonSerializer.Serialize(address).Should().Be("\"~foo\"");
        JsonSerializer.Deserialize<MemoryAddress>("\"~foo\"").Should().Be(address);
    }

    [Theory]
    [MemberData(nameof(MalformedAddresses))]
    public void TryParse_RejectsMalformedAddress(string? raw)
    {
        var parsed = MemoryAddress.TryParse(raw, out var address, out var error);

        parsed.Should().BeFalse();
        address.Value.Should().BeNull();
        error.Category.Should().Be(MemoryErrorCategory.MalformedAddress);
        Action parse = () => MemoryAddress.Parse(raw!);
        parse.Should().Throw<MemoryAddressException>()
            .Which.Category.Should().Be(MemoryErrorCategory.MalformedAddress);
    }

    public static TheoryData<string?> MalformedAddresses => new()
    {
        "/temp", "/temp/foo/bar", "~", "", "   ", "/", ".", "..", "/./x", "/../x",
        "a/../b", "a\\b", "C:\\x", "\\\\server\\share", "file:///x", "https://x",
        "scheme:value", "//authority", "/x?q", "/x#f", "/x%2Fy", "/%2e%2e/x",
        "/x\0y", "/x\ny", "/x//y", "/bad*name", "/bad:name", "/bad?name",
        "/bad<name>", "/bad|name", "/bad\"name"
    };

    [Fact]
    public void TryParse_RejectsAddressOverUtf8Limit()
    {
        var raw = "/" + new string('界', MemoryAddress.MaxUtf8Bytes);

        MemoryAddress.TryParse(raw, out var address, out var error).Should().BeFalse();
        address.Value.Should().BeNull();
        error.Category.Should().Be(MemoryErrorCategory.MalformedAddress);
    }
}
