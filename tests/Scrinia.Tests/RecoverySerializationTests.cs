using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// B2 serialization lane. Every durable recovery DTO round-trips through its source-generated
/// <c>JsonTypeInfo</c> and unsupported or malformed wire values fail closed with a typed failure —
/// never a reflection fallback or a swallowed exception.
/// </summary>
public sealed class RecoverySerializationTests
{
    [Fact]
    public void AppendEnvelopeAndBody_RoundTripThroughGeneratedContext()
    {
        RecoveryEnvelopeDocument envelope = BuildAppendEnvelope();
        RoundTripEnvelope(envelope);

        AppendRecoveryBody? body = envelope.TypedBody as AppendRecoveryBody;
        body.Should().NotBeNull();
        AppendRecoveryBody parsed = RoundTrip(body!, RecoveryEnvelopeJsonContext.Default.AppendRecoveryBody);
        parsed.Should().BeEquivalentTo(body);
    }

    [Fact]
    public void RestoreEnvelopeAndBody_RoundTripThroughGeneratedContext()
    {
        RecoveryEnvelopeDocument envelope = BuildRestoreEnvelope();
        RoundTripEnvelope(envelope);

        RestoreRecoveryBody? body = envelope.TypedBody as RestoreRecoveryBody;
        body.Should().NotBeNull();
        RestoreRecoveryBody parsed = RoundTrip(body!, RecoveryEnvelopeJsonContext.Default.RestoreRecoveryBody);
        parsed.Should().BeEquivalentTo(body);
    }

    [Fact]
    public void FileEntityEnvelopeAndBody_RoundTripThroughGeneratedContext()
    {
        RecoveryEnvelopeDocument envelope = BuildFileEntityEnvelope();
        RoundTripEnvelope(envelope);

        FileEntityRecoveryBody? body = envelope.TypedBody as FileEntityRecoveryBody;
        body.Should().NotBeNull();
        FileEntityRecoveryBody parsed = RoundTrip(body!, RecoveryEnvelopeJsonContext.Default.FileEntityRecoveryBody);
        parsed.Should().BeEquivalentTo(body);
    }

    [Fact]
    public void CommonEnvelope_CommonVersionTwoThrowsUnsupportedBeforeBodyDecode()
    {
        RecoveryEnvelopeDocument envelope = BuildAppendEnvelope();

        FluentActions.Invoking(() => RecoveryEnvelopeCodec.Serialize(envelope with { Version = 2 }))
            .Should().Throw<RecoveryEnvelopeException>()
            .Where(e => e.Failure == RecoveryEnvelopeFailure.UnsupportedVersion);
    }

    [Fact]
    public void CommonEnvelope_CommonVersionTwoIsRejectedByTheParseLane()
    {
        JsonNode node = JsonNode.Parse(Encoding.UTF8.GetString(ValidAppendEnvelopeBytes()))!.AsObject();
        node["version"] = 2;
        byte[] bytes = Encoding.UTF8.GetBytes(node.ToJsonString());

        (string evidencePath, string evidenceDirectory) = TempEvidence();
        FluentActions.Invoking(() => Parse(bytes, RecoveryTestFixtures.LocalNotesScope, evidencePath, evidenceDirectory))
            .Should().Throw<RecoveryEnvelopeException>()
            .Where(e => e.Failure == RecoveryEnvelopeFailure.UnsupportedVersion);
    }

    [Fact]
    public void CommonEnvelope_EachBodyVersionAboveSupportThrowsUnsupported()
    {
        RecoveryEnvelopeDocument[] envelopes =
            [BuildAppendEnvelope(), BuildRestoreEnvelope(), BuildFileEntityEnvelope()];

        foreach (RecoveryEnvelopeDocument envelope in envelopes)
        {
            FluentActions.Invoking(() => RecoveryEnvelopeCodec.Serialize(envelope with { BodyVersion = 2 }))
                .Should().Throw<RecoveryEnvelopeException>()
                .Where(e => e.Failure == RecoveryEnvelopeFailure.UnsupportedVersion
                    && e.Message.Contains("body version", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CommonEnvelope_MissingBodyThrowsMalformed()
    {
        JsonNode node = JsonNode.Parse(Encoding.UTF8.GetString(ValidAppendEnvelopeBytes()))!.AsObject();
        node["body"] = null;
        byte[] bytes = Encoding.UTF8.GetBytes(node.ToJsonString());

        (string evidencePath, string evidenceDirectory) = TempEvidence();
        FluentActions.Invoking(() => Parse(bytes, RecoveryTestFixtures.LocalNotesScope, evidencePath, evidenceDirectory))
            .Should().Throw<RecoveryEnvelopeException>().Which.Failure.Should().Be(RecoveryEnvelopeFailure.Malformed);
    }

    [Fact]
    public void CommonEnvelope_DuplicateIdentityMemberThrowsMalformed()
    {
        const string json = """
            {"format":"scrinia.recovery","format":"scrinia.recovery","version":1,"operation":"append","bodyVersion":1,
            "operationId":"id","scope":"local:notes","address":"/notes/note","state":"prepared",
            "createdAt":"2026-09-11T00:00:00Z","updatedAt":"2026-09-11T00:00:00Z","body":{}}
            """;

        (string evidencePath, string evidenceDirectory) = TempEvidence();
        FluentActions.Invoking(() => Parse(Encoding.UTF8.GetBytes(json), RecoveryTestFixtures.LocalNotesScope, evidencePath, evidenceDirectory))
            .Should().Throw<RecoveryEnvelopeException>().Which.Failure.Should().Be(RecoveryEnvelopeFailure.Malformed);
    }

    [Theory]
    [InlineData("operation", "bogus")]
    [InlineData("state", "BOGUS")]
    [InlineData("version", "two")]
    public void CommonEnvelope_MalformedWireValuesThrowMalformed(string member, string mutation)
    {
        JsonNode node = JsonNode.Parse(Encoding.UTF8.GetString(ValidAppendEnvelopeBytes()))!.AsObject();
        node[member] = mutation;
        byte[] bytes = Encoding.UTF8.GetBytes(node.ToJsonString());

        (string evidencePath, string evidenceDirectory) = TempEvidence();
        FluentActions.Invoking(() => Parse(bytes, RecoveryTestFixtures.LocalNotesScope, evidencePath, evidenceDirectory))
            .Should().Throw<RecoveryEnvelopeException>().Which.Failure.Should().Be(RecoveryEnvelopeFailure.Malformed);
    }

    [Fact]
    public void GeneratedContextFailsClosedOnWrongMemberTypeWithoutReflection()
    {
        string json = """
            {"format":"scrinia.recovery","version":"two","operation":"append","bodyVersion":1,
            "operationId":"id","scope":"local:notes","address":"/notes/note","state":"prepared",
            "createdAt":"2026-09-11T00:00:00Z","updatedAt":"2026-09-11T00:00:00Z","body":{}}
            """;

        FluentActions.Invoking(() => JsonSerializer.Deserialize(json, RecoveryEnvelopeJsonContext.Default.RecoveryEnvelopeDocument))
            .Should().Throw<JsonException>();
    }

    [Fact]
    public void GeneratedContextRegistersEveryDurableRecoveryDto()
    {
        RecoveryEnvelopeJsonContext context = RecoveryEnvelopeJsonContext.Default;

        context.RecoveryEnvelopeDocument.Type.Should().Be<RecoveryEnvelopeDocument>();
        context.AppendRecoveryBody.Type.Should().Be<AppendRecoveryBody>();
        context.RestoreRecoveryBody.Type.Should().Be<RestoreRecoveryBody>();
        context.FileEntityRecoveryBody.Type.Should().Be<FileEntityRecoveryBody>();
        context.RecoveryMemberSnapshot.Type.Should().Be<RecoveryMemberSnapshot>();
        context.RecoveryOperation.Type.Should().Be<RecoveryOperation>();
        context.RecoveryLifecycleState.Type.Should().Be<RecoveryLifecycleState>();
        context.RecoveryBlock.Type.Should().Be<RecoveryBlock>();
        context.RecoveryOutcome.Type.Should().Be<RecoveryOutcome>();
    }

    private static void RoundTripEnvelope(RecoveryEnvelopeDocument envelope)
    {
        byte[] viaContext = JsonSerializer.SerializeToUtf8Bytes(envelope, RecoveryEnvelopeJsonContext.Default.RecoveryEnvelopeDocument);
        byte[] viaCodec = RecoveryEnvelopeCodec.Serialize(envelope);
        viaContext.Should().Equal(viaCodec);

        RecoveryEnvelopeDocument parsed = JsonSerializer.Deserialize(viaContext, RecoveryEnvelopeJsonContext.Default.RecoveryEnvelopeDocument)
            ?? throw new InvalidDataException("The generated context produced no envelope.");
        AssertCommonEnvelopeFields(envelope, parsed);
    }

    private static void AssertCommonEnvelopeFields(RecoveryEnvelopeDocument original, RecoveryEnvelopeDocument parsed)
    {
        parsed.Format.Should().Be(original.Format);
        parsed.Version.Should().Be(original.Version);
        parsed.Operation.Should().Be(original.Operation);
        parsed.BodyVersion.Should().Be(original.BodyVersion);
        parsed.OperationId.Should().Be(original.OperationId);
        parsed.Scope.Should().Be(original.Scope);
        parsed.Address.Should().Be(original.Address);
        parsed.State.Should().Be(original.State);
        parsed.CreatedAt.Should().Be(original.CreatedAt);
        parsed.UpdatedAt.Should().Be(original.UpdatedAt);
        parsed.Body.GetRawText().Should().Be(original.Body.GetRawText());
    }

    private static T RoundTrip<T>(T value, JsonTypeInfo<T> typeInfo) where T : notnull
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        return JsonSerializer.Deserialize(bytes, typeInfo)
            ?? throw new InvalidDataException($"The generated context produced no {typeof(T).Name}.");
    }

    private static byte[] ValidAppendEnvelopeBytes() => RecoveryEnvelopeCodec.Serialize(BuildAppendEnvelope());

    private static RecoveryEnvelopeDocument BuildAppendEnvelope()
    {
        AppendJournal journal = AppendJournalCodec.ParseLegacyJournal(
            RecoveryTestFixtures.BuildLegacyAppendJournal(), RecoveryTestFixtures.LocalNotesScope, "append");
        return RecoveryEnvelopeCodec.FromAppend(journal);
    }

    private static RecoveryEnvelopeDocument BuildRestoreEnvelope()
    {
        RestoreJournal journal = RestoreJournalCodec.ParseLegacyJournal(
            RecoveryTestFixtures.BuildLegacyRestoreJournal(), RecoveryTestFixtures.LocalNotesScope, ".restore-journal.json");
        return RecoveryEnvelopeCodec.FromRestore(journal);
    }

    private static RecoveryEnvelopeDocument BuildFileEntityEnvelope()
    {
        FileEntityRecoveryBody body = new("skill", "entity",
            Snapshot("entity.md", RecoveryTestFixtures.FileEntityBody),
            Snapshot("entity.meta.json", RecoveryTestFixtures.FileEntityMetadata),
            null, null, null, null, "prepared");
        return RecoveryEnvelopeCodec.FromFileEntity(
            body, RecoveryTestFixtures.LocalNotesScope, null, RecoveryTestFixtures.OperationId,
            RecoveryLifecycleState.Prepared, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    }

    private static RecoveryMemberSnapshot Snapshot(string path, byte[] bytes) =>
        new(path, true, Convert.ToBase64String(bytes), RecoveryTestFixtures.Sha256(bytes));

    private static (string evidencePath, string evidenceDirectory) TempEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-serial-" + Guid.NewGuid().ToString("N"));
        string evidencePath = Path.Combine(root, "versions", ".append-journal.json");
        return (evidencePath, root);
    }

    private static RecoveryEnvelopeDocument Parse(byte[] bytes, string scope, string evidencePath, string evidenceDirectory)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var content = new RecoveryVerificationContent(bytes, new RecoveryVerifiedFileMetadata(bytes.Length, hash, true, true, true));
        RecoveryEvidenceKind kind = Path.GetFileName(evidencePath) == ".restore-journal.json"
            ? RecoveryEvidenceKind.RestoreJournal : RecoveryEvidenceKind.AppendJournal;
        return RecoveryEnvelopeCodec.ParseVerifiedEnvelope(in content, new RecoveryScopeBinding(scope, Path.GetFullPath(evidenceDirectory)),
            evidencePath, evidenceDirectory, kind, content.Metadata).Document;
    }
}
