namespace Scrinia.LongMemEvalV2.Worker.Protocol;

public sealed record LearningReceipt
{
    public required string SourceHash { get; init; }
    public required string? PreviousSnapshotHash { get; init; }
    public required string NewSnapshotHash { get; init; }
    public required string CommittedOperationId { get; init; }
}
public sealed record EvidenceItem
{
    public required string Type { get; init; }
    public required string Value { get; init; }
}
public sealed record WorkerResult
{
    public required int Version { get; init; }
    public required string OperationId { get; init; }
    public required string Status { get; init; }
    public required string EventLogPath { get; init; }
    public required LearningReceipt? Receipt { get; init; }
    public required EvidenceItem[]? Evidence { get; init; }
    public required string? ErrorCode { get; init; }

    public static WorkerResult Failure(WorkerRequest request, string code, bool cancelled = false) => new()
    {
        Version = 1, OperationId = request.OperationId, Status = cancelled ? "cancelled" : "error",
        EventLogPath = request.Paths.EventLogPath, Receipt = null, Evidence = null, ErrorCode = code
    };
}
