using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Scrinia.Agent.Benchmarks;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.LongMemEvalV2.Worker.Evidence;
using Scrinia.LongMemEvalV2.Worker.Learning;
using Scrinia.LongMemEvalV2.Worker.Protocol;
namespace Scrinia.LongMemEvalV2.Worker.Tests;

public sealed class LearningIntegrationTests
{
    [Fact]
    public async Task Real_mcp_sources_survive_ordered_learning_and_validate_after_owned_shutdown()
    {
        using var fixture = new LearningFixture();
        var request = await fixture.RequestAsync();
        int calls = 0;
        var learner = new TrajectoryLearner(_ => new FakeClient(messages =>
        {
            calls++;
            return calls == 1 ? FakeClient.Call("/derived/" + request.Trajectory.SourceHash + "/date",
                request.Fragments[0].SourceId) : FakeClient.Text("Learned.");
        }));
        var receipt = await learner.LearnAsync(request,CancellationToken.None);
        Assert.Equal(2,calls);
        Assert.Equal(request.OperationId,receipt.CommittedOperationId);
        await new SnapshotValidator().ValidateAsync(request.StagingPath,receipt.NewSnapshotHash,CancellationToken.None);
        var catalog = Assert.Single(SourceCatalog.ReadAll(request.StagingPath));
        Assert.Equal("2026-03-04 caf\u00e9 \U0001f600",string.Concat(catalog.Fragments.Select(f=>f.Text)));
        Assert.Equal(7,catalog.Trajectory.States[0].Step);
        Assert.True(File.Exists(catalog.ResolveImage(request.StagingPath,catalog.Trajectory.States[0].Screenshot)));
        var log = File.ReadAllText(request.Paths.EventLogPath);
        Assert.Contains("\"kind\":\"disconnected\"",log);
        var store = new FileMemoryStore(request.Paths.Workspace);
        var source = request.Fragments[0];
        await store.DeleteAsync(MemoryAddress.Parse(SourceCatalog.SourcePath(request.Trajectory.SourceHash,source)));
        await Assert.ThrowsAsync<InvalidDataException>(()=>new SnapshotValidator().ValidateAsync(
            request.StagingPath,SnapshotValidator.Fingerprint(request.StagingPath),CancellationToken.None));
    }

    [Fact]
    public async Task Catalog_mismatch_never_seeds_and_cancellation_between_batches_never_returns_receipt()
    {
        using var fixture = new LearningFixture();
        var request = await fixture.RequestAsync(twoBatches:true);
        await Assert.ThrowsAsync<InvalidDataException>(()=>new TrajectoryLearner(_=>throw new InvalidOperationException("inference forbidden"))
            .LearnAsync(request with {Learning=request.Learning with {CatalogHash=new string('a',64)}},CancellationToken.None));
        Assert.Empty(new FileMemoryStore(request.Paths.Workspace).ListScoped());
        File.Delete(request.Paths.EventLogPath);
        using var cancel = new CancellationTokenSource();
        int calls=0;
        var learner = new TrajectoryLearner(_=>new FakeClient(_=> {calls++;cancel.Cancel();return FakeClient.Text("Done.");}));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>learner.LearnAsync(request,cancel.Token));
        Assert.Equal(1,calls);
        Assert.False(Directory.Exists(Path.Combine(request.StagingPath,"catalogs")));
        Assert.Contains("\"kind\":\"disconnected\"",File.ReadAllText(request.Paths.EventLogPath));
    }

    [Fact]
    public async Task Lost_source_remember_response_is_uncertain_and_never_replayed()
    {
        using var fixture=new LearningFixture();
        var request=await fixture.RequestAsync();
        var catalog=SourceCatalog.Create(request.Trajectory,request.Fragments,request.StagingPath);
        int calls=0;
        var store=new FileMemoryStore(request.Paths.Workspace);
        var function=AIFunctionFactory.Create(async (string path,string[] content,string description) => {
            calls++;
            await store.StoreAsync(new MemoryWrite(MemoryAddress.Parse(path),
                content.Select(text=>(ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(text)).ToArray(),
                new Dictionary<string,IReadOnlyList<string>>(),[],description));
            throw new IOException("Synthetic lost committed remember response");
#pragma warning disable CS0162
            return "";
#pragma warning restore CS0162
        },"remember");
        var error=await Assert.ThrowsAsync<BenchmarkFailureException>(()=>new SourceSeeder().SeedAsync(catalog,function,CancellationToken.None));
        Assert.Equal(BenchmarkFailureCode.TOOL_COMPLETION_UNCERTAIN,error.Code);
        Assert.Equal(1,calls);
        Assert.NotNull(await store.ReadAsync(MemoryAddress.Parse("/source/"+request.Trajectory.SourceHash+"/trajectory")));
    }

    [Fact]
    public async Task Each_batch_receives_its_full_request_allowance()
    {
        using var fixture=new LearningFixture();
        var request=await fixture.RequestAsync(twoBatches:true);
        int calls=0;
        var learner=new TrajectoryLearner(_=>new FakeClient(_=>{calls++;return FakeClient.Text("Done.");}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>learner.LearnAsync(
            request with {Batches=[]},CancellationToken.None));
        Assert.Equal(0,calls);
        var receipt=await learner.LearnAsync(
            request with {Configuration=request.Configuration with {MaxModelRequests=1}},CancellationToken.None);
        Assert.Equal(2,calls);
        await new SnapshotValidator().ValidateAsync(request.StagingPath,receipt.NewSnapshotHash,CancellationToken.None);
    }

    [Fact]
    public async Task Each_batch_receives_its_full_deadline_when_trajectory_exceeds_one_episode()
    {
        using var fixture=new LearningFixture();
        var request=await fixture.RequestAsync(twoBatches:true);
        int calls=0;
        var learner=new TrajectoryLearner(_=>new FakeClient(_=>{
            calls++;
            Thread.Sleep(TimeSpan.FromMilliseconds(1200));
            return FakeClient.Text("Done.");
        }));
        var receipt=await learner.LearnAsync(
            request with {Configuration=request.Configuration with {EpisodeSeconds=2}},CancellationToken.None);
        Assert.Equal(2,calls);
        await new SnapshotValidator().ValidateAsync(request.StagingPath,receipt.NewSnapshotHash,CancellationToken.None);
    }

    [Fact]
    public async Task Dynamic_context_growth_fails_closed_before_another_provider_request()
    {
        using var next=new LearningFixture();
        var growth=await next.RequestAsync();
        int calls=0;
        var bounded=new TrajectoryLearner(_=>new FakeClient(_=>{
            calls++;return FakeClient.Call("/derived/"+growth.Trajectory.SourceHash+"/date",growth.Fragments[0].SourceId);
        }));
        await Assert.ThrowsAsync<InvalidDataException>(()=>bounded.LearnAsync(growth with {
            Batches=[growth.Batches[0] with {InputTokens=24500}]},CancellationToken.None));
        Assert.Equal(1,calls); // The second request is stopped before the provider is invoked.
    }

    [Fact]
    public void Dotnet_string_catalog_escaping_matches_python_vector()
    {
        string value = new(new[] {'\'','+','<','>','&',(char)96,'"', '\u00e9'});
        Assert.Equal("\"\\u0027\\u002B\\u003C\\u003E\\u0026\\u0060\\u0022\\u00E9\"",
            JsonSerializer.Serialize(value,WorkerJsonContext.Default.String));
    }

    private sealed class FakeClient(Func<IEnumerable<ChatMessage>,ChatResponse> response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)
            => Task.FromResult(response(messages));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)
            => throw new NotSupportedException();
        public object? GetService(Type type,object? key=null) => null;
        public void Dispose() { }
        public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant,text)) {FinishReason=ChatFinishReason.Stop,ModelId="synthetic"};
        public static ChatResponse Call(string path,string source) => new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("write","remember",new Dictionary<string,object?> {
                ["path"]=path,["description"]="Synthetic source date",
                ["content"]=JsonDocument.Parse("[\"The source date is 2026-03-04.\"]").RootElement,
                ["attributes"]=JsonDocument.Parse("{\"source\":[\""+source+"\"]}").RootElement })]))
            {FinishReason=ChatFinishReason.ToolCalls,ModelId="synthetic"};
    }

    internal sealed class LearningFixture : IDisposable
    {
        public string Root {get;} = Path.Combine(Path.GetTempPath(),"scrinia-v2-"+Guid.NewGuid().ToString("N"));
        public void Dispose() {if (Directory.Exists(Root)) Directory.Delete(Root,true);}
        public async Task<LearnRequest> RequestAsync(bool twoBatches=false, bool longData=false)
        {
            var stage=Path.Combine(Root,"generations",Guid.NewGuid().ToString("N"));
            var workspace=Path.Combine(stage,"workspace");var home=Path.Combine(stage,"home");
            Directory.CreateDirectory(workspace);Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(stage,"sources"));
            var bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aSfoAAAAASUVORK5CYII=");
            var imagePath=Path.Combine(stage,"sources","synthetic.png"); File.WriteAllBytes(imagePath,bytes);
            var image=new ImageReference {Path=imagePath,Sha256=SourceCatalog.Hash(bytes),MediaType="image/png",ByteLength=bytes.Length};
            var hash=new string('d',64);var text="2026-03-04 caf\u00e9 \U0001f600";
            if (longData) text = string.Concat(Enumerable.Repeat(" original source.",600));
            string[] parts=twoBatches ? ["2026-03-04 ","caf\u00e9 \U0001f600"] : [text];
            int offset=0;
            var fragments=parts.Select((part,index)=> {
                int start=offset;offset+=Encoding.UTF8.GetByteCount(part);
                return new SourceFragment {SourceId=SourceCatalog.SourceId(hash,0,index),StateIndex=0,FragmentIndex=index,StartByte=start,EndByte=offset,Text=part,TextHash=SourceCatalog.Hash(Encoding.UTF8.GetBytes(part))};
            }).ToArray();
            var metadata = longData ? string.Concat(Enumerable.Repeat("😀\"\\\r\n",2000)) : "";
            var state=new TrajectoryState {StateIndex=0,Step=7,Url="https://synthetic.invalid/"+(longData?new string('u',10000):""),Action="click"+metadata,Thoughts="source thoughts"+metadata,Text=text,Screenshot=image};
            var trajectory=new TrajectoryManifest {Id="synthetic",Goal="goal",Outcome=null,StartUrl=state.Url,Actions=["click"],SourceHash=hash,States=[state],OriginalJson=JsonSerializer.Serialize(new {date="2026-03-04",metadata})};
            hash=SourceCatalog.LogicalHash(trajectory);
            trajectory=trajectory with {SourceHash=hash};
            fragments=fragments.Select(f=>f with {SourceId=SourceCatalog.SourceId(hash,f.StateIndex,f.FragmentIndex)}).ToArray();
            var executable=Path.Combine(AppContext.BaseDirectory,"scrinia-payload",OperatingSystem.IsWindows()?"scri.exe":"scri");
            var settings=new ScriniaSettings(Path.GetDirectoryName(executable)!,executable,[],[],null);
            var paths=new AttemptPaths(Root,workspace,home,Path.Combine(Root,"catalog.jsonl"));
            string catalogHash,catalog;
            await using (var events=new NullEvents())
            await using (var session=await ScriniaMcpSession.OpenAsync(settings,paths,events,CancellationToken.None)) {
                catalogHash=session.CatalogHash;
                catalog=JsonSerializer.Serialize(session.Tools.OrderBy(t=>t.Name,StringComparer.Ordinal).Select(t=>t.ProtocolTool).ToArray(),WorkerJsonContext.Default.ToolArray);
            }
            var batches=fragments.Select(f=>new PreparedBatch {
                SourceIds=[f.SourceId],Images=[image],InputTokens=500,
                MessageText=JsonSerializer.SerializeToElement(new {trajectoryId=trajectory.Id,trajectorySourceHash=hash,
                    goal=trajectory.Goal,outcome=(string?)null,startUrl=trajectory.StartUrl,
                    fragments=new[]{new {sourceId=f.SourceId,stateIndex=0,fragmentIndex=f.FragmentIndex,startByte=f.StartByte,endByte=f.EndByte,
                    text=f.Text,textHash=f.TextHash,step=7,url=state.Url,action=state.Action,thoughts=state.Thoughts,screenshotSha256=image.Sha256}}}).GetRawText()
            }).ToArray();
            return new LearnRequest {Version=1,Operation="learn",OperationId=Guid.NewGuid().ToString("N"),
                Paths=new OwnedPaths {Root=Root,Workspace=workspace,Home=home,EventLogPath=Path.Combine(Root,"events","learn.jsonl")},
                Configuration=new ControllerConfiguration {Endpoint="http://localhost:1/v1",Model="synthetic",ApiKeyEnvironmentVariable="SYNTHETIC_TEST_KEY",
                    Temperature=0.6,TopP=0.95,TopK=20,Thinking=true,MaxOutputTokens=16384,NetworkTimeoutSeconds=180,EpisodeSeconds=300,MaxModelRequests=40,MaxAttempts=5},
                Trajectory=trajectory,PriorSnapshotPath=null,StagingPath=stage,PreviousFingerprint=null,Fragments=fragments,Batches=batches,
                Learning=new LearningConfiguration {Executable=executable,PrefixArguments=[],ToolCatalogJson=catalog,CatalogHash=catalogHash,
                    InstructionsHash=SourceCatalog.Hash(Encoding.UTF8.GetBytes(LearningInstructions.Text)),ProcessorRevision="c202236235762e1c871ad0ccb60c8ee5ba337b9a",
                    InputTokenLimit=24576,FragmentTokenLimit=4096,ImageLimit=4,ServedContextTokens=65536}};
        }
    }
    private sealed class NullEvents : IRunEvents
    {
        public ValueTask WriteAsync(string phase,string kind,JsonElement data,CancellationToken ct)=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
