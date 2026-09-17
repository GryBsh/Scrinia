using System.Security.Cryptography;
using Scrinia.Core;
using Scrinia.Core.Formats;

namespace Scrinia.Tests;

internal static class RecoveryLegacyTestParser
{
    internal static AppendJournal ParseAppend(ReadOnlySpan<byte> bytes, string scope, string path,
        string? expectedStoreDirectory = null)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.Combine(Path.GetTempPath(), "scrinia-test-envelope-" + Guid.NewGuid().ToString("N"));
        string evidencePath = Path.Combine(directory, "versions", AppendJournal.FileName);
        using var content = CreateContent(bytes);
        RecoveryVerifiedFileMetadata metadata = content.Metadata;
        RecoveryScopeBinding binding = new(scope, directory);
        RecoveryVerifiedEnvelope envelope;
        try
        {
            envelope = RecoveryEnvelopeCodec.ParseVerifiedEnvelope(in content, in binding,
                evidencePath, directory, RecoveryEvidenceKind.AppendJournal, in metadata);
        }
        catch (RecoveryEnvelopeException exception) when (exception.InnerException is AppendJournalException legacyException)
        {
            throw legacyException;
        }
        catch (RecoveryEnvelopeException exception)
        {
            AppendJournalFailure failure = exception.Failure == RecoveryEnvelopeFailure.UnsupportedVersion
                ? AppendJournalFailure.UnsupportedVersion
                : AppendJournalFailure.Malformed;
            throw new AppendJournalException(failure,
                exception.Message, exception);
        }
        AppendRecoveryBody body = (AppendRecoveryBody)envelope.Document.TypedBody!;
        var journal = new AppendJournal("scrinia.append-journal", DurableFormats.AppendJournal.Current,
            envelope.Document.OperationId, envelope.Document.Address!, envelope.Document.Scope, body.State, body.IsCreate,
            body.OldArtifact, body.OldSidecar, body.NewArtifact, body.NewSidecar, body.ArchiveArtifactPath,
            body.ArchiveSidecarPath, body.ArchiveArtifactStatus, body.ArchiveSidecarStatus, body.PruneCandidates,
            envelope.Document.CreatedAt, body.CommittedHash, body.Detail);
        if (expectedStoreDirectory is not null)
        {
            string storeDirectory = Path.GetFileName(expectedStoreDirectory) == "versions"
                ? Path.GetDirectoryName(expectedStoreDirectory)!
                : expectedStoreDirectory;
            global::Scrinia.Core.AppendJournalCodec.ValidateLegacyParsed(journal, scope, fullPath, storeDirectory);
        }
        return journal;
    }

    internal static RestoreJournal ParseRestore(ReadOnlySpan<byte> bytes, string scope, string path)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.Combine(Path.GetTempPath(), "scrinia-test-envelope-" + Guid.NewGuid().ToString("N"));
        string evidencePath = Path.Combine(directory, ".restore-journal.json");
        using var content = CreateContent(bytes);
        RecoveryVerifiedFileMetadata metadata = content.Metadata;
        RecoveryScopeBinding binding = new(scope, directory);
        RecoveryVerifiedEnvelope envelope;
        try
        {
            envelope = RecoveryEnvelopeCodec.ParseVerifiedEnvelope(in content, in binding,
                evidencePath, directory, RecoveryEvidenceKind.RestoreJournal, in metadata);
        }
        catch (RecoveryEnvelopeException exception) when (exception.InnerException is RestoreRecoveryException legacyException)
        {
            throw legacyException;
        }
        catch (RecoveryEnvelopeException exception)
        {
            throw new RestoreRecoveryException(scope, fullPath,
                RestoreRecoveryFailure.MalformedOrIntegrityFailed, exception.Message, exception);
        }
        RestoreRecoveryBody body = (RestoreRecoveryBody)envelope.Document.TypedBody!;
        var journal = new RestoreJournal("scrinia.restore-journal", DurableFormats.RestoreJournal.Current,
            body.State, body.TransactionId, envelope.Document.Scope, body.Entries);
        return journal;
    }

    internal static RecoveryEnvelopeDocument ParseFileEntity(ReadOnlySpan<byte> bytes, string scope, string path,
        string directory)
    {
        using var content = CreateContent(bytes);
        RecoveryScopeBinding binding = new(scope, directory);
        RecoveryVerifiedFileMetadata metadata = content.Metadata;
        return RecoveryEnvelopeCodec.ParseVerifiedEnvelope(in content, in binding, path, directory,
            RecoveryEvidenceKind.FileEntityJournal, in metadata).Document;
    }

    private static RecoveryVerificationContent CreateContent(ReadOnlySpan<byte> bytes) =>
        new(bytes.ToArray(), new RecoveryVerifiedFileMetadata(bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), true, true, true));
}

internal static class AppendJournalCodecTestFacade
{
    internal static AppendJournal CreatePrepared(AppendTransaction transaction) => global::Scrinia.Core.AppendJournalCodec.CreatePrepared(transaction);
    internal static byte[] Serialize(AppendJournal journal) => global::Scrinia.Core.AppendJournalCodec.Serialize(journal);
    internal static RecoveryEnvelopeDocument ToEnvelope(AppendJournal journal) => global::Scrinia.Core.AppendJournalCodec.ToEnvelope(journal);
    internal static AppendJournal WithState(AppendJournal journal, AppendJournalState state, string? committedHash = null, string? detail = null) =>
        global::Scrinia.Core.AppendJournalCodec.WithState(journal, state, committedHash, detail);
    internal static AppendJournal ConsumeParsed(in RecoveryVerifiedEnvelope envelope) => global::Scrinia.Core.AppendJournalCodec.ConsumeParsed(in envelope);
    internal static AppendJournal ParseLegacyJournal(ReadOnlySpan<byte> json, string expectedScope, string journalPath,
        string? expectedStoreDirectory = null) => RecoveryLegacyTestParser.ParseAppend(json, expectedScope, journalPath, expectedStoreDirectory);
}

internal static class RestoreJournalCodecTestFacade
{
    internal const int MaxJournalEntryCount = global::Scrinia.Core.RestoreJournalCodec.MaxJournalEntryCount;
    internal const long MaxRawNmp2BodyBytes = global::Scrinia.Core.RestoreJournalCodec.MaxRawNmp2BodyBytes;
    internal const long MaxSidecarBytes = global::Scrinia.Core.RestoreJournalCodec.MaxSidecarBytes;
    internal const long MaxAggregatePayloadBytes = global::Scrinia.Core.RestoreJournalCodec.MaxAggregatePayloadBytes;
    internal static AsyncLocal<Action?> DecodeObserver => global::Scrinia.Core.RestoreJournalCodec.DecodeObserver;
    internal static long MaxJournalJsonBytes => global::Scrinia.Core.RestoreJournalCodec.MaxJournalJsonBytes;
    internal static bool IsJournalDocumentLengthAdmitted(long value) => global::Scrinia.Core.RestoreJournalCodec.IsJournalDocumentLengthAdmitted(value);
    internal static bool IsEncodedPayloadLengthAdmitted(long value, long maximum) => global::Scrinia.Core.RestoreJournalCodec.IsEncodedPayloadLengthAdmitted(value, maximum);
    internal static bool IsDecodedPayloadLengthAdmitted(long value, long maximum) => global::Scrinia.Core.RestoreJournalCodec.IsDecodedPayloadLengthAdmitted(value, maximum);
    internal static long GetPaddedBase64Length(long value) => global::Scrinia.Core.RestoreJournalCodec.GetPaddedBase64Length(value);
    internal static RestoreJournal CreatePrepared(string scope, IReadOnlyList<RestoreJournalPayload> entries) => global::Scrinia.Core.RestoreJournalCodec.CreatePrepared(scope, entries);
    internal static byte[] Serialize(RestoreJournal journal) => global::Scrinia.Core.RestoreJournalCodec.Serialize(journal);
    internal static RestoreJournal WithCompleted(RestoreJournal journal) => global::Scrinia.Core.RestoreJournalCodec.WithCompleted(journal);
    internal static RecoveryEnvelopeDocument ToEnvelope(RestoreJournal journal) => global::Scrinia.Core.RestoreJournalCodec.ToEnvelope(journal);
    internal static long AddDecodedPayloadLength(long aggregate, long length, string scope, string path) => global::Scrinia.Core.RestoreJournalCodec.AddDecodedPayloadLength(aggregate, length, scope, path);
    internal static RestoreJournal ConsumeParsed(in RecoveryVerifiedEnvelope envelope) => global::Scrinia.Core.RestoreJournalCodec.ConsumeParsed(in envelope);
    internal static RestoreJournal ParseLegacyJournal(ReadOnlySpan<byte> json, string scope, string path) =>
        RecoveryLegacyTestParser.ParseRestore(json, scope, path);
}

internal static class RecoveryLegacyAdapterTestExtensions
{
    internal static RecoveryEnvelopeDocument ConsumeLegacy(this AppendRecoveryAdapter _, ReadOnlySpan<byte> bytes,
        string scope, string path, string directory) =>
        RecoveryEnvelopeCodec.FromAppend(RecoveryLegacyTestParser.ParseAppend(bytes, scope, path,
            Path.IsPathRooted(path) ? directory : null)) with { IsLegacy = true };

    internal static RecoveryEnvelopeDocument ConsumeLegacy(this RestoreRecoveryAdapter _, ReadOnlySpan<byte> bytes,
        string scope, string path, string directory) =>
        RecoveryEnvelopeCodec.FromRestore(RecoveryLegacyTestParser.ParseRestore(bytes, scope, path)) with { IsLegacy = true };

    internal static RecoveryEnvelopeDocument ConsumeLegacy(this FileEntityRecoveryAdapter adapter, ReadOnlySpan<byte> bytes,
        string scope, string path, string directory) => RecoveryLegacyTestParser.ParseFileEntity(bytes, scope, path, directory);
}
