using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Formats;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class ReconcileDurableFormatTests
{
    private static ScriniaMcpTools Tools() => new();

    private static async Task<(string ConflictId, string Path, string Content)> SeedConflictAsync(
        TestHelpers.StoreScope scope, string oursJson, string theirsJson)
    {
        string dir = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "local");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "auth-flow.meta.json");
        string content = "<<<<<<< HEAD\n" + oursJson + "\n=======\n" + theirsJson + "\n>>>>>>> theirs\n";
        await File.WriteAllTextAsync(path, content);
        return ("memories/local/auth-flow.meta.json", path, content);
    }

    private static string Sidecar(int version, string? extension = null) => $$"""
        {"name":"auth-flow","address":"/auth-flow","artifactKey":"artifacts/auth-flow.nmp2","originalBytes":10,"chunkCount":1,
        "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z",
        "keywords":["auth"],"v":{{version}}{{extension ?? ""}}}
        """;

    [Fact]
    public async Task Reconcile_AutoResolutionWithNewerArtifactSidecar_RefusesBeforeReplacingConflictBytes()
    {
        using var scope = new TestHelpers.StoreScope();
        var conflict = await SeedConflictAsync(
            scope,
            Sidecar(DurableFormats.ArtifactSidecar.Current + 1),
            Sidecar(DurableFormats.ArtifactSidecar.Current + 1));
        byte[] originalConflictBytes = File.ReadAllBytes(conflict.Path);

        string result = await Tools().MemoryReconcile(conflictId: conflict.ConflictId);

        var response = ResponseParser.Parse(result);
        response.Status.Should().Be("error");
        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        response.Error.Should().Contain("supports up to");
        response.ActionNeeded.Should().ContainSingle()
             .Which.Should().ContainAll("Upgrade", "re-export");
        (await File.ReadAllTextAsync(conflict.Path)).Should().Be(conflict.Content);
        File.ReadAllBytes(conflict.Path).Should().Equal(originalConflictBytes);
    }

    [Theory]
    [InlineData("ours")]
    [InlineData("theirs")]
    public async Task Reconcile_ExplicitSideWithNewerArtifactSidecar_RefusesBeforeReplacingConflictBytes(string choice)
    {
        using var scope = new TestHelpers.StoreScope();
        var conflict = await SeedConflictAsync(
            scope,
            Sidecar(DurableFormats.ArtifactSidecar.Current + 1),
            Sidecar(DurableFormats.ArtifactSidecar.Current + 1));
        byte[] originalConflictBytes = File.ReadAllBytes(conflict.Path);

        string result = await Tools().MemoryReconcile(conflictId: conflict.ConflictId, choice: choice);

        ResponseParser.Parse(result).ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        (await File.ReadAllTextAsync(conflict.Path)).Should().Be(conflict.Content);
        File.ReadAllBytes(conflict.Path).Should().Equal(originalConflictBytes);
    }

    [Fact]
    public async Task Reconcile_ExplicitMergedNewerArtifactSidecar_RefusesBeforeReplacingConflictBytes()
    {
        using var scope = new TestHelpers.StoreScope();
        var conflict = await SeedConflictAsync(
            scope,
            Sidecar(DurableFormats.ArtifactSidecar.Current),
            Sidecar(DurableFormats.ArtifactSidecar.Current));
        byte[] originalConflictBytes = File.ReadAllBytes(conflict.Path);

        string result = await Tools().MemoryReconcile(
            conflictId: conflict.ConflictId,
            choice: "merged",
            mergedContent: Sidecar(DurableFormats.ArtifactSidecar.Current + 1));

        ResponseParser.Parse(result).ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        (await File.ReadAllTextAsync(conflict.Path)).Should().Be(conflict.Content);
        File.ReadAllBytes(conflict.Path).Should().Equal(originalConflictBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task Reconcile_AutoResolutionWithSupportedSidecarVersion_PreservesUnknownExtensionFields(int version)
    {
        using var scope = new TestHelpers.StoreScope();
        var conflict = await SeedConflictAsync(
            scope,
            Sidecar(version, ",\"oursExtension\":{\"enabled\":true}"),
            Sidecar(version, ",\"theirsExtension\":[\"preserve\"]"));

        string result = await Tools().MemoryReconcile(conflictId: conflict.ConflictId);

        ResponseParser.Parse(result).Status.Should().Be("success");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(conflict.Path));
        document.RootElement.GetProperty("oursExtension").GetProperty("enabled").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("theirsExtension").EnumerateArray()
            .Select(value => value.GetString()).Should().Equal("preserve");
    }
}
