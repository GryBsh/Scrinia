using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Mcp;

namespace Scrinia.Tests.Shared;

public sealed class SharedDurableMatrixTests
{
    [Theory]
    [InlineData("memory", "{\"address\":\"/entry\",\"artifactKey\":\"artifacts/entry.nmp2\"}", "ReadWrite", null)]
    [InlineData("memory", "{\"address\":\"/entry\",\"artifactKey\":\"artifacts/entry.nmp2\",\"v\":null}", "ReadWrite", null)]
    [InlineData("memory", "{\"address\":\"/entry\",\"artifactKey\":\"artifacts/entry.nmp2\",\"v\":0}", "ReadWrite", null)]
    [InlineData("memory", "{\"address\":\"/entry\",\"artifactKey\":\"artifacts/entry.nmp2\",\"v\":1}", "ReadWrite", null)]
    [InlineData("memory", "{\"address\":\"/entry\",\"artifactKey\":\"artifacts/entry.nmp2\",\"v\":2}", "ReadOnly", null)]
    [InlineData("memory", "{\"address\":\"/entry\",\"artifactKey\":\"artifacts/entry.nmp2\",\"v\":\"bad\"}", "Unavailable", "SHARED_ENTRY_CORRUPT")]
    [InlineData("memory", "{\"address\":\"/../entry\",\"artifactKey\":\"artifacts/entry.nmp2\"}", "Unavailable", "SHARED_ENTRY_CORRUPT")]
    [InlineData("memory", "{\"address\":\"/entry\",\"artifactKey\":\"../entry.nmp2\"}", "Unavailable", "SHARED_ENTRY_CORRUPT")]
    [InlineData("skill", "{\"v\":null}", "ReadWrite", null)]
    [InlineData("skill", "{}", "ReadWrite", null)]
    [InlineData("skill", "{\"v\":0}", "ReadWrite", null)]
    [InlineData("skill", "{\"v\":1}", "ReadWrite", null)]
    [InlineData("skill", "{\"v\":2}", "ReadOnly", null)]
    [InlineData("skill", "{\"v\":\"bad\"}", "ReadWrite", null)]
    [InlineData("agent", "{\"v\":null}", "ReadWrite", null)]
    [InlineData("agent", "{}", "ReadWrite", null)]
    [InlineData("agent", "{\"v\":0}", "ReadWrite", null)]
    [InlineData("agent", "{\"v\":1}", "ReadWrite", null)]
    [InlineData("agent", "{\"v\":2}", "ReadOnly", null)]
    [InlineData("agent", "{\"v\":\"bad\"}", "ReadWrite", null)]
    public void Sidecar_compatibility_matrix_is_explicit(string kind, string metadata, string expectedAvailability, string? expectedError)
    {
        using var fixture = DurableFixture.Create();
        if (kind == "memory") fixture.WriteMemory("entry", fixture.ValidNmp, metadata);
        else fixture.WriteFileEntity((kind == "skill" ? "skills/entry" : "agent/entry"), "# Entry", metadata);

        using var runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        string path = kind == "memory" ? "/entry" : "/" + kind + "/entry";
        SharedDurableEntry? entry = expectedAvailability == "Unavailable"
            ? runtime.Snapshot.Entries.SingleOrDefault(candidate => candidate.ErrorCode == expectedError)
            : runtime.Snapshot.Entries.SingleOrDefault(candidate => candidate.Path == path);
        if (expectedAvailability == "Unavailable")
        {
            entry.Should().NotBeNull();
            entry!.Availability.ToString().Should().Be(expectedAvailability);
            entry.ErrorCode.Should().Be(expectedError);
            return;
        }
        entry.Should().NotBeNull();
        entry!.Availability.ToString().Should().Be(expectedAvailability);
        entry.ErrorCode.Should().BeNull();
    }

    [Theory]
    [InlineData("memory", "entry.meta.json")]
    [InlineData("skill", "skill.meta.json")]
    [InlineData("agent", "agent.meta.json")]
    public void Malformed_json_is_corrupt_for_memory_but_absent_metadata_for_file_entities(string kind, string expectedMetadataFile)
    {
        using var fixture = DurableFixture.Create();
        if (kind == "memory") fixture.WriteMemory("entry", fixture.ValidNmp, "{");
        else fixture.WriteFileEntity((kind == "skill" ? "skills/skill" : "agent/agent"), "# Entry", "{");
        using var runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        string path = kind == "memory" ? "/entry" : "/" + kind + "/" + Path.GetFileNameWithoutExtension(expectedMetadataFile)[..^5];
        SharedDurableEntry? entry = runtime.Snapshot.Entries.SingleOrDefault(candidate => candidate.Path == path);
        if (kind == "memory") entry!.ErrorCode.Should().Be("SHARED_ENTRY_CORRUPT");
        else entry!.Availability.Should().Be(SharedDurableAvailability.ReadWrite);
    }

    [Fact]
    public void Covers_every_spec_4_4_row_and_spec_4_5_default()
    {
        using var fixture = DurableFixture.Create();
        fixture.WriteMemory("good", fixture.ValidNmp, "{\"address\":\"/good\",\"artifactKey\":\"artifacts/good.nmp2\"}");
        fixture.WriteMemory("unsupported", "NMP/3 0B\nNMP/END\n", "{\"address\":\"/unsupported\",\"artifactKey\":\"artifacts/unsupported.nmp2\"}");
        fixture.WriteMemory("corrupt", "not nmp", "{\"address\":\"/corrupt\",\"artifactKey\":\"artifacts/corrupt.nmp2\"}");
        fixture.WriteMemory("missing-sidecar", fixture.ValidNmp, null);
        fixture.WriteMemory("newer", fixture.ValidNmp, "{\"address\":\"/newer\",\"artifactKey\":\"artifacts/newer.nmp2\",\"v\":2}");
        fixture.WriteFileEntity("skills/skill", "# Skill", "{\"v\":2}");
        fixture.WriteFileEntity("agent/agent", "# Agent", "not json");

        using var runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        var entries = runtime.Snapshot.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);

        entries["/good"].Availability.Should().Be(SharedDurableAvailability.ReadWrite);
        entries["/unsupported"].ErrorCode.Should().Be("SHARED_FORMAT_UNSUPPORTED");
        entries["/corrupt"].ErrorCode.Should().Be("SHARED_ENTRY_CORRUPT");
        entries["/missing-sidecar"].Availability.Should().Be(SharedDurableAvailability.Unavailable);
        entries["/missing-sidecar"].ErrorCode.Should().Be("SHARED_ENTRY_CORRUPT");
        runtime.Snapshot.Memories.Should().NotContain(item => item.Path == "/missing-sidecar");
        entries["/newer"].Availability.Should().Be(SharedDurableAvailability.ReadOnly);
        entries["/skill/skill"].Availability.Should().Be(SharedDurableAvailability.ReadOnly);
        entries["/agent/agent"].Availability.Should().Be(SharedDurableAvailability.ReadWrite);
        entries.Values.Where(entry => entry.Availability == SharedDurableAvailability.Unavailable)
            .Select(entry => entry.Path).Should().NotContain("/skill/skill");
    }

    [Fact]
    public async Task Skill_rewrite_reads_and_replaces_an_absent_version_sidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        var tools = new ScriniaMcpTools();
        string skillName = EmbeddedPrompts.LoadAllSkills().Keys.OrderBy(k => k, StringComparer.Ordinal).First();

        (await tools.MemoryRemember($"/skill/{skillName}", ["original instructions"]))
            .Should().NotContain("status: error");
        string filePath = Path.Combine(
            PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot), skillName + ".md");
        string sidecarPath = Path.ChangeExtension(filePath, ".meta.json");
        File.WriteAllText(sidecarPath, "{}");

        (await tools.MemoryRecall($"/skill/{skillName}"))
            .Should().Contain("original instructions");
        (await tools.MemoryRemember($"/skill/{skillName}", ["rewritten instructions"]))
            .Should().NotContain("status: error");

        File.ReadAllText(filePath).Should().Contain("rewritten instructions");
        File.ReadAllText(sidecarPath).Should().Contain("\"updatedAt\"");
    }

    [Fact]
    public async Task Agent_rewrite_reads_and_replaces_an_absent_version_sidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        var tools = new ScriniaMcpTools();

        (await tools.MemoryRemember("/agent/task6-agent", ["original configuration"]))
            .Should().NotContain("status: error");
        string filePath = Path.Combine(
            PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot), "task6-agent.md");
        string sidecarPath = Path.ChangeExtension(filePath, ".meta.json");
        File.WriteAllText(sidecarPath, "{}");

        (await tools.MemoryRemember("/agent/task6-agent", ["rewritten configuration"]))
            .Should().NotContain("status: error");

        File.ReadAllText(filePath).Should().Be("rewritten configuration");
        File.ReadAllText(sidecarPath).Should().Contain("\"updatedAt\"");
    }

    private sealed class DurableFixture : IDisposable
    {
        public required string Root { get; init; }
        public required SharedBaseEntry Entry { get; init; }
        public string ValidNmp { get; private init; } = null!;
        public static DurableFixture Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "scrinia-task6-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".scrinia", "memories"));
            string body = "payload";
            string nmp = new Scrinia.Core.Encoding.Nmp2Strategy().Encode(Encoding.UTF8.GetBytes(body), new()).Artifact;
            return new DurableFixture { Root = root, Entry = new() { Name = "team", Root = root }, ValidNmp = nmp };
        }
        public void WriteMemory(string name, string body, string? sidecar)
        {
            File.WriteAllText(Path.Combine(Root, ".scrinia", "memories", name + ".nmp2"), body);
            if (sidecar is not null) File.WriteAllText(Path.Combine(Root, ".scrinia", "memories", name + ".meta.json"), sidecar);
        }
        public void WriteFileEntity(string relative, string body, string metadata)
        {
            string path = Path.Combine(Root, ".scrinia", relative.Replace('/', Path.DirectorySeparatorChar) + ".md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
            File.WriteAllText(Path.ChangeExtension(path, ".meta.json"), metadata);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
