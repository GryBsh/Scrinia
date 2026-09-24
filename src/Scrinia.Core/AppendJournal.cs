using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>The durable states of one persistent append transaction.</summary>
[JsonConverter(typeof(AppendJournalStateJsonConverter))]
public enum AppendJournalState
{
    /// <summary>Transaction inputs and rollback evidence have been durably prepared.</summary>
    Prepared,
    /// <summary>The archive pair is being published.</summary>
    ArchiveInProgress,
    /// <summary>The prior version archive has been published.</summary>
    Archived,
    /// <summary>The new live artifact and sidecar pair is being published.</summary>
    NewPairInProgress,
    /// <summary>The new live pair has passed commit verification.</summary>
    Committed,
    /// <summary>The live pair committed, but journal or archive cleanup remains.</summary>
    CleanupPending,
    /// <summary>The journal retains evidence requiring explicit recovery.</summary>
    RecoveryRequired,
    /// <summary>The transaction was rolled back to its captured pre-append state.</summary>
    RolledBack
}

internal enum AppendJournalFailure
{
    Malformed,
    UnsupportedVersion,
    IntegrityFailed
}

internal sealed class AppendJournalException : IOException
{
    internal AppendJournalException(AppendJournalFailure failure, string message, Exception? inner = null,
        string? operationId = null)
        : base(message, inner)
    {
        Failure = failure;
        OperationId = operationId;
    }

    internal AppendJournalFailure Failure { get; }
    internal string? OperationId { get; }

    internal AppendJournalException WithOperationId(string operationId) =>
        OperationId is not null
            ? this
            : new AppendJournalException(Failure, Message, InnerException, operationId);
}

internal sealed class AppendJournalStateJsonConverter : JsonConverter<AppendJournalState>
{
    public override AppendJournalState Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Append journal state must be a lowercase string.");

        return reader.GetString() switch
        {
            "prepared" => AppendJournalState.Prepared,
            "archiveInProgress" => AppendJournalState.ArchiveInProgress,
            "archived" => AppendJournalState.Archived,
            "newPairInProgress" => AppendJournalState.NewPairInProgress,
            "committed" => AppendJournalState.Committed,
            "cleanupPending" => AppendJournalState.CleanupPending,
            "recoveryRequired" => AppendJournalState.RecoveryRequired,
            "rolledBack" => AppendJournalState.RolledBack,
            _ => throw new JsonException("Append journal state is invalid.")
        };
    }

    public override void Write(Utf8JsonWriter writer, AppendJournalState value,
        JsonSerializerOptions options) => writer.WriteStringValue(value switch
        {
            AppendJournalState.Prepared => "prepared",
            AppendJournalState.ArchiveInProgress => "archiveInProgress",
            AppendJournalState.Archived => "archived",
            AppendJournalState.NewPairInProgress => "newPairInProgress",
            AppendJournalState.Committed => "committed",
            AppendJournalState.CleanupPending => "cleanupPending",
            AppendJournalState.RecoveryRequired => "recoveryRequired",
            AppendJournalState.RolledBack => "rolledBack",
            _ => throw new JsonException("Append journal state is invalid.")
        });
}

internal sealed record AppendJournalMember(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("existed")] bool Existed,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sha256")] string Sha256)
{
    internal static AppendJournalMember FromSnapshot(RawMemberSnapshot snapshot) => new(
        snapshot.Path,
        snapshot.Existed,
        Convert.ToBase64String(snapshot.Bytes),
        snapshot.Sha256);

    internal RawMemberSnapshot ToSnapshot(string journalPath)
    {
        byte[] bytes = DecodePayload(Payload, Sha256, journalPath);
        return new RawMemberSnapshot(Path, Existed, bytes, Sha256);
    }

    private static byte[] DecodePayload(string payload, string expectedHash, string journalPath)
    {
        byte[] bytes;
        try
        {
            bytes = RecoveryPayload.DecodeBase64(payload);
        }
        catch (FormatException exception)
        {
            throw new AppendJournalException(AppendJournalFailure.IntegrityFailed,
                $"Append journal payload is not valid base64: {journalPath}", exception);
        }

        string actualHash = RecoveryPayload.Sha256HexLower(bytes);
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            throw new AppendJournalException(AppendJournalFailure.IntegrityFailed,
                $"Append journal payload hash does not match: {journalPath}");
        return bytes;
    }
}

internal sealed record AppendJournal(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("state")] AppendJournalState State,
    [property: JsonPropertyName("isCreate")] bool IsCreate,
    [property: JsonPropertyName("oldArtifact")] AppendJournalMember OldArtifact,
    [property: JsonPropertyName("oldSidecar")] AppendJournalMember OldSidecar,
    [property: JsonPropertyName("newArtifact")] AppendJournalMember NewArtifact,
    [property: JsonPropertyName("newSidecar")] AppendJournalMember NewSidecar,
    [property: JsonPropertyName("archiveArtifactPath")] string? ArchiveArtifactPath,
    [property: JsonPropertyName("archiveSidecarPath")] string? ArchiveSidecarPath,
    [property: JsonPropertyName("archiveArtifactStatus")] ArchiveMemberStatus ArchiveArtifactStatus,
    [property: JsonPropertyName("archiveSidecarStatus")] ArchiveMemberStatus ArchiveSidecarStatus,
    [property: JsonPropertyName("pruneCandidates")] IReadOnlyList<string> PruneCandidates,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("committedHash")] string? CommittedHash,
    [property: JsonPropertyName("detail")] string? Detail)
{
    internal const string FileName = ".append-journal.json";

    internal AppendTransaction ToTransaction()
    {
        var archive = new ArchivePlan(
            ArchiveArtifactPath is not null || ArchiveSidecarPath is not null,
            ArchiveArtifactPath,
            ArchiveSidecarPath,
            ArchiveArtifactStatus,
            ArchiveSidecarStatus,
            PruneCandidates);
        return new AppendTransaction(
            OperationId,
            MemoryAddress.Parse(Address),
            Scope,
            IsCreate,
            OldArtifact.ToSnapshot(FileName),
            OldSidecar.ToSnapshot(FileName),
            NewArtifact.ToSnapshot(FileName),
            NewSidecar.ToSnapshot(FileName),
            archive,
            StartedAt);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppendJournal))]
[JsonSerializable(typeof(AppendJournalMember))]
[JsonSerializable(typeof(AppendJournalState))]
[JsonSerializable(typeof(ArchiveMemberStatus))]
internal partial class AppendJournalJsonContext : JsonSerializerContext;

internal static class AppendJournalCodec
{
    private const string FormatName = "scrinia.append-journal";

    internal static AppendJournal ConsumeParsed(in RecoveryVerifiedEnvelope envelope) =>
        envelope.TypedBody as AppendRecoveryBody is { } body
            ? new AppendJournal(FormatName, DurableFormats.AppendJournal.Current, envelope.Document.OperationId,
                envelope.Document.Address!, envelope.Document.Scope, body.State, body.IsCreate, body.OldArtifact,
                body.OldSidecar, body.NewArtifact, body.NewSidecar, body.ArchiveArtifactPath,
                body.ArchiveSidecarPath, body.ArchiveArtifactStatus, body.ArchiveSidecarStatus,
                body.PruneCandidates, envelope.Document.CreatedAt, body.CommittedHash, body.Detail)
            : throw new ArgumentException("Verified envelope does not contain an append body.", nameof(envelope));

    internal static AppendJournal CreatePrepared(AppendTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new AppendJournal(
            FormatName,
            DurableFormats.AppendJournal.Current,
            transaction.OperationId,
            transaction.Address.ToString(),
            transaction.Scope,
            AppendJournalState.Prepared,
            transaction.IsCreate,
            AppendJournalMember.FromSnapshot(transaction.OldArtifact),
            AppendJournalMember.FromSnapshot(transaction.OldSidecar),
            AppendJournalMember.FromSnapshot(transaction.NewArtifact),
            AppendJournalMember.FromSnapshot(transaction.NewSidecar),
            transaction.Archive.ArtifactArchivePath,
            transaction.Archive.SidecarArchivePath,
            transaction.Archive.ArtifactStatus,
            transaction.Archive.SidecarStatus,
            transaction.Archive.PruneCandidates,
            transaction.StartedAt,
            null,
            null);
    }

    internal static RecoveryEnvelopeDocument ToEnvelope(AppendJournal journal) =>
        RecoveryEnvelopeCodec.FromAppend(journal);

    internal static byte[] Serialize(AppendJournal journal)
    {
        Validate(journal, journal.Scope, "<new append journal>");
        return JsonSerializer.SerializeToUtf8Bytes(journal, AppendJournalJsonContext.Default.AppendJournal);
    }

    internal static void ValidateLegacyParsed(AppendJournal journal, string expectedScope,
        string journalPath, string? expectedStoreDirectory = null)
    {
        Validate(journal, expectedScope, journalPath);
        if (expectedStoreDirectory is not null)
            ValidateLayout(journal, expectedStoreDirectory, journalPath);
        ValidatePayloads(journal, journalPath);
    }

    internal static AppendJournal WithState(AppendJournal journal, AppendJournalState state,
        string? committedHash = null, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!IsLegalTransition(journal.State, state))
            throw new AppendJournalException(AppendJournalFailure.Malformed,
                $"Append journal cannot transition from {journal.State} to {state}.");

        return journal with
        {
            State = state,
            CommittedHash = committedHash ?? journal.CommittedHash,
            Detail = detail ?? journal.Detail
        };
    }

    internal static void ValidateForRecovery(AppendJournal journal, string journalPath,
        string? expectedStoreDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Validate(journal, journal.Scope, journalPath);
        if (expectedStoreDirectory is not null)
            ValidateLayout(journal, expectedStoreDirectory, journalPath);
        ValidatePayloads(journal, journalPath);
    }

    internal static string GetJournalPath(string storeDirectory) =>
        Path.Combine(storeDirectory, AppendJournal.FileName);

    private static void Validate(AppendJournal journal, string expectedScope, string journalPath)
    {
        if (!string.Equals(journal.Format, FormatName, StringComparison.Ordinal))
            throw Invalid("Append journal format is invalid", journalPath);
        if (journal.Version > DurableFormats.AppendJournal.Current)
            throw new AppendJournalException(AppendJournalFailure.UnsupportedVersion,
                $"Append journal version {journal.Version} is newer than this build: {journalPath}");
        if (journal.Version != DurableFormats.AppendJournal.Current)
            throw Invalid("Append journal version is invalid", journalPath);
        if (!Enum.IsDefined(journal.State) || !Enum.IsDefined(journal.ArchiveArtifactStatus) ||
            !Enum.IsDefined(journal.ArchiveSidecarStatus))
            throw Invalid("Append journal state is invalid", journalPath);
        if (string.IsNullOrWhiteSpace(journal.OperationId) || string.IsNullOrWhiteSpace(journal.Scope) ||
            !string.Equals(journal.Scope, expectedScope, StringComparison.Ordinal))
            throw Invalid("Append journal identity does not match its scope", journalPath);
        try
        {
            _ = MemoryAddress.Parse(journal.Address);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("Append journal address is malformed", journalPath, exception);
        }
        if (journal.OldArtifact is null || journal.OldSidecar is null || journal.NewArtifact is null ||
            journal.NewSidecar is null || journal.PruneCandidates is null)
            throw Invalid("Append journal transaction data is incomplete", journalPath);
        ValidateMemberFields(journal.OldArtifact, journalPath);
        ValidateMemberFields(journal.OldSidecar, journalPath);
        ValidateMemberFields(journal.NewArtifact, journalPath);
        ValidateMemberFields(journal.NewSidecar, journalPath);
        ValidatePruneCandidates(journal.PruneCandidates, journalPath);
    }

    private static void ValidatePayloads(AppendJournal journal, string journalPath)
    {
        _ = journal.OldArtifact.ToSnapshot(journalPath);
        _ = journal.OldSidecar.ToSnapshot(journalPath);
        _ = journal.NewArtifact.ToSnapshot(journalPath);
        _ = journal.NewSidecar.ToSnapshot(journalPath);
    }

    private static void ValidateMemberFields(AppendJournalMember member, string journalPath)
    {
        if (string.IsNullOrWhiteSpace(member.Path))
            throw Invalid("Append journal member path is missing", journalPath);
        if (member.Payload is null)
            throw Invalid("Append journal member payload is missing", journalPath);
        if (!IsSha256(member.Sha256))
            throw Invalid("Append journal member hash is invalid", journalPath);
    }

    private static void ValidateLayout(AppendJournal journal, string storeDirectory, string journalPath)
    {
        string storeRoot = Path.GetFullPath(storeDirectory);
        string versionsRoot = Path.Combine(storeRoot, "versions");

        ValidateLiveMemberPath(journal.OldArtifact.Path, storeRoot, journalPath);
        ValidateLiveMemberPath(journal.OldSidecar.Path, storeRoot, journalPath);
        ValidateLiveMemberPath(journal.NewArtifact.Path, storeRoot, journalPath);
        ValidateLiveMemberPath(journal.NewSidecar.Path, storeRoot, journalPath);
        ValidateOptionalArchivePath(journal.ArchiveArtifactPath, storeRoot, versionsRoot, journalPath);
        ValidateOptionalArchivePath(journal.ArchiveSidecarPath, storeRoot, versionsRoot, journalPath);
        foreach (string candidate in journal.PruneCandidates)
            ValidatePruneCandidatePath(candidate, storeRoot, versionsRoot, journalPath);
    }

    private static void ValidateLiveMemberPath(string path, string storeRoot, string journalPath)
    {
        string fullPath;
        try
        {
            fullPath = ResolveStorePath(path, storeRoot);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("Append journal member path is malformed", journalPath, exception);
        }
        if (!string.Equals(Path.GetDirectoryName(fullPath), storeRoot, StringComparison.OrdinalIgnoreCase) ||
            !IsArtifactOrSidecar(fullPath))
            throw Invalid("Append journal member path is outside the expected store layout", journalPath);
    }

    private static void ValidateOptionalArchivePath(string? path, string storeRoot, string versionsRoot, string journalPath)
    {
        if (path is null)
            return;

        string fullPath;
        try
        {
            fullPath = ResolveStorePath(path, storeRoot);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("Append journal archive path is malformed", journalPath, exception);
        }
        if (!string.Equals(Path.GetDirectoryName(fullPath), versionsRoot, StringComparison.OrdinalIgnoreCase) ||
            !IsArtifactOrSidecar(fullPath))
            throw Invalid("Append journal archive path is outside the expected store layout", journalPath);
    }

    private static void ValidatePruneCandidates(IReadOnlyList<string> candidates, string journalPath)
    {
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                throw Invalid("Append journal prune candidate is missing", journalPath);
        }
    }

    private static void ValidatePruneCandidatePath(string path, string storeRoot, string versionsRoot, string journalPath) =>
        ValidateOptionalArchivePath(path, storeRoot, versionsRoot, journalPath);

    private static string ResolveStorePath(string path, string storeRoot) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(storeRoot, path));

    private static bool IsArtifactOrSidecar(string path) =>
        path.EndsWith(".nmp2", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value) => RecoveryPayload.IsLowercaseSha256(value);

    private static bool IsLegalTransition(AppendJournalState current, AppendJournalState next) =>
        current == next || (current, next) switch
        {
            (AppendJournalState.Prepared, AppendJournalState.ArchiveInProgress or AppendJournalState.NewPairInProgress or AppendJournalState.RecoveryRequired or AppendJournalState.RolledBack) => true,
            (AppendJournalState.ArchiveInProgress, AppendJournalState.Archived or AppendJournalState.RecoveryRequired or AppendJournalState.RolledBack) => true,
            (AppendJournalState.Archived, AppendJournalState.NewPairInProgress or AppendJournalState.RecoveryRequired or AppendJournalState.RolledBack) => true,
            (AppendJournalState.NewPairInProgress, AppendJournalState.Committed or AppendJournalState.RecoveryRequired or AppendJournalState.RolledBack) => true,
            (AppendJournalState.Committed, AppendJournalState.CleanupPending) => true,
            (AppendJournalState.RecoveryRequired, AppendJournalState.ArchiveInProgress or AppendJournalState.Archived or AppendJournalState.RolledBack) => true,
            _ => false
        };

    private static AppendJournalException Invalid(string message, string path, Exception? inner = null) =>
        new(AppendJournalFailure.Malformed, $"{message}: {path}", inner);
}
