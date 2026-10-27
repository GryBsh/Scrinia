using System.Reflection;
using System.Text;
using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;

namespace Scrinia.Tests.Mcp;

public sealed class RecallPagingTests
{
    // Reflection keeps the RED test runnable before the new optional paging parameters exist.
    private static async Task<McpResponseAssert> Page(string path, int offset, int limit, int? chunk = null)
    {
        var method = typeof(ScriniaMcpTools).GetMethod(nameof(ScriniaMcpTools.MemoryRecall))!;
        method.GetParameters().Select(p => p.Name).Should().Contain(["offset", "limit"]);
        object?[] args = method.GetParameters().Select(p => p.Name switch
        {
            "path" => (object)path,
            "chunk" => chunk,
            "offset" => offset,
            "limit" => limit,
            "cancellationToken" => CancellationToken.None,
            _ => p.DefaultValue
        }).ToArray();
        return ResponseParser.Parse(await (Task<string>)method.Invoke(new ScriniaMcpTools(), args)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedContentCanBeReassembledWithoutMutation(bool chunked)
    {
        using var scope = new TestHelpers.StoreScope();
        string content = string.Concat(Enumerable.Repeat("evidence\r\n😀 details \n", 850));
        string artifact = chunked ? Nmp2ChunkedEncoder.EncodeChunks(["first", content]) : Nmp2ChunkedEncoder.Encode(content);
        var output = new StringBuilder();
        while (output.Length < content.Length)
        {
            var page = await Page(artifact, output.Length, 997, chunked ? 2 : null);
            page.Status.Should().Be("success");
            page.Content.Should().NotBeNullOrEmpty();
            page.Content!.Length.Should().BeLessThanOrEqualTo(997);
            char.IsHighSurrogate(page.Content[^1]).Should().BeFalse();
            output.Append(page.Content);
        }
        output.ToString().Should().Be(content);
        (await Page(artifact, content.Length, 997, chunked ? 2 : null)).Content.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 0)]
    [InlineData(0, 7000)]
    [InlineData(4, 100)]
    public async Task InvalidPageIsRejected(int offset, int limit)
    {
        var response = await Page(Nmp2ChunkedEncoder.Encode("abc"), offset, limit);
        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task PagingStoredChunkPreservesArtifactAndReturnsContinuation()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string content = new('x', 9000);
        await tools.MemoryRemember("/notes/paged", ["first", content], description: "Paging evidence.");
        var address = MemoryAddress.Parse("/notes/paged");
        string before = await TestHelpers.Store.ReadArtifactAsync(address);

        var first = await Page("/notes/paged", 0, 4096, 2);
        var second = await Page("/notes/paged", 4096, 4096, 2);
        var last = await Page("/notes/paged", 8192, 4096, 2);

        first.FollowUp.Should().ContainSingle(s => s.Contains("offset: 4096", StringComparison.Ordinal));
        (first.Content + second.Content + last.Content).Should().Be(content);
        last.FollowUp.Should().BeEmpty();
        (await TestHelpers.Store.ReadArtifactAsync(address)).Should().Be(before);
    }

    [Fact]
    public async Task PageCannotStartInsideSurrogatePair()
    {
        (await Page(Nmp2ChunkedEncoder.Encode("a😀b"), 2, 2)).ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
    }
}
