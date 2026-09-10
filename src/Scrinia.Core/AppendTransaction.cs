using System.Security.Cryptography;
using Scrinia.Core.Models;

namespace Scrinia.Core;

/// <summary>Immutable bytes and identity captured for one durable member.</summary>
public sealed record RawMemberSnapshot
{
    private readonly byte[] _bytes;

    /// <param name="path">Absolute path of the captured member.</param>
    /// <param name="existed">Whether the member existed at capture time.</param>
    /// <param name="bytes">Captured member bytes.</param>
    /// <param name="sha256">Expected SHA-256 hash of <paramref name="bytes"/>.</param>
    public RawMemberSnapshot(string path, bool existed, byte[] bytes, string sha256)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(sha256);

        Path = path;
        Existed = existed;
        _bytes = bytes.ToArray();
        string computedHash = Convert.ToHexStringLower(SHA256.HashData(_bytes));
        if (!string.Equals(computedHash, sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sha256 must match the captured bytes.", nameof(sha256));
        Sha256 = computedHash;
    }

    /// <summary>Absolute path of the captured artifact or sidecar member.</summary>
    public string Path { get; }
    /// <summary>Whether the member existed when the transaction was prepared.</summary>
    public bool Existed { get; }
    /// <summary>A defensive copy of the captured member bytes.</summary>
    public byte[] Bytes => _bytes.ToArray();
    /// <summary>Lowercase SHA-256 hash of <see cref="Bytes"/>.</summary>
    public string Sha256 { get; }

    internal static RawMemberSnapshot Capture(string path, bool existed, byte[] bytes) =>
        new(path, existed, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
}

/// <summary>Frozen archive destinations and cleanup candidates for an append.</summary>
public sealed record ArchivePlan
{
    /// <param name="required">Whether an existing target requires an archive.</param>
    /// <param name="artifactArchivePath">Frozen artifact archive destination.</param>
    /// <param name="sidecarArchivePath">Frozen sidecar archive destination.</param>
    /// <param name="artifactStatus">Current artifact archive status.</param>
    /// <param name="sidecarStatus">Current sidecar archive status.</param>
    /// <param name="pruneCandidates">Archive paths eligible for pruning.</param>
    public ArchivePlan(
        bool required,
        string? artifactArchivePath,
        string? sidecarArchivePath,
        ArchiveMemberStatus artifactStatus,
        ArchiveMemberStatus sidecarStatus,
        IReadOnlyList<string> pruneCandidates)
    {
        ArgumentNullException.ThrowIfNull(pruneCandidates);

        Required = required;
        ArtifactArchivePath = artifactArchivePath;
        SidecarArchivePath = sidecarArchivePath;
        ArtifactStatus = artifactStatus;
        SidecarStatus = sidecarStatus;
        PruneCandidates = Array.AsReadOnly(pruneCandidates.ToArray());
    }

    /// <summary>Whether an existing memory requires a prior-version archive.</summary>
    public bool Required { get; }
    /// <summary>Frozen artifact archive destination, when required.</summary>
    public string? ArtifactArchivePath { get; }
    /// <summary>Frozen sidecar archive destination, when required.</summary>
    public string? SidecarArchivePath { get; }
    /// <summary>Durable status of the artifact archive member.</summary>
    public ArchiveMemberStatus ArtifactStatus { get; }
    /// <summary>Durable status of the sidecar archive member.</summary>
    public ArchiveMemberStatus SidecarStatus { get; }
    /// <summary>Archive paths eligible for post-commit pruning.</summary>
    public IReadOnlyList<string> PruneCandidates { get; }

    internal ArchivePlan WithStatuses(ArchiveMemberStatus artifactStatus, ArchiveMemberStatus sidecarStatus) =>
        new(Required, ArtifactArchivePath, SidecarArchivePath, artifactStatus, sidecarStatus, PruneCandidates);
}

public enum ArchiveMemberStatus
{
    /// <summary>No archive member is needed, as for create-via-append.</summary>
    NotRequired,
    /// <summary>An archive destination has been planned but not written.</summary>
    Planned,
    /// <summary>The archive member was written by this transaction.</summary>
    Created,
    /// <summary>An existing archive member was verified and reused.</summary>
    AlreadyPresentVerified,
    /// <summary>The archive member write or verification failed.</summary>
    Failed
}

/// <summary>All append inputs and outputs prepared before destructive publication.</summary>
public sealed record AppendTransaction(
    /// <param name="OperationId">Correlation ID for this append transaction.</param>
    string OperationId,
    /// <param name="Address">Logical memory address being appended to.</param>
    MemoryAddress Address,
    /// <param name="Scope">Store scope containing the memory.</param>
    string Scope,
    /// <param name="IsCreate">Whether this is create-via-append rather than an existing target append.</param>
    bool IsCreate,
    /// <param name="OldArtifact">Captured pre-append artifact bytes.</param>
    RawMemberSnapshot OldArtifact,
    /// <param name="OldSidecar">Captured pre-append sidecar bytes.</param>
    RawMemberSnapshot OldSidecar,
    /// <param name="NewArtifact">Prepared post-append artifact bytes.</param>
    RawMemberSnapshot NewArtifact,
    /// <param name="NewSidecar">Prepared post-append sidecar bytes.</param>
    RawMemberSnapshot NewSidecar,
    /// <param name="Archive">Frozen archive and pruning plan.</param>
    ArchivePlan Archive,
    /// <param name="StartedAt">UTC timestamp at which preparation began.</param>
    DateTimeOffset StartedAt);
