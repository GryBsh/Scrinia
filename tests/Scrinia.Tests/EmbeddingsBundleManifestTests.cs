using Scrinia.Commands;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Net.Sockets;

namespace Scrinia.Tests;

public sealed class EmbeddingsBundleManifestTests
{
    [Fact]
    public void TamperedPayloadIsRejectedWithoutCreatingHome()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        string executable = Path.Combine(fixture.Root, "plugins", "scri-plugin-embeddings", "0.6.0", "win-x64", "scri-plugin-embeddings.exe");
        File.AppendAllText(executable, "tampered");
        EmbeddingsBundleException error = Assert.Throws<EmbeddingsBundleException>(() =>
            EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0+source-sha", "win-x64"));
        Assert.Equal(EmbeddingsInstallStatus.InvalidBundle, error.Status);
        Assert.False(Directory.Exists(fixture.Home));
    }

    [Theory]
    [InlineData("win-x64", 7)]
    [InlineData("linux-x64", 7)]
    [InlineData("osx-arm64", 9)]
    public void ValidPolicyPayloadIsObservedWithoutMutation(string rid, int count)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", rid);
        byte[] before = File.ReadAllBytes(fixture.ManifestPath);
        var result = EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0+source-sha", rid);
        Assert.Equal(count, result.Manifest.Files.Length);
        Assert.Equal(Path.Combine(result.Root, "plugins", result.Manifest.Plugin, "0.6.0", rid), result.PayloadDirectory);
        Assert.Equal(before, File.ReadAllBytes(fixture.ManifestPath));
        AssertNoConfiguration(fixture);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("plugin")]
    [InlineData("pluginVersion")]
    [InlineData("rid")]
    [InlineData("files")]
    [InlineData("name")]
    [InlineData("length")]
    [InlineData("sha256")]
    public void EveryRequiredFieldMustAppearExactlyOnce(string field)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        JsonNode node = JsonNode.Parse(File.ReadAllText(fixture.ManifestPath))!;
        JsonObject owner = field is "name" or "length" or "sha256" ? node["files"]![0]!.AsObject() : node.AsObject();
        owner.Remove(field);
        File.WriteAllText(fixture.ManifestPath, node.ToJsonString());
        AssertInvalid(fixture);
    }

    [Theory]
    [InlineData("version", "2", false)]
    [InlineData("version", "null", false)]
    [InlineData("plugin", "\"another-plugin\"", true)]
    [InlineData("pluginVersion", "\"0.6.1\"", true)]
    [InlineData("pluginVersion", "\"0.6.0-rc.1\"", true)]
    [InlineData("pluginVersion", "\"0.6.0+metadata\"", true)]
    [InlineData("rid", "\"linux-x64\"", true)]
    [InlineData("rid", "\"../outside\"", true)]
    [InlineData("files", "null", false)]
    [InlineData("name", "null", false)]
    [InlineData("length", "-1", false)]
    [InlineData("length", "9223372036854775808", false)]
    [InlineData("length", "1.5", false)]
    [InlineData("sha256", "\"not-a-hash\"", false)]
    [InlineData("sha256", "null", false)]
    public void InvalidFieldsRejectBeforePayloadOpen(string field, string value, bool incompatible)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        JsonNode node = JsonNode.Parse(File.ReadAllText(fixture.ManifestPath))!;
        JsonObject owner = field is "name" or "length" or "sha256" ? node["files"]![0]!.AsObject() : node.AsObject();
        owner[field] = JsonNode.Parse(value);
        File.WriteAllText(fixture.ManifestPath, node.ToJsonString());
        int payloadOpens = 0;
        var error = Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", "win-x64",
            File.GetAttributes, path => { if (path != fixture.ManifestPath) payloadOpens++; return File.OpenRead(path); }));
        Assert.Equal(incompatible ? EmbeddingsInstallStatus.IncompatibleIdentity : EmbeddingsInstallStatus.InvalidBundle, error.Status);
        Assert.Equal(0, payloadOpens);
        AssertNoConfiguration(fixture);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("folder/file")]
    [InlineData("folder\\file")]
    [InlineData("C:stream")]
    [InlineData("/rooted")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void UnsafeLeafNamesAreRejected(string name)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        var files = fixture.Manifest.Files.ToArray();
        files[0] = files[0] with { Name = name };
        fixture.WriteManifest(fixture.Manifest with { Files = files });
        AssertInvalid(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateAndCaseCollidingNamesAreRejected(bool changeCase)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        var file = fixture.Manifest.Files[0];
        fixture.WriteManifest(fixture.Manifest with { Files = [.. fixture.Manifest.Files, file with { Name = changeCase ? file.Name.ToUpperInvariant() : file.Name }] });
        AssertInvalid(fixture);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("extra")]
    [InlineData("directory")]
    [InlineData("case")]
    public void PayloadInventoryAndBytesMustMatch(string mutation)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        string executable = Path.Combine(fixture.PayloadDirectory, fixture.Manifest.Files[0].Name);
        if (mutation == "missing") File.Delete(executable);
        if (mutation == "hash") { byte[] bytes = File.ReadAllBytes(executable); bytes[0] ^= 1; File.WriteAllBytes(executable, bytes); }
        if (mutation == "extra") File.WriteAllText(Path.Combine(fixture.PayloadDirectory, "extra.dll"), "extra");
        if (mutation == "directory") Directory.CreateDirectory(Path.Combine(fixture.PayloadDirectory, "extra"));
        if (mutation == "case")
        {
            var files = fixture.Manifest.Files.ToArray(); files[0] = files[0] with { Name = files[0].Name.ToUpperInvariant() };
            fixture.WriteManifest(fixture.Manifest with { Files = files });
        }
        AssertInvalid(fixture);
    }

    [Theory]
    [InlineData("https://example.com/bundle")]
    [InlineData("missing")]
    [InlineData("archive")]
    public void SourceMustBeExistingDirectory(string source)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        string path = source == "archive" ? Path.Combine(fixture.Workspace, "bundle.zip") : source == "missing" ? Path.Combine(fixture.Workspace, "missing") : source;
        if (source == "archive") File.WriteAllText(path, "archive");
        Assert.Equal(EmbeddingsInstallStatus.InvalidBundle, Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(path, "0.6.0", "win-x64")).Status);
        AssertNoConfiguration(fixture);
    }

    [Theory]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-file")]
    [InlineData("unknown-root")]
    [InlineData("unknown-file")]
    [InlineData("utf8")]
    [InlineData("trailing")]
    public void StrictJsonRejectsAmbiguousOrMalformedInput(string mutation)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        string json = File.ReadAllText(fixture.ManifestPath);
        json = mutation switch
        {
            "duplicate-root" => json.Replace("\"version\": 1", "\"version\": 1, \"version\": 1", StringComparison.Ordinal),
            "duplicate-file" => json.Replace("\"length\":", "\"length\": 0, \"length\":", StringComparison.Ordinal),
            "unknown-root" => json.Replace("\"version\": 1", "\"unknown\": 1, \"version\": 1", StringComparison.Ordinal),
            "unknown-file" => json.Replace("\"length\":", "\"unknown\": 1, \"length\":", StringComparison.Ordinal),
            "trailing" => json + "{}",
            _ => json
        };
        if (mutation == "utf8") { byte[] bytes = Encoding.UTF8.GetBytes(json); bytes[10] = 0xff; File.WriteAllBytes(fixture.ManifestPath, bytes); }
        else File.WriteAllText(fixture.ManifestPath, json);
        AssertInvalid(fixture);
    }

    [Fact]
    public void ManifestStreamIsBoundedAt64KiBEvenWhenLengthIsUnavailable()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        byte[] json = File.ReadAllBytes(fixture.ManifestPath);
        byte[] atLimit = Enumerable.Repeat((byte)' ', 65536).ToArray(); json.CopyTo(atLimit, 0);
        using var exact = new NonSeekableStream(atLimit);
        Assert.Equal(7, EmbeddingsBundleValidator.ReadManifest(exact).Files.Length);
        using var over = new NonSeekableStream([.. atLimit, (byte)' ']);
        Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.ReadManifest(over));
        Assert.Equal(65537, over.BytesRead);
    }

    [Theory]
    [InlineData(32, 0, true)]
    [InlineData(33, 0, false)]
    [InlineData(2, 536870912, true)]
    [InlineData(1, 536870913, false)]
    [InlineData(3, 536870912, false)]
    public void DeclarationBoundsAreCheckedWithoutAllocatingPayload(int count, long length, bool valid)
    {
        var files = Enumerable.Range(0, count).Select(i => new EmbeddingsBundleFile($"payload-{i}", length, new string('a', 64))).ToArray();
        var manifest = new EmbeddingsBundleManifest(1, "scri-plugin-embeddings", "0.6.0", "win-x64", files);
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, CliJsonContext.Default.EmbeddingsBundleManifest));
        if (valid) Assert.Equal(count, EmbeddingsBundleValidator.ReadManifest(stream).Files.Length);
        else Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.ReadManifest(stream));
    }

    [Fact]
    public void TotalOneByteAboveLimitRejectsBeforePayloadOpen()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        var files = fixture.Manifest.Files.Select((file, i) => file with { Length = i < 2 ? 536870912 : i == 2 ? 1 : 0 }).ToArray();
        fixture.WriteManifest(fixture.Manifest with { Files = files });
        int payloadOpens = 0;
        Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", "win-x64", File.GetAttributes,
            path => { if (path != fixture.ManifestPath) payloadOpens++; return File.OpenRead(path); }));
        Assert.Equal(0, payloadOpens);
    }

    [Fact]
    public void DeniedInspectionFailsClosed()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        Assert.Equal(EmbeddingsInstallStatus.InvalidBundle, Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", "win-x64",
            path => path == fixture.PayloadDirectory ? throw new UnauthorizedAccessException("denied") : File.GetAttributes(path), File.OpenRead)).Status);
        AssertNoConfiguration(fixture);
    }

    [Fact]
    public void GrowingNonSeekablePayloadIsRejectedByStreamedLength()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        string payload = Path.Combine(fixture.PayloadDirectory, fixture.Manifest.Files[0].Name);
        Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", "win-x64", File.GetAttributes,
            path => path == payload ? new NonSeekableStream([.. File.ReadAllBytes(path), (byte)1]) : File.OpenRead(path)));
    }

    [Fact]
    public void AdjacentPolicyCannotOverrideEmbeddedInventory()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        File.WriteAllText(Path.Combine(fixture.Root, "embeddings-payloads.json"), "invalid policy");
        Assert.Equal(7, EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", "win-x64").Manifest.Files.Length);
    }

    [SkippableFact]
    public void RootAliasIsAcceptedButDescendantEscapeIsRejected()
    {
        Skip.If(OperatingSystem.IsWindows(), "Symlink regression requires a native Unix host.");
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "linux-x64");
        string alias = Path.Combine(fixture.Workspace, "alias");
        Directory.CreateSymbolicLink(alias, fixture.Root);
        Assert.Equal(EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", "linux-x64").Root,
            EmbeddingsBundleValidator.Validate(alias, "0.6.0", "linux-x64").Root);
        string payload = Path.Combine(fixture.PayloadDirectory, fixture.Manifest.Files[0].Name);
        string outside = Path.Combine(fixture.Workspace, "outside"); File.Copy(payload, outside); File.Delete(payload);
        File.CreateSymbolicLink(payload, outside);
        AssertInvalid(fixture, "linux-x64");
        File.Delete(payload); Directory.Delete(alias);
    }

    [SkippableFact]
    public void DescendantParentLinkIsRejectedEvenWithMatchingBytes()
    {
        Skip.If(OperatingSystem.IsWindows(), "Symlink regression requires a native Unix host.");
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "linux-x64");
        string moved = Path.Combine(fixture.Workspace, "moved");
        Directory.Move(fixture.PayloadDirectory, moved); Directory.CreateSymbolicLink(fixture.PayloadDirectory, moved);
        AssertInvalid(fixture, "linux-x64"); Directory.Delete(fixture.PayloadDirectory);
    }

    [Theory]
    [InlineData("0.6.1", "win-x64")]
    [InlineData("0.6.0-rc.1", "win-x64")]
    [InlineData("../escape", "win-x64")]
    [InlineData("0.6.0", "unsupported")]
    public void HostIdentityCannotSelectAnotherVersionOrRid(string host, string rid)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        Assert.Equal(EmbeddingsInstallStatus.IncompatibleIdentity,
            Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(fixture.Root, host, rid)).Status);
        AssertNoConfiguration(fixture);
    }

    [Fact]
    public void MissingManifestIsRejected()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        File.Delete(fixture.ManifestPath);
        AssertInvalid(fixture);
    }

    [Fact]
    public void ExecutableMustHaveItsOwnPolicyRecord()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        fixture.WriteManifest(fixture.Manifest with { Files = fixture.Manifest.Files.Skip(1).ToArray() });
        AssertInvalid(fixture);
    }

    [Fact]
    public void OverlargeDeclarationRejectsBeforeInspectingPayload()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        var files = fixture.Manifest.Files.ToArray(); files[0] = files[0] with { Length = 536870913 };
        fixture.WriteManifest(fixture.Manifest with { Files = files });
        int inspected = 0;
        Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", "win-x64",
            path => { if (path.StartsWith(fixture.PayloadDirectory, StringComparison.Ordinal)) inspected++; return File.GetAttributes(path); }, File.OpenRead));
        Assert.Equal(0, inspected);
    }

    [Fact]
    public void DeniedReadFailsClosed()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        Assert.Equal(EmbeddingsInstallStatus.InvalidBundle, Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", "win-x64",
            File.GetAttributes, _ => throw new UnauthorizedAccessException("read denied"))).Status);
        AssertNoConfiguration(fixture);
    }

    [SkippableTheory]
    [InlineData("manifest-fifo")]
    [InlineData("payload-fifo")]
    [InlineData("payload-socket")]
    public void NonRegularCarriersAreRejectedBeforeOpening(string carrier)
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix FIFO/socket classification requires a native Unix host.");
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "linux-x64");
        string path = carrier == "manifest-fifo" ? fixture.ManifestPath : Path.Combine(fixture.PayloadDirectory, fixture.Manifest.Files[0].Name);
        File.Delete(path);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        if (carrier.EndsWith("socket", StringComparison.Ordinal))
        {
            string shortPath = Path.Combine(fixture.Workspace, "socket");
            socket.Bind(new UnixDomainSocketEndPoint(shortPath));
            File.Move(shortPath, path);
        }
        else
        {
            var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        }
        int forbiddenOpens = 0;
        Assert.Equal(EmbeddingsInstallStatus.InvalidBundle, Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", "linux-x64",
            File.GetAttributes, candidate => { if (candidate == path) { forbiddenOpens++; throw new IOException("Carrier must not be opened."); } return File.OpenRead(candidate); })).Status);
        Assert.Equal(0, forbiddenOpens);
        AssertNoConfiguration(fixture);
    }

    private static void AssertInvalid(EmbeddingsBundleTestFixture fixture, string rid = "win-x64")
    {
        Assert.Equal(EmbeddingsInstallStatus.InvalidBundle, Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(fixture.Root, "0.6.0", rid)).Status);
        AssertNoConfiguration(fixture);
    }

    private static void AssertNoConfiguration(EmbeddingsBundleTestFixture fixture)
    {
        Assert.False(Directory.Exists(fixture.Home));
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace, ".scrinia")));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "config.json")));
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private int _position;
        internal int BytesRead => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
