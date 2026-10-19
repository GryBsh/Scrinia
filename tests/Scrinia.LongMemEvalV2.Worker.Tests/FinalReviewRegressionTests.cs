using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Scrinia.Agent.Benchmarks;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.LongMemEvalV2.Worker.Evidence;
using Scrinia.LongMemEvalV2.Worker.Learning;
using Scrinia.LongMemEvalV2.Worker.Protocol;
using Scrinia.LongMemEvalV2.Worker.Retrieval;
namespace Scrinia.LongMemEvalV2.Worker.Tests;

public sealed class FinalReviewRegressionTests
{
    private static readonly string[] ValidNote = ["valid note"];
    [Fact]
    public async Task Long_sources_metadata_original_and_derived_chunks_survive_real_public_recall()
    {
        using var fixture = new LearningIntegrationTests.LearningFixture();
        var learn = await fixture.RequestAsync(longData:true);
        Assert.Equal(10200,learn.Fragments[0].Text.Length);
        string note = new string('n',7899) + "😀 café";
        var receipt = await new TrajectoryLearner(_=>new RetrievalTests.Client((_,_)=>RetrievalTests.Text("Done.")))
            .LearnAsync(learn,CancellationToken.None);
        var args=new AIFunctionArguments {
            ["path"]="/derived/"+learn.Trajectory.SourceHash+"/long-note",["description"]="Source-backed note",
            ["content"]=new[]{note[..7899],note[7899..]},
            ["attributes"]=JsonSerializer.SerializeToElement(new Dictionary<string,string[]> {["source"]=[learn.Fragments[0].SourceId]})};
        LearningInstructions.Guard(learn.Trajectory.SourceHash,[learn.Fragments[0].SourceId])("remember",args);
        await using(var events=Events(Path.Combine(fixture.Root,"seed-derived.jsonl")))
        await using(var session=await ScriniaMcpSession.OpenAsync(
            new ScriniaSettings(Path.GetDirectoryName(learn.Learning.Executable)!,learn.Learning.Executable,[],[],null),
            new AttemptPaths(fixture.Root,learn.Paths.Workspace,learn.Paths.Home,Path.Combine(fixture.Root,"seed-derived.jsonl")),events,CancellationToken.None))
            await session.Tools.Single(t=>t.Name=="remember").InvokeAsync(args,CancellationToken.None);
        receipt=receipt with {NewSnapshotHash=SnapshotValidator.Fingerprint(learn.StagingPath)};
        await new SnapshotValidator().ValidateAsync(learn.StagingPath,receipt.NewSnapshotHash,CancellationToken.None);
        var request = RetrievalTests.Request(learn,receipt);
        var result = await new EvidenceRetriever(_=>new RetrievalTests.Client((_,_)=>RetrievalTests.Text(RetrievalTests.Selection(learn))))
            .RetrieveEvidenceAsync(request,CancellationToken.None);
        var text = Assert.Single(result.Items,x=>x.Type=="text").Value;
        Assert.EndsWith(learn.Fragments[0].Text,text);
        Assert.Contains(learn.Trajectory.States[0].Thoughts!,text);
        Assert.Equal(await File.ReadAllBytesAsync(learn.Trajectory.States[0].Screenshot.Path),
            await File.ReadAllBytesAsync(Assert.Single(result.Items,x=>x.Type=="image").Value));
        var store = new FileMemoryStore(learn.Paths.Workspace);
        foreach (string path in new[]{"/source/"+learn.Trajectory.SourceHash+"/trajectory",
            SourceCatalog.SourcePath(learn.Trajectory.SourceHash,learn.Fragments[0]),"/derived/"+learn.Trajectory.SourceHash+"/long-note"})
        {
            var entry = (await store.ReadAsync(MemoryAddress.Parse(path)))!;
            Assert.True(entry.ChunkCount>1);
            string artifact = await store.ReadArtifactAsync(MemoryAddress.Parse(path));
            var chunks = Enumerable.Range(1,entry.ChunkCount).Select(i=>Nmp2ChunkedEncoder.DecodeChunk(artifact,i)).ToArray();
            Assert.All(chunks,c=>Assert.True(c.Length<=7900));
            string expected = path.EndsWith("/trajectory") ? learn.Trajectory.OriginalJson :
                path.EndsWith("/long-note") ? note : SourceSeeder.ContentFor(SourceCatalog.ReadAll(learn.StagingPath)[0],learn.Fragments[0]);
            Assert.Equal(expected,string.Concat(chunks));
            string selection = JsonSerializer.Serialize(new {references=new[]{new {kind="memory",path,chunks=Enumerable.Range(1,entry.ChunkCount).ToArray()}}});
            var next = request with {Paths=request.Paths with {EventLogPath=Path.Combine(fixture.Root,Guid.NewGuid()+".jsonl")}};
            var recalled = await new EvidenceRetriever(_=>new RetrievalTests.Client((_,_)=>RetrievalTests.Text(selection)))
                .RetrieveEvidenceAsync(next,CancellationToken.None);
            Assert.Equal(chunks,recalled.Items.Select(i=>i.Value[(i.Value.IndexOf('\n')+1)..]).ToArray());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("substituted")]
    public async Task Wrong_first_learning_identity_stops_before_directed_dispatch(string? model)
    {
        using var fixture = new LearningIntegrationTests.LearningFixture();
        var learn=await fixture.RequestAsync();
        var client=new RetrievalTests.Client((_,_)=>new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "Done.")) {ModelId=model,FinishReason=ChatFinishReason.Stop});
        await Assert.ThrowsAnyAsync<Exception>(()=>new TrajectoryLearner(_=>client).LearnAsync(learn,CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(learn.StagingPath,"catalogs")));
        Assert.DoesNotContain("tool-intent",File.ReadAllText(learn.Paths.EventLogPath));
        Assert.Contains("model-response",File.ReadAllText(learn.Paths.EventLogPath));
    }

    [Fact]
    public async Task Identity_switch_between_learning_batches_is_rejected()
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();
        var learn=await fixture.RequestAsync(twoBatches:true);
        int calls=0;
        await Assert.ThrowsAnyAsync<Exception>(()=>new TrajectoryLearner(_=>new RetrievalTests.Client((_,_)=>{
            var response=RetrievalTests.Text("Done.");response.ModelId=++calls==1?"synthetic":"changed";return response;
        })).LearnAsync(learn,CancellationToken.None));
        Assert.Equal(2,calls);
        Assert.False(Directory.Exists(Path.Combine(learn.StagingPath,"catalogs")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("changed")]
    public async Task Each_query_checks_its_first_identity(string? model)
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();var learn=await fixture.RequestAsync();
        var receipt=await new TrajectoryLearner(_=>new RetrievalTests.Client((_,_)=>RetrievalTests.Text("Done."))).LearnAsync(learn,CancellationToken.None);
        int calls=0;
        var retriever=new EvidenceRetriever(_=>new RetrievalTests.Client((_,_)=>{
            var response=RetrievalTests.Text("{\"references\":[]}");response.ModelId=++calls==1?"synthetic":model;return response;
        }));
        var request=RetrievalTests.Request(learn,receipt);
        await retriever.RetrieveAsync(request,CancellationToken.None);
        var next=request with {Paths=request.Paths with {EventLogPath=Path.Combine(fixture.Root,"second-query.jsonl")}};
        await Assert.ThrowsAnyAsync<Exception>(()=>retriever.RetrieveAsync(next,CancellationToken.None));
        Assert.DoesNotContain("tool-intent",File.ReadAllText(next.Paths.EventLogPath));
    }

    [Fact]
    public void Derived_oversize_chunk_fails_before_dispatch()
    {
        var args=new AIFunctionArguments {["path"]="/derived/hash/note",["description"]="note",
            ["content"]=new[]{new string('x',7901)},["attributes"]=new Dictionary<string,string[]> {["source"]=["source"]}};
        Assert.Throws<InvalidDataException>(()=>LearningInstructions.Guard("hash",["source"])("remember",args));
    }

    [Fact]
    public void Prompt_examples_obey_strict_contracts()
    {
        Assert.Contains("ASCII letters, digits and hyphens",LearningInstructions.Text);
        Assert.Contains("7,900 UTF-16",LearningInstructions.Text);
        string example=LearningInstructions.Text.Split("Remember example: ")[1].Split('\n')[0];
        var args=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(example)!;
        var call=new AIFunctionArguments(args.ToDictionary(p=>p.Key,p=>(object?)p.Value));
        LearningInstructions.Guard("trajectory-hash",["source-id"])("remember",call);
        Assert.Contains("{\"kind\":\"memory\",\"path\":\"/source/...\",\"chunks\":[1]}",RetrievalInstructions.Text);
        Assert.Contains("must match that source record",RetrievalInstructions.Text);
        int[] exampleChunks=[1];
        foreach(string path in new[]{"/derived/hash/note","/source/hash/trajectory"})
            EvidenceSelection.Parse(JsonSerializer.Serialize(new {references=new[]{new {kind="memory",path,chunks=exampleChunks}}})).Validate([]);
    }
    [Theory]
    [InlineData(7898)]
    [InlineData(7899)]
    [InlineData(7900)]
    public void Storage_boundary_preserves_unicode_and_exact_recall_projection_budget(int prefix)
    {
        string original=new string('x',prefix)+"😀\r\n\\\""+new string('y',16000);
        var chunks=StorageChunks.Split(original);
        Assert.Equal(original,string.Concat(chunks));
        Assert.Equal(Encoding.UTF8.GetBytes(original),chunks.SelectMany(Encoding.UTF8.GetBytes).ToArray());
        foreach(var chunk in chunks)
            Assert.True($"Chunk {int.MaxValue}/{int.MaxValue}\n\n{chunk}".Length<=7992);
    }

    [Fact]
    public async Task Oversized_derived_write_never_dispatches_or_publishes()
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();var learn=await fixture.RequestAsync();
        string path="/derived/"+learn.Trajectory.SourceHash+"/too-long";
        var response=new ChatResponse(new ChatMessage(ChatRole.Assistant,[
            new FunctionCallContent("write","remember",new Dictionary<string,object?> {
                ["path"]=path,["description"]="oversized",
                ["content"]=JsonSerializer.SerializeToElement(new[]{new string('x',7901)}),
                ["attributes"]=JsonSerializer.SerializeToElement(new {source=new[]{learn.Fragments[0].SourceId}})
            })])) {ModelId="synthetic",FinishReason=ChatFinishReason.ToolCalls};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new TrajectoryLearner(_=>new RetrievalTests.Client((_,_)=>response)).LearnAsync(learn,CancellationToken.None));
        Assert.Null(await new FileMemoryStore(learn.Paths.Workspace).ReadAsync(MemoryAddress.Parse(path)));
        Assert.False(Directory.Exists(Path.Combine(learn.StagingPath,"catalogs")));
        Assert.DoesNotContain("tool-intent",File.ReadAllText(learn.Paths.EventLogPath));
        Assert.Contains("model-response",File.ReadAllText(learn.Paths.EventLogPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("substituted")]
    public async Task Invalid_model_tool_response_never_reaches_learning_guard_or_dispatch(string? model)
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();var learn=await fixture.RequestAsync();
        string path="/derived/"+learn.Trajectory.SourceHash+"/valid-note";
        var response=new ChatResponse(new ChatMessage(ChatRole.Assistant,[
            new FunctionCallContent("write","remember",new Dictionary<string,object?> {
                ["path"]=path,["description"]="valid",
                ["content"]=JsonSerializer.SerializeToElement(ValidNote),
                ["attributes"]=JsonSerializer.SerializeToElement(new {source=new[]{learn.Fragments[0].SourceId}})
            })])) {ModelId=model,FinishReason=ChatFinishReason.ToolCalls};
        var failure=await Assert.ThrowsAsync<BenchmarkFailureException>(()=>new TrajectoryLearner(_=>new RetrievalTests.Client((_,_)=>response)).LearnAsync(learn,CancellationToken.None));
        Assert.Equal(BenchmarkFailureCode.MODEL_DRIFT,failure.Code);
        Assert.Null(await new FileMemoryStore(learn.Paths.Workspace).ReadAsync(MemoryAddress.Parse(path)));
        Assert.DoesNotContain("tool-intent",File.ReadAllText(learn.Paths.EventLogPath));
    }

    private static IRunEvents Events(string path)=>(IRunEvents)Activator.CreateInstance(
        typeof(TrajectoryLearner).Assembly.GetType("Scrinia.LongMemEvalV2.Worker.Learning.LearningEvents")!,path)!;

    [Fact]
    public async Task Concurrent_large_model_and_stderr_events_remain_complete_json_lines()
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();
        string path=Path.Combine(fixture.Root,"events.jsonl");
        await using(var events=Events(path)) {
            var data=JsonSerializer.SerializeToElement(new {text=new string('x',100000)});
            await Task.WhenAll(Enumerable.Range(0,80).Select(i=>events.WriteAsync("learn",i%2==0?"model-response":"stderr",data,CancellationToken.None).AsTask()));
        }
        var lines=File.ReadAllLines(path);Assert.Equal(80,lines.Length);
        foreach(var line in lines) {using var json=JsonDocument.Parse(line);Assert.Equal(100000,json.RootElement.GetProperty("data").GetProperty("text").GetString()!.Length);}
    }

    [Fact]
    public async Task Disposal_and_cancellation_settle_pending_event_writes()
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();
        string path=Path.Combine(fixture.Root,"events.jsonl");var events=Events(path);
        var data=JsonSerializer.SerializeToElement(new {text=new string('x',1000000)});
        var first=events.WriteAsync("learn","model-response",data,CancellationToken.None).AsTask();
        using var cancel=new CancellationTokenSource();
        var cancelled=events.WriteAsync("learn","stderr",data,cancel.Token).AsTask();
        cancel.Cancel();
        var pending=events.WriteAsync("learn","stderr",data,CancellationToken.None).AsTask();
        var dispose=events.DisposeAsync().AsTask();
        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled);
        try {await pending;} catch(ObjectDisposedException) {}
        await dispose;
        await Assert.ThrowsAsync<ObjectDisposedException>(()=>events.WriteAsync("learn","stderr",data,CancellationToken.None).AsTask());
        foreach(var line in File.ReadAllLines(path)) {using var json=JsonDocument.Parse(line);}
    }
}
