using System.Text.Json;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class CliReviewGapTests
{
    private static async Task<(int Exit, string Json)> RunJsonAsync(Func<Task<int>> command)
    {
        var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await command();
            return (exit, output.ToString());
        }
        finally
        {
            Console.SetOut(prior);
        }
    }

    private static void AssertOnlyProperties(JsonElement output, params string[] expected)
    {
        output.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("store")]
    [InlineData("append-create")]
    [InlineData("compact")]
    public async Task PersistentSubjectOverTheDurableFilenameLimit_JsonCliEmitsCliErrorAndExitsOne(string route)
    {
        using var scope = new TestHelpers.StoreScope();
        string subject = new('x', MemoryWrite.MaxPersistentSubjectLength + 1);
        string contentFile = Path.Combine(scope.TempDir, "content.md");
        await File.WriteAllTextAsync(contentFile, "content");

        if (route == "compact")
            await SeedLegacyLongTargetAsync(subject);

        var (exit, json) = await RunJsonAsync(() => route switch
        {
            "store" => new ScriniaCommands().Store(subject, contentFile, scope.WorkspaceDir, json: true),
            "append-create" => new ScriniaCommands().Append(subject, contentFile, scope.WorkspaceDir, json: true),
            "compact" => new ScriniaCommands().Compact(subject, workspaceRoot: scope.WorkspaceDir, json: true),
            _ => throw new InvalidOperationException($"Unknown route '{route}'."),
        });

        exit.Should().Be(1);
        CliErrorOutput output = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliErrorOutput)!;
        output.Error.Should().Contain("exceeds 182 sanitized characters");
        using var document = JsonDocument.Parse(json);
        AssertOnlyProperties(document.RootElement, "error");
    }

    [Fact]
    public async Task Store_JsonSerializesTheRevisedGeneratedSchema()
    {
        using var scope = new TestHelpers.StoreScope();
        string content = "store json content";
        string contentFile = Path.Combine(scope.TempDir, "content.md");
        await File.WriteAllTextAsync(contentFile, content);

        var (exit, json) = await RunJsonAsync(() =>
            new ScriniaCommands().Store("/findings/store-json", contentFile, scope.WorkspaceDir, json: true));

        exit.Should().Be(0);
        CliStoreOutput output = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliStoreOutput)!;
        output.Name.Should().Be("/findings/store-json");
        output.OriginalBytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount(content));
        output.Message.Should().StartWith("status: success");
        using var document = JsonDocument.Parse(json);
        AssertOnlyProperties(document.RootElement, "name", "originalBytes", "message");
        await BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Store_JsonUsesThePersistedCanonicalNameWhenInputCaseDiffers()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["original content"], "MixedCase");
        string contentFile = Path.Combine(scope.TempDir, "content.md");
        await File.WriteAllTextAsync(contentFile, "replacement content");

        var (exit, json) = await RunJsonAsync(() =>
            new ScriniaCommands().Store("mixedcase", contentFile, scope.WorkspaceDir, json: true));

        exit.Should().Be(0);
        JsonSerializer.Deserialize(json, CliJsonContext.Default.CliStoreOutput)!.Name.Should().Be("MixedCase");
    }

    [Fact]
    public async Task Forget_JsonSerializesTheRevisedGeneratedSchema()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["forget json content"], "/findings/forget-json");

        var (exit, json) = await RunJsonAsync(() =>
            new ScriniaCommands().Forget("/findings/forget-json", scope.WorkspaceDir, json: true));

        exit.Should().Be(0);
        CliForgetOutput output = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliForgetOutput)!;
        output.Name.Should().Be("/findings/forget-json");
        output.Message.Should().StartWith("status: success");
        using var document = JsonDocument.Parse(json);
        AssertOnlyProperties(document.RootElement, "name", "message");
    }

    [Fact]
    public async Task Export_JsonSerializesTheRevisedGeneratedSchema()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["export json content"], "/api/export-json");

        var (exit, json) = await RunJsonAsync(() =>
            new ScriniaCommands().Export("api", scope.WorkspaceDir, json: true));

        exit.Should().Be(0);
        CliExportOutput output = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliExportOutput)!;
        output.Message.Should().StartWith("status: success");
        using var document = JsonDocument.Parse(json);
        AssertOnlyProperties(document.RootElement, "message");
    }

    [Fact]
    public async Task Reindex_JsonSerializesTheRevisedGeneratedSchema()
    {
        using var scope = new TestHelpers.StoreScope();

        var (exit, json) = await RunJsonAsync(() =>
            new ScriniaCommands().Reindex(scope.WorkspaceDir, json: true));

        exit.Should().Be(0);
        CliReindexOutput output = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliReindexOutput)!;
        output.Embedded.Should().BeLessThanOrEqualTo(output.Total);
        output.Skipped.Should().BeGreaterThanOrEqualTo(0);
        output.Failed.Should().Be(0);
        output.Message.Should().Be($"Embedded {output.Embedded}/{output.Total}, " +
                                  $"{output.Skipped} skipped, {output.Failed} failed.");
        using var document = JsonDocument.Parse(json);
        AssertOnlyProperties(document.RootElement, "embedded", "total", "skipped", "failed", "message");
    }

    [Fact]
    public async Task CompactWithoutEnoughChunks_ExitsOne()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["only chunk"], "/findings/no-op-compact");

        var (exit, json) = await RunJsonAsync(() =>
            new ScriniaCommands().Compact("/findings/no-op-compact", workspaceRoot: scope.WorkspaceDir, json: true));

        exit.Should().Be(1);
        JsonSerializer.Deserialize(json, CliJsonContext.Default.CliMcpOutput)!.Status.Should().Be("warning");
    }

    [Fact]
    public async Task CompactWithMultipleChunks_ExitsZero()
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().Remember(["first chunk", "second chunk"], "/findings/mutating-compact");

        var (exit, json) = await RunJsonAsync(() =>
            new ScriniaCommands().Compact("/findings/mutating-compact", workspaceRoot: scope.WorkspaceDir, json: true));

        exit.Should().Be(0);
        JsonSerializer.Deserialize(json, CliJsonContext.Default.CliMcpOutput)!.Status.Should().Be("success");
    }

    [Theory]
    [InlineData("success", 0)]
    [InlineData("warning", 1)]
    [InlineData("error", 1)]
    public async Task EmitMcpResult_ReturnsTheStatusAppropriateExitCode(string status, int expectedExit)
    {
        McpResponse response = status switch
        {
            "success" => ResponseBuilder.Success("done"),
            "warning" => ResponseBuilder.Warning("nothing changed"),
            "error" => ResponseBuilder.Error("failed"),
            _ => throw new InvalidOperationException($"Unknown status '{status}'."),
        };

        var (exit, json) = await RunJsonAsync(() =>
            Task.FromResult(ScriniaCommands.EmitMcpResult(response, json: true, action: "test")));

        exit.Should().Be(expectedExit);
        if (status == "error")
        {
            CliErrorOutput errorOutput = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliErrorOutput)!;
            errorOutput.Error.Should().Be("failed");
            return;
        }

        CliMcpOutput output = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliMcpOutput)!;
        output.Action.Should().Be("test");
        output.Status.Should().Be(status);
    }

    private static async Task SeedLegacyLongTargetAsync(string name)
    {
        var store = TestHelpers.Store;
        string artifact = Nmp2ChunkedEncoder.EncodeChunks(["first", "second"]);
        await File.WriteAllTextAsync(store.ArtifactPath(name, "local"), artifact);
        var entry = new ArtifactEntry(name, "", "firstsecond".Length, 2, DateTimeOffset.UtcNow);
        string sidecarPath = Path.Combine(store.GetStoreDirForScope("local"), MemoryNaming.SanitizeSegment(name) + ".meta.json");
        await File.WriteAllTextAsync(sidecarPath, JsonSerializer.Serialize(entry));
    }
}
