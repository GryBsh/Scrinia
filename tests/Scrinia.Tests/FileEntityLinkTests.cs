using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class FileEntityLinkTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope = new();
    private readonly ScriniaMcpTools _tools = new();

    public void Dispose() => _scope.Dispose();

    [Theory]
    [InlineData("/agent/profile", "/notes/source")]
    [InlineData("/notes/source", "/agent/profile")]
    [InlineData("/agent/profile", "/agent/preferences")]
    [InlineData("/skill/custom", "/notes/source")]
    [InlineData("/agent/profile", "/skill/custom")]
    [InlineData("/agent/profile", "~scratch")]
    public async Task Link_PersistsBothReferencesWithoutChangingBodies(string from, string to)
    {
        (await _tools.MemoryRemember(from, ["source body"])).Should().NotContain("status: error");
        (await _tools.MemoryRemember(to, ["destination body"])).Should().NotContain("status: error");

        byte[]? beforeFrom = IsFileEntity(from) ? File.ReadAllBytes(BodyPath(from)) : null;
        byte[]? beforeTo = IsFileEntity(to) ? File.ReadAllBytes(BodyPath(to)) : null;
        string result = await _tools.MemoryLink(from, to);

        result.Should().Contain("action: linked");
        References(from).Should().Contain(to);
        References(to).Should().Contain(from);
        (await _tools.MemoryRecall(from)).Should().Contain("source body");
        (await _tools.MemoryRecall(to)).Should().Contain("destination body");
        if (beforeFrom is not null) File.ReadAllBytes(BodyPath(from)).Should().Equal(beforeFrom);
        if (beforeTo is not null) File.ReadAllBytes(BodyPath(to)).Should().Equal(beforeTo);
    }

    [Theory]
    [InlineData("/agent/profile")]
    [InlineData("/skill/custom")]
    public async Task Link_UnionIsIdempotentAndSurvivesContentWrites(string path)
    {
        await _tools.MemoryRemember(path, ["original body"]);
        await _tools.MemoryRemember("/notes/one", ["first note"]);
        await _tools.MemoryRemember("/notes/two", ["second note"]);
        string metaPath = Path.ChangeExtension(BodyPath(path), ".meta.json");
        var meta = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metaPath))!;
        meta["extension"] = "preserve me";
        File.WriteAllText(metaPath, meta.ToJsonString());

        (await _tools.MemoryLink(path, "/notes/one")).Should().Contain("action: linked");
        (await _tools.MemoryLink(path, "/notes/two")).Should().Contain("action: linked");
        (await _tools.MemoryLink(path, "/notes/one")).Should().Contain("action: linked");
        References(path).Should().BeEquivalentTo("/notes/one", "/notes/two");

        await _tools.MemoryAppend(path, "appended");
        await _tools.MemoryRemember(path, ["replacement body"], replace: true);
        References(path).Should().BeEquivalentTo("/notes/one", "/notes/two");
        using var saved = JsonDocument.Parse(File.ReadAllText(metaPath));
        saved.RootElement.GetProperty("extension").GetString().Should().Be("preserve me");
        File.ReadAllText(BodyPath(path)).Should().Contain("replacement body").And.NotContain("original body");
    }

    [Theory]
    [InlineData("/agent/profile", "/notes/missing")]
    [InlineData("/notes/present", "/agent/missing")]
    [InlineData("/skill/custom", "/agent/missing")]
    public async Task Link_MissingEndpointLeavesExistingMetadataUntouched(string from, string to)
    {
        await _tools.MemoryRemember(from, ["existing body"]);
        string before = Metadata(from);

        (await _tools.MemoryLink(from, to)).Should().Contain("NOT_FOUND");

        Metadata(from).Should().Be(before);
    }

    [Theory]
    [InlineData("""{"ref":"invalid"}""")]
    [InlineData("""{"v":999}""")]
    [InlineData("""{"ref":["/notes/old"],"REF":["/notes/other"]}""")]
    public async Task Link_UnwritableFileMetadataRefusesBeforeWritingOtherEndpoint(string malformed)
    {
        await _tools.MemoryRemember("/notes/source", ["source body"]);
        await _tools.MemoryRemember("/agent/profile", ["agent body"]);
        string metaPath = Path.ChangeExtension(BodyPath("/agent/profile"), ".meta.json");
        File.WriteAllText(metaPath, malformed);
        string before = Metadata("/notes/source");

        string result = await _tools.MemoryLink("/notes/source", "/agent/profile");

        result.Should().Contain("status: error");
        Metadata("/notes/source").Should().Be(before);
        File.ReadAllText(metaPath).Should().Be(malformed);
        File.ReadAllText(BodyPath("/agent/profile")).Should().Be("agent body");
    }

    [Fact]
    public async Task Link_ConcurrentUpdatesPreserveBothReferences()
    {
        await _tools.MemoryRemember("/agent/profile", ["profile"]);
        await _tools.MemoryRemember("/notes/one", ["one"]);
        await _tools.MemoryRemember("/notes/two", ["two"]);

        string[] results = await Task.WhenAll(
            Task.Run(() => _tools.MemoryLink("/agent/profile", "/notes/one")),
            Task.Run(() => _tools.MemoryLink("/agent/profile", "/notes/two")));

        results.Should().OnlyContain(result => result.Contains("action: linked"));
        References("/agent/profile").Should().BeEquivalentTo("/notes/one", "/notes/two");
        References("/notes/one").Should().Contain("/agent/profile");
        References("/notes/two").Should().Contain("/agent/profile");
    }

    [Fact]
    public async Task Link_ExistingIndexedAgentRemainsSupported()
    {
        var store = MemoryStoreContext.Current!;
        await store.StoreAsync(new MemoryWrite(MemoryAddress.Parse("/agent/legacy"),
            ["legacy body"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), []));
        await _tools.MemoryRemember("/notes/source", ["source"]);

        (await _tools.MemoryLink("/agent/legacy", "/notes/source")).Should().Contain("action: linked");

        var (scope, name) = store.ResolveAddress(MemoryAddress.Parse("/agent/legacy"));
        store.LoadIndex(scope).Single(entry => entry.Name == name).Attributes!["ref"]
            .Should().Contain("/notes/source");
        References("/notes/source").Should().Contain("/agent/legacy");
    }

    [Fact]
    public async Task Link_InvalidNestedAgentPathRefusesWithoutWritingOtherSide()
    {
        await _tools.MemoryRemember("/notes/source", ["source"]);
        string before = Metadata("/notes/source");

        (await _tools.MemoryLink("/notes/source", "/agent/nested/name"))
            .Should().Contain("INVALID_PARAMETER");

        Metadata("/notes/source").Should().Be(before);
    }

    [Theory]
    [InlineData("/agent/profile", "utf-8")]
    [InlineData("/agent/profile", "utf-16")]
    [InlineData("/skill/custom", "utf-8")]
    [InlineData("/skill/custom", "utf-16")]
    public async Task Link_AcceptsBomEncodedSidecars(string path, string encodingName)
    {
        await _tools.MemoryRemember(path, ["body"]);
        await _tools.MemoryRemember("/notes/source", ["source"]);
        string metaPath = Path.ChangeExtension(BodyPath(path), ".meta.json");
        string metadata = File.ReadAllText(metaPath);
        File.WriteAllText(metaPath, metadata, System.Text.Encoding.GetEncoding(encodingName));

        (await _tools.MemoryLink(path, "/notes/source")).Should().Contain("action: linked");

        References(path).Should().Contain("/notes/source");
        References("/notes/source").Should().Contain(path);
    }

    [Theory]
    [InlineData("/agent/profile")]
    [InlineData("/skill/custom")]
    public async Task Link_SecondEndpointRecoveryRetainsStructuredEvidence(string destination)
    {
        await _tools.MemoryRemember("/notes/source", ["source"]);
        await _tools.MemoryRemember(destination, ["body"]);
        var coordinator = new FileEntityTransactionCoordinator(_scope.WorkspaceDir);
        coordinator.FailOnceAt(FileEntityFaultPoint.VerificationRead);
        coordinator.FailOnceAt(FileEntityFaultPoint.RollbackWrite);

        McpResponse response = await _tools.Link(
            MemoryAddress.Parse("/notes/source"), MemoryAddress.Parse(destination));

        response.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
        response.OperationId.Should().NotBeNullOrWhiteSpace();
        response.JournalPath.Should().NotBeNullOrWhiteSpace().And.NotContain(_scope.WorkspaceDir);
        response.Info.Should().Contain($"partial: /notes/source -> {destination}");
        Directory.EnumerateFiles(Path.GetDirectoryName(BodyPath(destination))!,
            ".file-entity-*.recovery").Should().ContainSingle();
    }

    private static bool IsFileEntity(string path) =>
        path.StartsWith("/agent/") || path.StartsWith("/skill/");

    private string BodyPath(string path)
    {
        string[] parts = path.Trim('/').Split('/');
        return Path.Combine(_scope.WorkspaceDir, ".scrinia", parts[0] == "skill" ? "skills" : "agent", parts[1] + ".md");
    }

    private string Metadata(string path)
    {
        if (path.StartsWith("/agent/") || path.StartsWith("/skill/"))
            return File.ReadAllText(Path.ChangeExtension(BodyPath(path), ".meta.json"));
        var store = MemoryStoreContext.Current!;
        var (scope, name) = store.ResolveAddress(MemoryAddress.Parse(path));
        return JsonSerializer.Serialize(store.LoadIndex(scope).Single(entry => entry.Name == name));
    }

    private string[] References(string path)
    {
        if (path.StartsWith("/agent/") || path.StartsWith("/skill/"))
        {
            using var doc = JsonDocument.Parse(Metadata(path));
            return doc.RootElement.GetProperty("ref").EnumerateArray().Select(value => value.GetString()!).ToArray();
        }
        var store = MemoryStoreContext.Current!;
        if (path.StartsWith('~'))
            return store.GetEphemeral(MemoryAddress.Parse(path))!.Attributes!["ref"];
        var (scope, name) = store.ResolveAddress(MemoryAddress.Parse(path));
        return store.LoadIndex(scope).Single(entry => entry.Name == name).Attributes!["ref"];
    }
}
