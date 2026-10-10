using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Encoding;

namespace Scrinia.Tests.Shared;

public sealed class SharedOperationBoundaryTests
{
    [Theory]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, false)]
    [InlineData(4, 3, true)]
    [InlineData(99, 100, false)]
    [InlineData(100, 100, false)]
    [InlineData(101, 100, true)]
    public async Task Search_preserves_single_base_overflow_for_explicit_and_aggregate_selection(int matches, int limit, bool truncated)
    {
        using var first = new SharedOperationFixture();
        using var second = new SharedOperationFixture();
        // Seed equal-score durable entries from a real shared write, then exercise the real
        // snapshot/scorer through public tools. Batch fixture setup avoids quadratic write scans.
        (await first.Tools.Remember("team", "/note000", ["zebra matching content"])).Should().Contain("mutation: applied");
        string memories = Path.Combine(first.BaseRoot, ".scrinia", "memories");
        for (int i = 1; i < matches; i++)
        {
            File.Copy(Path.Combine(memories, "note000.nmp2"), Path.Combine(memories, $"note{i:D3}.nmp2"));
            // Metadata embeds both the logical address and artifact key; keep them aligned.
            File.WriteAllText(Path.Combine(memories, $"note{i:D3}.meta.json"),
                File.ReadAllText(Path.Combine(memories, "note000.meta.json")).Replace("note000", $"note{i:D3}", StringComparison.Ordinal));
        }
        (await first.Tools.Refresh("team")).Should().Contain("status: success");
        await second.Tools.Remember("team", "/other", ["unrelated content"]);
        var multiple = SharedOperationFixture.Select(("alpha", first.BaseRoot), ("beta", second.BaseRoot));
        string[] responses = [await first.Tools.Search("zebra", limit: limit),
            await first.Tools.Search("zebra", "team", limit: limit),
            await multiple.Search("zebra", limit: limit), await multiple.Search("zebra", "alpha", limit: limit)];
        foreach (string response in responses)
        {
            response.Should().Contain($"truncated: {truncated.ToString().ToLowerInvariant()}");
            response.Split('\n').Count(line => line.TrimStart().StartsWith("path:", StringComparison.Ordinal))
                .Should().Be(Math.Min(matches, limit));
            response.Should().NotContain("base: beta");
            response.Split('\n').Where(line => line.TrimStart().StartsWith("path:", StringComparison.Ordinal))
                .Select(line => line.Trim()).Should().Equal(Enumerable.Range(0, Math.Min(matches, limit)).Select(i => $"path: /note{i:D3}"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logical_slash_addresses_work_while_physical_escape_never_invokes_publication(bool destinationEscapes)
    {
        using var fixture = new SharedOperationFixture();
        (await fixture.Tools.Remember("team", "/valid/leading", ["contained body"])).Should().Contain("mutation: applied");
        (await fixture.Tools.Recall("team", "/valid/leading")).Should().Contain("contained body");
        var runtime = fixture.Factory.Open(new SharedBaseEntry { Name = "team", Root = fixture.BaseRoot });
        var before = AuthoritativeBytes(fixture.BaseRoot);
        string? generation = runtime.Snapshot.Generation;
        string contained = Directory.EnumerateFiles(Path.Combine(fixture.BaseRoot, ".scrinia"), "*.nmp2", SearchOption.AllDirectories).Single();
        string outside = Path.Combine(fixture.Root, "outside.nmp2");
        bool invoked = false;
        var commit = runtime.CommitMutation(new SharedMutationPlan
        {
            Identity = runtime.Snapshot.Identity,
            CanonicalPath = destinationEscapes ? contained : outside,
            CanonicalDestination = destinationEscapes ? outside : null,
            AuthoritativeFiles = [Path.GetRelativePath(fixture.BaseRoot, contained)]
        }, () => invoked = true);
        invoked.Should().BeFalse();
        commit.ErrorCode.Should().Be("SHARED_BASE_UNSAFE_PATH");
        commit.Mutation.Should().Be(SharedMutationCertainty.None);
        runtime.Snapshot.Generation.Should().Be(generation);
        runtime.Snapshot.Availability.Should().Be(SharedBaseAvailability.Available);
        AuthoritativeBytes(fixture.BaseRoot).Should().BeEquivalentTo(before);
        File.Exists(outside).Should().BeFalse();
    }

    [Theory]
    [InlineData("store")]
    [InlineData("topics")]
    public async Task Shared_operations_preserve_legacy_namespaces_and_root_ignore_bytes(string legacy)
    {
        using var fixture = new SharedOperationFixture();
        string directory = Path.Combine(fixture.BaseRoot, ".scrinia", legacy, "old");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "note.nmp2"), Nmp2ChunkedEncoder.Encode("legacy bytes"));
        File.WriteAllText(Path.Combine(fixture.BaseRoot, ".gitignore"), "# user rules\r\n.scrinia/store/\r\n.scrinia/topics/\r\n");
        var before = AuthoritativeBytes(fixture.BaseRoot);
        var runtime = fixture.Factory.Open(new SharedBaseEntry { Name = "team", Root = fixture.BaseRoot });
        AuthoritativeBytes(fixture.BaseRoot).Should().BeEquivalentTo(before);
        (await fixture.Tools.Bases()).Should().Contain("availability: available");
        AuthoritativeBytes(fixture.BaseRoot).Should().BeEquivalentTo(before);
        (await fixture.Tools.List()).Should().Contain("total: 0");
        AuthoritativeBytes(fixture.BaseRoot).Should().BeEquivalentTo(before);
        (await fixture.Tools.Refresh("team")).Should().Contain("status: success");
        AuthoritativeBytes(fixture.BaseRoot).Should().BeEquivalentTo(before);
        (await fixture.Tools.Remember("team", "/new", ["new body"])).Should().Contain("mutation: applied");
        var after = AuthoritativeBytes(fixture.BaseRoot);
        after.Remove(".scrinia/memories/new.nmp2").Should().BeTrue();
        after.Remove(".scrinia/memories/new.meta.json").Should().BeTrue();
        after.Should().BeEquivalentTo(before);
        runtime.Snapshot.AuthoritativeFiles.Should().Contain(legacy + "/old/note.nmp2");
    }

    [Fact]
    public async Task Oversize_generated_compact_write_is_a_known_validation_refusal()
    {
        using var fixture = new SharedOperationFixture();
        string chunk = new('.', 3 * 1024 * 1024);
        (await fixture.Tools.Remember("team", "/large", [chunk, chunk])).Should().Contain("mutation: applied");
        var runtime = fixture.Factory.Open(new SharedBaseEntry { Name = "team", Root = fixture.BaseRoot });
        string? generation = runtime.Snapshot.Generation;
        var before = AuthoritativeBytes(fixture.BaseRoot);
        (await fixture.Tools.Compact("team", "/large")).Should().Contain("INVALID_PARAMETER").And.Contain("mutation: none");
        AuthoritativeBytes(fixture.BaseRoot).Should().BeEquivalentTo(before);
        runtime.Snapshot.Generation.Should().Be(generation);
        runtime.Snapshot.Availability.Should().Be(SharedBaseAvailability.Available);
        (await fixture.Tools.Remember("team", "/other", ["allowed without refresh"])).Should().Contain("mutation: applied");
    }

    [Fact]
    public void Absent_store_preserves_runtime_identity_without_materializing_a_store()
    {
        string root = Path.Combine(Path.GetTempPath(), "scri-shared-absent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SharedBaseRuntimeFactory();
            var entry = new SharedBaseEntry { Name = "team", Root = root };
            var runtime = factory.Open(entry);
            factory.Open(entry).Should().BeSameAs(runtime);
            runtime.Snapshot.Availability.Should().Be(SharedBaseAvailability.Unavailable);
            runtime.Snapshot.Generation.Should().BeNull();
            Directory.GetFileSystemEntries(root).Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cached_runtime_reports_unsafe_topology_without_payloads(bool directoryLink)
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["private payload"]);
        string target = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "secret.txt"), "outside secret");
        string link = Path.Combine(fixture.BaseRoot, ".scrinia", "unsafe-link");
        if (directoryLink) Directory.CreateSymbolicLink(link, target);
        else File.CreateSymbolicLink(link, Path.Combine(target, "secret.txt"));
        try
        {
            string[] responses = [await fixture.Tools.Recall("team", "/note"), await fixture.Tools.Refresh("team"),
                await fixture.Tools.List("team"), await fixture.Tools.Search("private", "team"),
                await fixture.Tools.List(), await fixture.Tools.Search("private")];
            foreach (string response in responses)
                response.Should().Contain("SHARED_BASE_UNSAFE_PATH").And.Contain("mutation: none")
                    .And.Contain("topology").And.Contain("shared_refresh").And.NotContain("private payload")
                    .And.NotContain("outside secret").And.NotContain("chunks:").And.NotContain("results:");
        }
        finally { if (directoryLink) Directory.Delete(link); else File.Delete(link); }
    }

    private static Dictionary<string, string> AuthoritativeBytes(string root)
    {
        string store = Path.Combine(root, ".scrinia");
        var files = Directory.GetFiles(store, "*", SearchOption.AllDirectories)
            .Where(path => SharedBaseRuntime.IsAuthoritative(Path.GetRelativePath(store, path)))
            .Append(Path.Combine(root, ".gitignore")).Where(File.Exists);
        return files.ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'),
            path => Convert.ToBase64String(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task Markdown_bodies_remain_accessible_by_list_and_recall_without_ordinary_memory_search_indexing()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/skill/one", ["zebra searchable-looking body"]);
        await fixture.Tools.Remember("team", "/agent/two", ["zebra other body"]);
        (await fixture.Tools.List()).Should().Contain("path: /skill/one").And.Contain("path: /agent/two");
        (await fixture.Tools.Recall("team", "/skill/one")).Should().Contain("zebra");
        (await fixture.Tools.Search("zebra")).Should().Contain("results: []");
        (await fixture.Tools.Facts("team", "entities")).Should().Contain("rows: []");
    }

    [Fact]
    public async Task Search_context_uses_the_existing_query_augmentation_semantics()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["zebra unique content"]);
        (await fixture.Tools.Search("absentterm")).Should().Contain("results: []");
        string contextual = await fixture.Tools.Search("absentterm", context: "zebra");
        contextual.Should().Contain("path: /note");
        string explicitQuery = await fixture.Tools.Search("absentterm zebra");
        // Recency contributes a wall-clock-dependent score; compare stable fields exactly.
        static string[] StableLines(string response) => response.Split('\n').Where(line => !line.TrimStart().StartsWith("score:", StringComparison.Ordinal)).ToArray();
        static double Score(string response) => double.Parse(response.Split('\n').Single(line => line.TrimStart().StartsWith("score:", StringComparison.Ordinal)).Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
        StableLines(contextual).Should().Equal(StableLines(explicitQuery));
        Score(contextual).Should().BeApproximately(Score(explicitQuery), 0.001);
    }

    [Fact]
    public async Task Aggregate_results_preserve_base_provenance_partial_warnings_and_zero_success_shape()
    {
        using var first = new SharedOperationFixture();
        using var second = new SharedOperationFixture();
        await first.Tools.Remember("team", "/same", ["shared searchable first"]);
        await second.Tools.Remember("team", "/same", ["shared searchable second"]);
        var tools = SharedOperationFixture.Select(("beta", second.BaseRoot), ("missing", null), ("alpha", first.BaseRoot));
        string result = await tools.List(limit: 1);
        result.Should().Contain("status: partial").And.Contain("base: alpha").And.Contain("base: missing")
            .And.Contain("UNKNOWN_SHARED_BASE").And.Contain("total: 2").And.Contain("hasMore: true").And.NotContain("\nbase: ");
        (await tools.List(offset: 1, limit: 1)).Should().Contain("base: beta");
        (await tools.Search("searchable", limit: 1)).Should().Contain("status: partial").And.Contain("truncated: true");
        var unavailable = SharedOperationFixture.Select(("missing", null), ("offline", Path.Combine(first.Root, "absent")));
        string zero = await unavailable.List();
        zero.Should().Contain("status: error").And.Contain("base: missing").And.Contain("base: offline")
            .And.Contain("total: 0").And.NotContain("results:").And.NotContain("\nbase: ");
        (await unavailable.Bases()).Should().Contain("status: partial").And.Contain("generation: null");
    }

    [Fact]
    public async Task Second_link_endpoint_failure_reports_unknown_and_requires_explicit_refresh()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["memory"]);
        await fixture.Tools.Remember("team", "/skill/one", ["body"]);
        var runtime = fixture.Factory.Open(new SharedBaseEntry { Name = "team", Root = fixture.BaseRoot });
        string generation = runtime.Snapshot.Generation!;
        new FileEntityTransactionCoordinator(fixture.BaseRoot).FailOnceAt(FileEntityFaultPoint.MetaWrite);
        try
        {
            string result = await fixture.Tools.Link("team", "/note", "/skill/one");
            result.Should().Contain("DURABLE_WRITE_FAILED").And.Contain("mutation: unknown").And.NotContain("manifest:").And.NotContain("git -C");
            runtime.Snapshot.Generation.Should().Be(generation);
            runtime.Snapshot.Availability.Should().Be(SharedBaseAvailability.Stale);
            (await fixture.Tools.Append("team", "/note", "blocked")).Should().Contain("SHARED_BASE_STALE");
            (await fixture.Tools.Refresh("team")).Should().Contain("status: success");
            (await fixture.Tools.Recall("team", "/note")).Should().Contain("/skill/one");
            File.ReadAllText(Path.Combine(fixture.BaseRoot, ".scrinia", "skills", "one.meta.json")).Should().NotContain("/note");
        }
        finally { B0FaultSeam.ClearFaults(); }
    }

    [Fact]
    public async Task Existing_uppercase_archive_directory_stays_derived_after_real_overwrite()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/topic/note", ["before"]);
        string archives = Path.Combine(fixture.BaseRoot, ".scrinia", "memories", "topic", "Versions");
        Directory.CreateDirectory(archives);
        (await fixture.Tools.Remember("team", "/topic/note", ["after"])).Should().Contain("mutation: applied");
        if (OperatingSystem.IsWindows()) Directory.GetFiles(archives).Should().NotBeEmpty();
        var runtime = fixture.Factory.Open(new SharedBaseEntry { Name = "team", Root = fixture.BaseRoot });
        runtime.Snapshot.AuthoritativeFiles.Should().NotContain(path => path.Contains("Versions/", StringComparison.Ordinal));
        (await fixture.Tools.List()).Should().Contain("total: 1");
    }

    [Fact]
    public async Task File_entity_rewrites_preserve_valid_null_dates_versions_and_extensions()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/skill/one", ["before"]);
        string sidecar = Path.Combine(fixture.BaseRoot, ".scrinia", "skills", "one.meta.json");
        File.WriteAllText(sidecar, "{\"createdAt\":null,\"v\":0,\"custom\":{\"nested\":true}}");
        await fixture.Tools.Refresh("team");
        (await fixture.Tools.Remember("team", "/skill/one", ["after"])).Should().Contain("mutation: applied");
        using var metadata = JsonDocument.Parse(File.ReadAllText(sidecar));
        metadata.RootElement.TryGetProperty("createdAt", out _).Should().BeFalse();
        metadata.RootElement.GetProperty("v").GetInt32().Should().Be(0);
        metadata.RootElement.GetProperty("custom").GetProperty("nested").GetBoolean().Should().BeTrue();
        File.WriteAllText(sidecar, "invalid metadata");
        await fixture.Tools.Refresh("team");
        await fixture.Tools.Append("team", "/skill/one", "third");
        using var replaced = JsonDocument.Parse(File.ReadAllText(sidecar));
        replaced.RootElement.GetProperty("createdAt").GetString().Should().Be(replaced.RootElement.GetProperty("updatedAt").GetString());
        replaced.RootElement.TryGetProperty("v", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"ref\":42}")]
    [InlineData("{\"ref\":[],\"REF\":[]}")]
    [InlineData("{\"ref\":[],\"ref\":[]}")]
    public async Task Malformed_file_entity_references_fail_before_either_endpoint_changes(string metadata)
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["before"]);
        await fixture.Tools.Remember("team", "/skill/one", ["body"]);
        string sidecar = Path.Combine(fixture.BaseRoot, ".scrinia", "skills", "one.meta.json");
        File.WriteAllText(sidecar, metadata);
        await fixture.Tools.Refresh("team");
        string before = File.ReadAllText(Path.Combine(fixture.BaseRoot, ".scrinia", "memories", "note.meta.json"));
        (await fixture.Tools.Link("team", "/note", "/skill/one")).Should().Contain("SHARED_ENTRY_CORRUPT").And.Contain("mutation: none");
        File.ReadAllText(sidecar).Should().Be(metadata);
        File.ReadAllText(Path.Combine(fixture.BaseRoot, ".scrinia", "memories", "note.meta.json")).Should().Be(before);
    }

    [Theory]
    [InlineData("/versions/note")]
    [InlineData("/topic/Versions/note")]
    [InlineData("/topic/cache.tmp/note")]
    public async Task Derived_namespace_targets_never_report_applied(string path)
    {
        using var fixture = new SharedOperationFixture();
        (await fixture.Tools.Remember("team", path, ["forbidden"])).Should().Contain("INVALID_PATH").And.Contain("mutation: none");
        (await fixture.Tools.List()).Should().Contain("total: 0");
    }

    [Fact]
    public async Task Newer_memory_is_readable_but_rewrite_is_refused_and_forget_is_permitted()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["before"]);
        string sidecar = Path.Combine(fixture.BaseRoot, ".scrinia", "memories", "note.meta.json");
        string json = File.ReadAllText(sidecar);
        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        using var version = JsonDocument.Parse("2");
        fields["v"] = version.RootElement.Clone();
        // Test fixture serialization only; product serialization remains source-generated.
        File.WriteAllText(sidecar, JsonSerializer.Serialize(fields));
        await fixture.Tools.Refresh("team");
        (await fixture.Tools.Recall("team", "/note")).Should().Contain("before");
        (await fixture.Tools.Append("team", "/note", "after")).Should().Contain("SHARED_FORMAT_READ_ONLY");
        (await fixture.Tools.Forget("team", "/note")).Should().Contain("mutation: applied");
    }

    [Fact]
    public async Task Parameter_validation_precedes_disabled_membership_and_never_echoes_rejected_targets()
    {
        using var fixture = new SharedOperationFixture();
        (await fixture.Tools.Remember("private", "/secret", [])).Should().Contain("INVALID_PARAMETER").And.NotContain("private").And.NotContain("/secret");
        (await fixture.Tools.Recall("private", "~temporary")).Should().Contain("SHARED_BASE_NOT_ENABLED").And.NotContain("private");
        (await fixture.Tools.Recall("team", "~temporary")).Should().Contain("SHARED_EPHEMERAL_NOT_SUPPORTED");
        (await fixture.Tools.Link("team", "/missing", "/temp/note")).Should().Contain("SHARED_EPHEMERAL_NOT_SUPPORTED");
        (await fixture.Tools.List(offset: int.MaxValue)).Should().Contain("INVALID_PARAMETER");
        (await fixture.Tools.Facts("team", "intersect", entities: ["a", "a"])).Should().Contain("INVALID_PARAMETER");
        (await fixture.Tools.Refresh(null!)).Should().Contain("INVALID_PARAMETER");
    }

    [Fact]
    public async Task Reads_remain_on_open_generation_until_explicit_refresh_and_stale_mutations_do_not_write()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["before"]);
        string body = Path.Combine(fixture.BaseRoot, ".scrinia", "memories", "note.nmp2");
        File.WriteAllText(body, Nmp2ChunkedEncoder.Encode("external"));
        (await fixture.Tools.Recall("team", "/note")).Should().Contain("before").And.NotContain("external");
        (await fixture.Tools.Append("team", "/note", "forbidden")).Should().Contain("SHARED_BASE_STALE").And.Contain("mutation: none");
        File.ReadAllText(body).Should().Be(Nmp2ChunkedEncoder.Encode("external"));
        (await fixture.Tools.Refresh("team")).Should().Contain("status: success");
        (await fixture.Tools.Recall("team", "/note")).Should().Contain("external");
    }

    [Fact]
    public async Task Manifests_contain_exact_live_pairs_and_compaction_does_not_resurrect_archives()
    {
        using var fixture = new SharedOperationFixture();
        string created = await fixture.Tools.Remember("team", "/topic/note", ["one", "two"]);
        created.Should().Contain(".scrinia/memories/topic/note.nmp2").And.Contain(".scrinia/memories/topic/note.meta.json");
        string compacted = await fixture.Tools.Compact("team", "/topic/note");
        compacted.Should().Contain("mutation: applied").And.NotContain("versions/").And.NotContain(".lock");
        (await fixture.Tools.List()).Should().Contain("total: 1");
        (await fixture.Tools.Forget("team", "/topic/note")).Should().Contain("mutation: applied");
        (await fixture.Tools.List()).Should().Contain("total: 0").And.Contain("results: []");
    }

    [Fact]
    public async Task Missing_memory_sidecar_is_corrupt_and_cannot_be_overwritten()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["retained"]);
        File.Delete(Path.Combine(fixture.BaseRoot, ".scrinia", "memories", "note.meta.json"));
        await fixture.Tools.Refresh("team");
        (await fixture.Tools.Recall("team", "/note")).Should().Contain("SHARED_ENTRY_CORRUPT");
        (await fixture.Tools.Remember("team", "/note", ["replacement"])).Should().Contain("SHARED_ENTRY_CORRUPT");
        (await fixture.Tools.List()).Should().NotContain("path: /note");
        (await fixture.Tools.Search("retained")).Should().NotContain("path: /note");
        (await fixture.Tools.Facts("team", "entities")).Should().NotContain("/note");
    }

    [Fact]
    public async Task File_entity_link_preserves_body_bytes_and_unrelated_extensions_without_indexing_refs()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/skill/one", ["body one\r\n"]);
        await fixture.Tools.Remember("team", "/agent/two", ["body two"]);
        string first = Path.Combine(fixture.BaseRoot, ".scrinia", "skills", "one.md");
        File.WriteAllText(Path.ChangeExtension(first, ".meta.json"), "{\"custom\":{\"keep\":true}}");
        await fixture.Tools.Refresh("team");
        byte[] before = File.ReadAllBytes(first);
        (await fixture.Tools.Link("team", "/skill/one", "/agent/two", "do not persist me")).Should().Contain("mutation: applied");
        File.ReadAllBytes(first).Should().Equal(before);
        string sidecar = File.ReadAllText(Path.ChangeExtension(first, ".meta.json"));
        using var metadata = JsonDocument.Parse(sidecar);
        metadata.RootElement.GetProperty("ref")[0].GetString().Should().Be("/agent/two");
        metadata.RootElement.GetProperty("custom").GetProperty("keep").GetBoolean().Should().BeTrue();
        sidecar.Should().NotContain("do not persist me");
        (await fixture.Tools.Recall("team", "/skill/one")).Should().Contain("attributes: {}").And.Contain("keywords: []");
        (await fixture.Restart().Recall("team", "/skill/one")).Should().Contain("body one").And.Contain("attributes: {}");
        File.ReadAllText(Path.ChangeExtension(first, ".meta.json")).Should().Be(sidecar);
    }

    [Fact]
    public async Task Proven_whole_memory_rollback_reports_none_and_preserves_previous_generation()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["before"]);
        var runtime = fixture.Factory.Open(new SharedBaseEntry { Name = "team", Root = fixture.BaseRoot });
        runtime.Store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);
        string result = await fixture.Tools.Remember("team", "/note", ["after"]);
        result.Should().Contain("DURABLE_WRITE_FAILED").And.Contain("mutation: none");
        (await fixture.Tools.Recall("team", "/note")).Should().Contain("before");
    }

    [Fact]
    public async Task Retained_scope_recovery_evidence_blocks_cached_shared_reads()
    {
        using var fixture = new SharedOperationFixture();
        await fixture.Tools.Remember("team", "/note", ["before"]);
        File.WriteAllText(Path.Combine(fixture.BaseRoot, ".scrinia", "memories", ".restore-journal.json"), "invalid retained evidence");
        string result = await fixture.Tools.Recall("team", "/note");
        result.Should().Contain("status: error").And.NotContain("chunks:");
    }
}
