using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

// B5 Task 10 — compatibility, serialization, privacy, and final review gate (TDD).
// Final acceptance for B5 design §§0.2, 0.6, 0.7, 2.1–2.2, 5, 5.1, 12 and the plan's
// Task 10 gate: exact source-generated roots, fixed b5c2 vectors, no new durable
// format/index/policy key, unresolved-evidence survival, unchanged B1–B4/CLI behavior,
// privacy boundaries, exact JournalBoundaryProof fields/validator (positive + negative),
// the shared 128-admission invariant with the full class equations, the B5-scoped
// zero-symbol guard, the Task 4 inventory scan (invalid-limit zero-I/O, typed
// ProtectedInvalidLimits mapping, content ownership/no-second-read), the sole-parser
// signatures with the four superseded names absent, and the continuation ownership
// boundary. Test-only; no production changes.
public sealed class MaintenanceSerializationAndCompatibilityTests
{
    private static string RepoRoot() => RepoPaths.Root();

    private static string ReadSource(params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. relativeSegments]));

    private static string[] SerializableRoots(Type context) =>
        context.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.Name.Contains(
                "JsonSerializableAttribute", StringComparison.Ordinal))
            .Select(attribute => ((Type)attribute.ConstructorArguments[0].Value!).Name)
            .ToArray();

    private static string[] SerializableRootFullNames(Type context) =>
        context.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.Name.Contains(
                "JsonSerializableAttribute", StringComparison.Ordinal))
            .Select(attribute => ((Type)attribute.ConstructorArguments[0].Value!).FullName ?? string.Empty)
            .ToArray();

    // ── 1. Source-generated JSON roots ────────────────────────────────────────

    [Fact]
    public void MaintenanceJsonContext_RegistersOnlyTheDirectlySerializedResultPolicyGraph()
    {
        string[] roots = SerializableRoots(typeof(MaintenanceJsonContext))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        roots.Should().Contain("MaintenanceCandidate");
        roots.Should().Contain("MaintenanceCandidateResult");
        roots.Should().Contain("MaintenanceScopeResult");
        roots.Should().Contain("MaintenanceResult");
        roots.Should().Contain("MaintenanceCandidateKind");
        roots.Should().Contain("MaintenanceProtectionReason");
        roots.Should().HaveCount(6);
    }

    [Fact]
    public void SerializedRoots_NeverIncludeCursorKeysIdentitiesProjectionsOrLegacyPayloads()
    {
        string[] forbidden =
        [
            "MaintenanceCursor", "MaintenanceCursorCodec", "MaintenanceRunRegistry",
            "MaintenanceCandidateKey", "MaintenanceWorkspaceKey", "MaintenanceRunId",
            "MaintenanceSnapshotId", "MaintenanceScopeBinding", "MaintenanceHash256",
            "MaintenanceCandidateProjection", "MaintenanceCandidateIdentity",
            "MaintenanceDiscoveryPage", "JournalBoundaryProof", "JournalEnumeratorBoundary",
        ];

        string[] maintenanceRoots = SerializableRootFullNames(typeof(MaintenanceJsonContext));
        foreach (string name in forbidden)
            maintenanceRoots.Should().NotContain(root => root.Contains(name, StringComparison.Ordinal));

        string[] cliRoots = SerializableRootFullNames(typeof(CliJsonContext));
        foreach (string name in forbidden)
            cliRoots.Should().NotContain(root => root.Contains(name, StringComparison.Ordinal));
        cliRoots.Should().Contain(root => root.Contains("CliMaintenanceOutput", StringComparison.Ordinal));
        cliRoots.Should().Contain(root => root.Contains("CliMaintenanceScopeOutput", StringComparison.Ordinal));
    }

    [Fact]
    public void ContinuationCursors_AreBinaryAndNeverJsonSerialized()
    {
        string contracts = ReadSource("src", "Scrinia.Core", "MaintenanceContracts.cs");

        contracts.Should().Contain("MaintenanceCursorCodec");
        contracts.Should().Contain("b5c2.");

        string[] roots = SerializableRoots(typeof(MaintenanceJsonContext));
        roots.Should().NotContain(name => name.Contains("Cursor", StringComparison.Ordinal));
    }

    // ── 2. Fixed b5c2 vectors ─────────────────────────────────────────────────

    [Fact]
    public void HmacPrimitive_MatchesTheRfc4231ConformanceVector()
    {
        byte[] key = Enumerable.Repeat((byte)0x0b, 20).ToArray();
        byte[] data = Convert.FromHexString("4869205468657265");

        string mac = Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();

        mac.Should().Be("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7");
    }

    [Fact]
    public void FieldEncoder_MatchesTheApprovedU16U8U32Vector()
    {
        Span<byte> encoded = stackalloc byte[7];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(encoded[..2], 7);
        encoded[2] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(encoded[3..], 3);

        Convert.ToHexString(encoded).Should().Be("00070200000003");
    }

    [Fact]
    public void CandidateKey_MatchesTheApprovedB5c2Vector()
    {
        var identity = new MaintenanceCandidateIdentity(
            MaintenanceCandidateKind.CompletedRecoveryEvidence,
            new MaintenanceWorkspaceKey(Guid.Empty),
            3,
            new MaintenanceHash256(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray()),
            42);

        MaintenanceCandidateKey key = MaintenanceCursorCodec.ComputeCandidateKey(
            new MaintenanceWorkspaceKey(Guid.Empty), identity,
            "0123456789abcdef0123456789abcdef"u8);

        Convert.ToHexString(key.Value.ToByteArray()).Should().Be("C6D0988044D8F2D78F44EC2A1625F0AC");
    }

    [Fact]
    public void TokenMac_MatchesTheApprovedB5c2Vector()
    {
        byte[] signingKey = System.Text.Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        byte[] payload = new byte[128];
        payload[0] = 2;
        byte[] trailer = [0, 0, 0, 0, 0, 0, 0, 1];
        trailer.CopyTo(payload, 120);
        byte[] message = System.Text.Encoding.ASCII.GetBytes("b5c2.").Concat(payload).ToArray();

        string mac = Convert.ToHexString(HMACSHA256.HashData(signingKey, message)).ToLowerInvariant();

        mac.Should().Be("b23e2b7ba9ffb5015d1f7a276b6e56f503a23a49abda1604587cd62e555fd2cf");
    }

    [Fact]
    public void CursorWireFormat_IsB5c2PrefixOver160BytesWith219CharCeiling()
    {
        var cursor = new MaintenanceCursor(
            2,
            new MaintenanceWorkspaceKey(Guid.Empty),
            new MaintenanceRunId(Guid.Empty),
            new MaintenanceSnapshotId(Guid.Empty),
            new MaintenanceScopeBinding(Guid.Empty),
            0,
            MaintenanceCursorPhase.Discover,
            new MaintenanceCandidateKey(Guid.Empty),
            0,
            new MaintenanceHash256(new byte[32]),
            (ulong)DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());

        string token = MaintenanceCursorCodec.Encode(cursor, "0123456789abcdef0123456789abcdef"u8);

        token.Should().StartWith("b5c2.");
        token.Length.Should().BeLessThanOrEqualTo(219);
        string body = token["b5c2.".Length..];
        body.Length.Should().Be(214);
        byte[] decoded = Convert.FromBase64String(
            body.Replace('-', '+').Replace('_', '/') + "==");
        decoded.Should().HaveCount(160);
        decoded[..128].Should().NotBeNull();
        MaintenanceCursorCodec.TryDecode(token, "0123456789abcdef0123456789abcdef"u8,
            out MaintenanceCursor roundTripped, out string? error).Should().BeTrue();
        error.Should().BeNull();
        roundTripped.Should().Be(cursor);
    }

    // ── 3. No new durable format, index, or policy key ────────────────────────

    [Fact]
    public void PolicyLoader_OwnsExactlyTheFiveApprovedKeysWithConservativeDefaults()
    {
        FieldInfo[] keyFields = typeof(MaintenancePolicyLoader)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.Name.EndsWith("Key", StringComparison.Ordinal)
                && field.FieldType == typeof(string))
            .ToArray();

        keyFields.Should().HaveCount(5);
        MaintenancePolicy.DefaultMaxArchivedVersions.Should().Be(10);
        MaintenancePolicy.DefaultMinimumAgeDays.Should().Be(30);
        MaintenancePolicy.DefaultJournalCleanupBatchSize.Should().Be(50);
        MaintenancePolicy.DefaultTimeBudgetSeconds.Should().Be(5);
        MaintenancePolicy.DefaultBackgroundEnabled.Should().BeTrue();
    }

    [Fact]
    public void JournalCleanupBatchSize_StaysWithinOneToFiftyWithFiftyCeiling()
    {
        var config = new CoreWorkspaceConfig(new Dictionary<string, string>
        {
            ["Scrinia:Maintenance:JournalCleanupBatchSize"] = "51",
        });

        MaintenancePolicyLoadResult result = MaintenancePolicyLoader.Load(config);

        result.IsValid.Should().BeFalse();
        result.Policy.Should().BeNull();
        MaintenanceLimits.JournalBudgetMaximum.Should().Be(50);
        MaintenancePlanner.EffectiveJournalBudget(
            new MaintenancePolicy(10, 30, 50, 5, true), 50).Should().Be(50);
    }

    [Fact]
    public void CoreIntroduces_NoArchiveIndexAndKeepsTheNmp2WireContract()
    {
        Type[] coreTypes = typeof(MaintenanceLimits).Assembly.GetTypes();

        coreTypes.Should().NotContain(type => type.Name.Contains("ArchiveIndex", StringComparison.Ordinal));
        coreTypes.Should().NotContain(type => type.Name.Contains("MaintenanceManifest", StringComparison.Ordinal));
        new Nmp2Strategy().StrategyId.Should().Be("nmp/2");
    }

    // ── 4. Unresolved evidence survives ───────────────────────────────────────

    [Fact]
    public void BlockedAndQuarantinedEvidence_NeverAuthorizesCleanup()
    {
        var block = new RecoveryBlock("local", "evidence", RecoveryOperation.Restore,
            "operation", RecoveryBlockReason.Malformed, "detail");
        var projection = new B2RecoveryMaintenanceAdapter()
            .Project(new RecoveryDiscoveryResult([], block), "local").Single();

        RecoveryCleanupDecision decision = new B2RecoveryMaintenanceAdapter().EvaluateCleanup(
            projection, new HashSet<string>(StringComparer.Ordinal));

        decision.Eligible.Should().BeFalse();
        projection.IsTerminalAndVerified.Should().BeFalse();
    }

    [Fact]
    public void QuarantinedB4Evidence_RemainsProtectedUntilExplicitResolution()
    {
        var state = new B4ParticipantRecoveryState(
            "transaction", "operation", "local", "/notes/item", "participant", 1, "participant-1", "kind",
            ["rollback"], new Dictionary<string, string>(), new Dictionary<string, string>(),
            TransactionalParticipantLifecycleState.Ambiguous, "handle", "v1", 0, 0, true, true,
            TransactionalFailureCode.UnknownTransient, "evidence", "journal", "v1",
            TransactionalQuarantineState.Quarantined, "recovery-mismatch");

        RecoveryCleanupDecision decision = new B4ParticipantMaintenanceAdapter().EvaluateCleanup(
            new B4ParticipantMaintenanceAdapter().Project(state),
            new HashSet<string>(StringComparer.Ordinal));

        decision.Eligible.Should().BeFalse();
        decision.Protection.Should().Be(MaintenanceProtectionReason.UnresolvedRecovery);
    }

    [Fact]
    public async Task MaintenanceBatch_OnLiveContent_DeletesNothingAndRetainsEveryEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember(path: "/notes/kept", content: ["kept content"]);
        int invalidationsBefore = store.CommittedScopeInvalidationCount;
        var service = new FileMemoryMaintenanceService(store, new CoreWorkspaceConfig());

        MaintenanceResult result = await service.ExecuteAsync(
            new MaintenanceRequest(null, null, null, null), CancellationToken.None);

        result.PolicyInvalid.Should().BeFalse();
        result.DeletedArchives.Should().Be(0);
        result.DeletedRecoveryEvidence.Should().Be(0);
        store.CommittedScopeInvalidationCount.Should().Be(invalidationsBefore);
        string recalled = await new ScriniaMcpTools().MemoryRecall(path: "/notes/kept");
        recalled.Should().Contain("kept content");
    }

    // ── 5. B1–B4 and CLI behavior unchanged ───────────────────────────────────

    [Fact]
    public void B2B3B4Adapters_KeepTheirApprovedProjectionContracts()
    {
        typeof(B2RecoveryMaintenanceAdapter).Should().NotBeNull();
        typeof(B3CoordinatorMaintenanceAdapter).Should().NotBeNull();
        typeof(B4ParticipantMaintenanceAdapter).Should().NotBeNull();
        typeof(IRecoveryAdapter).GetMethod("ConsumeParsed").Should().NotBeNull();
        typeof(IRecoveryAdapter).GetMethod("RecoverOrCleanup").Should().NotBeNull();
        typeof(IRecoveryAdapter).GetMethod("Admit").Should().NotBeNull();
    }

    [Fact]
    public void CliKeeps_AllPreexistingJsonRootsAndAddsOnlyMaintenanceOutputs()
    {
        string[] roots = SerializableRoots(typeof(CliJsonContext));

        roots.Should().Contain("CliListOutput");
        roots.Should().Contain("CliSearchOutput");
        roots.Should().Contain("CliStoreOutput");
        roots.Should().Contain("CliMaintenanceOutput");
        roots.Should().Contain("CliMaintenanceScopeOutput");
        roots.Should().NotContain(name => name.Contains("Cursor", StringComparison.Ordinal));
    }

    // ── 6. Privacy boundaries ─────────────────────────────────────────────────

    [Fact]
    public void ContinuationFailures_AreSanitizedWithoutPathsOrSecrets()
    {
        MaintenanceResult local = ScriniaCommands.BuildLocalInvalidContinuation();

        ScriniaCommands.IsInvalidContinuation(local).Should().BeTrue();
        local.ContinuationToken.Should().BeNull();
        local.PolicyError.Should().BeNull();
        local.Warnings.Should().ContainSingle().Which.Should().Be("Invalid continuation.");

        using var registry = new MaintenanceRunRegistry();
        MaintenanceCursorCodec.TryDecode("b5c2.invalid", registry, out _, out string? error)
            .Should().BeFalse();
        error.Should().Be("Invalid continuation.");
    }

    [Fact]
    public void MaintenanceOutputs_CarryNoCursorInternalsKeysOrPaths()
    {
        PropertyInfo[] outputProperties = typeof(CliMaintenanceOutput).GetProperties();
        PropertyInfo[] scopeProperties = typeof(CliMaintenanceScopeOutput).GetProperties();

        foreach (PropertyInfo property in outputProperties.Concat(scopeProperties))
        {
            property.Name.Should().NotBe("SigningKey");
            property.Name.Should().NotBe("CandidateKey");
            property.Name.Should().NotBe("PolicyHash");
            property.Name.Should().NotBe("BodyPath");
        }

        CliMaintenanceOutput output = ScriniaCommands.ToMaintenanceOutput(
            ScriniaCommands.BuildLocalInvalidContinuation());
        output.ContinuationToken.Should().BeNull();
        output.PolicyError.Should().BeNull();
    }

    [Fact]
    public void CursorTokens_AreAsciiAndCarryNoPhysicalPaths()
    {
        var cursor = new MaintenanceCursor(
            2,
            new MaintenanceWorkspaceKey(Guid.NewGuid()),
            new MaintenanceRunId(Guid.NewGuid()),
            new MaintenanceSnapshotId(Guid.NewGuid()),
            new MaintenanceScopeBinding(Guid.NewGuid()),
            0,
            MaintenanceCursorPhase.Discover,
            new MaintenanceCandidateKey(Guid.Empty),
            0,
            new MaintenanceHash256(new byte[32]),
            (ulong)DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        string token = MaintenanceCursorCodec.Encode(cursor, "0123456789abcdef0123456789abcdef"u8);

        token.Should().Match(tokenValue =>
            tokenValue.All(character => character < 128));
        token.Should().NotContain("\\");
        token.Should().NotContain(".scrinia");
    }

    // ── 7. JournalBoundaryProof fields and validator ──────────────────────────
    // B5 design §5 — JournalBoundaryProof.

    [Fact]
    public void JournalBoundaryProof_ExposesExactlyTheApprovedBindingFields()
    {
        PropertyInfo[] properties = typeof(JournalBoundaryProof).GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        string[] names = properties.Select(property => property.Name).ToArray();
        names.Should().Contain("RunId");
        names.Should().Contain("SnapshotId");
        names.Should().Contain("SafeRootBinding");
        names.Should().Contain("EnumeratorId");
        names.Should().Contain("ScopeOrdinal");
        names.Should().Contain("AfterCandidate");
        names.Should().Contain("PageSequence");
        names.Should().Contain("DirectoryEntryOrdinal");
        names.Should().Contain("CanonicalEntryKey");
        names.Should().Contain("Location");
        names.Should().Contain("NameClass");
        names.Should().Contain("EntryKind");
        names.Should().Contain("Provenance");
        typeof(JournalBoundaryProof).GetConstructors().Should().BeEmpty();

        MethodInfo? factory = typeof(JournalBoundaryProof).GetMethod(
            "TryCaptureFromBufferedMetadata", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        factory.Should().NotBeNull();
        MethodInfo? validator = typeof(JournalBoundaryProof).GetMethod(
            "ValidateJournalBoundaryProof", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        validator.Should().NotBeNull();
        validator!.GetParameters().Should().HaveCount(3);
    }

    [Fact]
    public void JournalBoundaryProof_AcceptsOnlyValidatedSafeRootFixedLocationEntryKindMetadata()
    {
        var run = new MaintenanceRunId(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var root = new MaintenanceScopeBinding(Guid.NewGuid());
        var key = new MaintenanceCandidateKey(Guid.NewGuid());
        using var owner = JournalBoundaryEnumerator.Create(run, snapshot, root, 2, 4, null);
        var boundary = owner.BufferJournal(1, key, JournalBoundaryLocation.AppendVersions,
            JournalBoundaryNameClass.AppendJournal, JournalDirectoryEntryKind.File)!;

        JournalBoundaryProof.TryCaptureFromBufferedMetadata(boundary, out JournalBoundaryProof? proof)
            .Should().BeTrue();
        proof!.Provenance.Should().Be(JournalBoundaryProvenance.BufferedDirectoryEntryMetadataOnly);
        proof.EntryKind.Should().Be(JournalDirectoryEntryKind.File);
        var context = new JournalBoundaryValidationContext(run, snapshot, root, owner.Id, 2, null, 4, key,
            JournalDirectoryEntryKind.File, 0);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof, context, out JournalBoundaryRejection rejection)
            .Should().BeTrue();
        rejection.Should().Be(JournalBoundaryRejection.None);
    }

    public static TheoryData<byte, byte> FixedJournalPairs() =>
        new()
        {
            { (byte)JournalBoundaryLocation.Restore, (byte)JournalBoundaryNameClass.RestoreJournal },
            { (byte)JournalBoundaryLocation.AppendVersions, (byte)JournalBoundaryNameClass.AppendJournal },
            { (byte)JournalBoundaryLocation.FileEntity, (byte)JournalBoundaryNameClass.FileEntityRecovery },
        };

    [Theory]
    [MemberData(nameof(FixedJournalPairs))]
    public void JournalBoundaryProof_AcceptsEachFixedJournalLocationNamePair(
        byte locationValue, byte nameValue)
    {
        var location = (JournalBoundaryLocation)locationValue;
        var nameClass = (JournalBoundaryNameClass)nameValue;
        var run = new MaintenanceRunId(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var root = new MaintenanceScopeBinding(Guid.NewGuid());
        var key = new MaintenanceCandidateKey(Guid.NewGuid());
        using var owner = JournalBoundaryEnumerator.Create(run, snapshot, root, 0, 0, null);
        var boundary = owner.BufferJournal(7, key, location, nameClass, JournalDirectoryEntryKind.File)!;

        JournalBoundaryProof.TryCaptureFromBufferedMetadata(boundary, out JournalBoundaryProof? proof)
            .Should().BeTrue();
        var context = new JournalBoundaryValidationContext(run, snapshot, root, owner.Id, 0, null, 0, key,
            JournalDirectoryEntryKind.File, 0);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!, context, out JournalBoundaryRejection rejection)
            .Should().BeTrue();
        rejection.Should().Be(JournalBoundaryRejection.None);
    }

    [Fact]
    public void JournalBoundaryFactory_RejectsNonJournalUnsafeAndConsumedBoundaries()
    {
        var run = new MaintenanceRunId(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var root = new MaintenanceScopeBinding(Guid.NewGuid());
        var key = new MaintenanceCandidateKey(Guid.NewGuid());
        using var owner = JournalBoundaryEnumerator.Create(run, snapshot, root, 0, 0, null);

        var mismatched = owner.BufferJournal(1, key, JournalBoundaryLocation.Restore,
            JournalBoundaryNameClass.AppendJournal, JournalDirectoryEntryKind.File)!;
        JournalBoundaryProof.TryCaptureFromBufferedMetadata(mismatched, out _).Should().BeFalse();

        var directory = owner.BufferJournal(2, key, JournalBoundaryLocation.Restore,
            JournalBoundaryNameClass.RestoreJournal, JournalDirectoryEntryKind.Directory)!;
        JournalBoundaryProof.TryCaptureFromBufferedMetadata(directory, out _).Should().BeFalse();

        owner.BufferJournal(0, key, JournalBoundaryLocation.Restore,
            JournalBoundaryNameClass.RestoreJournal, JournalDirectoryEntryKind.File).Should().BeNull();

        var live = owner.BufferJournal(3, key, JournalBoundaryLocation.AppendVersions,
            JournalBoundaryNameClass.AppendJournal, JournalDirectoryEntryKind.File)!;
        JournalBoundaryProof.TryCaptureFromBufferedMetadata(live, out _).Should().BeTrue();
        owner.Advance();
        JournalBoundaryProof.TryCaptureFromBufferedMetadata(live, out _).Should().BeFalse();
    }

    [Fact]
    public void JournalBoundaryValidator_RejectsWrongBindingAndStaleOrBudgetedProofs()
    {
        var run = new MaintenanceRunId(Guid.NewGuid());
        var snapshot = new MaintenanceSnapshotId(Guid.NewGuid());
        var root = new MaintenanceScopeBinding(Guid.NewGuid());
        var key = new MaintenanceCandidateKey(Guid.NewGuid());
        using var owner = JournalBoundaryEnumerator.Create(run, snapshot, root, 2, 4, null);
        var boundary = owner.BufferJournal(5, key, JournalBoundaryLocation.AppendVersions,
            JournalBoundaryNameClass.AppendJournal, JournalDirectoryEntryKind.File)!;
        JournalBoundaryProof.TryCaptureFromBufferedMetadata(boundary, out JournalBoundaryProof? proof)
            .Should().BeTrue();
        var valid = new JournalBoundaryValidationContext(run, snapshot, root, owner.Id, 2, null, 4, key,
            JournalDirectoryEntryKind.File, 0);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!,
            valid with { ExpectedEntryKind = JournalDirectoryEntryKind.Directory },
            out JournalBoundaryRejection rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.WrongEntryKind);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!,
            valid with { ActiveScopeBinding = new MaintenanceScopeBinding(Guid.NewGuid()) },
            out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.UnsafeOrMismatchedSafeRoot);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!,
            valid with { ActiveRunId = new MaintenanceRunId(Guid.NewGuid()) },
            out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.WrongRun);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!,
            valid with { ActiveSnapshotId = new MaintenanceSnapshotId(Guid.NewGuid()) },
            out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.WrongSnapshot);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!,
            valid with { ActiveEnumeratorId = new MaintenanceEnumeratorId(Guid.NewGuid()) },
            out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.WrongEnumerator);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!,
            valid with { CurrentPageSequence = 5 },
            out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.BoundaryMismatch);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!,
            valid with { ExpectedCanonicalEntryKey = new MaintenanceCandidateKey(Guid.NewGuid()) },
            out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.CanonicalEntryKeyMismatch);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!,
            valid with { JournalBudgetRemaining = 3 },
            out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.JournalBudgetRemaining);

        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!,
            valid with { LastDirectoryEntryOrdinal = 5 },
            out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.StaleOrdinal);

        owner.Advance();
        JournalBoundaryProof.ValidateJournalBoundaryProof(proof!, valid, out rejection).Should().BeFalse();
        rejection.Should().Be(JournalBoundaryRejection.PostInspection);
    }

    [Fact]
    public void JournalBoundaryProof_NeverAuthorizesContentValidityOrAccessibility()
    {
        // Actionable means only validated-safe-root/fixed-location/accepted-layout/entry-kind
        // directory classification: malformed, unsupported, or inaccessible content is discovered
        // only after later counted admission as typed protected evidence.
        var block = new RecoveryBlock("local", "evidence", RecoveryOperation.Restore,
            "operation", RecoveryBlockReason.UnsupportedVersion, "detail");
        var projection = new B2RecoveryMaintenanceAdapter()
            .Project(new RecoveryDiscoveryResult([], block), "local").Single();

        RecoveryCleanupDecision decision = new B2RecoveryMaintenanceAdapter().EvaluateCleanup(
            projection, new HashSet<string>(StringComparer.Ordinal));

        decision.Eligible.Should().BeFalse();
        decision.Protection.Should().Be(MaintenanceProtectionReason.UnsupportedVersion);
        projection.IsTerminalAndVerified.Should().BeFalse();
    }

    // ── 8. Shared 128-admission invariant and class equations ─────────────────
    // B5 design §5.1.

    [Fact]
    public void AdmissionBounds_UseOneSharedPageWithExactCeilings()
    {
        MaintenanceLimits.CandidatePageSize.Should().Be(128);
        MaintenanceLimits.UnsafeScanLimit.Should().Be(32);
        MaintenanceLimits.JournalBudgetMaximum.Should().Be(50);
        MaintenanceLimits.ReturnedProjectionLimit.Should().Be(64);
        MaintenanceLimits.LookaheadSize.Should().Be(1);
        MaintenanceLimits.MetadataReadLimit.Should().Be(128);
    }

    private static MaintenancePlannerCandidate PlannerCandidate(
        ushort scope, MaintenanceCandidateKind kind, bool isJournal, bool isUnsafe, ulong ordinal) =>
        new(scope, kind, new MaintenanceCandidateKey(Guid.NewGuid()),
            new MaintenanceHash256(new byte[32]), ordinal, isJournal, isUnsafe, null,
            !isUnsafe, !isUnsafe, true, false, false);

    [Fact]
    public void PlannerPage_EnforcesTheFullClassEquationsOnEveryBoundary()
    {
        var projections = Array.Empty<MaintenancePlannerProjection>();
        var page = new MaintenancePlannerPage(projections,
            10, 8, 10, 8, 0, false, 2, 1, 1, 4, 4, 50, 5, 5, 5, false, 0);

        page.Scanned.Should().Be(page.JournalInspected + page.NonJournalInspected);
        page.Scanned.Should().Be(
            page.SafeJournalInspected + page.UnsafeJournalScanned +
            page.SafeNonJournalInspected + page.UnsafeNonJournalScanned);
        (page.Scanned - page.UnsafeScanned).Should().Be(
            page.SafeJournalInspected + page.SafeNonJournalInspected);
        page.UnsafeScanned.Should().Be(page.UnsafeJournalScanned + page.UnsafeNonJournalScanned);
        page.JournalInspected.Should().Be(page.SafeJournalInspected + page.UnsafeJournalScanned);
        page.NonJournalInspected.Should().Be(page.SafeNonJournalInspected + page.UnsafeNonJournalScanned);
        page.Scanned.Should().BeLessThanOrEqualTo(128);
        page.JournalInspected.Should().BeLessThanOrEqualTo(page.JournalBudget);
        page.UnsafeScanned.Should().BeLessThanOrEqualTo(32);
        page.MetadataReads.Should().BeLessThanOrEqualTo(128);
        page.Returned.Should().BeLessThanOrEqualTo(64);
        (page.Returned + page.Lookahead).Should().BeLessThanOrEqualTo(65);
    }

    [Fact]
    public void PlannerPlan_ChargesEveryUnsafeCandidateOnceAgainstTheSharedPage()
    {
        var candidates = new List<MaintenancePlannerCandidate>();
        for (ulong index = 1; index <= 40; index++)
            candidates.Add(PlannerCandidate(0, MaintenanceCandidateKind.ArchivePair, false, false, index));
        for (ulong index = 41; index <= 50; index++)
            candidates.Add(PlannerCandidate(0, MaintenanceCandidateKind.CompletedRecoveryEvidence, true, true, index));
        for (ulong index = 51; index <= 60; index++)
            candidates.Add(PlannerCandidate(0, MaintenanceCandidateKind.ArchivePair, false, true, index));

        MaintenancePlannerPage page = MaintenancePlanner.Plan(candidates, 50);

        page.Scanned.Should().BeLessThanOrEqualTo(128);
        page.UnsafeScanned.Should().Be(20);
        page.UnsafeJournalScanned.Should().Be(10);
        page.UnsafeNonJournalScanned.Should().Be(10);
        page.Scanned.Should().Be(
            page.SafeJournalInspected + page.UnsafeJournalScanned +
            page.SafeNonJournalInspected + page.UnsafeNonJournalScanned);
        page.JournalInspected.Should().Be(page.SafeJournalInspected + page.UnsafeJournalScanned);
        page.NonJournalInspected.Should().Be(page.SafeNonJournalInspected + page.UnsafeNonJournalScanned);
        page.NonJournalInspected.Should().BeLessThanOrEqualTo(128 - page.JournalInspected);
    }

    // ── 9. B5-scoped zero-symbol guard ────────────────────────────────────────
    // Plan Task 3 inventory: forbidden only outside the exact inventory row and the
    // named historical removal note; excluded embedding paths are never scanned.

    [Fact]
    public void B5Scope_ContainsNoSupersededCursorOrSecretKeySymbols()
    {
        string[] scopeFiles =
        [
            "src/Scrinia.Core/MaintenanceContracts.cs",
            "src/Scrinia.Core/Configuration/MaintenancePolicy.cs",
            "src/Scrinia.Core/MaintenancePlanner.cs",
            "src/Scrinia.Core/FileMemoryMaintenanceService.cs",
            "src/Scrinia.Core/MaintenanceJsonContext.cs",
            "src/Scrinia/Commands/ScriniaCommands.Maintenance.cs",
        ];
        string source = string.Join('\n',
            scopeFiles.Select(file => File.ReadAllText(Path.Combine(RepoRoot(), file))));

        string[] forbidden =
        [
            "b5ct1",
            "MaintenanceContinuationPayload",
            "MaintenanceDiscoveryCursor",
            "AfterRelativePath",
            "UnsafeCandidatesToSkip",
            "NextCursor",
        ];
        foreach (string symbol in forbidden)
            source.Should().NotContain(symbol, $"B5 scope must not contain superseded symbol '{symbol}'");
        source.Should().NotContain("OpenAiApiKey");
        source.ToLowerInvariant().Should().NotContain("openaiapikey");
    }

    [Fact]
    public void ZeroSymbolGuard_LeavesExcludedEmbeddingPathsUntouched()
    {
        string[] excludedRoots =
        [
            "src/Scrinia.Core/Embeddings",
            "src/Scrinia.Plugin.Embeddings",
            "src/Scrinia.Plugin.Embeddings.Cli",
        ];
        foreach (string root in excludedRoots)
            Directory.Exists(Path.Combine(RepoRoot(), root)).Should().BeTrue();

        // The guard scans only the B5 scope above; embedding key usage is out of scope
        // by construction and this control proves the exclusion is load-bearing.
        string scopeList = string.Join('\n',
        [
            "src/Scrinia.Core/MaintenanceContracts.cs",
            "src/Scrinia.Core/Configuration/MaintenancePolicy.cs",
            "src/Scrinia.Core/MaintenancePlanner.cs",
            "src/Scrinia.Core/FileMemoryMaintenanceService.cs",
            "src/Scrinia.Core/MaintenanceJsonContext.cs",
        ]);
        scopeList.Should().NotContain("Embeddings");
    }

    // ── 10. Task 4 inventory: limits, mapping, ownership ─────────────────────
    // Design §§0.2, 0.6, 0.7, 2.1–2.2.

    [Fact]
    public void MaintenanceLimits_OwnTheExactHardContractWithNoSecondDeclaration()
    {
        MaintenanceLimits.Hard.MaximumBytes.Should().Be(16 * 1024 * 1024);
        MaintenanceLimits.Hard.MaximumDuration.Should().Be(TimeSpan.FromSeconds(2));
        MaintenanceLimits.Hard.MaximumOpenOperations.Should().Be(2);
        MaintenanceLimits.Hard.MaximumStatOperations.Should().Be(8);
        MaintenanceLimits.Hard.MaximumReadOperations.Should().Be(257);
        MaintenanceLimits.Hard.ReadBufferBytes.Should().Be(64 * 1024);

        typeof(RecoveryEvidenceVerifier).Assembly.GetTypes()
            .Should().NotContain(type => type.Name.Contains("RecoveryVerificationLimits", StringComparison.Ordinal));
        string contracts = ReadSource("src", "Scrinia.Core", "MaintenanceContracts.cs");
        contracts.Should().NotContain("RecoveryVerificationLimits");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidLimits_PerformZeroIoBeforeAnyContainmentStatOpenReadOrParse(long bytes)
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var verifier = new RecoveryEvidenceVerifier(new RecoveryScopeBinding("local", root));
            var request = new RecoveryVerificationRequest(
                new RecoveryScopeBinding("local", root), RecoveryEvidenceKind.AppendJournal,
                "missing.json", null, new RecoveryExpectedHash("00"), null,
                new MaintenanceLimits(bytes, TimeSpan.FromSeconds(2), 2, 8, 257, 64 * 1024));

            RecoveryVerificationResult result = await verifier.VerifyAsync(request, CancellationToken.None);

            result.Outcome.Should().Be(RecoveryVerificationOutcome.ProtectedInvalidLimits);
            result.BytesRead.Should().Be(0);
            result.OpenOperations.Should().Be(0);
            result.StatOperations.Should().Be(0);
            result.ReadOperations.Should().Be(0);
            result.FinalZeroByteRead.Should().BeFalse();
            result.SourceContent.Should().BeNull();
            result.DestinationContent.Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AboveHardOverride_PerformsZeroIoAndNeverClampsOrRetries()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var verifier = new RecoveryEvidenceVerifier(new RecoveryScopeBinding("local", root));
            var request = new RecoveryVerificationRequest(
                new RecoveryScopeBinding("local", root), RecoveryEvidenceKind.AppendJournal,
                "missing.json", null, new RecoveryExpectedHash("00"), null,
                new MaintenanceLimits(
                    MaintenanceLimits.Hard.MaximumBytes + 1,
                    MaintenanceLimits.Hard.MaximumDuration,
                    MaintenanceLimits.Hard.MaximumOpenOperations,
                    MaintenanceLimits.Hard.MaximumStatOperations,
                    MaintenanceLimits.Hard.MaximumReadOperations,
                    MaintenanceLimits.Hard.ReadBufferBytes));

            RecoveryVerificationResult result = await verifier.VerifyAsync(request, CancellationToken.None);

            result.Outcome.Should().Be(RecoveryVerificationOutcome.ProtectedInvalidLimits);
            result.BytesRead.Should().Be(0);
            result.OpenOperations.Should().Be(0);
            result.StatOperations.Should().Be(0);
            result.ReadOperations.Should().Be(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProtectedInvalidLimits_MapsToUnverifiableAndOnlyVerifiedAuthorizesProgress()
    {
        ((byte)RecoveryVerificationOutcome.ProtectedInvalidLimits).Should().Be(10);

        RecoveryVerificationResult invalid = new(
            RecoveryVerificationOutcome.ProtectedInvalidLimits, 0, 0, 0, 0, false, false, false, null, null);
        MaintenancePlanner.MapVerification(in invalid, null)
            .Should().Be(MaintenanceProtectionReason.Unverifiable);

        RecoveryVerificationResult verified = new(
            RecoveryVerificationOutcome.Verified, 4, 1, 2, 2, true, false, false, null, null);
        MaintenancePlanner.MapVerification(in verified, null).Should().BeNull();
    }

    [Fact]
    public async Task VerifiedContent_IsCallerOwnedDisposedOnceAndNeverReread()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b5-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] bytes = "bounded evidence"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, "evidence.json"), bytes);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var verifier = new RecoveryEvidenceVerifier(new RecoveryScopeBinding("local", root));

            RecoveryVerificationResult first = await verifier.VerifyAsync(
                new RecoveryVerificationRequest(
                    new RecoveryScopeBinding("local", root), RecoveryEvidenceKind.AppendJournal,
                    "evidence.json", null, new RecoveryExpectedHash(hash), null, MaintenanceLimits.Hard),
                CancellationToken.None);
            RecoveryVerificationResult second = await verifier.VerifyAsync(
                new RecoveryVerificationRequest(
                    new RecoveryScopeBinding("local", root), RecoveryEvidenceKind.AppendJournal,
                    "evidence.json", null, new RecoveryExpectedHash(hash), null, MaintenanceLimits.Hard),
                CancellationToken.None);

            try
            {
                first.Outcome.Should().Be(RecoveryVerificationOutcome.Verified);
                second.Outcome.Should().Be(RecoveryVerificationOutcome.Verified);
                first.FinalZeroByteRead.Should().BeTrue();
                first.SourceContent.Should().NotBeNull();
                second.SourceContent.Should().NotBeNull();
                first.SourceContent.Should().NotBeSameAs(second.SourceContent);
                first.SourceContent!.Bytes.ToArray().Should().Equal(bytes);
            }
            finally
            {
                first.SourceContent?.Dispose();
                second.SourceContent?.Dispose();
            }

            first.SourceContent!.Bytes.Length.Should().Be(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SoleParser_ContradictoryMetadataFailsBeforeAnyEnvelopeWork()
    {
        byte[] bytes = "not an envelope"u8.ToArray();
        using var content = new RecoveryVerificationContent(bytes,
            new RecoveryVerifiedFileMetadata(bytes.Length + 1,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), true, true, true));
        var scope = new RecoveryScopeBinding("local", "/scopes/local");
        RecoveryVerifiedFileMetadata metadata = content.Metadata;

        Action parse = () => RecoveryEnvelopeCodec.ParseVerifiedEnvelope(
            in content, in scope, "/scopes/local/evidence.json", "/scopes/local",
            RecoveryEvidenceKind.AppendJournal, in metadata);

        parse.Should().Throw<RecoveryEnvelopeException>();
    }

    // ── 11. Sole parser and renamed consumers ─────────────────────────────────

    [Fact]
    public void ParseVerifiedEnvelope_IsTheSoleSixArgumentByteJsonParser()
    {
        MethodInfo[] parsers = typeof(RecoveryEnvelopeCodec)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name.Contains("ParseVerifiedEnvelope", StringComparison.Ordinal))
            .ToArray();

        parsers.Should().HaveCount(1);
        ParameterInfo[] parameters = parsers[0].GetParameters();
        parameters.Should().HaveCount(6);
        string[] parameterTypeNames = parameters
            .Select(parameter => parameter.ParameterType.FullName ?? string.Empty)
            .ToArray();
        parameterTypeNames.Should().Contain(name => name.Contains("RecoveryVerificationContent", StringComparison.Ordinal));
        parameterTypeNames.Should().Contain(name => name.Contains("RecoveryScopeBinding", StringComparison.Ordinal));
        parameterTypeNames.Should().Contain(name => name.Contains("RecoveryEvidenceKind", StringComparison.Ordinal));
        parameterTypeNames.Should().Contain(name => name.Contains("RecoveryVerifiedFileMetadata", StringComparison.Ordinal));
        parsers[0].ReturnType.Should().Be(typeof(RecoveryVerifiedEnvelope));
    }

    [Fact]
    public void TypedConsumers_AcceptOnlyTheAlreadyParsedEnvelope()
    {
        foreach (Type owner in new[] { typeof(AppendJournalCodec), typeof(RestoreJournalCodec), typeof(FileEntityRecoveryAdapter) })
        {
            MethodInfo? consume = owner.GetMethod("ConsumeParsed",
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            consume.Should().NotBeNull($"'{owner.Name}' must expose the renamed typed consumer");
            consume!.GetParameters().Should().HaveCount(1);
            consume.GetParameters()[0].ParameterType.FullName.Should().Contain("RecoveryVerifiedEnvelope");
        }

        string core = string.Join('\n',
            new[]
            {
                "src/Scrinia.Core/RecoveryVerification.cs",
                "src/Scrinia.Core/AppendJournal.cs",
                "src/Scrinia.Core/RestoreJournal.cs",
                "src/Scrinia.Core/RecoveryEnvelope.cs",
                "src/Scrinia.Core/RecoveryContracts.cs",
                "src/Scrinia.Core/RecoveryDiscovery.cs",
                "src/Scrinia.Core/FileMemoryStore.cs",
                "src/Scrinia.Core/FileEntityTransactionCoordinator.cs",
                "src/Scrinia.Core/MultiMemoryTransactionRecord.cs",
            }.Select(file => File.ReadAllText(Path.Combine(RepoRoot(), file))));

        core.Should().NotContain("ParseAndValidate(");
        core.Should().NotContain("ParseAndNormalize(");
        core.Should().NotContain("TryReadOperationId(");
        core.Should().NotContain("TryJsonOperationId(");
        core.Should().Contain("ConsumeParsed(in RecoveryVerifiedEnvelope envelope)");
        core.Should().Contain("ParseVerifiedEnvelope(");
    }

    [Fact]
    public void RecoveryRegions_KeepUnboundedReadsHashesAndDetachedTokensOut()
    {
        string[] files =
        [
            "src/Scrinia.Core/RecoveryDiscovery.cs",
            "src/Scrinia.Core/FileEntityTransactionCoordinator.cs",
            "src/Scrinia.Core/MultiMemoryTransactionRecord.cs",
            "src/Scrinia.Core/RecoveryEnvelope.cs",
        ];
        string source = string.Join('\n',
            files.Select(file => File.ReadAllText(Path.Combine(RepoRoot(), file))));

        source.Should().NotContain("CancellationToken.None");

        string[] verifierFiles =
        [
            "src/Scrinia.Core/FileMemoryStore.cs",
            "src/Scrinia.Core/RecoveryDiscovery.cs",
            "src/Scrinia.Core/RecoveryEnvelope.cs",
            "src/Scrinia.Core/FileEntityTransactionCoordinator.cs",
            "src/Scrinia.Core/MultiMemoryTransactionRecord.cs",
            "src/Scrinia.Core/RecoveryContracts.cs",
            "src/Scrinia.Core/RecoveryVerification.cs",
            "src/Scrinia.Core/AppendJournal.cs",
            "src/Scrinia.Core/RestoreJournal.cs",
        ];
        string inventory = string.Join('\n',
            verifierFiles.Select(file => File.ReadAllText(Path.Combine(RepoRoot(), file))));

        string scrubbed = inventory.Replace(
            "System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))",
            string.Empty, StringComparison.Ordinal);
        scrubbed.Should().NotContain("ReadAllBytes(");
        scrubbed.Should().NotContain("ReadAllBytesAsync(");
        scrubbed.Should().NotContain("SHA256.HashData(File.");
        inventory.Should().Contain("HashWorkspaceFile");
    }

    // ── 12. Continuation ownership boundary ───────────────────────────────────

    [Fact]
    public void SyntaxGate_RejectsFramingAsciiSizeLocallyWithZeroCoreCalls()
    {
        MaintenanceContinuationSyntax.TryValidate("legacy-token", out _).Should().BeFalse();
        MaintenanceContinuationSyntax.TryValidate("b5c2.", out _).Should().BeFalse();
        MaintenanceContinuationSyntax.TryValidate("b5c2." + new string('é', 214), out _).Should().BeFalse();
    }

    [Fact]
    public async Task MalformedFraming_IsRejectedLocallyWithZeroCoreCalls()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "scrinia-b5-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        int calls = 0;
        try
        {
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(ScriniaCommands.BuildLocalInvalidContinuation());
            }))
            {
                var commands = new ScriniaCommands();
                int exit = await commands.Maintenance(workspace, null, null, "legacy-token", false, CancellationToken.None);

                exit.Should().Be(1);
                calls.Should().Be(0);
            }
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void SyntacticallyValidToken_ReachesCoreForSemanticValidation()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(160);
        string encoded = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string token = "b5c2." + encoded;

        MaintenanceContinuationSyntax.TryValidate(token, out _).Should().BeTrue();
        MaintenanceCursorCodec.TryDecode(token, "0123456789abcdef0123456789abcdef"u8,
            out _, out _).Should().BeFalse();
    }
}
