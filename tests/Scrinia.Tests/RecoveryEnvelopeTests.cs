using System.Text.Json;
using System.Text;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class RecoveryEnvelopeTests
{
    [Fact]
    public void AppendEnvelope_UsesTheCommonWireShapeAndLowercaseState()
    {
        AppendJournal journal = AppendJournalCodec.ParseAndValidate(
            RecoveryTestFixtures.BuildLegacyAppendJournal(), RecoveryTestFixtures.LocalNotesScope, "append");

        byte[] bytes = RecoveryEnvelopeCodec.Serialize(RecoveryEnvelopeCodec.FromAppend(journal));
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;

        root.EnumerateObject().Select(property => property.Name).Should().Equal(
            "format", "version", "operation", "bodyVersion", "operationId", "scope", "address", "state",
            "createdAt", "updatedAt", "body");
        root.GetProperty("format").GetString().Should().Be("scrinia.recovery");
        root.GetProperty("operation").GetString().Should().Be("append");
        root.GetProperty("state").GetString().Should().Be("prepared");
        root.GetProperty("bodyVersion").GetInt32().Should().BePositive();
        root.GetProperty("updatedAt").GetDateTimeOffset().Should().BeOnOrAfter(root.GetProperty("createdAt").GetDateTimeOffset());
    }

    [Fact]
    public void RestoreEnvelope_OmitsAddressAndRoundTripsThroughGeneratedMetadata()
    {
        RestoreJournal journal = RestoreJournalCodec.ParseAndValidate(
            RecoveryTestFixtures.BuildLegacyRestoreJournal("local:notes", "completed"),
            RecoveryTestFixtures.LocalNotesScope, "restore");

        RecoveryEnvelopeDocument envelope = RecoveryEnvelopeCodec.FromRestore(journal);
        RecoveryEnvelopeDocument parsed = RecoveryEnvelopeCodec.Parse(
            RecoveryEnvelopeCodec.Serialize(envelope), RecoveryTestFixtures.LocalNotesScope,
            RecoveryTestFixtures.RestoreJournalPath("scope"), "scope");

        parsed.Operation.Should().Be(RecoveryOperation.Restore);
        parsed.Address.Should().BeNull();
        parsed.State.Should().Be(RecoveryLifecycleState.Committed);
        parsed.Body.GetProperty("entries").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void LegacyAdaptersNormalizeInMemoryWithoutChangingTheLegacyPayload()
    {
        byte[] appendBytes = RecoveryTestFixtures.BuildLegacyAppendJournal();
        byte[] restoreBytes = RecoveryTestFixtures.BuildLegacyRestoreJournal();

        RecoveryEnvelopeDocument append = new AppendRecoveryAdapter().NormalizeLegacy(
            appendBytes, RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.AppendJournalPath("scope"),
            "scope");
        RecoveryEnvelopeDocument restore = new RestoreRecoveryAdapter().NormalizeLegacy(
            restoreBytes, RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.RestoreJournalPath("scope"), "scope");

        append.IsLegacy.Should().BeTrue();
        restore.IsLegacy.Should().BeTrue();
        append.Operation.Should().Be(RecoveryOperation.Append);
        restore.Operation.Should().Be(RecoveryOperation.Restore);
        appendBytes.Should().Equal(RecoveryTestFixtures.BuildLegacyAppendJournal());
        restoreBytes.Should().Equal(RecoveryTestFixtures.BuildLegacyRestoreJournal());
    }

    [Theory]
    [InlineData("operation", "bogus")]
    [InlineData("state", "COMMITTED")]
    public void InvalidEnvelopeDiscriminatorsAreRejectedBeforeBodyUse(string member, string value)
    {
        string json = $$"""
            {"format":"scrinia.recovery","version":1,"operation":"append","bodyVersion":1,"operationId":"id","scope":"local:notes","address":"/notes/note","state":"prepared","createdAt":"2026-09-11T00:00:00Z","updatedAt":"2026-09-11T00:00:00Z","body":{ "oldArtifact": null },"{{member}}":"{{value}}"}
            """;

        RecoveryEnvelopeException exception = FluentActions.Invoking(() => RecoveryEnvelopeCodec.Parse(
                System.Text.Encoding.UTF8.GetBytes(json), RecoveryTestFixtures.LocalNotesScope, "evidence", "."))
            .Should().Throw<RecoveryEnvelopeException>().Which;

        exception.Failure.Should().Be(RecoveryEnvelopeFailure.Malformed);
    }

    [Fact]
    public void DuplicateIdentityMemberIsClassifiedAsMalformed()
    {
        const string json = "{\"format\":\"scrinia.recovery\",\"format\":\"scrinia.recovery\"}";

        RecoveryEnvelopeException exception = FluentActions.Invoking(() => RecoveryEnvelopeCodec.Parse(
                System.Text.Encoding.UTF8.GetBytes(json), RecoveryTestFixtures.LocalNotesScope, "evidence", "."))
            .Should().Throw<RecoveryEnvelopeException>().Which;

        exception.Failure.Should().Be(RecoveryEnvelopeFailure.Malformed);
    }

    [Fact]
    public void AdapterRejectsAValidEnvelopeForAnotherOperation()
    {
        RestoreJournal restore = RestoreJournalCodec.ParseAndValidate(
            RecoveryTestFixtures.BuildLegacyRestoreJournal(), RecoveryTestFixtures.LocalNotesScope, ".restore-journal.json");
        byte[] bytes = RecoveryEnvelopeCodec.Serialize(RecoveryEnvelopeCodec.FromRestore(restore));

        FluentActions.Invoking(() => ((IRecoveryAdapter)new AppendRecoveryAdapter()).ParseAndNormalize(
                bytes, RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.AppendJournalPath("scope"), "scope"))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void LegacyFileEntityNormalizationDoesNotReadTheEvidenceFile()
    {
        string evidencePath = Path.Combine(Path.GetTempPath(), ".file-entity-" + RecoveryTestFixtures.OperationId + ".recovery");
        RecoveryEnvelopeDocument envelope = new FileEntityRecoveryAdapter().NormalizeLegacy(
            RecoveryTestFixtures.BuildLegacyFileEntityRecovery(), RecoveryTestFixtures.LocalNotesScope, evidencePath,
            Path.GetDirectoryName(evidencePath)!);

        envelope.IsLegacy.Should().BeTrue();
        envelope.CreatedAt.Should().Be(DateTimeOffset.UnixEpoch);
        envelope.UpdatedAt.Should().Be(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void AdapterRejectsAValidEnvelopeForTheWrongSubject()
    {
        AppendJournal journal = AppendJournalCodec.ParseAndValidate(
            RecoveryTestFixtures.BuildLegacyAppendJournal(), RecoveryTestFixtures.LocalNotesScope,
            RecoveryTestFixtures.AppendJournalPath("scope"));
        RecoveryEnvelopeDocument envelope = RecoveryEnvelopeCodec.FromAppend(journal);

        FluentActions.Invoking(() => ((IRecoveryAdapter)new AppendRecoveryAdapter()).ValidateForMutation(
                envelope, RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.AppendJournalPath("scope"), "/wrong", "scope"))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("committed")]
    public void FileEntityAdapterAcceptsPublisherShapedAbsolutePaths(string state)
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-absolute-recovery-" + Guid.NewGuid().ToString("N"));
        string entityDirectory = Path.Combine(root, "skill");
        string versionsDirectory = Path.Combine(entityDirectory, "versions");
        string evidencePath = Path.Combine(entityDirectory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery");
        string bodyPath = Path.Combine(entityDirectory, "entity.md");
        string metaPath = Path.Combine(entityDirectory, "entity.meta.json");
        string archiveBodyPath = Path.Combine(versionsDirectory, "entity_20260911-000000000.md");
        string archiveMetaPath = Path.ChangeExtension(archiveBodyPath, ".meta.json");

        try
        {
            FileEntityRecoveryBody body = PublisherShapedBody(
                state, bodyPath, metaPath, archiveBodyPath, archiveMetaPath);
            RecoveryEnvelopeDocument envelope = RecoveryEnvelopeCodec.FromFileEntity(
                body, RecoveryTestFixtures.LocalNotesScope, null, RecoveryTestFixtures.OperationId,
                state == "committed" ? RecoveryLifecycleState.Committed : RecoveryLifecycleState.Prepared,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

            RecoveryEnvelopeDocument validated = ((IRecoveryAdapter)new FileEntityRecoveryAdapter()).ValidateForMutation(
                envelope, RecoveryTestFixtures.LocalNotesScope, evidencePath, "entity", entityDirectory);

            FileEntityRecoveryBody validatedBody = (FileEntityRecoveryBody)validated.TypedBody!;
            validatedBody.OldBody.Path.Should().Be(Path.GetFullPath(bodyPath));
            validatedBody.OldMeta.Path.Should().Be(Path.GetFullPath(metaPath));
            validatedBody.ArchiveBodyPath.Should().Be(Path.GetFullPath(archiveBodyPath));
            validatedBody.ArchiveMetaPath.Should().Be(Path.GetFullPath(archiveMetaPath));
            validatedBody.CommittedBody?.Path.Should().Be(Path.GetFullPath(bodyPath));
            validatedBody.CommittedMeta?.Path.Should().Be(Path.GetFullPath(metaPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FileEntityAdapterRejectsAbsolutePathOutsideEntityDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-absolute-recovery-" + Guid.NewGuid().ToString("N"));
        string entityDirectory = Path.Combine(root, "skill");
        string evidencePath = Path.Combine(entityDirectory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery");
        string outsidePath = Path.Combine(root, "outside.md");

        FileEntityRecoveryBody body = PublisherShapedBody(
            "prepared", outsidePath, Path.Combine(entityDirectory, "entity.meta.json"), null, null);
        RecoveryEnvelopeDocument envelope = RecoveryEnvelopeCodec.FromFileEntity(
            body, RecoveryTestFixtures.LocalNotesScope, null, RecoveryTestFixtures.OperationId,
            RecoveryLifecycleState.Prepared, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        FluentActions.Invoking(() => ((IRecoveryAdapter)new FileEntityRecoveryAdapter()).ValidateForMutation(
                envelope, RecoveryTestFixtures.LocalNotesScope, evidencePath, "entity", entityDirectory))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void FileEntityAdapterRejectsSwappedPreparedOldMemberRoles()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-swapped-prepared-" + Guid.NewGuid().ToString("N"));
        string entityDirectory = Path.Combine(root, "skill");
        string evidencePath = Path.Combine(entityDirectory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery");
        FileEntityRecoveryBody body = new("skill", "entity",
            Snapshot("entity.meta.json", RecoveryTestFixtures.FileEntityBody),
            Snapshot("entity.md", RecoveryTestFixtures.FileEntityMetadata), null, null, null, null, "prepared");

        FluentActions.Invoking(() => ValidateFileEntity(body, evidencePath, entityDirectory, "entity"))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void FileEntityAdapterRejectsSwappedCommittedMemberRoles()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-swapped-committed-" + Guid.NewGuid().ToString("N"));
        string entityDirectory = Path.Combine(root, "skill");
        string evidencePath = Path.Combine(entityDirectory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery");
        RecoveryMemberSnapshot oldBody = Snapshot(Path.Combine(entityDirectory, "entity.md"), RecoveryTestFixtures.FileEntityBody);
        RecoveryMemberSnapshot oldMeta = Snapshot(Path.Combine(entityDirectory, "entity.meta.json"), RecoveryTestFixtures.FileEntityMetadata);
        FileEntityRecoveryBody body = new("skill", "entity", oldBody, oldMeta, null, null,
            oldMeta, oldBody, "committed");

        FluentActions.Invoking(() => ValidateFileEntity(body, evidencePath, entityDirectory, "entity"))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void FileEntityAdapterRejectsSwappedArchiveMemberRoles()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-swapped-archive-" + Guid.NewGuid().ToString("N"));
        string entityDirectory = Path.Combine(root, "skill");
        string evidencePath = Path.Combine(entityDirectory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery");
        string archiveBody = Path.Combine(entityDirectory, "versions", "entity_20260911-000000000.md");
        string archiveMeta = Path.ChangeExtension(archiveBody, ".meta.json");
        FileEntityRecoveryBody body = PublisherShapedBody("prepared",
            Path.Combine(entityDirectory, "entity.md"), Path.Combine(entityDirectory, "entity.meta.json"),
            archiveMeta, archiveBody);

        FluentActions.Invoking(() => ValidateFileEntity(body, evidencePath, entityDirectory, "entity"))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void MetadataOnlyEvidenceRequiresOneCanonicalSidecarPathAndNoBodySnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-metadata-shape-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "skills");
        string evidence = Path.Combine(directory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery");
        string meta = Path.Combine(directory, "entity.meta.json");
        RecoveryMemberSnapshot old = Snapshot(meta, true, RecoveryTestFixtures.FileEntityMetadata);
        RecoveryMemberSnapshot committed = Snapshot(meta, true, RecoveryTestFixtures.NewSidecar);
        FileEntityRecoveryBody validBody = new("sidecar", "entity", old, old, null, null,
            null, committed, "committed");

        ValidateFileEntity(validBody, evidence, directory, "sidecar/entity").TypedBody.Should().NotBeNull();

        FileEntityRecoveryBody invalidBody = validBody with
        {
            OldMeta = Snapshot(Path.Combine(directory, "other.meta.json"), true, RecoveryTestFixtures.FileEntityMetadata)
        };
        FluentActions.Invoking(() => ValidateFileEntity(invalidBody, evidence, directory, "sidecar/entity"))
            .Should().Throw<RecoveryEnvelopeException>();

        invalidBody = validBody with { CommittedBody = committed };
        FluentActions.Invoking(() => ValidateFileEntity(invalidBody, evidence, directory, "sidecar/entity"))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void PreparedEvidenceRejectsCommittedMetadataForMetadataOnlyTransactions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "scrinia-prepared-sidecar-" + Guid.NewGuid().ToString("N"), "skills");
        string meta = Path.Combine(directory, "entity.meta.json");
        FileEntityRecoveryBody body = new("sidecar", "entity", Snapshot(meta, true, RecoveryTestFixtures.FileEntityMetadata),
            Snapshot(meta, true, RecoveryTestFixtures.FileEntityMetadata), null, null, null,
            Snapshot(meta, true, RecoveryTestFixtures.NewSidecar), "prepared");

        FluentActions.Invoking(() => ValidateFileEntity(body,
                Path.Combine(directory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery"), directory, "sidecar/entity"))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void LegacyFileEntityNormalizationRejectsCorruptedExistingSnapshotHash()
    {
        byte[] bytes = RecoveryTestFixtures.BuildLegacyFileEntityRecovery();
        bytes[^1] ^= 0x01;

        FluentActions.Invoking(() => new FileEntityRecoveryAdapter().NormalizeLegacy(
                bytes, RecoveryTestFixtures.LocalNotesScope,
                Path.Combine(Path.GetTempPath(), $".file-entity-{RecoveryTestFixtures.OperationId}.recovery"),
                Path.GetTempPath()))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void MetadataOnlyValidationResolvesRelativeAndAbsoluteEquivalentPaths()
    {
        string directory = Path.Combine(Path.GetTempPath(), "scrinia-relative-sidecar-" + Guid.NewGuid().ToString("N"), "skills");
        string meta = Path.Combine(directory, "entity.meta.json");
        FileEntityRecoveryBody body = new("sidecar", "entity",
            Snapshot("entity.meta.json", true, RecoveryTestFixtures.FileEntityMetadata),
            Snapshot(meta, true, RecoveryTestFixtures.FileEntityMetadata), null, null, null,
            Snapshot("entity.meta.json", true, RecoveryTestFixtures.NewSidecar), "committed");

        FileEntityRecoveryBody validated = (FileEntityRecoveryBody)ValidateFileEntity(body,
            Path.Combine(directory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery"), directory,
            "sidecar/entity").TypedBody!;
        validated.OldBody.Path.Should().Be(Path.GetFullPath(meta));
        validated.CommittedMeta!.Path.Should().Be(Path.GetFullPath(meta));
    }

    [Fact]
    public void OrdinaryCommittedEvidenceRequiresBothMembers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "scrinia-committed-shape-" + Guid.NewGuid().ToString("N"), "skill");
        string evidence = Path.Combine(directory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery");
        RecoveryMemberSnapshot oldBody = Snapshot(Path.Combine(directory, "entity.md"), true, RecoveryTestFixtures.FileEntityBody);
        RecoveryMemberSnapshot oldMeta = Snapshot(Path.Combine(directory, "entity.meta.json"), true, RecoveryTestFixtures.FileEntityMetadata);
        FileEntityRecoveryBody body = new("skill", "entity", oldBody, oldMeta, null, null, oldBody, null, "committed");

        FluentActions.Invoking(() => ValidateFileEntity(body, evidence, directory, "skill/entity"))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void MissingPayloadOnAbsentMemberNormalizesToEmptyBytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "scrinia-absent-member-" + Guid.NewGuid().ToString("N"), "skill");
        string evidence = Path.Combine(directory, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery");
        string emptyHash = RecoveryTestFixtures.Sha256([]);
        var absent = new RecoveryMemberSnapshot(Path.Combine(directory, "entity.md"), false, null!, emptyHash);
        var meta = Snapshot(Path.Combine(directory, "entity.meta.json"), true, RecoveryTestFixtures.FileEntityMetadata);
        var body = new FileEntityRecoveryBody("skill", "entity", absent, meta, null, null, null, null, "prepared");

        FileEntityRecoveryBody validated = (FileEntityRecoveryBody)ValidateFileEntity(body, evidence, directory, "skill/entity").TypedBody!;
        RecoveryMemberSnapshot normalized = validated.OldBody;
        normalized.Payload.Should().BeNull();
    }

    private static RecoveryEnvelopeDocument ValidateFileEntity(FileEntityRecoveryBody body, string evidencePath,
        string entityDirectory, string subject) =>
        ((IRecoveryAdapter)new FileEntityRecoveryAdapter()).ValidateForMutation(
            RecoveryEnvelopeCodec.FromFileEntity(body, RecoveryTestFixtures.LocalNotesScope, null,
                RecoveryTestFixtures.OperationId,
                body.State == "committed" ? RecoveryLifecycleState.Committed : RecoveryLifecycleState.Prepared,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            RecoveryTestFixtures.LocalNotesScope, evidencePath, subject, entityDirectory);

    private static FileEntityRecoveryBody PublisherShapedBody(
        string state, string bodyPath, string metaPath, string? archiveBodyPath, string? archiveMetaPath)
    {
        RecoveryMemberSnapshot oldBody = Snapshot(bodyPath, RecoveryTestFixtures.FileEntityBody);
        RecoveryMemberSnapshot oldMeta = Snapshot(metaPath, RecoveryTestFixtures.FileEntityMetadata);
        RecoveryMemberSnapshot? committedBody = state == "committed" ? oldBody : null;
        RecoveryMemberSnapshot? committedMeta = state == "committed" ? oldMeta : null;
        return new FileEntityRecoveryBody("skill", "entity", oldBody, oldMeta,
            archiveBodyPath, archiveMetaPath, committedBody, committedMeta, state);
    }

    private static RecoveryMemberSnapshot Snapshot(string path, byte[] bytes) =>
        new(path, true, Convert.ToBase64String(bytes), RecoveryTestFixtures.Sha256(bytes));

    private static RecoveryMemberSnapshot Snapshot(string path, bool existed, byte[] bytes) =>
        new(path, existed, Convert.ToBase64String(bytes), RecoveryTestFixtures.Sha256(bytes));

    [Fact]
    public void JsonEnvelopeRequiresItsPreservedFilenameAndFileEntityOperationId()
    {
        RestoreJournal restore = RestoreJournalCodec.ParseAndValidate(
            RecoveryTestFixtures.BuildLegacyRestoreJournal(), RecoveryTestFixtures.LocalNotesScope, ".restore-journal.json");
        byte[] restoreBytes = RecoveryEnvelopeCodec.Serialize(RecoveryEnvelopeCodec.FromRestore(restore));
        FluentActions.Invoking(() => RecoveryEnvelopeCodec.Parse(
                restoreBytes, RecoveryTestFixtures.LocalNotesScope, "wrong-name.json", "."))
            .Should().Throw<RecoveryEnvelopeException>();

        var snapshot = new RecoveryMemberSnapshot("entity.md", true,
            Convert.ToBase64String(RecoveryTestFixtures.FileEntityBody), RecoveryTestFixtures.Sha256(RecoveryTestFixtures.FileEntityBody));
        var meta = new RecoveryMemberSnapshot("entity.meta.json", true,
            Convert.ToBase64String(RecoveryTestFixtures.FileEntityMetadata), RecoveryTestFixtures.Sha256(RecoveryTestFixtures.FileEntityMetadata));
        var body = new FileEntityRecoveryBody("skill", "entity", snapshot, meta, null, null, null, null, "prepared");
        byte[] fileBytes = RecoveryEnvelopeCodec.Serialize(RecoveryEnvelopeCodec.FromFileEntity(
            body, RecoveryTestFixtures.LocalNotesScope, null, RecoveryTestFixtures.OperationId,
            RecoveryLifecycleState.Prepared, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        FluentActions.Invoking(() => RecoveryEnvelopeCodec.Parse(fileBytes, RecoveryTestFixtures.LocalNotesScope,
                ".file-entity-OTHER.recovery", "."))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void MissingNestedRequiredMembersAreRejectedByTypedDispatch()
    {
        string appendJson = Encoding.UTF8.GetString(RecoveryEnvelopeCodec.Serialize(RecoveryEnvelopeCodec.FromAppend(
            AppendJournalCodec.ParseAndValidate(RecoveryTestFixtures.BuildLegacyAppendJournal(),
                RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.AppendJournalPath("scope")))));
        string restoreJson = Encoding.UTF8.GetString(RecoveryEnvelopeCodec.Serialize(RecoveryEnvelopeCodec.FromRestore(
            RestoreJournalCodec.ParseAndValidate(RecoveryTestFixtures.BuildLegacyRestoreJournal(),
                RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.RestoreJournalPath("scope")))));

        FluentActions.Invoking(() => RecoveryEnvelopeCodec.Parse(
                Encoding.UTF8.GetBytes(appendJson.Replace("\"path\":\"notes.nmp2\"", "\"path\":null", StringComparison.Ordinal)),
                RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.AppendJournalPath("scope"), "scope"))
            .Should().Throw<RecoveryEnvelopeException>();
        FluentActions.Invoking(() => RecoveryEnvelopeCodec.Parse(
                Encoding.UTF8.GetBytes(restoreJson.Replace("\"name\":\"note\"", "\"name\":null", StringComparison.Ordinal)),
                RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.RestoreJournalPath("scope"), "scope"))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void AdaptersRejectCorrectlyNamedEvidenceFromTheWrongDirectory()
    {
        string scopeDirectory = Path.Combine(Path.GetTempPath(), "recovery-scope");
        string versionsDirectory = Path.Combine(scopeDirectory, "versions");
        string entityDirectory = Path.Combine(Path.GetTempPath(), "skills", "entity");

        byte[] appendBytes = RecoveryEnvelopeCodec.Serialize(RecoveryEnvelopeCodec.FromAppend(
            AppendJournalCodec.ParseAndValidate(RecoveryTestFixtures.BuildLegacyAppendJournal(),
                RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.AppendJournalPath("scope"))));
        byte[] restoreBytes = RecoveryEnvelopeCodec.Serialize(RecoveryEnvelopeCodec.FromRestore(
            RestoreJournalCodec.ParseAndValidate(RecoveryTestFixtures.BuildLegacyRestoreJournal(),
                RecoveryTestFixtures.LocalNotesScope, RecoveryTestFixtures.RestoreJournalPath("scope"))));
        byte[] fileEntityBytes = RecoveryTestFixtures.BuildLegacyFileEntityRecovery();

        FluentActions.Invoking(() => ((IRecoveryAdapter)new AppendRecoveryAdapter()).ParseAndNormalize(
                appendBytes, RecoveryTestFixtures.LocalNotesScope,
                Path.Combine(scopeDirectory, AppendJournal.FileName), versionsDirectory))
            .Should().Throw<RecoveryEnvelopeException>();
        FluentActions.Invoking(() => ((IRecoveryAdapter)new RestoreRecoveryAdapter()).ParseAndNormalize(
                restoreBytes, RecoveryTestFixtures.LocalNotesScope,
                Path.Combine(versionsDirectory, ".restore-journal.json"), scopeDirectory))
            .Should().Throw<RecoveryEnvelopeException>();
        FluentActions.Invoking(() => ((IRecoveryAdapter)new FileEntityRecoveryAdapter()).ParseAndNormalize(
                fileEntityBytes, RecoveryTestFixtures.LocalNotesScope,
                Path.Combine(Path.GetDirectoryName(entityDirectory)!, $".file-entity-{RecoveryTestFixtures.OperationId}.recovery"),
                entityDirectory))
            .Should().Throw<RecoveryEnvelopeException>();
    }

    [Fact]
    public void RecoveryOperation_HasExactlyTheSupportedOperations() =>
        Enum.GetValues<RecoveryOperation>().Should().Equal(
            RecoveryOperation.Restore,
            RecoveryOperation.Append,
            RecoveryOperation.FileEntity);

    [Fact]
    public void RecoveryLifecycleState_HasExactlyTheSupportedStates() =>
        Enum.GetValues<RecoveryLifecycleState>().Should().Equal(
            RecoveryLifecycleState.Prepared,
            RecoveryLifecycleState.Applying,
            RecoveryLifecycleState.Committed,
            RecoveryLifecycleState.CleanupPending,
            RecoveryLifecycleState.Cleaned,
            RecoveryLifecycleState.RecoveryRequired,
            RecoveryLifecycleState.RolledBack);

    [Fact]
    public void RecoveryBlock_RequiresItsScopeEvidencePathAndReason()
    {
        FluentActions.Invoking(() => new RecoveryBlock(
                " ", "evidence", RecoveryOperation.Append, "unknown",
                RecoveryBlockReason.Malformed, "detail"))
            .Should().Throw<ArgumentException>();

        FluentActions.Invoking(() => new RecoveryBlock(
                "local:notes", " ", RecoveryOperation.Append, "unknown",
                RecoveryBlockReason.Malformed, "detail"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecoveryOutcome_RequiresAValidatedOperationId()
    {
        FluentActions.Invoking(() => new RecoveryOutcome(
                false, false, false, "local:notes", "evidence", " ", null, null))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DurableFormats_AddsOnlyTheCommonRecoveryEnvelopeContract()
    {
        DurableFormats.All.Should().ContainSingle(contract => contract.Name == "recovery-envelope");
        DurableFormats.RecoveryEnvelope.Should().Be(
            new FormatContract("recovery-envelope", 1, null,
                FormatDisposition.Refuse, FormatDisposition.Refuse));
        DurableFormats.All.Select(contract => contract.Name)
            .Should().ContainInOrder("nmp2-body", "restore-journal", "append-journal", "recovery-envelope");
    }

    [Fact]
    public void LegacyFixtures_PreserveEachEvidenceLocationAndBytes()
    {
        RecoveryTestFixtures.BuildLegacyRestoreJournal().Should().NotBeEmpty();
        RecoveryTestFixtures.BuildLegacyAppendJournal().Should().NotBeEmpty();
        RecoveryTestFixtures.BuildLegacyFileEntityRecovery().Should().NotBeEmpty();

        RecoveryTestFixtures.RestoreJournalPath("scope")
            .Should().Be(Path.Combine("scope", ".restore-journal.json"));
        RecoveryTestFixtures.AppendJournalPath("scope")
            .Should().Be(Path.Combine("scope", "versions", ".append-journal.json"));
        RecoveryTestFixtures.FileEntityRecoveryPath("entity")
            .Should().Be(Path.Combine("entity", ".file-entity-01JOURNAL000000000000000001.recovery"));
        RecoveryTestFixtures.Sha256(RecoveryTestFixtures.FileEntityBody)
            .Should().Be("6c0143617a9d0759f78edaec176588f3e7a92ff37d0c2ae3fd39213c77007571");
        RecoveryTestFixtures.Sha256(RecoveryTestFixtures.FileEntityMetadata)
            .Should().Be("ae4f394f95259cf29c1ca17420adeda255ec94cab289f31f3daca9d1020d21e1");
    }

    [Fact]
    public void LegacyAppendFixture_IsAcceptedByTheProductionReaderWithExactEvidence()
    {
        byte[] bytes = RecoveryTestFixtures.BuildLegacyAppendJournal();

        AppendJournal journal = AppendJournalCodec.ParseAndValidate(
            bytes, RecoveryTestFixtures.LocalNotesScope,
            RecoveryTestFixtures.AppendJournalPath("scope"));

        journal.OperationId.Should().Be(RecoveryTestFixtures.OperationId);
        journal.Address.Should().Be(RecoveryTestFixtures.CanonicalAddress);
        journal.Scope.Should().Be(RecoveryTestFixtures.LocalNotesScope);
        journal.OldArtifact.Payload.Should().Be(Convert.ToBase64String(RecoveryTestFixtures.OldArtifact));
        journal.OldArtifact.Sha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.OldArtifact));
        journal.NewArtifact.Payload.Should().Be(Convert.ToBase64String(RecoveryTestFixtures.NewArtifact));
        journal.NewArtifact.Sha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.NewArtifact));
        journal.OldSidecar.Sha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.OldSidecar));
        journal.NewSidecar.Sha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.NewSidecar));
    }

    [Fact]
    public void LegacyRestoreFixtures_AreAcceptedByTheProductionReaderWithExactEvidence()
    {
        foreach (string state in new[] { "prepared", "completed" })
        {
            byte[] bytes = RecoveryTestFixtures.BuildLegacyRestoreJournal(state: state);
            RestoreJournal journal = RestoreJournalCodec.ParseAndValidate(
                bytes, RecoveryTestFixtures.LocalNotesScope,
                RecoveryTestFixtures.RestoreJournalPath("scope"));

            journal.TransactionId.Should().Be(RecoveryTestFixtures.RestoreTransactionId);
            journal.Scope.Should().Be(RecoveryTestFixtures.LocalNotesScope);
            journal.Entries.Should().ContainSingle();
            RestoreJournalEntry entry = journal.Entries[0];
            entry.BodyBase64.Should().Be(Convert.ToBase64String(RecoveryTestFixtures.NewArtifact));
            entry.BodySha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.NewArtifact));
            entry.SidecarBase64.Should().Be(Convert.ToBase64String(RecoveryTestFixtures.NewSidecar));
            entry.SidecarSha256.Should().Be(RecoveryTestFixtures.Sha256(RecoveryTestFixtures.NewSidecar));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyFileEntityFixtures_AreReadByTheProductionCoordinator(bool committed)
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-recovery-fixture-" + Guid.NewGuid().ToString("N"));
        string entityDirectory = Path.Combine(root, "entity");
        string bodyPath = Path.Combine(entityDirectory, "entity.md");
        string metaPath = Path.Combine(entityDirectory, "entity.meta.json");
        string versionsDirectory = Path.Combine(entityDirectory, "versions");
        string evidencePath = RecoveryTestFixtures.FileEntityRecoveryPath(entityDirectory);
        Directory.CreateDirectory(entityDirectory);
        File.WriteAllBytes(bodyPath, RecoveryTestFixtures.FileEntityBody);
        File.WriteAllBytes(metaPath, RecoveryTestFixtures.FileEntityMetadata);
        File.WriteAllBytes(evidencePath, RecoveryTestFixtures.BuildLegacyFileEntityRecovery(
            bodyPath, metaPath, committed));

        try
        {
            FileEntityMutationResult result = await new FileEntityTransactionCoordinator(root).ExecuteAsync(
                new FileEntityMutation("skill", "entity", bodyPath, metaPath, versionsDirectory,
                    RecoveryTestFixtures.NewArtifact, RecoveryTestFixtures.NewSidecar,
                    deleteBody: false, deleteMeta: false));

            result.Recovery.Should().BeNull();
            File.Exists(evidencePath).Should().BeFalse();
            File.ReadAllBytes(bodyPath).Should().Equal(RecoveryTestFixtures.NewArtifact);
            File.ReadAllBytes(metaPath).Should().Equal(RecoveryTestFixtures.NewSidecar);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
