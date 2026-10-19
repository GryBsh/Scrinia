using System.Text.Json;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Bundles;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    private static FileEntityTarget? LinkFileTarget(IMemoryStore store, MemoryAddress address)
    {
        if (address.Kind != MemoryAddressKind.Persistent) return null;
        string path = address.ToString();
        string? kind = path.StartsWith("/agent/", StringComparison.OrdinalIgnoreCase) ? "agent"
            : path.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) ? "skill" : null;
        if (kind is null) return null;
        string name = path[(kind.Length + 2)..];
        if (!PathRouter.TryToFileEntityPath(kind, name, store.WorkspaceRoot, out string body))
            throw new ArgumentException("Link endpoints under /agent/ and /skill/ require a valid file name.");
        return new(kind, name, body, Path.ChangeExtension(body, ".meta.json"),
            Path.Combine(Path.GetDirectoryName(body)!, "versions"));
    }

    private static bool LinkEndpointExists(
        IMemoryStore store, MemoryAddress address, MemoryAddress destination, CancellationToken ct)
    {
        FileEntityTarget? target = LinkFileTarget(store, address);
        if (target is null) return Exists(store, address);
        using WorkspaceLease lease = EnterFileEntityAdmission(store, mutation: false, ct);
        if (lease.CapturedAbsentWorkspace || !File.Exists(target.BodyPath))
        {
            lease.Dispose();
            return target.EntityKind == "agent" && Exists(store, address);
        }

        // Validate both sidecars before either endpoint is changed. Read again under the
        // entity lock during publication so concurrent reference additions are not lost.
        bool exists = File.Exists(target.MetaPath);
        var metadata = RawMemberSnapshot.Capture(target.MetaPath, exists,
            exists ? File.ReadAllBytes(target.MetaPath) : []);
        _ = LinkedFileMetadata(target, metadata, destination);
        return true;
    }

    private async Task<McpResponse> LinkEndpoint(
        MemoryAddress address, MemoryAddress destination, CancellationToken ct)
    {
        var store = CurrentStore;
        FileEntityTarget? target = LinkFileTarget(store, address);
        if (target is null)
            return await UpdateMeta(address, attributes: ReferenceTo(destination), cancellationToken: ct);

        try
        {
            FileEntityMutationResult result = await new FileEntityTransactionCoordinator(store.WorkspaceRoot)
                .TransformAsync(target, (current, _) =>
                {
                    if (!current.Body.Existed)
                        throw new FileNotFoundException($"Memory '{address}' no longer exists.");
                    byte[] metadata = LinkedFileMetadata(target, current.Meta, destination);
                    return ValueTask.FromResult(new FileEntityMutation(
                        target.EntityKind, target.Name, target.BodyPath, target.MetaPath,
                        target.VersionsDirectory, current.Body.Bytes, metadata, false, false));
                }, ct);
            return FileEntitySuccessResponse(result, $"Linked '{address}' to '{destination}'.",
                "linked", $"{target.EntityKind}/{target.Name}");
        }
        catch (FileNotFoundException)
        {
            // Older agents may still be indexed NMP entries; Markdown wins when present.
            return target.EntityKind == "agent"
                ? await UpdateMeta(address, attributes: ReferenceTo(destination), cancellationToken: ct)
                : ResponseBuilder.Error($"Memory '{address}' no longer exists.", ErrorCodes.NotFound);
        }
        catch (WholeMemoryTransactionException exception)
        {
            return WholeMemoryFailureResponse(exception, $"{target.EntityKind}/{target.Name}");
        }
    }

    private static byte[] LinkedFileMetadata(
        FileEntityTarget target, RawMemberSnapshot snapshot, MemoryAddress destination)
    {
        string now = DateTimeOffset.UtcNow.ToString("o");
        if (target.EntityKind == "agent")
        {
            AgentFileMeta metadata = ReadSidecarSnapshotOrRefuse(
                snapshot, target.BodyPath, ScriniaMcpJsonContext.Default.AgentFileMeta)
                ?? new AgentFileMeta(now, now);
            metadata = metadata with
            {
                UpdatedAt = now,
                Unknown = LinkedFileReferences(metadata.Unknown, snapshot, destination)
            };
            return JsonSerializer.SerializeToUtf8Bytes(metadata, ScriniaMcpJsonContext.Default.AgentFileMeta);
        }

        SkillFileMeta skill = ReadSidecarSnapshotOrRefuse(
            snapshot, target.BodyPath, ScriniaMcpJsonContext.Default.SkillFileMeta)
            ?? new SkillFileMeta(null, null, null, null, now, now);
        skill = skill with
        {
            UpdatedAt = now,
            Unknown = LinkedFileReferences(skill.Unknown, snapshot, destination)
        };
        return JsonSerializer.SerializeToUtf8Bytes(skill, ScriniaMcpJsonContext.Default.SkillFileMeta);
    }

    private static Dictionary<string, JsonElement> LinkedFileReferences(
        Dictionary<string, JsonElement>? existing, RawMemberSnapshot snapshot, MemoryAddress destination)
    {
        // File entities use the same top-level ref extension as shared links. Preserve
        // unrelated extensions and reject ambiguous/corrupt references before publication.
        var fields = existing?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var references = new HashSet<string>(StringComparer.Ordinal);
        if (snapshot.Existed)
        {
            using var document = JsonDocument.Parse(DecodeFileEntityBody(snapshot.Bytes));
            var members = document.RootElement.EnumerateObject()
                .Where(property => property.Name.Equals("ref", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (members.Length > 1) throw InvalidReferences();
        }
        string[] keys = fields.Keys.Where(key => key.Equals("ref", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (keys.Length == 1)
        {
            JsonElement value = fields[keys[0]];
            if (value.ValueKind != JsonValueKind.Array) throw InvalidReferences();
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String ||
                    !MemoryAddress.TryParse(item.GetString(), out var address, out _) ||
                    address.ToString() != item.GetString() || !references.Add(address.ToString()))
                    throw InvalidReferences();
            }
            fields.Remove(keys[0]);
        }
        references.Add(destination.ToString());
        if (references.Count > 1000) throw InvalidReferences();
        fields["ref"] = JsonSerializer.SerializeToElement(
            references.Order(StringComparer.Ordinal).ToArray(), ScriniaMcpJsonContext.Default.StringArray);
        return fields;

        DurableWriteException InvalidReferences() => new("link metadata", snapshot.Path,
            new InvalidDataException("File-entity references must be a unique array of canonical memory paths (at most 1,000)."));
    }
}
