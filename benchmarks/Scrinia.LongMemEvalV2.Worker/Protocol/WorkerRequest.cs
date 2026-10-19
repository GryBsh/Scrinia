using System.Text.Json;
using Scrinia.Agent.Benchmarks;

namespace Scrinia.LongMemEvalV2.Worker.Protocol;

public abstract record WorkerRequest
{
    public required int Version { get; init; }
    public required string Operation { get; init; }
    public required string OperationId { get; init; }
    public required ControllerConfiguration Configuration { get; init; }
    public required OwnedPaths Paths { get; init; }
}
public sealed record LearnRequest : WorkerRequest
{
    public required TrajectoryManifest Trajectory { get; init; }
    public required string? PriorSnapshotPath { get; init; }
    public required string StagingPath { get; init; }
}
public sealed record RetrieveRequest : WorkerRequest
{
    public required string SnapshotPath { get; init; }
    public required string Question { get; init; }
    public required ImageReference? QuestionImage { get; init; }
}
public sealed record ValidateSnapshotRequest : WorkerRequest
{
    public required string SnapshotPath { get; init; }
    public required string ExpectedFingerprint { get; init; }
}
public sealed record ControllerConfiguration
{
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public required string ApiKeyEnvironmentVariable { get; init; }
    public required double Temperature { get; init; }
    public required double TopP { get; init; }
    public required int TopK { get; init; }
    public required bool Thinking { get; init; }
    public required int MaxOutputTokens { get; init; }
    public required int NetworkTimeoutSeconds { get; init; }
    public required int EpisodeSeconds { get; init; }
    public required int MaxModelRequests { get; init; }
    public required int MaxAttempts { get; init; }
}
public sealed record OwnedPaths
{
    public required string Root { get; init; }
    public required string Workspace { get; init; }
    public required string Home { get; init; }
    public required string EventLogPath { get; init; }
}
public sealed record ImageReference
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required string MediaType { get; init; }
    public required long ByteLength { get; init; }
}
public sealed record TrajectoryManifest
{
    public required string Id { get; init; }
    public required string Goal { get; init; }
    public required string? Outcome { get; init; }
    public required string StartUrl { get; init; }
    public required string[] Actions { get; init; }
    public required TrajectoryState[] States { get; init; }
    public required string SourceHash { get; init; }
}
public sealed record TrajectoryState
{
    public required int StateIndex { get; init; }
    public required int Step { get; init; }
    public required string Url { get; init; }
    public required string? Action { get; init; }
    public required string? Thoughts { get; init; }
    public required string Text { get; init; }
    public required ImageReference Screenshot { get; init; }
}

public static class WorkerProtocol
{
    public static WorkerRequest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        RejectDuplicateKeys(document.RootElement);
        WorkerRequest request = document.RootElement.GetProperty("operation").GetString() switch
        {
            "learn" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.LearnRequest)!,
            "retrieve" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.RetrieveRequest)!,
            "validate-snapshot" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.ValidateSnapshotRequest)!,
            _ => throw new InvalidDataException("INVALID_OPERATION")
        };
        Validate(request);
        return request;
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(keys.Add(property.Name));
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }

    public static void Validate(WorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.Version == 1 && IsHex(request.OperationId, 32));
        var configuration = request.Configuration;
        Require(configuration is not null && request.Paths is not null);
        Require(Uri.TryCreate(configuration!.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https" && endpoint.UserInfo.Length == 0
            && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0);
        Require(!string.IsNullOrEmpty(configuration.Model));
        var variable = configuration.ApiKeyEnvironmentVariable;
        Require(!string.IsNullOrEmpty(variable) && (char.IsAsciiLetter(variable[0]) || variable[0] == '_')
            && variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
        Require(configuration.Temperature is >= 0 and <= 2 && configuration.TopP is > 0 and <= 1
            && configuration.TopK > 0 && configuration.MaxOutputTokens > 0
            && configuration.NetworkTimeoutSeconds > 0 && configuration.EpisodeSeconds > 0
            && configuration.MaxModelRequests > 0 && configuration.MaxAttempts > 0);
        var paths = request.Paths!;
        CheckPath(paths.Root, paths.Root);
        CheckPath(paths.Root, paths.Workspace);
        CheckPath(paths.Root, paths.Home);
        CheckPath(paths.Root, paths.EventLogPath);
        PathSafety.RequireSeparate(paths.Workspace, paths.Home);
        switch (request)
        {
            case LearnRequest learn:
                Require(request.Operation == "learn" && learn.Trajectory is not null);
                CheckPath(paths.Root, learn.StagingPath);
                if (learn.PriorSnapshotPath is not null) CheckPath(paths.Root, learn.PriorSnapshotPath);
                var trajectory = learn.Trajectory!;
                Require(!string.IsNullOrEmpty(trajectory.Id) && trajectory.Goal is not null
                    && !string.IsNullOrEmpty(trajectory.StartUrl) && IsHex(trajectory.SourceHash, 64)
                    && trajectory.Actions is not null && trajectory.Actions.All(a => a is not null)
                    && trajectory.States is { Length: > 0 });
                foreach (var state in trajectory.States!)
                {
                    Require(state is not null && state.StateIndex >= 0 && state.Step >= 0
                        && !string.IsNullOrEmpty(state.Url) && state.Text is not null);
                    CheckImage(paths.Root, state!.Screenshot);
                }
                break;
            case RetrieveRequest retrieve:
                Require(request.Operation == "retrieve" && !string.IsNullOrEmpty(retrieve.Question));
                CheckPath(paths.Root, retrieve.SnapshotPath);
                if (retrieve.QuestionImage is not null) CheckImage(paths.Root, retrieve.QuestionImage);
                break;
            case ValidateSnapshotRequest validate:
                Require(request.Operation == "validate-snapshot" && IsHex(validate.ExpectedFingerprint, 64));
                CheckPath(paths.Root, validate.SnapshotPath);
                break;
            default:
                throw new InvalidDataException("INVALID_OPERATION");
        }
    }

    private static void CheckImage(string root, ImageReference? image)
    {
        Require(image is not null && IsHex(image.Sha256, 64) && image.ByteLength > 0
            && image.MediaType is "image/png" or "image/jpeg" or "image/webp");
        CheckPath(root, image!.Path);
    }
    private static void CheckPath(string root, string path)
    {
        Require(!string.IsNullOrEmpty(path) && System.IO.Path.IsPathFullyQualified(path));
        PathSafety.RequireNoLinks(path);
        Require(PathSafety.Contains(root, path));
    }
    private static bool IsHex(string? value, int length)
        => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Require(bool valid)
    {
        if (!valid) throw new InvalidDataException("INVALID_REQUEST");
    }
}
