using System.Text;
using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Shared;

/// <summary>
/// Task 8 — the authoritative mutation manifest is a pure, deterministic normalization: paths are
/// made relative to the registered repository root, `/`-separated, sorted by UTF-8 ordinal bytes,
/// deduplicated, and reduced to exact authoritative files only. No directory, wildcard, derived, or
/// pretracked path may survive normalization.
/// </summary>
public sealed class SharedManifestTests
{
    [Fact]
    public void Manifest_contains_only_exact_authoritative_files_for_each_mutation()
    {
        // A remembered /patterns/review with two chunks rewrites the NMP body and its sidecar.
        var changed = new[]
        {
            "memories/local-topic-memory-patterns/review.nmp2",
            "memories/local-topic-memory-patterns/review.nmp2.meta.json",
        };

        string[] manifest = SharedManifest.Create(changed);

        manifest.Should().Equal(
            ".scrinia/memories/local-topic-memory-patterns/review.nmp2",
            ".scrinia/memories/local-topic-memory-patterns/review.nmp2.meta.json");
    }

    [Fact]
    public void Manifest_deduplicates_and_sorts_by_utf8_ordinal_byte_sequence()
    {
        var changed = new[]
        {
            "memories/b.nmp2.meta.json",
            "memories/a.nmp2",
            "memories/a.nmp2",
            "memories/b.nmp2.meta.json",
            "skills/qa.md.meta.json",
        };

        string[] manifest = SharedManifest.Create(changed);

        manifest.Should().Equal(
            ".scrinia/memories/a.nmp2",
            ".scrinia/memories/b.nmp2.meta.json",
            ".scrinia/skills/qa.md.meta.json");
    }

    [Fact]
    public void Manifest_normalizes_backslashes_to_forward_separators()
    {
        var changed = new[] { "memories\\local-topic\\review.nmp2" };

        string[] manifest = SharedManifest.Create(changed);

        manifest.Should().Equal(".scrinia/memories/local-topic/review.nmp2");
    }

    [Fact]
    public void Manifest_accepts_already_root_relative_paths_without_duplication()
    {
        var changed = new[]
        {
            ".scrinia/memories/a.nmp2",
            "memories/a.nmp2",
        };

        string[] manifest = SharedManifest.Create(changed);

        manifest.Should().Equal(".scrinia/memories/a.nmp2");
    }

    [Fact]
    public void Never_contains_directory_wildcard_derived_or_pretracked_derived_paths()
    {
        var changed = new[]
        {
            "embeddings/vector.json",   // canonical root derived directory (rule 1)
            "versions/001.nmp2",        // archived-copy derived singleton tree (rule 4)
            "cache.tmp",                // any-depth .tmp final segment (rule 3)
            "index.lock",               // root-child .lock (rule 4)
            "topics/*.nmp2",            // wildcard pathspec
            "memories/local-topic/review.nmp2", // the only true authoritative file
        };

        string[] manifest = SharedManifest.Create(changed);

        manifest.Should().Equal(".scrinia/memories/local-topic/review.nmp2");
    }

    [Fact]
    public void Empty_or_whitespace_inputs_produce_an_empty_manifest()
    {
        string[] manifest = SharedManifest.Create(new[] { "", "   ", null! });

        manifest.Should().BeEmpty();
    }

    [Fact]
    public void Manifest_sorts_non_ascii_paths_by_utf8_code_byte_order_not_code_unit_order()
    {
        // U+10000 (𐀀) is a surrogate pair in UTF-16 ([D800, DC00]) but four UTF-8 bytes (F0 90 80 80).
        // U+FDFF (﷿) is a single BMP code unit in UTF-16 ([FDFF]) but three UTF-8 bytes (EF BF BF).
        // StringComparer.Ordinal orders by UTF-16 code unit: D800 < FDFF, so 𐀀 precedes ﷿.
        // UTF-8 byte order orders by the first byte: EF < F0, so ﷿ precedes 𐀀.
        string utf16First = "\U00010000.nmp2";
        string utf8First = "\uFDFF.nmp2";

        // Prove the two orderings genuinely diverge before asserting the manifest follows UTF-8 bytes.
        StringComparer.Ordinal.Compare(utf16First, utf8First).Should().BeNegative(); // D800 < FDFF under UTF-16
        Encoding.UTF8.GetBytes(utf8First)[0].Should().BeLessThan(Encoding.UTF8.GetBytes(utf16First)[0]); // EF < F0 under UTF-8

        string[] manifest = SharedManifest.Create(new[] { utf16First, utf8First });

        manifest.Should().Equal(".scrinia/" + utf8First, ".scrinia/" + utf16First);
    }

    [Fact]
    public void No_op_leaves_a_empty_manifest_for_the_runtime_commit()
    {
        using RuntimeFixture fixture = RuntimeFixture.Create("alpha");
        using SharedBaseRuntime runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        var plan = new SharedMutationPlan { Identity = runtime.Snapshot.Identity, CanonicalPath = "/note" };

        SharedMutationCommit commit = runtime.CommitMutation(plan, static () => { });

        commit.Manifest.Should().BeEmpty();
    }

    private sealed class RuntimeFixture : IDisposable
    {
        public required string Root { get; init; }
        public required SharedBaseEntry Entry { get; init; }
        public static RuntimeFixture Create(string name)
        {
            string root = Path.Combine(Path.GetTempPath(), "scrinia-task8-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".scrinia"));
            return new RuntimeFixture { Root = root, Entry = new SharedBaseEntry { Name = name, Root = root } };
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
