using System.Text;
using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Shared;

public sealed class SharedDurableMatrixTests
{
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
        entries.Should().NotContainKey("/missing-sidecar");
        entries["/newer"].Availability.Should().Be(SharedDurableAvailability.ReadOnly);
        entries["/skill/skill"].Availability.Should().Be(SharedDurableAvailability.ReadOnly);
        entries["/agent/agent"].Availability.Should().Be(SharedDurableAvailability.ReadWrite);
        entries.Values.Where(entry => entry.Availability == SharedDurableAvailability.Unavailable)
            .Select(entry => entry.Path).Should().NotContain("/skill/skill");
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
