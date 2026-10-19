using System.Globalization;
using System.Text;
using System.Text.Json;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Core.Configuration;

internal sealed record SharedMemorySnapshot(string Path, string BodyFile, string MetaFile,
    string[] Chunks, ArtifactEntry? Entry, SkillFileMeta? Skill, AgentFileMeta? Agent,
    SharedDurableAvailability Availability, bool InvalidReferences = false)
{
    internal bool IsFileEntity => Entry is null;
    internal Dictionary<string, string[]> Attributes => Entry?.Attributes ?? [];
    internal string[] Keywords => Entry?.Keywords ?? [];
    internal string? Created => Entry is not null ? Timestamp(Entry.CreatedAt) : Skill?.CreatedAt ?? Agent?.CreatedAt;
    internal string? Updated => Entry is not null ? Timestamp(Entry.UpdatedAt) : Skill?.UpdatedAt ?? Agent?.UpdatedAt;
    internal static string? Timestamp(DateTimeOffset? value) => value?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

internal sealed class SharedOperationException(string code, SharedMutationCertainty mutation = SharedMutationCertainty.None)
    : InvalidOperationException(code)
{
    internal string Code { get; } = code;
    internal SharedMutationCertainty Mutation { get; } = mutation;
}

/// <summary>Adapts shared generation operations to the existing durable store boundaries.</summary>
internal static class SharedMemoryOperations
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static IReadOnlyList<SharedMemorySnapshot> Capture(string root, IReadOnlyList<SharedDurableEntry> inventory)
    {
        var entries = new List<SharedMemorySnapshot>();
        foreach (SharedDurableEntry item in inventory.Where(item => item.Availability != SharedDurableAvailability.Unavailable))
        {
            string member = Path.Combine(root, ".scrinia", item.RelativeFile.Replace('/', Path.DirectorySeparatorChar));
            if (item.RelativeFile.StartsWith("memories/", StringComparison.Ordinal))
            {
                ArtifactEntry entry = JsonSerializer.Deserialize(File.ReadAllBytes(member), SharedDurableJsonContext.Default.ArtifactEntry)!;
                string body = member[..^".meta.json".Length] + ".nmp2";
                string[] chunks = Nmp2ChunkedEncoder.DecodeAllChunksVerified(StrictUtf8.GetString(File.ReadAllBytes(body))).Chunks;
                entries.Add(new(entry.Address.Value, body, member, chunks, entry.NormalizeAttributesKey(), null, null, item.Availability));
            }
            else
            {
                string sidecar = Path.ChangeExtension(member, ".meta.json");
                SkillFileMeta? skill = null;
                AgentFileMeta? agent = null;
                bool invalidReferences = false;
                if (File.Exists(sidecar))
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(sidecar);
                        using var document = JsonDocument.Parse(bytes);
                        invalidReferences = document.RootElement.ValueKind == JsonValueKind.Object &&
                            document.RootElement.EnumerateObject().Count(property => property.Name.Equals("ref", StringComparison.OrdinalIgnoreCase)) > 1;
                        if (item.Path.StartsWith("/skill/", StringComparison.Ordinal))
                            skill = JsonSerializer.Deserialize(bytes, FileEntityJsonContext.Default.SkillFileMeta);
                        else agent = JsonSerializer.Deserialize(bytes, FileEntityJsonContext.Default.AgentFileMeta);
                    }
                    catch (JsonException) { /* malformed file-entity metadata has the specified absent defaults */ }
                }
                entries.Add(new(item.Path, member, sidecar, [StrictUtf8.GetString(File.ReadAllBytes(member))],
                    null, skill, agent, item.Availability, invalidReferences));
            }
        }
        return entries.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    internal static SharedMemorySnapshot RequireEntry(SharedBaseSnapshot snapshot, MemoryAddress address)
    {
        SharedDurableEntry? unavailable = snapshot.Entries.FirstOrDefault(item =>
            item.Path.Equals(address.Value, StringComparison.OrdinalIgnoreCase) && item.Availability == SharedDurableAvailability.Unavailable);
        if (unavailable is not null) throw new SharedOperationException(unavailable.ErrorCode ?? "SHARED_ENTRY_CORRUPT");
        return snapshot.Memories.FirstOrDefault(item => item.Path.Equals(address.Value, StringComparison.OrdinalIgnoreCase))
            ?? throw new SharedOperationException("SHARED_ENTRY_NOT_FOUND");
    }

    internal static IReadOnlyList<SearchResult> Search(SharedBaseSnapshot snapshot, SearchQuery query)
    {
        ScopedArtifact[] entries = Candidates(snapshot, query.ExcludeTopics);
        var candidates = entries.Where(entry => SearchResultFilter.CandidateMatches(entry,
            query.PathPrefix, query.AttributeFilter, MemoryNaming.FormatQualifiedName)).ToArray();
        // Shared search exposes only entries and chunks; topic summaries must not consume its limit.
        return new WeightedAttributeScorer(RankerOptions.Default).SearchAll(query.Query, candidates, [], query.Limit);
    }

    internal static ScopedArtifact[] Candidates(SharedBaseSnapshot snapshot, IReadOnlyList<string>? excludeTopics) =>
        snapshot.Memories.Where(item => item.Entry is not null && !Excluded(item.Path, excludeTopics))
            .Select(item => new ScopedArtifact(Scope(item.Path), item.Entry!)).ToArray();

    internal static bool Excluded(string path, IReadOnlyList<string>? topics) =>
        topics?.Any(topic => path.StartsWith("/" + topic.Trim('/') + "/", StringComparison.OrdinalIgnoreCase)) == true;

    private static string Scope(string path) => path.LastIndexOf('/') == 0 ? "local" : "local:" + path[1..path.LastIndexOf('/')];

    internal static SharedMutationCommit Mutate(SharedBaseRuntime runtime, string operation, MemoryAddress address,
        MemoryWrite? write, MemoryAddress? destination, int keepRecent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (address.Value.Split('/').Skip(1).SkipLast(1).Any(segment =>
            segment.Equals("versions", StringComparison.OrdinalIgnoreCase)))
            throw new SharedOperationException("INVALID_PATH");
        using SharedBaseMutationLease lease = runtime.AcquireMutationLease();
        // Logical slash addresses are not physical paths. Validate root/freshness now;
        // CommitMutationUnderLease checks the resolved physical targets before publication.
        SharedMutationPreflight preflight = runtime.RevalidateBeforeMutation(null, null);
        if (!preflight.IsSafe) throw new SharedOperationException(preflight.ErrorCode!);
        SharedBaseSnapshot snapshot = runtime.Snapshot;
        SharedMemorySnapshot? existing = null;
        try { existing = RequireEntry(snapshot, address); }
        catch (SharedOperationException exception) when (exception.Code == "SHARED_ENTRY_NOT_FOUND" && operation is "remember" or "append") { }
        if (existing is not null && existing.Availability == SharedDurableAvailability.ReadOnly &&
            (operation != "forget" || existing.IsFileEntity))
            throw new SharedOperationException("SHARED_FORMAT_READ_ONLY");
        SharedMemorySnapshot? other = destination is { } target ? RequireEntry(snapshot, target) : null;
        if (other?.Availability == SharedDurableAvailability.ReadOnly) throw new SharedOperationException("SHARED_FORMAT_READ_ONLY");
        if (operation == "link")
        {
            if (existing!.IsFileEntity) _ = EntityReferences(existing, other!.Path);
            if (other!.IsFileEntity) _ = EntityReferences(other, existing.Path);
        }
        if (operation == "compact" && (existing!.Chunks.Length <= 1 || keepRecent >= existing.Chunks.Length))
            return new() { Identity = snapshot.Identity, Mutation = SharedMutationCertainty.None, Generation = snapshot.Generation };

        // Generated rewrites can exceed write-policy bounds even when each stored chunk is valid.
        // Prepare both link endpoints before any publication so validation is a known no-write.
        MemoryWrite? operationWrite = write;
        MemoryWrite? firstLinkWrite = null;
        MemoryWrite? secondLinkWrite = null;
        if (operation == "compact")
        {
            string[] compacted = keepRecent == 0 ? [string.Concat(existing!.Chunks)]
                : [string.Concat(existing!.Chunks[..^keepRecent]), .. existing.Chunks[^keepRecent..]];
            operationWrite = Rewrite(existing!, compacted, existing!.Attributes);
        }
        if (operation == "link")
        {
            if (!existing!.IsFileEntity) firstLinkWrite = PrepareLinkWrite(existing, other!);
            if (!other!.IsFileEntity) secondLinkWrite = PrepareLinkWrite(other, existing);
        }

        var pair = Paths(runtime, address, existing);
        var second = other is null ? default : Paths(runtime, destination!.Value, other);
        string[] manifest = operation == "forget"
            ? new[] { pair.Body, pair.Meta }.Where(File.Exists).ToArray()
            : other is null ? [pair.Body, pair.Meta] : [pair.Body, pair.Meta, second.Body, second.Meta];
        foreach (string file in manifest)
            if (!SharedBaseRuntime.IsAuthoritative(Path.GetRelativePath(Path.Combine(runtime.RegisteredRoot, ".scrinia"), file)))
                throw new SharedOperationException("INVALID_PATH");
        var plan = new SharedMutationPlan
        {
            Identity = snapshot.Identity, CanonicalPath = pair.Body, CanonicalDestination = other is null ? null : second.Body,
            AuthoritativeFiles = manifest.Select(file => Path.GetRelativePath(runtime.RegisteredRoot, file)).ToArray()
        };
        return runtime.CommitMutationUnderLease(plan, () =>
        {
            if (operation == "forget")
            {
                if (existing!.IsFileEntity) PublishEntity(runtime, lease.Admission, address, existing, null, true, ct);
                else runtime.Store.DeleteShared(lease.Admission, MemoryAddress.Parse(existing.Path), ct);
                return;
            }
            if (operation == "link")
            {
                LinkOne(existing!, other!, firstLinkWrite);
                if (!existing!.Path.Equals(other!.Path, StringComparison.OrdinalIgnoreCase))
                {
                    try { LinkOne(other, existing, secondLinkWrite); }
                    catch { throw new SharedOperationException("DURABLE_WRITE_FAILED", SharedMutationCertainty.Unknown); }
                }
                return;
            }
            if (IsFileEntity(address))
            {
                string body = string.Concat(operationWrite!.RawContent);
                if (operation == "append" && existing is not null) body = string.Concat(existing.Chunks) + "\n" + body;
                PublishEntity(runtime, lease.Admission, address, existing, body, false, ct, description: operationWrite.Description);
            }
            else if (operation == "append") runtime.Store.AppendShared(lease.Admission, operationWrite!, ct);
            else runtime.Store.StoreShared(lease.Admission, operationWrite!, ct);

            void LinkOne(SharedMemorySnapshot source, SharedMemorySnapshot target, MemoryWrite? prepared)
            {
                if (source.IsFileEntity)
                {
                    PublishEntity(runtime, lease.Admission, MemoryAddress.Parse(source.Path), source,
                        string.Concat(source.Chunks), false, ct, EntityReferences(source, target.Path));
                }
                else
                {
                    runtime.Store.StoreShared(lease.Admission, prepared!, ct);
                }
            }
        });
    }

    private static MemoryWrite PrepareLinkWrite(SharedMemorySnapshot source, SharedMemorySnapshot target)
    {
        Dictionary<string, string[]> attributes = source.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        attributes["ref"] = attributes.GetValueOrDefault("ref", []).Append(target.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Rewrite(source, source.Chunks, attributes);
    }

    private static MemoryWrite Rewrite(SharedMemorySnapshot source, string[] chunks, Dictionary<string, string[]> attributes) =>
        MemoryWrite.Create(MemoryAddress.Parse(source.Path), chunks,
            attributes.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value),
            source.Entry?.AgentKeywords ?? MemoryDerivation.CuratedKeywords(source.Entry?.Keywords, source.Chunks) ?? [],
            description: source.Entry?.ContentPreview);

    private static bool IsFileEntity(MemoryAddress address) =>
        address.Value.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) || address.Value.StartsWith("/agent/", StringComparison.OrdinalIgnoreCase);

    private static (string Body, string Meta) Paths(SharedBaseRuntime runtime, MemoryAddress address, SharedMemorySnapshot? existing)
    {
        if (existing is not null)
        {
            if (!existing.IsFileEntity)
            {
                var expected = runtime.Store.SharedPaths(MemoryAddress.Parse(existing.Path));
                StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!expected.Body.Equals(existing.BodyFile, comparison) || !expected.Meta.Equals(existing.MetaFile, comparison))
                    throw new SharedOperationException("SHARED_ENTRY_CORRUPT");
            }
            return (existing.BodyFile, existing.MetaFile);
        }
        if (!IsFileEntity(address)) return runtime.Store.SharedPaths(address);
        string[] segments = address.Value.Trim('/').Split('/');
        if (segments.Length != 2) throw new SharedOperationException("INVALID_PATH");
        if (!PathRouter.TryToFileEntityPath(segments[0], segments[1], runtime.RegisteredRoot, out string body))
            throw new SharedOperationException("INVALID_PATH");
        return (body, Path.ChangeExtension(body, ".meta.json"));
    }

    private static void PublishEntity(SharedBaseRuntime runtime, WorkspaceLease lease, MemoryAddress address,
        SharedMemorySnapshot? existing, string? body, bool delete, CancellationToken ct,
        Dictionary<string, JsonElement>? extensions = null, string? description = null)
    {
        var pair = Paths(runtime, address, existing);
        string kind = address.Value.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) ? "skill" : "agent";
        string name = Path.GetFileNameWithoutExtension(pair.Body);
        string now = SharedMemorySnapshot.Timestamp(DateTimeOffset.UtcNow)!;
        byte[] meta = kind == "skill"
            ? JsonSerializer.SerializeToUtf8Bytes(new SkillFileMeta(existing?.Skill?.BasedOn, existing?.Skill?.Role,
                existing?.Skill?.Capabilities, existing?.Skill?.Scaffold, existing?.Skill is { } skill ? skill.CreatedAt : now,
                now, existing?.Skill?.SchemaVersion) { Description = description ?? existing?.Skill?.Description, Unknown = extensions ?? existing?.Skill?.Unknown }, FileEntityJsonContext.Default.SkillFileMeta)
            : JsonSerializer.SerializeToUtf8Bytes(new AgentFileMeta(existing?.Agent is { } agent ? agent.CreatedAt : now,
                now, existing?.Agent?.SchemaVersion) { Description = description ?? existing?.Agent?.Description, Unknown = extensions ?? existing?.Agent?.Unknown }, FileEntityJsonContext.Default.AgentFileMeta);
        var target = new FileEntityTarget(kind, name, pair.Body, pair.Meta, Path.Combine(Path.GetDirectoryName(pair.Body)!, "versions"));
        var coordinator = new FileEntityTransactionCoordinator(runtime.RegisteredRoot);
        FileEntityMutationResult result;
        try
        {
            result = coordinator.TransformUnderAdmissionAsync(target,
                (_, _) => ValueTask.FromResult(new FileEntityMutation(kind, name, pair.Body, pair.Meta, target.VersionsDirectory,
                    body is null ? [] : StrictUtf8.GetBytes(body), meta, delete, delete)), lease, ct).GetAwaiter().GetResult();
        }
        catch (WholeMemoryTransactionException exception) when (exception.Code == WholeMemoryErrorCode.Internal)
        { throw new SharedOperationException("DURABLE_WRITE_FAILED"); }
        if (result.Outcome == WholeMemoryOutcome.RecoveryRequired)
            throw new SharedOperationException("DURABLE_WRITE_FAILED", SharedMutationCertainty.Unknown);
    }

    private static Dictionary<string, JsonElement> EntityReferences(SharedMemorySnapshot entry, string destination)
    {
        if (entry.InvalidReferences) throw new SharedOperationException("SHARED_ENTRY_CORRUPT");
        Dictionary<string, JsonElement> extensions = (entry.Skill?.Unknown ?? entry.Agent?.Unknown ?? [])
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        string[] keys = extensions.Keys.Where(key => key.Equals("ref", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (keys.Length > 1) throw new SharedOperationException("SHARED_ENTRY_CORRUPT");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Length == 1)
        {
            JsonElement value = extensions[keys[0]];
            if (value.ValueKind != JsonValueKind.Array) throw new SharedOperationException("SHARED_ENTRY_CORRUPT");
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !MemoryAddress.TryParse(item.GetString(), out var address, out _) ||
                    address.Kind != MemoryAddressKind.Persistent || address.Value != item.GetString() || !paths.Add(address.Value))
                    throw new SharedOperationException("SHARED_ENTRY_CORRUPT");
            }
            extensions.Remove(keys[0]);
        }
        paths.Add(destination);
        if (paths.Count > 1000) throw new SharedOperationException("INVALID_PARAMETER");
        extensions["ref"] = JsonSerializer.SerializeToElement(paths.Order(StringComparer.Ordinal).ToArray(), SharedDurableJsonContext.Default.StringArray);
        return extensions;
    }
}
