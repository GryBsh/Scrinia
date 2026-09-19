using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// B6 regression lock — the artifact carrier carries NO B1–B5 transaction/recovery/maintenance
/// metadata. The B6 verdict is "no-op": the format needs NO change because the carrier
/// (sidecar + NMP/2 body) structurally cannot hold the control-data that B1–B5 lives in, while
/// still expressing the maintenance signals B5 needs at the memory level.
///
/// <para>Four structural facts, each pinned by one test:</para>
/// <list type="bullet">
/// <item>1. The sidecar never serializes a B1–B5 key (ArtifactEntry has no such field).</item>
/// <item>2. The sidecar already carries B5's maintenance signals (orphan/ref) — no schema change.</item>
/// <item>3. The NMP/2 body has no metadata slot beyond strategy/bytes/crc — embedding B1–B5 there
/// is structurally impossible without a version bump.</item>
/// <item>4. B1–B5 control data has its OWN separate, versioned format homes (append/restore journal,
/// recovery envelope, multi-memory transaction) — not a slot in the carrier.</item>
/// </list>
///
/// <para>Test-only: no <c>src/</c> file is touched. The sidecar is produced through the store's own
/// write seam (<see cref="FileMemoryStore.InvokeWriteSidecarForTest"/>) so a regression that ever
/// added a recovery/transaction field to <see cref="ArtifactEntry"/> fails here.</para>
/// </summary>
public sealed partial class B6CarrierInvarianceTests
{
    /// <summary>
    /// Every B1–B5 control-data key that must never appear inside the artifact sidecar carrier.
    /// Matched as a case-insensitive substring so a camelCase or PascalCase serialization of the
    /// key is caught regardless of the property's wire spelling.
    /// </summary>
    private static readonly string[] CarrierForbiddenKeys =
    [
        "recovery",          // B2  recovery envelope / RecoveryInfo
        "operationId",       // B1/B3 append + whole-memory correlation
        "failure",           // B0  failure taxonomy
        "b0result",          // B0  wrapper
        "b0outcome",         // B0  wrapper
        "b0failure",         // B0  wrapper
        "coordinator",       // B4  multi-memory coordinator
        "multimemory",       // B4  multi-memory record
        "recoveryenvelope",  // B2  recovery envelope document
        "maintenance",       // B5  maintenance event
        "transactionid",     // B1/B4 append + multi-memory transaction id
        "participant",       // B4  transactional participant
        "evidencepath",      // B3  recovery evidence
        "warning"            // B0  success-with-warning
    ];

    // Mirrors FileStoreJsonContext's options exactly (the store's context is private); the store
    // serializes with WriteIndented=true, camelCase, WhenWritingNull. Deserializing through this
    // mirror is the inverse of the store's write, so the round trip in test 2 exercises the store's
    // own wire shape end to end.
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ArtifactEntry))]
    private partial class SidecarMirror : JsonSerializerContext;

    /// <summary>A realistic entry with a full attribute set, including B5's maintenance signals
    /// (<c>orphan</c> state marker + <c>file</c>/<c>ref</c> reference keys).</summary>
    private static ArtifactEntry FullyAttributedEntry() => new(
        Address: MemoryAddress.Parse("/notes/b6"),
        ArtifactKey: "artifacts/notes/b6.nmp2",
        OriginalBytes: 200,
        ChunkCount: 1,
        CreatedAt: DateTimeOffset.UnixEpoch,
        ContentPreview: "a realistic regression note",
        Keywords: ["regression", "carrier"],
        Attributes: new Dictionary<string, string[]>
        {
            ["person"] = ["Alice"],
            ["place"] = ["HQ"],
            ["topic"] = ["B6"],
            ["file"] = ["file:src/Scrinia.Core/FileMemoryStore.cs"],
            ["ref"] = ["ref:/learn/b6-verdict"],
            [KnownAttributes.OrphanKey] = ["true"],
            [KnownAttributes.ReviewKey] = ["2026-09-01"],
        });

    /// <summary>Serializes an entry through the store's OWN write path and returns the sidecar JSON
    /// it produced — the exact bytes <c>SerializeSidecar</c>/<c>FileStoreJsonContext</c> emit.</summary>
    private static string WriteSidecarThroughStore(FileMemoryStore store, ArtifactEntry entry, string storeDir)
    {
        store.InvokeWriteSidecarForTest(entry, storeDir);
        string[] sidecars = Directory
            .GetFiles(storeDir, "*.meta.json", SearchOption.AllDirectories);
        sidecars.Should().HaveCount(1, "the store path writes exactly one sidecar per entry");
        return File.ReadAllText(sidecars[0]);
    }

    [Fact]
    public void Sidecar_carries_no_B1_through_B5_transaction_metadata()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string sidecar = WriteSidecarThroughStore(store, FullyAttributedEntry(), scope.TempDir);

        // Case-insensitive substring check: a camelCase or PascalCase wire spelling of any B1-B5
        // key is caught either way.
        string lower = sidecar.ToLowerInvariant();
        foreach (var key in CarrierForbiddenKeys)
            lower.Should().NotContain(key.ToLowerInvariant(),
                "B1-B5 control-data key '{0}' must never be serialized into the artifact sidecar carrier", key);
    }

    [Fact]
    public void Sidecar_expresses_B5_maintenance_signals_without_schema_change()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var entry = new ArtifactEntry(
            Address: MemoryAddress.Parse("/notes/maint"),
            ArtifactKey: "artifacts/notes/maint.nmp2",
            OriginalBytes: 50,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UnixEpoch,
            Attributes: new Dictionary<string, string[]>
            {
                [KnownAttributes.OrphanKey] = ["true"],
                ["ref"] = ["file:src/Scrinia.Core/FileMemoryStore.cs"],
            });

        string sidecar = WriteSidecarThroughStore(store, entry, scope.TempDir);

        // The serialized sidecar literally carries the maintenance signals B5 relies on.
        string lower = sidecar.ToLowerInvariant();
        lower.Should().Contain(KnownAttributes.OrphanKey,
            "orphan must be written to the sidecar as B5's no-garbage maintenance marker");
        lower.Should().Contain("\"ref\"",
            "ref must be written to the sidecar as B5's inbound-reference maintenance marker");

        // Round-trips through the store's own wire shape (mirror = store's private context).
        ArtifactEntry? round = JsonSerializer.Deserialize(sidecar, SidecarMirror.Default.ArtifactEntry);
        round.Should().NotBeNull("the maintenance attributes must survive the store's serialize/deserialize path");

        round!.Attributes.Should().ContainKey(KnownAttributes.OrphanKey)
            .WhoseValue.Should().Equal("true");
        round.Attributes.Should().ContainKey("ref")
            .WhoseValue.Should().ContainSingle().Which.Should().StartWith("file:");

        // KnownAttributes is the single schema point that exposes these keys — proving they are a
        // first-class, capacity-capped part of the EXISTING sidecar attribute schema.
        KnownAttributes.OrphanKey.Should().Be("orphan");
        KnownAttributes.ReferenceKeys.Should().Contain("ref");
    }

    [Fact]
    public void Nmp2_body_carries_no_metadata_slot_beyond_strategy_bytes_and_crc()
    {
        var strategy = Nmp2Strategy.Instance;
        byte[] body = TestHelpers.Utf8("carrier body");
        string artifact = strategy.Encode(body, new EncodingOptions()).Artifact;

        // Real NMP/2 header grammar: encode, decode-able, and parsed by the strategy itself.
        strategy.CanDecode(artifact).Should().BeTrue();
        strategy.Decode(artifact).Should().Equal(body);

        ArtifactMetadata metadata = strategy.ParseHeader(artifact);
        metadata.StrategyId.Should().Be("nmp/2");
        metadata.OriginalBytes.Should().Be(body.Length);
        metadata.Crc32.Should().NotBeNull("CRC32 is computed over the original bytes per the NMP/2 contract");

        // ArtifactMetadata exposes EXACTLY these three fields — there is no room to embed B1-B5
        // transaction/recovery data in the NMP/2 body without a version bump.
        typeof(ArtifactMetadata)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "StrategyId", "OriginalBytes", "Crc32" });
    }

    [Fact]
    public void Control_data_formats_are_registered_separately_from_the_carrier()
    {
        // B1-B5 control data has its own versioned homes, declared once in DurableFormats.All.
        DurableFormats.All.Should().HaveCount(12,
            "the twelve durable formats are declared exactly once, carrier and control data alike");

        DurableFormats.Registry.Keys.Should().Contain(
            new[] { "append-journal", "restore-journal", "recovery-envelope", "multi-memory-transaction" },
            "B1-B5 control data lives in its own distinct, versioned format contracts — not inside the carrier");

        // Four DISTINCT contracts: the four control-data formats are separate carriers, so the
        // transaction/recovery/maintenance metadata never needs to share the artifact sidecar or
        // the NMP/2 body.
        IReadOnlyList<FormatContract> controlData =
        [
            DurableFormats.AppendJournal,
            DurableFormats.RestoreJournal,
            DurableFormats.RecoveryEnvelope,
            DurableFormats.MultiMemoryTransaction,
        ];
        controlData.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count().Should().Be(4,
            "each control-data format is its own distinct contract");
    }
}
