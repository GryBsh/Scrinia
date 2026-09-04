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
    public void Detects_changed_canonical_target_as_unsafe_path()
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        using SharedBaseRuntime runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        string target = Path.Combine(fixture.Root, ".scrinia", "target.txt");
        string linked = Path.Combine(fixture.Root, ".scrinia", "linked.txt");
        File.WriteAllText(target, "inside");
        File.CreateSymbolicLink(linked, target);

        SharedMutationPreflight result = runtime.RevalidateBeforeMutation("/note", null);

        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("SHARED_BASE_UNSAFE_PATH");
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData(".gitignore")]
    public void Control_files_do_not_change_the_open_generation(string controlFile)
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        using SharedBaseRuntime runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        string generation = runtime.Snapshot.Generation!;

        File.WriteAllText(Path.Combine(fixture.Root, ".scrinia", controlFile), "changed control");

        runtime.RevalidateBeforeMutation("/note", null).IsSafe.Should().BeTrue();
        runtime.Snapshot.Generation.Should().Be(generation);
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

    [Fact]
    public void Successful_callback_with_invalid_postwrite_refresh_returns_unknown()
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        using SharedBaseRuntime runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        var plan = new SharedMutationPlan { Identity = runtime.Snapshot.Identity, CanonicalPath = "/note" };

        SharedMutationCommit commit = runtime.CommitMutation(plan, () => Directory.Delete(Path.Combine(fixture.Root, ".scrinia"), recursive: true));

        commit.Mutation.Should().Be(SharedMutationCertainty.Unknown);
        runtime.Snapshot.Availability.Should().Be(SharedBaseAvailability.Unavailable);
        runtime.RevalidateBeforeMutation("/note", null).IsSafe.Should().BeFalse();
    }

    [Fact]
    public void Returns_unknown_when_postwrite_inventory_contains_new_invalid_entry()
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        using SharedBaseRuntime runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        var plan = new SharedMutationPlan { Identity = runtime.Snapshot.Identity, CanonicalPath = "/note" };

        SharedMutationCommit commit = runtime.CommitMutation(plan, () =>
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, ".scrinia", "memories"));
            File.WriteAllBytes(Path.Combine(fixture.Root, ".scrinia", "memories", "new.meta.json"), [0xFF]);
        });

        commit.Mutation.Should().Be(SharedMutationCertainty.Unknown);
        runtime.Snapshot.Availability.Should().Be(SharedBaseAvailability.Stale);
        runtime.RevalidateBeforeMutation("/note", null).ErrorCode.Should().Be("SHARED_BASE_STALE");
    }

    [Fact]
    public void Commit_normalizes_the_authoritative_manifest_to_root_relative_sorted_files()
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        using SharedBaseRuntime runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        var changed = new[] { "memories/a.nmp2", "memories/a.meta.json" };
        var plan = new SharedMutationPlan
        {
            Identity = runtime.Snapshot.Identity,
            CanonicalPath = "/note",
            AuthoritativeFiles = changed,
        };

        SharedMutationCommit commit = runtime.CommitMutation(plan, static () => { });

        commit.Manifest.Should().Equal(
            ".scrinia/memories/a.meta.json",
            ".scrinia/memories/a.nmp2");
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
