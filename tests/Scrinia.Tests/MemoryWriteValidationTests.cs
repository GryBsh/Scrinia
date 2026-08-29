using System.Text;
using FluentAssertions;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class MemoryWriteValidationTests
{
    private static readonly MemoryAddress Address = MemoryAddress.Parse("/notes/validation");

    [Fact]
    public void EmptyContent_IsRejectedWithCategorizedError()
    {
        var result = MemoryWrite.TryCreate(Address, [], new Dictionary<string, IReadOnlyList<string>>(), [], DefaultOptions);

        result.Error.Should().BeEquivalentTo(new MemoryError(
            MemoryErrorCategory.InvalidWriteContent, "empty_content", "Content must contain at least one chunk.",
            "content", 1, 0));
    }

    [Fact]
    public void NullContentElement_IsRejectedWithItsActualIndex()
    {
        var result = MemoryWrite.TryCreate(Address, new string?[] { null }, new Dictionary<string, IReadOnlyList<string>>(), [], DefaultOptions);

        result.Error.Should().BeEquivalentTo(new MemoryError(
            MemoryErrorCategory.InvalidWriteContent, "null_content_chunk", "Content chunks must not be null.",
            "content[0]", null, null));
    }

    [Fact]
    public void NullMetadataCollections_AreRejectedAsInvalidWriteContent()
    {
        MemoryWrite.TryCreate(Address, ["body"], null, [], DefaultOptions).Error!.Code.Should().Be("attributes_null");
        MemoryWrite.TryCreate(Address, ["body"], new Dictionary<string, IReadOnlyList<string>>(), null, DefaultOptions)
            .Error!.Code.Should().Be("keywords_null");
    }

    [Fact]
    public void ContentLimits_ReportFieldLimitAndActualBytes()
    {
        var options = DefaultOptions with { MaxContentBytesPerChunk = 3, MaxTotalContentBytes = 4 };

        var perChunk = MemoryWrite.TryCreate(Address, ["éé"], new Dictionary<string, IReadOnlyList<string>>(), [], options);
        perChunk.Error.Should().BeEquivalentTo(new MemoryError(
            MemoryErrorCategory.InvalidWriteContent, "content_chunk_too_large", "Content chunk exceeds its UTF-8 byte limit.",
            "content[0]", 3, 4));

        var total = MemoryWrite.TryCreate(Address, ["ab", "cd", "e"], new Dictionary<string, IReadOnlyList<string>>(), [], DefaultOptions with { MaxTotalContentBytes = 4 });
        total.Error.Should().BeEquivalentTo(new MemoryError(
            MemoryErrorCategory.InvalidWriteContent, "content_total_too_large", "Total content exceeds its UTF-8 byte limit.",
            "content", 4, 5));
    }

    [Fact]
    public void ChunkAndEncodedLimits_AreEnforcedInOrder()
    {
        var tooMany = MemoryWrite.TryCreate(Address, ["a", "b", "c"], new Dictionary<string, IReadOnlyList<string>>(), [],
            DefaultOptions with { MaxChunks = 2 });
        tooMany.Error.Should().BeEquivalentTo(new MemoryError(
            MemoryErrorCategory.InvalidWriteContent, "too_many_chunks", "Content contains too many chunks.",
            "content", 2, 3));

        var encoded = MemoryWrite.TryCreate(Address, ["a"], new Dictionary<string, IReadOnlyList<string>>(), [],
            DefaultOptions with { MaxEncodedChunkBytes = 1 });
        encoded.Error!.Field.Should().Be("content[0].encoded");
        encoded.Error.Limit.Should().Be(1);
        encoded.Error.Actual.Should().BeGreaterThan(1);
    }

    [Fact]
    public void AddressAttributesAndKeywords_UseSharedPolicy()
    {
        var longAddress = MemoryAddress.Parse('/' + new string('x', 20));
        var options = DefaultOptions with { MaxAddressBytes = 5, MaxAttributeKeyLength = 3, MaxAttributeValueLength = 3, MaxKeywordLength = 3 };

        MemoryWrite.TryCreate(longAddress, ["ok"], new Dictionary<string, IReadOnlyList<string>>(), [], options).Error!.Field.Should().Be("address");
        MemoryWrite.TryCreate(Address, ["ok"], new Dictionary<string, IReadOnlyList<string>> { ["long"] = ["ok"] }, [], options with { MaxAddressBytes = MemoryAddress.MaxUtf8Bytes })
            .Error!.Field.Should().Be("attributes.key");
        MemoryWrite.TryCreate(Address, ["ok"], new Dictionary<string, IReadOnlyList<string>> { ["key"] = ["long"] }, [], options with { MaxAddressBytes = MemoryAddress.MaxUtf8Bytes })
            .Error!.Field.Should().Be("attributes[key][0]");
        MemoryWrite.TryCreate(Address, ["ok"], new Dictionary<string, IReadOnlyList<string>>(), ["long"], options with { MaxAddressBytes = MemoryAddress.MaxUtf8Bytes }).Error!.Field.Should().Be("keywords[0]");
    }

    [Fact]
    public void ValidRememberAndSingleChunkAppend_AreAcceptedWithoutMutation()
    {
        var remember = MemoryWrite.TryCreate(Address, ["first", "second"], new Dictionary<string, IReadOnlyList<string>>(), ["keyword"], DefaultOptions);
        var append = MemoryWrite.TryCreate(Address, ["third"], new Dictionary<string, IReadOnlyList<string>>(), [], DefaultOptions, isAppend: true);

        remember.IsValid.Should().BeTrue();
        append.IsValid.Should().BeTrue();
        remember.Write!.Content.Select(chunk => Encoding.UTF8.GetString(chunk.Span)).Should().Equal("first", "second");
        append.Write!.Content.Should().ContainSingle();
    }

    [Fact]
    public async Task FileMemoryStore_ValidatesBeforeCreatingProductionArtifacts()
    {
        var valid = MemoryWrite.TryCreate(Address, ["body"],
            new Dictionary<string, IReadOnlyList<string>>(), [], DefaultOptions).Write!;
        string root = Path.Combine(Path.GetTempPath(), "scrinia-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new FileMemoryStore(root,
            writeValidationOptions: DefaultOptions with { MaxContentBytesPerChunk = 1 });

        await FluentActions.Invoking(() => store.StoreAsync(valid)).Should().ThrowAsync<InvalidMemoryWriteException>();

        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();
        store.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private static MemoryValidationOptions DefaultOptions => new();
}
