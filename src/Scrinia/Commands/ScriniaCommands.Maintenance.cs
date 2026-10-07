using Scrinia.Core;
using Scrinia.Core.Configuration;
using Spectre.Console;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    /// <summary>Clean eligible archive pairs and verified terminal recovery evidence.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="scopes">Comma-separated scopes to maintain (e.g. local,api). Omit for the resolved workspace set.</param>
    /// <param name="batchSize">Batch size from 1 through 50; uses the configured value when omitted.</param>
    /// <param name="continuationToken">Opaque token from a previous batch.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public async Task<int> Maintenance(
        string? workspaceRoot = null,
        string? scopes = null,
        int? batchSize = null,
        string? continuationToken = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        // The CLI owns only the syntactic gate: framing, ASCII, canonical unpadded
        // base64url, and exact decoded size. A failure here builds the typed
        // InvalidContinuation result locally with zero Core service calls.
        if (continuationToken is not null &&
            !MaintenanceContinuationSyntax.TryValidate(continuationToken, out _))
        {
            RenderMaintenanceResult(BuildLocalInvalidContinuation(), json);
            return 1;
        }

        if (batchSize is < 1 or > 50)
        {
            EmitMaintenanceArgumentError("Batch size must be between 1 and 50.", json);
            return 1;
        }

        string[]? scopeList = MemoryNaming.SplitCommaSeparated(scopes);

        WorkspaceSetup.Configure(workspaceRoot);

        MaintenanceRequest request;
        try
        {
            request = new MaintenanceRequest(scopeList, batchSize, null, continuationToken);
        }
        catch (ArgumentException exception)
        {
            EmitMaintenanceArgumentError(exception.Message, json);
            return 1;
        }

        MaintenanceResult result;
        try
        {
            MaintenanceServiceInvoker invoker = MaintenanceInvokerOverride.Value ?? InvokeMaintenanceCoreAsync;
            result = await invoker(
                WorkspaceSetup.Store,
                CoreWorkspaceConfig.Load(WorkspaceSetup.Root),
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EmitMaintenanceArgumentError("Maintenance cancelled.", json);
            return 1;
        }

        RenderMaintenanceResult(result, json);
        return ExitCodeFor(result);
    }

    /// <summary>
    /// Test seam for the single Core invocation. Production leaves this unset so the
    /// handler constructs the shared <see cref="FileMemoryMaintenanceService"/>; tests
    /// install a counting delegate to prove the CLI/local ownership boundary without
    /// cross-test process-wide state (AsyncLocal, mirroring
    /// <c>EmbeddingsHost.OverrideEmbeddingProviderForTest</c>).
    /// </summary>
    internal delegate Task<MaintenanceResult> MaintenanceServiceInvoker(
        IMemoryStore store, IWorkspaceConfig config, MaintenanceRequest request,
        CancellationToken cancellationToken);

    internal static AsyncLocal<MaintenanceServiceInvoker?> MaintenanceInvokerOverride { get; } = new();

    internal static IDisposable OverrideMaintenanceInvokerForTest(MaintenanceServiceInvoker? invoker)
    {
        MaintenanceServiceInvoker? prior = MaintenanceInvokerOverride.Value;
        MaintenanceInvokerOverride.Value = invoker;
        return new RestoreMaintenanceInvoker(prior);
    }

    private sealed class RestoreMaintenanceInvoker(MaintenanceServiceInvoker? prior) : IDisposable
    {
        public void Dispose() => MaintenanceInvokerOverride.Value = prior;
    }

    internal static Task<MaintenanceResult> InvokeMaintenanceCoreAsync(
        IMemoryStore store, IWorkspaceConfig config, MaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        if (store is not FileMemoryStore fileStore)
            throw new InvalidOperationException("Maintenance requires a file-backed workspace store.");
        return new FileMemoryMaintenanceService(fileStore, config).ExecuteAsync(request, cancellationToken);
    }

    /// <summary>
    /// Builds the typed InvalidContinuation result locally for a token that fails the
    /// CLI-owned syntax gate. The shape is identical to the Core service's rejection so
    /// both paths render the same sanitized result.
    /// </summary>
    internal static MaintenanceResult BuildLocalInvalidContinuation() =>
        new(0, 0, 0, false, false, false, false, false, false, true, null,
            Array.Empty<MaintenanceScopeResult>(), ["Invalid continuation."], null);

    /// <summary>True when the result has exactly the InvalidContinuation shape.</summary>
    internal static bool IsInvalidContinuation(MaintenanceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !result.Completed && result.Inspected == 0 && result.DeletedArchives == 0 &&
            result.DeletedRecoveryEvidence == 0 && result.Scopes.Count == 0 &&
            !result.PolicyInvalid && !result.LockContended && !result.PartialFailure &&
            result.ContinuationToken is null && result.PolicyError is null &&
            result.Warnings.Count > 0;
    }

    internal static int ExitCodeFor(MaintenanceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.PolicyInvalid || result.LockContended || result.PartialFailure ||
            IsInvalidContinuation(result))
            return 1;
        return 0;
    }

    internal static CliMaintenanceOutput ToMaintenanceOutput(MaintenanceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new CliMaintenanceOutput(
            result.Inspected, result.DeletedArchives, result.DeletedRecoveryEvidence,
            result.Completed, result.MoreWork, result.RecoveryRetained, result.LockContended,
            result.PolicyInvalid, result.PartialFailure, result.ContinuationToken,
            result.Scopes.Select(ToMaintenanceScopeOutput).ToArray(),
            result.Warnings.ToArray(), result.PolicyError);
    }

    internal static CliMaintenanceScopeOutput ToMaintenanceScopeOutput(MaintenanceScopeResult scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new CliMaintenanceScopeOutput(
            scope.Scope, scope.Inspected, scope.DeletedArchives, scope.DeletedRecoveryEvidence,
            scope.Retained, scope.LockContended, scope.MoreWork, scope.ContinuationToken,
            scope.Warnings.ToArray());
    }

    private static void RenderMaintenanceResult(MaintenanceResult result, bool json)
    {
        if (json)
        {
            WriteJson(ToMaintenanceOutput(result), CliJsonContext.Default.CliMaintenanceOutput);
            return;
        }

        if (result.PolicyInvalid)
            AnsiConsole.MarkupLine($"[red]Maintenance policy invalid:[/] {Markup.Escape(result.PolicyError ?? "invalid policy")}");

        if (IsInvalidContinuation(result))
            AnsiConsole.MarkupLine("[red]Maintenance invalid continuation.[/]");

        AnsiConsole.MarkupLine(
            $"[bold]maintenance[/] — inspected {result.Inspected}, " +
            $"deleted {result.DeletedArchives} archives + {result.DeletedRecoveryEvidence} recovery evidence, " +
            $"completed: {(result.Completed ? "yes" : "no")}");

        foreach (MaintenanceScopeResult scope in result.Scopes)
        {
            string state = scope.LockContended ? ", lock contended" : scope.MoreWork ? ", more work" : "";
            AnsiConsole.MarkupLine(
                $"  [dim]•[/] {Markup.Escape(scope.Scope)} — inspected {scope.Inspected}, " +
                $"deleted {scope.DeletedArchives + scope.DeletedRecoveryEvidence}, retained {scope.Retained}{state}");
        }

        foreach (string warning in result.Warnings)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");

        if (result.ContinuationToken is not null)
        {
            // Plain console write, not AnsiConsole: the token is copy-paste material and
            // must never be line-wrapped, which AnsiConsole applies at console width.
            Console.WriteLine(
                $"continuation: {result.ContinuationToken} — repeat with --continuation-token to continue");
        }
    }

    private static void EmitMaintenanceArgumentError(string message, bool json)
    {
        if (json) WriteJsonError(message);
        else AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
    }
}

/// <summary>
/// The CLI-owned continuation syntax gate. Checks framing (<c>b5c2.</c>), ASCII input,
/// canonical unpadded base64url, and the exact 160-byte decoded size only. It never
/// compares a MAC or interprets cursor fields — every syntactically valid token,
/// including bad-MAC, wrong-binding, expired, or otherwise semantically invalid input,
/// passes through to the shared Core service.
/// </summary>
internal static class MaintenanceContinuationSyntax
{
    private const string Prefix = "b5c2.";
    private const int EncodedLength = 214;
    private const int DecodedSize = 160;

    internal static bool TryValidate(string? token, out MaintenanceContinuationSyntaxError error)
    {
        error = MaintenanceContinuationSyntaxError.None;
        if (string.IsNullOrEmpty(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return Fail(out error, MaintenanceContinuationSyntaxCode.InvalidFraming);
        string encoded = token[Prefix.Length..];
        if (encoded.Any(character => character > 127))
            return Fail(out error, MaintenanceContinuationSyntaxCode.NonAscii);
        if (encoded.Length != EncodedLength)
            return Fail(out error, MaintenanceContinuationSyntaxCode.InvalidSize);
        if (encoded.Contains('=') || encoded.Any(character => !IsBase64Url(character)))
            return Fail(out error, MaintenanceContinuationSyntaxCode.NonCanonicalEncoding);
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(
                encoded.Replace('-', '+').Replace('_', '/') + "==");
        }
        catch (FormatException)
        {
            return Fail(out error, MaintenanceContinuationSyntaxCode.NonCanonicalEncoding);
        }
        if (decoded.Length != DecodedSize)
            return Fail(out error, MaintenanceContinuationSyntaxCode.InvalidSize);
        string canonical = Convert.ToBase64String(decoded)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!string.Equals(canonical, encoded, StringComparison.Ordinal))
            return Fail(out error, MaintenanceContinuationSyntaxCode.NonCanonicalEncoding);
        return true;
    }

    private static bool IsBase64Url(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

    private static bool Fail(out MaintenanceContinuationSyntaxError error, MaintenanceContinuationSyntaxCode code)
    {
        error = new MaintenanceContinuationSyntaxError(code, "Invalid continuation.");
        return false;
    }
}

internal enum MaintenanceContinuationSyntaxCode : byte
{
    None = 0,
    InvalidFraming = 1,
    NonAscii = 2,
    NonCanonicalEncoding = 3,
    InvalidSize = 4
}

internal readonly record struct MaintenanceContinuationSyntaxError(
    MaintenanceContinuationSyntaxCode Code, string Message)
{
    internal static MaintenanceContinuationSyntaxError None =>
        new(MaintenanceContinuationSyntaxCode.None, "");
}
