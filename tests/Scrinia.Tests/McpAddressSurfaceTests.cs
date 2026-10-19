using FluentAssertions;
using ModelContextProtocol;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class McpAddressSurfaceTests
{
    public static TheoryData<string> UnsupportedRecallInputs => new()
    {
        "file:///tmp/memory.nmp2",
        "https://example.test/memory",
        "//host/memory",
        "/memory?chunk=1",
        "/memory#chunk",
        "/memory/%2e%2e/secret",
        "NMP/2",
        "TAMIS/2 payload",
        "UNKNOWN/9 payload",
        "{\"artifact\":true}\n",
    };

    [Theory]
    [MemberData(nameof(UnsupportedRecallInputs))]
    public async Task Recall_RejectsArtifactAndUriFormsBeforeStoreLookup(string input)
    {
        MemoryStoreContext.Current = null;
        string yaml = await new ScriniaMcpTools().MemoryRecall(input);

        var response = ResponseParser.Parse(yaml);
        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be("UNSUPPORTED_INPUT_FORM");
    }

    [Fact]
    public async Task Recall_DecodesValidInlineNmp2WithoutStore()
    {
        string original = "inline artifact content";
        string yaml = await new ScriniaMcpTools().MemoryRecall(Nmp2ChunkedEncoder.Encode(original));

        var response = ResponseParser.Parse(yaml);
        response.Status.Should().Be("success");
        response.Content.Should().Be(original);
    }

    [Fact]
    public async Task Recall_TrimsOnlyInlineArtifactForm()
    {
        string artifact = $"  {Nmp2ChunkedEncoder.Encode("trimmed artifact")}  ";

        var response = ResponseParser.Parse(await new ScriniaMcpTools().MemoryRecall(artifact));

        response.Status.Should().Be("success");
        response.Content.Should().Be("trimmed artifact");
    }

    [Fact]
    public async Task Recall_UsesMemoryAddressParserRulesForLogicalAddressInput()
    {
        var response = ResponseParser.Parse(await new ScriniaMcpTools().MemoryRecall(" /ordinary-name "));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Theory]
    [InlineData("  /ordinary-name  ", "/ordinary-name")]
    [InlineData("  ~scratch  ", "~scratch")]
    public void LogicalAddressAdapter_TrimsAndCanonicalizesOnce(string raw, string expected)
    {
        ScriniaMcpTools.TryParseLogicalAddress(raw, out var address, out var error).Should().BeTrue();
        error.Should().BeNull();
        address.ToString().Should().Be(expected);
    }

    [Fact]
    public async Task Resource_RejectsUriBeforeRequiringStore()
    {
        Func<Task> action = () => ScriniaMcpResources.ReadMemory("file:///tmp/memory.nmp2");

        var exception = await action.Should().ThrowAsync<ModelContextProtocol.McpException>();
        exception.Which.Message.Should().Contain("UNSUPPORTED_INPUT_FORM");
    }

    [Fact]
    public void ActiveMcpSurface_IsExplicitlyEnumerated()
    {
        typeof(ScriniaMcpTools).GetMethods()
            .Where(method => method.GetCustomAttributes(false)
                .Any(attribute => attribute.GetType().Name == "McpServerToolAttribute"))
            .Select(method => method.Name)
            .Should().BeEquivalentTo(
                "Guide", "MemoryRemember", "MemoryRecall", "MemorySearch", "MemoryList",
                "MemoryForget", "MemoryCompact", "MemoryLink", "MemoryRestore",
                "Facts");

        typeof(ScriniaMcpResources).GetMethod(nameof(ScriniaMcpResources.ReadMemory))
            .Should().NotBeNull();
    }

    [Fact]
    public void InternalAddressOperations_RequireTypedAddresses()
    {
        string[] operations = ["Remember", "Recall", "UpdateMeta", "Forget", "Append", "Link", "Compact"];
        var methods = typeof(ScriniaMcpTools).GetMethods(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            .Where(method => operations.Contains(method.Name, StringComparer.Ordinal))
            .ToArray();

        methods.Should().NotBeEmpty();
        foreach (var method in methods)
        {
            method.GetParameters()
                .Where(parameter => parameter.Name is "name" or "nameOrUri" or "from" or "to" or "address")
                .Should().OnlyContain(parameter => parameter.ParameterType == typeof(Scrinia.Core.Models.MemoryAddress));
        }
    }

    [Fact]
    public void MemoryAddress_DoesNotExposeImplicitStringConversion()
    {
        typeof(MemoryAddress).GetMethods(System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Static)
            .Should().NotContain(method => method.Name == "op_Implicit" &&
                method.ReturnType == typeof(MemoryAddress) &&
                method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType == typeof(string));
    }

    [Theory]
    [InlineData(" /ordinary-name ", true, null)]
    [InlineData("~scratch", true, null)]
    [InlineData("/temp/scratch", true, null)]
    [InlineData("file:///tmp/memory", false, ErrorCodes.UnsupportedInputForm)]
    [InlineData("https://example.test/memory", false, ErrorCodes.UnsupportedInputForm)]
    [InlineData("//host/memory", false, ErrorCodes.UnsupportedInputForm)]
    [InlineData("/memory?chunk=1", false, ErrorCodes.UnsupportedInputForm)]
    [InlineData("/memory/%2e%2e/secret", false, ErrorCodes.UnsupportedInputForm)]
    [InlineData("/", false, ErrorCodes.MalformedAddress)]
    public void LogicalAddressAdapter_UsesTheAddressMatrixWithoutSideEffects(
        string input, bool expectedSuccess, string? expectedErrorCode)
    {
        bool parsed = ScriniaMcpTools.TryParseLogicalAddress(input, out var address, out var error);

        parsed.Should().Be(expectedSuccess);
        if (expectedSuccess)
        {
            address.IsValid.Should().BeTrue();
            error.Should().BeNull();
            return;
        }

        error.ErrorCode.Should().Be(expectedErrorCode);
        address.Should().Be(default(MemoryAddress));
    }

    [Fact]
    public async Task InvalidAddress_IsRejectedBeforeStoreMutation()
    {
        using var scope = new TestHelpers.StoreScope();
        string[] before = Directory.GetFiles(TestHelpers.Store.WorkspaceRoot, "*", SearchOption.AllDirectories);

        var response = ResponseParser.Parse(await new ScriniaMcpTools().MemoryRemember(
            "file:///tmp/not-a-memory", ["content"], description: "content"));

        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.UnsupportedInputForm);
        Directory.GetFiles(TestHelpers.Store.WorkspaceRoot, "*", SearchOption.AllDirectories)
            .Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task NullRequiredAddressInputs_ReturnCategorizedResponses()
    {
        var tools = new ScriniaMcpTools();

        foreach (string yaml in new[]
        {
            await tools.MemoryRemember(null, ["content"], description: "content"),
            await tools.MemoryRecall(null),
            await tools.MemoryAppend(null, "content"),
            await tools.MemoryForget(null),
            await tools.MemoryCompact(null),
            await tools.MemoryLink(null, "/target"),
            await tools.MemoryLink("/source", null)
        })
        {
            var response = ResponseParser.Parse(yaml);
            response.Status.Should().Be("error");
            response.ErrorCode.Should().BeOneOf(ErrorCodes.InvalidParameter, ErrorCodes.MalformedAddress);
        }
    }

    [Fact]
    public async Task NullContentInputs_ReturnCategorizedResponses()
    {
        var tools = new ScriniaMcpTools();

        var remember = ResponseParser.Parse(await tools.MemoryRemember("/address", null, description: "Test memory."));
        var append = ResponseParser.Parse(await tools.MemoryAppend("/address", null));

        remember.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
        append.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task ResourceNullAddress_IsReportedAsMalformedAddress()
    {
        Func<Task> action = () => ScriniaMcpResources.ReadMemory(null);

        var exception = await action.Should().ThrowAsync<ModelContextProtocol.McpException>();
        exception.Which.Message.Should().Contain(ErrorCodes.MalformedAddress);
    }
}
