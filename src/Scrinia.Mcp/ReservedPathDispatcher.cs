using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;

namespace Scrinia.Mcp;

internal enum ReservedPathKind
{
    Ordinary,
    Skill,
    Agent,
}

internal enum ReservedPathOperation
{
    Read,
    Write,
    Append,
    Delete,
    List,
    Search,
    Copy,
    Export,
    Import,
    Compact,
}

internal sealed record ReservedPathRequest(
    string[]? Content,
    string? InlineArtifact,
    bool WithBuiltin,
    bool Overwrite);

internal sealed class ReservedPathOperationException : InvalidOperationException
{
    internal ReservedPathOperationException(string message) : base(message) { }
}

/// <summary>Owns the operation policy for the disk-backed reserved namespaces.</summary>
internal sealed class ReservedPathDispatcher
{
    private readonly ScriniaMcpTools _tools;

    internal ReservedPathDispatcher(ScriniaMcpTools tools)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    }

    internal async Task<McpResponse?> DispatchAsync(
        ReservedPathOperation operation,
        MemoryAddress address,
        ReservedPathRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return Classify(address) switch
            {
                ReservedPathKind.Ordinary => null,
                ReservedPathKind.Skill => await DispatchSkillAsync(operation, Subject(address), request, cancellationToken),
                ReservedPathKind.Agent => await DispatchAgentAsync(operation, Subject(address), request, cancellationToken),
                _ => throw new ReservedPathOperationException($"Unsupported reserved path kind for '{address}'."),
            };
        }
        catch (RestoreRecoveryException exception)
        {
            return ScriniaMcpTools.RecoveryBlockedResponse(exception);
        }
        catch (BundleImportException exception)
        {
            return ResponseBuilder.Error(exception, "Resolve retained bundle recovery evidence before retrying.");
        }
    }

    internal static ReservedPathKind Classify(MemoryAddress address)
    {
        if (address.Kind != MemoryAddressKind.Persistent)
            return ReservedPathKind.Ordinary;

        string path = address.ToString();
        int slash = path.IndexOf('/', 1);
        string prefix = slash < 0 ? path[1..] : path[1..slash];
        return prefix switch
        {
            "skill" => ReservedPathKind.Skill,
            "agent" => ReservedPathKind.Agent,
            _ => ReservedPathKind.Ordinary,
        };
    }

    private static string Subject(MemoryAddress address)
    {
        string path = address.ToString();
        int slash = path.IndexOf('/', 1);
        return slash < 0 ? string.Empty : path[(slash + 1)..];
    }

    private async Task<McpResponse> DispatchSkillAsync(
        ReservedPathOperation operation,
        string subject,
        ReservedPathRequest request,
        CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case ReservedPathOperation.Read:
                return await ScriniaMcpTools.SkillLoad(
                    string.IsNullOrWhiteSpace(subject) ? null : subject,
                    request.WithBuiltin,
                    cancellationToken);
            case ReservedPathOperation.List:
            case ReservedPathOperation.Search:
                return await ScriniaMcpTools.SkillLoad(null, request.WithBuiltin, cancellationToken);
            case ReservedPathOperation.Write:
                return await WriteSkillAsync(subject, request, cancellationToken);
            case ReservedPathOperation.Append:
                return await AppendSkillAsync(subject, request, cancellationToken);
            case ReservedPathOperation.Delete:
                return await ScriniaMcpTools.SkillDelete(subject, cancellationToken);
            default:
                return Forbidden(operation, ReservedPathKind.Skill);
        }
    }

    private async Task<McpResponse> DispatchAgentAsync(
        ReservedPathOperation operation,
        string subject,
        ReservedPathRequest request,
        CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case ReservedPathOperation.Read:
                return string.IsNullOrWhiteSpace(subject)
                    ? _tools.ListAgents()
                    : await _tools.ReadAgentAsync(subject, cancellationToken);
            case ReservedPathOperation.List:
            case ReservedPathOperation.Search:
                return _tools.ListAgents();
            case ReservedPathOperation.Write:
                return await _tools.WriteAgentAsync(subject, request.Content, cancellationToken);
            case ReservedPathOperation.Append:
                return await _tools.AppendAgentAsync(subject, request.Content, cancellationToken);
            case ReservedPathOperation.Delete:
                return await _tools.DeleteAgentAsync(subject, cancellationToken);
            default:
                return Forbidden(operation, ReservedPathKind.Agent);
        }
    }

    private static async Task<McpResponse> WriteSkillAsync(
        string subject, ReservedPathRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return ResponseBuilder.Error("A skill path requires a skill name.", ErrorCodes.InvalidParameter);
        if (request.Content is not { Length: > 0 })
            return ResponseBuilder.Error("A reserved-path write requires non-empty content.", ErrorCodes.InvalidParameter);

        return await ScriniaMcpTools.SkillCreate(subject, "custom", string.Join("\n\n", request.Content), null, cancellationToken);
    }

    private static async Task<McpResponse> AppendSkillAsync(
        string subject, ReservedPathRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return ResponseBuilder.Error("An append path requires a skill name.", ErrorCodes.InvalidParameter);
        if (request.Content is not { Length: > 0 })
            return ResponseBuilder.Error("A reserved-path append requires non-empty content.", ErrorCodes.InvalidParameter);

        return await ScriniaMcpTools.AppendSkillAsync(subject, request.Content, cancellationToken);
    }

    private static McpResponse Forbidden(ReservedPathOperation operation, ReservedPathKind kind) =>
        ResponseBuilder.Error(
            $"Operation '{operation}' is not supported for the /{kind.ToString().ToLowerInvariant()}/ reserved namespace.",
            ErrorCodes.ReservedPathOperationForbidden,
            "Use the supported reserved-path operation or a non-reserved memory path.");
}
