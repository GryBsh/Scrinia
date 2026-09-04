using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Shared;

public sealed class SharedFreshnessTests
{
    [Fact]
    public void Detects_external_fingerprint_drift_before_mutation()
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        string first = Path.Combine(fixture.Root, ".scrinia", "b.txt");
        File.WriteAllText(first, "one");
        using SharedBaseRuntime runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        File.WriteAllText(first, "two");

        SharedMutationPreflight result = runtime.RevalidateBeforeMutation("/note", null);

        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("SHARED_BASE_STALE");
    }

    [Fact]
    public void Refresh_swaps_snapshot_and_fingerprint_atomically()
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        using SharedBaseRuntime runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        string oldGeneration = runtime.Snapshot.Generation!;
        File.WriteAllText(Path.Combine(fixture.Root, ".scrinia", "entry.txt"), "content");

        SharedRefreshCommit commit = runtime.Refresh();

        commit.PreviousGeneration.Should().Be(oldGeneration);
        commit.Snapshot.Generation.Should().NotBe(oldGeneration);
        commit.Snapshot.Fingerprint.Should().Be(commit.Snapshot.Generation);
        runtime.RevalidateBeforeMutation("/note", null).IsSafe.Should().BeTrue();
    }

    [Fact]
    public void Fingerprint_uses_length_prefixed_utf8_path_and_content_sequence()
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        string path = Path.Combine(fixture.Root, ".scrinia", "é.txt");
        byte[] content = Encoding.UTF8.GetBytes("payload");
        File.WriteAllBytes(path, content);

        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] relative = Encoding.UTF8.GetBytes("é.txt");
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(length, relative.Length);
        expected.AppendData(length);
        expected.AppendData(relative);
        BinaryPrimitives.WriteInt64BigEndian(length, content.Length);
        expected.AppendData(length);
        expected.AppendData(content);

        SharedBaseRuntime.Fingerprint(fixture.Root).Should().Be(Convert.ToHexString(expected.GetHashAndReset()).ToLowerInvariant());
    }

    [Fact]
    public void Returns_unknown_and_blocks_writes_after_post_write_invalidity()
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        using SharedBaseRuntime runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        var plan = new SharedMutationPlan { Identity = runtime.Snapshot.Identity, CanonicalPath = "/note" };

        SharedMutationCommit commit = runtime.CommitMutation(plan, () => throw new IOException("durable write failed"));

        commit.Mutation.Should().Be(SharedMutationCertainty.Unknown);
        runtime.Snapshot.Availability.Should().Be(SharedBaseAvailability.Stale);
        runtime.RevalidateBeforeMutation("/note", null).ErrorCode.Should().Be("SHARED_BASE_STALE");
    }

    private sealed class RuntimeFixture : IDisposable
    {
        public required string Root { get; init; }
        public required SharedBaseEntry Entry { get; init; }
        public static RuntimeFixture Create(string name)
        {
            string root = Path.Combine(Path.GetTempPath(), "scrinia-task3-fresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".scrinia"));
            return new RuntimeFixture { Root = root, Entry = new SharedBaseEntry { Name = name, Root = root } };
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
