using System.Security.Cryptography;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp.SharedContracts;

public sealed partial class SharedServer
{
    internal SharedEnvelope Bases(CancellationToken ct) => Invoke(null, () => { }, () =>
    {
        ct.ThrowIfCancellationRequested();
        SharedBaseSummary[] summaries = GetEnabledBaseSummaries().ToArray();
        return new SharedEnvelope
        {
            Status = summaries.All(item => item.Availability == SharedBaseAvailability.Available) ? "success" : "partial",
            Results = summaries.Cast<object>().ToArray(),
            Warnings = summaries.Where(item => item.Availability != SharedBaseAvailability.Available)
                .Select(item => Warning(item.Base, AvailabilityCode(item.Availability))).ToArray()
        };
    });

    internal SharedEnvelope Search(string query, string? baseName, int limit, string? pathPrefix,
        Dictionary<string, string[]>? attributeFilter, string? context, string[]? excludeTopics, CancellationToken ct) =>
        Invoke(baseName, () =>
        {
            SharedValidation.Text(query); SharedValidation.Range(limit, 1, 100);
            SharedValidation.Text(pathPrefix, true); SharedValidation.Text(context, true);
            SharedValidation.Attributes(attributeFilter); SharedValidation.Members(excludeTopics, 100, 128);
        }, () => Aggregate(baseName, pathPrefix, snapshot =>
        {
            string? prefix = pathPrefix is null ? null : ParsePersistentAddress(pathPrefix).Value;
            var request = new SearchQuery(context is null ? query : query + " " + context)
            // Keep one overflow candidate per base so final top-K projection can report
            // incompleteness even when only one selected base has matching entries.
            { Limit = limit + 1, PathPrefix = prefix, AttributeFilter = attributeFilter, ExcludeTopics = excludeTopics };
            return SharedMemoryOperations.Search(snapshot, request).Select(result => result switch
            {
                EntryResult entry => new SearchHit(snapshot.Identity.BaseName, entry.Item.Entry.Address.Value,
                    SharedHitKind.Entry, null, entry.Score, RetrievalPreview(entry.Item.Entry.ContentPreview, entry.ContextPreview), entry.Item.Entry.Attributes ?? []),
                ChunkEntryResult chunk => new SearchHit(snapshot.Identity.BaseName, chunk.ParentItem.Entry.Address.Value,
                    SharedHitKind.Chunk, chunk.Chunk.ChunkIndex, chunk.Score, RetrievalPreview(chunk.ParentItem.Entry.ContentPreview, chunk.ContextPreview ?? chunk.Chunk.ContentPreview), chunk.ParentItem.Entry.Attributes ?? []),
                _ => null
            }).Where(hit => hit is not null).Cast<object>().ToArray();
        }, results =>
        {
            SearchHit[] sorted = results.Cast<SearchHit>().OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.Base, StringComparer.Ordinal).ThenBy(hit => hit.Path, StringComparer.Ordinal)
                .ThenBy(hit => hit.Kind).ThenBy(hit => hit.Chunk).ToArray();
            return (sorted.Take(limit).Cast<object>().ToArray(), sorted.Length > limit, (SharedPage?)null);
        }, ct, search: true), aggregate: true);

    internal SharedEnvelope List(string? baseName, int offset, int limit, string mode, string[]? excludeTopics, CancellationToken ct) =>
        Invoke(baseName, () =>
        {
            SharedValidation.Range(offset, 0, int.MaxValue - limit); SharedValidation.Range(limit, 1, 100);
            if (mode is not ("summary" or "full" or "drift")) SharedValidation.Invalid();
            SharedValidation.Members(excludeTopics, 100, 128);
        }, () => Aggregate(baseName, null, snapshot => snapshot.Memories
            .Where(item => !SharedMemoryOperations.Excluded(item.Path, excludeTopics))
            .Select(item => (object)ListEntry(snapshot.Identity, item, mode)).ToArray(), results =>
        {
            ListItem[] sorted = results.Cast<ListItem>().OrderBy(item => item.Base, StringComparer.Ordinal)
                .ThenBy(item => item.Path, StringComparer.Ordinal).ToArray();
            object[] page = sorted.Skip(offset).Take(limit).Cast<object>().ToArray();
            return (page, (bool?)null, new SharedPage(offset, limit, page.Length, sorted.Length, offset + page.Length < sorted.Length));
        }, ct, page: new SharedPage(offset, limit, 0, 0, false)), aggregate: true);

    internal SharedEnvelope Recall(string baseName, string path, int? chunk, CancellationToken ct) =>
        Invoke(baseName, () => { SharedValidation.Text(baseName); SharedValidation.Text(path); if (chunk is { } number) SharedValidation.Range(number, 1, 100000); }, () =>
        {
            SharedBaseRuntime runtime = OpenValidatedBase(baseName, path);
            using var lease = runtime.AcquireReadLease();
            EnsureAvailable(runtime.Snapshot);
            ct.ThrowIfCancellationRequested();
            SharedMemorySnapshot item = SharedMemoryOperations.RequireEntry(runtime.Snapshot, ParsePersistentAddress(path));
            if (chunk > item.Chunks.Length) throw new SharedOperationException("SHARED_ENTRY_NOT_FOUND");
            return SharedEnvelope.Success(baseName, new RecallResult(baseName, item.Path,
                chunk is { } index ? [item.Chunks[index - 1]] : item.Chunks.ToArray(), item.Attributes, item.Keywords, item.Created, item.Updated))
                with { Warnings = Warnings(runtime.Snapshot) };
        });

    internal SharedEnvelope Facts(string baseName, string op, string? entity, string[]? entities, string? key,
        int limit, string[]? excludeTopics, CancellationToken ct) => Invoke(baseName, () =>
        {
            SharedValidation.Text(baseName);
            SharedValidation.Range(limit, 1, 100); SharedValidation.Members(excludeTopics, 100, 128);
            SharedValidation.Text(entity, true); SharedValidation.Text(key, true, 128); SharedValidation.Members(entities, 100);
            if (op == "entities") { if (entity is not null || entities is not null) SharedValidation.Invalid(); }
            else if (op == "profile") { SharedValidation.Text(entity); if (entities is not null || key is not null) SharedValidation.Invalid(); }
            else if (op == "intersect") { if (entities is null || entities.Length < 2 || entity is not null || key is not null) SharedValidation.Invalid(); }
            else SharedValidation.Invalid();
        }, () =>
        {
            SharedBaseRuntime runtime = OpenValidatedBase(baseName, null);
            using var lease = runtime.AcquireReadLease();
            EnsureAvailable(runtime.Snapshot); ct.ThrowIfCancellationRequested();
            EntityFactStore store = EntityFactDerivation.BuildStore(SharedMemoryOperations.Candidates(runtime.Snapshot, excludeTopics));
            object result;
            if (op == "entities")
            {
                var all = store.Entities(key).Select(item => new EntityCount(EntityName(item), store.MentionCount(item)))
                    .OrderByDescending(item => item.Count).ThenBy(item => item.Entity, StringComparer.Ordinal).ToArray();
                result = new FactsEntitiesResult(baseName, FactsOperation.Entities, all.Take(limit).ToArray(), all.Length > limit);
            }
            else if (op == "profile")
            {
                EntityRef resolved = ResolveEntity(store, entity!);
                EntityFact[] facts = store.Facts(resolved).OrderBy(fact => fact.Date).ThenBy(fact => fact.MemoryId, StringComparer.Ordinal).ToArray();
                var neighbors = store.Neighbors(resolved).Select(item => new EntityCount(EntityName(item.Entity), item.Count)).ToArray();
                result = new FactsProfileResult(baseName, FactsOperation.Profile, EntityName(resolved), store.MentionCount(resolved),
                    SharedMemorySnapshot.Timestamp(facts.FirstOrDefault()?.Date), SharedMemorySnapshot.Timestamp(facts.LastOrDefault()?.Date),
                    neighbors.Take(limit).ToArray(), neighbors.Length > limit, facts.Take(limit).Select(fact => CanonicalFactPath(fact.MemoryId)).ToArray(), facts.Length > limit);
            }
            else
            {
                EntityRef[] resolved = entities!.Select(value => ResolveEntity(store, value)).ToArray();
                string[] paths = store.MemoriesFor(resolved).Select(CanonicalFactPath).Order(StringComparer.Ordinal).ToArray();
                result = new FactsIntersectResult(baseName, FactsOperation.Intersect, paths.Take(limit).ToArray(), paths.Length > limit);
            }
            return SharedEnvelope.Success(baseName, result) with { Warnings = Warnings(runtime.Snapshot) };
        });

    private static string RetrievalPreview(string? description, string? excerpt) =>
        "Description (may be a content excerpt in older memories): " + (description is { Length: > 1500 } ? description[..1500] + " (truncated)" : description ?? "") +
        (excerpt is null ? "" : "\nExcerpt: " + excerpt) +
        "\nRecall relevant content with shared_recall for full evidence; this preview is not exhaustive.";

    internal SharedEnvelope Mutate(string baseName, string path, string operation, string[]? content,
        Dictionary<string, string[]>? attributes, string[]? keywords, string? destination, string? reason, int keepRecent, CancellationToken ct, string? description = null) =>
        Invoke(baseName, () =>
        {
            SharedValidation.Text(baseName);
            SharedValidation.Text(path); SharedValidation.Text(destination, true); SharedValidation.Text(reason, true);
            if (operation == "link") SharedValidation.Text(destination);
            SharedValidation.Range(keepRecent, 0, 100000);
            if (operation is "remember" or "append")
            {
                SharedValidation.Content(content, attributes, keywords, operation == "append");
                // Validate the subject limit before selecting or opening any base. Logical-path
                // errors retain their later precedence; this check only adds write-policy bounds.
                if (MemoryAddress.TryParse(path, out var parsed, out _))
                    _ = SharedValidation.Write(parsed, content!, attributes, keywords, operation == "append");
            }
        }, () =>
        {
            SharedBaseRuntime runtime = OpenValidatedBase(baseName, path, destination);
            MemoryAddress address = ParsePersistentAddress(path);
            MemoryAddress? target = destination is null ? null : ParsePersistentAddress(destination);
            MemoryWrite? write = operation is "remember" or "append"
                ? SharedValidation.Write(address, content!, attributes, keywords, operation == "append", description) : null;
            EnsureAvailable(runtime.Snapshot);
            SharedMutationCommit commit = SharedMemoryOperations.Mutate(runtime, operation, address, write, target, keepRecent, ct);
            if (commit.ErrorCode is not null) throw new SharedOperationException(commit.ErrorCode, commit.Mutation);
            if (commit.Mutation == SharedMutationCertainty.Unknown)
                throw new SharedOperationException("DURABLE_WRITE_FAILED", SharedMutationCertainty.Unknown);
            string[] manifest = commit.Manifest.ToArray();
            string? guidance = GuidanceForMutation(runtime.RegisteredRoot, manifest);
            if (reason is not null) guidance = guidance is null ? reason : guidance + "\n" + reason;
            return SharedEnvelope.CreateMutation(baseName, new MutationResult(baseName, address.Value, target?.Value,
                commit.Mutation == SharedMutationCertainty.Applied ? MutationKind.Applied : MutationKind.None, commit.Generation!, manifest), guidance);
        });

    internal SharedEnvelope Refresh(string baseName, CancellationToken ct) => Invoke(baseName, () => SharedValidation.Text(baseName), () =>
    {
        SharedBaseRuntime runtime = OpenValidatedBase(baseName, null);
        ct.ThrowIfCancellationRequested();
        SharedRefreshCommit refreshed = runtime.Refresh();
        EnsureAvailable(refreshed.Snapshot);
        SharedWarning[] warnings = Warnings(refreshed.Snapshot);
        return SharedEnvelope.Success(baseName, new RefreshResult(baseName, refreshed.PreviousGeneration,
            refreshed.Snapshot.Generation!, SharedBaseAvailability.Available, warnings)) with { Warnings = warnings };
    });

    private SharedEnvelope Invoke(string? baseName, Action validate, Func<SharedEnvelope> action, bool aggregate = false)
    {
        bool member = false;
        try
        {
            validate();
            if (baseName is not null)
            {
                SharedValidation.Text(baseName);
                if (!SharedBaseRegistry.IsCanonicalName(baseName)) SharedValidation.Invalid();
                if (!_selections.ContainsKey(baseName)) return Error("SHARED_BASE_NOT_ENABLED");
                member = true;
            }
            return action();
        }
        catch (SharedAddressException exception) { return Error(exception.Code, member && !aggregate ? baseName : null); }
        catch (SharedOperationException exception)
        { return Error(exception.Code, member && !aggregate ? baseName : null, exception.Mutation); }
        catch (InvalidMemoryWriteException) { return Error("INVALID_PARAMETER", member && !aggregate ? baseName : null); }
        catch (Exception exception) when (IsExpected(exception))
        { return Error(exception is ArgumentException ? "INVALID_PATH" : "SHARED_BASE_UNAVAILABLE", member && !aggregate ? baseName : null); }
    }

    private SharedBaseRuntime OpenValidatedBase(string baseName, string? path, string? destination = null)
    {
        SharedBaseEntry entry = _selections.GetValueOrDefault(baseName) ?? throw new SharedOperationException("UNKNOWN_SHARED_BASE");
        if (!Directory.Exists(entry.Root)) throw new SharedOperationException("SHARED_BASE_UNAVAILABLE");
        if (!SharedBaseRegistry.HasGitEvidence(entry.Root)) throw new SharedOperationException("SHARED_BASE_NOT_GIT_ROOT");
        if (path is not null) _ = ParsePersistentAddress(path);
        if (destination is not null) _ = ParsePersistentAddress(destination);
        string ignore = Path.Combine(entry.Root, ".scrinia", ".gitignore");
        if (!File.Exists(ignore) || ManagedIgnoreBlock.Validate(File.ReadAllText(ignore), TrackingPolicy.ProjectGit) != ManagedIgnoreValidation.Valid)
            throw new SharedOperationException("SHARED_TRACKING_POLICY_DRIFT");
        return _runtimeFactory.Open(entry);
    }

    private SharedEnvelope Aggregate(string? baseName, string? path, Func<SharedBaseSnapshot, object[]> project,
        Func<List<object>, (object[] Results, bool? Truncated, SharedPage? Page)> finish, CancellationToken ct,
        bool search = false, SharedPage? page = null)
    {
        var results = new List<object>();
        var warnings = new List<SharedWarning>();
        int succeeded = 0;
        foreach (string name in baseName is null ? _selections.Keys : [baseName])
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                SharedBaseRuntime runtime = OpenValidatedBase(name, path);
                using var lease = runtime.AcquireReadLease();
                EnsureAvailable(runtime.Snapshot);
                results.AddRange(project(runtime.Snapshot));
                warnings.AddRange(Warnings(runtime.Snapshot));
                succeeded++;
            }
            catch (SharedOperationException exception)
            {
                if (baseName is not null) return Error(exception.Code) with { Warnings = [Warning(name, exception.Code)], Truncated = search ? false : null, Page = page };
                warnings.Add(Warning(name, exception.Code));
            }
            catch (SharedAddressException) { throw; }
            catch (Exception exception) when (IsExpected(exception))
            {
                if (baseName is not null) return Error("SHARED_BASE_UNAVAILABLE") with { Warnings = [Warning(name, "SHARED_BASE_UNAVAILABLE")], Truncated = search ? false : null, Page = page };
                warnings.Add(Warning(name, "SHARED_BASE_UNAVAILABLE"));
            }
        }
        if (succeeded == 0) return Error("SHARED_BASE_UNAVAILABLE") with { Error = "No selected shared base is available.",
            Instruction = "Restore an enabled base and restart or refresh it.", Warnings = warnings.ToArray(), Truncated = search ? false : null, Page = page };
        var finished = finish(results);
        return new SharedEnvelope { Status = warnings.Count == 0 ? "success" : "partial", Results = finished.Results,
            Warnings = warnings.ToArray(), Truncated = finished.Truncated, Page = finished.Page };
    }

    private static ListItem ListEntry(SharedBaseIdentity identity, SharedMemorySnapshot item, string mode)
    {
        FileDriftRef[]? files = null;
        SharedDrift? drift = null;
        if (mode == "drift")
        {
            files = (item.Entry?.FileHashes ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                string? actual = null;
                if (Path.IsPathFullyQualified(pair.Key) || pair.Key.StartsWith('/') || pair.Key.StartsWith('\\') ||
                    pair.Key.Contains(':') || pair.Key.Any(char.IsControl)) throw new SharedOperationException("INVALID_PATH");
                string path = Path.GetFullPath(Path.Combine(identity.RegisteredRoot, pair.Key));
                if (!Path.IsPathFullyQualified(pair.Key) && PathContainment.IsWithin(identity.RegisteredRoot, path) && File.Exists(path))
                    actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                return new FileDriftRef(pair.Key, pair.Value, actual);
            }).ToArray();
            drift = files.Any(file => file.ActualSha256 is null) ? SharedDrift.Missing
                : files.Any(file => file.ActualSha256 != file.ExpectedSha256) ? SharedDrift.Changed : SharedDrift.Unchanged;
        }
        return new(identity.BaseName, item.Path, SharedListKind.Persistent, item.Created, item.Updated,
            item.Entry?.ContentPreview ?? item.Skill?.Description ?? item.Agent?.Description, item.Attributes, item.Keywords, drift, files);
    }

    private static string CanonicalFactPath(string value) => value.StartsWith('/') ? value : "/" + value;
    private static string EntityName(EntityRef entity) => entity.Key + ":" + entity.Value;
    private static EntityRef ResolveEntity(EntityFactStore store, string value) => FactsQuery.Resolve(store, value) switch
    {
        EntityResolution.Resolved resolved => resolved.Entity,
        EntityResolution.Ambiguous => throw new SharedOperationException("INVALID_PARAMETER"),
        _ => throw new SharedOperationException("SHARED_ENTITY_NOT_FOUND")
    };
    private static void EnsureAvailable(SharedBaseSnapshot snapshot)
    { if (snapshot.Availability != SharedBaseAvailability.Available) throw new SharedOperationException(AvailabilityCode(snapshot.Availability)); }
    private static string AvailabilityCode(SharedBaseAvailability availability) => availability switch
    { SharedBaseAvailability.Stale => "SHARED_BASE_STALE", SharedBaseAvailability.Conflicted => "SHARED_BASE_CONFLICTED", _ => "SHARED_BASE_UNAVAILABLE" };
    private static bool IsExpected(Exception exception) => exception is SharedOperationException or IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException;
    private static SharedWarning Warning(string name, string code) => new(name, code, Diagnostic(code));
    private static SharedWarning[] Warnings(SharedBaseSnapshot snapshot) => snapshot.Warnings.Select(warning =>
    {
        string code = warning.Split(':')[0];
        if (code is not ("SHARED_ENTRY_CORRUPT" or "SHARED_FORMAT_UNSUPPORTED")) code = "SHARED_BASE_UNAVAILABLE";
        return Warning(snapshot.Identity.BaseName, code);
    }).ToArray();
    private static SharedEnvelope Error(string code, string? baseName = null, SharedMutationCertainty mutation = SharedMutationCertainty.None) =>
        SharedEnvelope.CreateError(code, Diagnostic(code), baseName: baseName,
            mutation: mutation == SharedMutationCertainty.Unknown ? MutationCertainty.Unknown : MutationCertainty.None,
            instruction: mutation == SharedMutationCertainty.Unknown
                ? "The operation may have changed files. Call shared_refresh for this base, then inspect the affected entries before deciding whether to retry."
                : code == "SHARED_BASE_STALE" ? "Refresh this base before another mutation."
                : code == "SHARED_BASE_UNSAFE_PATH" ? "Repair the shared base topology, then call shared_refresh."
                : code == "SHARED_BASE_NOT_ENABLED" ? "Enable the base in this workspace before selecting it." : null);
    private static string Diagnostic(string code) => code switch
    {
        "SHARED_BASE_NOT_ENABLED" => "The selected base is not enabled for this workspace.",
        "UNKNOWN_SHARED_BASE" => "The enabled shared base is absent from the registry.",
        "SHARED_BASE_STALE" => "The shared base has changed. Refresh it before another mutation.",
        "SHARED_BASE_CONFLICTED" => "Resolve the shared base conflict, then refresh it.",
        "SHARED_BASE_NOT_GIT_ROOT" => "The shared base root has no valid Git repository marker.",
        "SHARED_TRACKING_POLICY_DRIFT" => "Restore the project-git managed block in this shared base's .scrinia/.gitignore, then call shared_refresh.",
        "SHARED_EPHEMERAL_NOT_SUPPORTED" => "Shared bases accept persistent addresses only.",
        "SHARED_ENTRY_NOT_FOUND" => "The requested shared entry or chunk was not found.",
        "SHARED_ENTITY_NOT_FOUND" => "The requested entity was not found.",
        "SHARED_FORMAT_READ_ONLY" => "The shared entry uses a newer read-only sidecar format.",
        "SHARED_FORMAT_UNSUPPORTED" => "The shared entry uses an unsupported format.",
        "SHARED_ENTRY_CORRUPT" => "The shared entry could not be validated.",
        "DURABLE_WRITE_FAILED" => "The durable write outcome could not be verified.",
        "INVALID_PARAMETER" => "The request is invalid.",
        "MALFORMED_ADDRESS" => "The logical memory address is malformed.",
        "SHARED_BASE_UNSAFE_PATH" => "The shared base topology is unsafe. Repair it, then call shared_refresh.",
        "INVALID_PATH" => "The requested path is unsafe.",
        _ => "The enabled base could not be opened."
    };
}
