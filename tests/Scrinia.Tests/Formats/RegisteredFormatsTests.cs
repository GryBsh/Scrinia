using System.Text.Json.Nodes;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests.Formats;

public class RegisteredFormatsTests
{
    /// <summary>
    /// facts.json rebuilds on ANY version that is not Current — older as well as newer. That
    /// asymmetry is deliberate: its version tracks the derivation RULE, not the file shape, so a
    /// cache written by a previous build is a path around the new rule.
    ///
    /// <para>Versions are computed from Current rather than hard-coded. Literal 2 and 4 against a
    /// Current of 3 turn into a FALSE FAILURE the moment the derivation rule is bumped to 4 —
    /// the exact event this test exists to keep safe.</para>
    /// </summary>
    [Fact]
    public void FactsSidecar_RebuildsOnAnyVersionThatIsNotCurrent()
    {
        int current = DurableFormats.EntityFacts.Current;
        foreach (int version in new[] { current - 1, current + 1, 0 })
        {
            string json = $$"""{"version":{{version}},"token":"t","memories":[]}""";
            EntityFactSidecar.TryLoad(json).Should().BeNull(
                $"version {version} is not {current}, and a cache from any other build " +
                "predates or postdates this one's derivation rule");
        }
    }

    [Fact]
    public void FactsSidecar_LoadsAtTheCurrentVersion()
    {
        string json = $$"""
        {"version":{{DurableFormats.EntityFacts.Current}},"token":"t","memories":[]}
        """;
        EntityFactSidecar.TryLoad(json).Should().NotBeNull();
    }

    [Fact]
    public void FactsSidecarVersionConstant_MatchesItsContract() =>
        EntityFactSidecar.Version.Should().Be(DurableFormats.EntityFacts.Current);

    // CanDecode (Nmp2Strategy.cs:205-207) requires BOTH the "NMP/2 " prefix AND an "NMP/END"
    // marker, so a fixture carrying only the header is rejected and would make the true case
    // fail. Build these from a real encode where possible rather than by hand.
    [Theory]
    [InlineData("NMP/2 5B CRC32:00000000\n<payload>\nNMP/END", true)]
    [InlineData("NMP/2 5B CRC32:00000000", false)]
    [InlineData("NMP/1 5B CRC32:00000000\n<payload>\nNMP/END", false)]
    [InlineData("plain text, no sentinel", false)]
    public void Nmp2Parser_MatchesItsDeclaredContract(string header, bool shouldParse)
    {
        // Conformance only. The parser is NOT routed through the gate -- its sentinel check
        // already enforces this and it is a hot, heavily-tested path. This test is what keeps
        // the registry's claim about it honest.
        DurableFormats.Nmp2Body.OnOlder.Should().Be(FormatDisposition.Refuse);
        DurableFormats.Nmp2Body.OnNewer.Should().Be(FormatDisposition.Refuse);

        Nmp2Strategy.Instance.CanDecode(header).Should().Be(shouldParse);
    }

    // ── Task 6: skill and agent sidecars ─────────────────────────────────────────────
    //
    // These two formats were not in the original review's finding at all — they turned up only
    // by enumerating every durable format instead of sampling the one that was reported.

    [Fact]
    public void SkillMeta_PreservesUnknownKeys()
    {
        const string json = """{"role":"reviewer","futureThing":42}""";
        var meta = JsonSerializer.Deserialize(json, ScriniaMcpJsonContext.Default.SkillFileMeta)!;

        meta.Unknown.Should().ContainKey("futureThing");
        JsonSerializer.Serialize(meta, ScriniaMcpJsonContext.Default.SkillFileMeta)
            .Should().Contain("futureThing");
    }

    [Fact]
    public void SkillMeta_AtV1_EmitsNoVersionKey()
    {
        var meta = new SkillFileMeta(null, "reviewer", null, null, null, null);
        JsonSerializer.Serialize(meta, ScriniaMcpJsonContext.Default.SkillFileMeta)
            .Should().NotContain("\"v\"");
    }

    [Fact]
    public void AgentMeta_PreservesUnknownKeys()
    {
        const string json = """{"createdAt":"2026-01-01T00:00:00+00:00","futureThing":42}""";
        var meta = JsonSerializer.Deserialize(json, ScriniaMcpJsonContext.Default.AgentFileMeta)!;

        meta.Unknown.Should().ContainKey("futureThing");
        JsonSerializer.Serialize(meta, ScriniaMcpJsonContext.Default.AgentFileMeta)
            .Should().Contain("futureThing");
    }

    [Fact]
    public void AgentMeta_AtV1_EmitsNoVersionKey()
    {
        var meta = new AgentFileMeta(null, null);
        JsonSerializer.Serialize(meta, ScriniaMcpJsonContext.Default.AgentFileMeta)
            .Should().NotContain("\"v\"");
    }

    [Fact]
    public void FileEntityJsonContext_SerializesTheSharedSkillAndAgentMetadataContracts()
    {
        JsonSerializer.Serialize(
                new SkillFileMeta(null, "reviewer", null, null, null, null),
                FileEntityJsonContext.Default.SkillFileMeta)
            .Should().Contain("reviewer");
        JsonSerializer.Serialize(
                new AgentFileMeta("created", "updated"),
                FileEntityJsonContext.Default.AgentFileMeta)
            .Should().Contain("created");
    }

    /// <summary>
    /// The write-side gate: a <see cref="SkillFileMeta"/> declaring a version above
    /// <see cref="DurableFormats.SkillMeta"/>'s Current must refuse, mirroring the artifact
    /// sidecar's <c>WriteSidecarCore</c> backstop. Calls <c>WriteSidecarMeta</c> directly (not
    /// through <c>ScriniaMcpTools.SkillCreate</c>), which is what makes this a test of the gate
    /// itself rather than of whatever a specific caller happens to thread through.
    /// </summary>
    [Fact]
    public void WriteSidecarMeta_RefusesASkillMetaNewerThanCurrent()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"scrinia_skillmeta_{Guid.NewGuid():N}.md");
        string metaPath = Path.ChangeExtension(tempFile, ".meta.json");
        try
        {
            var newer = new SkillFileMeta(null, "reviewer", null, null, null, null)
            {
                SchemaVersion = DurableFormats.SkillMeta.Current + 1
            };

            const string sentinel = "{\"createdAt\":\"sentinel-must-survive\"}";
            File.WriteAllText(metaPath, sentinel);

            var act = () => ScriniaMcpTools.WriteSidecarMeta(
                tempFile, newer, ScriniaMcpJsonContext.Default.SkillFileMeta);

            act.Should().Throw<ScopeReadOnlyException>();

            // Assert the sentinel SURVIVES, not that the file is absent. The old assertion was
            // File.Exists(metaPath).Should().BeFalse() on a path nothing had ever created,
            // reached only after the Throw assertion had already passed -- it could not fail,
            // and it could not tell "refused before writing" from "never attempted".
            File.ReadAllText(metaPath).Should().Be(sentinel,
                "a refused write must leave the existing sidecar byte-for-byte untouched");
        }
        finally
        {
            File.Delete(metaPath);
        }
    }

    /// <summary>
    /// Discrimination companion to the refusal test above. If <c>WriteSidecarMeta</c> refused
    /// every write regardless of version — rather than specifically the newer-than-Current
    /// case — the refusal test would still pass, but for the wrong reason. This pins that a
    /// Current-version write still lands.
    /// </summary>
    [Fact]
    public void WriteSidecarMeta_StillWritesASkillMetaAtCurrent()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"scrinia_skillmeta_{Guid.NewGuid():N}.md");
        string metaPath = Path.ChangeExtension(tempFile, ".meta.json");
        try
        {
            var current = new SkillFileMeta(null, "reviewer", null, null, null, null);
            ScriniaMcpTools.WriteSidecarMeta(tempFile, current, ScriniaMcpJsonContext.Default.SkillFileMeta);

            File.Exists(metaPath).Should().BeTrue();
        }
        finally
        {
            File.Delete(metaPath);
        }
    }

    /// <summary>
    /// A read must never throw on a version, even a newer one — <c>ReadSidecarMeta</c>
    /// deliberately does not gate at all (only <c>WriteSidecarMeta</c> does). Written directly
    /// to disk (bypassing the write gate) to exercise the read path against bytes a newer build
    /// actually would have produced.
    /// </summary>
    [Fact]
    public void ReadSidecarMeta_NeverThrowsOnANewerSkillMeta()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"scrinia_skillmeta_{Guid.NewGuid():N}.md");
        string metaPath = Path.ChangeExtension(tempFile, ".meta.json");
        try
        {
            string json = $$"""{"role":"reviewer","v":{{DurableFormats.SkillMeta.Current + 1}}}""";
            File.WriteAllText(metaPath, json);

            var act = () => ScriniaMcpTools.ReadSidecarMeta(tempFile, ScriniaMcpJsonContext.Default.SkillFileMeta);

            act.Should().NotThrow().Which!.SchemaVersion.Should().Be(DurableFormats.SkillMeta.Current + 1);
        }
        finally
        {
            File.Delete(metaPath);
        }
    }

    /// <summary>
    /// A skill rewrite carries its sidecar's unknown fields and version forward.
    ///
    /// <para><b>Through the real production path, not a round-trip.</b> SkillCreate builds a
    /// FRESH SkillFileMeta on every call — it already carries CreatedAt across, and before this
    /// it dropped everything else. So the extension bucket added for these formats was
    /// decorative: the first re-create after versioning shipped would delete exactly the fields
    /// versioning exists to preserve, and the fresh record's null version would sail past the
    /// write gate while doing it. Identical defect to the one BuildDerivedEntry closes for the
    /// artifact sidecar; a round-trip test cannot see either.</para>
    /// </summary>
    [Fact]
    public async Task ReCreatingASkill_KeepsItsSidecarsUnknownKeys()
    {
        using var scope = new TestHelpers.StoreScope();

        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/skill/versioned-probe", ["Review carefully."]);

        string metaPath = Path.Combine(
            scope.WorkspaceDir, ".scrinia", "skills", "versioned-probe.meta.json");
        var obj = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        obj["futureThing"] = JsonValue.Create(42);
        File.WriteAllText(metaPath, obj.ToJsonString());

        await tools.MemoryRemember("/skill/versioned-probe", ["Review even more carefully."]);

        File.ReadAllText(metaPath).Should().Contain("futureThing",
            "a rewrite must carry forward fields this build has no member for");
    }

    [Fact]
    public async Task ReWritingAnAgentConfig_KeepsItsCurrentVersionAndUnknownSidecarFields()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/agent/profile", ["you are helpful"]);

        string metaPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "agent", "profile.meta.json");
        var seededSidecar = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        seededSidecar["v"] = DurableFormats.AgentMeta.Current;
        seededSidecar["futureThing"] = JsonValue.Create(42);
        File.WriteAllText(metaPath, seededSidecar.ToJsonString());

        await tools.MemoryRemember("/agent/profile", ["you are rewritten"]);

        _ = new FileMemoryStore(scope.WorkspaceDir);
        var rewrittenSidecar = JsonDocument.Parse(File.ReadAllBytes(metaPath)).RootElement;
        rewrittenSidecar.GetProperty("v").GetInt32().Should().Be(DurableFormats.AgentMeta.Current);
        rewrittenSidecar.GetProperty("futureThing").GetInt32().Should().Be(42);
    }

    /// <summary>
    /// The SKILL path's version carry-forward, pinned. Re-creating a skill whose sidecar was
    /// written by a newer build must be REFUSED.
    ///
    /// <para>Without <c>SchemaVersion: existingMeta?.SchemaVersion</c> the replacement record
    /// declares no version, so it is not read-only, so the write gate never fires and the newer
    /// sidecar is silently clobbered. The sibling unknown-keys test asserts only on the bucket
    /// half and stayed green through exactly that deletion.</para>
    /// </summary>
    [Fact]
    public async Task ReCreatingASkill_WhoseSidecarIsNewer_IsRefused()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/skill/newer-probe", ["Review carefully."]);

        StampVersion(
            Path.Combine(scope.WorkspaceDir, ".scrinia", "skills", "newer-probe.meta.json"),
            DurableFormats.SkillMeta.Current + 1);

        string skillPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills", "newer-probe.md");
        string versionsDir = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills", "versions");
        byte[] bodyBefore = File.ReadAllBytes(skillPath);
        string sidecarPath = Path.ChangeExtension(skillPath, ".meta.json");
        byte[] sidecarBefore = File.ReadAllBytes(sidecarPath);
        int versionsBefore = Directory.Exists(versionsDir)
            ? Directory.GetFiles(versionsDir).Length : 0;

        var response = ResponseParser.Parse(
            await tools.MemoryRemember("/skill/newer-probe", ["Rewritten."]));

        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);

        // An error envelope alone would miss a refusal after mutation: the archive and body write
        // have no rollback, so raw cold-disk bytes and the archive count prove refusal was early.
        _ = new FileMemoryStore(scope.WorkspaceDir);
        File.ReadAllBytes(skillPath).Should().Equal(bodyBefore,
            "a refused rewrite must not have overwritten the prompt");
        File.ReadAllBytes(sidecarPath).Should().Equal(sidecarBefore,
            "a refused rewrite must not have replaced metadata");
        (Directory.Exists(versionsDir) ? Directory.GetFiles(versionsDir).Length : 0)
            .Should().Be(versionsBefore,
                "a refused rewrite must not deposit a versions/ entry for a change it rejected");
    }

    /// <summary>
    /// The AGENT path's carry-forward, pinned. It received the identical fix as the skill path
    /// and had no test of its own -- deleting BOTH of its carry-forwards left the whole suite
    /// green. A symmetric untested path is where the next defect lives.
    /// </summary>
    [Fact]
    public async Task ReWritingAnAgentConfig_WhoseSidecarIsNewer_IsRefused()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/agent/profile", ["you are helpful"]);

        StampVersion(
            Path.Combine(scope.WorkspaceDir, ".scrinia", "agent", "profile.meta.json"),
            DurableFormats.AgentMeta.Current + 1);

        string agentPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "agent", "profile.md");
        string agentVersions = Path.Combine(scope.WorkspaceDir, ".scrinia", "agent", "versions");
        byte[] bodyBefore = File.ReadAllBytes(agentPath);
        string sidecarPath = Path.ChangeExtension(agentPath, ".meta.json");
        byte[] sidecarBefore = File.ReadAllBytes(sidecarPath);
        int versionsBefore = Directory.Exists(agentVersions)
            ? Directory.GetFiles(agentVersions).Length : 0;

        var response = ResponseParser.Parse(
            await tools.MemoryRemember("/agent/profile", ["you are rewritten"]));

        response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);

        _ = new FileMemoryStore(scope.WorkspaceDir);
        File.ReadAllBytes(agentPath).Should().Equal(bodyBefore,
            "a refused rewrite must not have overwritten the config");
        File.ReadAllBytes(sidecarPath).Should().Equal(sidecarBefore,
            "a refused rewrite must not have replaced metadata");
        (Directory.Exists(agentVersions) ? Directory.GetFiles(agentVersions).Length : 0)
            .Should().Be(versionsBefore,
                "a refused rewrite must not deposit a versions/ entry for a change it rejected");
    }

    [Fact]
    public async Task ReCreatingASkill_WhenTheSidecarMoveFails_ReportsDurableWriteFailureAndLeavesTheAcceptedPrefix()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/skill/durable-control", ["original control instructions"]);
        await tools.MemoryRemember("/skill/durable-control", ["replacement control instructions"]);
        string controlPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills", "durable-control.md");
        File.Exists(controlPath).Should().BeTrue();
        File.Exists(Path.ChangeExtension(controlPath, ".meta.json")).Should().BeTrue();

        await tools.MemoryRemember("/skill/durable-probe", ["original instructions"]);

        string skillPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills", "durable-probe.md");
        string sidecarPath = Path.ChangeExtension(skillPath, ".meta.json");
        string versionsDir = Path.Combine(scope.WorkspaceDir, ".scrinia", "skills", "versions");
        byte[] sidecarBefore = File.ReadAllBytes(sidecarPath);
        int versionsBefore = Directory.Exists(versionsDir) ? Directory.GetFiles(versionsDir).Length : 0;

        DurableFile.FailureInjector = (phase, destination) =>
            phase == DurableWritePhase.BeforeMove && destination.EndsWith(".meta.json", StringComparison.Ordinal)
                ? new UnauthorizedAccessException("sidecar move denied")
                : null;
        try
        {
            var response = ResponseParser.Parse(
                await tools.MemoryRemember("/skill/durable-probe", ["failed sidecar replacement"]));

            response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        // A skill body and sidecar are individually atomic files, not a paired transaction: a
        // sidecar failure may leave this complete new body with the previous sidecar.
        _ = new FileMemoryStore(scope.WorkspaceDir);
        File.ReadAllText(skillPath).Should().Contain("failed sidecar replacement");
        File.ReadAllBytes(sidecarPath).Should().Equal(sidecarBefore);
        Directory.GetFiles(versionsDir).Length.Should().Be(versionsBefore + 1);
    }

    [Fact]
    public async Task ReWritingAnAgent_WhenTheSidecarMoveFails_ReportsDurableWriteFailureAndLeavesTheAcceptedPrefix()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/agent/durable-control", ["original control agent"]);
        await tools.MemoryRemember("/agent/durable-control", ["replacement control agent"]);
        string controlPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "agent", "durable-control.md");
        File.Exists(controlPath).Should().BeTrue();
        File.Exists(Path.ChangeExtension(controlPath, ".meta.json")).Should().BeTrue();

        await tools.MemoryRemember("/agent/durable-probe", ["original agent"]);

        string agentPath = Path.Combine(scope.WorkspaceDir, ".scrinia", "agent", "durable-probe.md");
        string sidecarPath = Path.ChangeExtension(agentPath, ".meta.json");
        string versionsDir = Path.Combine(scope.WorkspaceDir, ".scrinia", "agent", "versions");
        byte[] sidecarBefore = File.ReadAllBytes(sidecarPath);
        int versionsBefore = Directory.Exists(versionsDir) ? Directory.GetFiles(versionsDir).Length : 0;

        DurableFile.FailureInjector = (phase, destination) =>
            phase == DurableWritePhase.BeforeMove && destination.EndsWith(".meta.json", StringComparison.Ordinal)
                ? new UnauthorizedAccessException("sidecar move denied")
                : null;
        try
        {
            var response = ResponseParser.Parse(
                await tools.MemoryRemember("/agent/durable-probe", ["failed sidecar replacement"]));

            response.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        }
        finally
        {
            DurableFile.FailureInjector = null;
        }

        // A config body and sidecar are individually atomic files, not a paired transaction: a
        // sidecar failure may leave this complete new body with the previous sidecar.
        _ = new FileMemoryStore(scope.WorkspaceDir);
        File.ReadAllText(agentPath).Should().Be("failed sidecar replacement");
        File.ReadAllBytes(sidecarPath).Should().Equal(sidecarBefore);
        Directory.GetFiles(versionsDir).Length.Should().Be(versionsBefore + 1);
    }

    /// <summary>Stamps a "v" onto an existing sidecar by hand. The typed record cannot express a
    /// version above Current, because the write gate refuses to persist one.</summary>
    private static void StampVersion(string metaPath, int version)
    {
        File.Exists(metaPath).Should().BeTrue(
            "the sidecar must exist before it can be stamped: " + metaPath);
        var obj = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        obj["v"] = JsonValue.Create(version);
        File.WriteAllText(metaPath, obj.ToJsonString());
    }
}
