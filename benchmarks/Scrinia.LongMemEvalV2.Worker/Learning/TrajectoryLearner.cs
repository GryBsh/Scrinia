using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Scrinia.Agent.Benchmarks;
using Scrinia.LongMemEvalV2.Worker.Evidence;
using Scrinia.LongMemEvalV2.Worker.Protocol;
namespace Scrinia.LongMemEvalV2.Worker.Learning;

public sealed class TrajectoryLearner(Func<ModelSettings,IChatClient>? createClient = null)
{
    public async Task<LearningReceipt> LearnAsync(LearnRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        WorkerProtocol.Validate(request);
        SourceCatalog.ValidateFragments(request.Trajectory,request.Fragments);
        ValidateBatches(request);
        // Each batch gets its own episode. Two additional episode allowances bound
        // non-model operation work (startup/seeding and final persistence/validation).
        // Validation above rejects an empty batch manifest before deriving this limit.
        long operationSeconds = checked((long)request.Configuration.EpisodeSeconds * (request.Batches.LongLength + 2L));
        using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operationDeadline.CancelAfter(TimeSpan.FromSeconds(operationSeconds));
        ct = operationDeadline.Token;
        var catalog = SourceCatalog.Create(request.Trajectory,request.Fragments,request.StagingPath);
        await using var events = new LearningEvents(request.Paths.EventLogPath);
        var settings = new ScriniaSettings(Path.GetDirectoryName(request.Learning.Executable)!,
            request.Learning.Executable,request.Learning.PrefixArguments,[],null);
        var paths = new AttemptPaths(request.Paths.Root,request.Paths.Workspace,request.Paths.Home,request.Paths.EventLogPath);
        // Disposal is inside this method: no receipt can precede owned MCP shutdown.
        await using (var session = await ScriniaMcpSession.OpenAsync(settings,paths,events,ct))
        {
            LearningInstructions.VerifyContext(request.Learning,session.CatalogHash);
            // Supplied catalog is bound to the same hash used by the shared runtime.
            using var descriptor = JsonDocument.Parse(request.Learning.ToolCatalogJson);
            if (descriptor.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("LEARNING_CATALOG_INVALID");
            var actualTools = JsonSerializer.SerializeToElement(session.Tools.OrderBy(t=>t.Name,StringComparer.Ordinal).Select(t=>t.ProtocolTool).ToArray(),
                WorkerJsonContext.Default.ToolArray);
            var expectedTools = descriptor.RootElement.EnumerateArray().OrderBy(t=>t.GetProperty("name").GetString(),StringComparer.Ordinal).ToArray();
            if (actualTools.GetArrayLength()!=expectedTools.Length
                || actualTools.EnumerateArray().Where((tool,i)=>!JsonElement.DeepEquals(tool,expectedTools[i])).Any())
                throw new InvalidDataException("LEARNING_CATALOG_DESCRIPTOR_MISMATCH");
            await new SourceSeeder().SeedAsync(catalog,session,ct);
            foreach (var batch in request.Batches)
            {
                using var batchDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                batchDeadline.CancelAfter(TimeSpan.FromSeconds(request.Configuration.EpisodeSeconds));
                var batchCt = batchDeadline.Token;
                batchCt.ThrowIfCancellationRequested();
                List<AIContent> content = [new TextContent(batch.MessageText)];
                foreach (var image in batch.Images)
                {
                    var data = await File.ReadAllBytesAsync(image.Path,batchCt);
                    if (SourceCatalog.Hash(data)!=image.Sha256) throw new InvalidDataException("SOURCE_IMAGE_CHANGED");
                    content.Add(new DataContent(data,image.MediaType));
                }
                var configuration = request.Configuration;
                var model = new ModelSettings(new Uri(configuration.Endpoint),configuration.Model,
                    configuration.ApiKeyEnvironmentVariable,(float)configuration.Temperature,configuration.MaxOutputTokens,
                    (float)configuration.TopP,configuration.TopK);
                var episode = new RuntimeAgentEpisode(m => new LearningBudgetClient(
                    (createClient ?? ModelClientFactory.Create)(m), batch,request.Learning), expectedModelId:configuration.Model);
                var result = await episode.RunAsync(new RuntimeEpisodeRequest("learn",LearningInstructions.Text,
                    [new ChatMessage(ChatRole.User,content)],LearningInstructions.AllowedTools,
                    EnableResponseRecovery:true,EnableThinking:configuration.Thinking,
                    ValidateToolCall:LearningInstructions.Guard(request.Trajectory.SourceHash,batch.SourceIds)),
                    session,model,new RunLimits(configuration.MaxModelRequests,configuration.EpisodeSeconds,configuration.EpisodeSeconds,configuration.MaxAttempts),events,batchCt);
                batchCt.ThrowIfCancellationRequested();
                if (result.ToolErrors != 0) throw new InvalidDataException("LEARNING_TOOL_ERROR");
            }
            ct.ThrowIfCancellationRequested();
        }
        ct.ThrowIfCancellationRequested();
        var directory = Path.Combine(request.StagingPath,"catalogs"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,request.Trajectory.SourceHash+".json");
        if (File.Exists(path)) throw new InvalidDataException("SOURCE_CATALOG_CONFLICT");
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(catalog,WorkerJsonContext.Default.SourceCatalog),ct);
        string fingerprint = SnapshotValidator.Fingerprint(request.StagingPath);
        await new SnapshotValidator().ValidateAsync(request.StagingPath,fingerprint,ct);
        return new() { SourceHash=request.Trajectory.SourceHash,PreviousSnapshotHash=request.PreviousFingerprint,
            NewSnapshotHash=fingerprint,CommittedOperationId=request.OperationId };
    }
    private static void ValidateBatches(LearnRequest request)
    {
        var ordered = request.Batches.SelectMany(b=>b.SourceIds).ToArray();
        if (!ordered.SequenceEqual(request.Fragments.Select(f=>f.SourceId),StringComparer.Ordinal))
            throw new InvalidDataException("LEARNING_BATCH_ORDER");
        var fragments = request.Fragments.ToDictionary(f=>f.SourceId,StringComparer.Ordinal);
        foreach (var batch in request.Batches)
        {
            using var message = JsonDocument.Parse(batch.MessageText);
            var root = message.RootElement;
            if (root.GetProperty("trajectoryId").GetString()!=request.Trajectory.Id
                || root.GetProperty("trajectorySourceHash").GetString()!=request.Trajectory.SourceHash
                || root.GetProperty("goal").GetString()!=request.Trajectory.Goal
                || root.GetProperty("outcome").GetString()!=request.Trajectory.Outcome
                || root.GetProperty("startUrl").GetString()!=request.Trajectory.StartUrl
                || root.GetProperty("fragments").GetArrayLength()!=batch.SourceIds.Length)
                throw new InvalidDataException("LEARNING_BATCH_SOURCE_MISMATCH");
            int index=0;
            foreach (var record in root.GetProperty("fragments").EnumerateArray())
            {
                var fragment = fragments[batch.SourceIds[index++]];
                var state = request.Trajectory.States[fragment.StateIndex];
                if (record.GetProperty("sourceId").GetString()!=fragment.SourceId
                    || record.GetProperty("text").GetString()!=fragment.Text
                    || record.GetProperty("textHash").GetString()!=fragment.TextHash
                    || record.GetProperty("stateIndex").GetInt32()!=fragment.StateIndex
                    || record.GetProperty("fragmentIndex").GetInt32()!=fragment.FragmentIndex
                    || record.GetProperty("startByte").GetInt32()!=fragment.StartByte
                    || record.GetProperty("endByte").GetInt32()!=fragment.EndByte
                    || record.GetProperty("step").GetInt32()!=state.Step
                    || record.GetProperty("url").GetString()!=state.Url
                    || record.GetProperty("action").GetString()!=state.Action
                    || record.GetProperty("thoughts").GetString()!=state.Thoughts
                    || record.GetProperty("screenshotSha256").GetString()!=state.Screenshot.Sha256)
                    throw new InvalidDataException("LEARNING_BATCH_SOURCE_MISMATCH");
            }
            var expected = batch.SourceIds.Select(id=>request.Trajectory.States[fragments[id].StateIndex].Screenshot).DistinctBy(i=>i.Sha256).ToArray();
            if (expected.Length!=batch.Images.Length || expected.Where((image,i)=>image!=batch.Images[i]).Any())
                throw new InvalidDataException("LEARNING_BATCH_IMAGE_MISMATCH");
        }
    }

    // Exact pinned processor count covers the initial request. Later requests retain the
    // same prompt/images/tools and add a conservative UTF-8/JSON byte bound plus framing.
    // Unknown/new image content fails closed; no conversation is silently truncated.
    private sealed class LearningBudgetClient(IChatClient inner,PreparedBatch batch,LearningConfiguration limits) : DelegatingChatClient(inner)
    {
        private long? _initialBytes; private int _initialMessages;
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)
        {
            var list=messages.ToArray(); long bytes=0;
            foreach (var message in list) foreach (var content in message.Contents) bytes=checked(bytes+Size(content));
            _initialBytes ??= bytes;
            if (_initialMessages==0) _initialMessages=list.Length;
            long bound=checked(batch.InputTokens+Math.Max(0,bytes-_initialBytes.Value)+1024L*Math.Max(0,list.Length-_initialMessages));
            if (bound>limits.InputTokenLimit) throw new InvalidDataException("LEARNING_CONTEXT_BUDGET_EXHAUSTED");
            return base.GetResponseAsync(list,options,cancellationToken);
        }
        private long Size(object? value) => value switch
        {
            null => 4, string text => 6L*Encoding.UTF8.GetByteCount(text)+2,
            TextContent text => Size(text.Text),
            TextReasoningContent reasoning => Size(reasoning.Text),
            DataContent data when batch.Images.Any(image=>image.Sha256==SourceCatalog.Hash(data.Data.Span)) => 0,
            FunctionCallContent call => 1024+Size(call.Name)+Size(call.CallId)+Size(call.Arguments)
                + (call.RawRepresentation is OpenAI.Chat.ChatToolCall raw ? Size(raw.FunctionArguments?.ToString()) : 0),
            FunctionResultContent result => 1024+Size(result.CallId)+Size(result.Result),
            JsonElement element => 6L*Encoding.UTF8.GetByteCount(element.GetRawText()),
            System.Text.Json.Nodes.JsonNode node => 6L*Encoding.UTF8.GetByteCount(node.ToJsonString()),
            IEnumerable<KeyValuePair<string,object?>> map => map.Sum(p=>Size(p.Key)+Size(p.Value)+4),
            System.Collections.IEnumerable sequence => sequence.Cast<object?>().Sum(item=>Size(item)+4),
            bool or int or long or float or double or decimal => 64,
            _ => throw new InvalidDataException("LEARNING_UNACCOUNTED_CONTENT")
        };
    }
}
internal sealed class LearningEvents : IRunEvents
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1,1);
    private bool _disposed;
    public LearningEvents(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false));
    }
    public async ValueTask WriteAsync(string phase,string kind,JsonElement data,CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();writer.WriteString("phase",phase);writer.WriteString("kind",kind);
            writer.WritePropertyName("data");data.WriteTo(writer);writer.WriteEndObject();
        }
        await _gate.WaitAsync(ct);
        try {
            ObjectDisposedException.ThrowIf(_disposed,this);
            ct.ThrowIfCancellationRequested();
            // Once accepted, finish the whole JSON line even if cancellation arrives.
            // Cancellation is propagated after flush, never as a partial record.
            await _writer.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray()).AsMemory(),CancellationToken.None);
            await _writer.FlushAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try {
            if (_disposed) return;
            _disposed = true;
            await _writer.DisposeAsync();
        }
        finally { _gate.Release(); }
        // Retain the managed gate so queued/later writers can observe disposal
        // under the same gate instead of racing SemaphoreSlim.Dispose.
    }
}
