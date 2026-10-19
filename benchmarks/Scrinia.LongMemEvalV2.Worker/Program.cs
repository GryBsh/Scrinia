using System.Text.Json;
using Scrinia.Agent.Benchmarks;
using Scrinia.LongMemEvalV2.Worker.Protocol;

namespace Scrinia.LongMemEvalV2.Worker;

public interface IWorkerOperation : IAsyncDisposable
{
    Task<WorkerResult> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken);
}

public sealed class UnsupportedOperation : IWorkerOperation
{
    public Task<WorkerResult> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(WorkerResult.Failure(request, "UNSUPPORTED_OPERATION"));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LearningOperation : IWorkerOperation
{
    public async Task<WorkerResult> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        if (request is LearnRequest learn) {
            var receipt = await new Learning.TrajectoryLearner().LearnAsync(learn,cancellationToken);
            return new() { Version=1,OperationId=request.OperationId,Status="ok",EventLogPath=request.Paths.EventLogPath,
                Receipt=receipt,Evidence=null,ErrorCode=null };
        }
        if(request is RetrieveRequest retrieve) {
            var evidence=await new Retrieval.EvidenceRetriever().RetrieveEvidenceAsync(retrieve,cancellationToken);
            return new() {Version=1,OperationId=request.OperationId,Status="ok",EventLogPath=request.Paths.EventLogPath,
                Receipt=null,Evidence=evidence.Items,ErrorCode=null};
        }
        if (request is ValidateSnapshotRequest validate) {
            await new Evidence.SnapshotValidator().ValidateAsync(validate.SnapshotPath,validate.ExpectedFingerprint,cancellationToken);
            return new() { Version=1,OperationId=request.OperationId,Status="ok",EventLogPath=request.Paths.EventLogPath,
                Receipt=null,Evidence=null,ErrorCode=null };
        }
        return WorkerResult.Failure(request,"UNSUPPORTED_OPERATION");
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class WorkerHost
{
    public static async Task<WorkerResult> ExecuteOwnedAsync(WorkerRequest request, IWorkerOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            await using (operation.ConfigureAwait(false))
                return await operation.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WorkerResult.Failure(request, "CANCELLED", cancelled: true);
        }
        catch (Exception)
        {
            return WorkerResult.Failure(request, "OPERATION_FAILED");
        }
    }

    public static async Task<WorkerResult> RunWithControlAsync(WorkerRequest request, TextReader input,
        IWorkerOperation operation, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stopControl = new CancellationTokenSource();
        var control = MonitorControlAsync(input, linked, stopControl.Token);
        try
        {
            return await ExecuteOwnedAsync(request, operation, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            await stopControl.CancelAsync().ConfigureAwait(false);
            await control.ConfigureAwait(false);
        }
    }

    private static async Task MonitorControlAsync(TextReader input, CancellationTokenSource operation, CancellationToken stop)
    {
        try
        {
            // Console stdin cannot cancel an in-flight Windows pipe read. Cancel the wait;
            // the process owns the pipe and exits after operation disposal/result publication.
            var line = await input.ReadLineAsync(stop).AsTask().WaitAsync(stop).ConfigureAwait(false);
            if (line is null)
            {
                await operation.CancelAsync().ConfigureAwait(false);
                return;
            }
            _ = JsonSerializer.Deserialize(line, WorkerJsonContext.Default.ControlLine);
            // Only cancel is supported. Invalid input also fails closed by cancelling.
            await operation.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) { await operation.CancelAsync().ConfigureAwait(false); }
    }
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--capture-catalog")
        {
            try
            {
                if (!Path.IsPathFullyQualified(args[1]) || new FileInfo(args[1]).Length > 65536) return 2;
                PathSafety.RequireNoLinks(args[1]);
                var request = JsonSerializer.Deserialize(await File.ReadAllTextAsync(args[1]),
                    WorkerJsonContext.Default.CatalogCaptureRequest) ?? throw new InvalidDataException();
                await CatalogCapture.RunAsync(request, CancellationToken.None);
                return 0;
            }
            catch (Exception)
            {
                Console.Error.WriteLine("CAPTURE_FAILED");
                return 2;
            }
        }
        if (args.Length != 4 || args[0] != "--request" || args[2] != "--result"
            || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[3]))
            return 2;
        try
        {
            if (!File.Exists(args[1]) || new FileInfo(args[1]).Length > 16 * 1024 * 1024 || File.Exists(args[3]))
                return 2;
            PathSafety.RequireNoLinks(args[1]);
            PathSafety.RequireNoLinks(args[3]);
            var request = WorkerProtocol.Parse(await File.ReadAllTextAsync(args[1]).ConfigureAwait(false));
            if (!PathSafety.Contains(request.Paths.Root, args[1]) || !PathSafety.Contains(request.Paths.Root, args[3]))
                return 2;
            // The adapter retains ownership of the stdin pipe throughout operation lifetime.
            using var input = new StreamReader(Console.OpenStandardInput());
            var result = await WorkerHost.RunWithControlAsync(request, input,
                new LearningOperation(), CancellationToken.None).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(request.Paths.EventLogPath)!);
            await File.AppendAllTextAsync(request.Paths.EventLogPath,
                JsonSerializer.Serialize(result, WorkerJsonContext.Default.WorkerResult) + "\n").ConfigureAwait(false);
            var temporary = args[3] + "." + request.OperationId + ".tmp";
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(result, WorkerJsonContext.Default.WorkerResult)).ConfigureAwait(false);
            File.Move(temporary, args[3], overwrite: false);
            return result.Status == "ok" ? 0 : result.Status == "cancelled" ? 3 : 4;
        }
        catch (Exception)
        {
            // Deliberately avoid emitting paths, payloads, provider responses or credentials.
            Console.Error.WriteLine("WORKER_FAILED");
            return 2;
        }
    }
}
