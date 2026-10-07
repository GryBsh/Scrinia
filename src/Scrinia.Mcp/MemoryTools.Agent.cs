using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.Mcp.SharedContracts;
using System.Text;
using System.Text.Json;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    internal async Task<McpResponse> WriteAgentAsync(
        string subject, string[]? content, CancellationToken cancellationToken)
    {
        if (!FileEntityName.IsValid(subject))
            return ResponseBuilder.Error(
                "Invalid agent config name. Use letters, digits, '-', '_' or '.' only.",
                ErrorCodes.InvalidParameter,
                "Use a path like '/agent/profile' for the agent config.");
        if (content is not { Length: > 0 })
            return ResponseBuilder.Error("A reserved-path write requires non-empty content.", ErrorCodes.InvalidParameter);

        var store = CurrentStore;
        string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        if (!PathRouter.TryToFileEntityPath("agent", subject, store.WorkspaceRoot, out string filePath))
            return ResponseBuilder.Error("Invalid agent config name.", ErrorCodes.InvalidParameter);

        using WorkspaceLease lease = EnterFileEntityAdmission(store, mutation: true, cancellationToken);
        FileMemoryStore.EnsureGitIgnore(store.WorkspaceRoot);
        Directory.CreateDirectory(agentDir);
        var existingMeta = ReadSidecarMetaOrRefuse(filePath, ScriniaMcpJsonContext.Default.AgentFileMeta);
        cancellationToken.ThrowIfCancellationRequested();

        string body = string.Join("\n", content);
        string now = DateTimeOffset.UtcNow.ToString("o");
        var metadata = new AgentFileMeta(
            existingMeta?.CreatedAt ?? now,
            now,
            existingMeta?.SchemaVersion)
        {
            Unknown = existingMeta?.Unknown is { } unknown
                ? new Dictionary<string, JsonElement>(unknown)
                : null
        };

        try
        {
            FileEntityMutationResult result = await PublishFileEntityAsync(
                "agent", subject, filePath, Path.ChangeExtension(filePath, ".meta.json"),
                Path.Combine(agentDir, "versions"), Encoding.UTF8.GetBytes(body),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, ScriniaMcpJsonContext.Default.AgentFileMeta)),
                false, false, cancellationToken);
            return FileEntitySuccessResponse(
                result, $"Remembered: agent:{subject} ({FormatBytes(Encoding.UTF8.GetByteCount(body))}).",
                "stored", $"agent/{subject}").WithPath($"/agent/{subject}");
        }
        catch (WholeMemoryTransactionException exception)
        {
            return WholeMemoryFailureResponse(exception, $"agent/{subject}");
        }
    }

    internal async Task<McpResponse> AppendAgentAsync(
        string subject, string[]? content, CancellationToken cancellationToken)
    {
        if (content is not { Length: > 0 })
            return ResponseBuilder.Error("A reserved-path append requires non-empty content.", ErrorCodes.InvalidParameter);

        var store = CurrentStore;
        if (!PathRouter.TryToFileEntityPath("agent", subject, store.WorkspaceRoot, out string filePath))
            return ResponseBuilder.Error("Invalid agent config name.", ErrorCodes.InvalidParameter);
        string directory = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        var target = new FileEntityTarget("agent", subject, filePath,
            Path.ChangeExtension(filePath, ".meta.json"), Path.Combine(directory, "versions"));
        bool promoted = false;
        try
        {
            FileEntityMutationResult result = await new FileEntityTransactionCoordinator(store.WorkspaceRoot)
                .TransformAsync(target, async (current, ct) =>
                {
                    AgentFileMeta? existing = ReadSidecarSnapshotOrRefuse(
                        current.Meta, filePath, ScriniaMcpJsonContext.Default.AgentFileMeta);
                    RawFileEntityContent<AgentFileMeta> raw = await LoadRawAgentAsync(store, subject,
                        current.Body.Existed ? DecodeFileEntityBody(current.Body.Bytes) : null, existing, ct)
                        ?? throw new FileNotFoundException($"Agent '{subject}' not found.");
                    promoted = raw.Source == FileEntityContentSource.PromotedAgent;
                    string now = DateTimeOffset.UtcNow.ToString("o");
                    AgentFileMeta metadata = raw.Metadata is { } prior
                        ? prior with
                        {
                            UpdatedAt = now,
                            Unknown = prior.Unknown is { } unknown ? new Dictionary<string, JsonElement>(unknown) : null
                        }
                        : new AgentFileMeta(now, now);
                    string updated = raw.Body + "\n" + string.Join("\n", content);
                    byte[] body = Encoding.UTF8.GetBytes(updated);
                    byte[] meta = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                        metadata, ScriniaMcpJsonContext.Default.AgentFileMeta));
                    FileMemoryStore.EnsureGitIgnore(store.WorkspaceRoot);
                    return new FileEntityMutation(target.EntityKind, target.Name,
                        target.BodyPath, target.MetaPath, target.VersionsDirectory, body, meta, false, false,
                        sourceBodyToArchive: raw.SourceBodyToArchive);
                }, cancellationToken);
            if (promoted && result.Outcome is WholeMemoryOutcome.Created or WholeMemoryOutcome.Replaced)
            {
                using WorkspaceLease lease = EnterFileEntityAdmission(store, mutation: true, cancellationToken);
                MemoryAddress address = MemoryAddress.Parse($"/agent/{subject}");
                (string scope, string legacySubject) = store.ResolveAddress(address);
                store.Remove(address);
                store.DeleteArtifact(legacySubject, scope);
            }
            return FileEntitySuccessResponse(result,
                $"Appended to /agent/{subject}.", "appended", $"agent/{subject}");
        }
        catch (FileNotFoundException exception)
        {
            return ResponseBuilder.Error(exception.Message, ErrorCodes.NotFound,
                "remember({ path: '/agent/profile', content: [...] })");
        }
        catch (WholeMemoryTransactionException exception)
        {
            return WholeMemoryFailureResponse(exception, $"agent/{subject}");
        }
    }

    internal async Task<McpResponse> ReadAgentAsync(string subject, CancellationToken cancellationToken)
    {
        if (!FileEntityName.IsValid(subject))
            return ResponseBuilder.Error("Invalid agent config name.", ErrorCodes.InvalidParameter);

        var store = CurrentStore;
        using WorkspaceLease lease = EnterFileEntityAdmission(store, mutation: false, cancellationToken);
        if (!PathRouter.TryToFileEntityPath("agent", subject, store.WorkspaceRoot, out string filePath))
            return ResponseBuilder.Error("Invalid agent config name.", ErrorCodes.InvalidParameter);
        if (lease.CapturedAbsentWorkspace)
            return ResponseBuilder.Error($"Agent '{subject}' not found.", ErrorCodes.NotFound);
        if (!File.Exists(filePath))
        {
            lease.Dispose();
            using WorkspaceLease mutation = EnterFileEntityAdmission(store, mutation: true, cancellationToken);
            MemoryAddress address = MemoryAddress.Parse($"/agent/{subject}");
            if (await PromoteLocalAgentAsync(store, address, subject, cancellationToken) && File.Exists(filePath))
            {
                string promoted = await File.ReadAllTextAsync(filePath, cancellationToken);
                SessionBudget.RecordAccess($"/agent/{subject}", promoted.Length);
                return ResponseBuilder.Success(promoted).WithPath($"/agent/{subject}").WithAction("loaded");
            }

            return ResponseBuilder.Error(
                $"Agent '{subject}' not found.", ErrorCodes.NotFound,
                "remember({ path: '/agent/profile', content: [...] })");
        }

        RawFileEntityContent<AgentFileMeta>? raw = await LoadRawAgentAsync(store, subject,
            await File.ReadAllTextAsync(filePath, cancellationToken),
            ReadSidecarMeta(filePath, ScriniaMcpJsonContext.Default.AgentFileMeta), cancellationToken);
        string content = (raw ?? throw new InvalidOperationException("The captured agent body is missing.")).Body;
        SessionBudget.RecordAccess($"/agent/{subject}", content.Length);
        return ResponseBuilder.Success(content).WithPath($"/agent/{subject}").WithAction("loaded");
    }

    internal McpResponse ListAgents()
    {
        using WorkspaceLease lease = EnterFileEntityAdmission(CurrentStore, mutation: false, CancellationToken.None);
        string agentDir = PathRouter.ToFileEntityDir("agent", CurrentStore.WorkspaceRoot);
        string[] names = !lease.CapturedAbsentWorkspace && Directory.Exists(agentDir)
            ? Directory.GetFiles(agentDir, "*.md")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null)
                .Cast<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        string content = names.Length == 0
            ? "No agents available."
            : "Available agents:\n\n" + string.Join('\n', names.Select(name => $"- /agent/{name}"));
        return ResponseBuilder.Success(content).WithAction("listed");
    }

    internal async Task<McpResponse> DeleteAgentAsync(string subject, CancellationToken cancellationToken)
    {
        if (!FileEntityName.IsValid(subject))
            return ResponseBuilder.Error("Invalid agent config name.", ErrorCodes.InvalidParameter);

        var store = CurrentStore;
        using WorkspaceLease lease = EnterFileEntityAdmission(store, mutation: true, cancellationToken);
        if (!PathRouter.TryToFileEntityPath("agent", subject, store.WorkspaceRoot, out string filePath))
            return ResponseBuilder.Error("Invalid agent config name.", ErrorCodes.InvalidParameter);
        string metaPath = Path.ChangeExtension(filePath, ".meta.json");
        if (!File.Exists(filePath) && !File.Exists(metaPath))
            return ResponseBuilder.Error($"Agent '{subject}' not found.", ErrorCodes.NotFound);

        string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        string versionsDir = Path.Combine(agentDir, "versions");
        string[] versionPaths = Directory.Exists(versionsDir)
            ? Directory.EnumerateFiles(versionsDir, subject + "_*")
                .Where(path => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
        try
        {
            FileEntityMutationResult result = await PublishFileEntityAsync(
                "agent", subject, filePath, metaPath,
                versionsDir, [], [], true, true, cancellationToken, versionPaths);
            return FileEntitySuccessResponse(
                result, $"Forgot /agent/{subject}.", "forgotten", $"agent/{subject}")
                .WithPath($"/agent/{subject}");
        }
        catch (WholeMemoryTransactionException exception)
        {
            return WholeMemoryFailureResponse(exception, $"agent/{subject}");
        }
    }

    /// <summary>
    /// Auto-promotes a legacy <c>local:agent</c> NMP/2 entry to a first-class
    /// <c>.scrinia/agent/{subject}.md</c> file entity on first touch, then removes the legacy
    /// artifact. Mirrors the <c>/skill/</c> disk-first resolution so <c>/agent/</c> and
    /// <c>/skill/</c> answer the same way once the legacy data is gone.
    ///
    /// <para>Promotion is TWO ORDERED STEPS, matching the <c>remember</c> file-entity publish:
    /// <enumerate>
    /// <item>publish the <c>.md</c> body plus its <c>.meta.json</c> sidecar through the shared
    /// <see cref="FileEntityTransactionCoordinator"/>;</item>
    /// <item>on <c>Created</c>/<c>Replaced</c> only, delete the legacy artifact so it can never be
    /// served again.</item>
    /// </enumerate>
    /// The second read of a promoted agent finds the published file on disk and returns it
    /// directly, so promotion is idempotent and never re-deletes. Returns <c>true</c> only when the
    /// publish succeeded and the legacy entry was removed; <c>false</c> when there is no legacy
    /// entry (or the publish fails), leaving the fall-through NMP/2 resolution untouched.
    ///
    /// <para>Shared with the bulk sweep in the follow-up task — the single promotion primitive for
    /// every legacy <c>local:agent</c> entry.</para>
    /// </summary>
    internal static async Task<bool> PromoteLocalAgentAsync(
        IMemoryStore store, MemoryAddress address, string agentSubject, CancellationToken cancellationToken)
    {
        RawFileEntityContent<AgentFileMeta>? raw = await LoadRawAgentAsync(
            store, agentSubject, null, null, cancellationToken);
        if (raw is null)
            return false;
        byte[] body = Encoding.UTF8.GetBytes(raw.Body);
        string dir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        string bodyPath = Path.Combine(dir, agentSubject + ".md");
        string metaPath = Path.ChangeExtension(bodyPath, ".meta.json");
        string versionsDir = Path.Combine(dir, "versions");

        FileEntityMutationResult publish;
        try
        {
            publish = await new FileEntityTransactionCoordinator(store.WorkspaceRoot)
                .ExecuteAsync(
                    new FileEntityMutation("agent", agentSubject, bodyPath, metaPath, versionsDir, body, null, false, false),
                    cancellationToken);
        }
        catch
        {
            return false;
        }

        if (publish.Outcome is not (WholeMemoryOutcome.Created or WholeMemoryOutcome.Replaced))
            return false;

        (string scope, string subject) = store.ResolveAddress(address);
        store.Remove(address);
        store.DeleteArtifact(subject, scope);
        return true;
    }
}
