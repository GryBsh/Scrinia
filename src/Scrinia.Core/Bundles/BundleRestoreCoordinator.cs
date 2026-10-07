using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Core.Bundles;

internal sealed record BundlePreparedMember(BundleJournalMember Evidence, byte[]? AfterBytes);
internal sealed record BundlePreparedPlan(BundlePreparedMember[] Members, int TopicCount, int EntryCount, int FileEntityCount);

internal enum BundleFaultPoint
{
    BeforeMember, AfterMember, BeforeCommitDecision, AfterCommitDecision,
    BeforeRollbackDecision, AfterRollbackDecision, Cleanup
}

/// <summary>Publishes a bundle with one durable decision and operation-owned compensation.</summary>
internal sealed class BundleRestoreCoordinator(FileMemoryStore store, WorkspaceAdmission admission, BundleReadLimits limits)
{
    internal static AsyncLocal<Func<BundleFaultPoint, string, Exception?>?> FailureInjector { get; } = new();
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    internal Task<BundleRestoreResult> ExecuteAsync(BundleRestorePlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using WorkspaceLease lease = admission.EnterRestore(Timeout, ct);
        string journalPath = JournalPath(lease);
        try { RecoverUnderLease(lease, limits, ct); }
        finally { store.InvalidateCaches(); }
        BundlePreparedPlan prepared;
        try { prepared = PrepareMembersUnderLease(plan, lease, ct); }
        catch (BundleImportException) { throw; }
        catch (RestoreRecoveryException) { throw; }
        catch (IOException failure)
        { throw new BundleImportException(BundleImportFailure.RolledBack, failure); }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or InvalidDataException)
        { throw new BundleImportException(BundleImportFailure.InvalidBundle, failure); }
        finally { store.InvalidateCaches(); }

        BundleRestoreJournal journal = BundleRestoreJournalCodec.CreatePrepared(Guid.NewGuid().ToString("N"),
            lease.WorkspaceRoot, prepared.Members.Select(m => m.Evidence).ToArray(), limits);
        bool preparedAttempted = false;
        bool decisionAttempted = false;
        bool commitProven = false;
        try
        {
            FileMemoryStore.ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.BeforePreparedWrite, journalPath);
            preparedAttempted = true;
            WriteJournal(journal, lease, limits);
            FileMemoryStore.ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.AfterPreparedWrite, journalPath);
            ApplyMembersUnderLease(prepared.Members, lease, ct);
            VerifyAfterStates(journal, lease, ct);
            // A durable write may throw after replacement. Never compensate after attempting it.
            decisionAttempted = true;
            Inject(BundleFaultPoint.BeforeCommitDecision, journalPath);
            WriteJournal(journal with { State = "committed" }, lease, limits);
            commitProven = true;
            Inject(BundleFaultPoint.AfterCommitDecision, journalPath);
            CleanupTerminalEvidence(journalPath);
        }
        catch (Exception failure)
        {
            if (commitProven)
                Console.Error.WriteLine($"scrinia: bundle committed; evidence cleanup deferred: {failure.Message}");
            else if (decisionAttempted)
                throw Recovery(failure, journalPath);
            else if (!preparedAttempted)
                throw new BundleImportException(BundleImportFailure.RolledBack, failure);
            else
            {
                try
                {
                    using var recoveryDeadline = new CancellationTokenSource(Timeout);
                    RollbackUnderLease(journal, lease, limits, recoveryDeadline.Token);
                }
                catch (Exception rollbackFailure) { throw Recovery(rollbackFailure, journalPath); }
                throw new BundleImportException(BundleImportFailure.RolledBack, failure);
            }
        }
        finally { store.InvalidateCaches(); }
        return Task.FromResult(new BundleRestoreResult(prepared.TopicCount, prepared.EntryCount,
            prepared.FileEntityCount, BundleRestoreOutcome.Committed));
    }

    internal static void RecoverUnderLease(WorkspaceLease lease, BundleReadLimits limits, CancellationToken ct)
    {
        lease.AssertValid(lease.WorkspaceRoot, requireExclusive: true);
        string path = JournalPath(lease);
        try
        {
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            BundleRestoreJournal journal = BundleRestoreJournalCodec.ReadValidated(path, lease.WorkspaceRoot, limits);
            if (journal.State == "prepared") RollbackUnderLease(journal, lease, limits, ct);
            else
            {
                BundleRestoreJournalCodec.PreflightCurrentState(journal, lease.WorkspaceRoot, ct);
                CleanupTerminalEvidence(path);
            }
        }
        catch (BundleImportException) { throw; }
        catch (Exception failure) { throw Recovery(failure, path); }
    }

    private BundlePreparedPlan PrepareMembersUnderLease(BundleRestorePlan plan, WorkspaceLease lease, CancellationToken ct)
    {
        lease.AssertValid(store.WorkspaceRoot, requireExclusive: true);
        if (plan.Topics is null || plan.FileEntities is null ||
            plan.Topics.Sum(t => (long)t.Entries.Count) + plan.FileEntities.Count > limits.LogicalEntities)
            throw new BundleValidationException("INVALID_BUNDLE: too many logical entities.");
        var members = new List<BundlePreparedMember>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long priorBytes = 0;
        long afterBytes = 0;
        int topics = 0, entries = 0, files = 0;
        // Validate all mapped paths before recovering scopes or capturing any prior bytes.
        foreach (BundleTopicRestore topic in plan.Topics)
        foreach (ArtifactEntry entry in topic.Entries)
        {
            if (FormatVersion.Evaluate(DurableFormats.BundleEntry, entry.SchemaVersion) != FormatDisposition.Load)
                throw new BundleValidationException("INVALID_BUNDLE: unsupported memory entry schema version.");
            MemoryWrite.ValidatePersistentSubject(entry.Name);
            _ = Address(Path.Combine(PathRouter.ToStoreDir(topic.Scope, lease.WorkspaceRoot), entry.Name + ".nmp2"), "memory-body");
            _ = Address(Path.Combine(PathRouter.ToStoreDir(topic.Scope, lease.WorkspaceRoot), entry.Name + ".meta.json"), "memory-meta");
            if (!topic.ArtifactBytes.ContainsKey(entry.Name)) throw new BundleValidationException("INVALID_BUNDLE: missing memory body.");
        }
        foreach (BundleFileEntityRestore file in plan.FileEntities)
        {
            if (file.Kind is not ("skill" or "agent") || !file.Name.EndsWith(".md", StringComparison.Ordinal) ||
                !FileEntityName.IsValid(file.Name[..^3]))
                throw new BundleValidationException("INVALID_BUNDLE: invalid file entity.");
            _ = Address(Path.Combine(PathRouter.ToFileEntityDir(file.Kind, lease.WorkspaceRoot), file.Name), file.Kind + "-body");
        }
        foreach (BundleTopicRestore topic in plan.Topics.OrderBy(t => t.Scope, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            store.RecoverBundleScopeUnderLease(lease, topic.Scope, ct);
            int initial = entries;
            foreach (ArtifactEntry entry in topic.Entries)
            {
                string directory = PathRouter.ToStoreDir(topic.Scope, lease.WorkspaceRoot);
                string subject = ResolveMemorySubjectUnderLease(lease, directory, entry.Name);
                string body = Path.Combine(directory, subject + ".nmp2");
                string meta = Path.Combine(directory, subject + ".meta.json");
                if (!plan.Overwrite && (File.Exists(body) || File.Exists(meta))) continue;
                string address = topic.Scope == "local" ? "/" + subject : MemoryNaming.FormatQualifiedName(topic.Scope, subject);
                ArtifactEntry mapped = entry with
                {
                    Name = subject,
                    Address = MemoryAddress.Parse(address),
                    ArtifactKey = ArtifactIdentityMapper.BuildArtifactKey(subject, topic.Scope),
                    CreatedAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt
                };
                Add(body, "memory-body", topic.ArtifactBytes[entry.Name]);
                Add(meta, "memory-meta", System.Text.Encoding.UTF8.GetBytes(FileMemoryStore.SerializeBundleSidecar(mapped)));
                entries++;
            }
            if (entries > initial) topics++;
        }
        foreach (BundleFileEntityRestore file in plan.FileEntities.OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            string directory = PathRouter.ToFileEntityDir(file.Kind, lease.WorkspaceRoot);
            store.RecoverBundleEntityUnderLease(lease, file.Kind, Path.GetFileNameWithoutExtension(file.Name), directory, ct);
            string body = Path.Combine(directory, file.Name);
            string meta = Path.ChangeExtension(body, ".meta.json");
            if (!plan.Overwrite && (File.Exists(body) || File.Exists(meta))) continue;
            Add(body, file.Kind + "-body", file.Body);
            Add(meta, file.Kind + "-meta", file.Sidecar);
            files++;
        }
        return new(members.ToArray(), topics, entries, files);

        BundleJournalMember Address(string path, string kind)
        {
            var member = new BundleJournalMember(Path.GetRelativePath(lease.WorkspaceRoot, path).Replace('\\', '/'),
                kind, false, null, null, false, null);
            BundleRestoreJournalCodec.Authorize(member, lease.WorkspaceRoot);
            return member;
        }
        void Add(string path, string kind, byte[]? bytes)
        {
            ct.ThrowIfCancellationRequested();
            BundleJournalMember evidence = Address(path, kind);
            if (!paths.Add(evidence.RelativePath)) throw new BundleValidationException("INVALID_BUNDLE: duplicate member.");
            if (members.Count >= limits.JournalMembers) throw new BundleValidationException("INVALID_BUNDLE: too many members.");
            if (bytes is not null && bytes.LongLength > (kind.EndsWith("-body", StringComparison.Ordinal) ? limits.BodyBytes : limits.JsonBytes))
                throw new BundleValidationException("INVALID_BUNDLE: member exceeds capacity.");
            afterBytes = checked(afterBytes + (bytes?.LongLength ?? 0));
            if (afterBytes > limits.ExpandedBytes) throw new BundleValidationException("INVALID_BUNDLE: expanded payload exceeds capacity.");
            byte[]? before = null;
            // Authorize performs positive native regular-file classification before this open.
            if (File.Exists(path))
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                long remaining = limits.PriorBytes - priorBytes;
                if (input.Length > remaining) throw new BundleImportException(BundleImportFailure.RolledBack,
                    new InvalidOperationException("Prior-state byte capacity exceeded."));
                using var output = new MemoryStream();
                byte[] block = new byte[8192];
                int count;
                while ((count = input.Read(block, 0, (int)Math.Min(block.Length, remaining + 1))) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    remaining -= count;
                    if (remaining < 0) throw new BundleImportException(BundleImportFailure.RolledBack,
                        new InvalidOperationException("Prior-state byte capacity exceeded."));
                    output.Write(block, 0, count);
                }
                before = output.ToArray();
                priorBytes += before.LongLength;
            }
            byte[]? after = bytes?.ToArray();
            if (kind.EndsWith("-meta", StringComparison.Ordinal) && before is not null)
                RefuseForwardVersion(before, kind, path);
            members.Add(new(evidence with { BeforeExists = before is not null, BeforeBytes = before,
                BeforeSha256 = Hash(before), AfterExists = after is not null, AfterSha256 = Hash(after) }, after));
        }
    }

    private static string ResolveMemorySubjectUnderLease(WorkspaceLease lease, string directory, string incoming)
    {
        lease.AssertValid(lease.WorkspaceRoot, requireExclusive: true);
        if (!Directory.Exists(directory)) return incoming;
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string file = Path.GetFileName(path);
            string? subject = file.EndsWith(".meta.json", StringComparison.Ordinal) ? file[..^10]
                : file.EndsWith(".nmp2", StringComparison.Ordinal) ? file[..^5] : null;
            if (subject is not null && subject.Equals(incoming, StringComparison.OrdinalIgnoreCase))
                matches.Add(subject);
        }
        if (matches.Count > 1)
            throw new BundleValidationException("INVALID_BUNDLE: existing memory has ambiguous case-only member names.");
        return matches.Count == 1 ? matches.Single() : incoming;
    }

    private static void RefuseForwardVersion(byte[] bytes, string kind, string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase) ||
                    property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out int version)) continue;
                FormatContract contract = kind switch
                {
                    "skill-meta" => DurableFormats.SkillMeta,
                    "agent-meta" => DurableFormats.AgentMeta,
                    _ => DurableFormats.ArtifactSidecar
                };
                if (FormatVersion.Evaluate(contract, version) == FormatDisposition.LoadReadOnly)
                    throw new ScopeReadOnlyException(Path.GetDirectoryName(path)!, Path.GetFileName(path), version, contract.Current);
            }
        }
        catch (JsonException)
        {
            // Restore may repair a corrupt pair. Its exact prior bytes remain in the manifest.
        }
    }

    private static void ApplyMembersUnderLease(BundlePreparedMember[] members, WorkspaceLease lease, CancellationToken ct)
    {
        lease.AssertValid(lease.WorkspaceRoot, requireExclusive: true);
        foreach (BundlePreparedMember member in members)
        {
            ct.ThrowIfCancellationRequested();
            string path = BundleRestoreJournalCodec.Authorize(member.Evidence, lease.WorkspaceRoot);
            Inject(BundleFaultPoint.BeforeMember, path);
            Publish(path, member.AfterBytes);
            Inject(BundleFaultPoint.AfterMember, path);
        }
    }

    private static void VerifyAfterStates(BundleRestoreJournal journal, WorkspaceLease lease, CancellationToken ct)
    {
        lease.AssertValid(lease.WorkspaceRoot, requireExclusive: true);
        BundleRestoreJournalCodec.PreflightCurrentState(journal with { State = "committed" }, lease.WorkspaceRoot, ct);
    }

    private static void RollbackUnderLease(BundleRestoreJournal journal, WorkspaceLease lease, BundleReadLimits limits, CancellationToken ct)
    {
        lease.AssertValid(lease.WorkspaceRoot, requireExclusive: true);
        BundleRestoreJournalCodec.PreflightCurrentState(journal, lease.WorkspaceRoot, ct);
        foreach (BundleJournalMember member in journal.Members)
        {
            ct.ThrowIfCancellationRequested();
            Publish(BundleRestoreJournalCodec.Authorize(member, lease.WorkspaceRoot), member.BeforeBytes);
        }
        var rolledBack = journal with { State = "rolled-back" };
        BundleRestoreJournalCodec.PreflightCurrentState(rolledBack, lease.WorkspaceRoot, ct);
        Inject(BundleFaultPoint.BeforeRollbackDecision, JournalPath(lease));
        WriteJournal(rolledBack, lease, limits);
        Inject(BundleFaultPoint.AfterRollbackDecision, JournalPath(lease));
        CleanupTerminalEvidence(JournalPath(lease));
    }

    private static void Publish(string path, byte[]? bytes)
    {
        if (bytes is null) { if (File.Exists(path)) DurableFile.DeleteAndSync(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DurableFile.AtomicWriteAllBytes(path, bytes);
    }

    private static void WriteJournal(BundleRestoreJournal journal, WorkspaceLease lease, BundleReadLimits limits) =>
        DurableFile.AtomicWriteAllBytes(JournalPath(lease), BundleRestoreJournalCodec.SerializeValidated(journal, lease.WorkspaceRoot, limits));

    private static void CleanupTerminalEvidence(string path)
    {
        Inject(BundleFaultPoint.Cleanup, path);
        FileMemoryStore.ThrowIfRestoreJournalTransitionFails(RestoreJournalTransition.BeforeCleanupDelete, path);
        DurableFile.DeleteAndSync(path);
    }

    private static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string JournalPath(WorkspaceLease lease) => Path.Combine(lease.WorkspaceRoot, ".scrinia", ".bundle-restore-journal.json");
    private static BundleImportException Recovery(Exception failure, string path) => new(BundleImportFailure.RecoveryRequired, failure, path);
    private static void Inject(BundleFaultPoint point, string path)
    {
        if (FailureInjector.Value?.Invoke(point, path) is { } failure) throw failure;
    }
}
